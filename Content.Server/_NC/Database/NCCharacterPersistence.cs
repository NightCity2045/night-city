using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using System.Threading.Tasks;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    /// <summary>
    /// Loads a complete character aggregate and creates its mandatory rows when first used.
    /// </summary>
    Task<NCCharacterSnapshot> GetOrCreateNCCharacterAsync(
        int profileId,
        Guid accountId,
        string currencyPrototypeId,
        long startingBalance);
}

public sealed partial class ServerDbManager
{
    public Task<NCCharacterSnapshot> GetOrCreateNCCharacterAsync(
        int profileId,
        Guid accountId,
        string currencyPrototypeId,
        long startingBalance)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.GetOrCreateNCCharacterAsync(
            profileId,
            accountId,
            currencyPrototypeId,
            startingBalance));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<NCCharacterSnapshot> GetOrCreateNCCharacterAsync(
        int profileId,
        Guid accountId,
        string currencyPrototypeId,
        long startingBalance)
    {
        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync();

        if (!await db.DbContext.Profile.AnyAsync(profile =>
                profile.Id == profileId &&
                profile.Preference.UserId == accountId))
        {
            throw new InvalidOperationException(
                $"Persistent character profile {profileId} is not owned by account {accountId}.");
        }

        var now = DateTime.UtcNow;
        var progression = await db.DbContext.NCCharacterProgression
            .SingleOrDefaultAsync(entry => entry.ProfileId == profileId);
        if (progression == null)
        {
            progression = new NCCharacterProgression
            {
                ProfileId = profileId,
                CompletedRounds = 0,
                Level = 1,
                SpentSkillPoints = 0,
                UpdatedAt = now,
            };
            db.DbContext.NCCharacterProgression.Add(progression);
        }

        var lifecycle = await db.DbContext.NCCharacterLifecycle
            .SingleOrDefaultAsync(entry => entry.ProfileId == profileId);
        if (lifecycle == null)
        {
            lifecycle = new NCCharacterLifecycle
            {
                ProfileId = profileId,
                Status = NCCharacterLifecycleStatus.Alive,
            };
            db.DbContext.NCCharacterLifecycle.Add(lifecycle);
        }

        var bank = await db.DbContext.NCBankAccount
            .SingleOrDefaultAsync(entry =>
                entry.OwnerProfileId == profileId &&
                entry.AccountType == NCBankAccountType.Personal &&
                entry.Status != NCBankAccountStatus.Closed);
        if (bank == null)
        {
            bank = new NCBankAccount
            {
                BankAccountId = Guid.NewGuid(),
                AccountNumber = $"NC{profileId:D10}",
                AccountType = NCBankAccountType.Personal,
                OwnerProfileId = profileId,
                CurrencyPrototypeId = currencyPrototypeId,
                Balance = Math.Max(0, startingBalance),
                Status = NCBankAccountStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.DbContext.NCBankAccount.Add(bank);
        }

        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        // All returned entities are detached so gameplay code cannot accidentally mutate tracked database state.
        var skills = await db.DbContext.NCCharacterSkill
            .AsNoTracking()
            .Where(entry => entry.ProfileId == profileId)
            .ToListAsync();
        var employment = await db.DbContext.NCCharacterEmployment
            .AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.ProfileId == profileId);
        bank = await db.DbContext.NCBankAccount
            .AsNoTracking()
            .SingleAsync(entry => entry.BankAccountId == bank.BankAccountId);
        var properties = await db.DbContext.NCPropertyOwnership
            .AsNoTracking()
            .Where(entry =>
                entry.OwnerType == NCOwnerType.Character &&
                entry.OwnerId == profileId.ToString())
            .ToListAsync();
        var businesses = await db.DbContext.NCBusinessOwnership
            .AsNoTracking()
            .Where(entry => entry.OwnerProfileId == profileId)
            .ToListAsync();
        var licenses = await db.DbContext.NCCharacterLicense
            .AsNoTracking()
            .Where(entry => entry.ProfileId == profileId)
            .ToListAsync();

        return new NCCharacterSnapshot(
            progression,
            skills,
            employment,
            bank,
            properties,
            businesses,
            licenses,
            lifecycle);
    }
}
