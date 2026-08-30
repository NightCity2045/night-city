// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Text.Json;
using Content.Server.Database;
using Content.Server._NC.City.Connectivity;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Market;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Market;

/// <summary>
/// Persistent shipment lifecycle. Loading and delivery reuse canonical asset transfers, while this aggregate owns
/// carrier, semantic route, active-time ETA, risk, handoff, and proof of delivery.
/// </summary>
public sealed partial class NCShipmentSystem : EntitySystem
{
    public const string AggregateType = "living-city.shipment";
    private const int SnapshotFormatVersion = 1;
    private const int RecoveryPageSize = 100;
    private const int MaximumRecoveredShipments = 10_000;
    private const int MaximumAdvancesPerFrame = 8;
    private static readonly ProtoId<NCMarketPolicyPrototype> DefaultPolicy = "Default";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPartyResolverSystem _parties = default!;
    [Dependency] private NCEconomicAssetRegistrySystem _assets = default!;
    [Dependency] private NCCityConnectivitySystem _city = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    private readonly ConcurrentDictionary<NCShipmentId, NCShipmentRecord> _cache = [];
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly PriorityQueue<NCShipmentId, long> _due = new();
    private readonly HashSet<NCShipmentId> _queued = [];
    private readonly HashSet<NCShipmentId> _processing = [];
    private readonly HashSet<Task> _inFlight = [];
    private bool _active;
    private int _generation;

