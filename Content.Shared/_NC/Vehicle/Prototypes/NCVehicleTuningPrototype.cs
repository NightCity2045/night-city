// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Vehicle.Prototypes;

/// <summary>Workshop calibration, separate from installed engine and driveline hardware.</summary>
[Prototype("ncVehicleTuning")]
public sealed partial class NCVehicleTuningPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public LocId Name;
    [DataField] public float EngineMultiplier = 1;
    [DataField] public float FrontBrakeMultiplier = 1;
    [DataField] public float RearBrakeMultiplier = 1;
    [DataField] public float SteeringMultiplier = 1;
    [DataField] public float DifferentialMultiplier = 1;
    [DataField] public float HeatMultiplier = 1;
    [DataField] public float FuelMultiplier = 1;
    [DataField] public float Seconds = 30;
}
