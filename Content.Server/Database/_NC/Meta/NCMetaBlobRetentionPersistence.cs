// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Meta.Persistence;
using Content.Shared._NC.Persistence.Time;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public sealed record NCMetaBlobMaintenanceResult(bool BackfillComplete, int RevisionsRemoved, int BlobsRemoved);

public partial interface IServerDbManager
{
    Task<NCMetaBlobMaintenanceResult> MaintainNCMetaBlobsAsync();
    Task<NCAggregateSnapshotData?> GetNCMetaAggregateRevisionAsync(string aggregateType, string aggregateId, long version);
}

public sealed partial class ServerDbManager
{
    public Task<NCMetaBlobMaintenanceResult> MaintainNCMetaBlobsAsync()
    {
        DbWriteOpsMetric.Inc();
        return RunNCPersistentDbCommand(true, null, () => _db.MaintainNCMetaBlobsAsync());
    }

    public Task<NCAggregateSnapshotData?> GetNCMetaAggregateRevisionAsync(string aggregateType, string aggregateId, long version)
    {
        DbReadOpsMetric.Inc();
        return RunNCPersistentDbCommand(false, null, () => _db.GetNCMetaAggregateRevisionAsync(aggregateType, aggregateId, version));
    }
}

public abstract partial class ServerDbBase
{
    public const int NCMetaRevisionRetentionDays = 30;
    public const int NCMetaMaximumRetainedRevisions = 128;
    public const int NCMetaOrphanGraceHours = 24;
    private const string MetaCheckpointSection = "meta-aggregates";

    private static async Task TrackNCMetaBlobReferencesAsync(ServerDbContext context, string type, string id,
        long version, int formatVersion, Guid epoch, long ticks, string payload, DateTime createdAt, bool reconcileReferences = false)
    {
        if (!MetaBlobReferences.IsTracked(type)) return;
        var roots = MetaBlobReferences.Extract(type, payload);
        var existing = await context.NCMetaAggregateRevisions.SingleOrDefaultAsync(value =>
            value.AggregateType == type && value.AggregateId == id && value.AggregateVersion == version);
        if (existing != null)
        {
            if (existing.ReferencePolicyVersion > MetaBlobReferences.PolicyVersion)
                throw new InvalidOperationException("A newer META reference policy owns this revision.");
            if (existing.Payload != payload || existing.FormatVersion != formatVersion || existing.SessionEpoch != epoch ||
                existing.ActiveWorldTicks != ticks) throw new InvalidOperationException("Conflicting immutable META revision.");
            if (!reconcileReferences && existing.ReferencePolicyVersion == MetaBlobReferences.PolicyVersion) return;
            var known = (await context.NCMetaAggregateBlobReferences.AsNoTracking().Where(value =>
                value.AggregateType == type && value.AggregateId == id && value.AggregateVersion == version)
                .Select(value => value.Digest).ToListAsync()).ToHashSet(StringComparer.Ordinal);
            foreach (var digest in roots)
                if (!known.Contains(digest)) context.NCMetaAggregateBlobReferences.Add(new NCMetaAggregateBlobReference
                {
                    AggregateType = type, AggregateId = id, AggregateVersion = version, Digest = digest,
                });
            existing.ReferencePolicyVersion = MetaBlobReferences.PolicyVersion;
            return;
        }
        context.NCMetaAggregateRevisions.Add(new NCMetaAggregateRevision
        {
            AggregateType = type, AggregateId = id, AggregateVersion = version, FormatVersion = formatVersion,
            SessionEpoch = epoch, ActiveWorldTicks = ticks, Payload = payload, CreatedAt = createdAt,
            ReferencePolicyVersion = MetaBlobReferences.PolicyVersion,
        });
        foreach (var digest in roots)
            context.NCMetaAggregateBlobReferences.Add(new NCMetaAggregateBlobReference
            {
                AggregateType = type, AggregateId = id, AggregateVersion = version, Digest = digest,
            });
        if (version > NCMetaMaximumRetainedRevisions)
        {
            var cutoff = version - NCMetaMaximumRetainedRevisions;
            var expired = await context.NCMetaAggregateRevisions.Where(value => value.AggregateType == type &&
                value.AggregateId == id && value.AggregateVersion <= cutoff).OrderBy(value => value.AggregateVersion)
                .Select(value => value.AggregateVersion).Take(32).ToArrayAsync();
            if (expired.Length != 0)
                await context.NCMetaAggregateRevisions.Where(value => value.AggregateType == type && value.AggregateId == id &&
                    expired.Contains(value.AggregateVersion)).ExecuteDeleteAsync();
        }
        // Both the immutable revision and every blob FK are committed by the
        // caller's aggregate/receipt transaction. A missing byte-store row can
        // never become a successfully acknowledged reference.
    }

