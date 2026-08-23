// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using Content.Server._NC.City.Zones;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Robust.Shared.Map;

namespace Content.Server._NC.City.Connectivity;

/// <summary>
/// Composes semantic CityGraph routing and local Room/Portal routing into bounded targets. Movement remains owned by
/// the vanilla Robust pathfinding and NPC steering systems.
/// </summary>
public sealed partial class NCCityNavigationPlannerSystem : EntitySystem
{
    [Dependency] private NCCityConnectivitySystem _city = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private NCRoomPortalGraphSystem _rooms = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private NCZoneSystem _zones = default!;

    private readonly List<NCCityRouteStep> _cityRoute = [];
    private readonly List<NCRoomRouteStep> _roomRoute = [];

    /// <summary>
    /// Produces a versioned route plan for a materialized actor. Filters are domain-owned access/blockage checks;
    /// no RED skill checks, dice, or generic action formula are involved.
    /// </summary>
    public bool TryPlan(
        EntityUid actor,
        NCMapCoordinates destination,
        NCCityTravelMode travelMode,
        List<NCCityNavigationWaypoint> result,
        out NCCityNavigationPlanVersion version,
        Func<NCCityPortalInfo, bool>? canTraverseCity = null,
        Func<NCRoomPortalInfo, bool>? canTraverseRoom = null)
    {
        result.Clear();
        version = new NCCityNavigationPlanVersion(_city.GraphVersion, _rooms.GraphVersion);
        if (!_coordinates.TryGetCoordinates(actor, out var origin) ||
            !_coordinates.TryResolve(origin, out _) ||
            !_coordinates.TryResolve(destination, out _) ||
            !TryResolveEntityCoordinates(origin, out var localCursor) ||
            !TryResolveEntityCoordinates(destination, out var localDestination))
        {
            return false;
        }

        var fromZone = _zones.GetLocationContext(origin).MostSpecific;
        var toZone = _zones.GetLocationContext(destination).MostSpecific;
        if (!fromZone.IsValid || !toZone.IsValid ||
            !_city.TryFindRoute(origin, destination, travelMode, _cityRoute, canTraverseCity))
        {
            return false;
        }

        foreach (var cityStep in _cityRoute)
        {
            if (!TryResolveEntityCoordinates(cityStep.EntryTarget, out var cityEntry) ||
                !AppendRoomSegment(localCursor, cityEntry, result, canTraverseRoom))
            {
                result.Clear();
                return false;
            }

            result.Add(new NCCityNavigationWaypoint(
                NCCityNavigationWaypointKind.CityPortalEntry,
                cityStep.EntryTarget,
                cityStep.Controller,
                default,
                cityStep.PortalId));
            result.Add(new NCCityNavigationWaypoint(
                NCCityNavigationWaypointKind.CityPortalExit,
                cityStep.ExitTarget,
                cityStep.Controller,
                default,
                cityStep.PortalId));

            if (!TryResolveEntityCoordinates(cityStep.ExitTarget, out localCursor))
            {
                result.Clear();
                return false;
            }
        }

        if (!AppendRoomSegment(localCursor, localDestination, result, canTraverseRoom))
        {
            result.Clear();
            return false;
        }

        result.Add(new NCCityNavigationWaypoint(
            NCCityNavigationWaypointKind.Destination,
            destination,
            EntityUid.Invalid,
            default,
            default));
        return true;
    }

    private bool AppendRoomSegment(
        EntityCoordinates from,
        EntityCoordinates to,
        List<NCCityNavigationWaypoint> result,
        Func<NCRoomPortalInfo, bool>? canTraverse)
    {
        if (!_rooms.TryGetRoom(from, out var fromRoom) ||
            !_rooms.TryGetRoom(to, out var toRoom) ||
            !_rooms.TryFindRoute(fromRoom, toRoom, _roomRoute, canTraverse))
        {
            return false;
        }

        foreach (var roomStep in _roomRoute)
        {
            result.Add(new NCCityNavigationWaypoint(
                NCCityNavigationWaypointKind.RoomPortalEntry,
                roomStep.EntryTarget,
                roomStep.Controller,
                roomStep.PortalId,
                default));
            result.Add(new NCCityNavigationWaypoint(
                NCCityNavigationWaypointKind.RoomPortalExit,
                roomStep.ExitTarget,
                roomStep.Controller,
                roomStep.PortalId,
                default));
        }

        return true;
    }

    private bool TryResolveEntityCoordinates(NCMapCoordinates coordinates, out EntityCoordinates entityCoordinates)
    {
        entityCoordinates = default;
        if (!_coordinates.TryResolve(coordinates, out var mapCoordinates) ||
            !_maps.TryGetMap(mapCoordinates.MapId, out var mapUid) ||
            mapUid is not { } map ||
            !_maps.TryFindGridAt(map, mapCoordinates.Position, out var gridUid, out _))
        {
            return false;
        }

        entityCoordinates = _transform.ToCoordinates((gridUid, null), mapCoordinates);
        return true;
    }
}
