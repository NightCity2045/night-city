// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Numerics;
using Content.Shared._NC.City.ZoneTemplates;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Serialization;
using Robust.Shared.GameStates;

namespace Content.Shared._NC.Missions.Components;

/// <summary>Semantic mapper point resolved from a mission definition after static deployment.</summary>
[RegisterComponent]
public sealed partial class NCMissionDynamicPointComponent : Component
{
    [DataField(required: true)] public string PoolId = string.Empty;
    [DataField] public NCMissionExtractionPolicy ExtractionPolicy = NCMissionExtractionPolicy.DeleteOnCleanup;
}

/// <summary>Tags temporary or extractable entities with stable run ownership.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCMissionRunEntityComponent : Component
{
    [DataField(required: true), AutoNetworkedField] public Guid RunId;
    [DataField(required: true), AutoNetworkedField] public Guid DeploymentId;
    [DataField, AutoNetworkedField] public NCMissionExtractionPolicy ExtractionPolicy;
}

/// <summary>Explicitly marks entities that must prevent rebuilding a mission zone.</summary>
[RegisterComponent]
public sealed partial class NCMissionOccupancyBlockerComponent : Component
{
}

/// <summary>
/// Semantic mapper point used to move external actors and mobs out before a timed mission cleanup.
/// Exactly one point is expected in a public mission template.
/// </summary>
[RegisterComponent]
public sealed partial class NCMissionEvacuationPointComponent : Component
{
    /// <summary>Map-space offset from the in-layout marker to a safe point outside the mission footprint.</summary>
    [DataField(required: true)]
    public Vector2 ExitOffset;
}

/// <summary>Blocks an exterior mission door until all floors are ready and while cleanup is running.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCMissionEntranceComponent : Component
{
    [DataField(required: true, customTypeSerializer: typeof(NCMissionSlotIdSerializer))]
    [AutoNetworkedField]
    public NCMissionSlotId SlotId;

    [DataField, AutoNetworkedField]
    public bool Open;
}