    private static string[] ReadNCMetaCheckpointBlobRoots(NCRoundSnapshotSectionWrite section)
    {
        if (section.SectionType != MetaCheckpointSection) return [];
        if (section.OwnerType != "world" || section.OwnerId != "city" || section.FormatVersion is not (1 or 2) ||
            Encoding.UTF8.GetByteCount(section.Payload) > 4 * 1024 * 1024 ||
            NCMetaJournalIntegrity.HashPayload(section.Payload) != section.Checksum)
            throw new InvalidOperationException("Invalid retained META checkpoint chunk.");
        var records = JsonSerializer.Deserialize<NCAggregateSnapshotData[]>(section.Payload,
            new JsonSerializerOptions { MaxDepth = 32 });
        if (records == null || records.Length != section.EntityCount || records.Length > 131072)
            throw new InvalidOperationException("Invalid retained META checkpoint records.");
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (record == null || record.AggregateType is not { Length: > 0 and <= 64 } ||
                !record.AggregateType.StartsWith("meta.", StringComparison.Ordinal) || record.FormatVersion < 1 ||
                !record.SessionEpoch.IsValid || !record.ActiveWorldTime.IsValid || record.Payload == null ||
                record.AggregateVersion < 1 || record.AggregateId is not { Length: > 0 and <= 128 })
                throw new InvalidOperationException("Invalid retained META checkpoint identity.");
            roots.UnionWith(MetaBlobReferences.Extract(record.AggregateType, record.Payload));
        }
        return roots.Order(StringComparer.Ordinal).ToArray();
    }

    private static void TrackNCMetaCheckpointBlobReferences(ServerDbContext context, Guid snapshotId,
        IReadOnlyList<NCRoundSnapshotSectionWrite> sections)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in sections) roots.UnionWith(ReadNCMetaCheckpointBlobRoots(section));
        foreach (var digest in roots)
            context.NCMetaCheckpointBlobReferences.Add(new NCMetaCheckpointBlobReference { SnapshotId = snapshotId, Digest = digest });
        context.NCMetaCheckpointBlobBackfills.Add(new NCMetaCheckpointBlobBackfill
            { SnapshotId = snapshotId, Complete = true, PolicyVersion = MetaBlobReferences.PolicyVersion });
    }

    public async Task<NCAggregateSnapshotData?> GetNCMetaAggregateRevisionAsync(string aggregateType, string aggregateId, long version)
    {
        ValidateIdentityPart(aggregateType, nameof(aggregateType), 64);
        ValidateIdentityPart(aggregateId, nameof(aggregateId), 128);
        if (!MetaBlobReferences.IsTracked(aggregateType) || version < 1) return null;
        await using var db = await GetDb();
        var revision = await db.DbContext.NCMetaAggregateRevisions.AsNoTracking().SingleOrDefaultAsync(value =>
            value.AggregateType == aggregateType && value.AggregateId == aggregateId && value.AggregateVersion == version);
        return revision == null ? null : new(revision.AggregateType, revision.AggregateId, revision.AggregateVersion,
            revision.FormatVersion, new NCSessionEpoch(revision.SessionEpoch), new NCActiveWorldTime(revision.ActiveWorldTicks),
            revision.Payload, revision.CreatedAt);
    }

    /// <summary>One bounded DB work unit. No blob deletion is permitted before legacy roots are completely backfilled.</summary>
    public async Task<NCMetaBlobMaintenanceResult> MaintainNCMetaBlobsAsync()
    {
        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var context = db.DbContext;
        var storage = await LockNCMetaBlobStorageAsync(context);
        if (!await BackfillNCMetaBlobStorageAsync(context, storage))
        {
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
            return new(false, 0, 0);
        }
        var state = await context.NCMetaBlobMaintenanceState.SingleOrDefaultAsync(value => value.Id == 1);
        if (state == null)
        {
            state = new NCMetaBlobMaintenance { Id = 1 };
            context.NCMetaBlobMaintenanceState.Add(state);
        }
        if (state.PolicyVersion > MetaBlobReferences.PolicyVersion)
            throw new InvalidOperationException("A newer META reference policy owns storage maintenance.");
        if (state.PolicyVersion != MetaBlobReferences.PolicyVersion)
        {
            state.PolicyVersion = MetaBlobReferences.PolicyVersion;
            state.AggregateTypeCursor = string.Empty;
            state.AggregateIdCursor = string.Empty;
            state.AggregatesComplete = false;
        }
        if (!state.AggregatesComplete)
        {
            var typeCursor = state.AggregateTypeCursor;
            var idCursor = state.AggregateIdCursor;
            var page = await context.NCAggregateSnapshots.AsNoTracking().Where(value => value.AggregateType.StartsWith("meta.") &&
                (value.AggregateType.CompareTo(typeCursor) > 0 || value.AggregateType == typeCursor && value.AggregateId.CompareTo(idCursor) > 0))
                .OrderBy(value => value.AggregateType).ThenBy(value => value.AggregateId).Take(8).ToListAsync();
            foreach (var snapshot in page)
                await TrackNCMetaBlobReferencesAsync(context, snapshot.AggregateType, snapshot.AggregateId, snapshot.AggregateVersion,
                    snapshot.FormatVersion, snapshot.SessionEpoch, snapshot.ActiveWorldTicks, snapshot.Payload, snapshot.CreatedAt, true);
            if (page.Count != 0)
            {
                state.AggregateTypeCursor = page[^1].AggregateType;
                state.AggregateIdCursor = page[^1].AggregateId;
            }
            state.AggregatesComplete = page.Count < 8;
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
            return new(false, 0, 0);
        }

        // A policy upgrade must cover retained historical JSON too. Its roots can differ
        // from the latest aggregate; collecting between these pages would lose history.
        if (await context.NCMetaAggregateRevisions.AnyAsync(value => value.ReferencePolicyVersion > MetaBlobReferences.PolicyVersion))
            throw new InvalidOperationException("Cannot collect blobs under an older reference policy.");
        var historical = await context.NCMetaAggregateRevisions.Where(value => value.ReferencePolicyVersion < MetaBlobReferences.PolicyVersion)
            .OrderBy(value => value.ReferencePolicyVersion).ThenBy(value => value.AggregateType).ThenBy(value => value.AggregateId).ThenBy(value => value.AggregateVersion)
            .Take(8).ToListAsync();
        if (historical.Count != 0)
        {
            foreach (var revision in historical)
            {
                await TrackNCMetaBlobReferencesAsync(context, revision.AggregateType, revision.AggregateId, revision.AggregateVersion,
                    revision.FormatVersion, revision.SessionEpoch, revision.ActiveWorldTicks, revision.Payload, revision.CreatedAt, true);
                revision.ReferencePolicyVersion = MetaBlobReferences.PolicyVersion;
            }
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
            return new(false, 0, 0);
        }

        var checkpoint = await context.NCRoundSnapshots.AsNoTracking().Where(value => value.Status == NCRoundSnapshotStatus.Committed &&
                !context.NCMetaCheckpointBlobBackfills.Any(backfill => backfill.SnapshotId == value.SnapshotId && backfill.Complete &&
                    backfill.PolicyVersion == MetaBlobReferences.PolicyVersion))
            .OrderBy(value => value.CommittedAt).FirstOrDefaultAsync();
        if (checkpoint != null)
        {
            await BackfillNCMetaCheckpointBlobsAsync(context, checkpoint);
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
            return new(false, 0, 0);
        }

        var revisionCutoff = DateTime.UtcNow.AddDays(-NCMetaRevisionRetentionDays);
        var oldRevisions = await context.NCMetaAggregateRevisions.Where(value =>
            (value.CreatedAt < revisionCutoff || context.NCAggregateSnapshots.Any(current => current.AggregateType == value.AggregateType &&
                current.AggregateId == value.AggregateId && current.AggregateVersion - NCMetaMaximumRetainedRevisions >= value.AggregateVersion)) &&
            !context.NCAggregateSnapshots.Any(current => current.AggregateType == value.AggregateType &&
                current.AggregateId == value.AggregateId && current.AggregateVersion == value.AggregateVersion))
            .OrderBy(value => value.CreatedAt).Take(32).ToListAsync();
        context.NCMetaAggregateRevisions.RemoveRange(oldRevisions);
        await context.SaveChangesAsync();
        var orphanCutoff = DateTime.UtcNow.AddHours(-NCMetaOrphanGraceHours);
        var admissionCutoff = DateTime.UtcNow.Ticks;
        var orphans = await context.NCMetaBlobs.Where(blob => blob.CreatedAt < orphanCutoff && blob.AdmittedUntilUtcTicks < admissionCutoff &&
                !context.NCMetaAggregateBlobReferences.Any(reference => reference.Digest == blob.Digest) &&
                !context.NCMetaCheckpointBlobReferences.Any(reference => reference.Digest == blob.Digest))
            .OrderBy(blob => blob.CreatedAt).ThenBy(blob => blob.Digest).Select(blob => new { blob.Digest, blob.ByteCount }).Take(32).ToArrayAsync();
        var orphanDigests = orphans.Select(blob => blob.Digest).ToArray();
        var removed = orphans.Length == 0 ? 0 : await context.NCMetaBlobs.Where(blob => orphanDigests.Contains(blob.Digest) &&
            blob.AdmittedUntilUtcTicks < admissionCutoff &&
            !context.NCMetaAggregateBlobReferences.Any(reference => reference.Digest == blob.Digest) &&
            !context.NCMetaCheckpointBlobReferences.Any(reference => reference.Digest == blob.Digest)).ExecuteDeleteAsync();
        if (removed != orphans.Length) throw new InvalidOperationException("META blob allocation changed during collection.");
        storage.AllocatedCount = checked(storage.AllocatedCount - removed);
        storage.AllocatedBytes = checked(storage.AllocatedBytes - orphans.Sum(blob => (long) blob.ByteCount));
        if (storage.AllocatedCount < 0 || storage.AllocatedBytes < 0) throw new InvalidOperationException("Invalid META blob allocation counters.");
        await context.SaveChangesAsync();
        await transaction.CommitAsync();
        return new(true, oldRevisions.Count, removed);
    }

    private static async Task BackfillNCMetaCheckpointBlobsAsync(ServerDbContext context, NCRoundSnapshot checkpoint)
    {
        var progress = await context.NCMetaCheckpointBlobBackfills.SingleOrDefaultAsync(value => value.SnapshotId == checkpoint.SnapshotId);
        if (progress == null)
        {
            // Validate the complete descriptor inventory before admitting any legacy root set.
            var inventory = await context.NCRoundSnapshotSections.AsNoTracking().Where(value => value.SnapshotId == checkpoint.SnapshotId)
                .Select(value => new NCRoundSnapshotSectionWrite(value.SectionType, value.OwnerType, value.OwnerId,
                    value.ChunkIndex, value.FormatVersion, value.EntityCount, "", value.Checksum))
                .Take(MaximumRoundSnapshotSections + 1).ToListAsync();
            if (inventory.Count != checkpoint.SectionCount || inventory.Count > MaximumRoundSnapshotSections ||
                ComputeSnapshotChecksum(inventory) != checkpoint.Checksum)
                throw new InvalidOperationException("Cannot collect blobs while a retained world checkpoint inventory is corrupt.");
            progress = new NCMetaCheckpointBlobBackfill { SnapshotId = checkpoint.SnapshotId, PolicyVersion = MetaBlobReferences.PolicyVersion };
            context.NCMetaCheckpointBlobBackfills.Add(progress);
        }
        if (progress.PolicyVersion > MetaBlobReferences.PolicyVersion)
            throw new InvalidOperationException("A newer META reference policy owns this checkpoint.");
        if (progress.PolicyVersion != MetaBlobReferences.PolicyVersion)
        {
            progress.PolicyVersion = MetaBlobReferences.PolicyVersion;
            progress.NextChunk = 0;
            progress.DigestCursor = string.Empty;
            progress.Complete = false;
        }
        var next = progress.NextChunk;
        var section = await context.NCRoundSnapshotSections.AsNoTracking().Where(value => value.SnapshotId == checkpoint.SnapshotId &&
                value.SectionType == MetaCheckpointSection && value.ChunkIndex >= next)
            .OrderBy(value => value.ChunkIndex).FirstOrDefaultAsync();
        if (section == null) { progress.Complete = true; return; }
        if (section.ChunkIndex != next) throw new InvalidOperationException("Missing META checkpoint chunk during blob backfill.");
        var roots = ReadNCMetaCheckpointBlobRoots(new(section.SectionType, section.OwnerType, section.OwnerId,
            section.ChunkIndex, section.FormatVersion, section.EntityCount, section.Payload, section.Checksum));
        var page = roots.Where(digest => string.CompareOrdinal(digest, progress.DigestCursor) > 0).Take(128).ToArray();
        var existing = (await context.NCMetaCheckpointBlobReferences.AsNoTracking()
            .Where(value => value.SnapshotId == checkpoint.SnapshotId && page.Contains(value.Digest))
            .Select(value => value.Digest).ToListAsync()).ToHashSet(StringComparer.Ordinal);
        foreach (var digest in page)
            if (!existing.Contains(digest))
                context.NCMetaCheckpointBlobReferences.Add(new NCMetaCheckpointBlobReference { SnapshotId = checkpoint.SnapshotId, Digest = digest });
        if (page.Length == 128) progress.DigestCursor = page[^1];
        else { progress.NextChunk = checked(next + 1); progress.DigestCursor = string.Empty; }
    }
}
