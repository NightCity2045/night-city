// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Vehicle.Assembly;

/// <summary>Engineering outputs, not a catalog of item types. Slots declare which circuit they constrain.</summary>
public enum NCVehicleFunction : byte
{
    Starting, Ignition, Charging, Cooling, Lubrication, Steering, Hydraulics, Electronics,
    Aerodynamics, Transmission, Grip, Braking, Suspension, Drive, Lights, Horn, Washer, Airbags, SteeringAssist,
    Count,
}

/// <summary>Reduces a physical assembly into bounded scalar inputs for the predicted solver.</summary>
public static class NCVehiclePerformanceCompiler
{
    public const int Width = (int) NCVehicleFunction.Count;

    public static float[] Compile(NCVehicleChassisPrototype chassis,
        IReadOnlyDictionary<string, NCVehicleInstalledPart> installed,
        Func<ProtoId<NCVehiclePartPrototype>, NCVehiclePartPrototype?> resolve, int wheelCount)
    {
        var values = new float[Width * (wheelCount + 1)];
        Array.Fill(values, 1f);
        var occupied = new bool[values.Length];
        foreach (var (id, slot) in chassis.Slots)
        {
            if (slot.WheelIndex < -1 || slot.WheelIndex >= wheelCount)
                continue;
            if (slot.OptionalFunctions && !installed.ContainsKey(id))
                continue;
            var performance = 0f;
            if (installed.TryGetValue(id, out var state) && state.Connected && state.Fastened && resolve(state.Prototype) is { } part)
                performance = part.PerformanceMultiplier *
                    (part.ConditionFloor + (1 - part.ConditionFloor) * state.Condition);
            foreach (var function in slot.Functions)
            {
                var index = (slot.WheelIndex + 1) * Width + (int) function;
                // Series circuits are limited by their weakest member; adding ten healthy hoses does not add power.
                values[index] = occupied[index] ? Math.Min(values[index], performance) : performance;
                occupied[index] = true;
            }
        }
        return values;
    }

    public static float Get(float[] values, NCVehicleFunction function, int wheel = -1)
    {
        var index = (wheel + 1) * Width + (int) function;
        return index >= 0 && index < values.Length ? values[index] : 1;
    }
}
