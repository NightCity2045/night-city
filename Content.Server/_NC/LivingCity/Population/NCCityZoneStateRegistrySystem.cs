// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Population;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;

namespace Content.Server._NC.LivingCity.Population;

/// <summary>
/// Persistent optimistic registry for district/sector population and city-state read models. One zone-state write
/// is atomic, idempotent and independent from materialized NPC entities.
/// </summary>
public sealed partial class NCCityZoneStateRegistrySystem : EntitySystem
{
    public const string AggregateType = "living-city.zone-state";
    public const int SnapshotFormatVersion = 1;

    private const int MutationGateStripeCount = 32;
    private const int MaximumAuditSourceLength = 128;
    private const int MaximumSignalSourceLength = 128;
    private const int MaximumArchetypeCount = 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private ITaskManager _taskManager = default!;

    private readonly ConcurrentDictionary<NCZoneId, NCCityZoneStateRecord> _cache = [];
    private readonly SemaphoreSlim[] _mutationGates = CreateMutationGates();

    public int CachedScopeCount => _cache.Count;

    public bool TryGetCached(NCZoneId scopeZone, out NCCityZoneStateRecord record)
    {
        return _cache.TryGetValue(scopeZone, out record!);
    }

    public bool Unload(NCZoneId scopeZone)
    {
        return _cache.TryRemove(scopeZone, out _);
    }

