// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using Content.Server.Database;
using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Actors.Domains;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Social;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Actors;

/// <summary>
/// Durable registry for physical GPT residents. It persists identity and recovery state, but never virtualizes the
/// actor, allocates a home/job/faction, or runs an abstract simulation.
/// </summary>
public sealed partial class NCCityActorRegistrySystem : EntitySystem
{
    public const string AggregateType = "living-city.actor";
    public const int SnapshotFormatVersion = 2;
    private const int MaximumNameLength = 128;
    private const int MaximumAliasCount = 16;
    private const int MaximumHumanoidProfilePayloadLength = 262_144;
    private const int MaximumAuditSourceLength = 128;
    private const int MutationGateStripeCount = 64;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NCCityActorDomainRegistrySystem _domains = default!;
    [Dependency] private NCSocialProfileRegistrySystem _socialProfiles = default!;
    [Dependency] private ITaskManager _taskManager = default!;

    private readonly ConcurrentDictionary<NCActorId, NCCityActorRecord> _cache = [];
    private readonly SemaphoreSlim[] _mutationGates = CreateMutationGates();

    public int CachedActorCount => _cache.Count;

    public bool TryGetCached(NCActorId actorId, out NCCityActorRecord record) =>
        _cache.TryGetValue(actorId, out record!);

    public bool Unload(NCActorId actorId) => _cache.TryRemove(actorId, out _);

