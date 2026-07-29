// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading.Tasks;

namespace Content.Server.Database;

public partial interface IServerDbManager
{
    Task<NCLegalMutationResult> SetNCLicenseAsync(
        int profileId,
        string prototypeId,
        bool active,
        int? validityDays,
        int? actorProfileId,
        Guid? actorAdminId,
        Guid actorAccountId,
        string reason,
        int? roundId,
        Guid requestId);

    Task<NCLegalMutationResult> SetNCDocumentAsync(
        int profileId,
        string prototypeId,
        bool active,
        int? validityDays,
        string payload,
        int? actorProfileId,
        Guid? actorAdminId,
        Guid actorAccountId,
        string reason,
        int? roundId,
        Guid requestId);
}

public sealed partial class ServerDbManager
{
    public Task<NCLegalMutationResult> SetNCLicenseAsync(
        int profileId,
        string prototypeId,
        bool active,
        int? validityDays,
        int? actorProfileId,
        Guid? actorAdminId,
        Guid actorAccountId,
        string reason,
        int? roundId,
        Guid requestId)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.SetNCLicenseAsync(
            profileId, prototypeId, active, validityDays, actorProfileId,
            actorAdminId, actorAccountId, reason, roundId, requestId));
    }

    public Task<NCLegalMutationResult> SetNCDocumentAsync(
        int profileId,
        string prototypeId,
        bool active,
        int? validityDays,
        string payload,
        int? actorProfileId,
        Guid? actorAdminId,
        Guid actorAccountId,
        string reason,
        int? roundId,
        Guid requestId)
    {
        DbWriteOpsMetric.Inc();
        return RunDbCommand(() => _db.SetNCDocumentAsync(
            profileId, prototypeId, active, validityDays, payload, actorProfileId,
            actorAdminId, actorAccountId, reason, roundId, requestId));
    }
}

public abstract partial class ServerDbBase
{
    public async Task<NCLegalMutationResult> SetNCLicenseAsync(
        int profileId,
        string prototypeId,
        bool active,
        int? validityDays,
        int? actorProfileId,
        Guid? actorAdminId,
        Guid actorAccountId,
        string reason,
        int? roundId,
        Guid requestId)
    {
        if (!ValidLegalMutation(prototypeId, reason, validityDays))
            return new NCLegalMutationResult(false, "nc-legal-error-invalid-data");

        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync();
        if (!await IsMutableProfileAsync(db.DbContext, profileId))
            return new NCLegalMutationResult(false, "nc-legal-error-profile-unavailable");
        if (await db.DbContext.NCPersistenceAudit.AnyAsync(entry => entry.RequestId == requestId))
            return new NCLegalMutationResult(false, "nc-legal-error-duplicate-request");

        var now = DateTime.UtcNow;
        var row = await db.DbContext.NCCharacterLicense.FindAsync(profileId, prototypeId);
        if (row == null)
        {
            if (!active)
                return new NCLegalMutationResult(false, "nc-legal-error-record-not-found");
            row = new NCCharacterLicense
            {
                ProfileId = profileId,
                LicensePrototypeId = prototypeId,
            };
            db.DbContext.NCCharacterLicense.Add(row);
        }

        row.Status = active ? NCLegalRecordStatus.Active : NCLegalRecordStatus.Revoked;
        row.IssuedAt = active ? now : row.IssuedAt;
        row.ExpiresAt = active && validityDays != null ? now.AddDays(validityDays.Value) : null;
        row.IssuedByProfileId = active ? actorProfileId : row.IssuedByProfileId;
        row.IssuedByAdminId = active ? actorAdminId : row.IssuedByAdminId;
        row.RevokedAt = active ? null : now;
        row.Reason = reason.Trim();
        AddLegalAudit(
            db.DbContext, profileId, actorProfileId, actorAccountId, roundId, requestId,
            active ? "license-issued" : "license-revoked", prototypeId, reason);
        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return new NCLegalMutationResult(true, null);
    }

    public async Task<NCLegalMutationResult> SetNCDocumentAsync(
        int profileId,
        string prototypeId,
        bool active,
        int? validityDays,
        string payload,
        int? actorProfileId,
        Guid? actorAdminId,
        Guid actorAccountId,
        string reason,
        int? roundId,
        Guid requestId)
    {
        if (!ValidLegalMutation(prototypeId, reason, validityDays) || payload.Length > 2048)
            return new NCLegalMutationResult(false, "nc-legal-error-invalid-data");

        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync();
        if (!await IsMutableProfileAsync(db.DbContext, profileId))
            return new NCLegalMutationResult(false, "nc-legal-error-profile-unavailable");
        if (await db.DbContext.NCPersistenceAudit.AnyAsync(entry => entry.RequestId == requestId))
            return new NCLegalMutationResult(false, "nc-legal-error-duplicate-request");

        var row = await db.DbContext.NCCharacterDocument
            .SingleOrDefaultAsync(entry =>
                entry.ProfileId == profileId &&
                entry.DocumentPrototypeId == prototypeId &&
                entry.Status == NCLegalRecordStatus.Active);
        var now = DateTime.UtcNow;
        if (row == null)
        {
            if (!active)
                return new NCLegalMutationResult(false, "nc-legal-error-record-not-found");
            row = new NCCharacterDocument
            {
                DocumentId = Guid.NewGuid(),
                ProfileId = profileId,
                DocumentPrototypeId = prototypeId,
                SerialNumber = $"NCD{Guid.NewGuid():N}"[..32],
            };
            db.DbContext.NCCharacterDocument.Add(row);
        }

        row.Status = active ? NCLegalRecordStatus.Active : NCLegalRecordStatus.Revoked;
        row.IssuedAt = active ? now : row.IssuedAt;
        row.ExpiresAt = active && validityDays != null ? now.AddDays(validityDays.Value) : null;
        row.IssuedByProfileId = active ? actorProfileId : row.IssuedByProfileId;
        row.IssuedByAdminId = active ? actorAdminId : row.IssuedByAdminId;
        row.RevokedAt = active ? null : now;
        row.Payload = active ? payload.Trim() : row.Payload;
        row.Reason = reason.Trim();
        AddLegalAudit(
            db.DbContext, profileId, actorProfileId, actorAccountId, roundId, requestId,
            active ? "document-issued" : "document-revoked", prototypeId, reason);
        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return new NCLegalMutationResult(true, null);
    }

    private static Task<bool> IsMutableProfileAsync(ServerDbContext context, int profileId)
    {
        return context.NCCharacterLifecycle.AnyAsync(entry =>
            entry.ProfileId == profileId &&
            entry.Status == NCCharacterLifecycleStatus.Alive);
    }

    private static bool ValidLegalMutation(string prototypeId, string reason, int? validityDays)
    {
        return !string.IsNullOrWhiteSpace(prototypeId) &&
               prototypeId.Length <= 64 &&
               !string.IsNullOrWhiteSpace(reason) &&
               reason.Length <= 512 &&
               validityDays is null or > 0;
    }

    private static void AddLegalAudit(
        ServerDbContext context,
        int profileId,
        int? actorProfileId,
        Guid actorAccountId,
        int? roundId,
        Guid requestId,
        string action,
        string prototypeId,
        string reason)
    {
        context.NCPersistenceAudit.Add(new NCPersistenceAudit
        {
            Timestamp = DateTime.UtcNow,
            RoundId = roundId,
            ActorAccountId = actorAccountId,
            ActorProfileId = actorProfileId,
            TargetProfileId = profileId,
            Action = action,
            NewValue = prototypeId,
            Reason = reason.Trim(),
            RequestId = requestId,
        });
    }
}
