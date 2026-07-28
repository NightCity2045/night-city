using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<NCLifecycleResult> SetNCPermadeathPendingAsync(
        int profileId,
        Guid accountId,
        int roundId,
        bool pending,
        string reason,
        Guid requestId);

    Task<NCLifecycleResult> FinalizeNCPermadeathForAccountAsync(Guid accountId, int roundId);
    Task<NCLifecycleResult> FinalizeAllNCPermadeathsAsync(int roundId);
}

public sealed partial class ServerDbManager
{
    public Task<NCLifecycleResult> SetNCPermadeathPendingAsync(
        int profileId,
        Guid accountId,
        int roundId,
        bool pending,
        string reason,
        Guid requestId)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.SetNCPermadeathPendingAsync(
            profileId, accountId, roundId, pending, reason, requestId));
    }

    public Task<NCLifecycleResult> FinalizeNCPermadeathForAccountAsync(Guid accountId, int roundId)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.FinalizeNCPermadeathForAccountAsync(accountId, roundId));
    }

    public Task<NCLifecycleResult> FinalizeAllNCPermadeathsAsync(int roundId)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.FinalizeAllNCPermadeathsAsync(roundId));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<NCLifecycleResult> SetNCPermadeathPendingAsync(
        int profileId,
        Guid accountId,
        int roundId,
        bool pending,
        string reason,
        Guid requestId)
    {
        await using var db = await GetDb();
        var lifecycle = await db.DbContext.NCCharacterLifecycle
            .SingleOrDefaultAsync(entry => entry.ProfileId == profileId);
        if (lifecycle == null ||
            !await db.DbContext.Profile.AnyAsync(profile =>
                profile.Id == profileId && profile.Preference.UserId == accountId))
            return new NCLifecycleResult(false, "nc-permadeath-error-profile-not-found");

        if (!pending)
        {
            lifecycle.Status = NCCharacterLifecycleStatus.Alive;
            lifecycle.DeclaredRoundId = null;
            lifecycle.DeclaredAt = null;
            lifecycle.DeclaredByAccountId = null;
            lifecycle.DeclaredByProfileId = null;
            lifecycle.Reason = null;
            lifecycle.RequestId = null;
        }
        else if (lifecycle.Status == NCCharacterLifecycleStatus.Alive)
        {
            lifecycle.Status = NCCharacterLifecycleStatus.PermadeathPending;
            lifecycle.DeclaredRoundId = roundId;
            lifecycle.DeclaredAt = DateTime.UtcNow;
            lifecycle.DeclaredByAccountId = accountId;
            lifecycle.DeclaredByProfileId = profileId;
            lifecycle.Reason = reason;
            lifecycle.RequestId = requestId;
        }

        await db.DbContext.SaveChangesAsync();
        return new NCLifecycleResult(true, null);
    }

    public async Task<NCLifecycleResult> FinalizeNCPermadeathForAccountAsync(Guid accountId, int roundId)
    {
        await using var db = await GetDb();
        var profileIds = await db.DbContext.NCCharacterLifecycle
            .Where(entry => entry.Status == NCCharacterLifecycleStatus.PermadeathPending)
            .Where(entry => db.DbContext.Profile.Any(profile =>
                profile.Id == entry.ProfileId && profile.Preference.UserId == accountId))
            .Select(entry => entry.ProfileId)
            .ToListAsync();
        return await FinalizeProfilesAsync(db, profileIds, roundId);
    }

    public async Task<NCLifecycleResult> FinalizeAllNCPermadeathsAsync(int roundId)
    {
        await using var db = await GetDb();
        var profileIds = await db.DbContext.NCCharacterLifecycle
            .Where(entry => entry.Status == NCCharacterLifecycleStatus.PermadeathPending)
            .Select(entry => entry.ProfileId)
            .ToListAsync();
        return await FinalizeProfilesAsync(db, profileIds, roundId);
    }

    private static async Task<NCLifecycleResult> FinalizeProfilesAsync(
        DbGuard db,
        IReadOnlyCollection<int> profileIds,
        int roundId)
    {
        if (profileIds.Count == 0)
            return new NCLifecycleResult(true, null);

        await using var transaction = await db.DbContext.Database.BeginTransactionAsync();
        var finalized = 0;
        foreach (var profileId in profileIds)
        {
            var profile = await db.DbContext.Profile
                .Include(entry => entry.Preference)
                .SingleOrDefaultAsync(entry => entry.Id == profileId);
            var lifecycle = await db.DbContext.NCCharacterLifecycle.FindAsync(profileId);
            if (profile == null || lifecycle?.Status != NCCharacterLifecycleStatus.PermadeathPending)
                continue;

            lifecycle.Status = NCCharacterLifecycleStatus.Finalizing;
            var now = DateTime.UtcNow;
            var requestId = Guid.NewGuid();

            var personalAccounts = await db.DbContext.NCBankAccount
                .Where(entry => entry.OwnerProfileId == profileId)
                .ToListAsync();
            foreach (var account in personalAccounts)
            {
                account.Status = NCBankAccountStatus.Closed;
                account.AccountType = NCBankAccountType.System;
                account.OwnerProfileId = null;
                account.ClosedAt = now;
                account.UpdatedAt = now;
                account.Version++;
            }

            var businessOwnerships = await db.DbContext.NCBusinessOwnership
                .Where(entry => entry.OwnerProfileId == profileId)
                .ToListAsync();
            var businessIds = businessOwnerships.Select(entry => entry.BusinessId).ToArray();
            var businesses = await db.DbContext.NCBusiness
                .Where(entry => businessIds.Contains(entry.BusinessId))
                .ToListAsync();
            foreach (var business in businesses)
                business.Status = NCBusinessStatus.InheritancePending;
            db.DbContext.NCBusinessOwnership.RemoveRange(businessOwnerships);

            var ownerId = profileId.ToString();
            var propertyOwnerships = await db.DbContext.NCPropertyOwnership
                .Where(entry => entry.OwnerType == NCOwnerType.Character && entry.OwnerId == ownerId)
                .ToListAsync();
            var propertyIds = propertyOwnerships.Select(entry => entry.PropertyId).ToArray();
            var properties = await db.DbContext.NCProperty
                .Where(entry => propertyIds.Contains(entry.PropertyId))
                .ToListAsync();
            foreach (var property in properties)
                property.Status = NCPropertyStatus.InheritancePending;
            db.DbContext.NCPropertyOwnership.RemoveRange(propertyOwnerships);

            db.DbContext.NCDeletedCharacterAudit.Add(new NCDeletedCharacterAudit
            {
                DeletedProfileId = profileId,
                LastCharacterName = profile.CharacterName,
                AccountId = profile.Preference.UserId,
                DeletionReason = lifecycle.Reason ?? "permadeath",
                RoundId = roundId,
                DeletedAt = now,
                RequestId = requestId,
            });
            db.DbContext.NCPersistenceAudit.Add(new NCPersistenceAudit
            {
                Timestamp = now,
                RoundId = roundId,
                ActorAccountId = lifecycle.DeclaredByAccountId,
                ActorProfileId = lifecycle.DeclaredByProfileId,
                TargetProfileId = profileId,
                Action = "permadeath-finalized",
                OldValue = profile.CharacterName,
                Reason = lifecycle.Reason ?? "permadeath",
                RequestId = requestId,
            });

            var remainingSlot = await db.DbContext.Profile
                .Where(entry => entry.PreferenceId == profile.PreferenceId && entry.Id != profileId)
                .OrderBy(entry => entry.Slot)
                .Select(entry => (int?) entry.Slot)
                .FirstOrDefaultAsync();
            if (profile.Preference.SelectedCharacterSlot == profile.Slot)
                profile.Preference.SelectedCharacterSlot = remainingSlot ?? 0;

            db.DbContext.Profile.Remove(profile);
            finalized++;
        }

        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return new NCLifecycleResult(true, null, finalized);
    }
}
