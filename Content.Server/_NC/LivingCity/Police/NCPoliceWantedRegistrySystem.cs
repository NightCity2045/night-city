// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Text.Json;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Police;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.LivingCity.Police;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.Database._NC.Police;

namespace Content.Server._NC.LivingCity.Police;

public enum NCPoliceWantedMutationStatus : byte
{
    Applied,
    Duplicate,
    NotFound,
    VersionConflict,
    Rejected,
    StorageFailure,
}

public sealed record NCPoliceWantedRecord(
    NCPoliceWantedSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCPoliceWantedMutationResult(
    NCPoliceWantedMutationStatus Status,
    NCPoliceWantedRecord? Record,
    string? Error);

/// <summary>
/// Canonical wanted authority for both actor and character parties. Character records are projected into the legacy
/// police terminal only after this aggregate commits successfully.
/// </summary>
public sealed partial class NCPoliceWantedRegistrySystem : EntitySystem
{
    public const string AggregateType = "living-city.police-wanted";
    public const int SnapshotFormatVersion = 1;
    private const int PageSize = 500;
    private const int MaximumPages = 8;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPartyResolverSystem _parties = default!;
    [Dependency] private NCPoliceRecordsSystem _legacyRecords = default!;

    private readonly ConcurrentDictionary<NCPoliceWantedId, NCPoliceWantedRecord> _cache = [];
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public int CachedCount => _cache.Count;

    public static NCPoliceWantedId Identity(NCCityIncidentId incidentId, NCPartyRef subject)
    {
        if (!incidentId.IsValid || !subject.IsValid)
            return default;
        return new NCPoliceWantedId(NCPersistentIdentity.Command(
            "ncpd-wanted", "identity", $"{incidentId}.{subject}"));
    }

    public async Task<NCPoliceWantedRecord?> GetAsync(NCPoliceWantedId wantedId, bool forceReload = false)
    {
        if (!wantedId.IsValid)
            return null;
        if (!forceReload && _cache.TryGetValue(wantedId, out var cached))
            return cached;
        var stored = await _database.GetNCAggregateSnapshotAsync(AggregateType, wantedId.ToString());
        var record = Deserialize(stored, wantedId);
        return record == null ? null : CacheNewest(record);
    }

    public async Task<IReadOnlyList<NCPoliceWantedRecord>> GetPageAsync(
        int limit,
        NCPartyRef? subject = null,
        bool activeOnly = false)
    {
        if (limit is < 1 or > 200 || subject is { IsValid: false })
            return [];
        var result = new List<NCPoliceWantedRecord>(limit);
        string? before = null;
        for (var pageNumber = 0; pageNumber < MaximumPages && result.Count < limit; pageNumber++)
        {
            var requested = subject == null && !activeOnly ? limit : PageSize;
            var page = await _database.GetNCAggregateSnapshotPageDescendingAsync(AggregateType, before, requested);
            if (page.Count == 0)
                break;
            foreach (var stored in page)
            {
                var record = Deserialize(stored, null);
                if (record == null || subject is { } target && record.Snapshot.Subject != target ||
                    activeOnly && record.Snapshot.Status != NCPoliceWantedStatus.Active)
                {
                    continue;
                }
                result.Add(CacheNewest(record));
                if (result.Count >= limit)
                    break;
            }
            before = page[^1].AggregateId;
            if (page.Count < requested)
                break;
        }
        return result;
    }

    public async Task<NCPoliceWantedMutationResult> CreateAsync(
        NCPartyRef subject,
        NCCityIncidentId incidentId,
        Guid sourceWorldEventId,
        int risk,
        string reasonKey,
        NCPartyRef issuedBy,
        string auditSource)
    {
        reasonKey = reasonKey.Trim();
        auditSource = auditSource.Trim();
        if (!subject.IsValid || subject.Kind is not (NCPartyKind.Actor or NCPartyKind.Character) ||
            !incidentId.IsValid || sourceWorldEventId == Guid.Empty || risk is < 0 or > 1000 ||
            reasonKey.Length is < 1 or > 160 || !issuedBy.IsValid || auditSource.Length is < 1 or > 128 ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject("The wanted request is invalid.");
        }

        var subjectResolution = await _parties.ResolveAsync(subject, true);
        var issuerResolution = await _parties.ResolveAsync(issuedBy, true);
        if (!subjectResolution.IsResolved || !issuerResolution.IsResolved)
            return Reject(subjectResolution.Error ?? issuerResolution.Error ?? "A wanted party is unavailable.");

        var stable = $"{incidentId}.{subject}";
        var wantedId = Identity(incidentId, subject);
        var eventId = NCPersistentIdentity.Command("ncpd-wanted", "issued-event", stable);
        var snapshot = new NCPoliceWantedSnapshot(
            wantedId,
            subject,
            subjectResolution.DisplayName,
            incidentId,
            sourceWorldEventId,
            NCPoliceWantedStatus.Active,
            risk,
            reasonKey,
            issuedBy,
            issuerResolution.DisplayName,
            now,
            now,
            1,
            eventId);
        var receipt = new WantedReceipt(snapshot, 1, epoch, now);
        var result = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(
            NCPersistentIdentity.Command("ncpd-wanted", "issue", stable),
            "living-city.police-wanted.issue",
            "police-wanted",
            wantedId.ToString(),
            eventId,
            "living-city.police-wanted.issued",
            AggregateType,
            wantedId.ToString(),
            0,
            SnapshotFormatVersion,
            JsonSerializer.Serialize(snapshot, JsonOptions),
            JsonSerializer.Serialize(new WantedEvent(wantedId, subject, incidentId, NCPoliceWantedStatus.Active,
                auditSource), JsonOptions),
            JsonSerializer.Serialize(receipt, JsonOptions),
            NCPersistentIdentity.Correlation("ncpd-wanted", "issue", stable),
            sourceWorldEventId,
            epoch,
            now,
            []));
        var applied = Apply(result, receipt);
        if (applied.Status is NCPoliceWantedMutationStatus.Applied or NCPoliceWantedMutationStatus.Duplicate &&
            applied.Record != null)
        {
            await MirrorCharacterAsync(applied.Record.Snapshot, NCPoliceOperationalStatus.Wanted,
                $"Wanted for incident {incidentId}.");
            RaiseLocalEvent(new NCPoliceWantedPersistedEvent(applied.Record));
        }
        return applied;
    }

    public async Task<NCPoliceWantedMutationResult> SetStatusAsync(
        NCPoliceWantedId wantedId,
        NCPoliceWantedStatus status,
        NCPartyRef actor,
        string auditSource)
    {
        auditSource = auditSource.Trim();
        if (!wantedId.IsValid || status == NCPoliceWantedStatus.Active || !Enum.IsDefined(status) ||
            !actor.IsValid || auditSource.Length is < 1 or > 128 ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject("The wanted status request is invalid.");
        }

        var gate = Gate(wantedId);
        await gate.WaitAsync();
        try
        {
            var current = await GetAsync(wantedId, true);
            if (current == null)
                return new(NCPoliceWantedMutationStatus.NotFound, null, "The wanted record does not exist.");
            if (current.Snapshot.Status != NCPoliceWantedStatus.Active)
                return current.Snapshot.Status == status
                    ? new(NCPoliceWantedMutationStatus.Duplicate, current, null)
                    : Reject("A terminal wanted record cannot change status.");

            var stable = $"{wantedId}.{status}.{current.AggregateVersion}";
            var eventId = NCPersistentIdentity.Command("ncpd-wanted", "status-event", stable);
            var snapshot = current.Snapshot with
            {
                Status = status,
                UpdatedAt = now,
                DataVersion = current.Snapshot.DataVersion + 1,
                LastAppliedEventId = eventId,
            };
            var receipt = new WantedReceipt(snapshot, current.AggregateVersion + 1, epoch, now);
            var result = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(
                NCPersistentIdentity.Command("ncpd-wanted", "set-status", stable),
                "living-city.police-wanted.set-status",
                "police-wanted",
                wantedId.ToString(),
                eventId,
                "living-city.police-wanted.status-changed",
                AggregateType,
                wantedId.ToString(),
                current.AggregateVersion,
                SnapshotFormatVersion,
                JsonSerializer.Serialize(snapshot, JsonOptions),
                JsonSerializer.Serialize(new WantedEvent(wantedId, snapshot.Subject, snapshot.IncidentId, status,
                    auditSource), JsonOptions),
                JsonSerializer.Serialize(receipt, JsonOptions),
                NCPersistentIdentity.Correlation("ncpd-wanted", "set-status", stable),
                current.Snapshot.LastAppliedEventId,
                epoch,
                now,
                []));
            var applied = Apply(result, receipt);
            if (applied.Status is NCPoliceWantedMutationStatus.Applied or NCPoliceWantedMutationStatus.Duplicate &&
                applied.Record != null)
            {
                var legacyStatus = status == NCPoliceWantedStatus.Detained
                    ? NCPoliceOperationalStatus.Detained
                    : NCPoliceOperationalStatus.Released;
                await MirrorCharacterAsync(applied.Record.Snapshot, legacyStatus,
                    $"Wanted record {status.ToString().ToLowerInvariant()}.");
                RaiseLocalEvent(new NCPoliceWantedPersistedEvent(applied.Record));
            }
            return applied;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task MirrorCharacterAsync(
        NCPoliceWantedSnapshot snapshot,
        NCPoliceOperationalStatus status,
        string reason)
    {
        if (!snapshot.Subject.TryGetCharacter(out var characterId))
            return;
        var actorCharacter = snapshot.IssuedBy.TryGetCharacter(out var issuer) ? issuer : (Content.Shared._NC.Identity.NCCharacterId?) null;
        var record = await _legacyRecords.PersistStatusAsync(characterId, status, reason, actorCharacter,
            snapshot.IssuedByName);
        if (record != null)
            _legacyRecords.ApplyCommittedStatus(record);
    }

    private NCPoliceWantedMutationResult Apply(NCPersistentMutationResult result, WantedReceipt submitted)
    {
        if (result.Status == NCPersistentMutationStatus.Applied)
        {
            var record = CacheNewest(new NCPoliceWantedRecord(submitted.Snapshot, result.AggregateVersion,
                submitted.SessionEpoch, submitted.PersistedAt));
            return new(NCPoliceWantedMutationStatus.Applied, record, null);
        }
        if (result.Status == NCPersistentMutationStatus.DuplicateCompleted)
        {
            var receipt = DeserializeReceipt(result.ResultPayload);
            if (receipt != null)
            {
                var record = CacheNewest(new NCPoliceWantedRecord(receipt.Snapshot, receipt.AggregateVersion,
                    receipt.SessionEpoch, receipt.PersistedAt));
                return new(NCPoliceWantedMutationStatus.Duplicate, record, null);
            }
        }
        var status = result.Status is NCPersistentMutationStatus.VersionConflict or
            NCPersistentMutationStatus.DuplicateFailed
            ? NCPoliceWantedMutationStatus.VersionConflict
            : NCPoliceWantedMutationStatus.StorageFailure;
        return new(status, null, result.Error ?? "The wanted mutation was not committed.");
    }

    private static NCPoliceWantedRecord? Deserialize(NCAggregateSnapshotData? stored, NCPoliceWantedId? expected)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCPoliceWantedSnapshot>(stored.Payload, JsonOptions);
            return Valid(snapshot, expected)
                ? new NCPoliceWantedRecord(snapshot!, stored.AggregateVersion, stored.SessionEpoch,
                    stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static WantedReceipt? DeserializeReceipt(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;
        try
        {
            var receipt = JsonSerializer.Deserialize<WantedReceipt>(payload, JsonOptions);
            return receipt != null && receipt.AggregateVersion >= 1 && receipt.SessionEpoch.IsValid &&
                   receipt.PersistedAt.IsValid && Valid(receipt.Snapshot, receipt.Snapshot.WantedId)
                ? receipt
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Valid(NCPoliceWantedSnapshot? value, NCPoliceWantedId? expected) =>
        value != null && value.WantedId.IsValid && (expected == null || value.WantedId == expected) &&
        value.Subject.IsValid && value.Subject.Kind is NCPartyKind.Actor or NCPartyKind.Character &&
        value.SubjectName.Length is > 0 and <= 128 && value.SubjectName.Trim() == value.SubjectName &&
        value.IncidentId.IsValid && value.SourceWorldEventId != Guid.Empty && Enum.IsDefined(value.Status) &&
        value.Risk is >= 0 and <= 1000 && value.ReasonKey.Length is > 0 and <= 160 &&
        value.IssuedBy.IsValid && value.IssuedByName.Length is > 0 and <= 128 && value.IssuedAt.IsValid &&
        value.UpdatedAt >= value.IssuedAt && value.DataVersion >= 1 && value.LastAppliedEventId != Guid.Empty;

    private NCPoliceWantedRecord CacheNewest(NCPoliceWantedRecord candidate) =>
        _cache.AddOrUpdate(candidate.Snapshot.WantedId, candidate,
            (_, current) => current.AggregateVersion > candidate.AggregateVersion ? current : candidate);

    private SemaphoreSlim Gate(NCPoliceWantedId id) => _gates[(id.GetHashCode() & int.MaxValue) % _gates.Length];
    private static NCPoliceWantedMutationResult Reject(string error) =>
        new(NCPoliceWantedMutationStatus.Rejected, null, error);

    private sealed record WantedReceipt(NCPoliceWantedSnapshot Snapshot, long AggregateVersion,
        NCSessionEpoch SessionEpoch, NCActiveWorldTime PersistedAt);
    private sealed record WantedEvent(NCPoliceWantedId WantedId, NCPartyRef Subject, NCCityIncidentId IncidentId,
        NCPoliceWantedStatus Status, string AuditSource);
}

public sealed class NCPoliceWantedPersistedEvent(NCPoliceWantedRecord record) : EntityEventArgs
{
    public NCPoliceWantedRecord Record { get; } = record;
}
