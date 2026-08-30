// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.City.Connectivity;

/// <summary>
/// Requests an on-demand connectivity snapshot. The server accepts this only from Mapping administrators.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCCityConnectivityDebugRequest(bool enabled) : EntityEventArgs
{
    public bool Enabled { get; } = enabled;
}

[Serializable, NetSerializable]
public enum NCConnectivityDebugRoomPortalKind : byte
{
    Door,
    OpenBoundary,
}

[Serializable, NetSerializable]
public enum NCConnectivityDebugIssueSeverity : byte
{
    Warning,
    Error,
}

[Serializable, NetSerializable]
public readonly record struct NCConnectivityDebugZone(
    NCZoneId Id,
    NCZoneId Parent,
    string Name,
    NCZoneContextSlot ContextSlot);

[Serializable, NetSerializable]
public readonly record struct NCConnectivityDebugRoom(
    long Id,
    NCZoneId ZoneId,
    NCMapCoordinates Anchor,
    int NavigationNodeCount);

[Serializable, NetSerializable]
public readonly record struct NCConnectivityDebugCityPortal(
    long Id,
    NCZoneId FromZone,
    NCZoneId ToZone,
    NCMapCoordinates EntryTarget,
    NCMapCoordinates ExitTarget,
    NCCityPortalKind Kind,
    float TravelSeconds,
    float ThreatCost,
    bool Enabled,
    bool Stable);

[Serializable, NetSerializable]
public readonly record struct NCConnectivityDebugRoomPortal(
    long Id,
    long RoomA,
    long RoomB,
    NCMapCoordinates TargetA,
    NCMapCoordinates TargetB,
    NCConnectivityDebugRoomPortalKind Kind,
    bool Controlled);

[Serializable, NetSerializable]
public readonly record struct NCConnectivityDebugIssue(
    NCConnectivityDebugIssueSeverity Severity,
    byte Code,
    NCZoneId ZoneId,
    string Message);

/// <summary>
/// Immutable, request-driven debug view. It contains no EntityUid and is safe across the network boundary.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCCityConnectivityDebugSnapshot(
    bool enabled,
    bool authorized,
    long cityGraphVersion,
    long roomGraphVersion,
    long spawnIndexVersion,
    long validationVersion,
    NCConnectivityDebugZone[] zones,
    NCConnectivityDebugRoom[] rooms,
    NCConnectivityDebugCityPortal[] cityPortals,
    NCConnectivityDebugRoomPortal[] roomPortals,
    NCConnectivityDebugIssue[] issues) : EntityEventArgs
{
    public bool Enabled { get; } = enabled;
    public bool Authorized { get; } = authorized;
    public long CityGraphVersion { get; } = cityGraphVersion;
    public long RoomGraphVersion { get; } = roomGraphVersion;
    public long SpawnIndexVersion { get; } = spawnIndexVersion;
    public long ValidationVersion { get; } = validationVersion;
    public NCConnectivityDebugZone[] Zones { get; } = zones;
    public NCConnectivityDebugRoom[] Rooms { get; } = rooms;
    public NCConnectivityDebugCityPortal[] CityPortals { get; } = cityPortals;
    public NCConnectivityDebugRoomPortal[] RoomPortals { get; } = roomPortals;
    public NCConnectivityDebugIssue[] Issues { get; } = issues;
}
