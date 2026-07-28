using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading.Tasks;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<NCOwnershipResult> RegisterNCPropertyAsync(
        string prototypeId,
        string propertyType,
        int ownerProfileId,
        int shareBasisPoints,
        Guid actorAccountId,
        int? roundId,
        Guid requestId);

    Task<NCOwnershipResult> RegisterNCBusinessAsync(
        string name,
        string businessType,
        int ownerProfileId,
        int shareBasisPoints,
        string currencyPrototypeId,
        Guid actorAccountId,
        int? roundId,
        Guid requestId);
}

public sealed partial class ServerDbManager
{
    public Task<NCOwnershipResult> RegisterNCPropertyAsync(
        string prototypeId,
        string propertyType,
        int ownerProfileId,
        int shareBasisPoints,
        Guid actorAccountId,
        int? roundId,
        Guid requestId)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.RegisterNCPropertyAsync(
            prototypeId, propertyType, ownerProfileId, shareBasisPoints,
            actorAccountId, roundId, requestId));
    }

    public Task<NCOwnershipResult> RegisterNCBusinessAsync(
        string name,
        string businessType,
        int ownerProfileId,
        int shareBasisPoints,
        string currencyPrototypeId,
        Guid actorAccountId,
        int? roundId,
        Guid requestId)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.RegisterNCBusinessAsync(
            name, businessType, ownerProfileId, shareBasisPoints,
            currencyPrototypeId, actorAccountId, roundId, requestId));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<NCOwnershipResult> RegisterNCPropertyAsync(
        string prototypeId,
        string propertyType,
        int ownerProfileId,
        int shareBasisPoints,
        Guid actorAccountId,
        int? roundId,
        Guid requestId)
    {
        if (!ValidOwnership(prototypeId, propertyType, shareBasisPoints))
            return new NCOwnershipResult(false, "nc-ownership-error-invalid-data", null);

        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync();
        if (await db.DbContext.NCPersistenceAudit.AnyAsync(entry => entry.RequestId == requestId))
            return new NCOwnershipResult(false, "nc-ownership-error-duplicate-request", null);
        if (!await db.DbContext.Profile.AnyAsync(profile => profile.Id == ownerProfileId))
            return new NCOwnershipResult(false, "nc-ownership-error-profile-not-found", null);

        var now = DateTime.UtcNow;
        var property = new NCProperty
        {
            PropertyId = Guid.NewGuid(),
            PrototypeId = prototypeId.Trim(),
            PropertyType = propertyType.Trim(),
            Status = NCPropertyStatus.Active,
        };
        db.DbContext.NCProperty.Add(property);
        db.DbContext.NCPropertyOwnership.Add(new NCPropertyOwnership
        {
            PropertyId = property.PropertyId,
            OwnerType = NCOwnerType.Character,
            OwnerId = ownerProfileId.ToString(),
            ShareBasisPoints = shareBasisPoints,
            AcquiredAt = now,
        });
        AddOwnershipAudit(db.DbContext, "property-created", ownerProfileId,
            actorAccountId, roundId, requestId, property.PropertyId.ToString());
        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return new NCOwnershipResult(true, null, property.PropertyId);
    }

    public async Task<NCOwnershipResult> RegisterNCBusinessAsync(
        string name,
        string businessType,
        int ownerProfileId,
        int shareBasisPoints,
        string currencyPrototypeId,
        Guid actorAccountId,
        int? roundId,
        Guid requestId)
    {
        if (!ValidOwnership(name, businessType, shareBasisPoints) ||
            string.IsNullOrWhiteSpace(currencyPrototypeId))
            return new NCOwnershipResult(false, "nc-ownership-error-invalid-data", null);

        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync();
        if (await db.DbContext.NCPersistenceAudit.AnyAsync(entry => entry.RequestId == requestId))
            return new NCOwnershipResult(false, "nc-ownership-error-duplicate-request", null);
        if (!await db.DbContext.Profile.AnyAsync(profile => profile.Id == ownerProfileId))
            return new NCOwnershipResult(false, "nc-ownership-error-profile-not-found", null);

        var now = DateTime.UtcNow;
        var businessId = Guid.NewGuid();
        var account = new NCBankAccount
        {
            BankAccountId = Guid.NewGuid(),
            AccountNumber = businessId.ToString("N"),
            AccountType = NCBankAccountType.Business,
            CurrencyPrototypeId = currencyPrototypeId,
            Status = NCBankAccountStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.DbContext.NCBankAccount.Add(account);
        db.DbContext.NCBusiness.Add(new NCBusiness
        {
            BusinessId = businessId,
            Name = name.Trim(),
            BusinessType = businessType.Trim(),
            BankAccountId = account.BankAccountId,
            Status = NCBusinessStatus.Active,
            CreatedAt = now,
        });
        db.DbContext.NCBusinessOwnership.Add(new NCBusinessOwnership
        {
            BusinessId = businessId,
            OwnerProfileId = ownerProfileId,
            ShareBasisPoints = shareBasisPoints,
            OwnershipType = NCOwnershipType.Administrative,
            AcquiredAt = now,
        });
        AddOwnershipAudit(db.DbContext, "business-created", ownerProfileId,
            actorAccountId, roundId, requestId, businessId.ToString());
        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return new NCOwnershipResult(true, null, businessId);
    }

    private static bool ValidOwnership(string name, string type, int shareBasisPoints)
    {
        return !string.IsNullOrWhiteSpace(name) &&
               !string.IsNullOrWhiteSpace(type) &&
               shareBasisPoints is > 0 and <= 10_000;
    }

    private static void AddOwnershipAudit(
        ServerDbContext context,
        string action,
        int targetProfileId,
        Guid actorAccountId,
        int? roundId,
        Guid requestId,
        string newValue)
    {
        context.NCPersistenceAudit.Add(new NCPersistenceAudit
        {
            Timestamp = DateTime.UtcNow,
            RoundId = roundId,
            ActorAccountId = actorAccountId,
            TargetProfileId = targetProfileId,
            Action = action,
            NewValue = newValue,
            Reason = "administrative-registration",
            RequestId = requestId,
        });
    }
}
