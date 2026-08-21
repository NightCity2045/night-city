// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.LivingCity.Market;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Market;

/// <summary>
/// Projects an awaiting shipment as one sealed physical parcel at a mapper-authored handoff point. The manifest stays
/// canonical in persistent stock; the stock lease blocks abstract transfer until delivery or destruction is captured.
/// </summary>
public sealed partial class NCShipmentCargoSystem : EntitySystem
{
    private static readonly EntProtoId CargoPrototype = "NCShipmentCargoParcel";

    [Dependency] private NCShipmentSystem _shipments = default!;
    [Dependency] private NCEconomicMaterializationLeaseIndexSystem _leases = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private NCZoneSystem _zones = default!;

    private readonly HashSet<EntityUid> _endpoints = [];
    private readonly Dictionary<NCShipmentId, CargoProjection> _cargoByShipment = [];
    private readonly Dictionary<EntityUid, NCShipmentId> _shipmentByCargo = [];
    private readonly Dictionary<EntityUid, int> _endpointLoads = [];
    private readonly HashSet<EntityUid> _intentionalRemoval = [];
    private readonly HashSet<Task> _inFlight = [];
    private bool _active;

    public int PhysicalCargoCount => _cargoByShipment.Count;
    public int HandoffPointCount => _endpoints.Count;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCShipmentHandoffPointComponent, ComponentStartup>(OnEndpointStartup);
        SubscribeLocalEvent<NCShipmentHandoffPointComponent, ComponentShutdown>(OnEndpointShutdown);
        SubscribeLocalEvent<NCShipmentCargoComponent, ComponentShutdown>(OnCargoShutdown);
        SubscribeLocalEvent<NCShipmentAwaitingHandoffEvent>(OnAwaitingHandoff);
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(_ => _active = true);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnSessionClosing);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Reset());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Reset());
    }

    public async Task<NCShipmentCargoMutationResult> MaterializeAsync(NCShipmentId shipmentId)
    {
        if (!_active || !_clock.IsReady || _cargoByShipment.ContainsKey(shipmentId))
            return new(NCMarketMutationStatus.Rejected, null, null,
                "The shipment is already physical or the active round is unavailable.");
        var shipment = await _shipments.GetAsync(shipmentId, true);
        if (shipment?.Snapshot.Status != NCShipmentStatus.AwaitingHandoff)
            return new(NCMarketMutationStatus.Rejected, null, shipment,
                "The shipment is not awaiting a physical handoff.");
        if (!TryFindEndpoint(shipment.Snapshot, out var endpoint))
            return new(NCMarketMutationStatus.NotFound, null, shipment,
                "No compatible mapped handoff point exists in the destination zone.");
        if (!await _shipments.ValidateTransitAssetsAsync(shipment.Snapshot))
            return new(NCMarketMutationStatus.VersionConflict, null, shipment,
                "The shipment manifest no longer matches transit stock.");

        var cargo = Spawn(CargoPrototype, Transform(endpoint).Coordinates);
        var component = EnsureComp<NCShipmentCargoComponent>(cargo);
        component.ShipmentId = shipmentId;
        component.TransitStockNodeId = shipment.Snapshot.TransitStockNodeId;
        component.Sealed = true;
        if (!_leases.TryAcquireExternal(shipmentId.Value, shipment.Snapshot.TransitStockNodeId, cargo) ||
            !await _shipments.ValidateTransitAssetsAsync(shipment.Snapshot))
        {
            _leases.TryReleaseExternal(shipmentId.Value, cargo);
            _intentionalRemoval.Add(cargo);
            QueueDel(cargo);
            return new(NCMarketMutationStatus.VersionConflict, null, shipment,
                "The transit stock changed while the cargo projection was acquired.");
        }
        var projection = new CargoProjection(shipmentId, cargo, endpoint, shipment.Snapshot.TransitStockNodeId);
        _cargoByShipment.Add(shipmentId, projection);
        _shipmentByCargo.Add(cargo, shipmentId);
        _endpointLoads[endpoint] = _endpointLoads.GetValueOrDefault(endpoint) + 1;
        return new(NCMarketMutationStatus.Applied, cargo, shipment, null);
    }

    public async Task<NCShipmentCargoMutationResult> CompleteHandoffAsync(
        NCShipmentId shipmentId,
        NCPartyRef recipient)
    {
        if (!_cargoByShipment.TryGetValue(shipmentId, out var projection))
            return new(NCMarketMutationStatus.NotFound, null, null, "The physical shipment parcel is missing.");
        _leases.TryReleaseExternal(shipmentId.Value, projection.CargoEntity);
        var result = await _shipments.CompletePhysicalHandoffAsync(shipmentId, recipient);
        if (result.Status is NCMarketMutationStatus.Applied or NCMarketMutationStatus.Duplicate &&
            result.Shipment?.Snapshot.Status == NCShipmentStatus.Delivered)
        {
            _intentionalRemoval.Add(projection.CargoEntity);
            QueueDel(projection.CargoEntity);
            return new(result.Status, projection.CargoEntity, result.Shipment, null);
        }
        if (result.Shipment != null && await _shipments.ValidateTransitAssetsAsync(result.Shipment.Snapshot))
            _leases.TryAcquireExternal(shipmentId.Value, projection.TransitStockNodeId, projection.CargoEntity);
        return new(result.Status, projection.CargoEntity, result.Shipment, result.Error);
    }

    private void OnAwaitingHandoff(NCShipmentAwaitingHandoffEvent args) => Track(MaterializeLoggedAsync(args.ShipmentId));

    private async Task MaterializeLoggedAsync(NCShipmentId shipmentId)
    {
        var result = await MaterializeAsync(shipmentId);
        if (result.Status is not (NCMarketMutationStatus.Applied or NCMarketMutationStatus.Duplicate))
            Log.Warning($"Shipment {shipmentId} awaits a mapped handoff: {result.Error}");
    }

    private void OnEndpointStartup(Entity<NCShipmentHandoffPointComponent> entity, ref ComponentStartup args)
    {
        if (Valid(entity.Comp))
            _endpoints.Add(entity.Owner);
        else
            Log.Error($"Invalid shipment handoff point on {ToPrettyString(entity.Owner)}.");
    }

    private void OnEndpointShutdown(Entity<NCShipmentHandoffPointComponent> entity, ref ComponentShutdown args)
    {
        _endpoints.Remove(entity.Owner);
        _endpointLoads.Remove(entity.Owner);
    }

    private void OnCargoShutdown(Entity<NCShipmentCargoComponent> entity, ref ComponentShutdown args)
    {
        if (!_shipmentByCargo.Remove(entity.Owner, out var shipmentId) ||
            !_cargoByShipment.Remove(shipmentId, out var projection))
            return;
        DecrementEndpoint(projection.EndpointEntity);
        _leases.TryReleaseExternal(shipmentId.Value, entity.Owner);
        if (_intentionalRemoval.Remove(entity.Owner) || !_active || !_clock.IsReady)
            return;
        Track(CaptureDestroyedCargoAsync(shipmentId));
    }

    private async Task CaptureDestroyedCargoAsync(NCShipmentId shipmentId)
    {
        var result = await _shipments.MarkCargoLostAsync(shipmentId);
        if (result.Status is NCMarketMutationStatus.Applied or NCMarketMutationStatus.Duplicate)
            return;
        var intercepted = await _shipments.MarkCargoInterceptedAsync(shipmentId);
        if (intercepted.Status is not (NCMarketMutationStatus.Applied or NCMarketMutationStatus.Duplicate))
            Log.Error($"Destroyed shipment cargo {shipmentId} could not be captured: {result.Error}");
    }

    private bool TryFindEndpoint(NCShipmentSnapshot shipment, out EntityUid endpoint)
    {
        foreach (var candidate in _endpoints.OrderBy(uid => uid.Id))
        {
            if (!TryComp<NCShipmentHandoffPointComponent>(candidate, out var component) ||
                component.HandoffKind != shipment.HandoffKind ||
                _endpointLoads.GetValueOrDefault(candidate) >= component.MaximumActiveParcels ||
                !_coordinates.TryGetCoordinates(candidate, out var coordinates) ||
                !_zones.Contains(shipment.DestinationZone, coordinates))
                continue;
            endpoint = candidate;
            return true;
        }
        endpoint = EntityUid.Invalid;
        return false;
    }

    private void OnSessionClosing(NCActiveWorldSessionClosingEvent args)
    {
        _active = false;
        foreach (var projection in _cargoByShipment.Values)
            _leases.TryReleaseExternal(projection.ShipmentId.Value, projection.CargoEntity);
        if (_inFlight.Count > 0)
            args.AddPendingWrite(Task.WhenAll(_inFlight.ToArray()));
    }

    private void Reset()
    {
        _active = false;
        _cargoByShipment.Clear();
        _shipmentByCargo.Clear();
        _endpointLoads.Clear();
        _intentionalRemoval.Clear();
    }

    private void DecrementEndpoint(EntityUid endpoint)
    {
        if (!_endpointLoads.TryGetValue(endpoint, out var count))
            return;
        if (count <= 1)
            _endpointLoads.Remove(endpoint);
        else
            _endpointLoads[endpoint] = count - 1;
    }

    private void Track(Task task)
    {
        _inFlight.Add(task);
        _ = FinishAsync(task);
    }

    private async Task FinishAsync(Task task)
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

    private static bool Valid(NCShipmentHandoffPointComponent value) =>
        !string.IsNullOrWhiteSpace(value.StableKey) && value.StableKey.Length <= 128 &&
        value.StableKey == value.StableKey.Trim().ToLowerInvariant() && Enum.IsDefined(value.HandoffKind) &&
        value.HandoffKind != NCShipmentHandoffKind.Direct && value.MaximumActiveParcels is > 0 and <= 64;

    private sealed record CargoProjection(
        NCShipmentId ShipmentId,
        EntityUid CargoEntity,
        EntityUid EndpointEntity,
        Content.Shared._NC.LivingCity.Economy.NCStockNodeId TransitStockNodeId);
}
