// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle.Prototypes;

namespace Content.Shared._NC.Vehicle.Assembly;

/// <summary>Rejects invalid workshop data before it can introduce non-finite forces or fluid consumption.</summary>
public static class NCVehicleCalibrationValidator
{
    public static bool IsValid(NCVehicleTuningPrototype setup)
        => Positive(setup.EngineMultiplier) && Positive(setup.FrontBrakeMultiplier) &&
            Positive(setup.RearBrakeMultiplier) && Positive(setup.SteeringMultiplier) &&
            Positive(setup.DifferentialMultiplier) && Positive(setup.HeatMultiplier) &&
            Positive(setup.FuelMultiplier) && float.IsFinite(setup.Seconds) && setup.Seconds >= 0;

    private static bool Positive(float value) => float.IsFinite(value) && value > 0;
}
