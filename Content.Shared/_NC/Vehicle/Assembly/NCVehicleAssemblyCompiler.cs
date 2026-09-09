// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Vehicle.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Vehicle.Assembly;

/// <summary>
/// Pure assembly validation and mass reduction. Called when assembly data changes, never per physics tick.
/// It neither mutates prototypes nor creates entities; the entity system owns publishing the result.
/// </summary>
public static class NCVehicleAssemblyCompiler
{
    public static bool TryCompile(
        NCVehicleChassisPrototype chassis,
        IReadOnlyDictionary<string, NCVehicleInstalledPart> installed,
        Func<ProtoId<NCVehiclePartPrototype>, NCVehiclePartPrototype?> resolve,
        out NCVehicleAssemblyProfile profile,
        out NCVehicleAssemblyFailure failure)
    {
        profile = default;
        failure = default;
        if (!Positive(chassis.Mass) || !Positive(chassis.Inertia) || !Finite(chassis.CenterOfMass) ||
            !Positive(chassis.MassRefreshInterval))
            return Fail(NCVehicleAssemblyError.InvalidChassis, "", out failure);
        foreach (var (id, fluid) in chassis.FluidMass)
            if (string.IsNullOrWhiteSpace(id) || !Positive(fluid.KilogramsPerUnit) || !Finite(fluid.Position))
                return Fail(NCVehicleAssemblyError.InvalidChassis, id, out failure);
        foreach (var (id, position) in chassis.CargoMass)
            if (string.IsNullOrWhiteSpace(id) || !Finite(position))
                return Fail(NCVehicleAssemblyError.InvalidChassis, id, out failure);

        // Validate the complete definition, including empty branches, so a latent cycle cannot be installed later.
        foreach (var (id, slot) in chassis.Slots)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(slot.Mount) || !Finite(slot.Position) ||
                slot.WheelIndex is < -1 or >= 64)
                return Fail(NCVehicleAssemblyError.InvalidSlot, id, out failure);
            foreach (var function in slot.Functions)
                if (function >= NCVehicleFunction.Count)
                    return Fail(NCVehicleAssemblyError.InvalidSlot, id, out failure);
            foreach (var dependency in slot.Requires)
            {
                if (!chassis.Slots.ContainsKey(dependency))
                    return Fail(NCVehicleAssemblyError.MissingDependency, id, out failure);
            }
        }

        // Iterative topological validation avoids recursion on malformed designer-authored graphs.
        var resolved = new HashSet<string>();
        while (resolved.Count < chassis.Slots.Count)
        {
            var previous = resolved.Count;
            foreach (var (id, slot) in chassis.Slots)
            {
                if (!resolved.Contains(id) && slot.Requires.IsSubsetOf(resolved))
                    resolved.Add(id);
            }
            if (resolved.Count == previous)
                return Fail(NCVehicleAssemblyError.DependencyCycle, "", out failure);
        }

        var mass = (double) chassis.Mass;
        var weightedX = mass * chassis.CenterOfMass.X;
        var weightedY = mass * chassis.CenterOfMass.Y;
        var parts = new List<(NCVehiclePartPrototype Part, Vector2 Position)>(installed.Count);
        foreach (var (id, state) in installed)
        {
            if (!chassis.Slots.TryGetValue(id, out var slot))
                return Fail(NCVehicleAssemblyError.UnknownSlot, id, out failure);
            if (!float.IsFinite(state.Condition) || state.Condition < 0 || state.Condition > 1)
                return Fail(NCVehicleAssemblyError.InvalidCondition, id, out failure);
            if (state.StoredCharge is { } charge && (!float.IsFinite(charge) || charge < 0))
                return Fail(NCVehicleAssemblyError.InvalidCondition, id, out failure);
            var part = resolve(state.Prototype);
            if (part == null)
                return Fail(NCVehicleAssemblyError.UnknownPart, id, out failure);
            if (!Positive(part.Mass) || !float.IsFinite(part.Inertia) || part.Inertia < 0 ||
                !Positive(part.PowerMultiplier) || !float.IsFinite(part.EngineWearShare) || part.EngineWearShare < 0 ||
                !float.IsFinite(part.GearboxWearShare) || part.GearboxWearShare < 0 ||
                !Positive(part.PerformanceMultiplier) || !float.IsFinite(part.ConditionFloor) ||
                !float.IsFinite(part.MinimumRepairCondition) || !float.IsFinite(part.MaximumRepairCondition) ||
                !float.IsFinite(part.BatteryCapacity) || part.BatteryCapacity < 0 ||
                !float.IsFinite(part.InitialCharge) || part.InitialCharge < 0 || part.InitialCharge > part.BatteryCapacity ||
                part.MinimumRepairCondition < 0 || part.MaximumRepairCondition > 1 || part.MinimumRepairCondition > part.MaximumRepairCondition ||
                part.ConditionFloor is < 0 or > 1 || !float.IsFinite(part.WheelWearShare) || part.WheelWearShare < 0)
                return Fail(NCVehicleAssemblyError.InvalidPart, id, out failure);
            if (!string.Equals(slot.Mount, part.Mount, StringComparison.Ordinal))
                return Fail(NCVehicleAssemblyError.IncompatibleMount, id, out failure);
            foreach (var dependency in slot.Requires)
            {
                if (!installed.ContainsKey(dependency))
                    return Fail(NCVehicleAssemblyError.UnoccupiedDependency, id, out failure);
            }

            mass += part.Mass;
            weightedX += (double) part.Mass * slot.Position.X;
            weightedY += (double) part.Mass * slot.Position.Y;
            parts.Add((part, slot.Position));
        }

        var centerX = weightedX / mass;
        var centerY = weightedY / mass;
        // Parallel-axis theorem: intrinsic inertia plus each mass's offset from the combined center.
        var inertia = chassis.Inertia + chassis.Mass * DistanceSquared(chassis.CenterOfMass, centerX, centerY);
        foreach (var (part, position) in parts)
            inertia += part.Inertia + part.Mass * DistanceSquared(position, centerX, centerY);

        var center = new Vector2((float) centerX, (float) centerY);
        if (!Positive((float) mass) || !Positive((float) inertia) || !Finite(center))
            return Fail(NCVehicleAssemblyError.InvalidMassProperties, "", out failure);

        var complete = true;
        foreach (var (id, slot) in chassis.Slots)
            complete &= !slot.Required || installed.ContainsKey(id);

        profile = new NCVehicleAssemblyProfile((float) mass, center, (float) inertia, complete);
        return true;
    }

    private static double DistanceSquared(Vector2 point, double x, double y)
    {
        var dx = point.X - x;
        var dy = point.Y - y;
        return dx * dx + dy * dy;
    }

    private static bool Positive(float value) => float.IsFinite(value) && value > 0;
    private static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool Fail(NCVehicleAssemblyError error, string slot, out NCVehicleAssemblyFailure failure)
    {
        failure = new NCVehicleAssemblyFailure(error, slot);
        return false;
    }
}
