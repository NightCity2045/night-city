// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public enum NCRoundSnapshotCommitStatus : byte
{
    Committed,
    Duplicate,
    ClockRejected,
}

public sealed record NCRoundSnapshotSectionWrite(
    string SectionType,
    string OwnerType,
    string OwnerId,
    int ChunkIndex,
    int FormatVersion,
    int EntityCount,
    string Payload,
    string Checksum);

public sealed record NCRoundSnapshotCommitRequest(
    Guid SnapshotId,
    Guid SessionEpoch,
    long ActiveWorldTicks,
    int SchemaVersion,
    string MapId,
    int MapRevision,
    string MapHash,
    string Checksum,
    DateTime CapturedAt,
    IReadOnlyList<NCRoundSnapshotSectionWrite> Sections,
    IReadOnlyList<int> DeletedCharacterIds,
    int RetainCommittedSnapshots = 3);

public sealed record NCRoundSnapshotCommitResult(
    NCRoundSnapshotCommitStatus Status,
    Guid SnapshotId,
    string? Error);

public sealed record NCRoundSnapshotManifestData(
    Guid SnapshotId,
    Guid SessionEpoch,
    long ActiveWorldTicks,
    int SchemaVersion,
    string MapId,
    int MapRevision,
    string MapHash,
    int SectionCount,
    long PayloadBytes,
    string Checksum,
    DateTime CapturedAt,
    DateTime CommittedAt);

public sealed record NCRoundSnapshotSectionData(
    Guid SnapshotId,
    string SectionType,
    string OwnerType,
    string OwnerId,
    int ChunkIndex,
    int FormatVersion,
    int EntityCount,
    string Payload,
    string Checksum);

public partial interface IServerDbManager
{
    Task<NCRoundSnapshotCommitResult> CommitNCRoundSnapshotAsync(NCRoundSnapshotCommitRequest request);
    Task<NCRoundSnapshotManifestData?> GetLatestCommittedNCRoundSnapshotAsync();
    Task<IReadOnlyList<NCRoundSnapshotSectionData>> GetLatestNCRoundSnapshotSectionsAsync(
        string sectionType,
        string ownerType,
        string ownerId);
}

public sealed partial class ServerDbManager
{
    public Task<NCRoundSnapshotCommitResult> CommitNCRoundSnapshotAsync(NCRoundSnapshotCommitRequest request)
    {
        DbWriteOpsMetric.Inc();
        return RunNCPersistentDbCommand(true, NCPersistenceWriteClass.CoalescedCheckpoint,
            () => _db.CommitNCRoundSnapshotAsync(request));
    }

    public Task<NCRoundSnapshotManifestData?> GetLatestCommittedNCRoundSnapshotAsync()
    {
        DbReadOpsMetric.Inc();
        return RunNCPersistentDbCommand(false, null, () => _db.GetLatestCommittedNCRoundSnapshotAsync());
    }

    public Task<IReadOnlyList<NCRoundSnapshotSectionData>> GetLatestNCRoundSnapshotSectionsAsync(
        string sectionType,
        string ownerType,
        string ownerId)
    {
        DbReadOpsMetric.Inc();
        return RunNCPersistentDbCommand(false, null,
            () => _db.GetLatestNCRoundSnapshotSectionsAsync(sectionType, ownerType, ownerId));
    }
}

public abstract partial class ServerDbBase
{
    private const int MaximumRoundSnapshotSections = 16_384;
    private const int MaximumRoundSnapshotPayloadBytes = 256 * 1024 * 1024;

    public async Task<NCRoundSnapshotCommitResult> CommitNCRoundSnapshotAsync(
        NCRoundSnapshotCommitRequest request)
    {
        ValidateRoundSnapshotRequest(request);

        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var existing = await db.DbContext.NCRoundSnapshots
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.SnapshotId == request.SnapshotId ||
                                           value.SessionEpoch == request.SessionEpoch);
        if (existing != null)
        {
            var status = existing.Status == NCRoundSnapshotStatus.Committed &&
                         existing.SessionEpoch == request.SessionEpoch &&
                         existing.Checksum == request.Checksum
                ? NCRoundSnapshotCommitStatus.Duplicate
                : throw new InvalidOperationException(
                    $"Snapshot ID {request.SnapshotId} or epoch {request.SessionEpoch} is already in use.");
            return new NCRoundSnapshotCommitResult(status, existing.SnapshotId, existing.Error);
        }

