﻿using DbUp;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Bit.Setup
{
    public class Program
    {
        private static string[] _args = null;
        private static IDictionary<string, string> _parameters = null;
        private static string _domain = null;
        private static string _identityCertPassword = null;
        private static bool _ssl = false;
        private static bool _selfSignedSsl = false;
        private static bool _letsEncrypt = false;
        private static Guid? _installationId = null;
        private static string _installationKey = null;

        public static void Main(string[] args)
        {
            _args = args;
            _parameters = ParseParameters();
            if(_parameters.ContainsKey("install"))
            {
                Install();
            }
            else if(_parameters.ContainsKey("update"))
            {
                Update();
            }
            else if(_parameters.ContainsKey("printenv"))
            {
                PrintEnvironment();
            }
            else
            {
                Console.WriteLine("No top-level command detected. Exiting...");
            }
        }

        private static void Install()
        {
            var outputDir = _parameters.ContainsKey("out") ?
                _parameters["out"].ToLowerInvariant() : "/etc/bitwarden";
            _domain = _parameters.ContainsKey("domain") ?
                _parameters["domain"].ToLowerInvariant() : "localhost";
            _letsEncrypt = _parameters.ContainsKey("letsencrypt") ?
                _parameters["letsencrypt"].ToLowerInvariant() == "y" : false;

            if(!ValidateInstallation())
            {
                return;
            }

            _ssl = _letsEncrypt;
            if(!_letsEncrypt)
            {
                Console.Write("(!) Do you have a SSL certificate to use? (y/n): ");
                _ssl = Console.ReadLine().ToLowerInvariant() == "y";

                if(_ssl)
                {
                    Console.WriteLine("Make sure 'certificate.crt' and 'private.key' are provided in the " +
                        "appropriate directory (see setup instructions).");
                }
            }

            _identityCertPassword = Helpers.SecureRandomString(32, alpha: true, numeric: true);
            MakeCerts();

            var url = _ssl ? $"https://{_domain}" : $"http://{_domain}";
            var nginxBuilder = new NginxConfigBuilder(_domain, _ssl, _selfSignedSsl, _letsEncrypt);
            nginxBuilder.BuildForInstaller();

            Console.Write("(!) Do you want to use push notifications? (y/n): ");
            var push = Console.ReadLine().ToLowerInvariant() == "y";

            var environmentFileBuilder = new EnvironmentFileBuilder
            {
                DatabasePassword = Helpers.SecureRandomString(32),
                Domain = _domain,
                IdentityCertPassword = _identityCertPassword,
                InstallationId = _installationId,
                InstallationKey = _installationKey,
                OutputDirectory = outputDir,
                Push = push,
                Url = url
            };
            environmentFileBuilder.Build();

            var appSettingsBuilder = new AppSettingsBuilder(url, _domain);
            appSettingsBuilder.Build();

            var appIdBuilder = new AppIdBuilder(url);
            appIdBuilder.Build();
        }

        private static void Update()
        {
            if(_parameters.ContainsKey("db"))
            {
                MigrateDatabase();
            }
            else
            {
                RebuildConfigs();
            }
        }

        private static void PrintEnvironment()
        {
            var vaultUrl = Helpers.GetValueFronEnvFile("global", "globalSettings__baseServiceUri__vault");
            Console.WriteLine("\nbitwarden is up and running!");
            Console.WriteLine("===================================================");
            Console.WriteLine("\n- visit {0}", vaultUrl);
            Console.Write("- to update, run ");
            if(_parameters.ContainsKey("env") && _parameters["env"] == "win")
            {
                Console.Write("'.\\bitwarden.ps1 -update'");
            }
            else
            {
                Console.Write("'./bitwarden.sh update'");
            }
            Console.WriteLine("\n");
        }

        private static void MigrateDatabase()
        {
            Console.WriteLine("Migrating database.");

            var dbPass = Helpers.GetValueFronEnvFile("mssql", "SA_PASSWORD");
            var masterConnectionString = Helpers.MakeSqlConnectionString("mssql", "master", "sa", dbPass ?? string.Empty);
            var vaultConnectionString = Helpers.MakeSqlConnectionString("mssql", "vault", "sa", dbPass ?? string.Empty);

            using(var connection = new SqlConnection(masterConnectionString))
            {
                var command = new SqlCommand(
                    "IF ((SELECT COUNT(1) FROM sys.databases WHERE [name] = 'vault') = 0) CREATE DATABASE [vault];",
                    connection);
                command.Connection.Open();
                command.ExecuteNonQuery();
            }

            var upgrader = DeployChanges.To
                .SqlDatabase(vaultConnectionString)
                .JournalToSqlTable("dbo", "Migration")
                .WithScriptsAndCodeEmbeddedInAssembly(Assembly.GetExecutingAssembly(),
                    s => s.Contains($".DbScripts.") && !s.Contains(".Archive."))
                .WithTransaction()
                .WithExecutionTimeout(new TimeSpan(0, 5, 0))
                .LogToConsole()
                .Build();

            var result = upgrader.PerformUpgrade();
            if(result.Successful)
            {
                Console.WriteLine("Migration successful.");
            }
            else
            {
                Console.WriteLine("Migration failed.");
            }
        }

        private static bool ValidateInstallation()
        {
            Console.Write("(!) Enter your installation id (get it at https://bitwarden.com/host): ");
            var installationId = Console.ReadLine();
            Guid installationidGuid;
            if(!Guid.TryParse(installationId.Trim(), out installationidGuid))
            {
                Console.WriteLine("Invalid installation id.");
                return false;
            }
            _installationId = installationidGuid;

            Console.Write("(!) Enter your installation key: ");
            _installationKey = Console.ReadLine();

            try
            {
                var response = new HttpClient().GetAsync("https://api.bitwarden.com/installations/" + _installationId)
                    .GetAwaiter().GetResult();

                if(!response.IsSuccessStatusCode)
                {
                    if(response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        Console.WriteLine("Invalid installation id.");
                    }
                    else
                    {
                        Console.WriteLine("Unable to validate installation id.");
                    }

                    return false;
                }

                var resultString = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var result = JsonConvert.DeserializeObject<dynamic>(resultString);
                if(!(bool)result.Enabled)
                {
                    Console.WriteLine("Installation id has been disabled.");
                    return false;
                }

                return true;
            }
            catch
            {
                Console.WriteLine("Unable to validate installation id. Problem contacting bitwarden server.");
                return false;
            }
        }

        private static void MakeCerts()
        {
            if(!_ssl)
            {
                Directory.CreateDirectory($"/bitwarden/ssl/self/{_domain}/");
                Console.WriteLine("Generating self signed SSL certificate.");
                _ssl = _selfSignedSsl = true;
                Exec("openssl req -x509 -newkey rsa:4096 -sha256 -nodes -days 365 " +
                    $"-keyout /bitwarden/ssl/self/{_domain}/private.key " +
                    $"-out /bitwarden/ssl/self/{_domain}/certificate.crt " +
                    $"-subj \"/C=US/ST=New York/L=New York/O=8bit Solutions LLC/OU=bitwarden/CN={_domain}\"");
            }

            if(_letsEncrypt)
            {
                Directory.CreateDirectory($"/bitwarden/letsencrypt/live/{_domain}/");
                Exec($"openssl dhparam -out /bitwarden/letsencrypt/live/{_domain}/dhparam.pem 2048");
            }

            Console.WriteLine("Generating key for IdentityServer.");
            Directory.CreateDirectory("/bitwarden/identity/");
            Exec("openssl req -x509 -newkey rsa:4096 -sha256 -nodes -keyout identity.key " +
                "-out identity.crt -subj \"/CN=bitwarden IdentityServer\" -days 10950");
            Exec("openssl pkcs12 -export -out /bitwarden/identity/identity.pfx -inkey identity.key " +
                $"-in identity.crt -certfile identity.crt -passout pass:{_identityCertPassword}");
        }

        private static void RebuildConfigs()
        {
            var url = Helpers.GetValueFronEnvFile("global", "globalSettings__baseServiceUri__vault");
            if(!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
            {
                Console.WriteLine("Unable to determine existing installation url.");
                return;
            }
            var domain = uri.Host;

            var nginxBuilder = new NginxConfigBuilder(domain);
            nginxBuilder.UpdateContext();
            nginxBuilder.BuildForUpdater();

            var appSettingsBuilder = new AppSettingsBuilder(url, domain);
            appSettingsBuilder.Build();

            var appIdBuilder = new AppIdBuilder(url);
            appIdBuilder.Build();
        }

        private static IDictionary<string, string> ParseParameters()
        {
            var dict = new Dictionary<string, string>();
            for(var i = 0; i < _args.Length; i = i + 2)
            {
                if(!_args[i].StartsWith("-"))
                {
                    continue;
                }

                dict.Add(_args[i].Substring(1), _args[i + 1]);
            }

            return dict;
        }

        private static string Exec(string cmd)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }
            };

            if(!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var escapedArgs = cmd.Replace("\"", "\\\"");
                process.StartInfo.FileName = "/bin/bash";
                process.StartInfo.Arguments = $"-c \"{escapedArgs}\"";
            }
            else
            {
                process.StartInfo.FileName = "powershell";
                process.StartInfo.Arguments = cmd;
            }

            process.Start();
            var result = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return result;
        }
    }
}
