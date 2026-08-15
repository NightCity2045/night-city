// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Actors.Domains;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Actors;

/// <summary>
/// Lazy persistent-actor registry. Actors are JSON aggregate snapshots and do not require ECS entities.
/// Writes use per-actor gates plus the shared optimistic/idempotent persistence boundary.
/// </summary>
public sealed partial class NCCityActorRegistrySystem : EntitySystem
{
    public const string AggregateType = "living-city.actor";
    public const int SnapshotFormatVersion = 1;
    private const int MaximumNameLength = 128;
    private const int MaximumAliasCount = 16;
    private const int MaximumStableLocalIdLength = 128;
    private const int MaximumCommitmentCount = 64;
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
    [Dependency] private ITaskManager _taskManager = default!;

    private readonly ConcurrentDictionary<NCActorId, NCCityActorRecord> _cache = [];
    // A fixed stripe set bounds synchronization memory even when the city contains tens of thousands of actors.
    private readonly SemaphoreSlim[] _mutationGates = CreateMutationGates();

    public int CachedActorCount => _cache.Count;

    public bool TryGetCached(NCActorId actorId, out NCCityActorRecord record)
    {
        return _cache.TryGetValue(actorId, out record!);
    }

    public bool Unload(NCActorId actorId)
    {
        return _cache.TryRemove(actorId, out _);
    }

    public async Task<NCCityActorRecord?> GetAsync(NCActorId actorId, bool forceReload = false)
    {
        if (!actorId.IsValid)
            return null;
        if (!forceReload && _cache.TryGetValue(actorId, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(AggregateType, ActorKey(actorId));
        var record = Deserialize(stored, actorId);
        if (record != null)
            record = CacheNewest(record);
        return record;
    }

    /// <summary>
    /// Reads one stable keyset page. Callers can process 10,000 actors without creating entities or retaining all rows.
    /// </summary>
    public async Task<IReadOnlyList<NCCityActorRecord>> GetPageAsync(
        NCActorId? afterActorId,
        int limit,
        bool cache = false)
    {
        return (await GetPageResultAsync(afterActorId, limit, cache)).Records;
    }

    /// <summary>
    /// Returns a continuation based on the last database row, even if that row's payload is corrupt.
    /// This prevents one bad snapshot from truncating a full-city keyset scan.
    /// </summary>
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
            records.Add(record);
            if (cache)
                CacheNewest(record);
        }

        NCActorId? continuation = null;
        if (page.Count > 0 && Guid.TryParse(page[^1].AggregateId, out var lastActorId))
            continuation = new NCActorId(lastActorId);
        return new NCCityActorPage(records, continuation, page.Count == limit);
    }