    public async Task<NCCityZoneStateRecord?> GetAsync(NCZoneId scopeZone, bool forceReload = false)
    {
        if (!scopeZone.IsValid)
            return null;
        if (!forceReload && _cache.TryGetValue(scopeZone, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(AggregateType, ZoneKey(scopeZone));
        var record = Deserialize(stored, scopeZone);
        return record == null ? null : CacheNewest(record);
    }

    public async Task<NCCityZoneStateMutationResult> PersistAsync(NCCityZoneStateMutationRequest request)
    {
        var scopeZone = request.Snapshot?.ScopeZone ?? default;
        if (!TryValidateRequestShape(request, out var error))
            return Reject(scopeZone, NCCityZoneStateMutationStatus.Rejected, error!);

        NCPersistentCommandResultData? existing;
        try
        {
            // Exact command replay must be resolved before consulting mutable projection state.
            existing = await _database.GetNCPersistentCommandResultAsync(request.CommandId);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to read zone-state command receipt {request.CommandId}: {exception}");
            return Reject(scopeZone, NCCityZoneStateMutationStatus.StorageFailure, exception.Message);
        }

        if (existing != null)
            return ReplayExisting(request, existing);

        // Zone prototypes are mutable mapper data, so this check belongs after exact receipt replay.
        if (!TryValidateScope(request.Snapshot!, out error))
            return Reject(scopeZone, NCCityZoneStateMutationStatus.Rejected, error!);

        if (!_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(scopeZone, NCCityZoneStateMutationStatus.ClockUnavailable,
                "The active-world clock is not ready.");
        }

        var gate = GetMutationGate(scopeZone);
        await gate.WaitAsync();
        try
        {
            var snapshot = request.Snapshot!;
            var payload = JsonSerializer.Serialize(snapshot, JsonOptions);
            var receiptPayload = JsonSerializer.Serialize(
                new ZoneStateReceipt(snapshot, request.ExpectedVersion + 1, epoch, now),
                JsonOptions);
            var result = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(
                request.CommandId,
                "living-city.zone-state.project",
                "zone",
                ZoneKey(scopeZone),
                snapshot.LastAppliedEventId,
                "living-city.zone-state.projected",
                AggregateType,
                ZoneKey(scopeZone),
                request.ExpectedVersion,
                SnapshotFormatVersion,
                payload,
                JsonSerializer.Serialize(
                    new ZoneStateEventPayload(scopeZone, snapshot.ScopeSlot, request.AuditSource, snapshot),
                    JsonOptions),
                receiptPayload,
                request.CorrelationId,
                request.CausationId,
                epoch,
                now,
                []));

            if (result.Status == NCPersistentMutationStatus.Applied)
                return ApplyReceipt(receiptPayload, NCCityZoneStateMutationStatus.Applied);
            if (result.Status == NCPersistentMutationStatus.DuplicateCompleted)
                return ApplyReceipt(result.ResultPayload, NCCityZoneStateMutationStatus.Duplicate);

            var status = result.Status switch
            {
                NCPersistentMutationStatus.VersionConflict => NCCityZoneStateMutationStatus.VersionConflict,
                NCPersistentMutationStatus.ClockRejected => NCCityZoneStateMutationStatus.ClockUnavailable,
                NCPersistentMutationStatus.DuplicateFailed when
                    result.Error?.StartsWith("Expected aggregate version ") == true =>
                    NCCityZoneStateMutationStatus.VersionConflict,
                NCPersistentMutationStatus.DuplicateFailed when
                    result.Error?.StartsWith("The mutation timestamp ") == true =>
                    NCCityZoneStateMutationStatus.ClockUnavailable,
                _ => NCCityZoneStateMutationStatus.StorageFailure,
            };
            return Reject(scopeZone, status, result.Error ?? "The zone-state mutation failed.");
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to persist city zone state {scopeZone}: {exception}");
            return Reject(scopeZone, NCCityZoneStateMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private NCCityZoneStateMutationResult ReplayExisting(
        NCCityZoneStateMutationRequest request,
        NCPersistentCommandResultData existing)
    {
        var scopeZone = request.Snapshot.ScopeZone;
        if (existing.CommandType != "living-city.zone-state.project" || existing.ScopeType != "zone" ||
            existing.ScopeId != ZoneKey(scopeZone))
        {
            return Reject(scopeZone, NCCityZoneStateMutationStatus.StorageFailure,
                "The command ID is already owned by another persistent scope or command type.");
        }

        if (existing.Status == NCPersistentMutationStatus.DuplicateCompleted)
            return ApplyReceipt(existing.ResultPayload, NCCityZoneStateMutationStatus.Duplicate);

        var status = existing.Status switch
        {
            NCPersistentMutationStatus.DuplicateFailed when
                existing.Error?.StartsWith("Expected aggregate version ") == true =>
                NCCityZoneStateMutationStatus.VersionConflict,
            NCPersistentMutationStatus.DuplicateFailed when
                existing.Error?.StartsWith("The mutation timestamp ") == true =>
                NCCityZoneStateMutationStatus.ClockUnavailable,
            _ => NCCityZoneStateMutationStatus.StorageFailure,
        };
        return Reject(scopeZone, status,
            existing.Error ?? "The persistent zone-state command is still in progress.");
    }

    private NCCityZoneStateMutationResult ApplyReceipt(string? payload, NCCityZoneStateMutationStatus status)
    {
        var receipt = DeserializeReceipt(payload);
        if (receipt == null)
        {
            return Reject(default, NCCityZoneStateMutationStatus.StorageFailure,
                "The zone-state command receipt is invalid.");
        }

        var record = CacheNewest(new NCCityZoneStateRecord(
            receipt.Snapshot,
            receipt.AggregateVersion,
            receipt.SessionEpoch,
            receipt.PersistedAt));
        if (status == NCCityZoneStateMutationStatus.Applied)
        {
            _taskManager.RunOnMainThread(() => RaiseLocalEvent(new NCCityZoneStatePersistedEvent(
                record.Snapshot.ScopeZone,
                record.Snapshot.ScopeSlot,
                record.AggregateVersion)));
        }
        return new NCCityZoneStateMutationResult(
            status,
            record.Snapshot.ScopeZone,
            record.AggregateVersion,
            record,
            null);
    }

    private static bool TryValidateRequestShape(NCCityZoneStateMutationRequest request, out string? error)
    {
        error = null;
        if (request.CommandId == Guid.Empty || request.CorrelationId == Guid.Empty || request.ExpectedVersion < 0 ||
            string.IsNullOrWhiteSpace(request.AuditSource) || request.AuditSource.Length > MaximumAuditSourceLength ||
            !ValidSnapshot(request.Snapshot))
        {
            error = "Zone-state snapshot or command metadata is invalid.";
            return false;
        }

        return true;
    }

    private bool TryValidateScope(NCCityZoneStateSnapshot snapshot, out string? error)
    {
        error = null;
        if (!_zones.TryGetZone(snapshot.ScopeZone, out var scope) ||
            scope.ContextSlot != snapshot.ScopeSlot ||
            scope.ContextSlot is not (NCZoneContextSlot.District or NCZoneContextSlot.Sector))
        {
            error = "Zone-state scope must be an authored district or sector.";
            return false;
        }

        return true;
    }

    private static bool ValidSnapshot(NCCityZoneStateSnapshot? snapshot)
    {
        if (snapshot == null || !snapshot.ScopeZone.IsValid ||
            snapshot.ScopeSlot is not (NCZoneContextSlot.District or NCZoneContextSlot.Sector) ||
            snapshot.DataVersion != 1 || !snapshot.CapturedAt.IsValid || snapshot.LastAppliedEventId == Guid.Empty ||
            snapshot.Population == null || snapshot.Signals == null ||
            snapshot.Signals.Length != Enum.GetValues<NCCityStateSignalKind>().Length)
        {
            return false;
        }

        var population = snapshot.Population;
        if (population.TotalActors < 0 || population.LivingActors < 0 || population.DeadActors < 0 ||
            population.LivingActors + population.DeadActors != population.TotalActors ||
            population.FrozenActors < 0 || population.FrozenActors > population.TotalActors ||
            population.TravelingActors < 0 || population.TravelingActors > population.TotalActors ||
            !float.IsFinite(population.WeightedActivityUnits) || population.WeightedActivityUnits < 0 ||
            population.Statuses == null || population.CognitionTiers == null || population.Availability == null ||
            population.Archetypes == null || population.Archetypes.Length > MaximumArchetypeCount ||
            population.Statuses.Any(static entry => entry == null || !Enum.IsDefined(entry.Status) || entry.Count <= 0) ||
            population.CognitionTiers.Any(static entry => entry == null || !Enum.IsDefined(entry.Tier) || entry.Count <= 0) ||
            population.Availability.Any(static entry => entry == null || !Enum.IsDefined(entry.Availability) || entry.Count <= 0) ||
            population.Archetypes.Any(static entry => entry == null || string.IsNullOrWhiteSpace(entry.ArchetypeId) ||
                entry.ArchetypeId.Length > MaximumSignalSourceLength || entry.Count <= 0) ||
            population.Statuses.Sum(static entry => entry.Count) != population.TotalActors ||
            population.CognitionTiers.Sum(static entry => entry.Count) != population.TotalActors ||
            population.Availability.Sum(static entry => entry.Count) != population.TotalActors)
        {
            return false;
        }

        var kinds = new HashSet<NCCityStateSignalKind>();
        foreach (var signal in snapshot.Signals)
        {
            if (signal == null || !Enum.IsDefined(signal.Kind) || !Enum.IsDefined(signal.Availability) ||
                !kinds.Add(signal.Kind) ||
                !float.IsFinite(signal.Value) || signal.Value is < 0f or > 1f ||
                string.IsNullOrWhiteSpace(signal.Source) || signal.Source.Length > MaximumSignalSourceLength ||
                signal.SourceVersion < 0 ||
                signal.Availability == NCCityStateSignalAvailability.Unavailable && signal.Value != 0f)
            {
                return false;
            }
        }

        return true;
    }

    private static NCCityZoneStateRecord? Deserialize(NCAggregateSnapshotData? stored, NCZoneId scopeZone)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCCityZoneStateSnapshot>(stored.Payload, JsonOptions);
            return ValidSnapshot(snapshot) && snapshot!.ScopeZone == scopeZone
                ? new NCCityZoneStateRecord(snapshot, stored.AggregateVersion, stored.SessionEpoch,
                    stored.ActiveWorldTime)
                : null;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static ZoneStateReceipt? DeserializeReceipt(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;
        try
        {
            var receipt = JsonSerializer.Deserialize<ZoneStateReceipt>(payload, JsonOptions);
            return receipt != null && receipt.AggregateVersion >= 1 && receipt.SessionEpoch.IsValid &&
                   receipt.PersistedAt.IsValid && ValidSnapshot(receipt.Snapshot)
                ? receipt
                : null;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private NCCityZoneStateRecord CacheNewest(NCCityZoneStateRecord candidate)
    {
        return _cache.AddOrUpdate(candidate.Snapshot.ScopeZone, candidate,
            (_, current) => current.AggregateVersion > candidate.AggregateVersion ? current : candidate);
    }

    private SemaphoreSlim GetMutationGate(NCZoneId scopeZone)
    {
        var index = scopeZone.Value.GetHashCode() & int.MaxValue;
        return _mutationGates[index % _mutationGates.Length];
    }

    private static SemaphoreSlim[] CreateMutationGates()
    {
        var gates = new SemaphoreSlim[MutationGateStripeCount];
        for (var i = 0; i < gates.Length; i++)
            gates[i] = new SemaphoreSlim(1, 1);
        return gates;
    }

    private static string ZoneKey(NCZoneId zoneId) => zoneId.Value.ToString("D");

    private static NCCityZoneStateMutationResult Reject(
        NCZoneId scopeZone,
        NCCityZoneStateMutationStatus status,
        string error)
    {
        return new NCCityZoneStateMutationResult(status, scopeZone, 0, null, error);
    }

    private sealed record ZoneStateEventPayload(
        NCZoneId ScopeZone,
        NCZoneContextSlot ScopeSlot,
        string AuditSource,
        NCCityZoneStateSnapshot Snapshot);

    private sealed record ZoneStateReceipt(
        NCCityZoneStateSnapshot Snapshot,
        long AggregateVersion,
        NCSessionEpoch SessionEpoch,
        NCActiveWorldTime PersistedAt);
}
