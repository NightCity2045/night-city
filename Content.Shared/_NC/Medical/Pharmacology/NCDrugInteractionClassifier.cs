// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.Medical.Pharmacology;

/// <summary>
/// Clinician-facing reading of a drug pair. This is a summary of the deterministic interaction rules, never an
/// input to them; the physiology keeps using the raw multipliers.
/// </summary>
public enum NCDrugInteractionClass : byte
{
    Compatible,
    Caution,
    Dangerous,

    /// <summary>At least one compound has no charted interaction data, so nothing can be promised either way.</summary>
    Unknown,
}

public static class NCDrugInteractionClassifier
{
    /// <summary>Toxicity multiplier at or above which a pair is dangerous.</summary>
    public const float DangerousToxicity = 1.5f;

    /// <summary>Therapeutic multiplier at or below which a pair is dangerous (the drug stops working).</summary>
    public const float DangerousTherapeutic = 0.4f;

    /// <summary>Relative deviation from 1 that makes a pair worth a caution.</summary>
    public const float CautionDeviation = 0.15f;

    /// <summary>Classifies one directed rule. A null rule means the compound declares nothing about the partner.</summary>
    public static NCDrugInteractionClass ClassifyRule(NCDrugInteractionRule? rule)
    {
        if (rule == null)
            return NCDrugInteractionClass.Compatible;

        return ClassifyMultipliers(rule.TherapeuticMultiplier, rule.ToxicityMultiplier);
    }

    public static NCDrugInteractionClass ClassifyMultipliers(float therapeutic, float toxicity)
    {
        if (!float.IsFinite(therapeutic) || !float.IsFinite(toxicity))
            return NCDrugInteractionClass.Unknown;

        if (toxicity >= DangerousToxicity || therapeutic <= DangerousTherapeutic)
            return NCDrugInteractionClass.Dangerous;

        if (MathF.Abs(toxicity - 1f) >= CautionDeviation || MathF.Abs(therapeutic - 1f) >= CautionDeviation)
            return NCDrugInteractionClass.Caution;

        return NCDrugInteractionClass.Compatible;
    }

    /// <summary>
    /// Classifies a pair from both directed rules. The worse reading wins; Unknown applies only when neither side
    /// declares a rule and one side's interaction data is not charted.
    /// </summary>
    public static NCDrugInteractionClass ClassifyPair(
        NCDrugInteractionRule? aToB,
        NCDrugInteractionRule? bToA,
        bool aCharted,
        bool bCharted)
    {
        if (aToB == null && bToA == null)
            return aCharted && bCharted ? NCDrugInteractionClass.Compatible : NCDrugInteractionClass.Unknown;

        var first = ClassifyRule(aToB);
        var second = ClassifyRule(bToA);
        return Worst(first, second);
    }

    public static NCDrugInteractionClass Worst(NCDrugInteractionClass a, NCDrugInteractionClass b)
    {
        // Dangerous > Caution > Unknown > Compatible: an unknown pair is not reassuring, but a known danger outranks it.
        return Rank(a) >= Rank(b) ? a : b;
    }

    private static int Rank(NCDrugInteractionClass value) => value switch
    {
        NCDrugInteractionClass.Dangerous => 3,
        NCDrugInteractionClass.Caution => 2,
        NCDrugInteractionClass.Unknown => 1,
        _ => 0,
    };
}
