// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.Medical.Persistence;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Function;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;

namespace Content.Server._NC.LivingCity.Actors.Domains;

/// <summary>
/// Stores bounded actor-domain projections independently from the actor root. A physical capture advances
/// health and inventory together, so an NPC cannot dematerialize with mismatched strategic state.
/// </summary>
public sealed partial class NCCityActorDomainRegistrySystem : EntitySystem
{
    public const string HealthAggregateType = "living-city.actor-health";
    public const string InventoryAggregateType = "living-city.actor-inventory";
    public const int SnapshotFormatVersion = 1;
    private const int MaximumAuditSourceLength = 128;
    private const int MutationGateStripeCount = 64;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCCityActorHealthAdapterSystem _healthAdapter = default!;
    [Dependency] private NCCityActorInventoryAdapterSystem _inventoryAdapter = default!;
    [Dependency] private NCMedicalPersistenceSystem _medical = default!;
    [Dependency] private ITaskManager _taskManager = default!;

    private readonly ConcurrentDictionary<NCActorId, NCCityActorHealthRecord> _healthCache = [];
    private readonly ConcurrentDictionary<NCActorId, NCCityActorInventoryRecord> _inventoryCache = [];
    private readonly SemaphoreSlim[] _mutationGates = CreateMutationGates();

    public bool Unload(NCActorId actorId)
    {
        return _healthCache.TryRemove(actorId, out _) | _inventoryCache.TryRemove(actorId, out _);
    }

