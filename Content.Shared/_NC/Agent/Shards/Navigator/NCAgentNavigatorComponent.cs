// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Coordinates;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Agent.Shards.Navigator;

/// <summary>
/// Data-only storage for persistent city waypoints held by one physical NAVIGATOR shard.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCAgentNavigatorComponent : Component
{
    [DataField, AutoNetworkedField]
    public List<NCAgentWaypoint> Waypoints = [];

    [DataField, AutoNetworkedField]
    public int MaximumWaypoints = 24;

    [DataField, AutoNetworkedField]
    public int MaximumLabelLength = 30;
}

/// <summary>
/// Stable waypoint identity avoids ambiguous deletion when labels are duplicated.
/// </summary>
[Serializable, NetSerializable, DataRecord]
public sealed partial class NCAgentWaypoint
{
    public readonly Guid Id;
    public readonly string Label;
    public readonly NCMapCoordinates Coordinates;
    public readonly string ZonePath;

    public NCAgentWaypoint(Guid id, string label, NCMapCoordinates coordinates, string zonePath)
    {
        Id = id;
        Label = label;
        Coordinates = coordinates;
        ZonePath = zonePath;
    }
}
