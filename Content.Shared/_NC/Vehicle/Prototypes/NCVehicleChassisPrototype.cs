// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Vehicle.Assembly;
using Content.Shared._NC.Vehicle.Workshop;
using Content.Shared.Stacks;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.Prototype.Array;

namespace Content.Shared._NC.Vehicle.Prototypes;

/// <summary>Round-local assembly definition. The bare chassis excludes every installed part.</summary>
[Prototype("ncVehicleChassis")]
public sealed partial class NCVehicleChassisPrototype : IPrototype, IInheritingPrototype
{
    [ParentDataField(typeof(AbstractPrototypeIdArraySerializer<NCVehicleChassisPrototype>))]
    public string[]? Parents { get; private set; }
    [AbstractDataField, NeverPushInheritance]
    public bool Abstract { get; private set; }
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public ProtoId<NCVehicleWorkshopPrototype>? Workshop;
    [DataField(required: true)] public float Mass;
    /// <summary>Yaw inertia about the bare chassis center, in mass * distance squared.</summary>
    [DataField(required: true)] public float Inertia;
    [DataField] public Vector2 CenterOfMass;
    [DataField(required: true)] public Dictionary<string, NCVehicleSlotDefinition> Slots = new();
    [DataField] public Dictionary<string, NCVehicleSeatDefinition> Seats = new();
    [DataField] public float ServiceRange = 2;
    [DataField] public float ServiceMaxSpeed = 0.1f;
    [DataField] public float WorkshopSearchRange = 6;
    [DataField] public float RightingSeconds = 30;
    [DataField] public ProtoId<StackPrototype> Fasteners = "NCVehicleFasteners";
    [DataField] public HashSet<ProtoId<NCVehicleTuningPrototype>> Calibrations = new();
    [DataField] public Vector2 FrontTowPoint;
    [DataField] public Vector2 RearTowPoint;
    [DataField] public float MassRefreshInterval = 0.5f;
    [DataField] public Dictionary<string, NCVehicleFluidMassDefinition> FluidMass = new();
    /// <summary>Additional physical containers, such as a trunk. Seat containers are derived from Seats.</summary>
    [DataField] public Dictionary<string, Vector2> CargoMass = new();
    [DataField] public ProtoId<NCVehicleHandlingPrototype>? ManualHandling;
    [DataField] public ProtoId<NCVehicleHandlingPrototype>? AutomaticHandling;
    [DataField] public ProtoId<NCVehicleHandlingPrototype>? AllWheelHandling;
    [DataField] public ProtoId<NCVehicleHandlingPrototype>? AutomaticAllWheelHandling;
}

[DataDefinition]
public sealed partial class NCVehicleFluidMassDefinition
{
    [DataField(required: true)] public float KilogramsPerUnit;
    [DataField] public Vector2 Position;
}

[DataDefinition]
public sealed partial class NCVehicleSeatDefinition
{
    [DataField(required: true)] public LocId Name;
    [DataField] public Vector2 Position;
    [DataField(required: true)] public Vector2 Exit;
    [DataField] public bool Driver;
    [DataField] public bool HasBelt = true;
}

/// <summary>Installation geometry and prerequisites, separate from the mutable installed instance.</summary>
[DataDefinition]
public sealed partial class NCVehicleSlotDefinition
{
    [DataField] public LocId Name = "nc-vehicle-module";
    [DataField] public string ToolQuality = "Screwing";
    [DataField] public float ServiceSeconds = 30;
    [DataField] public bool RequiresLift;
    [DataField] public bool RequiresHoist;
    [DataField] public bool RequiresOpenHood;
    [DataField(required: true)] public string Mount = string.Empty;
    [DataField] public Vector2 Position;
    [DataField] public bool Required;
    [DataField] public ProtoId<NCVehiclePartPrototype>? DefaultPart;
    /// <summary>These slots must be occupied before this slot can contain a part.</summary>
    [DataField] public HashSet<string> Requires = new();
    [DataField] public HashSet<string> BlockedBy = new();
    /// <summary>Children travel with this carrier instead of being destroyed or left unsupported.</summary>
    [DataField] public HashSet<string> ExtractWith = new();
    [DataField] public Dictionary<string, NCVehicleWorkshopPointDefinition> WorkPoints = new();
    /// <summary>Fallback torque in Nm for fastener points in this slot.</summary>
    [DataField] public float FastenerTorque;
    /// <summary>-1 addresses a vehicle-wide circuit; other values match the handling wheel order.</summary>
    [DataField] public int WheelIndex = -1;
    [DataField] public HashSet<NCVehicleFunction> Functions = new();
    /// <summary>An uninstalled optional conversion does not constrain the stock vehicle's circuits.</summary>
    [DataField] public bool OptionalFunctions;
}
