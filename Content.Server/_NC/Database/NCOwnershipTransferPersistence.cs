// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<NCOwnershipResult> TransferNCPropertyShareAsync(
        Guid propertyId,
        NCOwnerType sourceType,
        string sourceId,
        NCOwnerType targetType,
        string targetId,
        int shareBasisPoints,
        Guid actorAccountId,
        string reason,
        int? roundId,
        Guid requestId);

    Task<NCOwnershipResult> TransferNCBusinessShareAsync(
        Guid businessId,
        int sourceProfileId,
        int targetProfileId,
        int shareBasisPoints,
        Guid actorAccountId,
        string reason,
        int? roundId,
        Guid requestId);

    Task<NCOwnershipResult> ResolveNCInheritanceAsync(
        Guid inheritanceCaseId,
        NCOwnerType targetType,
        string targetId,
        Guid actorAccountId,
        string reason,
        int? roundId,
        Guid requestId);
}

public sealed partial class ServerDbManager
{
    public Task<NCOwnershipResult> TransferNCPropertyShareAsync(
        Guid propertyId,
        NCOwnerType sourceType,
        string sourceId,
        NCOwnerType targetType,
        string targetId,
        int shareBasisPoints,
        Guid actorAccountId,
        string reason,
        int? roundId,
        Guid requestId)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.TransferNCPropertyShareAsync(
            propertyId, sourceType, sourceId, targetType, targetId, shareBasisPoints,
            actorAccountId, reason, roundId, requestId));
    }

    public Task<NCOwnershipResult> TransferNCBusinessShareAsync(
        Guid businessId,
        int sourceProfileId,
        int targetProfileId,
        int shareBasisPoints,
        Guid actorAccountId,
        string reason,
        int? roundId,
        Guid requestId)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.TransferNCBusinessShareAsync(
            businessId, sourceProfileId, targetProfileId, shareBasisPoints,
            actorAccountId, reason, roundId, requestId));
    }

    public Task<NCOwnershipResult> ResolveNCInheritanceAsync(
        Guid inheritanceCaseId,
        NCOwnerType targetType,
        string targetId,
        Guid actorAccountId,
        string reason,
        int? roundId,
        Guid requestId)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.ResolveNCInheritanceAsync(
            inheritanceCaseId, targetType, targetId, actorAccountId,
            reason, roundId, requestId));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<NCOwnershipResult> TransferNCPropertyShareAsync(
        Guid propertyId,
        NCOwnerType sourceType,
        string sourceId,
        NCOwnerType targetType,
        string targetId,
        int shareBasisPoints,
        Guid actorAccountId,
        string reason,
        int? roundId,
        Guid requestId)
    {
        if (!ValidShareMutation(sourceId, targetId, shareBasisPoints, reason) ||
            (sourceType == targetType && sourceId == targetId))
            return new NCOwnershipResult(false, "nc-ownership-error-invalid-data", null);

        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync();
        if (!await ValidateOwnershipRequestAsync(
                db.DbContext, targetType, targetId, requestId))
            return new NCOwnershipResult(false, "nc-ownership-error-target-unavailable", null);

        var property = await db.DbContext.NCProperty.FindAsync(propertyId);
        var source = await db.DbContext.NCPropertyOwnership.FindAsync(
            propertyId, sourceType, sourceId);
        if (property == null || source == null || source.ShareBasisPoints < shareBasisPoints)
            return new NCOwnershipResult(false, "nc-ownership-error-insufficient-share", null);

        var target = await db.DbContext.NCPropertyOwnership.FindAsync(
            propertyId, targetType, targetId);
        if (target == null)
        {
            target = new NCPropertyOwnership
            {
                PropertyId = propertyId,
                OwnerType = targetType,
                OwnerId = targetId,
                AcquiredAt = DateTime.UtcNow,
            };
            db.DbContext.NCPropertyOwnership.Add(target);
        }

        target.ShareBasisPoints += shareBasisPoints;
        source.ShareBasisPoints -= shareBasisPoints;
        if (source.ShareBasisPoints == 0)
            db.DbContext.NCPropertyOwnership.Remove(source);
        AddOwnershipMutationAudit(
            db.DbContext, "property-share-transferred", targetType, targetId,
            actorAccountId, reason, roundId, requestId, propertyId, shareBasisPoints);
        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return new NCOwnershipResult(true, null, propertyId);
    }

    public async Task<NCOwnershipResult> TransferNCBusinessShareAsync(
        Guid businessId,
        int sourceProfileId,
        int targetProfileId,
        int shareBasisPoints,
        Guid actorAccountId,
        string reason,
        int? roundId,
        Guid requestId)
    {
        if (!ValidShareMutation(
                sourceProfileId.ToString(), targetProfileId.ToString(), shareBasisPoints, reason) ||
            sourceProfileId == targetProfileId)
            return new NCOwnershipResult(false, "nc-ownership-error-invalid-data", null);

        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync();
        if (!await ValidateOwnershipRequestAsync(
                db.DbContext, NCOwnerType.Character, targetProfileId.ToString(), requestId))
            return new NCOwnershipResult(false, "nc-ownership-error-target-unavailable", null);

        var business = await db.DbContext.NCBusiness.FindAsync(businessId);
        var source = await db.DbContext.NCBusinessOwnership.FindAsync(
            businessId, sourceProfileId);
        if (business == null || source == null || source.ShareBasisPoints < shareBasisPoints)
            return new NCOwnershipResult(false, "nc-ownership-error-insufficient-share", null);

        var target = await db.DbContext.NCBusinessOwnership.FindAsync(
            businessId, targetProfileId);
        if (target == null)
        {
            target = new NCBusinessOwnership
            {
                BusinessId = businessId,
                OwnerProfileId = targetProfileId,
                OwnershipType = NCOwnershipType.Direct,
                AcquiredAt = DateTime.UtcNow,
            };
            db.DbContext.NCBusinessOwnership.Add(target);
        }

        target.ShareBasisPoints += shareBasisPoints;
        source.ShareBasisPoints -= shareBasisPoints;
        if (source.ShareBasisPoints == 0)
            db.DbContext.NCBusinessOwnership.Remove(source);
        AddOwnershipMutationAudit(
            db.DbContext, "business-share-transferred", NCOwnerType.Character,
            targetProfileId.ToString(), actorAccountId, reason, roundId,
            requestId, businessId, shareBasisPoints);
        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return new NCOwnershipResult(true, null, businessId);
    }

    public async Task<NCOwnershipResult> ResolveNCInheritanceAsync(
        Guid inheritanceCaseId,
        NCOwnerType targetType,
        string targetId,
        Guid actorAccountId,
        string reason,
        int? roundId,
        Guid requestId)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 512)
            return new NCOwnershipResult(false, "nc-ownership-error-invalid-data", null);

        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync();
        if (!await ValidateOwnershipRequestAsync(db.DbContext, targetType, targetId, requestId))
            return new NCOwnershipResult(false, "nc-ownership-error-target-unavailable", null);

        var inheritance = await db.DbContext.NCInheritanceCase.FindAsync(inheritanceCaseId);
        if (inheritance is not { Status: NCInheritanceStatus.Pending })
            return new NCOwnershipResult(false, "nc-ownership-error-inheritance-not-pending", null);

        if (inheritance.AssetType == NCInheritanceAssetType.Property)
        {
            var estateId = $"estate:{inheritanceCaseId:N}";
            var estate = await db.DbContext.NCPropertyOwnership.FindAsync(
                inheritance.AssetId, NCOwnerType.System, estateId);
            if (estate == null)
                return new NCOwnershipResult(false, "nc-ownership-error-estate-missing", null);
            var target = await db.DbContext.NCPropertyOwnership.FindAsync(
                inheritance.AssetId, targetType, targetId);
            if (target == null)
            {
                target = new NCPropertyOwnership
                {
                    PropertyId = inheritance.AssetId,
                    OwnerType = targetType,
                    OwnerId = targetId,
                    AcquiredAt = DateTime.UtcNow,
                };
                db.DbContext.NCPropertyOwnership.Add(target);
            }
            target.ShareBasisPoints += estate.ShareBasisPoints;
            db.DbContext.NCPropertyOwnership.Remove(estate);
            var property = await db.DbContext.NCProperty.FindAsync(inheritance.AssetId);
            if (property != null)
                property.Status = NCPropertyStatus.Active;
        }
        else
        {
            if (targetType != NCOwnerType.Character ||
                !int.TryParse(targetId, out var targetProfileId))
                return new NCOwnershipResult(false, "nc-ownership-error-invalid-business-owner", null);
            var target = await db.DbContext.NCBusinessOwnership.FindAsync(
                inheritance.AssetId, targetProfileId);
            if (target == null)
            {
                target = new NCBusinessOwnership
                {
                    BusinessId = inheritance.AssetId,
                    OwnerProfileId = targetProfileId,
                    OwnershipType = NCOwnershipType.Inherited,
                    AcquiredAt = DateTime.UtcNow,
                };
                db.DbContext.NCBusinessOwnership.Add(target);
            }
            target.ShareBasisPoints += inheritance.ShareBasisPoints;
            var business = await db.DbContext.NCBusiness.FindAsync(inheritance.AssetId);
            if (business != null)
                business.Status = NCBusinessStatus.Active;
        }

        inheritance.Status = NCInheritanceStatus.Resolved;
        inheritance.ResolvedAt = DateTime.UtcNow;
        inheritance.ResolvedOwnerType = targetType;
        inheritance.ResolvedOwnerId = targetId;
        inheritance.ResolvedByAccountId = actorAccountId;
        inheritance.Reason = reason.Trim();
        AddOwnershipMutationAudit(
            db.DbContext, "inheritance-resolved", targetType, targetId,
            actorAccountId, reason, roundId, requestId,
            inheritance.AssetId, inheritance.ShareBasisPoints);
        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return new NCOwnershipResult(true, null, inheritance.AssetId);
    }

    private static async Task<bool> ValidateOwnershipRequestAsync(
        ServerDbContext context,
        NCOwnerType targetType,
        string targetId,
        Guid requestId)
    {
        if (await context.NCPersistenceAudit.AnyAsync(entry => entry.RequestId == requestId))
            return false;
        if (targetType != NCOwnerType.Character)
            return !string.IsNullOrWhiteSpace(targetId) && targetId.Length <= 64;
        return int.TryParse(targetId, out var profileId) &&
               await context.NCCharacterLifecycle.AnyAsync(entry =>
                   entry.ProfileId == profileId &&
                   entry.Status == NCCharacterLifecycleStatus.Alive);
    }

    private static bool ValidShareMutation(
        string sourceId,
        string targetId,
        int shareBasisPoints,
        string reason)
    {
        return !string.IsNullOrWhiteSpace(sourceId) &&
               sourceId.Length <= 64 &&
               !string.IsNullOrWhiteSpace(targetId) &&
               targetId.Length <= 64 &&
               shareBasisPoints is > 0 and <= 10_000 &&
               !string.IsNullOrWhiteSpace(reason) &&
               reason.Length <= 512;
    }

    private static void AddOwnershipMutationAudit(
        ServerDbContext context,
        string action,
        NCOwnerType targetType,
        string targetId,
        Guid actorAccountId,
        string reason,
        int? roundId,
        Guid requestId,
        Guid assetId,
        int shareBasisPoints)
    {
        context.NCPersistenceAudit.Add(new NCPersistenceAudit
        {
            Timestamp = DateTime.UtcNow,
            RoundId = roundId,
            ActorAccountId = actorAccountId,
            TargetProfileId = targetType == NCOwnerType.Character &&
                int.TryParse(targetId, out var profileId)
                    ? profileId
                    : null,
            Action = action,
            NewValue = $"{assetId}:{shareBasisPoints}",
            Reason = reason.Trim(),
            RequestId = requestId,
        });
    }
}