    public async Task<NCCityActorRecord?> GetAsync(NCActorId actorId, bool forceReload = false)
    {
        if (!actorId.IsValid)
            return null;
        if (!forceReload && _cache.TryGetValue(actorId, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(AggregateType, ActorKey(actorId));
        var record = Deserialize(stored, actorId);
        return record == null ? null : CacheNewest(record);
    }

    public async Task<IReadOnlyList<NCCityActorRecord>> GetPageAsync(
        NCActorId? afterActorId,
        int limit,
        bool cache = false) =>
        (await GetPageResultAsync(afterActorId, limit, cache)).Records;

    public async Task<NCCityActorPage> GetPageResultAsync(
        NCActorId? afterActorId,
        int limit,
        bool cache = false)
    {
        var page = await _database.GetNCAggregateSnapshotPageAsync(
            AggregateType,
            afterActorId is { IsValid: true } after ? ActorKey(after) : null,
            limit);
        var records = new List<NCCityActorRecord>(page.Count);
        foreach (var stored in page)
        {
            if (!Guid.TryParse(stored.AggregateId, out var rawId))
                continue;
            var actorId = new NCActorId(rawId);
            var record = Deserialize(stored, actorId);
            if (record == null)
                continue;
            records.Add(cache ? CacheNewest(record) : record);
        }

        NCActorId? continuation = null;
        if (page.Count > 0 && Guid.TryParse(page[^1].AggregateId, out var lastActorId))
            continuation = new NCActorId(lastActorId);
        return new NCCityActorPage(records, continuation, page.Count == limit);
    }

    public async Task<NCCityActorMutationResult> CreateResidentAsync(NCCityResidentCreateRequest request)
    {
        if (!TryValidateResidentCreate(request, out var archetype, out var error) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(request.ActorId,
                _clock.IsReady ? NCCityActorMutationStatus.Rejected : NCCityActorMutationStatus.ClockUnavailable,
                error ?? "The active-world clock is not ready.");
        }

        var gate = GetMutationGate(request.ActorId);
        await gate.WaitAsync();
        try
        {
            var eventId = Guid.NewGuid();
            var (appearanceSeed, biographySeed) = CreatePresentationSeeds(request.ActorId);
            var snapshot = new NCCityActorSnapshot(
                new NCCityActorIdentity(
                    request.ActorId,
                    request.FullName.Trim(),
                    [],
                    archetype.ID,
                    appearanceSeed,
                    biographySeed,
                    1)
                {
                    NeuralPersonaProfileId = request.Persona.Id,
                    HumanoidProfilePayload = request.HumanoidProfilePayload,
                    GeneratedPersona = request.GeneratedPersona,
                },
                new NCCityActorLocation(
                    request.CurrentZone,
                    default,
                    NCCityActorTravelPhase.Stationary,
                    archetype.TravelMode,
                    null,
                    now),
                new NCCityActorLifecycle(NCCityActorStatus.Active, false, null, null),
                eventId)
            {
                PhysicalRecovery = request.PhysicalRecovery,
            };

            return await PersistCreate(request, archetype, snapshot, epoch, now, eventId);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to create persistent resident {request.ActorId}: {exception}");
            return Reject(request.ActorId, NCCityActorMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<NCCityActorMutationResult> SetLocationAsync(NCCityActorLocationMutationRequest request)
    {
        if (!TryValidateLocation(request, out var error))
            return Task.FromResult(Reject(request.ActorId, NCCityActorMutationStatus.Rejected, error!));
        return Mutate(
            request.ActorId,
            request.CommandId,
            "living-city.actor.set-location",
            "living-city.actor.location-changed",
            request.ExpectedVersion,
            request.CorrelationId,
            request.CausationId,
            request.AuditSource,
            (current, now, eventId) =>
            {
                if (CannotMutate(current.Snapshot))
                    return null;
                return current.Snapshot with
                {
                    Identity = IncrementIdentity(current.Snapshot.Identity),
                    Location = new NCCityActorLocation(
                        request.CurrentZone,
                        request.DestinationZone,
                        request.TravelPhase,
                        request.TravelMode,
                        request.ExpectedArrivalAt,
                        now),
                    LastAppliedEventId = eventId,
                };
            });
    }

    public Task<NCCityActorMutationResult> SetStatusAsync(NCCityActorStatusMutationRequest request)
    {
        if (!TryValidateMutationMetadata(request.ActorId, request.CommandId, request.ExpectedVersion,
                request.CorrelationId, request.AuditSource, out var error) || !Enum.IsDefined(request.Status))
        {
            return Task.FromResult(Reject(request.ActorId, NCCityActorMutationStatus.Rejected,
                error ?? "Actor status is invalid."));
        }

        return Mutate(
            request.ActorId,
            request.CommandId,
            "living-city.actor.set-status",
            "living-city.actor.status-changed",
            request.ExpectedVersion,
            request.CorrelationId,
            request.CausationId,
            request.AuditSource,
            (current, now, eventId) =>
            {
                var lifecycle = current.Snapshot.Lifecycle;
                if (lifecycle.Status is NCCityActorStatus.Dead or NCCityActorStatus.Archived &&
                    request.Status != lifecycle.Status)
                    return null;
                return current.Snapshot with
                {
                    Identity = IncrementIdentity(current.Snapshot.Identity),
                    Lifecycle = lifecycle with
                    {
                        Status = request.Status,
                        DiedAt = request.Status == NCCityActorStatus.Dead ? lifecycle.DiedAt ?? now : lifecycle.DiedAt,
                        ArchivedAt = request.Status == NCCityActorStatus.Archived
                            ? lifecycle.ArchivedAt ?? now
                            : lifecycle.ArchivedAt,
                    },
                    LastAppliedEventId = eventId,
                };
            });
    }

    public Task<NCCityActorMutationResult> SetFrozenAsync(NCCityActorFreezeMutationRequest request)
    {
        if (!TryValidateMutationMetadata(request.ActorId, request.CommandId, request.ExpectedVersion,
                request.CorrelationId, request.AuditSource, out var error))
        {
            return Task.FromResult(Reject(request.ActorId, NCCityActorMutationStatus.Rejected, error!));
        }

        return Mutate(
            request.ActorId,
            request.CommandId,
            "living-city.actor.set-frozen",
            "living-city.actor.frozen-changed",
            request.ExpectedVersion,
            request.CorrelationId,
            request.CausationId,
            request.AuditSource,
            (current, _, eventId) =>
            {
                if (current.Snapshot.Lifecycle.Status is NCCityActorStatus.Dead or NCCityActorStatus.Archived)
                    return null;
                return current.Snapshot with
                {
                    Identity = IncrementIdentity(current.Snapshot.Identity),
                    Lifecycle = current.Snapshot.Lifecycle with { Frozen = request.Frozen },
                    LastAppliedEventId = eventId,
                };
            });
    }

    public async Task<NCPhysicalActorCheckpointResult> CommitPhysicalCheckpointAsync(
        NCPhysicalActorCheckpointRequest request)
    {
        const string commandType = "living-city.actor.checkpoint";
        if (!TryValidateCheckpoint(request, out var error))
            return RejectCheckpoint(request.ActorId, NCCityActorMutationStatus.Rejected, error!);

        try
        {
            var existing = await _database.GetNCPersistentCommandResultAsync(request.CommandId);
            if (existing != null)
                return ReplayCheckpoint(request.ActorId, commandType, existing);
        }
        catch (Exception exception)
        {
            return RejectCheckpoint(request.ActorId, NCCityActorMutationStatus.StorageFailure, exception.Message);
        }

        if (!_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return RejectCheckpoint(request.ActorId, NCCityActorMutationStatus.ClockUnavailable,
                "The active-world clock is not ready.");
        }

        var gate = GetMutationGate(request.ActorId);
        await gate.WaitAsync();
        try
        {
            var current = await GetAsync(request.ActorId, true);
            if (current == null)
                return RejectCheckpoint(request.ActorId, NCCityActorMutationStatus.NotFound, "Actor not found.");
            if (current.Snapshot.Lifecycle.Status is NCCityActorStatus.Dead or NCCityActorStatus.Archived)
            {
                return RejectCheckpoint(request.ActorId, NCCityActorMutationStatus.Rejected,
                    "Terminal actors cannot accept physical checkpoints.");
            }
            if (current.AggregateVersion != request.ExpectedActorVersion)
            {
                return RejectCheckpoint(request.ActorId, NCCityActorMutationStatus.VersionConflict,
                    "Actor changed before its physical checkpoint was committed.");
            }

            var eventId = Guid.NewGuid();
            var next = current.Snapshot with
            {
                Identity = IncrementIdentity(current.Snapshot.Identity),
                Location = current.Snapshot.Location with
                {
                    CurrentZone = request.CurrentZone,
                    LastKnownLocationAt = now,
                },
                // Runtime map coordinates are deliberately not durable. An unzoned checkpoint also clears any stale
                // city recovery point so the next round uses a normal SS14 spawn point.
                PhysicalRecovery = request.CurrentZone.IsValid
                    ? request.PhysicalRecovery ?? current.Snapshot.PhysicalRecovery
                    : null,
                LastAppliedEventId = eventId,
            };
            if (!TryNormalizeSnapshot(next, request.ActorId, out next))
            {
                return RejectCheckpoint(request.ActorId, NCCityActorMutationStatus.Rejected,
                    "Physical checkpoint produced an invalid actor snapshot.");
            }

            var receipt = new NCPhysicalActorCheckpointReceipt(
                next,
                request.ExpectedActorVersion + 1,
                request.Domains.Health,
                request.Domains.ExpectedHealthVersion + 1,
                request.Domains.Inventory,
                request.Domains.ExpectedInventoryVersion + 1,
                epoch,
                now);
            var writes = new List<NCPersistentAggregateWrite>
            {
                BuildWrite(eventId, "living-city.actor.checkpointed", request.ActorId,
                    request.ExpectedActorVersion, next, request.AuditSource),
            };
            writes.AddRange(_domains.BuildCaptureWrites(request.Domains, request.AuditSource));
            writes.AddRange(request.ParticipantWrites ?? []);

            var persistence = await _database.ApplyNCPersistentBatchMutationAsync(
                new NCPersistentBatchMutationRequest(
                    request.CommandId,
                    commandType,
                    "actor",
                    ActorKey(request.ActorId),
                    writes,
                    JsonSerializer.Serialize(receipt, JsonOptions),
                    request.CorrelationId,
                    request.CausationId,
                    epoch,
                    now,
                    [],
                    NCPersistenceWriteClass.CoalescedCheckpoint));

            if (persistence.Status == NCPersistentMutationStatus.Applied)
                return ApplyCheckpointReceipt(receipt, NCCityActorMutationStatus.Applied);
            if (persistence.Status == NCPersistentMutationStatus.DuplicateCompleted)
                return ApplyCheckpointReceipt(persistence.ResultPayload, request.ActorId,
                    NCCityActorMutationStatus.Duplicate);
            return RejectCheckpoint(request.ActorId, MapStatus(persistence.Status, persistence.Error),
                persistence.Error ?? "Physical checkpoint transaction failed.");
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to checkpoint physical actor {request.ActorId}: {exception}");
            return RejectCheckpoint(request.ActorId, NCCityActorMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<NCCityActorMutationResult> PersistCreate(
        NCCityResidentCreateRequest request,
        NCCityActorArchetypePrototype archetype,
        NCCityActorSnapshot snapshot,
        NCSessionEpoch epoch,
        NCActiveWorldTime now,
        Guid eventId)
    {
        var writes = new List<NCPersistentAggregateWrite>
        {
            BuildWrite(eventId, "living-city.actor.resident-created", request.ActorId, 0, snapshot,
                request.AuditSource),
        };
        writes.AddRange(_domains.BuildInitialWrites(request.ActorId, now, request.AuditSource));
        writes.Add(_socialProfiles.BuildInitialWrite(request.ActorId, archetype, now, request.AuditSource));
        // The empty stock node is the durable authority for the physical inventory even for a plain resident.
        writes.Add(NCEconomicAssetRegistrySystem.BuildInitialActorStockNodeWrite(
            request.ActorId,
            now,
            request.AuditSource));

        var receipt = new NCCityActorReceipt(snapshot, 1, epoch, now);
        var result = await _database.ApplyNCPersistentBatchMutationAsync(new NCPersistentBatchMutationRequest(
            request.CommandId,
            "living-city.actor.create-resident",
            "actor",
            ActorKey(request.ActorId),
            writes,
            JsonSerializer.Serialize(receipt, JsonOptions),
            request.CorrelationId,
            request.CausationId,
            epoch,
            now,
            []));

        if (result.Status == NCPersistentMutationStatus.Applied)
        {
            var version = result.Aggregates
                .First(value => value.AggregateType == AggregateType && value.AggregateId == ActorKey(request.ActorId))
                .AggregateVersion;
            var record = CacheNewest(new NCCityActorRecord(snapshot, version, epoch, now));
            PublishPersisted(record);
            return new NCCityActorMutationResult(NCCityActorMutationStatus.Applied, request.ActorId, version,
                record, null);
        }
        if (result.Status == NCPersistentMutationStatus.DuplicateCompleted)
        {
            var record = DeserializeReceipt(result.ResultPayload, request.ActorId) ??
                         await GetAsync(request.ActorId, true);
            return new NCCityActorMutationResult(NCCityActorMutationStatus.Duplicate, request.ActorId,
                record?.AggregateVersion ?? 0, record, record == null ? "Invalid creation receipt." : null);
        }
        return Reject(request.ActorId, MapStatus(result.Status, result.Error),
            result.Error ?? "Resident creation transaction failed.");
    }

    private async Task<NCCityActorMutationResult> Mutate(
        NCActorId actorId,
        Guid commandId,
        string commandType,
        string eventType,
        long expectedVersion,
        Guid correlationId,
        Guid? causationId,
        string auditSource,
        Func<NCCityActorRecord, NCActiveWorldTime, Guid, NCCityActorSnapshot?> transform)
    {
        if (!_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject(actorId, NCCityActorMutationStatus.ClockUnavailable, "Active-world clock is unavailable.");

        var gate = GetMutationGate(actorId);
        await gate.WaitAsync();
        try
        {
            var current = await GetAsync(actorId, true);
            if (current == null)
                return Reject(actorId, NCCityActorMutationStatus.NotFound, "Actor not found.");
            if (current.AggregateVersion != expectedVersion)
            {
                return new NCCityActorMutationResult(NCCityActorMutationStatus.VersionConflict, actorId,
                    current.AggregateVersion, current, "Actor aggregate version changed.");
            }

            var eventId = Guid.NewGuid();
            var next = transform(current, now, eventId);
            if (next == null || !TryNormalizeSnapshot(next, actorId, out next))
                return Reject(actorId, NCCityActorMutationStatus.Rejected, "Actor mutation violates lifecycle rules.");

            var receipt = new NCCityActorReceipt(next, expectedVersion + 1, epoch, now);
            var result = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(
                commandId,
                commandType,
                "actor",
                ActorKey(actorId),
                eventId,
                eventType,
                AggregateType,
                ActorKey(actorId),
                expectedVersion,
                SnapshotFormatVersion,
                JsonSerializer.Serialize(next, JsonOptions),
                JsonSerializer.Serialize(new NCCityActorEvent(actorId, eventType, auditSource, next), JsonOptions),
                JsonSerializer.Serialize(receipt, JsonOptions),
                correlationId,
                causationId,
                epoch,
                now,
                [],
                eventType == "living-city.actor.location-changed"
                    ? NCPersistenceWriteClass.CoalescedCheckpoint
                    : NCPersistenceWriteClass.DurableOutcome));

            if (result.Status == NCPersistentMutationStatus.Applied)
            {
                var record = CacheNewest(new NCCityActorRecord(next, result.AggregateVersion, epoch, now));
                PublishPersisted(record);
                return new NCCityActorMutationResult(NCCityActorMutationStatus.Applied, actorId,
                    result.AggregateVersion, record, null);
            }
            if (result.Status == NCPersistentMutationStatus.DuplicateCompleted)
            {
                var record = DeserializeReceipt(result.ResultPayload, actorId) ?? await GetAsync(actorId, true);
                return new NCCityActorMutationResult(NCCityActorMutationStatus.Duplicate, actorId,
                    record?.AggregateVersion ?? result.AggregateVersion, record, null);
            }
            return new NCCityActorMutationResult(MapStatus(result.Status, result.Error), actorId,
                result.AggregateVersion, null, result.Error);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to mutate actor {actorId}: {exception}");
            return Reject(actorId, NCCityActorMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private bool TryValidateResidentCreate(
        NCCityResidentCreateRequest request,
        out NCCityActorArchetypePrototype archetype,
        out string? error)
    {
        archetype = default!;
        error = null;
        if (!request.ActorId.IsValid || request.CommandId == Guid.Empty || request.CorrelationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.FullName) || request.FullName.Length > MaximumNameLength ||
            string.IsNullOrWhiteSpace(request.AuditSource) || request.AuditSource.Length > MaximumAuditSourceLength ||
            string.IsNullOrWhiteSpace(request.HumanoidProfilePayload) ||
            request.HumanoidProfilePayload.Length > MaximumHumanoidProfilePayloadLength ||
            !ValidGeneratedPersona(request.GeneratedPersona) ||
            !ValidCreateLocation(request.CurrentZone, request.PhysicalRecovery) ||
            !_prototypes.HasIndex(request.Persona) ||
            !_prototypes.TryIndex(request.Archetype, out NCCityActorArchetypePrototype? loaded) || loaded == null)
        {
            error = "Resident identity, prototype, location or recovery data is invalid.";
            return false;
        }
        archetype = loaded;
        return true;
    }

    private bool TryValidateLocation(NCCityActorLocationMutationRequest request, out string? error)
    {
        if (!TryValidateMutationMetadata(request.ActorId, request.CommandId, request.ExpectedVersion,
                request.CorrelationId, request.AuditSource, out error) ||
            request.ExpectedArrivalAt is { IsValid: false } ||
            !Enum.IsDefined(request.TravelPhase) || !_zones.TryGetZone(request.CurrentZone, out _) ||
            request.DestinationZone.IsValid && !_zones.TryGetZone(request.DestinationZone, out _))
        {
            error ??= "Location mutation metadata is invalid.";
            return false;
        }
        if (request.TravelPhase is NCCityActorTravelPhase.Planned or NCCityActorTravelPhase.InTransit or
                NCCityActorTravelPhase.Blocked &&
            (!request.DestinationZone.IsValid || !HasSingleTravelMode(request.TravelMode)))
        {
            error = "Physical travel requires one destination and one travel mode.";
            return false;
        }
        return true;
    }

    private bool TryValidateCheckpoint(NCPhysicalActorCheckpointRequest request, out string? error)
    {
        if (!TryValidateMutationMetadata(request.ActorId, request.CommandId, request.ExpectedActorVersion,
                request.CorrelationId, request.AuditSource, out error) ||
            request.Domains.ActorId != request.ActorId || request.Domains.ExpectedHealthVersion < 1 ||
            request.Domains.ExpectedInventoryVersion < 1 || request.Domains.Health.ActorId != request.ActorId ||
            request.Domains.Inventory.ActorId != request.ActorId ||
            !ValidCheckpointLocation(request.CurrentZone, request.PhysicalRecovery) ||
            !ValidParticipantWrites(request.ParticipantWrites))
        {
            error ??= "Physical checkpoint metadata or domain capture is invalid.";
            return false;
        }
        return true;
    }

    private static bool TryValidateMutationMetadata(
        NCActorId actorId,
        Guid commandId,
        long expectedVersion,
        Guid correlationId,
        string auditSource,
        out string? error)
    {
        if (!actorId.IsValid || commandId == Guid.Empty || expectedVersion < 1 || correlationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(auditSource) || auditSource.Length > MaximumAuditSourceLength)
        {
            error = "Actor identity or mutation metadata is invalid.";
            return false;
        }
        error = null;
        return true;
    }

    private static bool TryNormalizeSnapshot(
        NCCityActorSnapshot? snapshot,
        NCActorId expectedActorId,
        out NCCityActorSnapshot normalized)
    {
        normalized = default!;
        if (snapshot == null || snapshot.Identity.ActorId != expectedActorId || !expectedActorId.IsValid ||
            string.IsNullOrWhiteSpace(snapshot.Identity.FullName) ||
            snapshot.Identity.FullName.Length > MaximumNameLength ||
            snapshot.Identity.Aliases == null || snapshot.Identity.Aliases.Length > MaximumAliasCount ||
            snapshot.Identity.Aliases.Any(alias => string.IsNullOrWhiteSpace(alias) || alias.Length > MaximumNameLength) ||
            string.IsNullOrWhiteSpace(snapshot.Identity.ArchetypeId) || snapshot.Identity.DataVersion < 1 ||
            snapshot.LastAppliedEventId == Guid.Empty || !ValidSnapshotLocation(snapshot) ||
            !snapshot.Location.LastKnownLocationAt.IsValid || !Enum.IsDefined(snapshot.Location.TravelPhase) ||
            !Enum.IsDefined(snapshot.Lifecycle.Status) ||
            snapshot.Location.ExpectedArrivalAt is { IsValid: false } ||
            snapshot.Identity.HumanoidProfilePayload?.Length > MaximumHumanoidProfilePayloadLength ||
            !ValidGeneratedPersona(snapshot.Identity.GeneratedPersona))
        {
            return false;
        }
        if (snapshot.Location.TravelPhase is NCCityActorTravelPhase.Planned or NCCityActorTravelPhase.InTransit or
                NCCityActorTravelPhase.Blocked &&
            (!snapshot.Location.DestinationZone.IsValid || !HasSingleTravelMode(snapshot.Location.TravelMode)))
            return false;
        if (snapshot.Lifecycle.Status == NCCityActorStatus.Dead && snapshot.Lifecycle.DiedAt is not { IsValid: true })
            return false;
        if (snapshot.Lifecycle.Status == NCCityActorStatus.Archived &&
            snapshot.Lifecycle.ArchivedAt is not { IsValid: true })
            return false;

        normalized = snapshot with
        {
            Identity = snapshot.Identity with
            {
                FullName = snapshot.Identity.FullName.Trim(),
                Aliases = snapshot.Identity.Aliases.Select(alias => alias.Trim()).ToArray(),
            },
        };
        return true;
    }

    private static bool ValidGeneratedPersona(NCGeneratedPersonaDetails? persona)
    {
        if (persona == null)
            return true;
        return StablePersonaKey(persona.BackgroundKey) && StablePersonaKey(persona.PressureKey) &&
               StablePersonaKey(persona.FearKey) && StablePersonaKey(persona.GoalKey) &&
               StablePersonaKey(persona.SocialHabitKey) && StablePersonaKey(persona.SpeechQuirkKey);
    }

    private static bool StablePersonaKey(string value) => value.Length is > 0 and <= 96 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private bool ValidCreateLocation(NCZoneId zone, NCCityActorPhysicalRecoveryState? recovery) =>
        zone.IsValid
            ? _zones.TryGetZone(zone, out _) && recovery is { } cityRecovery && ValidRecovery(cityRecovery, zone)
            : recovery == null;

    private bool ValidCheckpointLocation(NCZoneId zone, NCCityActorPhysicalRecoveryState? recovery) =>
        zone.IsValid
            ? _zones.TryGetZone(zone, out _) &&
              (recovery == null || ValidRecovery(recovery, zone))
            : recovery == null;

    private static bool ValidSnapshotLocation(NCCityActorSnapshot snapshot)
    {
        var zone = snapshot.Location.CurrentZone;
        if (zone.IsValid)
            return snapshot.PhysicalRecovery is not { } recovery || ValidRecovery(recovery, zone);

        // A resident without CityGraph geography has no durable travel destination or physical map coordinates.
        return snapshot.PhysicalRecovery == null && !snapshot.Location.DestinationZone.IsValid &&
               snapshot.Location.TravelPhase == NCCityActorTravelPhase.Stationary &&
               snapshot.Location.ExpectedArrivalAt == null;
    }

    private static bool ValidRecovery(NCCityActorPhysicalRecoveryState recovery, NCZoneId zone) =>
        recovery.NetworkId.IsValid && float.IsFinite(recovery.X) && float.IsFinite(recovery.Y) &&
        recovery.LocationContextId == zone;

    private static bool ValidParticipantWrites(IReadOnlyList<NCPersistentAggregateWrite>? writes)
    {
        if (writes == null)
            return true;
        if (writes.Count > 29)
            return false;
        var aggregates = new HashSet<(string Type, string Id)>();
        var events = new HashSet<Guid>();
        return writes.All(write => write.EventId != Guid.Empty && events.Add(write.EventId) &&
            !string.IsNullOrWhiteSpace(write.EventType) && !string.IsNullOrWhiteSpace(write.AggregateType) &&
            !string.IsNullOrWhiteSpace(write.AggregateId) && write.ExpectedAggregateVersion >= 0 &&
            write.SnapshotFormatVersion >= 1 && !string.IsNullOrWhiteSpace(write.SnapshotPayload) &&
            !string.IsNullOrWhiteSpace(write.EventPayload) && aggregates.Add((write.AggregateType, write.AggregateId)) &&
            write.AggregateType is not (AggregateType or NCCityActorDomainRegistrySystem.HealthAggregateType or
                NCCityActorDomainRegistrySystem.InventoryAggregateType));
    }

    private static NCCityActorRecord? Deserialize(NCAggregateSnapshotData? stored, NCActorId expectedActorId)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCCityActorSnapshot>(stored.Payload, JsonOptions);
            return TryNormalizeSnapshot(snapshot, expectedActorId, out var normalized)
                ? new NCCityActorRecord(normalized, stored.AggregateVersion, stored.SessionEpoch,
                    stored.ActiveWorldTime)
                : null;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static NCCityActorRecord? DeserializeReceipt(string? payload, NCActorId actorId)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;
        try
        {
            var receipt = JsonSerializer.Deserialize<NCCityActorReceipt>(payload, JsonOptions);
            return receipt != null && receipt.AggregateVersion > 0 &&
                   TryNormalizeSnapshot(receipt.Snapshot, actorId, out var normalized)
                ? new NCCityActorRecord(normalized, receipt.AggregateVersion, receipt.SessionEpoch,
                    receipt.PersistedAt)
                : null;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private NCPhysicalActorCheckpointResult ReplayCheckpoint(
        NCActorId actorId,
        string commandType,
        NCPersistentCommandResultData existing)
    {
        if (existing.CommandType != commandType || existing.ScopeType != "actor" ||
            existing.ScopeId != ActorKey(actorId))
        {
            return RejectCheckpoint(actorId, NCCityActorMutationStatus.StorageFailure,
                "Command ID belongs to another persistent operation.");
        }
        if (existing.Status == NCPersistentMutationStatus.DuplicateCompleted)
            return ApplyCheckpointReceipt(existing.ResultPayload, actorId, NCCityActorMutationStatus.Duplicate);
        return RejectCheckpoint(actorId, MapStatus(existing.Status, existing.Error),
            existing.Error ?? "Physical checkpoint is incomplete.");
    }

    private NCPhysicalActorCheckpointResult ApplyCheckpointReceipt(
        string? payload,
        NCActorId actorId,
        NCCityActorMutationStatus status)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return RejectCheckpoint(actorId, NCCityActorMutationStatus.StorageFailure, "Checkpoint receipt missing.");
        try
        {
            var receipt = JsonSerializer.Deserialize<NCPhysicalActorCheckpointReceipt>(payload, JsonOptions);
            return receipt == null
                ? RejectCheckpoint(actorId, NCCityActorMutationStatus.StorageFailure, "Checkpoint receipt invalid.")
                : ApplyCheckpointReceipt(receipt, status);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return RejectCheckpoint(actorId, NCCityActorMutationStatus.StorageFailure, exception.Message);
        }
    }

    private NCPhysicalActorCheckpointResult ApplyCheckpointReceipt(
        NCPhysicalActorCheckpointReceipt receipt,
        NCCityActorMutationStatus status)
    {
        var actorId = receipt.Actor.Identity.ActorId;
        if (!TryNormalizeSnapshot(receipt.Actor, actorId, out var actorSnapshot) ||
            receipt.ActorVersion < 1 || receipt.HealthVersion < 1 || receipt.InventoryVersion < 1 ||
            receipt.Health.ActorId != actorId || receipt.Inventory.ActorId != actorId)
        {
            return RejectCheckpoint(actorId, NCCityActorMutationStatus.StorageFailure,
                "Checkpoint receipt failed structural validation.");
        }
        var actor = CacheNewest(new NCCityActorRecord(actorSnapshot, receipt.ActorVersion,
            receipt.SessionEpoch, receipt.PersistedAt));
        var capture = new NCCityActorDomainCapture(
            actorId,
            receipt.HealthVersion - 1,
            receipt.Health,
            receipt.InventoryVersion - 1,
            receipt.Inventory,
            0,
            0);
        var domains = _domains.ApplyCommittedCapture(capture, receipt.SessionEpoch, receipt.PersistedAt);
        PublishPersisted(actor);
        return new NCPhysicalActorCheckpointResult(status, actorId, actor, domains.Health, domains.Inventory, null);
    }

    private NCPersistentAggregateWrite BuildWrite(
        Guid eventId,
        string eventType,
        NCActorId actorId,
        long expectedVersion,
        NCCityActorSnapshot snapshot,
        string auditSource) =>
        new(
            eventId,
            eventType,
            AggregateType,
            ActorKey(actorId),
            expectedVersion,
            SnapshotFormatVersion,
            JsonSerializer.Serialize(snapshot, JsonOptions),
            JsonSerializer.Serialize(new NCCityActorEvent(actorId, eventType, auditSource, snapshot), JsonOptions));

    private NCCityActorRecord CacheNewest(NCCityActorRecord candidate) =>
        _cache.AddOrUpdate(candidate.Snapshot.Identity.ActorId, candidate,
            (_, existing) => candidate.AggregateVersion >= existing.AggregateVersion ? candidate : existing);

    private void PublishPersisted(NCCityActorRecord record)
    {
        var snapshot = record.Snapshot;
        _taskManager.RunOnMainThread(() =>
        {
            var isNarrative = !string.IsNullOrWhiteSpace(snapshot.Identity.NarrativeActorProfileId) ||
                              _prototypes.TryIndex(
                                  new ProtoId<NCCityActorArchetypePrototype>(snapshot.Identity.ArchetypeId),
                                  out var archetype) && archetype?.NarrativeActorProfile != null;
            RaiseLocalEvent(new NCCityActorPersistedEvent(
                snapshot.Identity.ActorId,
                record.AggregateVersion,
                record.SessionEpoch,
                snapshot.Lifecycle.Status,
                snapshot.Lifecycle.Frozen,
                snapshot.Location.TravelPhase,
                snapshot.Location.ExpectedArrivalAt,
                snapshot.Location.CurrentZone,
                snapshot.Location.DestinationZone,
                snapshot.Identity.ArchetypeId,
                isNarrative));
        });
    }

    private SemaphoreSlim GetMutationGate(NCActorId actorId)
    {
        var hash = actorId.Value.GetHashCode() & int.MaxValue;
        return _mutationGates[hash % _mutationGates.Length];
    }

    private static SemaphoreSlim[] CreateMutationGates()
    {
        var gates = new SemaphoreSlim[MutationGateStripeCount];
        for (var i = 0; i < gates.Length; i++)
            gates[i] = new SemaphoreSlim(1, 1);
        return gates;
    }

    private static NCCityActorIdentity IncrementIdentity(NCCityActorIdentity identity) =>
        identity with { DataVersion = checked(identity.DataVersion + 1) };

    private static bool CannotMutate(NCCityActorSnapshot snapshot) =>
        snapshot.Lifecycle.Frozen ||
        snapshot.Lifecycle.Status is NCCityActorStatus.Dead or NCCityActorStatus.Archived;

    private static bool HasSingleTravelMode(NCCityTravelMode mode)
    {
        var value = (byte) mode;
        return value != 0 && (value & (value - 1)) == 0;
    }

    private static (int AppearanceSeed, int BiographySeed) CreatePresentationSeeds(NCActorId actorId)
    {
        Span<byte> bytes = stackalloc byte[16];
        actorId.Value.TryWriteBytes(bytes);
        return (BitConverter.ToInt32(bytes[..4]), BitConverter.ToInt32(bytes[4..8]));
    }

    private static string ActorKey(NCActorId actorId) => actorId.Value.ToString("D");

    private static NCCityActorMutationStatus MapStatus(NCPersistentMutationStatus status, string? error) =>
        status switch
        {
            NCPersistentMutationStatus.VersionConflict => NCCityActorMutationStatus.VersionConflict,
            NCPersistentMutationStatus.ClockRejected => NCCityActorMutationStatus.ClockUnavailable,
            NCPersistentMutationStatus.DuplicateFailed when
                error?.Contains("version", StringComparison.OrdinalIgnoreCase) == true =>
                NCCityActorMutationStatus.VersionConflict,
            NCPersistentMutationStatus.DuplicateFailed when
                error?.Contains("timestamp", StringComparison.OrdinalIgnoreCase) == true =>
                NCCityActorMutationStatus.ClockUnavailable,
            _ => NCCityActorMutationStatus.StorageFailure,
        };

    private static NCCityActorMutationResult Reject(
        NCActorId actorId,
        NCCityActorMutationStatus status,
        string error) =>
        new(status, actorId, 0, null, error);

    private static NCPhysicalActorCheckpointResult RejectCheckpoint(
        NCActorId actorId,
        NCCityActorMutationStatus status,
        string error) =>
        new(status, actorId, null, null, null, error);

    private sealed record NCCityActorEvent(
        NCActorId ActorId,
        string EventType,
        string AuditSource,
        NCCityActorSnapshot Snapshot);

    private sealed record NCCityActorReceipt(
        NCCityActorSnapshot Snapshot,
        long AggregateVersion,
        NCSessionEpoch SessionEpoch,
        NCActiveWorldTime PersistedAt);

    private sealed record NCPhysicalActorCheckpointReceipt(
        NCCityActorSnapshot Actor,
        long ActorVersion,
        NCCityActorHealthSnapshot Health,
        long HealthVersion,
        NCCityActorInventorySnapshot Inventory,
        long InventoryVersion,
        NCSessionEpoch SessionEpoch,
        NCActiveWorldTime PersistedAt);
}
