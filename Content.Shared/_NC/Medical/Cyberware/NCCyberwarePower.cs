// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Cyberware;

/// <summary>The physical holder owns vanilla item slots, never a second charge balance.</summary>
[RegisterComponent]
public sealed partial class NCCyberwarePowerBusComponent : Component
{
    public const string MainSlot = "nc_main_cell";
    public const string ReserveSlot = "nc_reserve_cell";
    [DataField] public float MaximumPower = 40f;
    [DataField] public float LowChargeFraction = 0.2f;
    [ViewVariables] public bool Restoring;
}

[RegisterComponent]
public sealed partial class NCCyberwareBioconverterComponent : Component
{
    [DataField] public float MaximumPower = 3f;
    [DataField] public float EnergyPerNutrition = 120f;
    [DataField] public float ProtectedNutrition = 100f;
}

/// <summary>Identity and bounded runtime EMP replay protection follow the same real cell when it is removed.</summary>
[RegisterComponent]
public sealed partial class NCCyberwareCellComponent : Component
{
    [DataField] public Guid StableInstanceId;
    [ViewVariables] public Queue<Guid> EmpPulses = new();
}

[RegisterComponent, UnsavedComponent]
public sealed partial class NCCyberwarePowerStateComponent : Component
{
    [ViewVariables] public TimeSpan LastAdvancedAt;
    [ViewVariables] public new bool Initialized;
    [ViewVariables] public bool Advancing;
    [ViewVariables] public NCCyberwarePowerWarning Warning;
}

public enum NCCyberwarePowerWarning : byte
{
    None,
    Low,
    Reserve,
    Unavailable,
}

[DataDefinition]
public sealed partial class NCCyberwarePowerBusSnapshot
{
    [DataField] public NCCyberwareCellSnapshot? Main;
    [DataField] public NCCyberwareCellSnapshot? Reserve;
}

[DataDefinition]
public sealed partial class NCCyberwareCellSnapshot
{
    [DataField] public EntProtoId Prototype;
    [DataField] public Guid StableInstanceId;
    [DataField] public float Charge;
    [DataField] public TimeSpan? RechargeDelay;
}

public enum NCCyberwarePowerPriority : byte
{
    LifeSupport,
    Control,
    Professional,
    Combat,
}

public enum NCCyberwarePowerSource : byte
{
    None,
    Main,
    Reserve,
}

public readonly record struct NCCyberwarePowerDemand(Guid Id, NCCyberwarePowerPriority Priority, float Watts);
public readonly record struct NCCyberwarePowerGrant(Guid Id, NCCyberwarePowerSource Source, float Watts);

/// <summary>Stable all-or-nothing allocation. Capacity and instantaneous bus power have separate meanings.</summary>
public static class NCCyberwarePowerMath
{
    public static List<NCCyberwarePowerGrant> Allocate(IEnumerable<NCCyberwarePowerDemand> demands,
        float maximumPower, bool mainAvailable, bool reserveAvailable)
    {
        var remaining = float.IsFinite(maximumPower) ? MathF.Max(0f, maximumPower) : 0f;
        var grants = new List<NCCyberwarePowerGrant>();
        foreach (var demand in demands.OrderBy(value => value.Priority).ThenBy(value => value.Id))
        {
            var source = NCCyberwarePowerSource.None;
            if (float.IsFinite(demand.Watts) && demand.Watts >= 0f && demand.Watts <= remaining)
            {
                source = mainAvailable ? NCCyberwarePowerSource.Main
                    : reserveAvailable && demand.Priority == NCCyberwarePowerPriority.LifeSupport
                        ? NCCyberwarePowerSource.Reserve : NCCyberwarePowerSource.None;
            }
            var granted = source == NCCyberwarePowerSource.None ? 0f : demand.Watts;
            remaining -= granted;
            grants.Add(new NCCyberwarePowerGrant(demand.Id, source, granted));
        }
        return grants;
    }

    public static float ConversionEnergy(float seconds, float rate, float nutrition, float protectedNutrition,
        float energyPerNutrition, float freeCapacity)
    {
        if (!float.IsFinite(seconds) || !float.IsFinite(rate) || !float.IsFinite(nutrition) ||
            !float.IsFinite(protectedNutrition) || !float.IsFinite(energyPerNutrition) ||
            !float.IsFinite(freeCapacity) || seconds <= 0f || rate <= 0f || energyPerNutrition <= 0f)
            return 0f;
        return MathF.Max(0f, MathF.Min(rate * seconds,
            MathF.Min(MathF.Max(0f, nutrition - protectedNutrition) * energyPerNutrition, freeCapacity)));
    }

    /// <summary>Exact integral of linear heat above the damage threshold, including time at capacity.</summary>
    public static (float Heat, float Damage) AdvanceHeat(float heat, float rate, float seconds,
        float capacity, float damageThreshold, float damagePerSecond)
    {
        var end = Math.Clamp(heat + rate * seconds, 0f, capacity);
        if (seconds <= 0f || capacity <= damageThreshold || damagePerSecond <= 0f)
            return (end, 0f);
        var moving = rate == 0f ? 0f : Math.Clamp((end - heat) / rate, 0f, seconds);
        var startExcess = MathF.Max(0f, heat - damageThreshold);
        var endExcess = MathF.Max(0f, end - damageThreshold);
        var above = moving;
        if (rate > 0f && heat < damageThreshold)
            above = MathF.Max(0f, moving - (damageThreshold - heat) / rate);
        else if (rate < 0f && end < damageThreshold)
            above = MathF.Max(0f, (heat - damageThreshold) / -rate);
        var integral = (startExcess + endExcess) * 0.5f * above + endExcess * (seconds - moving);
        return (end, integral / (capacity - damageThreshold) * damagePerSecond);
    }
}
