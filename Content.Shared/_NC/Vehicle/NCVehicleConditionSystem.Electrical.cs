// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Vehicle.Components;

namespace Content.Shared._NC.Vehicle;

public sealed partial class NCVehicleConditionSystem
{
    /// <summary>Admits a bounded physical auxiliary load using the same battery as ignition and accessories.</summary>
    public bool TryUseElectricalEnergy(EntityUid uid, float joules)
    {
        if (!float.IsFinite(joules) || joules < 0 || !TryComp<NCVehicleConditionComponent>(uid, out var state) ||
            !float.IsFinite(state.BatteryVoltage) || state.BatteryVoltage <= 0 ||
            !float.IsFinite(state.BatteryCharge) || state.BatteryCharge < 0) return false;
        var ampereHours = joules / state.BatteryVoltage / 3600;
        var wasPowered = state.BatteryCharge > 0;
        var enough = ampereHours <= state.BatteryCharge;
        state.BatteryCharge = Math.Max(0, state.BatteryCharge - ampereHours);
        Dirty(uid, state);
        if (wasPowered && state.BatteryCharge <= 0)
        {
            var changed = new NCVehicleElectricalPowerChangedEvent(false);
            RaiseLocalEvent(uid, ref changed);
        }
        return enough;
    }
}

[ByRefEvent]
public readonly record struct NCVehicleElectricalPowerChangedEvent(bool Powered);
