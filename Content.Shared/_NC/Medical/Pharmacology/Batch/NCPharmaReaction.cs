// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Pharmacology.Batch;

/// <summary>
/// How one reaction behaves in a reactor. It is data, so an easy and a dangerous reaction use the same code and
/// differ only in these numbers: a narrow safe window, a strong exotherm or a volatile mixture make a hard one.
/// </summary>
[Prototype("ncPharmaReaction")]
public sealed partial class NCPharmaReactionProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name { get; private set; }

    [DataField]
    public float AmbientTemperature { get; private set; } = 20f;

    /// <summary>Temperatures at which the reaction runs at full efficiency.</summary>
    [DataField]
    public float OptimalMin { get; private set; } = 60f;

    [DataField]
    public float OptimalMax { get; private set; } = 75f;

    /// <summary>Temperatures at which the reaction still runs, at a fraction of its efficiency.</summary>
    [DataField]
    public float SafeMin { get; private set; } = 45f;

    [DataField]
    public float SafeMax { get; private set; } = 95f;

    /// <summary>Degrees per second one heater step adds.</summary>
    [DataField]
    public float HeaterPower { get; private set; } = 0.8f;

    [DataField]
    public float CoolerPower { get; private set; } = 0.8f;

    /// <summary>Share of the difference to the room temperature lost every second.</summary>
    [DataField]
    public float HeatLoss { get; private set; } = 0.015f;

    /// <summary>Heat the reaction itself releases, in degrees per second at full rate.</summary>
    [DataField]
    public float Exothermicity { get; private set; }

    /// <summary>Progress gained per second at full efficiency. One divided by this is the run time.</summary>
    [DataField]
    public float ProgressPerSecond { get; private set; } = 0.012f;

    [DataField]
    public float CatalystRateGain { get; private set; } = 0.6f;

    /// <summary>How much more heat a full catalyst charge makes the reaction release.</summary>
    [DataField]
    public float CatalystHeatGain { get; private set; } = 0.5f;

    [DataField]
    public float PressureBase { get; private set; } = 100f;

    /// <summary>Pressure added per degree above the room temperature, scaled by the volatility.</summary>
    [DataField]
    public float PressurePerDegree { get; private set; } = 1.1f;

    [DataField]
    public float Volatility { get; private set; } = 1f;

    [DataField]
    public float PressureSoftLimit { get; private set; } = 190f;

    [DataField]
    public float PressureHardLimit { get; private set; } = 260f;

    /// <summary>Instability at which the operator is warned.</summary>
    [DataField]
    public float WarningInstability { get; private set; } = 0.4f;

    /// <summary>Level I: the product degrades into sludge.</summary>
    [DataField]
    public float DegradeInstability { get; private set; } = 0.55f;

    /// <summary>Level II: a toxic release.</summary>
    [DataField]
    public float ReleaseInstability { get; private set; } = 0.75f;

    /// <summary>Level III: the vessel ruptures and burns.</summary>
    [DataField]
    public float RuptureInstability { get; private set; } = 0.9f;

    /// <summary>Only a reaction marked hazardous can reach level IV, a severe explosion.</summary>
    [DataField]
    public bool Hazardous { get; private set; }
}

/// <summary>Process values of one run. Everything the operator can read on the gauges.</summary>
public sealed class NCReactorState
{
    public float Temperature;
    public float Pressure;
    public float Mixing;
    public float Catalyst;
    public float Progress;
    public float Instability;
    public float PeakInstability;
    public float Elapsed;
    public float SecondsOptimal;
    public float SecondsSafe;
    public float SecondsOutside;
    public float MixingIntegral;
    public float TemperatureRate;
    public bool Degraded;
    public bool Quenched;
}

/// <summary>What the operator sets. Catalyst, quench and stop are one-time actions, not controls.</summary>
public readonly record struct NCReactorControls(
    int Heater,
    int Cooler,
    bool Stirring,
    bool Venting);

[Flags]
[Serializable, NetSerializable]
public enum NCReactorWarnings : byte
{
    None = 0,
    HighTemperature = 1 << 0,
    NearSafeLimit = 1 << 1,
    HighPressure = 1 << 2,
    PoorMixing = 1 << 3,
    Runaway = 1 << 4,
    InstabilityRising = 1 << 5,
}

/// <summary>How the product of a finished run compares with a clean one.</summary>
public readonly record struct NCReactorOutcome(
    float PurityFactor,
    float HomogeneityFactor,
    float YieldFactor);

/// <summary>
/// The reactor model. It is pure and deterministic: the same controls always lead to the same run, so a
/// failure always has a cause the operator could see and act on, and no roll ever decides an accident.
/// </summary>
public static class NCReactorMath
{
    public const int MaximumLevel = 3;

    public static NCReactorState Start(NCPharmaReactionProfilePrototype profile) => new()
    {
        Temperature = profile.AmbientTemperature,
        Pressure = profile.PressureBase,
    };