        var clock = await db.DbContext.NCActiveWorldClocks
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == 1);
        if (clock == null || !clock.RoundActive || clock.SessionEpoch != request.SessionEpoch ||
            request.ActiveWorldTicks < clock.ActiveWorldTicks)
        {
            return new NCRoundSnapshotCommitResult(
                NCRoundSnapshotCommitStatus.ClockRejected,
                request.SnapshotId,
                "The snapshot does not belong to the active world epoch.");
        }

        var payloadBytes = request.Sections.Sum(value => (long) Encoding.UTF8.GetByteCount(value.Payload));
        var now = DateTime.UtcNow;
        var manifest = new NCRoundSnapshot
        {
            SnapshotId = request.SnapshotId,
            SessionEpoch = request.SessionEpoch,
            ActiveWorldTicks = request.ActiveWorldTicks,
            SchemaVersion = request.SchemaVersion,
            MapId = request.MapId,
            MapRevision = request.MapRevision,
            MapHash = request.MapHash,
            Status = NCRoundSnapshotStatus.Staging,
            SectionCount = request.Sections.Count,
            PayloadBytes = payloadBytes,
            Checksum = request.Checksum,
            CapturedAt = request.CapturedAt,
        };
        db.DbContext.NCRoundSnapshots.Add(manifest);
        foreach (var section in request.Sections)
        {
            db.DbContext.NCRoundSnapshotSections.Add(new NCRoundSnapshotSection
            {
                SnapshotId = request.SnapshotId,
                SectionType = section.SectionType,
                OwnerType = section.OwnerType,
                OwnerId = section.OwnerId,
                ChunkIndex = section.ChunkIndex,
                FormatVersion = section.FormatVersion,
                EntityCount = section.EntityCount,
                Payload = section.Payload,
                Checksum = section.Checksum,
            });
        }

        await db.DbContext.SaveChangesAsync();

        // Character death is part of publishing the world state. Keeping it in this transaction prevents
        // a committed inventory/body snapshot from surviving after its owning character was deleted.
        await DeleteCharactersAsync(db.DbContext, request.DeletedCharacterIds);

        manifest.Status = NCRoundSnapshotStatus.Committed;
        manifest.CommittedAt = now;
        await db.DbContext.SaveChangesAsync();

        var obsolete = await db.DbContext.NCRoundSnapshots
            .Where(value => value.Status == NCRoundSnapshotStatus.Committed)
            .OrderByDescending(value => value.CommittedAt)
            .Skip(request.RetainCommittedSnapshots)
            .ToListAsync();
        if (obsolete.Count != 0)
        {
            db.DbContext.NCRoundSnapshots.RemoveRange(obsolete);
            await db.DbContext.SaveChangesAsync();
        }

        await transaction.CommitAsync();
        return new NCRoundSnapshotCommitResult(NCRoundSnapshotCommitStatus.Committed, request.SnapshotId, null);
    }

    public async Task<NCRoundSnapshotManifestData?> GetLatestCommittedNCRoundSnapshotAsync()
    {
        await using var db = await GetDb();
        var value = await db.DbContext.NCRoundSnapshots
            .AsNoTracking()
            .Where(snapshot => snapshot.Status == NCRoundSnapshotStatus.Committed)
            .OrderByDescending(snapshot => snapshot.CommittedAt)
            .FirstOrDefaultAsync();
        return value?.CommittedAt is { } committedAt
            ? new NCRoundSnapshotManifestData(
                value.SnapshotId,
                value.SessionEpoch,
                value.ActiveWorldTicks,
                value.SchemaVersion,
                value.MapId,
                value.MapRevision,
                value.MapHash,
                value.SectionCount,
                value.PayloadBytes,
                value.Checksum,
                value.CapturedAt,
                committedAt)
            : null;
    }

    public async Task<IReadOnlyList<NCRoundSnapshotSectionData>> GetLatestNCRoundSnapshotSectionsAsync(
        string sectionType,
        string ownerType,
        string ownerId)
    {
        ValidateSectionIdentity(sectionType, ownerType, ownerId);
        await using var db = await GetDb();
        var latest = await db.DbContext.NCRoundSnapshots
            .AsNoTracking()
            .Where(value => value.Status == NCRoundSnapshotStatus.Committed)
            .OrderByDescending(value => value.CommittedAt)
            .Select(value => (Guid?) value.SnapshotId)
            .FirstOrDefaultAsync();
        if (latest == null)
            return [];

        return await db.DbContext.NCRoundSnapshotSections
            .AsNoTracking()
            .Where(value => value.SnapshotId == latest && value.SectionType == sectionType &&
                            value.OwnerType == ownerType && value.OwnerId == ownerId)
            .OrderBy(value => value.ChunkIndex)
            .Select(value => new NCRoundSnapshotSectionData(
                value.SnapshotId,
                value.SectionType,
                value.OwnerType,
                value.OwnerId,
                value.ChunkIndex,
                value.FormatVersion,
                value.EntityCount,
                value.Payload,
                value.Checksum))
            .ToListAsync();
    }

    private static void ValidateRoundSnapshotRequest(NCRoundSnapshotCommitRequest request)
    {
        if (request.SnapshotId == Guid.Empty || request.SessionEpoch == Guid.Empty ||
            request.ActiveWorldTicks < 0 || request.SchemaVersion <= 0 ||
            request.MapId.Length > 128 || request.MapHash.Length > 64 ||
            request.Checksum.Length != 64 || request.Sections.Count > MaximumRoundSnapshotSections ||
            request.DeletedCharacterIds.Count > 1024 ||
            request.RetainCommittedSnapshots is < 1 or > 32)
        {
            throw new ArgumentException("The round snapshot manifest is invalid.", nameof(request));
        }


        if (request.DeletedCharacterIds.Any(value => value <= 0) ||
            request.DeletedCharacterIds.Distinct().Count() != request.DeletedCharacterIds.Count)
        {
            throw new ArgumentException("The deleted character list is invalid.", nameof(request));
        }

        long payloadBytes = 0;
        var keys = new HashSet<(string, string, string, int)>();
        foreach (var section in request.Sections)
        {
            ValidateSectionIdentity(section.SectionType, section.OwnerType, section.OwnerId);
            if (section.ChunkIndex < 0 || section.FormatVersion <= 0 || section.EntityCount < 0 ||
                section.Checksum.Length != 64 ||
                !string.Equals(Hash(section.Payload), section.Checksum, StringComparison.Ordinal) ||
                !keys.Add((section.SectionType, section.OwnerType, section.OwnerId, section.ChunkIndex)))
            {
                throw new ArgumentException("A round snapshot section is invalid.", nameof(request));
            }
            payloadBytes = checked(payloadBytes + Encoding.UTF8.GetByteCount(section.Payload));
        }

        if (payloadBytes > MaximumRoundSnapshotPayloadBytes ||
            !string.Equals(ComputeSnapshotChecksum(request.Sections), request.Checksum, StringComparison.Ordinal))
        {
            throw new ArgumentException("The round snapshot payload is invalid.", nameof(request));
        }
    }

    private static async Task DeleteCharactersAsync(ServerDbContext context, IReadOnlyList<int> characterIds)
    {
        if (characterIds.Count == 0)
            return;

        var deletedIds = characterIds.ToHashSet();
        var profiles = await context.Profile
            .Where(value => deletedIds.Contains(value.Id))
            .Include(value => value.Preference)
            .ThenInclude(value => value.Profiles)
            .AsSplitQuery()
            .ToListAsync();

        foreach (var preferenceGroup in profiles.GroupBy(value => value.Preference))
        {
            var preference = preferenceGroup.Key;
            var survivors = preference.Profiles
                .Where(value => !deletedIds.Contains(value.Id))
                .OrderBy(value => value.Slot)
                .ToArray();
            if (survivors.Length == 0)
            {
                // The normal preference loader recreates a default profile when the account next loads.
                context.Preference.Remove(preference);
                continue;
            }

            if (preferenceGroup.Any(value => value.Slot == preference.SelectedCharacterSlot))
                preference.SelectedCharacterSlot = survivors[0].Slot;
            context.Profile.RemoveRange(preferenceGroup);
        }
    }

    private static void ValidateSectionIdentity(string sectionType, string ownerType, string ownerId)
    {
        if (string.IsNullOrWhiteSpace(sectionType) || sectionType.Length > 64 ||
            string.IsNullOrWhiteSpace(ownerType) || ownerType.Length > 64 ||
            string.IsNullOrWhiteSpace(ownerId) || ownerId.Length > 128)
        {
            throw new ArgumentException("The round snapshot section identity is invalid.");
        }
    }

    private static string ComputeSnapshotChecksum(IEnumerable<NCRoundSnapshotSectionWrite> sections)
    {
        var canonical = string.Join('\n', sections
            .OrderBy(value => value.SectionType, StringComparer.Ordinal)
            .ThenBy(value => value.OwnerType, StringComparer.Ordinal)
            .ThenBy(value => value.OwnerId, StringComparer.Ordinal)
            .ThenBy(value => value.ChunkIndex)
            .Select(value => $"{value.SectionType}\u001f{value.OwnerType}\u001f{value.OwnerId}\u001f{value.ChunkIndex}\u001f{value.FormatVersion}\u001f{value.EntityCount}\u001f{value.Checksum}"));
        return Hash(canonical);
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
