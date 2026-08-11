// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.City.Zones;
using Content.Shared._NC.Agent.Shards.Navigator;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared.Administration.Logs;
using Content.Shared.CartridgeLoader;
using Content.Shared.Database;

namespace Content.Server._NC.Agent.Shards;

/// <summary>
/// Resolves persistent Night City coordinates only on explicit NAVIGATOR requests.
/// It performs no entity scans or per-frame position polling.
/// </summary>
public sealed partial class NCAgentNavigatorSystem : EntitySystem
{
    [Dependency] private CartridgeLoaderSystem _cartridges = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private ISharedAdminLogManager _adminLog = default!;

    private readonly List<NCZoneInfo> _zoneBuffer = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCAgentNavigatorComponent, CartridgeUiReadyEvent>(OnUiReady);
        SubscribeLocalEvent<NCAgentNavigatorComponent, CartridgeMessageEvent>(OnUiMessage);
    }

    private void OnUiReady(Entity<NCAgentNavigatorComponent> ent, ref CartridgeUiReadyEvent args)
    {
        UpdateUi(ent, args.Loader);
    }

    private void OnUiMessage(Entity<NCAgentNavigatorComponent> ent, ref CartridgeMessageEvent args)
    {
        if (args is not NCAgentNavigatorUiMessage message)
            return;

        var loader = GetEntity(args.LoaderUid);
        var changed = message.Action switch
        {
            NCAgentNavigatorAction.SaveCurrent => TrySave(ent, loader, message.Label),
            NCAgentNavigatorAction.Delete => ent.Comp.Waypoints.RemoveAll(point => point.Id == message.WaypointId) > 0,
            _ => false,
        };

        if (changed)
        {
            Dirty(ent);
            _adminLog.Add(
                LogType.PdaInteract,
                LogImpact.Low,
                $"{ToPrettyString(args.Actor)} performed {message.Action} in Agent NAVIGATOR on {ToPrettyString(ent)}");
        }

        UpdateUi(ent, loader);
    }

    private bool TrySave(Entity<NCAgentNavigatorComponent> ent, EntityUid loader, string rawLabel)
    {
        var label = rawLabel.Trim();
        if (string.IsNullOrWhiteSpace(label) ||
            label.Length > ent.Comp.MaximumLabelLength ||
            ent.Comp.Waypoints.Count >= ent.Comp.MaximumWaypoints ||
            !_coordinates.TryGetCoordinates(loader, out var coordinates))
        {
            return false;
        }

        ent.Comp.Waypoints.Add(new NCAgentWaypoint(
            Guid.NewGuid(),
            label,
            coordinates,
            ResolveZonePath(coordinates)));
        return true;
    }

    private void UpdateUi(Entity<NCAgentNavigatorComponent> ent, EntityUid loader)
    {
        NCMapCoordinates? current = null;
        var currentZone = "CITY GRID // SIGNAL UNAVAILABLE";
        if (_coordinates.TryGetCoordinates(loader, out var coordinates))
        {
            current = coordinates;
            currentZone = ResolveZonePath(coordinates);
        }

        // Copy device-local data so later mutations cannot alter an already queued UI state.
        _cartridges.UpdateCartridgeUiState(
            loader,
            new NCAgentNavigatorUiState(current, currentZone, [.. ent.Comp.Waypoints]));
    }

    private string ResolveZonePath(NCMapCoordinates coordinates)
    {
        _zones.GetZones(coordinates, _zoneBuffer);
        var names = _zoneBuffer
            .Where(zone => zone.ContextSlot is NCZoneContextSlot.District or
                NCZoneContextSlot.Sector or
                NCZoneContextSlot.Street or
                NCZoneContextSlot.Building)
            .Select(zone => zone.Name)
            .TakeLast(3)
            .ToArray();
        return names.Length == 0 ? "UNMAPPED CITY SECTOR" : string.Join(" / ", names);
    }
}
