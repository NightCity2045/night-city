// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle.Components;
using Content.Shared._NC.Vehicle.Assembly;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Vehicle;

/// <summary>Predicted thermal integration; the server bridges measured wear to physical installed modules.</summary>
public sealed partial class NCVehicleConditionSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;

    /// <summary>Shared read-only check also supplies the dashboard's launch diagnosis.</summary>
    public bool CanStart(EntityUid uid, out LocId? reason)
    {
        reason = null;
        if (TryComp<NCVehicleBodyComponent>(uid, out var shell) && shell.Burning)
            reason = "nc-vehicle-start-burning";
        TryComp<NCVehicleDriveComponent>(uid, out var drive);
        var efficiency = drive == null ? 1 : NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Starting);
        if (reason == null && drive != null)
        {
            if (drive.Overturned)
                reason = "nc-vehicle-overturned";
            else if (efficiency <= 0)
                reason = "nc-vehicle-start-starter-fault";
            else if (NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Ignition) <= 0)
                reason = "nc-vehicle-start-ignition-fault";
        }
        if (reason == null && TryComp<NCVehicleConditionComponent>(uid, out var state))
        {
            if (state.EngineTemperature >= state.ShutdownTemperature)
                reason = "nc-vehicle-start-overheated";
            else if (state.StarterCharge <= 0 || state.BatteryCharge < state.StarterCharge / efficiency)
                reason = "nc-vehicle-start-battery";
        }
        return reason == null;
    }

    public bool TryStart(EntityUid uid)
    {
        if (!CanStart(uid, out _))
            return false;
        if (!TryComp<NCVehicleConditionComponent>(uid, out var state))
            return true;
        var efficiency = TryComp<NCVehicleDriveComponent>(uid, out var drive)
            ? NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Starting) : 1;
        state.BatteryCharge -= state.StarterCharge / efficiency;
        Dirty(uid, state);
        return true;
    }

    public void Advance(EntityUid uid, float speed, float dt)
    {
        if (!TryComp<NCVehicleConditionComponent>(uid, out var state) ||
            !TryComp<NCVehicleDriveComponent>(uid, out var drive) || !Valid(state) || !float.IsFinite(dt) || dt <= 0)
            return;
        state.Elapsed += dt;
        if (state.Elapsed < state.Interval)
            return;
        dt = state.Elapsed;
        state.Elapsed = 0;
        var running = drive.EngineRunning;
        state.OilPressure = running ? state.NominalOilPressure * state.OilFraction *
            NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Lubrication) : 0;
        var cooling = state.PassiveCooling + state.CoolantFraction *
            NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Cooling) *
            (state.RadiatorCooling + state.AirCooling * speed);
        var heat = running ? state.IdleHeat + state.LoadHeat * drive.Throttle : 0;
        if (drive.Calibration is { } calibration && _prototypes.TryIndex(calibration, out var setup) &&
            NCVehicleCalibrationValidator.IsValid(setup))
            heat *= setup.HeatMultiplier;
        // Implicit Newton cooling remains dissipative even on a delayed server tick.
        state.EngineTemperature = state.AmbientTemperature +
            ((state.EngineTemperature - state.AmbientTemperature) + heat * dt / state.EngineHeatCapacity) /
            (1 + cooling * dt / state.EngineHeatCapacity);
        state.BrakeTemperature = state.AmbientTemperature +
            ((state.BrakeTemperature - state.AmbientTemperature) + state.BrakeHeat / state.BrakeHeatCapacity) /
            (1 + state.BrakeCooling * dt / state.BrakeHeatCapacity);
        state.BrakeHeat = 0;
        state.EnginePower = Math.Clamp((state.ShutdownTemperature - state.EngineTemperature) /
            (state.ShutdownTemperature - state.OverheatTemperature), 0, 1);
        state.BrakePower = state.BrakeFluidFraction * NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Hydraulics) *
            Math.Clamp((state.BrakeFailureTemperature - state.BrakeTemperature) /
            (state.BrakeFailureTemperature - state.BrakeFadeTemperature), 0, 1);
        if (running)
        {
            state.BatteryCharge = Math.Clamp(state.BatteryCharge +
                (state.AlternatorAmps * NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Charging) -
                 state.IgnitionAmps) * dt / 3600, 0, state.BatteryCapacity);
            state.EngineWear += dt * (state.NormalWear * (1 + drive.Throttle) +
                state.DryWear * Math.Clamp(1 - state.OilFraction *
                    NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Lubrication) / state.MinimumOil, 0, 1) +
                state.HotWear * (1 - state.EnginePower));
            drive.GearboxWear += dt * state.DryTransmissionWear * (1 - state.TransmissionFraction);
            if (state.BatteryCharge <= 0 || state.EnginePower <= 0 ||
                NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Ignition) <= 0)
                drive.EngineRunning = false;
        }
        state.WearElapsed += dt;
        if (state.WearElapsed >= state.WearInterval)
        {
            var wear = new NCVehicleWearEvent(state.EngineWear, drive.GearboxWear);
            state.EngineWear = 0;
            drive.GearboxWear = 0;
            state.WearElapsed = 0;
            RaiseLocalEvent(uid, ref wear);
            // Clear on both prediction sides after the server has consumed the same interval's wear.
            Array.Clear(drive.WheelWear);
        }
        Dirty(uid, state);
        var maintenance = new NCVehicleMaintenanceEvent(dt);
        RaiseLocalEvent(uid, ref maintenance);
    }

    private static bool Valid(NCVehicleConditionComponent value)
    {
        if (!NCVehicleDriveMath.Positive(value.EngineHeatCapacity) ||
            !NCVehicleDriveMath.Positive(value.BrakeHeatCapacity) ||
            !NCVehicleDriveMath.Positive(value.MinimumOil) ||
            !NCVehicleDriveMath.Positive(value.WearInterval) ||
            !NCVehicleDriveMath.Positive(value.BatteryCapacity) ||
            !NCVehicleDriveMath.Positive(value.StarterCharge) ||
            !NCVehicleDriveMath.Positive(value.Interval))
            return false;
        if (!NCVehicleDriveMath.Nonnegative(value.IdleHeat) ||
            !NCVehicleDriveMath.Nonnegative(value.NominalOilPressure) ||
            !NCVehicleDriveMath.Nonnegative(value.DryTransmissionWear) ||
            !NCVehicleDriveMath.Nonnegative(value.UnassistedSteering) || value.UnassistedSteering > 1 ||
            !NCVehicleDriveMath.Nonnegative(value.LoadHeat) ||
            !NCVehicleDriveMath.Nonnegative(value.PassiveCooling) ||
            !NCVehicleDriveMath.Nonnegative(value.RadiatorCooling) ||
            !NCVehicleDriveMath.Nonnegative(value.AirCooling) ||
            !NCVehicleDriveMath.Nonnegative(value.BrakeCooling) ||
            !NCVehicleDriveMath.Nonnegative(value.NormalWear) ||
            !NCVehicleDriveMath.Nonnegative(value.DryWear) ||
            !NCVehicleDriveMath.Nonnegative(value.HotWear) ||
            !NCVehicleDriveMath.Nonnegative(value.IgnitionAmps) ||
            !NCVehicleDriveMath.Nonnegative(value.AlternatorAmps) ||
            !NCVehicleDriveMath.Nonnegative(value.AmbientTemperature) ||
            !NCVehicleDriveMath.Nonnegative(value.OverheatTemperature) ||
            !NCVehicleDriveMath.Nonnegative(value.ShutdownTemperature) ||
            !NCVehicleDriveMath.Nonnegative(value.BrakeFadeTemperature) ||
            !NCVehicleDriveMath.Nonnegative(value.BrakeFailureTemperature))
            return false;
        return value.ShutdownTemperature > value.OverheatTemperature && value.BrakeFailureTemperature > value.BrakeFadeTemperature;
    }
}

[ByRefEvent]
public readonly record struct NCVehicleWearEvent(float Engine, float Gearbox);

[ByRefEvent]
public readonly record struct NCVehicleMaintenanceEvent(float Seconds);