    public async Task<NCCityActorMutationResult> CreateAsync(NCCityActorCreateRequest request)
    {
        if (!TryValidateCreate(request, out var archetype, out var error) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(
                request.PopulationScopeId == Guid.Empty || string.IsNullOrWhiteSpace(request.StableLocalId)
                    ? default
                    : CreateDeterministicId(request.PopulationScopeId, request.StableLocalId),
                _clock.IsReady ? NCCityActorMutationStatus.Rejected : NCCityActorMutationStatus.ClockUnavailable,
                error ?? "The active-world clock is not ready.");
        }

        var actorId = CreateDeterministicId(request.PopulationScopeId, request.StableLocalId);
        var gate = GetMutationGate(actorId);
        await gate.WaitAsync();
        try
        {
            var eventId = Guid.NewGuid();
            var (appearanceSeed, biographySeed) = CreatePresentationSeeds(actorId);
            var snapshot = new NCCityActorSnapshot(
                new NCCityActorIdentity(
                    actorId,
                    request.FullName.Trim(),
                    request.Aliases.Select(static alias => alias.Trim()).ToArray(),
                    archetype.ID,
                    appearanceSeed,
                    biographySeed,
                    1),
                new NCCityActorLocation(
                    request.HomeZone,
                    default,
                    request.HomeZone,
                    request.WorkZone,
                    NCCityActorTravelPhase.Stationary,
                    archetype.TravelMode,
                    null,
                    now),
                new NCCityActorScheduleSummary(
                    archetype.RoutineProfile.Id,
                    [],
                    string.Empty,
                    string.Empty,
                    NCCityActorAvailability.Available,
                    now),
                new NCCityActorLifecycle(NCCityActorStatus.Active, false, null, null),
                archetype.CognitionTier,
                eventId);

            return await PersistCreate(
                actorId,
                request.CommandId,
                "living-city.actor.create",
                eventId,
                "living-city.actor.created",
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
            Log.Error($"Failed to create persistent actor {actorId}: {exception}");
            return Reject(actorId, NCCityActorMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Creates the actor root and mandatory health/inventory subdomains under one serializable transaction.
    /// A partially initialized persistent person can therefore never become visible to the schedulers.
    /// </summary>
    private async Task<NCCityActorMutationResult> PersistCreate(
        NCActorId actorId,
        Guid commandId,
        string commandType,
        Guid eventId,
        string eventType,
        long expectedVersion,
        NCCityActorSnapshot snapshot,
        Guid correlationId,
        Guid? causationId,
        string auditSource,
        NCSessionEpoch epoch,
        NCActiveWorldTime time)
    {
        var receiptPayload = JsonSerializer.Serialize(
            new NCCityActorReceiptPayload(snapshot, expectedVersion + 1, epoch, time),
            JsonOptions);
        var writes = new List<NCPersistentAggregateWrite>
        {
            new(
                eventId,
                eventType,
                AggregateType,
                ActorKey(actorId),
                expectedVersion,
                SnapshotFormatVersion,
                JsonSerializer.Serialize(snapshot, JsonOptions),
                JsonSerializer.Serialize(
                    new NCCityActorEventPayload(actorId, eventType, auditSource, snapshot),
                    JsonOptions)),
        };
        writes.AddRange(_domains.BuildInitialWrites(actorId, time, auditSource));

        var result = await _database.ApplyNCPersistentBatchMutationAsync(new NCPersistentBatchMutationRequest(
            commandId,
            commandType,
            "actor",
            ActorKey(actorId),
            writes,
            receiptPayload,
            correlationId,
            causationId,
            epoch,
            time,
            []));

        if (result.Status == NCPersistentMutationStatus.Applied)
        {
            var aggregateVersion = result.Aggregates
                .First(value => value.AggregateType == AggregateType && value.AggregateId == ActorKey(actorId))
                .AggregateVersion;
            var record = new NCCityActorRecord(snapshot, aggregateVersion, epoch, time);
            CacheNewest(record);
            PublishPersisted(record);
            return new NCCityActorMutationResult(
                NCCityActorMutationStatus.Applied,
                actorId,
                aggregateVersion,
                record,
                null);
        }

        if (result.Status == NCPersistentMutationStatus.DuplicateCompleted)
        {
            var record = DeserializeReceipt(result.ResultPayload, actorId) ?? await GetAsync(actorId, true);
            var aggregateVersion = result.Aggregates
                .FirstOrDefault(value => value.AggregateType == AggregateType && value.AggregateId == ActorKey(actorId))
                ?.AggregateVersion ?? record?.AggregateVersion ?? 0;
            return new NCCityActorMutationResult(
                NCCityActorMutationStatus.Duplicate,
                actorId,
                aggregateVersion,
                record,
                record == null ? "The completed actor creation receipt is invalid." : null);
        }

        var status = result.Status switch
        {
            NCPersistentMutationStatus.VersionConflict => NCCityActorMutationStatus.VersionConflict,
            NCPersistentMutationStatus.ClockRejected => NCCityActorMutationStatus.ClockUnavailable,
            NCPersistentMutationStatus.DuplicateFailed when
                result.Error?.StartsWith("One or more aggregate versions ") == true =>
                NCCityActorMutationStatus.VersionConflict,
            NCPersistentMutationStatus.DuplicateFailed when
                result.Error?.StartsWith("The batch timestamp ") == true =>
                NCCityActorMutationStatus.ClockUnavailable,
            _ => NCCityActorMutationStatus.StorageFailure,
        };
        return new NCCityActorMutationResult(status, actorId, 0, null, result.Error);
    }

    public async Task<NCCityActorMutationResult> SetLocationAsync(NCCityActorLocationMutationRequest request)
    {
        string? error = null;
        if (!request.ActorId.IsValid || !TryValidateLocation(request, out error))
            return Reject(request.ActorId, NCCityActorMutationStatus.Rejected, error ?? "Invalid actor identity.");

        return await Mutate(
            request.ActorId,
            request.CommandId,
            request.ExpectedVersion,
            request.CorrelationId,
            request.CausationId,
            request.AuditSource,
            "living-city.actor.set-location",
            "living-city.actor.location-changed",
            (current, now, eventId) =>
            {
                if (current.Snapshot.Lifecycle is { Frozen: true } ||
                    current.Snapshot.Lifecycle.Status is NCCityActorStatus.Dead or NCCityActorStatus.Archived)
                {
                    return null;
                }

                return current.Snapshot with
                {
                    Identity = current.Snapshot.Identity with
                    {
                        DataVersion = checked(current.Snapshot.Identity.DataVersion + 1),
                    },
                    Location = current.Snapshot.Location with
                    {
                        CurrentZone = request.CurrentZone,
                        DestinationZone = request.DestinationZone,
                        TravelPhase = request.TravelPhase,
                        TravelMode = request.TravelMode,
                        ExpectedArrivalAt = request.ExpectedArrivalAt,
                        LastKnownLocationAt = now,
                    },
                    Schedule = current.Snapshot.Schedule with
                    {
                        Commitments = (current.Snapshot.Schedule.Commitments ?? [])
                            .Where(commitment => commitment.EndsAt > now)
                            .ToArray(),
                        CurrentActivity = request.CurrentActivity.Trim(),
                        CurrentGoal = request.CurrentGoal.Trim(),
                        Availability = request.Availability,
                        NextUpdateAt = request.NextUpdateAt,
                    },
                    LastAppliedEventId = eventId,
                };
            });
    }

    /// <summary>
    /// Commits the actor root, medical projection and inventory projection as one physical-to-strategic hand-off.
    /// The caller may remove the ECS entity only after this method returns Applied or Duplicate.
    /// </summary>
    public async Task<NCCityActorDematerializationCommitResult> CommitDematerializationAsync(
        NCCityActorDematerializationCommitRequest request)
    {
        const string commandType = "living-city.actor.dematerialize";
        if (!TryValidateDematerialization(request, out var error))
            return RejectDematerialization(request.ActorId, NCCityActorMutationStatus.Rejected, error!);

        try
        {
            var existing = await _database.GetNCPersistentCommandResultAsync(request.CommandId);
            if (existing != null)
                return ReplayDematerialization(request.ActorId, commandType, existing);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to read actor dematerialization receipt {request.CommandId}: {exception}");
            return RejectDematerialization(request.ActorId, NCCityActorMutationStatus.StorageFailure,
                exception.Message);
        }

        if (!_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return RejectDematerialization(request.ActorId, NCCityActorMutationStatus.ClockUnavailable,
                "The active-world clock is not ready.");
        }

        var gate = GetMutationGate(request.ActorId);
        await gate.WaitAsync();
        try
        {
            var current = await GetAsync(request.ActorId, true);
            if (current == null)
            {
                return RejectDematerialization(request.ActorId, NCCityActorMutationStatus.NotFound,
                    "Persistent actor does not exist.");
            }

            if (current.AggregateVersion != request.ExpectedActorVersion)
            {
                return RejectDematerialization(request.ActorId, NCCityActorMutationStatus.VersionConflict,
                    "The actor root changed after the physical capture was requested.");
            }

            var eventId = Guid.NewGuid();
            var next = current.Snapshot with
            {
                Identity = current.Snapshot.Identity with
                {
                    DataVersion = checked(current.Snapshot.Identity.DataVersion + 1),
                },
                Location = current.Snapshot.Location with
                {
                    CurrentZone = request.CurrentZone,
                    LastKnownLocationAt = now,
                },
                Schedule = current.Snapshot.Schedule with
                {
                    Commitments = (current.Snapshot.Schedule.Commitments ?? [])
                        .Where(commitment => commitment.EndsAt > now)
                        .ToArray(),
                },
                LastAppliedEventId = eventId,
            };
            if (!TryNormalizeSnapshot(next, request.ActorId, out next))
            {
                return RejectDematerialization(request.ActorId, NCCityActorMutationStatus.Rejected,
                    "The physical actor produced an invalid strategic root snapshot.");
            }

            var receipt = new NCCityActorDematerializationReceipt(
                next,
                request.ExpectedActorVersion + 1,
                request.Domains.Health,
                request.Domains.ExpectedHealthVersion + 1,
                request.Domains.Inventory,
                request.Domains.ExpectedInventoryVersion + 1,
                epoch,
                now);
            var receiptPayload = JsonSerializer.Serialize(receipt, JsonOptions);
            var writes = new List<NCPersistentAggregateWrite>
            {
                new(
                    eventId,
                    "living-city.actor.dematerialized",
                    AggregateType,
                    ActorKey(request.ActorId),
                    request.ExpectedActorVersion,
                    SnapshotFormatVersion,
                    JsonSerializer.Serialize(next, JsonOptions),
                    JsonSerializer.Serialize(
                        new NCCityActorEventPayload(
                            request.ActorId,
                            "living-city.actor.dematerialized",
                            request.AuditSource,
                            next),
                        JsonOptions)),
            };
            writes.AddRange(_domains.BuildCaptureWrites(request.Domains, request.AuditSource));

            var persistence = await _database.ApplyNCPersistentBatchMutationAsync(
                new NCPersistentBatchMutationRequest(
                    request.CommandId,
                    commandType,
                    "actor",
                    ActorKey(request.ActorId),
                    writes,
                    receiptPayload,
                    request.CorrelationId,
                    request.CausationId,
                    epoch,
                    now,
                    []));

            if (persistence.Status == NCPersistentMutationStatus.Applied)
                return ApplyDematerializationReceipt(receipt, NCCityActorMutationStatus.Applied);
            if (persistence.Status == NCPersistentMutationStatus.DuplicateCompleted)
                return ApplyDematerializationReceipt(persistence.ResultPayload, request.ActorId,
                    NCCityActorMutationStatus.Duplicate);

            var status = persistence.Status switch
            {
                NCPersistentMutationStatus.VersionConflict => NCCityActorMutationStatus.VersionConflict,
                NCPersistentMutationStatus.ClockRejected => NCCityActorMutationStatus.ClockUnavailable,
                NCPersistentMutationStatus.DuplicateFailed when
                    persistence.Error?.StartsWith("One or more aggregate versions ") == true =>
                    NCCityActorMutationStatus.VersionConflict,
                NCPersistentMutationStatus.DuplicateFailed when
                    persistence.Error?.StartsWith("The batch timestamp ") == true =>
                    NCCityActorMutationStatus.ClockUnavailable,
                _ => NCCityActorMutationStatus.StorageFailure,
            };
            return RejectDematerialization(request.ActorId, status,
                persistence.Error ?? "The atomic actor dematerialization batch failed.");
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to commit actor dematerialization for {request.ActorId}: {exception}");
            return RejectDematerialization(request.ActorId, NCCityActorMutationStatus.StorageFailure,
                exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NCCityActorMutationResult> SetStatusAsync(NCCityActorStatusMutationRequest request)
    {
        if (!TryValidateMutationMetadata(
                request.ActorId,
                request.CommandId,
                request.ExpectedVersion,
                request.CorrelationId,
                request.AuditSource,
                out var error) ||
            !Enum.IsDefined(request.Status))
        {
            return Reject(request.ActorId, NCCityActorMutationStatus.Rejected, error ?? "Invalid actor status.");
        }

        return await Mutate(
            request.ActorId,
            request.CommandId,
            request.ExpectedVersion,
            request.CorrelationId,
            request.CausationId,
            request.AuditSource,
            "living-city.actor.set-status",
            "living-city.actor.status-changed",
            (current, now, eventId) =>
            {
                var lifecycle = current.Snapshot.Lifecycle;
                if (lifecycle.Status is NCCityActorStatus.Dead or NCCityActorStatus.Archived)
                    return null;

                var diedAt = request.Status == NCCityActorStatus.Dead ? now : lifecycle.DiedAt;
                var archivedAt = request.Status == NCCityActorStatus.Archived ? now : lifecycle.ArchivedAt;
                return current.Snapshot with
                {
                    Identity = current.Snapshot.Identity with
                    {
                        DataVersion = checked(current.Snapshot.Identity.DataVersion + 1),
                    },
                    Lifecycle = lifecycle with
                    {
                        Status = request.Status,
                        DiedAt = diedAt,
                        ArchivedAt = archivedAt,
                    },
                    LastAppliedEventId = eventId,
                };
            });
    }

    public async Task<NCCityActorMutationResult> SetFrozenAsync(NCCityActorFreezeMutationRequest request)
    {
        if (!TryValidateMutationMetadata(
                request.ActorId,
                request.CommandId,
                request.ExpectedVersion,
                request.CorrelationId,
                request.AuditSource,
                out var error))
        {
            return Reject(request.ActorId, NCCityActorMutationStatus.Rejected, error!);
        }

        return await Mutate(
            request.ActorId,
            request.CommandId,
            request.ExpectedVersion,
            request.CorrelationId,
            request.CausationId,
            request.AuditSource,
            "living-city.actor.set-frozen",
            "living-city.actor.frozen-changed",
            (current, _, eventId) =>
            {
                if (current.Snapshot.Lifecycle.Status is NCCityActorStatus.Dead or NCCityActorStatus.Archived)
                    return null;

                return current.Snapshot with
                {
                    Identity = current.Snapshot.Identity with
                    {
                        DataVersion = checked(current.Snapshot.Identity.DataVersion + 1),
                    },
                    Lifecycle = current.Snapshot.Lifecycle with { Frozen = request.Frozen },
                    LastAppliedEventId = eventId,
                };
            });
    }

    public async Task<NCCityActorMutationResult> AddCommitmentAsync(NCCityActorAddCommitmentRequest request)
    {
        if (!TryValidateMutationMetadata(
                request.ActorId,
                request.CommandId,
                request.ExpectedVersion,
                request.CorrelationId,
                request.AuditSource,
                out var error) ||
            !TryValidateCommitment(request.Commitment, out error))
        {
            return Reject(request.ActorId, NCCityActorMutationStatus.Rejected, error!);
        }

        return await Mutate(
            request.ActorId,
            request.CommandId,
            request.ExpectedVersion,
            request.CorrelationId,
            request.CausationId,
            request.AuditSource,
            "living-city.actor.add-commitment",
            "living-city.actor.commitment-added",
            (current, _, eventId) =>
            {
                var currentCommitments = current.Snapshot.Schedule.Commitments ?? [];
                if (current.Snapshot.Lifecycle is { Frozen: true } ||
                    current.Snapshot.Lifecycle.Status is NCCityActorStatus.Dead or NCCityActorStatus.Archived ||
                    currentCommitments.Length >= MaximumCommitmentCount ||
                    currentCommitments.Any(commitment =>
                        commitment.CommitmentId == request.Commitment.CommitmentId))
                {
                    return null;
                }

                var commitments = currentCommitments
                    .Append(request.Commitment)
                    .OrderBy(static commitment => commitment.StartsAt)
                    .ThenByDescending(static commitment => commitment.Priority)
                    .ThenBy(static commitment => commitment.CommitmentId.Value)
                    .ToArray();
                var nextUpdateAt = request.Commitment.StartsAt < current.Snapshot.Schedule.NextUpdateAt
                    ? request.Commitment.StartsAt
                    : current.Snapshot.Schedule.NextUpdateAt;
                return current.Snapshot with
                {
                    Identity = current.Snapshot.Identity with
                    {
                        DataVersion = checked(current.Snapshot.Identity.DataVersion + 1),
                    },
                    Schedule = current.Snapshot.Schedule with
                    {
                        Commitments = commitments,
                        NextUpdateAt = nextUpdateAt,
                    },
                    LastAppliedEventId = eventId,
                };
            });
    }

    public async Task<NCCityActorMutationResult> RemoveCommitmentAsync(NCCityActorRemoveCommitmentRequest request)
    {
        if (!TryValidateMutationMetadata(
                request.ActorId,
                request.CommandId,
                request.ExpectedVersion,
                request.CorrelationId,
                request.AuditSource,
                out var error) ||
            !request.CommitmentId.IsValid)
        {
            return Reject(request.ActorId, NCCityActorMutationStatus.Rejected,
                error ?? "Invalid commitment identity.");
        }

        return await Mutate(
            request.ActorId,
            request.CommandId,
            request.ExpectedVersion,
            request.CorrelationId,
            request.CausationId,
            request.AuditSource,
            "living-city.actor.remove-commitment",
            "living-city.actor.commitment-removed",
            (current, now, eventId) =>
            {
                var currentCommitments = current.Snapshot.Schedule.Commitments ?? [];
                if (current.Snapshot.Lifecycle is { Frozen: true } ||
                    current.Snapshot.Lifecycle.Status is NCCityActorStatus.Dead or NCCityActorStatus.Archived ||
                    currentCommitments.All(commitment =>
                        commitment.CommitmentId != request.CommitmentId))
                {
                    return null;
                }

                var commitments = currentCommitments
                    .Where(commitment => commitment.CommitmentId != request.CommitmentId)
                    .ToArray();
                return current.Snapshot with
                {
                    Identity = current.Snapshot.Identity with
                    {
                        DataVersion = checked(current.Snapshot.Identity.DataVersion + 1),
                    },
                    Schedule = current.Snapshot.Schedule with
                    {
                        Commitments = commitments,
                        NextUpdateAt = now,
                    },
                    LastAppliedEventId = eventId,
                };
            });
    }

    public static NCActorId CreateDeterministicId(Guid populationScopeId, string stableLocalId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(populationScopeId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableLocalId);
        Span<byte> scope = stackalloc byte[16];
        populationScopeId.TryWriteBytes(scope);
        var local = Encoding.UTF8.GetBytes(stableLocalId.Trim());
        var input = new byte[scope.Length + local.Length];
        scope.CopyTo(input);
        local.CopyTo(input.AsSpan(scope.Length));
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        hash[6] = (byte) ((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte) ((hash[8] & 0x3F) | 0x80);
        return new NCActorId(new Guid(hash[..16]));
    }

    private async Task<NCCityActorMutationResult> Mutate(
        NCActorId actorId,
        Guid commandId,
        long expectedVersion,
        Guid correlationId,
        Guid? causationId,
        string auditSource,
        string commandType,
        string eventType,
        Func<NCCityActorRecord, NCActiveWorldTime, Guid, NCCityActorSnapshot?> transform)
    {
        if (!_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject(actorId, NCCityActorMutationStatus.ClockUnavailable, "The active-world clock is not ready.");

        var gate = GetMutationGate(actorId);
        await gate.WaitAsync();
        try
        {
            var current = await GetAsync(actorId);
            if (current == null)
                return Reject(actorId, NCCityActorMutationStatus.NotFound, "Persistent actor does not exist.");
            if (current.AggregateVersion != expectedVersion)
            {
                // The database checks the command receipt before optimistic versioning. Sending a harmless
                // snapshot on this path lets a retry return its original completed receipt exactly once, while
                // a genuinely stale command is still recorded and rejected as a version conflict.
                return await Persist(
                    actorId,
                    commandId,
                    commandType,
                    Guid.NewGuid(),
                    eventType,
                    expectedVersion,
                    current.Snapshot,
                    correlationId,
                    causationId,
                    auditSource,
                    epoch,
                    now);
            }

            var eventId = Guid.NewGuid();
            var next = transform(current, now, eventId);
            if (next == null)
                return Reject(actorId, NCCityActorMutationStatus.Rejected, "Actor lifecycle rejects this mutation.");

            return await Persist(
                actorId,
                commandId,
                commandType,
                eventId,
                eventType,
                expectedVersion,
                next,
                correlationId,
                causationId,
                auditSource,
                epoch,
                now);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to mutate persistent actor {actorId}: {exception}");
            return Reject(actorId, NCCityActorMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<NCCityActorMutationResult> Persist(
        NCActorId actorId,
        Guid commandId,
        string commandType,
        Guid eventId,
        string eventType,
        long expectedVersion,
        NCCityActorSnapshot snapshot,
        Guid correlationId,
        Guid? causationId,
        string auditSource,
        NCSessionEpoch epoch,
        NCActiveWorldTime time)
    {
        var payload = JsonSerializer.Serialize(snapshot, JsonOptions);
        var receiptPayload = JsonSerializer.Serialize(
            new NCCityActorReceiptPayload(snapshot, expectedVersion + 1, epoch, time),
            JsonOptions);
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
            payload,
            JsonSerializer.Serialize(
                new NCCityActorEventPayload(actorId, eventType, auditSource, snapshot),
                JsonOptions),
            receiptPayload,
            correlationId,
            causationId,
            epoch,
            time,
            []));

        if (result.Status is NCPersistentMutationStatus.Applied)
        {
            var record = new NCCityActorRecord(snapshot, result.AggregateVersion, epoch, time);
            CacheNewest(record);
            PublishPersisted(record);
            return new NCCityActorMutationResult(
                NCCityActorMutationStatus.Applied,
                actorId,
                result.AggregateVersion,
                record,
                null);
        }

        if (result.Status == NCPersistentMutationStatus.DuplicateCompleted)
        {
            var record = DeserializeReceipt(result.ResultPayload, actorId) ?? await GetAsync(actorId, true);
            return new NCCityActorMutationResult(
                NCCityActorMutationStatus.Duplicate,
                actorId,
                result.AggregateVersion,
                record,
                null);
        }

        var status = result.Status switch
        {
            NCPersistentMutationStatus.VersionConflict => NCCityActorMutationStatus.VersionConflict,
            NCPersistentMutationStatus.ClockRejected => NCCityActorMutationStatus.ClockUnavailable,
            NCPersistentMutationStatus.DuplicateFailed when result.Error?.StartsWith("Expected aggregate version ") == true =>
                NCCityActorMutationStatus.VersionConflict,
            NCPersistentMutationStatus.DuplicateFailed when result.Error?.StartsWith("The mutation timestamp ") == true =>
                NCCityActorMutationStatus.ClockUnavailable,
            _ => NCCityActorMutationStatus.StorageFailure,
        };
        return new NCCityActorMutationResult(status, actorId, result.AggregateVersion, null, result.Error);
    }

    private bool TryValidateCreate(
        NCCityActorCreateRequest request,
        out NCCityActorArchetypePrototype archetype,
        out string? error)
    {
        archetype = default!;
        error = null;
        if (request.CommandId == Guid.Empty || request.CorrelationId == Guid.Empty ||
            request.PopulationScopeId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.AuditSource) ||
            request.AuditSource.Length > MaximumAuditSourceLength ||
            string.IsNullOrWhiteSpace(request.StableLocalId) ||
            request.StableLocalId.Length > MaximumStableLocalIdLength ||
            string.IsNullOrWhiteSpace(request.FullName) ||
            request.FullName.Length > MaximumNameLength ||
            request.Aliases == null ||
            request.Aliases.Length > MaximumAliasCount ||
            request.Aliases.Any(static alias => string.IsNullOrWhiteSpace(alias) || alias.Length > MaximumNameLength))
        {
            error = "Actor identity or command metadata is invalid.";
            return false;
        }

        if (!_prototypes.TryIndex(request.Archetype, out NCCityActorArchetypePrototype? loaded) || loaded == null)
        {
            error = $"Unknown actor archetype {request.Archetype}.";
            return false;
        }

        archetype = loaded;

        if (!_zones.TryGetZone(request.HomeZone, out var home) ||
            !archetype.AllowedHomeContexts.Contains(home.ContextSlot))
        {
            error = "Home zone is missing or incompatible with the actor archetype.";
            return false;
        }

        if (request.WorkZone.IsValid &&
            (!_zones.TryGetZone(request.WorkZone, out var work) ||
             !archetype.AllowedWorkContexts.Contains(work.ContextSlot)))
        {
            error = "Work zone is incompatible with the actor archetype.";
            return false;
        }

        return true;
    }

    private bool TryValidateLocation(NCCityActorLocationMutationRequest request, out string? error)
    {
        error = null;
        if (!TryValidateMutationMetadata(
                request.ActorId,
                request.CommandId,
                request.ExpectedVersion,
                request.CorrelationId,
                request.AuditSource,
                out error) ||
            !request.NextUpdateAt.IsValid ||
            request.ExpectedArrivalAt is { IsValid: false } ||
            request.CurrentActivity == null || request.CurrentGoal == null ||
            request.CurrentActivity.Length > MaximumNameLength || request.CurrentGoal.Length > MaximumNameLength ||
            !Enum.IsDefined(request.Availability) ||
            !_zones.TryGetZone(request.CurrentZone, out _))
        {
            error = "Location mutation metadata is invalid.";
            return false;
        }

        if (request.DestinationZone.IsValid && !_zones.TryGetZone(request.DestinationZone, out _))
        {
            error = "Destination city zone does not exist.";
            return false;
        }

        if (request.TravelPhase is NCCityActorTravelPhase.Planned or NCCityActorTravelPhase.InTransit &&
            (!request.DestinationZone.IsValid || !HasSingleTravelMode(request.TravelMode)))
        {
            error = "Planned travel requires a destination and one concrete travel mode.";
            return false;
        }

        return true;
    }

    private bool TryValidateDematerialization(
        NCCityActorDematerializationCommitRequest request,
        out string? error)
    {
        error = null;
        if (!TryValidateMutationMetadata(
                request.ActorId,
                request.CommandId,
                request.ExpectedActorVersion,
                request.CorrelationId,
                request.AuditSource,
                out error) ||
            request.Domains == null || request.Domains.ActorId != request.ActorId ||
            request.Domains.ExpectedHealthVersion < 1 || request.Domains.ExpectedInventoryVersion < 1 ||
            request.Domains.Health.ActorId != request.ActorId ||
            request.Domains.Inventory.ActorId != request.ActorId ||
            !_zones.TryGetZone(request.CurrentZone, out _))
        {
            error = "The actor dematerialization metadata, domain capture, or semantic city zone is invalid.";
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

    private bool TryValidateCommitment(NCCityActorCommitment commitment, out string? error)
    {
        if (!commitment.CommitmentId.IsValid ||
            string.IsNullOrWhiteSpace(commitment.Activity) || commitment.Activity.Length > MaximumNameLength ||
            commitment.Goal == null || commitment.Goal.Length > MaximumNameLength ||
            !commitment.TargetZone.IsValid || !_zones.TryGetZone(commitment.TargetZone, out _) ||
            !commitment.StartsAt.IsValid || !commitment.EndsAt.IsValid ||
            commitment.EndsAt <= commitment.StartsAt ||
            commitment.Priority is < -10000 or > 10000 ||
            !Enum.IsDefined(commitment.Availability))
        {
            error = "Commitment identity, interval, activity, availability, or target zone is invalid.";
            return false;
        }

        error = null;
        return true;
    }

    private static NCCityActorRecord? Deserialize(NCAggregateSnapshotData? stored, NCActorId expectedActorId)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCCityActorSnapshot>(stored.Payload, JsonOptions);
            if (!TryNormalizeSnapshot(snapshot, expectedActorId, out var normalized))
                return null;

            return new NCCityActorRecord(
                normalized,
                stored.AggregateVersion,
                stored.SessionEpoch,
                stored.ActiveWorldTime);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static NCCityActorRecord? DeserializeReceipt(string? payload, NCActorId expectedActorId)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;
        try
        {
            var receipt = JsonSerializer.Deserialize<NCCityActorReceiptPayload>(payload, JsonOptions);
            if (receipt == null || receipt.AggregateVersion < 1 ||
                !TryNormalizeSnapshot(receipt.Snapshot, expectedActorId, out var normalized))
            {
                return null;
            }

            return new NCCityActorRecord(
                normalized,
                receipt.AggregateVersion,
                receipt.SessionEpoch,
                receipt.PersistedAt);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Rejects structurally corrupt payloads and normalizes arrays that older JSON snapshots may omit.
    /// Domain mutations can consequently use the immutable snapshot without defensive world-state guesses.
    /// </summary>
    private static bool TryNormalizeSnapshot(
        NCCityActorSnapshot? snapshot,
        NCActorId expectedActorId,
        out NCCityActorSnapshot normalized)
    {
        normalized = default!;
        if (snapshot?.Identity == null || snapshot.Location == null || snapshot.Schedule == null ||
            snapshot.Lifecycle == null || snapshot.Identity.ActorId != expectedActorId ||
            !snapshot.Identity.ActorId.IsValid || snapshot.Identity.DataVersion < 1 ||
            string.IsNullOrWhiteSpace(snapshot.Identity.FullName) ||
            string.IsNullOrWhiteSpace(snapshot.Identity.ArchetypeId) ||
            string.IsNullOrWhiteSpace(snapshot.Schedule.RoutineProfileId) ||
            !snapshot.Location.CurrentZone.IsValid || !snapshot.Location.HomeZone.IsValid ||
            !snapshot.Location.LastKnownLocationAt.IsValid || !snapshot.Schedule.NextUpdateAt.IsValid ||
            !Enum.IsDefined(snapshot.Location.TravelPhase) ||
            !Enum.IsDefined(snapshot.Schedule.Availability) ||
            !Enum.IsDefined(snapshot.Lifecycle.Status) ||
            !Enum.IsDefined(snapshot.CognitionTier))
        {
            return false;
        }

        var aliases = snapshot.Identity.Aliases ?? [];
        var commitments = snapshot.Schedule.Commitments ?? [];
        if (commitments.Length > MaximumCommitmentCount || commitments.Any(static commitment => commitment == null))
            return false;

        normalized = snapshot with
        {
            Identity = snapshot.Identity with { Aliases = aliases },
            Schedule = snapshot.Schedule with { Commitments = commitments },
        };
        return true;
    }

    private static bool HasSingleTravelMode(Content.Shared._NC.City.Connectivity.NCCityTravelMode travelMode)
    {
        var raw = (byte) travelMode;
        return raw != 0 && (raw & (raw - 1)) == 0;
    }

    private SemaphoreSlim GetMutationGate(NCActorId actorId)
    {
        var index = actorId.Value.GetHashCode() & int.MaxValue;
        return _mutationGates[index % _mutationGates.Length];
    }

    private NCCityActorRecord CacheNewest(NCCityActorRecord candidate)
    {
        return _cache.AddOrUpdate(
            candidate.Snapshot.Identity.ActorId,
            candidate,
            (_, current) => current.AggregateVersion > candidate.AggregateVersion ? current : candidate);
    }

    private static SemaphoreSlim[] CreateMutationGates()
    {
        var gates = new SemaphoreSlim[MutationGateStripeCount];
        for (var i = 0; i < gates.Length; i++)
            gates[i] = new SemaphoreSlim(1, 1);
        return gates;
    }

    private void PublishPersisted(NCCityActorRecord record)
    {
        var actorId = record.Snapshot.Identity.ActorId;
        var nextUpdateAt = record.Snapshot.Schedule.NextUpdateAt;
        var status = record.Snapshot.Lifecycle.Status;
        var frozen = record.Snapshot.Lifecycle.Frozen;
        var travelPhase = record.Snapshot.Location.TravelPhase;
        var expectedArrivalAt = record.Snapshot.Location.ExpectedArrivalAt;
        var currentZone = record.Snapshot.Location.CurrentZone;
        var archetypeId = record.Snapshot.Identity.ArchetypeId;
        var cognitionTier = record.Snapshot.CognitionTier;
        var availability = record.Snapshot.Schedule.Availability;
        _taskManager.RunOnMainThread(() => RaiseLocalEvent(new NCCityActorPersistedEvent(
            actorId,
            record.AggregateVersion,
            record.SessionEpoch,
            nextUpdateAt,
            status,
            frozen,
            travelPhase,
            expectedArrivalAt,
            currentZone,
            archetypeId,
            cognitionTier,
            availability)));
    }

    private static (int AppearanceSeed, int BiographySeed) CreatePresentationSeeds(NCActorId actorId)
    {
        Span<byte> bytes = stackalloc byte[16];
        actorId.Value.TryWriteBytes(bytes);
        return (BitConverter.ToInt32(bytes[..4]), BitConverter.ToInt32(bytes[4..8]));
    }

    private static string ActorKey(NCActorId actorId) => actorId.Value.ToString("D");

    private static NCCityActorMutationResult Reject(
        NCActorId actorId,
        NCCityActorMutationStatus status,
        string error)
    {
        return new NCCityActorMutationResult(status, actorId, 0, null, error);
    }

    private NCCityActorDematerializationCommitResult ReplayDematerialization(
        NCActorId actorId,
        string commandType,
        NCPersistentCommandResultData existing)
    {
        if (existing.CommandType != commandType || existing.ScopeType != "actor" ||
            existing.ScopeId != ActorKey(actorId))
        {
            return RejectDematerialization(actorId, NCCityActorMutationStatus.StorageFailure,
                "The command ID is already owned by another persistent scope or command type.");
        }

        if (existing.Status == NCPersistentMutationStatus.DuplicateCompleted)
        {
            return ApplyDematerializationReceipt(
                existing.ResultPayload,
                actorId,
                NCCityActorMutationStatus.Duplicate);
        }

        var status = existing.Status switch
        {
            NCPersistentMutationStatus.DuplicateFailed when
                existing.Error?.StartsWith("One or more aggregate versions ") == true =>
                NCCityActorMutationStatus.VersionConflict,
            NCPersistentMutationStatus.DuplicateFailed when
                existing.Error?.StartsWith("The batch timestamp ") == true =>
                NCCityActorMutationStatus.ClockUnavailable,
            _ => NCCityActorMutationStatus.StorageFailure,
        };
        return RejectDematerialization(actorId, status,
            existing.Error ?? "The persistent actor dematerialization command is still in progress.");
    }

    private NCCityActorDematerializationCommitResult ApplyDematerializationReceipt(
        string? payload,
        NCActorId actorId,
        NCCityActorMutationStatus status)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return RejectDematerialization(actorId, NCCityActorMutationStatus.StorageFailure,
                "The actor dematerialization receipt is missing.");
        }

        try
        {
            var receipt = JsonSerializer.Deserialize<NCCityActorDematerializationReceipt>(payload, JsonOptions);
            return receipt == null
                ? RejectDematerialization(actorId, NCCityActorMutationStatus.StorageFailure,
                    "The actor dematerialization receipt is invalid.")
                : ApplyDematerializationReceipt(receipt, status);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return RejectDematerialization(actorId, NCCityActorMutationStatus.StorageFailure,
                "The actor dematerialization receipt is unreadable.");
        }
    }

    private NCCityActorDematerializationCommitResult ApplyDematerializationReceipt(
        NCCityActorDematerializationReceipt receipt,
        NCCityActorMutationStatus status)
    {
        var actorId = receipt.Actor.Identity.ActorId;
        if (!actorId.IsValid || receipt.ActorVersion < 1 || receipt.HealthVersion < 1 ||
            receipt.InventoryVersion < 1 || receipt.Health.ActorId != actorId || receipt.Inventory.ActorId != actorId ||
            !TryNormalizeSnapshot(receipt.Actor, actorId, out var normalized))
        {
            return RejectDematerialization(actorId, NCCityActorMutationStatus.StorageFailure,
                "The actor dematerialization receipt failed structural validation.");
        }

        var actor = CacheNewest(new NCCityActorRecord(
            normalized,
            receipt.ActorVersion,
            receipt.SessionEpoch,
            receipt.PersistedAt));
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
        return new NCCityActorDematerializationCommitResult(
            status,
            actorId,
            actor,
            domains.Health,
            domains.Inventory,
            null);
    }

    private static NCCityActorDematerializationCommitResult RejectDematerialization(
        NCActorId actorId,
        NCCityActorMutationStatus status,
        string error)
    {
        return new NCCityActorDematerializationCommitResult(status, actorId, null, null, null, error);
    }

    private sealed record NCCityActorEventPayload(
        NCActorId ActorId,
        string EventType,
        string AuditSource,
        NCCityActorSnapshot Snapshot);

    private sealed record NCCityActorReceiptPayload(
        NCCityActorSnapshot Snapshot,
        long AggregateVersion,
        NCSessionEpoch SessionEpoch,
        NCActiveWorldTime PersistedAt);

    private sealed record NCCityActorDematerializationReceipt(
        NCCityActorSnapshot Actor,
        long ActorVersion,
        NCCityActorHealthSnapshot Health,
        long HealthVersion,
        NCCityActorInventorySnapshot Inventory,
        long InventoryVersion,
        NCSessionEpoch SessionEpoch,
        NCActiveWorldTime PersistedAt);
}
