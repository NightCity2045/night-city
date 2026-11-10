// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Pharmacology.Lab;

/// <summary>How deep a laboratory method looks. A deeper method confirms everything a shallower one does.</summary>
[Serializable, NetSerializable, Flags]
public enum NCAnalysisTier : byte
{
    None = 0,

    /// <summary>What is inside: composition and how the body clears it.</summary>
    Chemical = 1,

    /// <summary>What it does to living tissue.</summary>
    InVitro = 2,

    /// <summary>What it really does to a living organism.</summary>
    InVivo = 4,
}

public enum NCCompoundPropertyKind : byte
{
    MetabolicRoute,
    PrimaryAction,
    SecondaryAction,
    Duration,
    TherapeuticWindow,
    DependenceRisk,
    Interactions,
}

/// <summary>
/// One stable fact about a compound, derived from the real pharmacological profile so a card can never
/// disagree with what the compound does. <see cref="Value"/> is a localization selector, never shown raw.
/// </summary>
public readonly record struct NCCompoundProperty(
    NCCompoundPropertyKind Kind,
    string Value,
    NCAnalysisTier ConfirmedAt,
    NCAnalysisTier SuspectedAt);

public enum NCCompoundPropertyState : byte
{
    Confirmed,
    Suspected,
    Unknown,
}

public readonly record struct NCCompoundPropertyReading(
    NCCompoundPropertyKind Kind,
    string Value,
    NCCompoundPropertyState State);

/// <summary>Builds and reads compound dossiers. Pure: it never touches entities.</summary>
public static class NCCompoundDossierMath
{
    private const float MinimumSecondaryAction = 0.05f;

    /// <summary>Builds the 3–7 stable properties of a compound from its profile.</summary>
    public static List<NCCompoundProperty> Build(NCDrugProfilePrototype drug, NCDependenceProfilePrototype? dependence)
    {
        var result = new List<NCCompoundProperty>(7)
        {
            new(
                NCCompoundPropertyKind.MetabolicRoute,
                ResolveRoute(drug),
                NCAnalysisTier.Chemical,
                NCAnalysisTier.None),
        };

        var axes = ResolveActions(drug);
        if (axes.Count > 0)
        {
            result.Add(new NCCompoundProperty(
                NCCompoundPropertyKind.PrimaryAction,
                axes[0].Axis,
                NCAnalysisTier.InVitro,
                NCAnalysisTier.None));
        }

        if (axes.Count > 1 && axes[1].Magnitude >= MinimumSecondaryAction)
        {
            result.Add(new NCCompoundProperty(
                NCCompoundPropertyKind.SecondaryAction,
                axes[1].Axis,
                NCAnalysisTier.InVivo,
                NCAnalysisTier.InVitro));
        }

        result.Add(new NCCompoundProperty(
            NCCompoundPropertyKind.Duration,
            ResolveDuration(drug),
            NCAnalysisTier.InVivo,
            NCAnalysisTier.None));
        result.Add(new NCCompoundProperty(
            NCCompoundPropertyKind.TherapeuticWindow,
            ResolveWindow(drug),
            NCAnalysisTier.InVivo,
            NCAnalysisTier.InVitro));
        result.Add(new NCCompoundProperty(
            NCCompoundPropertyKind.DependenceRisk,
            ResolveDependence(drug, dependence),
            NCAnalysisTier.InVivo,
            NCAnalysisTier.None));
        result.Add(new NCCompoundProperty(
            NCCompoundPropertyKind.Interactions,
            drug.InteractionsCharted ? "charted" : "uncharted",
            NCAnalysisTier.InVivo,
            NCAnalysisTier.None));
        return result;
    }

    /// <summary>
    /// Reads a dossier at the given knowledge. A property is confirmed once a method at or above its tier has
    /// run, suspected when only the method named as its hint has run, and unknown otherwise.
    /// </summary>
    public static List<NCCompoundPropertyReading> Read(
        IReadOnlyList<NCCompoundProperty> properties,
        NCAnalysisTier reached)
    {
        var result = new List<NCCompoundPropertyReading>(properties.Count);
        foreach (var property in properties)
        {
            var state = NCCompoundPropertyState.Unknown;
            if (Reaches(reached, property.ConfirmedAt))
                state = NCCompoundPropertyState.Confirmed;
            else if (property.SuspectedAt != NCAnalysisTier.None && Reaches(reached, property.SuspectedAt))
                state = NCCompoundPropertyState.Suspected;

            result.Add(new NCCompoundPropertyReading(property.Kind, property.Value, state));
        }

        return result;
    }

