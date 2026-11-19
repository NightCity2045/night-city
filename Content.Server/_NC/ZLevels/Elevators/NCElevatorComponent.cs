// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Maps;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.ZLevels.Elevators;

/// <summary>Mapper-authored settings shared by the spawner and its physical cabin.</summary>
[DataDefinition]
public sealed partial class NCElevatorSettings
{
    [DataField]
    public TimeSpan TravelTime = TimeSpan.FromSeconds(8);

    /// <summary>Empty means every existing adjacent depth in this Z-network.</summary>
    [DataField]
    public HashSet<int> Stops = [];

    [DataField]
    public ProtoId<ContentTileDefinition> FloorTile = "FloorNCElevator";

    [DataField]
    public EntProtoId PanelPrototype = "NCElevatorPanel";

    [DataField]
    public EntProtoId BarrierPrototype = "NCElevatorTravelBarrier";

    [DataField]
    public SoundSpecifier MotorSound = new SoundPathSpecifier("/Audio/Ambience/Objects/engine_hum.ogg")
    {
        Params = AudioParams.Default.WithVolume(-12),
    };

    [DataField]
    public SoundSpecifier ArrivalSound = new SoundPathSpecifier("/Audio/Machines/ding.ogg");
}

/// <summary>Server-owned controller attached to one independent 3x3 floor grid.</summary>
[RegisterComponent, AutoGenerateComponentPause]
public sealed partial class NCElevatorComponent : Component
{
    [DataField]
    public NCElevatorSettings Settings = new();

    /// <summary>Reconnects a saved mapper grid to its persistent controller.</summary>
    [DataField]
    public Guid CabinId;

    [ViewVariables]
    public EntityUid? Controller;

    [ViewVariables]
    public int? TargetDepth;

    [ViewVariables]
    public EntityUid? TargetMap;

    /// <summary>One landing call destination; intermediate hops keep the cabin closed.</summary>
    [ViewVariables]
    public int? CalledDepth;

    [AutoPausedField]
    public TimeSpan ArrivesAt;

    [ViewVariables]
    public EntityUid? Motor;

    [ViewVariables]
    public EntityUid? Barrier;
}

/// <summary>Persistent mapper controller. Rebuilds its physical cabin at the last committed stop.</summary>
[RegisterComponent]
public sealed partial class NCElevatorSpawnerComponent : Component
{
    /// <summary>Matches landing call buttons within this Z-network.</summary>
    [DataField]
    public string ElevatorId = string.Empty;

    [DataField]
    public NCElevatorSettings Settings = new();

    [DataField]
    public Guid CabinId;

    /// <summary>Durable stop; an interrupted trip resumes parked at its departure floor.</summary>
    [DataField]
    public int? CurrentDepth;

    [ViewVariables]
    public EntityUid? Cabin;
}

/// <summary>Landing button linked to one persistent elevator controller.</summary>
[RegisterComponent]
public sealed partial class NCElevatorCallButtonComponent : Component
{
    [DataField]
    public string ElevatorId = string.Empty;
}
