// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;

namespace Content.Server._NC.City.Connectivity;

/// <summary>
/// Round-local identity for one connected navigation region inside a semantic city zone.
/// </summary>
public readonly record struct NCRoomNodeId(long Value)
{
    public bool IsValid => Value > 0;
}

/// <summary>
/// Round-local identity for one aggregated transition between navigation regions.
/// </summary>
public readonly record struct NCRoomPortalId(long Value)
{
    public bool IsValid => Value > 0;
}

public enum NCRoomPortalKind : byte
{
    Door,
    OpenBoundary,
}

public readonly record struct NCRoomNodeInfo(
    NCRoomNodeId Id,
    EntityUid GridUid,
    NCZoneId ZoneId,
    NCMapCoordinates Anchor,
    int NavigationNodeCount);

public readonly record struct NCRoomPortalInfo(
    NCRoomPortalId Id,
    NCRoomNodeId RoomA,
    NCRoomNodeId RoomB,
    NCMapCoordinates TargetA,
    NCMapCoordinates TargetB,
    NCRoomPortalKind Kind,
    EntityUid Controller);

public readonly record struct NCRoomRouteStep(
    NCRoomPortalId PortalId,
    NCRoomNodeId FromRoom,
    NCRoomNodeId ToRoom,
    NCMapCoordinates EntryTarget,
    NCMapCoordinates ExitTarget,
    NCRoomPortalKind Kind,
    EntityUid Controller);

/// <summary>
/// Navigable polygon center available to derived indices. It is round-local and never persisted.
/// </summary>
public readonly record struct NCRoomNavigationSample(
    NCRoomNodeId RoomId,
    EntityUid GridUid,
    NCZoneId ZoneId,
    NCMapCoordinates Target);

public enum NCCityNavigationWaypointKind : byte
{
    RoomPortalEntry,
    RoomPortalExit,
    CityPortalEntry,
    CityPortalExit,
    Destination,
}

/// <summary>
/// One bounded target for vanilla Robust tile pathfinding or one semantic portal traversal.
/// </summary>
public readonly record struct NCCityNavigationWaypoint(
    NCCityNavigationWaypointKind Kind,
    NCMapCoordinates Target,
    EntityUid Controller,
    NCRoomPortalId RoomPortalId,
    NCCityPortalId CityPortalId);

public readonly record struct NCCityNavigationPlanVersion(long CityGraphVersion, long RoomGraphVersion);

/// <summary>
/// Raised after a structural Room/Portal cache change. Door open/close state does not change this topology version.
/// </summary>
[ByRefEvent]
public readonly record struct NCRoomPortalGraphChangedEvent(
    long OldVersion,
    long NewVersion,
    EntityUid AffectedGrid);
