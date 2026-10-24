// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Shared._NC.Persistence.Time;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

/// <summary>The immutable deletion marker survives retirement and prevents reuse of a deleted project ID.</summary>
public sealed record NCMetaProjectTombstoneData(Guid Id, string Owner, bool Retired = false);

public partial interface IServerDbManager
{
    Task<int> RetireNCMetaProjectsAsync(NCSessionEpoch epoch, NCActiveWorldTime time);
}

public sealed partial class ServerDbManager
{
    public Task<int> RetireNCMetaProjectsAsync(NCSessionEpoch epoch, NCActiveWorldTime time)
    {
        DbWriteOpsMetric.Inc();
        return RunNCPersistentDbCommand(true, NCPersistenceWriteClass.DurableOutcome,
            () => _db.RetireNCMetaProjectsAsync(epoch, time));
    }
}

public abstract partial class ServerDbBase
{
    public const string NCMetaProjectTombstoneType = "meta.project-tombstone";
    private const string NCMetaRetireProjectCommand = "meta.project.retire";
    public const int NCMetaMaximumProjectRetirements = 8;
    private static readonly JsonSerializerOptions NCMetaProjectJson = new() { MaxDepth = 24 };

    /// <summary>
    /// Retires at most eight deleted authoring projects through the normal journal and receipt writer.
    /// Installations, publications and retained world checkpoints own their own immutable image roots.
    /// </summary>
    public async Task<int> RetireNCMetaProjectsAsync(NCSessionEpoch epoch, NCActiveWorldTime time)
    {
        if (!epoch.IsValid || !time.IsValid) throw new ArgumentException("A captured active-world timestamp is required.");
        var cutoff = DateTime.UtcNow.AddDays(-NCMetaRevisionRetentionDays);
        List<NCAggregateSnapshot> candidates;
        await using (var db = await GetDb())
            candidates = await db.DbContext.NCAggregateSnapshots.AsNoTracking()
                .Where(value => value.AggregateType == NCMetaProjectTombstoneType && value.AggregateVersion == 1 &&
                    value.CreatedAt < cutoff).OrderBy(value => value.CreatedAt).ThenBy(value => value.AggregateId)
                .Take(NCMetaMaximumProjectRetirements).ToListAsync();
        var retired = 0;
        foreach (var marker in candidates)
        {
            var tombstone = ReadNCMetaProjectTombstone(marker.Payload, marker.AggregateId);
            if (marker.FormatVersion != 1 || tombstone == null || tombstone.Retired)
                throw new InvalidOperationException("Invalid META project deletion marker.");
            var project = await GetNCAggregateSnapshotAsync("meta.project", marker.AggregateId);
            if (project == null || project.FormatVersion != 1 || project.AggregateVersion == long.MaxValue ||
                !TryReadNCMetaProjectIdentity(project.Payload, marker.AggregateId, out var owner, out var revision) ||
                owner != tombstone.Owner || revision != project.AggregateVersion)
                throw new InvalidOperationException("Deleted META project does not match its deletion marker.");
            var payload = JsonSerializer.Serialize(new
            {
                tombstone.Id, tombstone.Owner, Name = "", Revision = checked(revision + 1),
                Files = Array.Empty<object>(), Releases = Array.Empty<object>(),
            });
            var command = Guid.NewGuid();
            var result = await ApplyNCPersistentBatchMutationAsync(new(command, NCMetaRetireProjectCommand,
                "meta.owner", owner,
                [new(Guid.NewGuid(), "meta.project.retired", "meta.project", marker.AggregateId, revision, 1, payload, "{}"),
                    new(Guid.NewGuid(), "meta.project-tombstone.retired", NCMetaProjectTombstoneType, marker.AggregateId,
                        1, 1, JsonSerializer.Serialize(tombstone with { Retired = true }), "{}")],
                "{}", command, null, epoch, time, [],
                RequestFingerprint: NCMetaJournalIntegrity.HashPayload(marker.AggregateId + ":" +
                    revision.ToString(CultureInfo.InvariantCulture))));
            if (result.Status == NCPersistentMutationStatus.ClockRejected) break;
            if (result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted) retired++;
        }
        return retired;
    }

