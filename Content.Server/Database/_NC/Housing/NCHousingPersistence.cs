// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Shared._NC.City.ZoneTemplates;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Housing;
using Content.Shared._NC.Identity;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record NCPropertySeedData(
    NCApartmentId PropertyId,
    NCBuildingId BuildingId,
    IReadOnlyList<NCZoneId> RoomIds,
    int PurchasePrice,
    string RentPolicyId,
    string AccessPolicyId);

public sealed record NCPropertyTenantData(
    NCCharacterId CharacterId,
    DateTime StartedAt,
    DateTime? ValidUntil,
    DateTime? RevokedAt);

public sealed record NCPropertyAccessGrantData(
    Guid GrantId,
    NCApartmentId PropertyId,
    NCCharacterId CharacterId,
    NCPropertyPermission Permissions,
    int AccessRevision,
    DateTime IssuedAt,
    DateTime? ValidUntil,
    DateTime? RevokedAt);

public sealed record NCPropertyData(
    NCApartmentId PropertyId,
    NCBuildingId BuildingId,
    IReadOnlyList<NCZoneId> RoomIds,
    NCCharacterId? OwnerCharacterId,
    IReadOnlyList<NCPropertyTenantData> Tenants,
    NCPropertyStatus Status,
    int PurchasePrice,
    DateTime? AcquiredAt,
    int AccessRevision,
    string RentPolicyId,
    string AccessPolicyId,
    string? CurrentTemplateId,
    int? CurrentTemplateRevision,
    NCZoneDeploymentId ActiveDeploymentId,
    string InteriorState,
    DateTime? NextRentDueAt,
    int Version);

public enum NCPropertyMutationResult : byte
{
    Success,
    NotFound,
    CharacterNotFound,
    BankAccountNotFound,
    InsufficientFunds,
    NotAvailable,
    NotAuthorized,
    Conflict,
    InvalidRequest,
}

public sealed record NCPropertyMutationData(
    NCPropertyMutationResult Result,
    NCPropertyData? Property,
    NCPropertyAccessGrantData? Grant,
    int? Balance);

public sealed record NCPropertySnapshotData(
    NCApartmentId PropertyId,
    string TemplateId,
    int TemplateRevision,
    int FormatVersion,
    string Payload,
    int EntityCount,
    int ResidentEntityCount,
    DateTime UpdatedAt);

public partial interface IServerDbManager
{
    Task<NCPropertyData?> EnsureNCPropertyAsync(NCPropertySeedData seed);
    Task<NCPropertyData?> GetNCPropertyAsync(NCApartmentId propertyId);
    Task<IReadOnlyList<NCPropertyData>> GetNCPropertiesAsync();
    Task<NCPropertyMutationData> TryPurchaseNCPropertyAsync(
        NCApartmentId propertyId,
        NCCharacterId characterId,
        int price,
        NCPropertyPermission permissions);
    Task<NCPropertyMutationData> TryRentNCPropertyAsync(
        NCApartmentId propertyId,
        NCCharacterId characterId,
        string templateId,
        int templateRevision,
        string rentPolicyId,
        int initialCharge,
        DateTime nextRentDueAt,
        NCPropertyPermission permissions);
    Task<NCPropertyMutationData> TryRenewNCPropertyRentAsync(
        NCApartmentId propertyId,
        NCCharacterId characterId,
        int charge,
        DateTime nextRentDueAt,
        int expectedVersion);
    Task<NCPropertyMutationData> TryMarkNCPropertyRentDelinquentAsync(
        NCApartmentId propertyId,
        int expectedVersion);
    Task<NCPropertyMutationData> TryEvictNCPropertyForNonPaymentAsync(
        NCApartmentId propertyId,
        int expectedVersion);
    Task<NCPropertyMutationData> TryConvertNCPropertyOwnerToRentalAsync(
        NCApartmentId propertyId,
        NCCharacterId characterId,
        string templateId,
        int templateRevision,
        string rentPolicyId,
        DateTime nextRentDueAt,
        int expectedVersion);
    Task<NCPropertyMutationData> TryReleaseNCPropertyAsync(
        NCApartmentId propertyId,
        NCCharacterId characterId,
        NCPropertyReleaseReason reason,
        int refund);
    Task<NCPropertyMutationData> GrantNCPropertyAccessAsync(
        NCApartmentId propertyId,
        NCCharacterId actorCharacterId,
        NCCharacterId subjectCharacterId,
        NCPropertyPermission permissions,
        DateTime? validUntil,
        int maximumActiveCards);
    Task<NCPropertyData?> SetNCPropertyInteriorAsync(
        NCApartmentId propertyId,
        NCZoneDeploymentId deploymentId,
        string templateId,
        int templateRevision,
        string interiorState,
        int expectedVersion);
    Task<IReadOnlyList<NCPropertySnapshotData>> GetNCPropertySnapshotsAsync();
    Task<bool> UpsertNCPropertySnapshotAsync(NCPropertySnapshotData snapshot);
    Task DeleteNCPropertySnapshotAsync(NCApartmentId propertyId);
}

