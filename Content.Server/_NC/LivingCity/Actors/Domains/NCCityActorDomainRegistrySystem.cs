// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.Medical.Persistence;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Function;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Actors.Domains;

/// <summary>
/// Stores bounded actor-domain projections independently from the actor root. Round checkpoints advance health and
/// inventory together so a persistent resident is restored from one coherent physical state.
/// </summary>
public sealed partial class NCCityActorDomainRegistrySystem : EntitySystem
{
    public const string HealthAggregateType = "living-city.actor-health";
    public const string InventoryAggregateType = "living-city.actor-inventory";
    public const int SnapshotFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCCityActorHealthAdapterSystem _healthAdapter = default!;
    [Dependency] private NCCityActorInventoryAdapterSystem _inventoryAdapter = default!;
    [Dependency] private NCMedicalPersistenceSystem _medical = default!;

    private readonly ConcurrentDictionary<NCActorId, NCCityActorHealthRecord> _healthCache = [];
    private readonly ConcurrentDictionary<NCActorId, NCCityActorInventoryRecord> _inventoryCache = [];

    public bool Unload(NCActorId actorId)
    {
        return _healthCache.TryRemove(actorId, out _) | _inventoryCache.TryRemove(actorId, out _);
    }

    public bool TryGetCached(
        NCActorId actorId,
        out NCCityActorHealthRecord health,
        out NCCityActorInventoryRecord inventory)
    {
        health = default!;
        inventory = default!;
        if (!_healthCache.TryGetValue(actorId, out var cachedHealth) || cachedHealth == null ||
            !_inventoryCache.TryGetValue(actorId, out var cachedInventory) || cachedInventory == null)
        {
            return false;
        }
        health = cachedHealth;
        inventory = cachedInventory;
        return true;
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
            new NCCityActorInventorySummary([], [], [], [], [], [], string.Empty),
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
    /// Captures both physical domains without awaiting or touching storage. Callers invoke this on the simulation
    /// thread and persist the immutable value as part of a round checkpoint.
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
            var snapshot = NormalizeInventory(
                JsonSerializer.Deserialize<NCCityActorInventorySnapshot>(stored.Payload, JsonOptions));
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

    private static NCCityActorInventorySnapshot? NormalizeInventory(NCCityActorInventorySnapshot? snapshot)
    {
        if (snapshot?.Summary == null || snapshot.Summary.InstalledCyberware != null)
            return snapshot;
        return snapshot with
        {
            Summary = snapshot.Summary with { InstalledCyberware = [] },
        };
    }

    private static bool ValidInventory(NCCityActorInventorySnapshot? snapshot, NCActorId actorId)
    {
        if (snapshot == null || !actorId.IsValid || snapshot.ActorId != actorId || snapshot.DataVersion != 1 ||
            snapshot.SourceAuthorityVersion < 0 || !snapshot.CapturedAt.IsValid ||
            snapshot.LastAppliedEventId == Guid.Empty || snapshot.Summary == null ||
            snapshot.Summary.UniqueItems == null || snapshot.Summary.Weapons == null ||
            snapshot.Summary.Documents == null || snapshot.Summary.Credentials == null ||
            snapshot.Summary.Commodities == null || snapshot.Summary.InstalledCyberware == null ||
            snapshot.Summary.EquippedLoadoutId == null ||
            snapshot.Summary.EquippedLoadoutId.Length > 128)
        {
            return false;
        }

        return snapshot.Summary.UniqueItems.Length <= 512 && snapshot.Summary.Weapons.Length <= 512 &&
               snapshot.Summary.Documents.Length <= 512 && snapshot.Summary.Credentials.Length <= 512 &&
               snapshot.Summary.Commodities.Length <= 128 && snapshot.Summary.InstalledCyberware.Length <= 512 &&
               snapshot.Summary.UniqueItems.All(static id => id.IsValid) &&
               snapshot.Summary.InstalledCyberware.All(static id => id.IsValid) &&
               snapshot.Summary.InstalledCyberware.Distinct().Count() ==
               snapshot.Summary.InstalledCyberware.Length &&
               snapshot.Summary.InstalledCyberware.All(snapshot.Summary.UniqueItems.Contains) &&
               snapshot.Summary.Weapons.All(static id => id.IsValid) &&
               snapshot.Summary.Documents.All(static id => id.IsValid) &&
               snapshot.Summary.Credentials.All(static id => id.IsValid) &&
               snapshot.Summary.Commodities.All(static bundle => bundle != null &&
                   bundle.BatchId.IsValid &&
                   !string.IsNullOrWhiteSpace(bundle.CommodityId) && bundle.CommodityId.Length <= 128 &&
                   bundle.Units > 0) && snapshot.Summary.Commodities.Select(static bundle => bundle.BatchId)
                   .Distinct().Count() == snapshot.Summary.Commodities.Length;
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

    private static string ActorKey(NCActorId actorId) => actorId.Value.ToString("D");

    private sealed record DomainEvent<T>(NCActorId ActorId, string EventType, string AuditSource, T Snapshot);

    private sealed record HealthDomainEvent(
        NCActorId ActorId,
        string EventType,
        string AuditSource,
        NCCityActorHealthSummary Summary,
        ushort MedicalSnapshotVersion,
        bool HasMedicalStatePayload);

}
