﻿using AutoMapper;
using Bit.Core.KeyManagement.UserKey;
using Bit.Core.Repositories;
using Bit.Infrastructure.EntityFramework.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DataModel = Bit.Core.Models.Data;

#nullable enable

namespace Bit.Infrastructure.EntityFramework.Repositories;

public class UserRepository : Repository<Core.Entities.User, User, Guid>, IUserRepository
{
    public UserRepository(IServiceScopeFactory serviceScopeFactory, IMapper mapper)
        : base(serviceScopeFactory, mapper, (DatabaseContext context) => context.Users)
    { }

    public async Task<Core.Entities.User?> GetByEmailAsync(string email)
    {
        using (var scope = ServiceScopeFactory.CreateScope())
        {
            var dbContext = GetDatabaseContext(scope);
            var entity = await GetDbSet(dbContext).FirstOrDefaultAsync(e => e.Email == email);
            return Mapper.Map<Core.Entities.User>(entity);
        }
    }

    public async Task<IEnumerable<Core.Entities.User>> GetManyByEmailsAsync(IEnumerable<string> emails)
    {
        using (var scope = ServiceScopeFactory.CreateScope())
        {
            var dbContext = GetDatabaseContext(scope);
            var users = await GetDbSet(dbContext)
                .Where(u => emails.Contains(u.Email))
                .ToListAsync();
            return Mapper.Map<List<Core.Entities.User>>(users);
        }
    }

    public async Task<DataModel.UserKdfInformation?> GetKdfInformationByEmailAsync(string email)
    {
        using (var scope = ServiceScopeFactory.CreateScope())
        {
            var dbContext = GetDatabaseContext(scope);
            return await GetDbSet(dbContext).Where(e => e.Email == email)
                .Select(e => new DataModel.UserKdfInformation
                {
                    Kdf = e.Kdf,
                    KdfIterations = e.KdfIterations,
                    KdfMemory = e.KdfMemory,
                    KdfParallelism = e.KdfParallelism
                }).SingleOrDefaultAsync();
        }
    }

    public async Task<ICollection<Core.Entities.User>> SearchAsync(string email, int skip, int take)
    {
        using (var scope = ServiceScopeFactory.CreateScope())
        {
            var dbContext = GetDatabaseContext(scope);
            List<User> users;
            if (dbContext.Database.IsNpgsql())
            {
                users = await GetDbSet(dbContext)
                    .Where(e => e.Email == null ||
                        EF.Functions.ILike(EF.Functions.Collate(e.Email, "default"), $"{email}%"))
                    .OrderBy(e => e.Email)
                    .Skip(skip).Take(take)
                    .ToListAsync();
            }
            else
            {
                users = await GetDbSet(dbContext)
                    .Where(e => email == null || e.Email.StartsWith(email))
                    .OrderBy(e => e.Email)
                    .Skip(skip).Take(take)
                    .ToListAsync();
            }
            return Mapper.Map<List<Core.Entities.User>>(users);
        }
    }

    public async Task<ICollection<Core.Entities.User>> GetManyByPremiumAsync(bool premium)
    {
        using (var scope = ServiceScopeFactory.CreateScope())
        {
            var dbContext = GetDatabaseContext(scope);
            var users = await GetDbSet(dbContext).Where(e => e.Premium == premium).ToListAsync();
            return Mapper.Map<List<Core.Entities.User>>(users);
        }
    }

    public async Task<string?> GetPublicKeyAsync(Guid id)
    {
        using (var scope = ServiceScopeFactory.CreateScope())
        {
            var dbContext = GetDatabaseContext(scope);
            return await GetDbSet(dbContext).Where(e => e.Id == id).Select(e => e.PublicKey).SingleOrDefaultAsync();
        }
    }

    public async Task<DateTime> GetAccountRevisionDateAsync(Guid id)
    {
        using (var scope = ServiceScopeFactory.CreateScope())
        {
            var dbContext = GetDatabaseContext(scope);
            return await GetDbSet(dbContext).Where(e => e.Id == id).Select(e => e.AccountRevisionDate)
                .SingleOrDefaultAsync();
        }
    }

    public async Task UpdateStorageAsync(Guid id)
    {
        await base.UserUpdateStorage(id);
    }

    public async Task UpdateRenewalReminderDateAsync(Guid id, DateTime renewalReminderDate)
    {
        using (var scope = ServiceScopeFactory.CreateScope())
        {
            var dbContext = GetDatabaseContext(scope);
            var user = new User
            {
                Id = id,
                RenewalReminderDate = renewalReminderDate,
            };
            var set = GetDbSet(dbContext);
            set.Attach(user);
            dbContext.Entry(user).Property(e => e.RenewalReminderDate).IsModified = true;
            await dbContext.SaveChangesAsync();
        }
    }

