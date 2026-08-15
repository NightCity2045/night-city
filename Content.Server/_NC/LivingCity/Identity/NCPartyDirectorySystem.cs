// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;

namespace Content.Server._NC.LivingCity.Identity;

/// <summary>
/// Persistent directory for organizations, businesses, city services and estates. Character and actor identities keep
/// their own authoritative aggregates and are resolved by <see cref="NCPartyResolverSystem"/>.
/// </summary>
public sealed partial class NCPartyDirectorySystem : EntitySystem
{
    public const string AggregateType = "living-city.party-directory";
    public const int SnapshotFormatVersion = 1;

    private const int MaximumDisplayNameLength = 128;
    private const int MaximumAuditSourceLength = 128;
    private const int MutationGateStripeCount = 64;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private ITaskManager _taskManager = default!;

    private readonly ConcurrentDictionary<NCPartyRef, NCPartyDirectoryRecord> _cache = [];
    private readonly SemaphoreSlim[] _mutationGates = CreateMutationGates();

    public int CachedPartyCount => _cache.Count;

    public bool TryGetCached(NCPartyRef party, out NCPartyDirectoryRecord record)
    {
        return _cache.TryGetValue(party, out record!);
    }

    public bool Unload(NCPartyRef party)
    {
        return _cache.TryRemove(party, out _);
    }

