// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Vehicle.Prototypes;

/// <summary>Physical properties of an installed part; no item entity is needed inside the chassis.</summary>
[Prototype("ncVehiclePart")]
public sealed partial class NCVehiclePartPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public string Mount = string.Empty;
    [DataField(required: true)] public float Mass;
    /// <summary>Intrinsic yaw inertia about the part's mounting point.</summary>
    [DataField] public float Inertia;
    [DataField] public EntProtoId? Item;
    /// <summary>Multipliers from connected modules are compiled outside the physics loop.</summary>
    [DataField] public float PowerMultiplier = 1;
    [DataField] public bool PowerDependsOnCondition;
    [DataField] public float EngineWearShare;
    [DataField] public float GearboxWearShare;
    [DataField] public float PerformanceMultiplier = 1;
    [DataField] public float ConditionFloor;
    [DataField] public float WheelWearShare;
    [DataField] public bool AutomaticTransmission;
    [DataField] public bool AllWheelDrive;
    [DataField] public string RepairCategory = "Mechanical";
    [DataField] public float MinimumRepairCondition;
    [DataField] public float MaximumRepairCondition = 0.95f;
    [DataField] public float BatteryCapacity;
    [DataField] public float InitialCharge;
}
