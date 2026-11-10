// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.Medical.Pharmacology.Batch;

/// <summary>
/// Pure, deterministic batch rules. Nothing here touches entities or randomness sources: the same inputs
/// always give the same batch and the same dose, which keeps every outcome explainable and replayable.
/// </summary>
public static class NCPharmaBatchMath
{
    public const int MaximumModifiers = 2;

    /// <summary>Nothing is perfectly pure or sterile; the last fraction of a percent is not reachable.</summary>
    public const float QualityCeiling = 0.999f;

    /// <summary>No dose may linger in the absorption depot longer than this.</summary>
    public const double MaximumAbsorptionSeconds = 300d;

    public static bool ValidateFormula(
        NCPharmaFormulaPrototype formula,
        IReadOnlyList<NCPharmaModifierPrototype> modifiers)
    {
        return formula.Version > 0 &&
               IsFinitePositive(formula.TargetConcentration) &&
               IsFiniteRange(formula.BaseStability, 0f, 1f) &&
               formula.Modifiers.Count <= MaximumModifiers &&
               modifiers.Count == formula.Modifiers.Count;
    }

    /// <summary>
    /// Turns a formula, a production mode and the quality of inputs and equipment into a physical batch.
    /// Quality is a product of its inputs, so a better result must be paid for with yield, time or money.
    /// </summary>
    public static NCPharmaProductionOutcome Produce(
        NCPharmaFormulaPrototype formula,
        IReadOnlyList<NCPharmaModifierPrototype> modifiers,
        NCPharmaProductionProfilePrototype profile,
        NCPharmaProductionInputs inputs)
    {
        var concentrationFactor = 1f;
        var purityDelta = 0f;
        var stabilityDelta = 0f;
        var toxicity = 1f;
        var cost = profile.CostMultiplier;
        foreach (var modifier in modifiers)
        {
            concentrationFactor *= modifier.ConcentrationMultiplier;
            purityDelta += modifier.PurityDelta;
            stabilityDelta += modifier.StabilityDelta;
            toxicity *= modifier.ToxicityMultiplier;
            cost *= modifier.CostMultiplier;
        }

        var calibration = Math.Clamp(inputs.CalibrationError, -0.5f, 0.5f);
        var concentration = MathF.Max(
            0f,
            formula.TargetConcentration * concentrationFactor * (1f + profile.ConcentrationBias + calibration));
        var quality = new NCPharmaQuality(
            concentration,
            ClampQuality(inputs.InputPurity * profile.PurityMultiplier + purityDelta),
            ClampQuality(inputs.EquipmentSterility * profile.SterilityMultiplier),
            ClampQuality(inputs.EquipmentMixing * profile.HomogeneityMultiplier),
            ClampQuality((formula.BaseStability + stabilityDelta) * profile.StabilityMultiplier));
        var doses = (int) MathF.Floor(MathF.Max(0f, inputs.InputUnits) * MathF.Max(0f, profile.YieldMultiplier));
        return new NCPharmaProductionOutcome(quality, doses, toxicity, cost, profile.Duration);
    }

    /// <summary>
    /// Resolves what one dose of a batch actually does. The dose index makes every dose of the same batch
    /// slightly different when mixing was poor, without any hidden randomness.
    /// </summary>
    public static NCPharmaDoseEffect ResolveDose(
        NCPharmaTuningPrototype tuning,
        NCPharmaDeliveryFormPrototype form,
        NCPharmaQuality actual,
        float modifierToxicity,
        Guid batchId,
        int doseIndex)
    {
        var spread = (1f - actual.Homogeneity) * tuning.MaximumDoseSpread;
        var deviation = 1f + spread * DoseNoise(batchId, doseIndex);
        var effective = MathF.Max(0f, actual.Concentration * form.ConcentrationMultiplier * deviation);
        var impurity = 1f - actual.Purity;
        var toxin = impurity * effective * form.AdulterantMultiplier * modifierToxicity *
                    tuning.AdulterantToxinPerConcentration;
        var sepsis = (1f - actual.Sterility) * form.SterilityRiskMultiplier * tuning.SepsisBurdenAtZeroSterility;
        return new NCPharmaDoseEffect(effective, toxin, sepsis);
    }

    /// <summary>
    /// Time a dose needs to be fully absorbed: the delivery form's own absorption scaled by every modifier.
    /// </summary>
    public static TimeSpan ResolveAbsorption(
        NCPharmaDeliveryFormPrototype form,
        IReadOnlyList<NCPharmaModifierPrototype> modifiers)
    {
        var seconds = form.AbsorptionDuration.TotalSeconds;
        foreach (var modifier in modifiers)
            seconds *= modifier.AbsorptionMultiplier;
        return double.IsFinite(seconds) && seconds > 0d
            ? TimeSpan.FromSeconds(Math.Min(seconds, MaximumAbsorptionSeconds))
            : TimeSpan.Zero;
    }

