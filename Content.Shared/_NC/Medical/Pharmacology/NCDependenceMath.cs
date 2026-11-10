// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Pharmacology;

/// <summary>Readable withdrawal stage. It is the only dependence value a diagnostic ever needs to show.</summary>
[Serializable, NetSerializable]
public enum NCWithdrawalStage : byte
{
    None,
    Craving,
    Mild,
    Moderate,
    Severe,
}

/// <summary>Persistent dependence state of one body towards one compound.</summary>
[DataDefinition]
public sealed partial class NCDrugDependenceState
{
    [DataField]
    public float Tolerance;

    [DataField]
    public float Dependence;

    [DataField]
    public float Craving;

    /// <summary>Server time of the last dose of this compound.</summary>
    [DataField]
    public TimeSpan LastDose;

    [DataField]
    public NCWithdrawalStage Stage;
}

/// <summary>Result of resolving one body's withdrawal for one compound at one moment.</summary>
public readonly record struct NCWithdrawalOutcome(NCWithdrawalStage Stage, float SymptomConcentration);

/// <summary>
/// Pure dependence rules. Tolerance lowers the wanted effect but never the toxicity; dependence builds slowly
/// from repeated dosing; withdrawal grows over time after the last dose and is cancelled by a replacement.
/// </summary>
public static class NCDependenceMath
{
    /// <summary>Share of the therapeutic effect that is still delivered at the given tolerance.</summary>
    public static float EffectFactor(NCDependenceProfilePrototype profile, NCDrugDependenceState state) =>
        1f - Math.Clamp(state.Tolerance, 0f, Math.Clamp(profile.MaximumTolerance, 0f, 0.95f));

    /// <summary>Registers one dose. Repeated and large doses move tolerance and dependence further.</summary>
    public static void RegisterDose(
        NCDependenceProfilePrototype profile,
        NCDrugDependenceState state,
        float concentration,
        TimeSpan now)
    {
        if (!float.IsFinite(concentration) || concentration <= 0f)
            return;

        state.Tolerance = Math.Clamp(
            state.Tolerance + concentration * profile.ToleranceGainPerConcentration,
            0f,
            Math.Clamp(profile.MaximumTolerance, 0f, 0.95f));
        state.Dependence = Math.Clamp(state.Dependence + concentration * profile.DependenceGainPerConcentration, 0f, 1f);
        state.Craving = 0f;
        state.LastDose = now;
        state.Stage = NCWithdrawalStage.None;
    }

    /// <summary>
    /// Lets tolerance and dependence fade. A replacement compound speeds the dependence decay up: that is a
    /// controlled taper, and the only way to leave a strong dependence without suffering its full withdrawal.
    /// </summary>
    public static void Decay(
        NCDependenceProfilePrototype profile,
        NCDrugDependenceState state,
        float seconds,
        bool replacementActive)
    {
        if (!float.IsFinite(seconds) || seconds <= 0f)
            return;

        state.Tolerance = MathF.Max(0f, state.Tolerance - profile.ToleranceDecayPerSecond * seconds);
        var dependenceDecay = profile.DependenceDecayPerSecond * seconds *
                              (replacementActive ? MathF.Max(1f, profile.ReplacementDecayMultiplier) : 1f);
        state.Dependence = MathF.Max(0f, state.Dependence - dependenceDecay);
    }

    /// <summary>
    /// Resolves withdrawal. Nothing happens below the threshold, while the compound is still active, while a
    /// replacement is active, or before the onset delay after the last dose. After that the stage grows over
    /// the ramp and the symptom concentration scales with how deep the dependence is.
    /// </summary>
    public static NCWithdrawalOutcome ResolveWithdrawal(
        NCDependenceProfilePrototype profile,
        NCDrugDependenceState state,
        TimeSpan now,
        bool compoundActive,
        bool replacementActive)
    {
        if (state.Dependence < profile.WithdrawalThreshold || compoundActive || replacementActive)
            return new NCWithdrawalOutcome(NCWithdrawalStage.None, 0f);

        var sinceDose = now - state.LastDose;
        if (sinceDose < profile.WithdrawalOnset)
            return new NCWithdrawalOutcome(NCWithdrawalStage.None, 0f);

        var ramp = MathF.Max(1f, (float) profile.WithdrawalRamp.TotalSeconds);
        var progress = Math.Clamp((float) (sinceDose - profile.WithdrawalOnset).TotalSeconds / ramp, 0f, 1f);
        var depth = Math.Clamp(
            (state.Dependence - profile.WithdrawalThreshold) / MathF.Max(0.0001f, 1f - profile.WithdrawalThreshold),
            0f,
            1f);
        var intensity = progress * (0.35f + 0.65f * depth);
        var stage = intensity switch
        {
            < 0.1f => NCWithdrawalStage.Craving,
            < 0.4f => NCWithdrawalStage.Mild,
            < 0.75f => NCWithdrawalStage.Moderate,
            _ => NCWithdrawalStage.Severe,
        };
        return new NCWithdrawalOutcome(stage, profile.WithdrawalConcentrationAtFull * intensity);
    }

    /// <summary>Craving builds while a dependent body goes without the compound and fades after a dose.</summary>
    public static void AdvanceCraving(
        NCDependenceProfilePrototype profile,
        NCDrugDependenceState state,
        float seconds,
        bool dependent,
        bool compoundActive)
    {
        if (!float.IsFinite(seconds) || seconds <= 0f)
            return;

        state.Craving = dependent && !compoundActive
            ? Math.Clamp(state.Craving + profile.CravingGainPerSecond * seconds, 0f, 1f)
            : MathF.Max(0f, state.Craving - profile.CravingDecayPerSecond * seconds);
    }

    public static bool Validate(NCDependenceProfilePrototype profile) =>
        IsRange(profile.MaximumTolerance, 0f, 0.95f) &&
        IsNonNegative(profile.ToleranceGainPerConcentration) &&
        IsNonNegative(profile.ToleranceDecayPerSecond) &&
        IsNonNegative(profile.DependenceGainPerConcentration) &&
        IsNonNegative(profile.DependenceDecayPerSecond) &&
        IsRange(profile.WithdrawalThreshold, 0.01f, 1f) &&
        profile.WithdrawalOnset >= TimeSpan.Zero &&
        profile.WithdrawalRamp > TimeSpan.Zero &&
        IsNonNegative(profile.WithdrawalConcentrationAtFull) &&
        IsNonNegative(profile.CravingGainPerSecond) &&
        IsNonNegative(profile.CravingDecayPerSecond) &&
        float.IsFinite(profile.ReplacementDecayMultiplier) && profile.ReplacementDecayMultiplier >= 1f;

    public static bool Validate(NCDrugDependenceState state) =>
        IsRange(state.Tolerance, 0f, 1f) &&
        IsRange(state.Dependence, 0f, 1f) &&
        IsRange(state.Craving, 0f, 1f) &&
        Enum.IsDefined(state.Stage);

    private static bool IsRange(float value, float minimum, float maximum) =>
        float.IsFinite(value) && value >= minimum && value <= maximum;

    private static bool IsNonNegative(float value) => float.IsFinite(value) && value >= 0f;
}