    public async Task<NCCityActorHealthRecord?> GetHealthAsync(NCActorId actorId, bool forceReload = false)
    {
        if (!actorId.IsValid)
            return null;
        if (!forceReload && _healthCache.TryGetValue(actorId, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(HealthAggregateType, ActorKey(actorId));
        var record = DeserializeHealth(stored, actorId);
        return record == null ? null : CacheNewest(record);
    }

    public async Task<NCCityActorInventoryRecord?> GetInventoryAsync(NCActorId actorId, bool forceReload = false)
    {
        if (!actorId.IsValid)
            return null;
        if (!forceReload && _inventoryCache.TryGetValue(actorId, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(InventoryAggregateType, ActorKey(actorId));
        var record = DeserializeInventory(stored, actorId);
        return record == null ? null : CacheNewest(record);
    }

    /// <summary>
    /// Produces the two empty subdomain writes committed atomically with a new actor root.
    /// </summary>
    public IReadOnlyList<NCPersistentAggregateWrite> BuildInitialWrites(
        NCActorId actorId,
        NCActiveWorldTime now,
        string auditSource)
    {
        var healthEventId = Guid.NewGuid();
        var inventoryEventId = Guid.NewGuid();
        var health = new NCCityActorHealthSnapshot(
            actorId,
            new NCCityActorHealthSummary(
                NCCityOverallCondition.Stable,
                NCCityBleedingState.None,
                NCCityMobilityState.Mobile,
                NCCityConsciousnessState.Alert,
                [],
                NCCityTreatmentState.None,
                NCCityCyberwareState.None,
                null),
            0,
            null,
            now,
            1,
            healthEventId);
        var inventory = new NCCityActorInventorySnapshot(
            actorId,
            new NCCityActorInventorySummary([], [], [], [], [], string.Empty),
            0,
            now,
            1,
            inventoryEventId);

        return
        [
            CreateHealthWrite(healthEventId, "living-city.actor-health.initialized",
                actorId, 0, health, auditSource),
            CreateInventoryWrite(inventoryEventId, "living-city.actor-inventory.initialized",
                actorId, 0, inventory, auditSource),
        ];
    }

    /// <summary>
    /// Must be entered on the simulation thread. ECS reads finish before the first database await.
    /// </summary>
    public async Task<NCCityActorDomainCaptureResult> CaptureMaterializedAsync(
        NCCityActorDomainCaptureRequest request)
    {
        if (!TryValidateRequest(request, out var error))
            return Reject(request.ActorId, NCCityActorMutationStatus.Rejected, error!);

        NCPersistentCommandResultData? existing;
        try
        {
            existing = await _database.GetNCPersistentCommandResultAsync(request.CommandId);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to read actor-domain command receipt {request.CommandId}: {exception}");
            return Reject(request.ActorId, NCCityActorMutationStatus.StorageFailure, exception.Message);
        }

        if (existing != null)
            return ReplayExisting(request, existing);

        if (!_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject(request.ActorId, NCCityActorMutationStatus.ClockUnavailable,
                "The active-world clock is not ready.");
        if (!_runtime.TryGetEntity(request.ActorId, out var actorEntity) || !Exists(actorEntity) ||
            !_runtime.TryGetActor(actorEntity, out var boundActor) || boundActor != request.ActorId)
        {
            return Reject(request.ActorId, NCCityActorMutationStatus.NotFound,
                "The actor has no current materialized entity binding.");
        }

        var gate = GetMutationGate(request.ActorId);
        await gate.WaitAsync();
        try
        {
            if (!TryCaptureMaterialized(
                    request.ActorId,
                    actorEntity,
                    request.ExpectedHealthVersion,
                    request.ExpectedInventoryVersion,
                    now,
                    out var capture,
                    out error))
            {
                return Reject(request.ActorId, NCCityActorMutationStatus.Rejected,
                    error ?? "The materialized actor domains could not be captured.");
            }

            var receiptPayload = JsonSerializer.Serialize(
                new DomainReceipt(
                    capture.Health,
                    request.ExpectedHealthVersion + 1,
                    capture.Inventory,
                    request.ExpectedInventoryVersion + 1,
                    epoch,
                    now),
                JsonOptions);
            var writes = BuildCaptureWrites(capture, request.AuditSource);
            var persistence = await _database.ApplyNCPersistentBatchMutationAsync(
                new NCPersistentBatchMutationRequest(
                    request.CommandId,
                    "living-city.actor.capture-domains",
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
                return ApplyReceipt(receiptPayload, NCCityActorMutationStatus.Applied, actorEntity);
            if (persistence.Status == NCPersistentMutationStatus.DuplicateCompleted)
                return ApplyReceipt(persistence.ResultPayload, NCCityActorMutationStatus.Duplicate, actorEntity);

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
            return Reject(request.ActorId, status, persistence.Error ?? "The actor domain batch failed.");
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to capture persistent actor domains for {request.ActorId}: {exception}");
            return Reject(request.ActorId, NCCityActorMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    public bool TryRestoreHealth(NCActorId actorId, EntityUid actorEntity, NCCityActorHealthRecord record)
    {
        var snapshot = record.Snapshot;
        if (!actorId.IsValid || snapshot.ActorId != actorId ||
            !_runtime.TryGetActor(actorEntity, out var boundActor) || boundActor != actorId ||
            snapshot.MedicalSnapshotVersion < 1 || string.IsNullOrWhiteSpace(snapshot.MedicalStatePayload))
        {
            return false;
        }

        return _medical.TryRestorePersistentActorState(
            actorEntity,
            actorId,
            record.AggregateVersion,
            snapshot.MedicalSnapshotVersion,
            snapshot.MedicalStatePayload);
    }

    /// <summary>
    /// Captures both physical domains without awaiting or touching storage. Callers must invoke this on the
    /// simulation thread and persist the returned immutable value before deleting the entity.
    /// </summary>
    public bool TryCaptureMaterialized(
        NCActorId actorId,
        EntityUid actorEntity,
        long expectedHealthVersion,
        long expectedInventoryVersion,
        NCActiveWorldTime now,
        out NCCityActorDomainCapture capture,
        out string? error)
    {
        capture = default!;
        error = null;
        if (!actorId.IsValid || expectedHealthVersion < 1 || expectedInventoryVersion < 1 || !now.IsValid ||
            !_runtime.TryGetEntity(actorId, out var boundEntity) || boundEntity != actorEntity ||
            !_runtime.TryGetActor(actorEntity, out var boundActor) || boundActor != actorId || !Exists(actorEntity))
        {
            error = "The actor domain capture no longer owns a valid materialized binding.";
            return false;
        }

        var healthEventId = Guid.NewGuid();
        var inventoryEventId = Guid.NewGuid();
        if (!_healthAdapter.TryCapture(
                actorEntity,
                actorId,
                expectedHealthVersion,
                now,
                healthEventId,
                out var health,
                out error) ||
            !_inventoryAdapter.TryCapture(
                actorEntity,
                actorId,
                now,
                inventoryEventId,
                out var inventory,
                out error))
        {
            return false;
        }

        capture = new NCCityActorDomainCapture(
            actorId,
            expectedHealthVersion,
            health,
            expectedInventoryVersion,
            inventory,
            Comp<NCMedicalBodyComponent>(actorEntity).MedicalRevision,
            Comp<NCBodyFunctionComponent>(actorEntity).Revision);
        return true;
    }

    /// <summary>
    /// Compares bounded authoritative revision tokens after an asynchronous commit, avoiding a second serialization
    /// whose capture timestamps would differ even when gameplay state did not.
    /// </summary>
    public bool HasPhysicalStateChanged(EntityUid actorEntity, NCCityActorDomainCapture capture)
    {
        return !Exists(actorEntity) ||
               !TryComp<NCMedicalBodyComponent>(actorEntity, out var medical) ||
               !TryComp<NCBodyFunctionComponent>(actorEntity, out var function) ||
               !TryComp<NCCityActorInventoryAdapterComponent>(actorEntity, out var inventory) ||
               medical.MedicalRevision != capture.MedicalRevision ||
               function.Revision != capture.BodyFunctionRevision ||
               inventory.AuthorityVersion != capture.Inventory.SourceAuthorityVersion;
    }

    /// <summary>
    /// Builds domain writes for a larger atomic actor transaction. No mutable ECS state is retained here.
    /// </summary>
    public IReadOnlyList<NCPersistentAggregateWrite> BuildCaptureWrites(
        NCCityActorDomainCapture capture,
        string auditSource)
    {
        return
        [
            CreateHealthWrite(capture.Health.LastAppliedEventId, "living-city.actor-health.captured",
                capture.ActorId, capture.ExpectedHealthVersion, capture.Health, auditSource),
            CreateInventoryWrite(capture.Inventory.LastAppliedEventId, "living-city.actor-inventory.captured",
                capture.ActorId, capture.ExpectedInventoryVersion, capture.Inventory, auditSource),
        ];
    }

    /// <summary>
    /// Advances the read-through caches only after the enclosing database transaction is confirmed.
    /// </summary>
    public (NCCityActorHealthRecord Health, NCCityActorInventoryRecord Inventory) ApplyCommittedCapture(
        NCCityActorDomainCapture capture,
        NCSessionEpoch epoch,
        NCActiveWorldTime persistedAt)
    {
        var health = CacheNewest(new NCCityActorHealthRecord(
            capture.Health,
            capture.ExpectedHealthVersion + 1,
            epoch,
            persistedAt));
        var inventory = CacheNewest(new NCCityActorInventoryRecord(
            capture.Inventory,
            capture.ExpectedInventoryVersion + 1,
            epoch,
            persistedAt));
        return (health, inventory);
    }

    /// <summary>
    /// Keeps a retained physical entity on the newly committed medical revision after an atomic hand-off attempt.
    /// </summary>
    public bool TrySetMaterializedHealthRevision(NCActorId actorId, EntityUid actorEntity, long revision)
    {
        if (!actorId.IsValid || revision < 1 || !Exists(actorEntity) ||
            !_runtime.TryGetActor(actorEntity, out var boundActor) || boundActor != actorId)
        {
            return false;
        }

        _medical.SetPersistentActorRevision(actorEntity, actorId, revision);
        return true;
    }

    private NCCityActorDomainCaptureResult ApplyReceipt(
        string? payload,
        NCCityActorMutationStatus status,
        EntityUid? actorEntity)
    {
        var receipt = DeserializeReceipt(payload);
        if (receipt == null)
            return Reject(default, NCCityActorMutationStatus.StorageFailure,
                "The actor domain command receipt is invalid.");

        var health = CacheNewest(new NCCityActorHealthRecord(
            receipt.Health,
            receipt.HealthVersion,
            receipt.SessionEpoch,
            receipt.PersistedAt));
        var inventory = CacheNewest(new NCCityActorInventoryRecord(
            receipt.Inventory,
            receipt.InventoryVersion,
            receipt.SessionEpoch,
            receipt.PersistedAt));
        if (actorEntity is { } materialized)
        {
            _taskManager.RunOnMainThread(() =>
            {
                if (Exists(materialized) && _runtime.TryGetActor(materialized, out var actorId) &&
                    actorId == health.Snapshot.ActorId)
                {
                    _medical.SetPersistentActorRevision(materialized, actorId, health.AggregateVersion);
                }
            });
        }

        return new NCCityActorDomainCaptureResult(
            status,
            health.Snapshot.ActorId,
            health,
            inventory,
            null);
    }

    private NCCityActorDomainCaptureResult ReplayExisting(
        NCCityActorDomainCaptureRequest request,
        NCPersistentCommandResultData existing)
    {
        if (existing.CommandType != "living-city.actor.capture-domains" || existing.ScopeType != "actor" ||
            existing.ScopeId != ActorKey(request.ActorId))
        {
            return Reject(request.ActorId, NCCityActorMutationStatus.StorageFailure,
                "The command ID is already owned by another persistent scope or command type.");
        }

        if (existing.Status == NCPersistentMutationStatus.DuplicateCompleted)
        {
            EntityUid? materialized = _runtime.TryGetEntity(request.ActorId, out var entity) && Exists(entity)
                ? entity
                : null;
            return ApplyReceipt(existing.ResultPayload, NCCityActorMutationStatus.Duplicate, materialized);
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
        return Reject(request.ActorId, status,
            existing.Error ?? "The persistent actor-domain command is still in progress.");
    }

    private static NCPersistentAggregateWrite CreateHealthWrite(
        Guid eventId,
        string eventType,
        NCActorId actorId,
        long expectedVersion,
        NCCityActorHealthSnapshot snapshot,
        string auditSource)
    {
        // The authoritative snapshot and idempotency receipt retain the opaque medical graph. The append-only event
        // carries only its bounded strategic projection to avoid multiplying a large medical payload in the journal.
        return CreateWrite(
            eventId,
            eventType,
            HealthAggregateType,
            actorId,
            expectedVersion,
            snapshot,
            new HealthDomainEvent(
                actorId,
                eventType,
                auditSource,
                snapshot.Summary,
                snapshot.MedicalSnapshotVersion,
                snapshot.MedicalStatePayload != null));
    }

    private static NCPersistentAggregateWrite CreateInventoryWrite(
        Guid eventId,
        string eventType,
        NCActorId actorId,
        long expectedVersion,
        NCCityActorInventorySnapshot snapshot,
        string auditSource)
    {
        return CreateWrite(
            eventId,
            eventType,
            InventoryAggregateType,
            actorId,
            expectedVersion,
            snapshot,
            new DomainEvent<NCCityActorInventorySnapshot>(actorId, eventType, auditSource, snapshot));
    }

    private static NCPersistentAggregateWrite CreateWrite<TSnapshot, TEvent>(
        Guid eventId,
        string eventType,
        string aggregateType,
        NCActorId actorId,
        long expectedVersion,
        TSnapshot snapshot,
        TEvent domainEvent)
    {
        return new NCPersistentAggregateWrite(
            eventId,
            eventType,
            aggregateType,
            ActorKey(actorId),
            expectedVersion,
            SnapshotFormatVersion,
            JsonSerializer.Serialize(snapshot, JsonOptions),
            JsonSerializer.Serialize(domainEvent, JsonOptions));
    }

    private static bool TryValidateRequest(NCCityActorDomainCaptureRequest request, out string? error)
    {
        if (request.CommandId == Guid.Empty || !request.ActorId.IsValid ||
            request.ExpectedHealthVersion < 1 || request.ExpectedInventoryVersion < 1 ||
            request.CorrelationId == Guid.Empty || string.IsNullOrWhiteSpace(request.AuditSource) ||
            request.AuditSource.Length > MaximumAuditSourceLength)
        {
            error = "The actor domain capture metadata is invalid.";
            return false;
        }

        error = null;
        return true;
    }

    private static NCCityActorHealthRecord? DeserializeHealth(NCAggregateSnapshotData? stored, NCActorId actorId)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCCityActorHealthSnapshot>(stored.Payload, JsonOptions);
            return ValidHealth(snapshot, actorId)
                ? new NCCityActorHealthRecord(snapshot!, stored.AggregateVersion, stored.SessionEpoch,
                    stored.ActiveWorldTime)
                : null;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static NCCityActorInventoryRecord? DeserializeInventory(
        NCAggregateSnapshotData? stored,
        NCActorId actorId)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCCityActorInventorySnapshot>(stored.Payload, JsonOptions);
            return ValidInventory(snapshot, actorId)
                ? new NCCityActorInventoryRecord(snapshot!, stored.AggregateVersion, stored.SessionEpoch,
                    stored.ActiveWorldTime)
                : null;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static DomainReceipt? DeserializeReceipt(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;
        try
        {
            var receipt = JsonSerializer.Deserialize<DomainReceipt>(payload, JsonOptions);
            return receipt != null && receipt.HealthVersion >= 1 && receipt.InventoryVersion >= 1 &&
                   receipt.Health.ActorId == receipt.Inventory.ActorId &&
                   ValidHealth(receipt.Health, receipt.Health.ActorId) &&
                   ValidInventory(receipt.Inventory, receipt.Inventory.ActorId)
                ? receipt
                : null;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool ValidHealth(NCCityActorHealthSnapshot? snapshot, NCActorId actorId)
    {
        return snapshot != null && actorId.IsValid && snapshot.ActorId == actorId && snapshot.DataVersion == 1 &&
               snapshot.CapturedAt.IsValid && snapshot.LastAppliedEventId != Guid.Empty &&
               snapshot.Summary != null && snapshot.Summary.CriticalWoundIds != null &&
               snapshot.Summary.CriticalWoundIds.Length <= 64 &&
               (snapshot.MedicalStatePayload == null
                   ? snapshot.MedicalSnapshotVersion == 0
                   : snapshot.MedicalSnapshotVersion >= 1 &&
                     snapshot.MedicalStatePayload.Length <= NCMedicalCharacterSnapshot.MaximumPayloadLength);
    }

    private static bool ValidInventory(NCCityActorInventorySnapshot? snapshot, NCActorId actorId)
    {
        if (snapshot == null || !actorId.IsValid || snapshot.ActorId != actorId || snapshot.DataVersion != 1 ||
            snapshot.SourceAuthorityVersion < 0 || !snapshot.CapturedAt.IsValid ||
            snapshot.LastAppliedEventId == Guid.Empty || snapshot.Summary == null ||
            snapshot.Summary.UniqueItems == null || snapshot.Summary.Weapons == null ||
            snapshot.Summary.Documents == null || snapshot.Summary.Credentials == null ||
            snapshot.Summary.Commodities == null || snapshot.Summary.EquippedLoadoutId == null ||
            snapshot.Summary.EquippedLoadoutId.Length > 128)
        {
            return false;
        }

        return snapshot.Summary.UniqueItems.Length <= 512 && snapshot.Summary.Weapons.Length <= 512 &&
               snapshot.Summary.Documents.Length <= 512 && snapshot.Summary.Credentials.Length <= 512 &&
               snapshot.Summary.Commodities.Length <= 128 &&
               snapshot.Summary.UniqueItems.All(static id => id.IsValid) &&
               snapshot.Summary.Weapons.All(static id => id.IsValid) &&
               snapshot.Summary.Documents.All(static id => id.IsValid) &&
               snapshot.Summary.Credentials.All(static id => id.IsValid) &&
               snapshot.Summary.Commodities.All(static bundle => bundle != null &&
                   !string.IsNullOrWhiteSpace(bundle.CommodityId) && bundle.CommodityId.Length <= 128 &&
                   bundle.Units > 0);
    }

    private NCCityActorHealthRecord CacheNewest(NCCityActorHealthRecord candidate)
    {
        return _healthCache.AddOrUpdate(candidate.Snapshot.ActorId, candidate,
            (_, current) => current.AggregateVersion > candidate.AggregateVersion ? current : candidate);
    }

    private NCCityActorInventoryRecord CacheNewest(NCCityActorInventoryRecord candidate)
    {
        return _inventoryCache.AddOrUpdate(candidate.Snapshot.ActorId, candidate,
            (_, current) => current.AggregateVersion > candidate.AggregateVersion ? current : candidate);
    }

    private SemaphoreSlim GetMutationGate(NCActorId actorId)
    {
        var index = actorId.Value.GetHashCode() & int.MaxValue;
        return _mutationGates[index % _mutationGates.Length];
    }

    private static SemaphoreSlim[] CreateMutationGates()
    {
        var gates = new SemaphoreSlim[MutationGateStripeCount];
        for (var i = 0; i < gates.Length; i++)
            gates[i] = new SemaphoreSlim(1, 1);
        return gates;
    }

    private static string ActorKey(NCActorId actorId) => actorId.Value.ToString("D");

    private static NCCityActorDomainCaptureResult Reject(
        NCActorId actorId,
        NCCityActorMutationStatus status,
        string error)
    {
        return new NCCityActorDomainCaptureResult(status, actorId, null, null, error);
    }

    private sealed record DomainEvent<T>(NCActorId ActorId, string EventType, string AuditSource, T Snapshot);

    private sealed record HealthDomainEvent(
        NCActorId ActorId,
        string EventType,
        string AuditSource,
        NCCityActorHealthSummary Summary,
        ushort MedicalSnapshotVersion,
        bool HasMedicalStatePayload);

    private sealed record DomainReceipt(
        NCCityActorHealthSnapshot Health,
        long HealthVersion,
        NCCityActorInventorySnapshot Inventory,
        long InventoryVersion,
        NCSessionEpoch SessionEpoch,
        NCActiveWorldTime PersistedAt);
}