    public async Task<Core.Entities.User?> GetBySsoUserAsync(string externalId, Guid? organizationId)
    {
        using (var scope = ServiceScopeFactory.CreateScope())
        {
            var dbContext = GetDatabaseContext(scope);
            var ssoUser = await dbContext.SsoUsers.SingleOrDefaultAsync(e =>
                e.OrganizationId == organizationId && e.ExternalId == externalId);

            if (ssoUser == null)
            {
                return null;
            }

            var entity = await dbContext.Users.SingleOrDefaultAsync(e => e.Id == ssoUser.UserId);
            return Mapper.Map<Core.Entities.User>(entity);
        }
    }

    /// <inheritdoc />
    public async Task UpdateUserKeyAndEncryptedDataAsync(Core.Entities.User user,
        IEnumerable<UpdateEncryptedDataForKeyRotation> updateDataActions)
    {
        using var scope = ServiceScopeFactory.CreateScope();
        var dbContext = GetDatabaseContext(scope);

        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        try
        {
            // Update user
            var entity = await dbContext.Users.FindAsync(user.Id);
            if (entity == null)
            {
                throw new ArgumentException("User not found", nameof(user));
            }

            entity.SecurityStamp = user.SecurityStamp;
            entity.Key = user.Key;

            entity.PrivateKey = user.PrivateKey;
            entity.LastKeyRotationDate = user.LastKeyRotationDate;
            entity.AccountRevisionDate = user.AccountRevisionDate;
            entity.RevisionDate = user.RevisionDate;

            await dbContext.SaveChangesAsync();

            //  Update re-encrypted data
            foreach (var action in updateDataActions)
            {
                // connection and transaction aren't used in EF
                await action();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

    }


    public async Task UpdateUserKeyAndEncryptedDataV2Async(Core.Entities.User user,
        IEnumerable<UpdateEncryptedDataForKeyRotation> updateDataActions)
    {
        using var scope = ServiceScopeFactory.CreateScope();
        var dbContext = GetDatabaseContext(scope);

        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        // Update user
        var userEntity = await dbContext.Users.FindAsync(user.Id);
        if (userEntity == null)
        {
            throw new ArgumentException("User not found", nameof(user));
        }

        userEntity.SecurityStamp = user.SecurityStamp;
        userEntity.Key = user.Key;
        userEntity.PrivateKey = user.PrivateKey;

        userEntity.Kdf = user.Kdf;
        userEntity.KdfIterations = user.KdfIterations;
        userEntity.KdfMemory = user.KdfMemory;
        userEntity.KdfParallelism = user.KdfParallelism;

        userEntity.Email = user.Email;

        userEntity.MasterPassword = user.MasterPassword;
        userEntity.MasterPasswordHint = user.MasterPasswordHint;

        userEntity.LastKeyRotationDate = user.LastKeyRotationDate;
        userEntity.AccountRevisionDate = user.AccountRevisionDate;
        userEntity.RevisionDate = user.RevisionDate;

        await dbContext.SaveChangesAsync();

        //  Update re-encrypted data
        foreach (var action in updateDataActions)
        {
            // connection and transaction aren't used in EF
            await action();
        }

        await transaction.CommitAsync();
    }

    public async Task<IEnumerable<Core.Entities.User>> GetManyAsync(IEnumerable<Guid> ids)
    {
        using (var scope = ServiceScopeFactory.CreateScope())
        {
            var dbContext = GetDatabaseContext(scope);
            var users = dbContext.Users.Where(x => ids.Contains(x.Id));
            return await users.ToListAsync();
        }
    }

    public async Task<IEnumerable<DataModel.UserWithCalculatedPremium>> GetManyWithCalculatedPremiumAsync(IEnumerable<Guid> ids)
    {
        using (var scope = ServiceScopeFactory.CreateScope())
        {
            var dbContext = GetDatabaseContext(scope);
            var users = dbContext.Users.Where(x => ids.Contains(x.Id));
            return await users.Select(e => new DataModel.UserWithCalculatedPremium(e)
            {
                HasPremiumAccess = e.Premium || dbContext.OrganizationUsers
                    .Any(ou => ou.UserId == e.Id &&
                               dbContext.Organizations
                                   .Any(o => o.Id == ou.OrganizationId &&
                                             o.UsersGetPremium == true &&
                                             o.Enabled == true))
            }).ToListAsync();
        }
    }

    public override async Task DeleteAsync(Core.Entities.User user)
    {
        using (var scope = ServiceScopeFactory.CreateScope())
        {
            var dbContext = GetDatabaseContext(scope);

            var transaction = await dbContext.Database.BeginTransactionAsync();

            dbContext.WebAuthnCredentials.RemoveRange(dbContext.WebAuthnCredentials.Where(w => w.UserId == user.Id));
            dbContext.Ciphers.RemoveRange(dbContext.Ciphers.Where(c => c.UserId == user.Id));
            dbContext.Folders.RemoveRange(dbContext.Folders.Where(f => f.UserId == user.Id));
            dbContext.AuthRequests.RemoveRange(dbContext.AuthRequests.Where(s => s.UserId == user.Id));
            dbContext.Devices.RemoveRange(dbContext.Devices.Where(d => d.UserId == user.Id));
            var collectionUsers = from cu in dbContext.CollectionUsers
                                  join ou in dbContext.OrganizationUsers on cu.OrganizationUserId equals ou.Id
                                  where ou.UserId == user.Id
                                  select cu;
            dbContext.CollectionUsers.RemoveRange(collectionUsers);
            var groupUsers = from gu in dbContext.GroupUsers
                             join ou in dbContext.OrganizationUsers on gu.OrganizationUserId equals ou.Id
                             where ou.UserId == user.Id
                             select gu;
            dbContext.GroupUsers.RemoveRange(groupUsers);
            dbContext.UserProjectAccessPolicy.RemoveRange(
                dbContext.UserProjectAccessPolicy.Where(ap => ap.OrganizationUser.UserId == user.Id));
            dbContext.UserServiceAccountAccessPolicy.RemoveRange(
                dbContext.UserServiceAccountAccessPolicy.Where(ap => ap.OrganizationUser.UserId == user.Id));
            dbContext.OrganizationUsers.RemoveRange(dbContext.OrganizationUsers.Where(ou => ou.UserId == user.Id));
            dbContext.ProviderUsers.RemoveRange(dbContext.ProviderUsers.Where(pu => pu.UserId == user.Id));
            dbContext.SsoUsers.RemoveRange(dbContext.SsoUsers.Where(su => su.UserId == user.Id));
            dbContext.EmergencyAccesses.RemoveRange(
                dbContext.EmergencyAccesses.Where(ea => ea.GrantorId == user.Id || ea.GranteeId == user.Id));
            dbContext.Sends.RemoveRange(dbContext.Sends.Where(s => s.UserId == user.Id));
            dbContext.NotificationStatuses.RemoveRange(dbContext.NotificationStatuses.Where(ns => ns.UserId == user.Id));
            dbContext.Notifications.RemoveRange(dbContext.Notifications.Where(n => n.UserId == user.Id));

            var mappedUser = Mapper.Map<User>(user);
            dbContext.Users.Remove(mappedUser);

            await transaction.CommitAsync();
            await dbContext.SaveChangesAsync();
        }
    }

    public async Task DeleteManyAsync(IEnumerable<Core.Entities.User> users)
    {
        using (var scope = ServiceScopeFactory.CreateScope())
        {
            var dbContext = GetDatabaseContext(scope);

            var transaction = await dbContext.Database.BeginTransactionAsync();

            var targetIds = users.Select(u => u.Id).ToList();

            await dbContext.WebAuthnCredentials.Where(wa => targetIds.Contains(wa.UserId)).ExecuteDeleteAsync();
            await dbContext.Ciphers.Where(c => targetIds.Contains(c.UserId ?? default)).ExecuteDeleteAsync();
            await dbContext.Folders.Where(f => targetIds.Contains(f.UserId)).ExecuteDeleteAsync();
            await dbContext.AuthRequests.Where(a => targetIds.Contains(a.UserId)).ExecuteDeleteAsync();
            await dbContext.Devices.Where(d => targetIds.Contains(d.UserId)).ExecuteDeleteAsync();
            var collectionUsers = from cu in dbContext.CollectionUsers
                                  join ou in dbContext.OrganizationUsers on cu.OrganizationUserId equals ou.Id
                                  where targetIds.Contains(ou.UserId ?? default)
                                  select cu;
            dbContext.CollectionUsers.RemoveRange(collectionUsers);
            var groupUsers = from gu in dbContext.GroupUsers
                             join ou in dbContext.OrganizationUsers on gu.OrganizationUserId equals ou.Id
                             where targetIds.Contains(ou.UserId ?? default)
                             select gu;
            dbContext.GroupUsers.RemoveRange(groupUsers);
            await dbContext.UserProjectAccessPolicy.Where(ap => targetIds.Contains(ap.OrganizationUser.UserId ?? default)).ExecuteDeleteAsync();
            await dbContext.UserServiceAccountAccessPolicy.Where(ap => targetIds.Contains(ap.OrganizationUser.UserId ?? default)).ExecuteDeleteAsync();
            await dbContext.OrganizationUsers.Where(ou => targetIds.Contains(ou.UserId ?? default)).ExecuteDeleteAsync();
            await dbContext.ProviderUsers.Where(pu => targetIds.Contains(pu.UserId ?? default)).ExecuteDeleteAsync();
            await dbContext.SsoUsers.Where(su => targetIds.Contains(su.UserId)).ExecuteDeleteAsync();
            await dbContext.EmergencyAccesses.Where(ea => targetIds.Contains(ea.GrantorId) || targetIds.Contains(ea.GranteeId ?? default)).ExecuteDeleteAsync();
            await dbContext.Sends.Where(s => targetIds.Contains(s.UserId ?? default)).ExecuteDeleteAsync();
            await dbContext.NotificationStatuses.Where(ns => targetIds.Contains(ns.UserId)).ExecuteDeleteAsync();
            await dbContext.Notifications.Where(n => targetIds.Contains(n.UserId ?? default)).ExecuteDeleteAsync();

            foreach (var u in users)
            {
                var mappedUser = Mapper.Map<User>(u);
                dbContext.Users.Remove(mappedUser);
            }


            await transaction.CommitAsync();
            await dbContext.SaveChangesAsync();
        }
    }
}
