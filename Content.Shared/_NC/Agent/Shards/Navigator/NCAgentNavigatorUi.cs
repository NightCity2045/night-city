// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Coordinates;
using Content.Shared.CartridgeLoader;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Agent.Shards.Navigator;

[Serializable, NetSerializable]
public sealed class NCAgentNavigatorUiState(
    NCMapCoordinates? current,
    string currentZone,
    List<NCAgentWaypoint> waypoints) : BoundUserInterfaceState
{
    public readonly NCMapCoordinates? Current = current;
    public readonly string CurrentZone = currentZone;
    public readonly List<NCAgentWaypoint> Waypoints = waypoints;
}

[Serializable, NetSerializable]
public sealed class NCAgentNavigatorUiMessage(
    NCAgentNavigatorAction action,
    Guid waypointId,
    string label) : CartridgeMessageEvent
{
    public readonly NCAgentNavigatorAction Action = action;
    public readonly Guid WaypointId = waypointId;
    public readonly string Label = label;
}

[Serializable, NetSerializable]
public enum NCAgentNavigatorAction : byte
{
    Refresh,
    SaveCurrent,
    Delete,
}