    public int CachedShipmentCount => _cache.Count;
    public int ScheduledShipmentCount => _queued.Count;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnSessionClosing);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(OnSessionClosed);
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(OnClockFailed);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_active || !_clock.TryGetCurrentTime(out _, out var now))
            return;
        var count = 0;
        while (count < MaximumAdvancesPerFrame && _due.TryPeek(out var shipmentId, out var ticks) &&
               ticks <= now.Ticks)
        {
            _due.Dequeue();
            _queued.Remove(shipmentId);
            if (_processing.Add(shipmentId))
                Track(AdvanceScheduledAsync(shipmentId, _generation));
            count++;
        }
    }

    public async Task<NCShipmentRecord?> GetAsync(NCShipmentId id, bool forceReload = false)
    {
        if (!id.IsValid)
            return null;
        if (!forceReload && _cache.TryGetValue(id, out var cached))
            return cached;
        var stored = await _database.GetNCAggregateSnapshotAsync(AggregateType, id.ToString());
        var record = Deserialize(stored, id);
        return record == null ? null : _cache.AddOrUpdate(id, record,
            (_, current) => current.AggregateVersion > record.AggregateVersion ? current : record);
    }

    public async Task<NCShipmentMutationResult> CreateAsync(NCCreateShipmentRequest request)
    {
        if (!TryPolicy(out var policy) || !Valid(request, policy) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject(_clock.IsReady ? NCMarketMutationStatus.Rejected : NCMarketMutationStatus.ClockUnavailable,
                "The shipment request, policy, or active-world clock is invalid.");
        if (request.Initiator != request.Shipper ||
            !(await _parties.ResolveAsync(request.Shipper, true)).IsResolved ||
            !(await _parties.ResolveAsync(request.Recipient, true)).IsResolved ||
            !(await _parties.ResolveAsync(request.Carrier, true)).IsResolved)
            return Reject(NCMarketMutationStatus.PartyUnavailable,
                "The shipper, recipient, carrier, or initiator is unavailable.");

        var gate = Gate(request.ShipmentId.Value);
        await gate.WaitAsync();
        try
        {
            var existing = await GetAsync(request.ShipmentId, true);
            if (existing != null)
                return Same(existing.Snapshot, request)
                    ? new(NCMarketMutationStatus.Duplicate, existing, null)
                    : Reject(NCMarketMutationStatus.Rejected,
                        "The shipment identity belongs to different immutable data.");
            var origin = await _assets.GetStockNodeAsync(request.OriginStockNodeId, true);
            var destination = await _assets.GetStockNodeAsync(request.DestinationStockNodeId, true);
            if (origin == null || destination == null || origin.Snapshot.Owner != request.Shipper ||
                destination.Snapshot.Owner != request.Recipient || !origin.Snapshot.AllowsWithdrawals ||
                !destination.Snapshot.AcceptsDeposits || !await ValidateAssetsAsync(request, origin))
                return Reject(NCMarketMutationStatus.Rejected,
                    "The shipment stock nodes, ownership, or assets are invalid.");
            var transitId = NCPersistentIdentity.StockNode(request.Shipper, $"shipment.{request.ShipmentId}");
            var transit = await EnsureTransitStockAsync(request, transitId);
            if (transit == null)
                return Reject(NCMarketMutationStatus.StorageFailure,
                    "The shipment transit stock node could not be created.");
            var route = BuildRoute(request.OriginZone, request.DestinationZone, request.TravelMode, policy);
            if (route == null)
                return Reject(NCMarketMutationStatus.Rejected,
                    "No enabled city route exists for this shipment.");
            var risk = Math.Clamp(route.Sum(leg => leg.RiskBasisPoints), 0, 10_000);
            var eventId = Guid.NewGuid();
            var snapshot = new NCShipmentSnapshot(
                request.ShipmentId,
                request.Shipper,
                request.Recipient,
                request.Carrier,
                request.OriginStockNodeId,
                transitId,
                request.DestinationStockNodeId,
                request.OriginZone,
                request.DestinationZone,
                request.TravelMode,
                request.Assets.OrderBy(asset => asset.Kind).ThenBy(asset => asset.AssetId).ToArray(),
                route,
                0,
                risk,
                request.HandoffKind,
                NCShipmentStatus.Loading,
                null,
                null,
                null,
                now,
                now,
                1,
                eventId);
            var persisted = await Persist(
                request.CommandId,
                "living-city.shipment.create",
                null,
                snapshot,
                eventId,
                "living-city.shipment.created",
                request.CorrelationId,
                request.CausationId,
                request.AuditSource,
                epoch,
                now);
            if (persisted.Status is not (NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted))
                return Reject(Map(persisted.Status), persisted.Error ?? "The shipment was not committed.");
        }
        finally
        {
            gate.Release();
        }

        var loaded = await GetAsync(request.ShipmentId, true);
        if (loaded == null)
            return Reject(NCMarketMutationStatus.StorageFailure, "The committed shipment could not be reloaded.");
        var load = await LoadAsync(loaded);
        return load;
    }

    private async Task<NCShipmentMutationResult> LoadAsync(NCShipmentRecord initial)
    {
        var shipment = await GetAsync(initial.Snapshot.ShipmentId, true);
        if (shipment == null)
            return Reject(NCMarketMutationStatus.NotFound, "The shipment does not exist.");
        if (shipment.Snapshot.Status == NCShipmentStatus.InTransit)
            return new(NCMarketMutationStatus.Duplicate, shipment, null);
        if (shipment.Snapshot.Status != NCShipmentStatus.Loading)
            return Reject(NCMarketMutationStatus.Rejected, "The shipment is not loading.");

        for (var index = 0; index < shipment.Snapshot.Assets.Count; index++)
        {
            var asset = shipment.Snapshot.Assets[index];
            if (await IsAtNodeAsync(asset, shipment.Snapshot.TransitStockNodeId, shipment.Snapshot.Shipper))
                continue;
            var result = await TransferAsync(
                shipment.Snapshot,
                asset,
                index,
                shipment.Snapshot.OriginStockNodeId,
                shipment.Snapshot.TransitStockNodeId,
                shipment.Snapshot.Shipper,
                shipment.Snapshot.Shipper,
                "load");
            if (!result)
                return Reject(NCMarketMutationStatus.VersionConflict,
                    "Shipment loading stopped at a conflicting asset; recovery will retry safely.");
        }

        if (!_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject(NCMarketMutationStatus.ClockUnavailable, "The active-world clock stopped during loading.");
        shipment = await GetAsync(shipment.Snapshot.ShipmentId, true);
        if (shipment == null)
            return Reject(NCMarketMutationStatus.StorageFailure, "The shipment disappeared after loading.");
        var firstDuration = shipment.Snapshot.Route[0].ExpectedDuration;
        var eventId = Guid.NewGuid();
        var next = shipment.Snapshot with
        {
            Status = NCShipmentStatus.InTransit,
            CurrentLeg = 0,
            ExpectedArrivalAt = now.Advance(firstDuration),
            UpdatedAt = now,
            DataVersion = checked(shipment.Snapshot.DataVersion + 1),
            LastAppliedEventId = eventId,
        };
        var persisted = await Persist(
            NCPersistentIdentity.Command("shipment", "depart", shipment.Snapshot.ShipmentId.ToString()),
            "living-city.shipment.depart",
            shipment,
            next,
            eventId,
            "living-city.shipment.departed",
            NCPersistentIdentity.Correlation("shipment", "depart", shipment.Snapshot.ShipmentId.ToString()),
            shipment.Snapshot.LastAppliedEventId,
            "shipment-runtime",
            epoch,
            now);
        if (persisted.Status is not (NCPersistentMutationStatus.Applied or
            NCPersistentMutationStatus.DuplicateCompleted))
            return Reject(Map(persisted.Status), persisted.Error ?? "Shipment departure was not committed.");
        var loaded = await GetAsync(shipment.Snapshot.ShipmentId, true);
        if (loaded == null)
            return Reject(NCMarketMutationStatus.StorageFailure, "The departed shipment could not be reloaded.");
        Enqueue(loaded.Snapshot.ShipmentId, loaded.Snapshot.ExpectedArrivalAt!.Value);
        return new(persisted.Status == NCPersistentMutationStatus.Applied
            ? NCMarketMutationStatus.Applied
            : NCMarketMutationStatus.Duplicate, loaded, null);
    }

    private async Task AdvanceScheduledAsync(NCShipmentId shipmentId, int generation)
    {
        try
        {
            if (!_active || generation != _generation || !_clock.TryGetCurrentTime(out var epoch, out var now))
                return;
            var result = await AdvanceAsync(shipmentId, epoch, now);
            if ((result.Status is NCMarketMutationStatus.VersionConflict or NCMarketMutationStatus.StorageFailure) &&
                _active && generation == _generation)
                Enqueue(shipmentId, now.Advance(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            _processing.Remove(shipmentId);
        }
    }

    private async Task<NCShipmentMutationResult> AdvanceAsync(
        NCShipmentId shipmentId,
        NCSessionEpoch epoch,
        NCActiveWorldTime now)
    {
        var gate = Gate(shipmentId.Value);
        await gate.WaitAsync();
        try
        {
            var shipment = await GetAsync(shipmentId, true);
            if (shipment == null)
                return Reject(NCMarketMutationStatus.NotFound, "The scheduled shipment does not exist.");
            if (shipment.Snapshot.Status == NCShipmentStatus.Delivered)
                return new(NCMarketMutationStatus.Duplicate, shipment, null);
            if (shipment.Snapshot.Status is not (NCShipmentStatus.InTransit or NCShipmentStatus.AtRisk) ||
                shipment.Snapshot.ExpectedArrivalAt is not { } due || now < due)
                return Reject(NCMarketMutationStatus.Rejected, "The shipment is not due for a route advance.");

            var currentZone = shipment.Snapshot.Route[Math.Min(
                shipment.Snapshot.CurrentLeg, shipment.Snapshot.Route.Count - 1)].ToZone;
            if (currentZone != shipment.Snapshot.DestinationZone)
            {
                var policy = _prototypes.Index(DefaultPolicy);
                var reroute = BuildRoute(currentZone, shipment.Snapshot.DestinationZone,
                    shipment.Snapshot.TravelMode, policy);
                if (reroute == null)
                {
                    var riskEventId = Guid.NewGuid();
                    var atRisk = shipment.Snapshot with
                    {
                        Status = NCShipmentStatus.AtRisk,
                        ExpectedArrivalAt = now.Advance(TimeSpan.FromMinutes(5)),
                        UpdatedAt = now,
                        DataVersion = checked(shipment.Snapshot.DataVersion + 1),
                        LastAppliedEventId = riskEventId,
                    };
                    var riskPersisted = await Persist(Guid.NewGuid(), "living-city.shipment.route-blocked",
                        shipment, atRisk, riskEventId, "living-city.shipment.at-risk", Guid.NewGuid(),
                        shipment.Snapshot.LastAppliedEventId, "shipment-runtime", epoch, now);
                    if (riskPersisted.Status == NCPersistentMutationStatus.Applied)
                    {
                        var loadedRisk = await GetAsync(shipmentId, true);
                        if (loadedRisk != null)
                            Enqueue(shipmentId, loadedRisk.Snapshot.ExpectedArrivalAt!.Value);
                    }
                    return riskPersisted.Status == NCPersistentMutationStatus.Applied
                        ? new(NCMarketMutationStatus.Applied, await GetAsync(shipmentId, true), null)
                        : Reject(Map(riskPersisted.Status), riskPersisted.Error ?? "The blocked route was not saved.");
                }

                var legEventId = Guid.NewGuid();
                var next = shipment.Snapshot with
                {
                    Route = reroute,
                    CurrentLeg = 0,
                    RiskBasisPoints = Math.Clamp(reroute.Sum(leg => leg.RiskBasisPoints), 0, 10_000),
                    Status = NCShipmentStatus.InTransit,
                    ExpectedArrivalAt = now.Advance(reroute[0].ExpectedDuration),
                    UpdatedAt = now,
                    DataVersion = checked(shipment.Snapshot.DataVersion + 1),
                    LastAppliedEventId = legEventId,
                };
                var legPersisted = await Persist(Guid.NewGuid(), "living-city.shipment.route-advanced",
                    shipment, next, legEventId, "living-city.shipment.route-advanced", Guid.NewGuid(),
                    shipment.Snapshot.LastAppliedEventId, "shipment-runtime", epoch, now);
                if (legPersisted.Status is not (NCPersistentMutationStatus.Applied or
                    NCPersistentMutationStatus.DuplicateCompleted))
                    return Reject(Map(legPersisted.Status), legPersisted.Error ?? "The shipment leg was not saved.");
                var loadedLeg = await GetAsync(shipmentId, true);
                if (loadedLeg == null)
                    return Reject(NCMarketMutationStatus.StorageFailure, "The advanced shipment could not be reloaded.");
                Enqueue(shipmentId, loadedLeg.Snapshot.ExpectedArrivalAt!.Value);
                return new(NCMarketMutationStatus.Applied, loadedLeg, null);
            }
        }
        finally
        {
            gate.Release();
        }
        if ((await GetAsync(shipmentId, true))?.Snapshot.HandoffKind == NCShipmentHandoffKind.Direct)
            return await DeliverAsync(shipmentId);
        var awaiting = await MarkAwaitingHandoffAsync(shipmentId);
        if (awaiting.Status is NCMarketMutationStatus.Applied or NCMarketMutationStatus.Duplicate &&
            awaiting.Shipment != null)
            RaiseLocalEvent(new NCShipmentAwaitingHandoffEvent(awaiting.Shipment.Snapshot.ShipmentId));
        return awaiting;
    }

    public async Task<NCShipmentMutationResult> CompletePhysicalHandoffAsync(
        NCShipmentId shipmentId,
        NCPartyRef recipient)
    {
        var shipment = await GetAsync(shipmentId, true);
        if (shipment == null)
            return Reject(NCMarketMutationStatus.NotFound, "The shipment does not exist.");
        if (shipment.Snapshot.Status != NCShipmentStatus.AwaitingHandoff || shipment.Snapshot.Recipient != recipient)
            return Reject(NCMarketMutationStatus.Rejected,
                "Only the shipment recipient can complete a physical handoff.");
        return await DeliverAsync(shipmentId);
    }

    public async Task<NCShipmentMutationResult> MarkCargoLostAsync(NCShipmentId shipmentId)
    {
        var shipment = await GetAsync(shipmentId, true);
        if (shipment == null)
            return Reject(NCMarketMutationStatus.NotFound, "The shipment does not exist.");
        if (shipment.Snapshot.Status is not (NCShipmentStatus.AwaitingHandoff or NCShipmentStatus.Intercepted))
            return Reject(NCMarketMutationStatus.Rejected, "The shipment has no destructible physical cargo.");
        for (var index = 0; index < shipment.Snapshot.Assets.Count; index++)
        {
            var asset = shipment.Snapshot.Assets[index];
            if (!Guid.TryParse(asset.AssetId, out var raw))
                return Reject(NCMarketMutationStatus.Rejected, "The shipment manifest contains an invalid asset.");
            var node = await _assets.GetStockNodeAsync(shipment.Snapshot.TransitStockNodeId, true);
            if (node == null)
                return Reject(NCMarketMutationStatus.NotFound, "The shipment transit stock is missing.");
            long assetVersion;
            if (asset.Kind == NCEconomicTransferAssetKind.CommodityBatch)
            {
                var batch = await _assets.GetCommodityBatchAsync(new NCCommodityBatchId(raw), true);
                if (batch?.Snapshot.Lifecycle == NCEconomicAssetLifecycle.Destroyed)
                    continue;
                if (batch == null || batch.Snapshot.Owner != shipment.Snapshot.Shipper)
                    return Reject(NCMarketMutationStatus.VersionConflict, "A cargo commodity changed custody.");
                assetVersion = batch.AggregateVersion;
            }
            else
            {
                var item = await _assets.GetItemAsync(new NCItemId(raw), true);
                if (item?.Snapshot.Lifecycle == NCEconomicAssetLifecycle.Destroyed)
                    continue;
                if (item == null || item.Snapshot.Owner != shipment.Snapshot.Shipper)
                    return Reject(NCMarketMutationStatus.VersionConflict, "A cargo item changed custody.");
                assetVersion = item.AggregateVersion;
            }
            var stable = $"{shipmentId}.{index}";
            var destroyed = await _assets.DestroyAssetAsync(new NCDestroyEconomicAssetRequest(
                NCPersistentIdentity.Command("shipment", "destroy-cargo", stable),
                asset.Kind,
                asset.AssetId,
                assetVersion,
                shipment.Snapshot.TransitStockNodeId,
                node.AggregateVersion,
                shipment.Snapshot.Shipper,
                shipment.Snapshot.Shipper,
                NCPersistentIdentity.Correlation("shipment", "destroy-cargo", stable),
                shipment.Snapshot.LastAppliedEventId,
                "shipment-cargo"));
            if (destroyed.Status is not (NCEconomicAssetMutationStatus.Applied or
                NCEconomicAssetMutationStatus.Duplicate))
                return Reject(NCMarketMutationStatus.VersionConflict,
                    destroyed.Error ?? "A destroyed cargo asset could not be committed.");
        }
        return await PersistRuntimeStatusAsync(shipmentId, NCShipmentStatus.Lost,
            "lost", "living-city.shipment.cargo-destroyed");
    }

    public Task<NCShipmentMutationResult> MarkCargoInterceptedAsync(NCShipmentId shipmentId) =>
        PersistRuntimeStatusAsync(shipmentId, NCShipmentStatus.Intercepted,
            "intercepted", "living-city.shipment.cargo-intercepted");

    public async Task<bool> ValidateTransitAssetsAsync(NCShipmentSnapshot shipment)
    {
        foreach (var asset in shipment.Assets)
        {
            if (!await IsAtNodeAsync(asset, shipment.TransitStockNodeId, shipment.Shipper))
                return false;
        }
        return true;
    }

    private Task<NCShipmentMutationResult> MarkAwaitingHandoffAsync(NCShipmentId shipmentId) =>
        PersistRuntimeStatusAsync(shipmentId, NCShipmentStatus.AwaitingHandoff,
            "await-handoff", "living-city.shipment.awaiting-handoff");

    private async Task<NCShipmentMutationResult> PersistRuntimeStatusAsync(
        NCShipmentId shipmentId,
        NCShipmentStatus status,
        string operation,
        string eventType)
    {
        if (!_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject(NCMarketMutationStatus.ClockUnavailable, "The active-world clock is unavailable.");
        var gate = Gate(shipmentId.Value);
        await gate.WaitAsync();
        try
        {
            var shipment = await GetAsync(shipmentId, true);
            if (shipment == null)
                return Reject(NCMarketMutationStatus.NotFound, "The shipment does not exist.");
            if (shipment.Snapshot.Status == status)
                return new(NCMarketMutationStatus.Duplicate, shipment, null);
            var allowed = status switch
            {
                NCShipmentStatus.AwaitingHandoff => shipment.Snapshot.Status is
                    NCShipmentStatus.InTransit or NCShipmentStatus.AtRisk,
                NCShipmentStatus.Lost => shipment.Snapshot.Status is
                    NCShipmentStatus.AwaitingHandoff or NCShipmentStatus.Intercepted,
                NCShipmentStatus.Intercepted => shipment.Snapshot.Status == NCShipmentStatus.AwaitingHandoff,
                _ => false,
            };
            if (!allowed)
                return Reject(NCMarketMutationStatus.Rejected, "The shipment runtime transition is not allowed.");
            var eventId = Guid.NewGuid();
            var next = shipment.Snapshot with
            {
                Status = status,
                ExpectedArrivalAt = null,
                UpdatedAt = now,
                DataVersion = checked(shipment.Snapshot.DataVersion + 1),
                LastAppliedEventId = eventId,
            };
            var stable = shipmentId.ToString();
            var persisted = await Persist(NCPersistentIdentity.Command("shipment", operation, stable),
                $"living-city.shipment.{operation}", shipment, next, eventId, eventType,
                NCPersistentIdentity.Correlation("shipment", operation, stable),
                shipment.Snapshot.LastAppliedEventId, "shipment-runtime", epoch, now);
            if (persisted.Status is not (NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted))
                return Reject(Map(persisted.Status), persisted.Error ?? "The shipment status was not committed.");
            var loaded = await GetAsync(shipmentId, true);
            return loaded == null
                ? Reject(NCMarketMutationStatus.StorageFailure, "The shipment status could not be reloaded.")
                : new(persisted.Status == NCPersistentMutationStatus.Applied
                    ? NCMarketMutationStatus.Applied
                    : NCMarketMutationStatus.Duplicate, loaded, null);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<NCShipmentMutationResult> DeliverAsync(NCShipmentId shipmentId)
    {
        var shipment = await GetAsync(shipmentId, true);
        if (shipment == null)
            return Reject(NCMarketMutationStatus.NotFound, "The shipment does not exist.");
        for (var index = 0; index < shipment.Snapshot.Assets.Count; index++)
        {
            var asset = shipment.Snapshot.Assets[index];
            if (await IsAtNodeAsync(asset, shipment.Snapshot.DestinationStockNodeId, shipment.Snapshot.Recipient))
                continue;
            if (!await TransferAsync(shipment.Snapshot, asset, index,
                    shipment.Snapshot.TransitStockNodeId, shipment.Snapshot.DestinationStockNodeId,
                    shipment.Snapshot.Shipper, shipment.Snapshot.Recipient, "deliver"))
                return Reject(NCMarketMutationStatus.VersionConflict,
                    "Shipment delivery stopped at a conflicting asset; recovery will retry safely.");
        }
        if (!_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject(NCMarketMutationStatus.ClockUnavailable, "The active-world clock stopped during delivery.");
        shipment = await GetAsync(shipmentId, true);
        if (shipment == null)
            return Reject(NCMarketMutationStatus.StorageFailure, "The shipment disappeared during delivery.");
        var proof = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var next = shipment.Snapshot with
        {
            Status = NCShipmentStatus.Delivered,
            ExpectedArrivalAt = null,
            ProofOfDeliveryEventId = proof,
            UpdatedAt = now,
            DataVersion = checked(shipment.Snapshot.DataVersion + 1),
            LastAppliedEventId = eventId,
        };
        var persisted = await Persist(
            NCPersistentIdentity.Command("shipment", "deliver", shipmentId.ToString()),
            "living-city.shipment.deliver",
            shipment,
            next,
            eventId,
            "living-city.shipment.delivered",
            NCPersistentIdentity.Correlation("shipment", "deliver", shipmentId.ToString()),
            shipment.Snapshot.LastAppliedEventId,
            "shipment-runtime",
            epoch,
            now);
        if (persisted.Status is not (NCPersistentMutationStatus.Applied or
            NCPersistentMutationStatus.DuplicateCompleted))
            return Reject(Map(persisted.Status), persisted.Error ?? "Delivery proof was not committed.");
        var loaded = await GetAsync(shipmentId, true);
        return loaded == null
            ? Reject(NCMarketMutationStatus.StorageFailure, "The delivered shipment could not be reloaded.")
            : new(persisted.Status == NCPersistentMutationStatus.Applied
                ? NCMarketMutationStatus.Applied
                : NCMarketMutationStatus.Duplicate, loaded, null);
    }

    private async Task<bool> TransferAsync(
        NCShipmentSnapshot shipment,
        NCShipmentAsset asset,
        int index,
        NCStockNodeId sourceId,
        NCStockNodeId destinationId,
        NCPartyRef previousOwner,
        NCPartyRef newOwner,
        string operation)
    {
        var source = await _assets.GetStockNodeAsync(sourceId, true);
        var destination = await _assets.GetStockNodeAsync(destinationId, true);
        if (source == null || destination == null || !Guid.TryParse(asset.AssetId, out var raw))
            return false;
        var key = $"{shipment.ShipmentId}.{index}";
        if (asset.Kind == NCEconomicTransferAssetKind.ItemInstance)
        {
            var item = await _assets.GetItemAsync(new NCItemId(raw), true);
            if (item == null)
                return false;
            var result = await _assets.TransferItemAsync(new NCTransferItemRequest(
                NCPersistentIdentity.Command("shipment", operation, key),
                new NCEconomicTransferId(NCPersistentIdentity.Command("shipment", $"{operation}-transfer", key)),
                item.Snapshot.ItemId,
                item.AggregateVersion,
                sourceId,
                source.AggregateVersion,
                destinationId,
                destination.AggregateVersion,
                previousOwner,
                newOwner,
                previousOwner,
                NCPersistentIdentity.Correlation("shipment", operation, key),
                shipment.LastAppliedEventId,
                "shipment-runtime"));
            return result.Status is NCEconomicAssetMutationStatus.Applied or NCEconomicAssetMutationStatus.Duplicate;
        }
        var batch = await _assets.GetCommodityBatchAsync(new NCCommodityBatchId(raw), true);
        if (batch == null || batch.Snapshot.Quantity != asset.Quantity || batch.Snapshot.ReservedQuantity != 0)
            return false;
        var commodity = await _assets.TransferCommodityAsync(new NCTransferCommodityRequest(
            NCPersistentIdentity.Command("shipment", operation, key),
            new NCEconomicTransferId(NCPersistentIdentity.Command("shipment", $"{operation}-transfer", key)),
            batch.Snapshot.BatchId,
            null,
            asset.Quantity,
            batch.AggregateVersion,
            sourceId,
            source.AggregateVersion,
            destinationId,
            destination.AggregateVersion,
            previousOwner,
            newOwner,
            previousOwner,
            NCPersistentIdentity.Correlation("shipment", operation, key),
            shipment.LastAppliedEventId,
            "shipment-runtime"));
        return commodity.Status is NCEconomicAssetMutationStatus.Applied or NCEconomicAssetMutationStatus.Duplicate;
    }

    private async Task<bool> IsAtNodeAsync(NCShipmentAsset asset, NCStockNodeId nodeId, NCPartyRef owner)
    {
        if (!Guid.TryParse(asset.AssetId, out var raw))
            return false;
        var location = new NCEconomicAssetLocation(NCEconomicAssetLocationKind.StockNode, nodeId.ToString());
        if (asset.Kind == NCEconomicTransferAssetKind.ItemInstance)
        {
            var item = await _assets.GetItemAsync(new NCItemId(raw), true);
            return item?.Snapshot.Owner == owner && item.Snapshot.Location == location;
        }
        var batch = await _assets.GetCommodityBatchAsync(new NCCommodityBatchId(raw), true);
        return batch?.Snapshot.Owner == owner && batch.Snapshot.Location == location;
    }

    private async Task<bool> ValidateAssetsAsync(NCCreateShipmentRequest request, NCStockNodeRecord origin)
    {
        foreach (var asset in request.Assets)
        {
            if (!Guid.TryParse(asset.AssetId, out var raw))
                return false;
            if (asset.Kind == NCEconomicTransferAssetKind.ItemInstance)
            {
                var item = await _assets.GetItemAsync(new NCItemId(raw), true);
                if (asset.Quantity != 1 || item == null || item.Snapshot.Owner != request.Shipper ||
                    !origin.Snapshot.ItemIds.Contains(item.Snapshot.ItemId) || item.Snapshot.ReservationId.HasValue)
                    return false;
            }
            else
            {
                var batch = await _assets.GetCommodityBatchAsync(new NCCommodityBatchId(raw), true);
                if (batch == null || batch.Snapshot.Owner != request.Shipper ||
                    !origin.Snapshot.CommodityBatchIds.Contains(batch.Snapshot.BatchId) ||
                    batch.Snapshot.Quantity != asset.Quantity || batch.Snapshot.ReservedQuantity != 0)
                    return false;
            }
        }
        return true;
    }

    private async Task<NCStockNodeRecord?> EnsureTransitStockAsync(
        NCCreateShipmentRequest request,
        NCStockNodeId nodeId)
    {
        var existing = await _assets.GetStockNodeAsync(nodeId, true);
        if (existing != null)
            return existing.Snapshot.Owner == request.Shipper && existing.Snapshot.Kind == NCStockNodeKind.Shipment
                ? existing
                : null;
        var result = await _assets.CreateStockNodeAsync(new NCCreateStockNodeRequest(
            NCPersistentIdentity.Command("shipment", "transit-stock", request.ShipmentId.ToString()),
            nodeId,
            $"shipment.{request.ShipmentId}",
            NCStockNodeKind.Shipment,
            request.Shipper,
            $"shipment.{request.ShipmentId}.transit",
            true,
            true,
            request.Shipper,
            NCPersistentIdentity.Correlation("shipment", "transit-stock", request.ShipmentId.ToString()),
            request.CausationId,
            request.AuditSource));
        return result.StockNode;
    }

    private IReadOnlyList<NCShipmentRouteLeg>? BuildRoute(
        Content.Shared._NC.City.Zones.NCZoneId origin,
        Content.Shared._NC.City.Zones.NCZoneId destination,
        NCCityTravelMode mode,
        NCMarketPolicyPrototype policy)
    {
        var routeBuffer = new List<NCCityRouteStep>();
        if (origin != destination && !_city.TryFindRoute(origin, destination, mode, routeBuffer))
            return null;
        if (routeBuffer.Count > policy.MaximumRouteLegs)
            return null;
        if (routeBuffer.Count == 0)
            return [new NCShipmentRouteLeg(origin, destination, "local-handoff",
                policy.MinimumShipmentDuration, 0)];
        var result = new List<NCShipmentRouteLeg>(routeBuffer.Count);
        foreach (var step in routeBuffer)
        {
            if (!_prototypes.TryIndex(step.TraversalProfile, out NCCityTraversalProfilePrototype? traversal) ||
                traversal == null)
                return null;
            var seconds = traversal.BaseTravelSeconds * policy.ShipmentDurationBasisPoints / 10_000d;
            var duration = TimeSpan.FromSeconds(Math.Clamp(seconds,
                policy.MinimumShipmentDuration.TotalSeconds, policy.MaximumShipmentDuration.TotalSeconds));
            var risk = (int) Math.Clamp(Math.Round(
                traversal.ThreatCost * policy.ThreatCostToRiskBasisPoints), 0, 10_000);
            result.Add(new NCShipmentRouteLeg(
                step.FromZone, step.ToZone, traversal.ID, duration, risk));
        }
        return result;
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        _active = true;
        _generation++;
        _due.Clear();
        _queued.Clear();
        _processing.Clear();
        Track(RecoverAsync(_generation, args.Time));
    }

    private void OnSessionClosing(NCActiveWorldSessionClosingEvent args)
    {
        _active = false;
        _generation++;
        if (_inFlight.Count > 0)
            args.AddPendingWrite(Task.WhenAll(_inFlight.ToArray()));
    }

    private void OnSessionClosed(NCActiveWorldSessionClosedEvent args) => ClearRuntime();
    private void OnClockFailed(NCActiveWorldClockFailedEvent args) => ClearRuntime();

    private void ClearRuntime()
    {
        _active = false;
        _generation++;
        _due.Clear();
        _queued.Clear();
        _processing.Clear();
    }

    private async Task RecoverAsync(int generation, NCActiveWorldTime now)
    {
        string? after = null;
        var recovered = 0;
        while (_active && generation == _generation && recovered < MaximumRecoveredShipments)
        {
            var page = await _database.GetNCAggregateSnapshotPageAsync(AggregateType, after, RecoveryPageSize);
            if (!_active || generation != _generation)
                return;
            foreach (var stored in page)
            {
                if (!Guid.TryParse(stored.AggregateId, out var raw))
                    continue;
                var shipment = await GetAsync(new NCShipmentId(raw), true);
                if (shipment?.Snapshot.Status == NCShipmentStatus.Loading)
                    Track(LoadAsync(shipment));
                else if (shipment?.Snapshot.Status is NCShipmentStatus.InTransit or NCShipmentStatus.AtRisk)
                    Enqueue(shipment.Snapshot.ShipmentId, shipment.Snapshot.ExpectedArrivalAt ?? now);
                recovered++;
            }
            if (page.Count < RecoveryPageSize)
                return;
            after = page[^1].AggregateId;
        }
    }

    private void Enqueue(NCShipmentId shipmentId, NCActiveWorldTime dueAt)
    {
        if (_active && _queued.Add(shipmentId))
            _due.Enqueue(shipmentId, dueAt.Ticks);
    }

    private void Track(Task task)
    {
        _inFlight.Add(task);
        _ = CompleteTrackedAsync(task);
    }

    private async Task CompleteTrackedAsync(Task task)
    {
        try
        {
            await task;
        }
        finally
        {
            _inFlight.Remove(task);
        }
    }

    private Task<NCPersistentMutationResult> Persist(
        Guid commandId,
        string commandType,
        NCShipmentRecord? current,
        NCShipmentSnapshot next,
        Guid eventId,
        string eventType,
        Guid correlationId,
        Guid? causationId,
        string auditSource,
        NCSessionEpoch epoch,
        NCActiveWorldTime now) => _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(
        commandId, commandType, "shipment", next.ShipmentId.ToString(), eventId, eventType,
        AggregateType, next.ShipmentId.ToString(), current?.AggregateVersion ?? 0, SnapshotFormatVersion,
        JsonSerializer.Serialize(next, JsonOptions),
        JsonSerializer.Serialize(new ShipmentEvent(next.ShipmentId, auditSource), JsonOptions),
        JsonSerializer.Serialize(new Receipt(next.ShipmentId, (current?.AggregateVersion ?? 0) + 1), JsonOptions),
        correlationId, causationId, epoch, now, []));

    private bool TryPolicy(out NCMarketPolicyPrototype policy) => _prototypes.TryIndex(DefaultPolicy, out policy!) &&
        policy.MaximumShipmentAssets is > 0 and <= 64 && policy.MaximumRouteLegs is > 0 and <= 256 &&
        policy.MinimumShipmentDuration > TimeSpan.Zero &&
        policy.MaximumShipmentDuration >= policy.MinimumShipmentDuration &&
        policy.ShipmentDurationBasisPoints is > 0 and <= 100_000 &&
        policy.ThreatCostToRiskBasisPoints is >= 0 and <= 100_000;

    private static bool Valid(NCCreateShipmentRequest value, NCMarketPolicyPrototype policy) =>
        value.CommandId != Guid.Empty && value.ShipmentId.IsValid && value.Shipper.IsValid &&
        value.Recipient.IsValid && value.Carrier.IsValid && value.OriginStockNodeId.IsValid &&
        value.DestinationStockNodeId.IsValid && value.OriginStockNodeId != value.DestinationStockNodeId &&
        value.OriginZone.IsValid && value.DestinationZone.IsValid && value.TravelMode != NCCityTravelMode.None &&
        value.Assets.Count is > 0 && value.Assets.Count <= policy.MaximumShipmentAssets &&
        value.Assets.All(asset => Enum.IsDefined(asset.Kind) && Guid.TryParse(asset.AssetId, out var id) &&
            id != Guid.Empty && asset.AssetId == id.ToString("D") && asset.Quantity > 0) &&
        value.Assets.Select(asset => (asset.Kind, asset.AssetId)).Distinct().Count() == value.Assets.Count &&
        Enum.IsDefined(value.HandoffKind) && value.Initiator == value.Shipper &&
        value.CorrelationId != Guid.Empty && ValidAudit(value.AuditSource);

    private static bool Same(NCShipmentSnapshot value, NCCreateShipmentRequest request) =>
        value.ShipmentId == request.ShipmentId && value.Shipper == request.Shipper &&
        value.Recipient == request.Recipient && value.Carrier == request.Carrier &&
        value.OriginStockNodeId == request.OriginStockNodeId &&
        value.DestinationStockNodeId == request.DestinationStockNodeId &&
        value.OriginZone == request.OriginZone && value.DestinationZone == request.DestinationZone &&
        value.TravelMode == request.TravelMode && value.HandoffKind == request.HandoffKind &&
        value.Assets.SequenceEqual(request.Assets.OrderBy(asset => asset.Kind).ThenBy(asset => asset.AssetId));

    private static NCShipmentRecord? Deserialize(NCAggregateSnapshotData? stored, NCShipmentId id)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCShipmentSnapshot>(stored.Payload, JsonOptions);
            return snapshot?.ShipmentId == id
                ? new(snapshot, stored.AggregateVersion, stored.SessionEpoch, stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private SemaphoreSlim Gate(Guid id) => _gates[(id.GetHashCode() & int.MaxValue) % _gates.Length];
    private static bool ValidAudit(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
    private static NCMarketMutationStatus Map(NCPersistentMutationStatus status) => status switch
    {
        NCPersistentMutationStatus.VersionConflict => NCMarketMutationStatus.VersionConflict,
        NCPersistentMutationStatus.ClockRejected => NCMarketMutationStatus.ClockUnavailable,
        _ => NCMarketMutationStatus.StorageFailure,
    };
    private static NCShipmentMutationResult Reject(NCMarketMutationStatus status, string error) =>
        new(status, null, error);
    private sealed record ShipmentEvent(NCShipmentId ShipmentId, string AuditSource);
    private sealed record Receipt(NCShipmentId ShipmentId, long AggregateVersion);
}