    public async Task<NCPartyDirectoryRecord?> GetAsync(NCPartyRef party, bool forceReload = false)
    {
        if (!IsDirectoryParty(party))
            return null;
        if (!forceReload && _cache.TryGetValue(party, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(AggregateType, PartyKey(party));
        var record = Deserialize(stored, party);
        return record == null ? null : CacheNewest(record);
    }

    public async Task<NCPartyDirectoryMutationResult> RegisterAsync(NCPartyDirectoryRegisterRequest request)
    {
        if (!TryValidateMetadata(request.CommandId, request.Party, request.CorrelationId, request.AuditSource,
                out var error) || string.IsNullOrWhiteSpace(request.DisplayName) ||
            request.DisplayName.Trim().Length > MaximumDisplayNameLength ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(request.Party,
                _clock.IsReady ? NCPartyDirectoryMutationStatus.Rejected :
                    NCPartyDirectoryMutationStatus.ClockUnavailable,
                error ?? "The party display name or active-world clock is invalid.");
        }

        var gate = GetMutationGate(request.Party);
        await gate.WaitAsync();
        try
        {
            var eventId = Guid.NewGuid();
            var snapshot = new NCPartyDirectorySnapshot(
                request.Party,
                request.DisplayName.Trim(),
                NCPartyLifecycle.Active,
                now,
                now,
                1,
                eventId);
            return await Persist(
                request.CommandId,
                "living-city.party.register",
                eventId,
                "living-city.party.registered",
                request.Party,
                0,
                snapshot,
                request.CorrelationId,
                request.CausationId,
                request.AuditSource,
                epoch,
                now);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to register persistent party {request.Party}: {exception}");
            return Reject(request.Party, NCPartyDirectoryMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NCPartyDirectoryMutationResult> SetLifecycleAsync(NCPartyDirectoryLifecycleRequest request)
    {
        if (!TryValidateMetadata(request.CommandId, request.Party, request.CorrelationId, request.AuditSource,
                out var error) || request.ExpectedVersion < 1 || !Enum.IsDefined(request.Lifecycle) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(request.Party,
                _clock.IsReady ? NCPartyDirectoryMutationStatus.Rejected :
                    NCPartyDirectoryMutationStatus.ClockUnavailable,
                error ?? "The lifecycle mutation metadata or active-world clock is invalid.");
        }

        var gate = GetMutationGate(request.Party);
        await gate.WaitAsync();
        try
        {
            var current = await GetAsync(request.Party);
            if (current == null)
                return Reject(request.Party, NCPartyDirectoryMutationStatus.NotFound, "The party does not exist.");
            if (current.Snapshot.Lifecycle is NCPartyLifecycle.Dissolved or NCPartyLifecycle.Archived &&
                current.Snapshot.Lifecycle != request.Lifecycle)
            {
                return Reject(request.Party, NCPartyDirectoryMutationStatus.Rejected,
                    "A dissolved or archived party cannot return to an active lifecycle.");
            }

            var eventId = Guid.NewGuid();
            var snapshot = current.Snapshot with
            {
                Lifecycle = request.Lifecycle,
                ChangedAt = now,
                DataVersion = current.Snapshot.DataVersion + 1,
                LastAppliedEventId = eventId,
            };
            return await Persist(
                request.CommandId,
                "living-city.party.set-lifecycle",
                eventId,
                "living-city.party.lifecycle-changed",
                request.Party,
                request.ExpectedVersion,
                snapshot,
                request.CorrelationId,
                request.CausationId,
                request.AuditSource,
                epoch,
                now);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to change persistent party {request.Party}: {exception}");
            return Reject(request.Party, NCPartyDirectoryMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<NCPartyDirectoryMutationResult> Persist(
        Guid commandId,
        string commandType,
        Guid eventId,
        string eventType,
        NCPartyRef party,
        long expectedVersion,
        NCPartyDirectorySnapshot snapshot,
        Guid correlationId,
        Guid? causationId,
        string auditSource,
        NCSessionEpoch epoch,
        NCActiveWorldTime now)
    {
        var receipt = new PartyReceipt(snapshot, expectedVersion + 1, epoch, now);
        var result = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(
            commandId,
            commandType,
            "party",
            PartyKey(party),
            eventId,
            eventType,
            AggregateType,
            PartyKey(party),
            expectedVersion,
            SnapshotFormatVersion,
            JsonSerializer.Serialize(snapshot, JsonOptions),
            JsonSerializer.Serialize(new PartyEvent(party, eventType, auditSource, snapshot.Lifecycle), JsonOptions),
            JsonSerializer.Serialize(receipt, JsonOptions),
            correlationId,
            causationId,
            epoch,
            now,
            []));

        if (result.Status == NCPersistentMutationStatus.Applied)
        {
            var record = CacheNewest(new NCPartyDirectoryRecord(snapshot, result.AggregateVersion, epoch, now));
            Publish(record);
            return new NCPartyDirectoryMutationResult(NCPartyDirectoryMutationStatus.Applied, party, record, null);
        }

        if (result.Status == NCPersistentMutationStatus.DuplicateCompleted)
        {
            var replay = DeserializeReceipt(result.ResultPayload);
            if (replay == null || replay.Snapshot.Party != party)
                return Reject(party, NCPartyDirectoryMutationStatus.StorageFailure, "The party receipt is invalid.");
            var record = CacheNewest(new NCPartyDirectoryRecord(
                replay.Snapshot,
                replay.AggregateVersion,
                replay.SessionEpoch,
                replay.PersistedAt));
            Publish(record);
            return new NCPartyDirectoryMutationResult(NCPartyDirectoryMutationStatus.Duplicate, party, record, null);
        }

        return Reject(party, result.Status == NCPersistentMutationStatus.VersionConflict ||
                             result.Status == NCPersistentMutationStatus.DuplicateFailed
                ? NCPartyDirectoryMutationStatus.VersionConflict
                : NCPartyDirectoryMutationStatus.StorageFailure,
            result.Error ?? "The party mutation was not committed.");
    }

    private void Publish(NCPartyDirectoryRecord record)
    {
        _taskManager.RunOnMainThread(() => RaiseLocalEvent(new NCPartyDirectoryPersistedEvent(
            record.Snapshot.Party,
            record.AggregateVersion,
            record.Snapshot.Lifecycle)));
    }

    private static bool TryValidateMetadata(
        Guid commandId,
        NCPartyRef party,
        Guid correlationId,
        string auditSource,
        out string? error)
    {
        if (commandId == Guid.Empty || !IsDirectoryParty(party) || correlationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(auditSource) || auditSource.Length > MaximumAuditSourceLength)
        {
            error = "The party mutation metadata is invalid.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool IsDirectoryParty(NCPartyRef party)
    {
        return party.IsValid && party.Kind is NCPartyKind.Organization or NCPartyKind.Business or
            NCPartyKind.CityService or NCPartyKind.Estate;
    }

    private static NCPartyDirectoryRecord? Deserialize(NCAggregateSnapshotData? stored, NCPartyRef party)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCPartyDirectorySnapshot>(stored.Payload, JsonOptions);
            return Valid(snapshot, party)
                ? new NCPartyDirectoryRecord(snapshot!, stored.AggregateVersion, stored.SessionEpoch,
                    stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PartyReceipt? DeserializeReceipt(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;
        try
        {
            var receipt = JsonSerializer.Deserialize<PartyReceipt>(payload, JsonOptions);
            return receipt != null && receipt.AggregateVersion >= 1 && receipt.SessionEpoch.IsValid &&
                   receipt.PersistedAt.IsValid && Valid(receipt.Snapshot, receipt.Snapshot.Party)
                ? receipt
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Valid(NCPartyDirectorySnapshot? snapshot, NCPartyRef party)
    {
        return snapshot != null && IsDirectoryParty(party) && snapshot.Party == party &&
               !string.IsNullOrWhiteSpace(snapshot.DisplayName) &&
               snapshot.DisplayName.Length <= MaximumDisplayNameLength &&
               snapshot.DisplayName.Trim() == snapshot.DisplayName && Enum.IsDefined(snapshot.Lifecycle) &&
               snapshot.RegisteredAt.IsValid && snapshot.ChangedAt.IsValid && snapshot.DataVersion >= 1 &&
               snapshot.LastAppliedEventId != Guid.Empty;
    }

    private NCPartyDirectoryRecord CacheNewest(NCPartyDirectoryRecord candidate)
    {
        return _cache.AddOrUpdate(candidate.Snapshot.Party, candidate,
            (_, current) => current.AggregateVersion > candidate.AggregateVersion ? current : candidate);
    }

    private SemaphoreSlim GetMutationGate(NCPartyRef party)
    {
        var index = party.GetHashCode() & int.MaxValue;
        return _mutationGates[index % _mutationGates.Length];
    }

    private static SemaphoreSlim[] CreateMutationGates()
    {
        var gates = new SemaphoreSlim[MutationGateStripeCount];
        for (var i = 0; i < gates.Length; i++)
            gates[i] = new SemaphoreSlim(1, 1);
        return gates;
    }

    private static string PartyKey(NCPartyRef party) => party.ToString();

    private static NCPartyDirectoryMutationResult Reject(
        NCPartyRef party,
        NCPartyDirectoryMutationStatus status,
        string error)
    {
        return new NCPartyDirectoryMutationResult(status, party, null, error);
    }

    private sealed record PartyEvent(
        NCPartyRef Party,
        string EventType,
        string AuditSource,
        NCPartyLifecycle Lifecycle);

    private sealed record PartyReceipt(
        NCPartyDirectorySnapshot Snapshot,
        long AggregateVersion,
        NCSessionEpoch SessionEpoch,
        NCActiveWorldTime PersistedAt);
}