    /// <summary>Runs inside the same serializable transaction as every project mutation.</summary>
    private static async Task<bool> CheckNCMetaProjectMutationAsync(ServerDbContext context, string command,
        IReadOnlyList<NCPersistentAggregateWrite> writes)
    {
        var relevant = writes.Where(value => value.AggregateType is "meta.project" or NCMetaProjectTombstoneType).ToArray();
        if (relevant.Length == 0) return true;
        var ids = relevant.Select(value => value.AggregateId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        // Project edits and deletion take the same row lock. A writer cannot pass an absent-marker check
        // and then commit after another transaction has deleted the project.
        foreach (var id in ids)
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE nc_aggregate_snapshot SET aggregate_version = aggregate_version WHERE aggregate_type = 'meta.project' AND aggregate_id = {id}");
        var markers = await context.NCAggregateSnapshots.AsNoTracking()
            .Where(value => value.AggregateType == NCMetaProjectTombstoneType && ids.Contains(value.AggregateId)).ToListAsync();
        var projects = await context.NCAggregateSnapshots.AsNoTracking()
            .Where(value => value.AggregateType == "meta.project" && ids.Contains(value.AggregateId)).ToListAsync();
        foreach (var id in ids)
        {
            var stored = markers.SingleOrDefault(value => value.AggregateId == id);
            var projectWrite = relevant.SingleOrDefault(value => value.AggregateType == "meta.project" && value.AggregateId == id);
            var markerWrite = relevant.SingleOrDefault(value => value.AggregateType == NCMetaProjectTombstoneType && value.AggregateId == id);
            if (stored == null)
            {
                if (markerWrite == null) continue;
                // Native deletion must attest the exact current project in the same batch.
                var marker = ReadNCMetaProjectTombstone(markerWrite.SnapshotPayload, id);
                var currentProject = projects.SingleOrDefault(value => value.AggregateId == id);
                if (command != "meta.project.manage" || markerWrite.ExpectedAggregateVersion != 0 || markerWrite.SnapshotFormatVersion != 1 ||
                    marker is not { Retired: false } || projectWrite == null || projectWrite.ExpectedAggregateVersion < 1 ||
                    projectWrite.SnapshotFormatVersion != 1 || currentProject == null ||
                    !TryReadNCMetaProjectIdentity(projectWrite.SnapshotPayload, id, out var owner, out var revision) ||
                    owner != marker.Owner || revision != projectWrite.ExpectedAggregateVersion + 1 ||
                    !NCMetaProjectContentMatches(currentProject.Payload, projectWrite.SnapshotPayload)) return false;
                continue;
            }
            // Once deleted, the only permitted project write is the exact bounded retirement pair.
            var previous = ReadNCMetaProjectTombstone(stored.Payload, id);
            var next = markerWrite == null ? null : ReadNCMetaProjectTombstone(markerWrite.SnapshotPayload, id);
            if (command != NCMetaRetireProjectCommand || writes.Count != 2 || stored.AggregateVersion != 1 || stored.FormatVersion != 1 ||
                stored.CreatedAt >= DateTime.UtcNow.AddDays(-NCMetaRevisionRetentionDays) || previous is not { Retired: false } ||
                next == null || next != (previous with { Retired = true }) || markerWrite!.ExpectedAggregateVersion != 1 ||
                markerWrite.SnapshotFormatVersion != 1 || projectWrite == null || projectWrite.ExpectedAggregateVersion < 1 ||
                projectWrite.SnapshotFormatVersion != 1 || !TryReadNCMetaProjectIdentity(projectWrite.SnapshotPayload, id, out var retiredOwner,
                    out var retiredRevision) || retiredOwner != previous.Owner || retiredRevision != projectWrite.ExpectedAggregateVersion + 1 ||
                !NCMetaProjectIsEmpty(projectWrite.SnapshotPayload)) return false;
            var retiringProject = projects.SingleOrDefault(value => value.AggregateId == id);
            if (retiringProject == null || !await HasNCMetaProjectAttestationAsync(context, stored) ||
                !await HasNCMetaProjectAttestationAsync(context, retiringProject)) return false;
        }
        return true;
    }

    private static NCMetaProjectTombstoneData? ReadNCMetaProjectTombstone(string payload, string id)
    {
        if (payload.Length > 1024) return null;
        try
        {
            var marker = JsonSerializer.Deserialize<NCMetaProjectTombstoneData>(payload, NCMetaProjectJson);
            return marker is { Owner.Length: > 0 and <= 128 } && marker.Id != Guid.Empty && marker.Id.ToString("D") == id ? marker : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool TryReadNCMetaProjectIdentity(string payload, string id, out string owner, out long revision)
    {
        owner = ""; revision = 0;
        if (Encoding.UTF8.GetByteCount(payload) > 2 * NCMetaMaximumBlobBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(payload, new() { MaxDepth = 24 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Id", out var projectId) ||
                !projectId.TryGetGuid(out var parsedId) || parsedId == Guid.Empty || parsedId.ToString("D") != id ||
                !root.TryGetProperty("Owner", out var ownerField) || ownerField.ValueKind != JsonValueKind.String ||
                ownerField.GetString() is not { Length: > 0 and <= 128 } parsedOwner ||
                !root.TryGetProperty("Revision", out var version) || !version.TryGetInt64(out revision) || revision < 1) return false;
            owner = parsedOwner;
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool NCMetaProjectIsEmpty(string payload)
    {
        using var document = JsonDocument.Parse(payload, new() { MaxDepth = 24 });
        return document.RootElement.TryGetProperty("Files", out var files) && files.ValueKind == JsonValueKind.Array && files.GetArrayLength() == 0 &&
            document.RootElement.TryGetProperty("Releases", out var releases) && releases.ValueKind == JsonValueKind.Array && releases.GetArrayLength() == 0;
    }

    private static bool NCMetaProjectContentMatches(string current, string next)
    {
        try
        {
            using var before = JsonDocument.Parse(current, new() { MaxDepth = 24 });
            using var after = JsonDocument.Parse(next, new() { MaxDepth = 24 });
            if (before.RootElement.ValueKind != JsonValueKind.Object || after.RootElement.ValueKind != JsonValueKind.Object) return false;
            var properties = before.RootElement.EnumerateObject().Where(value => value.Name != "Revision").ToArray();
            return after.RootElement.EnumerateObject().Count(value => value.Name != "Revision") == properties.Length &&
                properties.All(value => after.RootElement.TryGetProperty(value.Name, out var replacement) &&
                    JsonElement.DeepEquals(value.Value, replacement));
        }
        catch (JsonException) { return false; }
    }

    private static async Task<bool> HasNCMetaProjectAttestationAsync(ServerDbContext context, NCAggregateSnapshot snapshot)
    {
        var entry = await context.NCEventJournal.AsNoTracking().SingleOrDefaultAsync(value =>
            value.AggregateType == snapshot.AggregateType && value.AggregateId == snapshot.AggregateId &&
            value.AggregateVersion == snapshot.AggregateVersion);
        if (entry == null || entry.SessionEpoch != snapshot.SessionEpoch || entry.ActiveWorldTicks != snapshot.ActiveWorldTicks) return false;
        try
        {
            using var document = JsonDocument.Parse(entry.Payload, new() { MaxDepth = 32 });
            if (!document.RootElement.TryGetProperty(NCMetaJournalIntegrity.Field, out var field)) return false;
            var proof = field.Deserialize<NCMetaJournalIntegrity.Attestation>();
            var data = new NCAggregateSnapshotData(snapshot.AggregateType, snapshot.AggregateId, snapshot.AggregateVersion,
                snapshot.FormatVersion, new(snapshot.SessionEpoch), new(snapshot.ActiveWorldTicks), snapshot.Payload, snapshot.CreatedAt);
            return proof is { Version: 1 } && proof.FormatVersion == snapshot.FormatVersion &&
                proof.PayloadHash == NCMetaJournalIntegrity.HashPayload(snapshot.Payload) && proof.CurrentHash == NCMetaJournalIntegrity.HashSnapshot(data) &&
                (snapshot.AggregateVersion != 1 || proof.PreviousHash == NCMetaJournalIntegrity.OriginHash);
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
