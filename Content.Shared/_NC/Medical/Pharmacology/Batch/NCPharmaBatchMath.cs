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

    /// <summary>Extra toxicity of a batch whose raw material was entirely contaminated.</summary>
    public const float ContaminantToxicityAtFull = 2f;

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

        toxicity *= 1f + Math.Clamp(inputs.Contaminants, 0f, 1f) * ContaminantToxicityAtFull;
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
    /// How strongly a dose lands in a body of the given mass: the same amount of drug is more concentrated in a
    /// lighter body. An unknown or non-positive mass is treated as the reference body.
    /// </summary>
    public static float ResolveMassScale(NCPharmaTuningPrototype tuning, float bodyMass)
    {
        if (!float.IsFinite(bodyMass) || bodyMass <= 0f || tuning.ReferenceBodyMass <= 0f)
            return 1f;

        return Math.Clamp(tuning.ReferenceBodyMass / bodyMass, tuning.MinimumMassScale, tuning.MaximumMassScale);
    }

    /// <summary>Share of a dose applied to the wounds directly, from the delivery form and the modifiers.</summary>
    public static float ResolveLocalFraction(
        NCPharmaTuningPrototype tuning,
        NCPharmaDeliveryFormPrototype form,
        IReadOnlyList<NCPharmaModifierPrototype> modifiers)
    {
        var fraction = form.LocalTissueFraction;
        foreach (var modifier in modifiers)
            fraction += modifier.LocalTissueFraction;
        return Math.Clamp(fraction, 0f, Math.Clamp(tuning.MaximumLocalFraction, 0f, 1f));
    }

    /// <summary>
    /// Infection a locally applied dose removes from one wound. A poor blood supply carries less of the drug
    /// to the tissue, and a strain that resists the antibiotic takes less harm from it.
    /// </summary>
    public static float ResolveLocalInfectionRelief(
        NCPharmaTuningPrototype tuning,
        float antibioticPower,
        float susceptibility,
        float localConcentration,
        float therapeuticConcentration,
        float perfusion)
    {
        if (therapeuticConcentration <= 0f || localConcentration <= 0f || antibioticPower <= 0f)
            return 0f;

        var fraction = localConcentration / therapeuticConcentration;
        return antibioticPower * Math.Clamp(susceptibility, 0f, 1f) * Math.Clamp(perfusion, 0f, 1f) *
               fraction * tuning.LocalInfectionReliefAtTherapeutic;
    }

    /// <summary>
    /// Pain relief and bleeding reduction a locally applied dose gives at the wound. Targeting makes it more
    /// efficient than the same drug spread through the body, but it needs blood supply to reach the tissue.
    /// </summary>
    public static (float Analgesia, float Hemostasis) ResolveLocalEffects(
        NCPharmaTuningPrototype tuning,
        float analgesiaAtTherapeutic,
        float bleedingMultiplierAtTherapeutic,
        float localConcentration,
        float therapeuticConcentration,
        float perfusion)
    {
        if (therapeuticConcentration <= 0f || localConcentration <= 0f)
            return (0f, 0f);

        var fraction = Math.Clamp(localConcentration / therapeuticConcentration, 0f, 1.5f);
        var efficiency = MathF.Max(0f, tuning.LocalEfficiency) * Math.Clamp(perfusion, 0f, 1f);
        var analgesia = Math.Clamp(MathF.Max(0f, analgesiaAtTherapeutic) * fraction * efficiency, 0f, 1f);
        var hemostasis = Math.Clamp(MathF.Max(0f, 1f - bleedingMultiplierAtTherapeutic) * fraction * efficiency, 0f, 0.9f);
        return (analgesia, hemostasis);
    }

    /// <summary>True when a compound has any local action at all, so a local application is not wasted.</summary>
    public static bool HasLocalAction(NCDrugProfilePrototype drug) =>
        drug.AntibioticPowerAtTherapeutic > 0f ||
        drug.AnalgesiaAtTherapeutic > 0f ||
        drug.BleedingMultiplierAtTherapeutic < 1f;

    /// <summary>
    /// What one dose does to a standard test subject. The outcome follows from the same numbers a patient would
    /// get: the peak against the toxic concentration, the adulterant toxin and the sepsis burden.
    /// </summary>
    public static NCPharmaStudyResult ResolveStudy(
        NCPharmaTuningPrototype tuning,
        NCPharmaDoseEffect effect,
        float toxicConcentration)
    {
        var ratio = toxicConcentration > 0f ? effect.EffectiveConcentration / toxicConcentration : 0f;
        var outcome = NCPharmaStudyOutcome.Tolerated;
        if (ratio >= 1f || effect.ToxinBurden >= tuning.StudyToxinLoss || effect.SepsisBurden >= tuning.StudySepsisLoss)
            outcome = NCPharmaStudyOutcome.Lost;
        else if (ratio >= tuning.StudyStrainedRatio || effect.ToxinBurden >= tuning.StudyToxinStrained ||
                 effect.SepsisBurden >= tuning.StudySepsisStrained)
            outcome = NCPharmaStudyOutcome.Strained;

        return new NCPharmaStudyResult(outcome, effect.EffectiveConcentration, ratio, effect.ToxinBurden, effect.SepsisBurden);
    }

    /// <summary>Combined elimination speed of the modifiers; never stops the body clearing the compound.</summary>
    public static float ResolveClearanceFactor(IReadOnlyList<NCPharmaModifierPrototype> modifiers)
    {
        var factor = 1f;
        foreach (var modifier in modifiers)
            factor *= Math.Clamp(modifier.ClearanceMultiplier, 0.1f, 1f);
        return Math.Clamp(factor, 0.2f, 1f);
    }

    /// <summary>The strongest masking among the modifiers; masking agents do not stack into invisibility.</summary>
    public static float ResolveMasking(IReadOnlyList<NCPharmaModifierPrototype> modifiers)
    {
        var strongest = 0f;
        foreach (var modifier in modifiers)
            strongest = MathF.Max(strongest, modifier.MaskingStrength);
        return Math.Clamp(strongest, 0f, 1f);
    }

    /// <summary>
    /// One purification pass over raw material: it closes part of the gap to the ceiling, so the last percents
    /// cost the most, and it always costs material. Returns the new purity and the units that remain.
    /// </summary>
    public static (float Purity, float Units) Purify(NCPharmaTuningPrototype tuning, float purity, float units)
    {
        if (!float.IsFinite(purity) || !float.IsFinite(units) || units <= 0f)
            return (purity, units);

        var ceiling = Math.Clamp(tuning.PurificationCeiling, 0f, QualityCeiling);
        var gap = MathF.Max(0f, ceiling - purity);
        var newPurity = purity + gap * Math.Clamp(tuning.PurificationGapClosed, 0f, 1f);
        var newUnits = units * (1f - Math.Clamp(tuning.PurificationYieldLoss, 0f, 1f));
        return (newPurity, newUnits);
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
    /// Purity of an unstable batch after it spent the given time outside cold storage. Only a batch whose
    /// stability is below the threshold degrades, and the lower the stability, the faster. Purity never rises.
    /// </summary>
    public static float ResolveDegradedPurity(
        NCPharmaTuningPrototype tuning,
        float stability,
        float purity,
        float seconds)
    {
        if (!float.IsFinite(seconds) || seconds <= 0f || !float.IsFinite(purity))
            return purity;

        var deficit = MathF.Max(0f, tuning.StableThreshold - stability);
        if (deficit <= 0f)
            return purity;

        var loss = deficit * tuning.DegradationPerHour * (seconds / 3600f);
        var floor = MathF.Min(purity, tuning.MinimumDegradedPurity);
        return MathF.Max(floor, purity - loss);
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
        NCPharmaQcTests requested,
        float masking = 0f,
        float quality = 1f)
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
            // A masking agent hides part of the impurity from this simple analysis.
            var measured = actual.Purity + (1f - actual.Purity) * Math.Clamp(masking, 0f, 1f);
            purity = measured;
            if (measured < label.Purity - tuning.QcPurityTolerance)
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

        // A sloppy run reads the numbers less precisely. Pass and fail were decided on the true values above.
        var blur = Math.Clamp(1f - quality, 0f, 0.5f) * 0.3f;
        return new NCPharmaQcReport(
            batchId,
            requested & NCPharmaQcTests.All,
            failed,
            Blur(concentration, batchId, 1001, blur),
            Blur(purity, batchId, 1002, blur),
            Blur(sterility, batchId, 1003, blur),
            Blur(homogeneity, batchId, 1004, blur),
            Blur(stability, batchId, 1005, blur));
    }

    private static float? Blur(float? value, Guid batchId, int stream, float blur)
    {
        if (value is not { } measured || blur <= 0f)
            return value;

        return MathF.Max(0f, measured * (1f + blur * DoseNoise(batchId, stream)));
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
        // An ordinary array on purpose: Content.Shared is passed through ILVerify, which cannot verify stackalloc.
        // It holds the same 16 bytes TryWriteBytes would write, so the noise of every batch is unchanged.
        var bytes = batchId.ToByteArray();
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
