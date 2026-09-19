// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Chemistry.Reagent;
using Content.Shared._NC.Vehicle;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Vehicle;

[RegisterComponent]
public sealed partial class NCVehicleFluidsComponent : Component
{
    [DataField(required: true)] public Dictionary<NCVehicleFluidCircuit, NCVehicleFluidDefinition> Circuits = new();

    /// <summary>
    /// Converts reservoir millilitres into chemistry units when a leak reaches the floor.
    /// Reservoir loss remains expressed in millilitres so vehicle consumption and mass stay coherent.
    /// </summary>
    [DataField] public float WorldSpillScale = 0.001f;
}

[DataDefinition]
public sealed partial class NCVehicleFluidDefinition
{
    [DataField(required: true)] public string Solution = string.Empty;
    [DataField(required: true)] public ProtoId<ReagentPrototype> Reagent;
    [DataField(required: true)] public float WorkingVolume;
    [DataField] public HashSet<string> SealSlots = new();
    [DataField] public float MaximumLeakPerSecond;
}

/// <summary>A bottle fits one physical fill port. Its solution remains the authoritative consumable.</summary>
[RegisterComponent]
public sealed partial class NCVehicleFluidBottleComponent : Component
{
    [DataField(required: true)] public NCVehicleFluidCircuit Circuit;
    [DataField] public string Solution = "tank";
    [DataField(required: true)] public float TransferVolume;
}

/// <summary>A physical pan receiving used fluid from one selected workshop drain point.</summary>
[RegisterComponent]
public sealed partial class NCVehicleFluidDrainPanComponent : Component
{
    [DataField] public string Solution = "tank";
    [DataField] public float TransferVolume = 1000;
}
