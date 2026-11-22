// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server._NC.Meta.Devices;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.Network;

namespace Content.Server._NC.Meta.Network;

public sealed partial class NCMetaNetworkSystem
{
    // Initial tuning, not final balance: calibrate on the door and three-level scenes.
    public const int CableBaseCost = 1;
    public const int CableTilesPerUnit = 8;
    public const int RadioBaseCost = 2;
    public const int RadioTilesPerUnit = 4;
    [Dependency] private SharedTransformSystem _transform = default!;

    /// <summary>Native invalidation signal; subscribers queue bounded work rather than querying routes in the callback.</summary>
    public event Action? RoutingChanged;

    private void InitializeRoutingNotifications()
    {
        _devices.Changed += OnRoutingDeviceChanged;
        _dataCables.Changed += OnDataCablesChanged;
        SubscribeLocalEvent<NCMetaPortComponent, MoveEvent>(OnRoutingPortMoved);
        SubscribeLocalEvent<NCMetaRadioInterferenceComponent, MoveEvent>(OnRoutingInterferenceMoved);
    }
    private void OnRoutingDeviceChanged(NCMetaDeviceEntry entry) => RoutingChanged?.Invoke();
    private void OnDataCablesChanged() => RoutingChanged?.Invoke();

    /// <summary>
    /// Link capacity units one link occupies on the host side. A radio link costs more than a laid cable, and both grow
    /// with distance: radio by straight-line tiles, cable by the number of laid tiles on the shortest path.
    /// </summary>
    public bool TryGetLinkCost(EntityUid from, EntityUid to, out int cost)
    {
        cost = 0;
        if (from == to)
            return true;
        if (!CanRoute(from, to))
            return false;

        if (TryComp<NCMetaPortComponent>(from, out var source) && TryComp<NCMetaPortComponent>(to, out var destination) &&
            AllowedSegment(from, to) && CanRadio(from, to, source, destination))
        {
            var here = _transform.GetMapCoordinates(from);
            var there = _transform.GetMapCoordinates(to);
            var tiles = here.MapId == there.MapId ? (int) MathF.Ceiling((here.Position - there.Position).Length()) : 32;
            cost = RadioBaseCost + (tiles + RadioTilesPerUnit - 1) / RadioTilesPerUnit;
            return true;
        }

        if (AllowedSegment(from, to) && _dataCables.TryGetDistance(from, to, out var laid))
        {
            cost = CableBaseCost + (laid + CableTilesPerUnit - 1) / CableTilesPerUnit;
            return true;
        }

        // Routed through the older two-ended cables.
        cost = CableBaseCost + 1;
        return true;
    }
    private void OnRoutingPortMoved(Entity<NCMetaPortComponent> ent, ref MoveEvent args) => RoutingChanged?.Invoke();
    private void OnRoutingInterferenceMoved(Entity<NCMetaRadioInterferenceComponent> ent, ref MoveEvent args) => RoutingChanged?.Invoke();
}
