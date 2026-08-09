// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using Content.Shared._NC.City.ZoneTemplates;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Serialization;
using Robust.Shared.GameStates;

namespace Content.Shared._NC.Dungeons.Components;

/// <summary>Semantic mapper point resolved from a dungeon definition after static deployment.</summary>
[RegisterComponent]
public sealed partial class NCDungeonDynamicPointComponent : Component
{
    [DataField(required: true)] public string PoolId = string.Empty;
    [DataField] public NCDungeonExtractionPolicy ExtractionPolicy = NCDungeonExtractionPolicy.DeleteOnCleanup;
}

/// <summary>Tags temporary or extractable entities with stable run ownership.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCDungeonRunEntityComponent : Component
{
    [DataField(required: true), AutoNetworkedField] public Guid RunId;
    [DataField(required: true), AutoNetworkedField] public Guid DeploymentId;
    [DataField, AutoNetworkedField] public NCDungeonExtractionPolicy ExtractionPolicy;
}

/// <summary>Explicitly marks entities that must prevent rebuilding a dungeon zone.</summary>
[RegisterComponent]
public sealed partial class NCDungeonOccupancyBlockerComponent : Component
{
}

/// <summary>Blocks an exterior dungeon door until all floors are ready and while cleanup is running.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCDungeonEntranceComponent : Component
{
    [DataField(required: true, customTypeSerializer: typeof(NCDungeonSlotIdSerializer))]
    [AutoNetworkedField]
    public NCDungeonSlotId SlotId;

    [DataField, AutoNetworkedField]
    public bool Open;
}