public sealed partial class ServerDbManager
{
    public Task<NCPropertyData?> EnsureNCPropertyAsync(NCPropertySeedData seed)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.EnsureNCPropertyAsync(seed));
    }

    public Task<NCPropertyData?> GetNCPropertyAsync(NCApartmentId propertyId)
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCPropertyAsync(propertyId));
    }

    public Task<IReadOnlyList<NCPropertyData>> GetNCPropertiesAsync()
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCPropertiesAsync());
    }

    public Task<NCPropertyMutationData> TryPurchaseNCPropertyAsync(
        NCApartmentId propertyId,
        NCCharacterId characterId,
        int price,
        NCPropertyPermission permissions)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.TryPurchaseNCPropertyAsync(propertyId, characterId, price, permissions));
    }

    public Task<NCPropertyMutationData> TryRentNCPropertyAsync(
        NCApartmentId propertyId,
        NCCharacterId characterId,
        string templateId,
        int templateRevision,
        string rentPolicyId,
        int initialCharge,
        DateTime nextRentDueAt,
        NCPropertyPermission permissions)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.TryRentNCPropertyAsync(
            propertyId, characterId, templateId, templateRevision, rentPolicyId,
            initialCharge, nextRentDueAt, permissions));
    }

    public Task<NCPropertyMutationData> TryRenewNCPropertyRentAsync(
        NCApartmentId propertyId,
        NCCharacterId characterId,
        int charge,
        DateTime nextRentDueAt,
        int expectedVersion)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.TryRenewNCPropertyRentAsync(
            propertyId, characterId, charge, nextRentDueAt, expectedVersion));
    }

    public Task<NCPropertyMutationData> TryMarkNCPropertyRentDelinquentAsync(
        NCApartmentId propertyId,
        int expectedVersion)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.TryMarkNCPropertyRentDelinquentAsync(propertyId, expectedVersion));
    }

    public Task<NCPropertyMutationData> TryEvictNCPropertyForNonPaymentAsync(
        NCApartmentId propertyId,
        int expectedVersion)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.TryEvictNCPropertyForNonPaymentAsync(propertyId, expectedVersion));
    }

    public Task<NCPropertyMutationData> TryConvertNCPropertyOwnerToRentalAsync(
        NCApartmentId propertyId,
        NCCharacterId characterId,
        string templateId,
        int templateRevision,
        string rentPolicyId,
        DateTime nextRentDueAt,
        int expectedVersion)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.TryConvertNCPropertyOwnerToRentalAsync(
            propertyId, characterId, templateId, templateRevision,
            rentPolicyId, nextRentDueAt, expectedVersion));
    }

    public Task<NCPropertyMutationData> TryReleaseNCPropertyAsync(
        NCApartmentId propertyId,
        NCCharacterId characterId,
        NCPropertyReleaseReason reason,
        int refund)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.TryReleaseNCPropertyAsync(propertyId, characterId, reason, refund));
    }

    public Task<NCPropertyMutationData> GrantNCPropertyAccessAsync(
        NCApartmentId propertyId,
        NCCharacterId actorCharacterId,
        NCCharacterId subjectCharacterId,
        NCPropertyPermission permissions,
        DateTime? validUntil,
        int maximumActiveCards)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.GrantNCPropertyAccessAsync(
            propertyId, actorCharacterId, subjectCharacterId, permissions, validUntil, maximumActiveCards));
    }

    public Task<NCPropertyData?> SetNCPropertyInteriorAsync(
        NCApartmentId propertyId,
        NCZoneDeploymentId deploymentId,
        string templateId,
        int templateRevision,
        string interiorState,
        int expectedVersion)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.SetNCPropertyInteriorAsync(
            propertyId, deploymentId, templateId, templateRevision, interiorState, expectedVersion));
    }

    public Task<IReadOnlyList<NCPropertySnapshotData>> GetNCPropertySnapshotsAsync()
    {
        DbReadOpsMetric.Inc();
        return RunDbCommand(() => _db.GetNCPropertySnapshotsAsync());
    }

    public Task<bool> UpsertNCPropertySnapshotAsync(NCPropertySnapshotData snapshot)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.UpsertNCPropertySnapshotAsync(snapshot));
    }

    public Task DeleteNCPropertySnapshotAsync(NCApartmentId propertyId)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.DeleteNCPropertySnapshotAsync(propertyId));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<NCPropertyData?> EnsureNCPropertyAsync(NCPropertySeedData seed)
    {
        if (!seed.PropertyId.IsValid || !seed.BuildingId.IsValid || seed.PurchasePrice < 0)
            return null;

        await using var db = await GetDb();
        var id = seed.PropertyId.Value;
        var property = await QueryProperty(db.DbContext, id).SingleOrDefaultAsync();
        if (property != null)
            return ToData(property);

        property = new NCPropertyRecord
        {
            PropertyId = id,
            BuildingId = seed.BuildingId.Value,
            Status = (int) NCPropertyStatus.Available,
            PurchasePrice = seed.PurchasePrice,
            AccessRevision = 1,
            RentPolicyId = seed.RentPolicyId,
            AccessPolicyId = seed.AccessPolicyId,
            Version = 1,
            UpdatedAt = DateTime.UtcNow,
        };
        foreach (var roomId in seed.RoomIds.Distinct())
            property.Rooms.Add(new NCPropertyRoom { PropertyId = id, RoomId = roomId.Value });
        property.Events.Add(new NCPropertyEvent
        {
            PropertyId = id,
            EventType = NCPropertyEventType.Registered,
            CreatedAt = DateTime.UtcNow,
        });

        db.DbContext.NCPropertyRecords.Add(property);
        await db.DbContext.SaveChangesAsync();
        return ToData(property);
    }

    public async Task<NCPropertyData?> GetNCPropertyAsync(NCApartmentId propertyId)
    {
        if (!propertyId.IsValid)
            return null;
        await using var db = await GetDb();
        var property = await QueryProperty(db.DbContext, propertyId.Value)
            .AsNoTracking()
            .SingleOrDefaultAsync();
        return property == null ? null : ToData(property);
    }

    public async Task<IReadOnlyList<NCPropertyData>> GetNCPropertiesAsync()
    {
        await using var db = await GetDb();
        var properties = await db.DbContext.NCPropertyRecords
            .AsNoTracking()
            .Include(value => value.Rooms)
            .Include(value => value.Tenants)
            .OrderBy(value => value.BuildingId)
            .ThenBy(value => value.PropertyId)
            .ToListAsync();
        return properties.Select(ToData).ToArray();
    }

    public async Task<NCPropertyMutationData> TryPurchaseNCPropertyAsync(
        NCApartmentId propertyId,
        NCCharacterId characterId,
        int price,
        NCPropertyPermission permissions)
    {
        if (!propertyId.IsValid || !characterId.IsValid || price < 0)
            return Failed(NCPropertyMutationResult.InvalidRequest);

        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync();
        var property = await QueryProperty(db.DbContext, propertyId.Value)
            .AsNoTracking()
            .SingleOrDefaultAsync();
        if (property == null)
            return Failed(NCPropertyMutationResult.NotFound);
        if (property.Status != (int) NCPropertyStatus.Available || property.OwnerProfileId != null ||
            property.Tenants.Any(value => value.RevokedAt == null))
            return Failed(NCPropertyMutationResult.NotAvailable, property);

        var account = await db.DbContext.NCCharacterBankAccounts
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProfileId == characterId.Value);
        if (account == null)
            return Failed(NCPropertyMutationResult.BankAccountNotFound, property);
        if (account.Balance < price)
            return Failed(NCPropertyMutationResult.InsufficientFunds, property, account.Balance);

        var now = DateTime.UtcNow;
        var updatedAccounts = await db.DbContext.NCCharacterBankAccounts
            .Where(value => value.ProfileId == characterId.Value && value.Balance >= price)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.Balance, value => value.Balance - price)
                .SetProperty(value => value.UpdatedAt, now));
        if (updatedAccounts != 1)
        {
            await transaction.RollbackAsync();
            var currentBalance = await GetCurrentBalanceAsync(db.DbContext, characterId);
            return currentBalance == null
                ? Failed(NCPropertyMutationResult.BankAccountNotFound, property)
                : Failed(NCPropertyMutationResult.InsufficientFunds, property, currentBalance);
        }

        // The version/status predicate is the database-level ownership lock. Only one buyer can
        // move this property out of Available, even when several server processes race each other.
        var updatedProperties = await db.DbContext.NCPropertyRecords
            .Where(value => value.PropertyId == propertyId.Value &&
                value.Version == property.Version &&
                value.Status == (int) NCPropertyStatus.Available &&
                value.OwnerProfileId == null &&
                !value.Tenants.Any(tenant => tenant.RevokedAt == null))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.OwnerProfileId, characterId.Value)
                .SetProperty(value => value.Status, (int) NCPropertyStatus.OwnerOccupied)
                .SetProperty(value => value.PurchasePrice, price)
                .SetProperty(value => value.AcquiredAt, now)
                .SetProperty(value => value.AccessRevision, value => value.AccessRevision + 1)
                .SetProperty(value => value.Version, value => value.Version + 1)
                .SetProperty(value => value.UpdatedAt, now));
        if (updatedProperties != 1)
        {
            await transaction.RollbackAsync();
            return await PropertyClaimFailureAsync(db.DbContext, propertyId);
        }

        var accessRevision = property.AccessRevision + 1;
        await RevokeAllAccessAsync(db.DbContext, propertyId.Value, now);
        var grant = CreateGrant(db.DbContext, propertyId.Value, accessRevision, characterId, permissions, null, now);
        AddEvent(db.DbContext, propertyId.Value, NCPropertyEventType.AccessRevisionChanged, null, accessRevision.ToString(), now);
        AddEvent(db.DbContext, propertyId.Value, NCPropertyEventType.Purchased, characterId, string.Empty, now);

        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        var updatedProperty = await QueryProperty(db.DbContext, propertyId.Value)
            .AsNoTracking()
            .SingleAsync();
        return new NCPropertyMutationData(
            NCPropertyMutationResult.Success,
            ToData(updatedProperty),
            ToData(grant),
            account.Balance - price);
    }

    public async Task<NCPropertyMutationData> TryRentNCPropertyAsync(
        NCApartmentId propertyId,
        NCCharacterId characterId,
        string templateId,
        int templateRevision,
        string rentPolicyId,
        int initialCharge,
        DateTime nextRentDueAt,
        NCPropertyPermission permissions)
    {
        if (!propertyId.IsValid || !characterId.IsValid || templateId.Length == 0 ||
            templateRevision <= 0 || rentPolicyId.Length == 0 || initialCharge < 0 ||
            nextRentDueAt <= DateTime.UtcNow)
            return Failed(NCPropertyMutationResult.InvalidRequest);

        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync();
        var property = await QueryProperty(db.DbContext, propertyId.Value)
            .AsNoTracking()
            .SingleOrDefaultAsync();
        if (property == null)
            return Failed(NCPropertyMutationResult.NotFound);
        if (property.Status != (int) NCPropertyStatus.Available || property.OwnerProfileId != null ||
            property.Tenants.Any(value => value.RevokedAt == null))
            return Failed(NCPropertyMutationResult.NotAvailable, property);

        var account = await db.DbContext.NCCharacterBankAccounts
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProfileId == characterId.Value);
        if (account == null)
            return Failed(NCPropertyMutationResult.BankAccountNotFound, property);
        if (account.Balance < initialCharge)
            return Failed(NCPropertyMutationResult.InsufficientFunds, property, account.Balance);

        var now = DateTime.UtcNow;
        var updatedAccounts = await db.DbContext.NCCharacterBankAccounts
            .Where(value => value.ProfileId == characterId.Value && value.Balance >= initialCharge)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.Balance, value => value.Balance - initialCharge)
                .SetProperty(value => value.UpdatedAt, now));
        if (updatedAccounts != 1)
        {
            await transaction.RollbackAsync();
            var currentBalance = await GetCurrentBalanceAsync(db.DbContext, characterId);
            return currentBalance == null
                ? Failed(NCPropertyMutationResult.BankAccountNotFound, property)
                : Failed(NCPropertyMutationResult.InsufficientFunds, property, currentBalance);
        }

        // Renting uses the same database-level claim as purchasing, so two tenants cannot both
        // pay for and acquire the same apartment during a cross-process race.
        var updatedProperties = await db.DbContext.NCPropertyRecords
            .Where(value => value.PropertyId == propertyId.Value &&
                value.Version == property.Version &&
                value.Status == (int) NCPropertyStatus.Available &&
                value.OwnerProfileId == null &&
                !value.Tenants.Any(tenant => tenant.RevokedAt == null))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.Status, (int) NCPropertyStatus.Rented)
                .SetProperty(value => value.RentPolicyId, rentPolicyId)
                .SetProperty(value => value.CurrentTemplateId, templateId)
                .SetProperty(value => value.CurrentTemplateRevision, templateRevision)
                .SetProperty(value => value.InteriorState, "ready")
                .SetProperty(value => value.AcquiredAt, now)
                .SetProperty(value => value.NextRentDueAt, nextRentDueAt)
                .SetProperty(value => value.AccessRevision, value => value.AccessRevision + 1)
                .SetProperty(value => value.Version, value => value.Version + 1)
                .SetProperty(value => value.UpdatedAt, now));
        if (updatedProperties != 1)
        {
            await transaction.RollbackAsync();
            return await PropertyClaimFailureAsync(db.DbContext, propertyId);
        }

        var existingTenant = await db.DbContext.NCPropertyTenants
            .SingleOrDefaultAsync(value => value.PropertyId == propertyId.Value &&
                value.ProfileId == characterId.Value);
        if (existingTenant == null)
        {
            db.DbContext.NCPropertyTenants.Add(new NCPropertyTenant
            {
                PropertyId = propertyId.Value,
                ProfileId = characterId.Value,
                StartedAt = now,
            });
        }
        else
        {
            // The tenant key is stable across leases; reactivate the historical row for a new lease.
            existingTenant.StartedAt = now;
            existingTenant.ValidUntil = null;
            existingTenant.RevokedAt = null;
        }

        var accessRevision = property.AccessRevision + 1;
        await RevokeAllAccessAsync(db.DbContext, propertyId.Value, now);
        var grant = CreateGrant(
            db.DbContext,
            propertyId.Value,
            accessRevision,
            characterId,
            permissions,
            null,
            now);
        AddEvent(db.DbContext, propertyId.Value, NCPropertyEventType.AccessRevisionChanged, null, accessRevision.ToString(), now);
        AddEvent(db.DbContext, propertyId.Value, NCPropertyEventType.Rented, characterId, templateId, now);

        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        var updatedProperty = await QueryProperty(db.DbContext, propertyId.Value)
            .AsNoTracking()
            .SingleAsync();
        return new NCPropertyMutationData(
            NCPropertyMutationResult.Success,
            ToData(updatedProperty),
            ToData(grant),
            account.Balance - initialCharge);
    }

    public async Task<NCPropertyMutationData> TryRenewNCPropertyRentAsync(
        NCApartmentId propertyId,
        NCCharacterId characterId,
        int charge,
        DateTime nextRentDueAt,
        int expectedVersion)
    {
        if (!propertyId.IsValid || !characterId.IsValid || charge < 0 ||
            nextRentDueAt <= DateTime.UtcNow || expectedVersion <= 0)
            return Failed(NCPropertyMutationResult.InvalidRequest);

        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync();
        var property = await QueryProperty(db.DbContext, propertyId.Value)
            .AsNoTracking()
            .SingleOrDefaultAsync();
        if (property == null)
            return Failed(NCPropertyMutationResult.NotFound);
        if (property.Version != expectedVersion ||
            property.Status != (int) NCPropertyStatus.Rented &&
            property.Status != (int) NCPropertyStatus.Delinquent ||
            !property.Tenants.Any(value => value.ProfileId == characterId.Value && value.RevokedAt == null))
            return Failed(NCPropertyMutationResult.Conflict, property);

        var account = await db.DbContext.NCCharacterBankAccounts
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.ProfileId == characterId.Value);
        if (account == null)
            return Failed(NCPropertyMutationResult.BankAccountNotFound, property);
        if (account.Balance < charge)
            return Failed(NCPropertyMutationResult.InsufficientFunds, property, account.Balance);

        var now = DateTime.UtcNow;
        var updatedAccounts = await db.DbContext.NCCharacterBankAccounts
            .Where(value => value.ProfileId == characterId.Value && value.Balance >= charge)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.Balance, value => value.Balance - charge)
                .SetProperty(value => value.UpdatedAt, now));
        if (updatedAccounts != 1)
        {
            await transaction.RollbackAsync();
            var currentBalance = await GetCurrentBalanceAsync(db.DbContext, characterId);
            return currentBalance == null
                ? Failed(NCPropertyMutationResult.BankAccountNotFound, property)
                : Failed(NCPropertyMutationResult.InsufficientFunds, property, currentBalance);
        }

        var updatedProperties = await db.DbContext.NCPropertyRecords
            .Where(value => value.PropertyId == propertyId.Value &&
                value.Version == expectedVersion &&
                (value.Status == (int) NCPropertyStatus.Rented ||
                 value.Status == (int) NCPropertyStatus.Delinquent) &&
                value.Tenants.Any(tenant => tenant.ProfileId == characterId.Value && tenant.RevokedAt == null))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.Status, (int) NCPropertyStatus.Rented)
                .SetProperty(value => value.NextRentDueAt, nextRentDueAt)
                .SetProperty(value => value.Version, value => value.Version + 1)
                .SetProperty(value => value.UpdatedAt, now));
        if (updatedProperties != 1)
        {
            await transaction.RollbackAsync();
            return Failed(NCPropertyMutationResult.Conflict, property);
        }

        AddEvent(db.DbContext, propertyId.Value, NCPropertyEventType.RentRenewed,
            characterId, charge.ToString(), now);
        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        var updatedProperty = await QueryProperty(db.DbContext, propertyId.Value)
            .AsNoTracking()
            .SingleAsync();
        return new NCPropertyMutationData(
            NCPropertyMutationResult.Success,
            ToData(updatedProperty),
            null,
            account.Balance - charge);
    }

    public async Task<NCPropertyMutationData> TryMarkNCPropertyRentDelinquentAsync(
        NCApartmentId propertyId,
        int expectedVersion)
    {
        if (!propertyId.IsValid || expectedVersion <= 0)
            return Failed(NCPropertyMutationResult.InvalidRequest);

        await using var db = await GetDb();
        var property = await QueryProperty(db.DbContext, propertyId.Value)
            .AsNoTracking()
            .SingleOrDefaultAsync();
        if (property == null)
            return Failed(NCPropertyMutationResult.NotFound);
        if (property.Version != expectedVersion)
            return Failed(NCPropertyMutationResult.Conflict, property);
        if (property.Status == (int) NCPropertyStatus.Delinquent)
            return new NCPropertyMutationData(NCPropertyMutationResult.Success, ToData(property), null, null);
        if (property.Status != (int) NCPropertyStatus.Rented)
            return Failed(NCPropertyMutationResult.Conflict, property);

        var now = DateTime.UtcNow;
        var updated = await db.DbContext.NCPropertyRecords
            .Where(value => value.PropertyId == propertyId.Value &&
                value.Version == expectedVersion &&
                value.Status == (int) NCPropertyStatus.Rented)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.Status, (int) NCPropertyStatus.Delinquent)
                .SetProperty(value => value.Version, value => value.Version + 1)
                .SetProperty(value => value.UpdatedAt, now));
        if (updated != 1)
        {
            var current = await QueryProperty(db.DbContext, propertyId.Value)
                .AsNoTracking()
                .SingleAsync();
            return Failed(NCPropertyMutationResult.Conflict, current);
        }

        AddEvent(db.DbContext, property.PropertyId, NCPropertyEventType.RentDelinquent,
            null, property.NextRentDueAt?.ToString("O") ?? string.Empty, now);
        await db.DbContext.SaveChangesAsync();
        var updatedProperty = await QueryProperty(db.DbContext, propertyId.Value)
            .AsNoTracking()
            .SingleAsync();
        return new NCPropertyMutationData(NCPropertyMutationResult.Success, ToData(updatedProperty), null, null);
    }

    public async Task<NCPropertyMutationData> TryEvictNCPropertyForNonPaymentAsync(
        NCApartmentId propertyId,
        int expectedVersion)
    {
        if (!propertyId.IsValid || expectedVersion <= 0)
            return Failed(NCPropertyMutationResult.InvalidRequest);

        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync();
        var property = await QueryProperty(db.DbContext, propertyId.Value)
            .AsNoTracking()
            .SingleOrDefaultAsync();
        if (property == null)
            return Failed(NCPropertyMutationResult.NotFound);
        if (property.Version != expectedVersion ||
            property.Status != (int) NCPropertyStatus.Rented &&
            property.Status != (int) NCPropertyStatus.Delinquent &&
            property.Status != (int) NCPropertyStatus.EvictionPending)
            return Failed(NCPropertyMutationResult.Conflict, property);

        var now = DateTime.UtcNow;
        var subject = property.Tenants
            .Where(value => value.RevokedAt == null)
            .OrderBy(value => value.StartedAt)
            .Select(value => (NCCharacterId?) new NCCharacterId(value.ProfileId))
            .FirstOrDefault();
        var updated = await db.DbContext.NCPropertyRecords
            .Where(value => value.PropertyId == propertyId.Value &&
                value.Version == expectedVersion &&
                (value.Status == (int) NCPropertyStatus.Rented ||
                 value.Status == (int) NCPropertyStatus.Delinquent ||
                 value.Status == (int) NCPropertyStatus.EvictionPending))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.OwnerProfileId, (int?) null)
                .SetProperty(value => value.AcquiredAt, (DateTime?) null)
                .SetProperty(value => value.Status, (int) NCPropertyStatus.Available)
                .SetProperty(value => value.PurchasePrice, 0)
                .SetProperty(value => value.RentPolicyId, string.Empty)
                .SetProperty(value => value.NextRentDueAt, (DateTime?) null)
                .SetProperty(value => value.ActiveDeploymentId, (Guid?) null)
                .SetProperty(value => value.CurrentTemplateId, (string?) null)
                .SetProperty(value => value.CurrentTemplateRevision, (int?) null)
                .SetProperty(value => value.InteriorState, string.Empty)
                .SetProperty(value => value.AccessRevision, value => value.AccessRevision + 1)
                .SetProperty(value => value.Version, value => value.Version + 1)
                .SetProperty(value => value.UpdatedAt, now));
        if (updated != 1)
        {
            await transaction.RollbackAsync();
            var current = await QueryProperty(db.DbContext, propertyId.Value)
                .AsNoTracking()
                .SingleAsync();
            return Failed(NCPropertyMutationResult.Conflict, current);
        }

        await db.DbContext.NCPropertyTenants
            .Where(value => value.PropertyId == propertyId.Value && value.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.RevokedAt, now));
        await RevokeAllAccessAsync(db.DbContext, propertyId.Value, now);
        await db.DbContext.NCPropertySnapshots
            .Where(value => value.PropertyId == propertyId.Value)
            .ExecuteDeleteAsync();
        AddEvent(db.DbContext, property.PropertyId, NCPropertyEventType.AccessRevisionChanged,
            null, (property.AccessRevision + 1).ToString(), now);
        AddEvent(db.DbContext, property.PropertyId, NCPropertyEventType.Evicted,
            subject, "rent-grace-expired", now);

        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        var updatedProperty = await QueryProperty(db.DbContext, propertyId.Value)
            .AsNoTracking()
            .SingleAsync();
        return new NCPropertyMutationData(NCPropertyMutationResult.Success, ToData(updatedProperty), null, null);
    }

    public async Task<NCPropertyMutationData> TryConvertNCPropertyOwnerToRentalAsync(
        NCApartmentId propertyId,
        NCCharacterId characterId,
        string templateId,
        int templateRevision,
        string rentPolicyId,
        DateTime nextRentDueAt,
        int expectedVersion)
    {
        if (!propertyId.IsValid || !characterId.IsValid || templateId.Length == 0 ||
            templateRevision <= 0 || rentPolicyId.Length == 0 ||
            nextRentDueAt <= DateTime.UtcNow || expectedVersion <= 0)
            return Failed(NCPropertyMutationResult.InvalidRequest);

        await using var db = await GetDb();
        var property = await QueryProperty(db.DbContext, propertyId.Value).SingleOrDefaultAsync();
        if (property == null)
            return Failed(NCPropertyMutationResult.NotFound);
        if (property.Version != expectedVersion || property.OwnerProfileId != characterId.Value)
            return Failed(NCPropertyMutationResult.Conflict, property);

        var now = DateTime.UtcNow;
        var tenant = property.Tenants.SingleOrDefault(value => value.ProfileId == characterId.Value);
        if (tenant == null)
        {
            property.Tenants.Add(new NCPropertyTenant
            {
                PropertyId = property.PropertyId,
                ProfileId = characterId.Value,
                StartedAt = now,
            });
        }
        else
        {
            tenant.StartedAt = now;
            tenant.ValidUntil = null;
            tenant.RevokedAt = null;
        }

        property.OwnerProfileId = null;
        property.Status = (int) NCPropertyStatus.Rented;
        property.RentPolicyId = rentPolicyId;
        property.CurrentTemplateId = templateId;
        property.CurrentTemplateRevision = templateRevision;
        property.InteriorState = "ready";
        property.NextRentDueAt = nextRentDueAt;
        property.Version++;
        property.UpdatedAt = now;
        AddEvent(db.DbContext, property.PropertyId, NCPropertyEventType.AdministrativeChange,
            characterId, "owner-converted-to-renter", now);
        await db.DbContext.SaveChangesAsync();
        return new NCPropertyMutationData(NCPropertyMutationResult.Success, ToData(property), null, null);
    }

    public async Task<NCPropertyMutationData> TryReleaseNCPropertyAsync(
        NCApartmentId propertyId,
        NCCharacterId characterId,
        NCPropertyReleaseReason reason,
        int refund)
    {
        if (!propertyId.IsValid || !characterId.IsValid || refund < 0)
            return Failed(NCPropertyMutationResult.InvalidRequest);

        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync();
        var property = await QueryProperty(db.DbContext, propertyId.Value).SingleOrDefaultAsync();
        if (property == null)
            return Failed(NCPropertyMutationResult.NotFound);

        var owner = property.OwnerProfileId == characterId.Value;
        var tenant = property.Tenants.FirstOrDefault(value => value.ProfileId == characterId.Value && value.RevokedAt == null);
        // A tenant may leave voluntarily, but only the registered owner may sell the entire property.
        if (reason == NCPropertyReleaseReason.Sale && !owner)
            return Failed(NCPropertyMutationResult.NotAuthorized, property);
        if (!owner && tenant == null && reason is not NCPropertyReleaseReason.Administrative and not NCPropertyReleaseReason.LegalEvent)
            return Failed(NCPropertyMutationResult.NotAuthorized, property);

        int? balance = null;
        if (refund > 0 && owner)
        {
            var account = await db.DbContext.NCCharacterBankAccounts
                .SingleOrDefaultAsync(value => value.ProfileId == characterId.Value);
            if (account == null || (long) account.Balance + refund > int.MaxValue)
                return Failed(NCPropertyMutationResult.BankAccountNotFound, property);
            account.Balance += refund;
            account.UpdatedAt = DateTime.UtcNow;
            balance = account.Balance;
        }

        property.OwnerProfileId = null;
        property.AcquiredAt = null;
        property.Status = (int) NCPropertyStatus.Available;
        property.PurchasePrice = 0;
        property.RentPolicyId = string.Empty;
        property.NextRentDueAt = null;
        property.ActiveDeploymentId = null;
        property.CurrentTemplateId = null;
        property.CurrentTemplateRevision = null;
        property.InteriorState = string.Empty;
        property.AccessRevision++;
        property.Version++;
        var now = DateTime.UtcNow;
        property.UpdatedAt = now;
        foreach (var activeTenant in property.Tenants.Where(value => value.RevokedAt == null))
            activeTenant.RevokedAt = now;
        RevokeAllAccess(db.DbContext, property, now);
        var snapshot = await db.DbContext.NCPropertySnapshots
            .SingleOrDefaultAsync(value => value.PropertyId == property.PropertyId);
        if (snapshot != null)
            db.DbContext.NCPropertySnapshots.Remove(snapshot);
        AddEvent(db.DbContext, property.PropertyId, ToReleaseEvent(reason), characterId, reason.ToString(), now);

        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return new NCPropertyMutationData(NCPropertyMutationResult.Success, ToData(property), null, balance);
    }

    public async Task<NCPropertyMutationData> GrantNCPropertyAccessAsync(
        NCApartmentId propertyId,
        NCCharacterId actorCharacterId,
        NCCharacterId subjectCharacterId,
        NCPropertyPermission permissions,
        DateTime? validUntil,
        int maximumActiveCards)
    {
        if (!propertyId.IsValid || !actorCharacterId.IsValid || !subjectCharacterId.IsValid ||
            permissions == NCPropertyPermission.None || maximumActiveCards <= 0 ||
            validUntil is { } expiry && expiry <= DateTime.UtcNow)
            return Failed(NCPropertyMutationResult.InvalidRequest);

        await using var db = await GetDb();
        var property = await QueryProperty(db.DbContext, propertyId.Value).SingleOrDefaultAsync();
        if (property == null)
            return Failed(NCPropertyMutationResult.NotFound);
        if (!property.Tenants.Any(value => value.ProfileId == actorCharacterId.Value && value.RevokedAt == null))
            return Failed(NCPropertyMutationResult.NotAuthorized, property);
        if (!await db.DbContext.Profile.AnyAsync(value => value.Id == subjectCharacterId.Value))
            return Failed(NCPropertyMutationResult.CharacterNotFound, property);
        if (property.AccessGrants.Count(value => value.RevokedAt == null &&
                (value.ValidUntil == null || value.ValidUntil > DateTime.UtcNow)) >= maximumActiveCards)
            return Failed(NCPropertyMutationResult.Conflict, property);

        var grant = CreateGrant(
            db.DbContext,
            property.PropertyId,
            property.AccessRevision,
            subjectCharacterId,
            permissions,
            validUntil,
            DateTime.UtcNow);
        property.Version++;
        property.UpdatedAt = DateTime.UtcNow;
        await db.DbContext.SaveChangesAsync();
        return new NCPropertyMutationData(NCPropertyMutationResult.Success, ToData(property), ToData(grant), null);
    }

    public async Task<NCPropertyData?> SetNCPropertyInteriorAsync(
        NCApartmentId propertyId,
        NCZoneDeploymentId deploymentId,
        string templateId,
        int templateRevision,
        string interiorState,
        int expectedVersion)
    {
        if (!propertyId.IsValid || !deploymentId.IsValid || templateId.Length == 0 || templateRevision <= 0)
            return null;

        await using var db = await GetDb();
        var property = await QueryProperty(db.DbContext, propertyId.Value).SingleOrDefaultAsync();
        if (property == null || property.Version != expectedVersion)
            return null;
        if (property.ActiveDeploymentId == deploymentId.Value &&
            property.CurrentTemplateId == templateId &&
            property.CurrentTemplateRevision == templateRevision)
            return ToData(property);

        property.ActiveDeploymentId = deploymentId.Value;
        property.CurrentTemplateId = templateId;
        property.CurrentTemplateRevision = templateRevision;
        property.InteriorState = interiorState;
        property.Version++;
        property.UpdatedAt = DateTime.UtcNow;
        AddEvent(
            db.DbContext,
            property.PropertyId,
            NCPropertyEventType.InteriorActivated,
            property.Tenants
                .Where(value => value.RevokedAt == null)
                .OrderBy(value => value.StartedAt)
                .Select(value => (NCCharacterId?) new NCCharacterId(value.ProfileId))
                .FirstOrDefault(),
            templateId,
            DateTime.UtcNow);
        await db.DbContext.SaveChangesAsync();
        return ToData(property);
    }

    public async Task<IReadOnlyList<NCPropertySnapshotData>> GetNCPropertySnapshotsAsync()
    {
        await using var db = await GetDb();
        return await db.DbContext.NCPropertySnapshots
            .AsNoTracking()
            .OrderBy(value => value.PropertyId)
            .Select(value => new NCPropertySnapshotData(
                new NCApartmentId(new NCZoneId(value.PropertyId)),
                value.TemplateId,
                value.TemplateRevision,
                value.FormatVersion,
                value.Payload,
                value.EntityCount,
                value.ResidentEntityCount,
                value.UpdatedAt))
            .ToListAsync();
    }

    public async Task<bool> UpsertNCPropertySnapshotAsync(NCPropertySnapshotData snapshot)
    {
        if (!snapshot.PropertyId.IsValid || snapshot.TemplateId.Length == 0 ||
            snapshot.TemplateRevision <= 0 || snapshot.FormatVersion <= 0 ||
            snapshot.Payload.Length == 0 || snapshot.EntityCount < 0 ||
            snapshot.ResidentEntityCount < 0 || snapshot.ResidentEntityCount > snapshot.EntityCount)
            return false;

        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync();
        if (!await db.DbContext.NCPropertyRecords.AnyAsync(value => value.PropertyId == snapshot.PropertyId.Value))
            return false;

        var record = await db.DbContext.NCPropertySnapshots
            .SingleOrDefaultAsync(value => value.PropertyId == snapshot.PropertyId.Value);
        if (record == null)
        {
            record = new NCPropertySnapshot { PropertyId = snapshot.PropertyId.Value };
            db.DbContext.NCPropertySnapshots.Add(record);
        }

        record.TemplateId = snapshot.TemplateId;
        record.TemplateRevision = snapshot.TemplateRevision;
        record.FormatVersion = snapshot.FormatVersion;
        record.Payload = snapshot.Payload;
        record.EntityCount = snapshot.EntityCount;
        record.ResidentEntityCount = snapshot.ResidentEntityCount;
        record.UpdatedAt = snapshot.UpdatedAt;
        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return true;
    }

    public async Task DeleteNCPropertySnapshotAsync(NCApartmentId propertyId)
    {
        if (!propertyId.IsValid)
            return;

        await using var db = await GetDb();
        await db.DbContext.NCPropertySnapshots
            .Where(value => value.PropertyId == propertyId.Value)
            .ExecuteDeleteAsync();
    }

    private static IQueryable<NCPropertyRecord> QueryProperty(ServerDbContext context, Guid propertyId)
    {
        return context.NCPropertyRecords
            .Include(value => value.Rooms)
            .Include(value => value.Tenants)
            .Include(value => value.AccessGrants)
            .Where(value => value.PropertyId == propertyId);
    }

    private static NCPropertyAccessGrant CreateGrant(
        ServerDbContext context,
        Guid propertyId,
        int accessRevision,
        NCCharacterId characterId,
        NCPropertyPermission permissions,
        DateTime? validUntil,
        DateTime issuedAt)
    {
        var grant = new NCPropertyAccessGrant
        {
            GrantId = Guid.NewGuid(),
            PropertyId = propertyId,
            ProfileId = characterId.Value,
            Permissions = (int) permissions,
            AccessRevision = accessRevision,
            IssuedAt = issuedAt,
            ValidUntil = validUntil,
        };
        // GrantId is application-generated. Explicit Add prevents EF from mistaking the non-default
        // GUID for an already-persistent dependent and issuing an UPDATE that affects zero rows.
        context.NCPropertyAccessGrants.Add(grant);
        return grant;
    }

    private static async Task RevokeAllAccessAsync(ServerDbContext context, Guid propertyId, DateTime revokedAt)
    {
        await context.NCPropertyAccessGrants
            .Where(value => value.PropertyId == propertyId && value.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.RevokedAt, revokedAt));
    }

    private static void RevokeAllAccess(ServerDbContext context, NCPropertyRecord property, DateTime revokedAt)
    {
        foreach (var grant in property.AccessGrants.Where(value => value.RevokedAt == null))
            grant.RevokedAt = revokedAt;
        AddEvent(
            context,
            property.PropertyId,
            NCPropertyEventType.AccessRevisionChanged,
            null,
            property.AccessRevision.ToString(),
            revokedAt);
    }

    private static void AddEvent(
        ServerDbContext context,
        Guid propertyId,
        NCPropertyEventType type,
        NCCharacterId? subject,
        string reason,
        DateTime createdAt)
    {
        context.NCPropertyEvents.Add(new NCPropertyEvent
        {
            PropertyId = propertyId,
            EventType = type,
            SubjectProfileId = subject?.Value,
            Reason = reason,
            CreatedAt = createdAt,
        });
    }

    private static Task<int?> GetCurrentBalanceAsync(ServerDbContext context, NCCharacterId characterId)
    {
        return context.NCCharacterBankAccounts
            .AsNoTracking()
            .Where(value => value.ProfileId == characterId.Value)
            .Select(value => (int?) value.Balance)
            .SingleOrDefaultAsync();
    }

    private static async Task<NCPropertyMutationData> PropertyClaimFailureAsync(
        ServerDbContext context,
        NCApartmentId propertyId)
    {
        var current = await QueryProperty(context, propertyId.Value)
            .AsNoTracking()
            .SingleOrDefaultAsync();
        if (current == null)
            return Failed(NCPropertyMutationResult.NotFound);
        if (current.Status != (int) NCPropertyStatus.Available || current.OwnerProfileId != null ||
            current.Tenants.Any(value => value.RevokedAt == null))
            return Failed(NCPropertyMutationResult.NotAvailable, current);
        return Failed(NCPropertyMutationResult.Conflict, current);
    }

    private static NCPropertyEventType ToReleaseEvent(NCPropertyReleaseReason reason)
    {
        return reason switch
        {
            NCPropertyReleaseReason.Sale => NCPropertyEventType.Sold,
            NCPropertyReleaseReason.VoluntaryMoveOut => NCPropertyEventType.Vacated,
            NCPropertyReleaseReason.Eviction => NCPropertyEventType.Evicted,
            NCPropertyReleaseReason.LegalEvent => NCPropertyEventType.LegalRevocation,
            _ => NCPropertyEventType.AdministrativeChange,
        };
    }

    private static NCPropertyMutationData Failed(
        NCPropertyMutationResult result,
        NCPropertyRecord? property = null,
        int? balance = null)
    {
        return new NCPropertyMutationData(result, property == null ? null : ToData(property), null, balance);
    }

    private static NCPropertyData ToData(NCPropertyRecord property)
    {
        return new NCPropertyData(
            new NCApartmentId(new NCZoneId(property.PropertyId)),
            new NCBuildingId(new NCZoneId(property.BuildingId)),
            property.Rooms.Select(value => new NCZoneId(value.RoomId)).ToArray(),
            property.OwnerProfileId is { } owner ? new NCCharacterId(owner) : null,
            property.Tenants.Select(value => new NCPropertyTenantData(
                new NCCharacterId(value.ProfileId), value.StartedAt, value.ValidUntil, value.RevokedAt)).ToArray(),
            (NCPropertyStatus) property.Status,
            property.PurchasePrice,
            property.AcquiredAt,
            property.AccessRevision,
            property.RentPolicyId,
            property.AccessPolicyId,
            property.CurrentTemplateId,
            property.CurrentTemplateRevision,
            new NCZoneDeploymentId(property.ActiveDeploymentId ?? Guid.Empty),
            property.InteriorState,
            property.NextRentDueAt,
            property.Version);
    }

    private static NCPropertyAccessGrantData ToData(NCPropertyAccessGrant grant)
    {
        return new NCPropertyAccessGrantData(
            grant.GrantId,
            new NCApartmentId(new NCZoneId(grant.PropertyId)),
            new NCCharacterId(grant.ProfileId),
            (NCPropertyPermission) grant.Permissions,
            grant.AccessRevision,
            grant.IssuedAt,
            grant.ValidUntil,
            grant.RevokedAt);
    }
}