    /// <summary>A method at a deeper level carries everything shallower methods reveal.</summary>
    public static NCAnalysisTier Include(NCAnalysisTier reached, NCAnalysisTier performed)
    {
        return performed switch
        {
            NCAnalysisTier.InVivo => NCAnalysisTier.Chemical | NCAnalysisTier.InVitro | NCAnalysisTier.InVivo,
            NCAnalysisTier.InVitro => reached | NCAnalysisTier.Chemical | NCAnalysisTier.InVitro,
            NCAnalysisTier.Chemical => reached | NCAnalysisTier.Chemical,
            _ => reached,
        };
    }

    private static bool Reaches(NCAnalysisTier reached, NCAnalysisTier required)
    {
        return required != NCAnalysisTier.None && (reached & required) != 0;
    }

    private static string ResolveRoute(NCDrugProfilePrototype drug)
    {
        var liver = drug.LiverClearanceFraction;
        var kidney = drug.KidneyClearanceFraction;
        var baseline = drug.BaselineClearanceFraction;
        if (liver >= kidney && liver >= baseline)
            return "liver";
        return kidney >= baseline ? "kidney" : "baseline";
    }

    private static string ResolveDuration(NCDrugProfilePrototype drug)
    {
        if (drug.ClearancePerSecond <= 0f)
            return "prolonged";

        // Half-life of the exposure at the profile's first-order clearance.
        var halfLife = 0.693f / drug.ClearancePerSecond;
        return halfLife < 30f ? "brief" : halfLife < 90f ? "moderate" : "prolonged";
    }

    private static string ResolveWindow(NCDrugProfilePrototype drug)
    {
        if (drug.TherapeuticConcentration <= 0f)
            return "narrow";

        var ratio = drug.ToxicConcentration / drug.TherapeuticConcentration;
        return ratio < 1.8f ? "narrow" : ratio < 3f ? "moderate" : "wide";
    }

    private static string ResolveDependence(NCDrugProfilePrototype drug, NCDependenceProfilePrototype? dependence)
    {
        if (dependence == null)
            return "none";

        var strength = dependence.DependenceGainPerConcentration * drug.TherapeuticConcentration;
        return strength < 0.01f ? "low" : strength < 0.03f ? "moderate" : "high";
    }

    private static List<(string Axis, float Magnitude)> ResolveActions(NCDrugProfilePrototype drug)
    {
        var axes = new List<(string Axis, float Magnitude)>
        {
            ("analgesia", drug.AnalgesiaAtTherapeutic),
            ("sedation", drug.SedationAtTherapeutic),
            ("anesthesia", drug.AnesthesiaAtTherapeutic),
            ("arousal", drug.ArousalAtTherapeutic),
            ("pressure-up", drug.CardiacOutputMultiplierAtTherapeutic - 1f),
            ("pressure-down", 1f - drug.CardiacOutputMultiplierAtTherapeutic),
            ("respiratory-depression", 1f - drug.RespiratoryTransferMultiplierAtTherapeutic),
            ("antibiotic", drug.AntibioticPowerAtTherapeutic),
            ("immunosuppression", drug.ImmunosuppressionAtTherapeutic),
            ("toxin-clearance", drug.ToxinRecoveryPerSecondAtTherapeutic),
            ("rhythm-instability", drug.RhythmInstabilityAtTherapeutic),
            ("motor-impairment", 1f - drug.MotorControlMultiplierAtTherapeutic),
        };

        axes.RemoveAll(entry => !float.IsFinite(entry.Magnitude) || entry.Magnitude <= 0f);
        // Stable order: larger first, ties broken by the axis name so the same profile always reads the same.
        axes.Sort((a, b) =>
        {
            var byMagnitude = b.Magnitude.CompareTo(a.Magnitude);
            return byMagnitude != 0 ? byMagnitude : string.CompareOrdinal(a.Axis, b.Axis);
        });
        return axes;
    }
}