    /// <summary>Advances the run by one step. Warnings and accidents are decided from the result.</summary>
    public static void Step(
        NCPharmaReactionProfilePrototype profile,
        NCReactorState state,
        NCReactorControls controls,
        float dt)
    {
        if (!float.IsFinite(dt) || dt <= 0f)
            return;

        var heater = Math.Clamp(controls.Heater, 0, MaximumLevel);
        var cooler = Math.Clamp(controls.Cooler, 0, MaximumLevel);

        state.Mixing += ((controls.Stirring ? 1f : 0f) - state.Mixing) * MathF.Min(1f, dt * 0.3f);
        state.Mixing = Math.Clamp(state.Mixing, 0f, 1f);

        var temperature = state.Temperature;
        var inOptimal = temperature >= profile.OptimalMin && temperature <= profile.OptimalMax;
        var inSafe = temperature >= profile.SafeMin && temperature <= profile.SafeMax;
        var efficiency = state.Quenched ? 0f : inOptimal ? 1f : inSafe ? 0.35f : 0f;
        var rate = efficiency * (1f + state.Catalyst * profile.CatalystRateGain) * (0.4f + 0.6f * state.Mixing);
        if (controls.Venting)
            rate *= 0.8f; // venting lets volatile product escape
        state.Progress = MathF.Min(1f, state.Progress + profile.ProgressPerSecond * rate * dt);

        // A reaction keeps releasing heat while it is hot enough to run, and runs away once it passes its window.
        var runaway = temperature > profile.OptimalMax
            ? 1f + (temperature - profile.OptimalMax) / MathF.Max(1f, profile.SafeMax - profile.OptimalMax) * 2f
            : 1f;
        var reacting = !state.Quenched && temperature >= profile.SafeMin;
        var reactionHeat = reacting
            ? profile.Exothermicity * MathF.Max(rate, 0.3f) * runaway * (1f - state.Progress) *
              (1f + state.Catalyst * profile.CatalystHeatGain)
            : 0f;

        var delta = heater * profile.HeaterPower
                    - cooler * profile.CoolerPower
                    - (temperature - profile.AmbientTemperature) * profile.HeatLoss
                    + reactionHeat;
        state.TemperatureRate = delta;
        state.Temperature = Math.Clamp(temperature + delta * dt, profile.AmbientTemperature - 30f, 500f);

        var excess = MathF.Max(0f, state.Temperature - profile.AmbientTemperature);
        var pressureTarget = profile.PressureBase + excess * profile.PressurePerDegree * profile.Volatility;
        if (controls.Venting)
            pressureTarget = profile.PressureBase + (pressureTarget - profile.PressureBase) * 0.35f;
        state.Pressure += (pressureTarget - state.Pressure) * MathF.Min(1f, dt * 0.5f);

        var gain = 0f;
        if (state.Temperature > profile.SafeMax)
            gain += (state.Temperature - profile.SafeMax) * 0.004f;
        if (state.Pressure > profile.PressureSoftLimit)
        {
            gain += (state.Pressure - profile.PressureSoftLimit) /
                    MathF.Max(1f, profile.PressureHardLimit - profile.PressureSoftLimit) * 0.05f;
        }

        if (profile.Exothermicity > 0f && state.Mixing < 0.3f && reacting)
            gain += 0.01f;

        state.Instability += gain > 0f ? gain * dt : -0.01f * dt;
        state.Instability = Math.Clamp(state.Instability, 0f, 1f);
        state.PeakInstability = MathF.Max(state.PeakInstability, state.Instability);

        state.Elapsed += dt;
        state.MixingIntegral += state.Mixing * dt;
        if (inOptimal)
            state.SecondsOptimal += dt;
        else if (inSafe)
            state.SecondsSafe += dt;
        else
            state.SecondsOutside += dt;
    }

    public static NCReactorWarnings ResolveWarnings(NCPharmaReactionProfilePrototype profile, NCReactorState state)
    {
        var warnings = NCReactorWarnings.None;
        if (state.Temperature > profile.OptimalMax)
            warnings |= NCReactorWarnings.HighTemperature;
        if (state.Temperature > profile.SafeMax - 5f)
            warnings |= NCReactorWarnings.NearSafeLimit;
        if (state.Pressure > profile.PressureSoftLimit * 0.85f)
            warnings |= NCReactorWarnings.HighPressure;
        if (profile.Exothermicity > 0f && state.Mixing < 0.3f)
            warnings |= NCReactorWarnings.PoorMixing;
        if (state.TemperatureRate > 2f && state.Temperature > profile.OptimalMax)
            warnings |= NCReactorWarnings.Runaway;
        if (state.Instability > 0.2f)
            warnings |= NCReactorWarnings.InstabilityRising;
        return warnings;
    }

    /// <summary>
    /// Turns the record of a run into the quality factors of its product. Time spent outside the window and the
    /// worst instability cost purity, poor mixing costs homogeneity, and a degraded or quenched run costs yield.
    /// </summary>
    public static NCReactorOutcome Evaluate(NCReactorState state)
    {
        var elapsed = MathF.Max(1f, state.Elapsed);
        var outside = state.SecondsOutside / elapsed;
        var safe = state.SecondsSafe / elapsed;
        var purity = Math.Clamp(1f - 0.5f * outside - 0.15f * safe - 0.5f * state.PeakInstability, 0.2f, 1f);
        var homogeneity = Math.Clamp(state.MixingIntegral / elapsed, 0.2f, 1f);
        var yield = 1f;
        if (state.Degraded)
            yield *= 0.5f;
        if (state.Quenched)
            yield *= state.Progress * 0.5f;
        return new NCReactorOutcome(purity, homogeneity, Math.Clamp(yield, 0f, 1f));
    }
}