    /// <summary>
    /// Predicts a formula from the same numbers production uses, assuming ideal inputs. The toxic margin is how
    /// far the peak concentration stays below the compound's toxic level: below one is already unsafe.
    /// </summary>
    public static NCPharmaForecast Forecast(
        NCPharmaFormulaPrototype formula,
        IReadOnlyList<NCPharmaModifierPrototype> modifiers,
        NCPharmaDeliveryFormPrototype form,
        NCPharmaProductionProfilePrototype profile,
        float compoundToxicConcentration)
    {
        var concentration = formula.TargetConcentration;
        var stability = formula.BaseStability;
        var toxicity = 1f;
        var cost = profile.CostMultiplier;
        foreach (var modifier in modifiers)
        {
            concentration *= modifier.ConcentrationMultiplier;
            stability += modifier.StabilityDelta;
            toxicity *= modifier.ToxicityMultiplier;
            cost *= modifier.CostMultiplier;
        }

        var peak = MathF.Max(0f, concentration * form.ConcentrationMultiplier);
        var margin = peak > 0f && compoundToxicConcentration > 0f ? compoundToxicConcentration / peak : float.PositiveInfinity;
        return new NCPharmaForecast(
            peak,
            margin,
            ClampQuality(stability * profile.StabilityMultiplier),
            toxicity,
            ResolveAbsorption(form, modifiers),
            cost);
    }

    /// <summary>Market class derived from measured parameters; concentration never counts as quality.</summary>
    public static NCPharmaGrade ResolveGrade(NCPharmaTuningPrototype tuning, NCPharmaQuality quality)
    {
        if (quality.Purity >= tuning.CorporateMinimumPurity &&
            quality.Sterility >= tuning.CorporateMinimumSterility &&
            quality.Homogeneity >= tuning.CorporateMinimumHomogeneity)
        {
            return NCPharmaGrade.Corporate;
        }

        return quality.Purity >= tuning.MedicalMinimumPurity &&
               quality.Sterility >= tuning.MedicalMinimumSterility &&
               quality.Homogeneity >= tuning.MedicalMinimumHomogeneity
            ? NCPharmaGrade.Medical
            : NCPharmaGrade.Street;
    }

    /// <summary>
    /// Compares the measured value of each requested test against the label. A test that was not requested
    /// reveals nothing and cannot fail, so partial work gives partial knowledge.
    /// </summary>
    public static NCPharmaQcReport RunQc(
        NCPharmaTuningPrototype tuning,
        Guid batchId,
        NCPharmaQuality actual,
        NCPharmaLabel label,
        NCPharmaQcTests requested)
    {
        var failed = NCPharmaQcTests.None;
        float? concentration = null;
        float? purity = null;
        float? sterility = null;
        float? homogeneity = null;
        float? stability = null;

        if (requested.HasFlag(NCPharmaQcTests.Concentration))
        {
            concentration = actual.Concentration;
            var allowed = label.Concentration * tuning.QcConcentrationTolerance;
            if (MathF.Abs(actual.Concentration - label.Concentration) > allowed)
                failed |= NCPharmaQcTests.Concentration;
        }

        if (requested.HasFlag(NCPharmaQcTests.Purity))
        {
            purity = actual.Purity;
            if (actual.Purity < label.Purity - tuning.QcPurityTolerance)
                failed |= NCPharmaQcTests.Purity;
        }

        if (requested.HasFlag(NCPharmaQcTests.Sterility))
        {
            sterility = actual.Sterility;
            if (label.Sterile && actual.Sterility < tuning.QcMinimumSterility)
                failed |= NCPharmaQcTests.Sterility;
        }

        if (requested.HasFlag(NCPharmaQcTests.Homogeneity))
        {
            homogeneity = actual.Homogeneity;
            if (actual.Homogeneity < tuning.QcMinimumHomogeneity)
                failed |= NCPharmaQcTests.Homogeneity;
        }

        if (requested.HasFlag(NCPharmaQcTests.Stability))
        {
            stability = actual.Stability;
            if (actual.Stability < tuning.QcMinimumStability)
                failed |= NCPharmaQcTests.Stability;
        }

        return new NCPharmaQcReport(
            batchId,
            requested & NCPharmaQcTests.All,
            failed,
            concentration,
            purity,
            sterility,
            homogeneity,
            stability);
    }

    /// <summary>Any failed test rejects the batch; all five passed certify it; everything else is partial.</summary>
    public static NCPharmaQcState ResolveQcState(NCPharmaQcState previous, NCPharmaQcReport report)
    {
        if (previous == NCPharmaQcState.Recalled)
            return NCPharmaQcState.Recalled;
        if (report.Failed != NCPharmaQcTests.None)
            return NCPharmaQcState.Rejected;
        if (report.Performed == NCPharmaQcTests.All)
            return NCPharmaQcState.Certified;
        return report.Performed == NCPharmaQcTests.None ? previous : NCPharmaQcState.PartialQc;
    }

    /// <summary>
    /// Stable value in [-1, 1] for one dose of one batch. FNV-1a over the batch id and dose index, so it does
    /// not depend on process-randomized hash codes and is identical on every server run.
    /// </summary>
    public static float DoseNoise(Guid batchId, int doseIndex)
    {
        const uint offset = 2166136261u;
        const uint prime = 16777619u;
        var hash = offset;
        Span<byte> bytes = stackalloc byte[16];
        batchId.TryWriteBytes(bytes);
        foreach (var value in bytes)
        {
            hash ^= value;
            hash *= prime;
        }

        unchecked
        {
            var index = (uint) doseIndex;
            for (var shift = 0; shift < 32; shift += 8)
            {
                hash ^= (index >> shift) & 0xFFu;
                hash *= prime;
            }
        }

        return (hash % 20001u) / 10000f - 1f;
    }

    private static float ClampQuality(float value) =>
        float.IsFinite(value) ? Math.Clamp(value, 0f, QualityCeiling) : 0f;

    private static bool IsFinitePositive(float value) => float.IsFinite(value) && value > 0f;

    private static bool IsFiniteRange(float value, float minimum, float maximum) =>
        float.IsFinite(value) && value >= minimum && value <= maximum;
}
