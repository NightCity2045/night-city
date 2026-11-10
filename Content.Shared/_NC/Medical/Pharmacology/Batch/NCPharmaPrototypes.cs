// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Systemic;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Pharmacology.Batch;

/// <summary>
/// Global tuning for the batch layer. Every threshold lives in data so balance never changes the resolver.
/// Exactly one prototype must be marked <see cref="DefaultTuning"/>.
/// </summary>
[Prototype("ncPharmaTuning")]
public sealed partial class NCPharmaTuningPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public bool DefaultTuning { get; private set; }

    /// <summary>Largest relative dose-to-dose deviation of a batch with zero homogeneity.</summary>
    [DataField]
    public float MaximumDoseSpread { get; private set; } = 0.5f;

    /// <summary>Systemic toxic burden added per unit of concentration for a completely impure dose.</summary>
    [DataField]
    public float AdulterantToxinPerConcentration { get; private set; } = 0.15f;

    /// <summary>Systemic sepsis burden added by a dose with zero sterility and a delivery risk of one.</summary>
    [DataField]
    public float SepsisBurdenAtZeroSterility { get; private set; } = 8f;

    [DataField(required: true)]
    public ProtoId<NCSystemicDamageProfilePrototype> ToxinProfile { get; private set; }

    [DataField(required: true)]
    public ProtoId<NCSystemicDamageProfilePrototype> SepsisProfile { get; private set; }

    [DataField]
    public float MedicalMinimumPurity { get; private set; } = 0.9f;

    [DataField]
    public float MedicalMinimumSterility { get; private set; } = 0.9f;

    [DataField]
    public float MedicalMinimumHomogeneity { get; private set; } = 0.85f;

    [DataField]
    public float CorporateMinimumPurity { get; private set; } = 0.97f;

    [DataField]
    public float CorporateMinimumSterility { get; private set; } = 0.97f;

    [DataField]
    public float CorporateMinimumHomogeneity { get; private set; } = 0.95f;

    /// <summary>Relative concentration error a QC run still accepts against the label.</summary>
    [DataField]
    public float QcConcentrationTolerance { get; private set; } = 0.1f;

    /// <summary>Absolute purity shortfall against the label a QC run still accepts.</summary>
    [DataField]
    public float QcPurityTolerance { get; private set; } = 0.05f;

    [DataField]
    public float QcMinimumSterility { get; private set; } = 0.9f;

    [DataField]
    public float QcMinimumHomogeneity { get; private set; } = 0.85f;

    [DataField]
    public float QcMinimumStability { get; private set; } = 0.6f;

    [DataField]
    public int MaximumAuditEntriesPerBatch { get; private set; } = 256;

    [DataField]
    public int MaximumBatches { get; private set; } = 4096;

    /// <summary>Largest run of an unapproved formula, so an experiment stays small.</summary>
    [DataField]
    public int MaximumExperimentalUnits { get; private set; } = 5;

    /// <summary>Most versions a laboratory may develop in one world.</summary>
    [DataField]
    public int MaximumDerivedFormulas { get; private set; } = 256;

    [DataField]
    public int MaximumJournalEntries { get; private set; } = 2048;

    [DataField]
    public int MaximumFormulaNameLength { get; private set; } = 48;

    /// <summary>Smallest and largest target concentration relative to the formula a version is derived from.</summary>
    [DataField]
    public float MinimumConcentrationRatio { get; private set; } = 0.25f;

    [DataField]
    public float MaximumConcentrationRatio { get; private set; } = 2f;

    /// <summary>Reaction used by a formula that does not name one.</summary>
    [DataField(required: true)]
    public ProtoId<NCPharmaReactionProfilePrototype> DefaultReaction { get; private set; }

    /// <summary>Most of a dose that may be applied locally; some always reaches the body.</summary>
    [DataField]
    public float MaximumLocalFraction { get; private set; } = 0.9f;

    /// <summary>How much more a locally applied dose does per unit than the same dose spread through the body.</summary>
    [DataField]
    public float LocalEfficiency { get; private set; } = 1.5f;

    /// <summary>How long a local effect on pain or bleeding lasts.</summary>
    [DataField]
    public TimeSpan LocalDuration { get; private set; } = TimeSpan.FromSeconds(180);

    /// <summary>Infection removed per unit of local antibiotic action at therapeutic concentration.</summary>
    [DataField]
    public float LocalInfectionReliefAtTherapeutic { get; private set; } = 0.35f;

    [DataField]
    public int MaximumNoteLength { get; private set; } = 240;

    [DataField]
    public int MaximumExperiments { get; private set; } = 512;

    [DataField]
    public int MaximumExperimentNotes { get; private set; } = 24;

    /// <summary>A test subject is strained when the peak reaches this share of the toxic concentration.</summary>
    [DataField]
    public float StudyStrainedRatio { get; private set; } = 0.6f;

    /// <summary>Adulterant toxin at which a test subject is strained or lost.</summary>
    [DataField]
    public float StudyToxinStrained { get; private set; } = 0.2f;

    [DataField]
    public float StudyToxinLoss { get; private set; } = 0.5f;

    [DataField]
    public float StudySepsisStrained { get; private set; } = 0.15f;

    [DataField]
    public float StudySepsisLoss { get; private set; } = 0.4f;

    /// <summary>Share of the remaining impurity one purification pass removes. Later passes gain less.</summary>
    [DataField]
    public float PurificationGapClosed { get; private set; } = 0.5f;

    /// <summary>Share of the material lost in one purification pass.</summary>
    [DataField]
    public float PurificationYieldLoss { get; private set; } = 0.2f;

    /// <summary>Purity that no amount of purification passes.</summary>
    [DataField]
    public float PurificationCeiling { get; private set; } = 0.985f;

    /// <summary>Body mass a dose is calibrated for. A lighter body feels it more strongly, a heavier one less.</summary>
    [DataField]
    public float ReferenceBodyMass { get; private set; } = 70f;

    /// <summary>Bounds on the mass scaling so an odd physics mass can never nullify or explode a dose.</summary>
    [DataField]
    public float MinimumMassScale { get; private set; } = 0.5f;

    [DataField]
    public float MaximumMassScale { get; private set; } = 2f;

    /// <summary>Largest run made by hand in a reactor.</summary>
    [DataField]
    public int MaximumReactorUnits { get; private set; } = 8;

    /// <summary>How many catalyst charges one run may use.</summary>
    [DataField]
    public int CatalystCharges { get; private set; } = 2;

    /// <summary>Stability at or above which a batch keeps its quality without any special storage.</summary>
    [DataField]
    public float StableThreshold { get; private set; } = 0.7f;

    /// <summary>Purity lost per hour for each unit of stability below the threshold, outside cold storage.</summary>
    [DataField]
    public float DegradationPerHour { get; private set; } = 1.5f;

    [DataField]
    public TimeSpan DegradationInterval { get; private set; } = TimeSpan.FromSeconds(60);

    /// <summary>Degradation never takes purity below this, so a spoiled batch is poor, not nothing.</summary>
    [DataField]
    public float MinimumDegradedPurity { get; private set; } = 0.2f;
}

/// <summary>
/// Physical way a dose enters the patient. It scales delivered concentration and how much a contaminated or
/// non-sterile batch matters, but never decides a medical outcome itself.
/// </summary>
[Prototype("ncPharmaDeliveryForm")]
public sealed partial class NCPharmaDeliveryFormPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name { get; private set; }

    [DataField]
    public float ConcentrationMultiplier { get; private set; } = 1f;

    /// <summary>
    /// Volume of blood-like fluid one dose infuses. Zero for forms that carry no volume. It is given as a
    /// universal product: it is matched to nobody, which is what a synthetic blood is for.
    /// </summary>
    [DataField]
    public float TransfusionVolume { get; private set; }

    /// <summary>The physical pack this form is delivered in. Missing means the station's default pack.</summary>
    [DataField]
    public EntProtoId? Pack { get; private set; }

    /// <summary>
    /// Share of a dose that is applied to the wounds directly instead of reaching the whole body. A surgical gel
    /// works almost entirely on the wound; a pill does not touch it at all.
    /// </summary>
    [DataField]
    public float LocalTissueFraction { get; private set; }

    [DataField]
    public float SterilityRiskMultiplier { get; private set; } = 1f;

    [DataField]
    public float AdulterantMultiplier { get; private set; } = 1f;

    [DataField]
    public TimeSpan AdministrationDelay { get; private set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a dose takes to be absorbed. Zero means the whole dose enters at once. A slower absorption gives
    /// a lower peak and a longer effect, because clearance works while the dose is still arriving.
    /// </summary>
    [DataField]
    public TimeSpan AbsorptionDuration { get; private set; } = TimeSpan.Zero;
}

/// <summary>
/// A formula modifier. Every modifier carries a cost: a stronger effect is paid for in stability, toxicity,
/// purity or price. A formula may carry at most <see cref="NCPharmaBatchMath.MaximumModifiers"/> of them.
/// </summary>
[Prototype("ncPharmaModifier")]
public sealed partial class NCPharmaModifierPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name { get; private set; }

    [DataField]
    public float ConcentrationMultiplier { get; private set; } = 1f;

    [DataField]
    public float PurityDelta { get; private set; }

    [DataField]
    public float StabilityDelta { get; private set; }

    [DataField]
    public float ToxicityMultiplier { get; private set; } = 1f;

    [DataField]
    public float CostMultiplier { get; private set; } = 1f;

    /// <summary>Scales the absorption time of the delivery form. Below one is fast, above one is slow.</summary>
    [DataField]
    public float AbsorptionMultiplier { get; private set; } = 1f;

    /// <summary>
    /// How much of the batch's impurity a simple quality-control run fails to see. A masked batch reads purer
    /// than it is; only a study of the compound itself gives it away.
    /// </summary>
    [DataField]
    public float MaskingStrength { get; private set; }

    /// <summary>How fast the body clears the compound. Below one slows elimination and lengthens the effect.</summary>
    [DataField]
    public float ClearanceMultiplier { get; private set; } = 1f;

    /// <summary>Adds to the share of a dose that works directly on the wounds instead of the whole body.</summary>
    [DataField]
    public float LocalTissueFraction { get; private set; }
}

/// <summary>
/// Production mode of a known formula: quick, standard or clean. The multipliers are applied to the quality of
/// the inputs and equipment, so a clean run costs yield, time and money instead of granting free quality.
/// </summary>
[Prototype("ncPharmaProductionProfile")]
public sealed partial class NCPharmaProductionProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name { get; private set; }

    [DataField]
    public float YieldMultiplier { get; private set; } = 1f;

    [DataField]
    public float PurityMultiplier { get; private set; } = 1f;

    [DataField]
    public float SterilityMultiplier { get; private set; } = 1f;

    [DataField]
    public float HomogeneityMultiplier { get; private set; } = 1f;

    [DataField]
    public float StabilityMultiplier { get; private set; } = 1f;

    /// <summary>Additive fraction applied to concentration. Rushed runs drift away from the target.</summary>
    [DataField]
    public float ConcentrationBias { get; private set; }

    [DataField]
    public float CostMultiplier { get; private set; } = 1f;

    [DataField]
    public TimeSpan Duration { get; private set; } = TimeSpan.FromSeconds(30);

    /// <summary>What the producer writes on the label. It is a claim, not a measurement.</summary>
    [DataField]
    public float LabelPurity { get; private set; } = 0.9f;

    [DataField]
    public bool LabelSterile { get; private set; } = true;

    [DataField]
    public NCPharmaGrade LabelGrade { get; private set; } = NCPharmaGrade.Medical;
}

/// <summary>
/// An approved, versioned recipe. It describes intent; a physical result is always a batch.
/// </summary>
[Prototype("ncPharmaFormula")]
public sealed partial class NCPharmaFormulaPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name { get; private set; }

    [DataField]
    public int Version { get; private set; } = 1;

    /// <summary>The existing physiological drug profile this formula delivers.</summary>
    [DataField(required: true)]
    public ProtoId<NCDrugProfilePrototype> Compound { get; private set; }

    [DataField(required: true)]
    public ProtoId<NCPharmaDeliveryFormPrototype> DeliveryForm { get; private set; }

    /// <summary>Concentration one dose delivers at nominal quality, in the compound's concentration units.</summary>
    [DataField(required: true)]
    public float TargetConcentration { get; private set; }

    [DataField]
    public List<ProtoId<NCPharmaModifierPrototype>> Modifiers { get; private set; } = [];

    [DataField(required: true)]
    public ProtoId<NCPharmaProductionProfilePrototype> DefaultProductionProfile { get; private set; }

    [DataField]
    public float BaseStability { get; private set; } = 0.85f;

    [DataField]
    public NCPharmaRegulatoryStatus RegulatoryStatus { get; private set; } = NCPharmaRegulatoryStatus.Prescription;

    /// <summary>Formula this one was developed from. Empty for a base formula authored as data.</summary>
    public string? DerivedFrom { get; private set; }

    /// <summary>Name given by the pharmacologist who developed the version. Absent for data formulas.</summary>
    public string? CustomName { get; private set; }

    public string? Author { get; private set; }

    /// <summary>How this formula behaves in a reactor. A version inherits it from the formula it came from.</summary>
    [DataField]
    public ProtoId<NCPharmaReactionProfilePrototype>? Reaction { get; private set; }

    /// <summary>
    /// Builds a formula at runtime, for versions developed in game. A prototype loaded from YAML is never
    /// touched: every change is a new object with a new id.
    /// </summary>
    public static NCPharmaFormulaPrototype Create(
        string id,
        LocId name,
        int version,
        ProtoId<NCDrugProfilePrototype> compound,
        ProtoId<NCPharmaDeliveryFormPrototype> deliveryForm,
        float targetConcentration,
        List<ProtoId<NCPharmaModifierPrototype>> modifiers,
        ProtoId<NCPharmaProductionProfilePrototype> productionProfile,
        float baseStability,
        NCPharmaRegulatoryStatus status,
        string? derivedFrom,
        string? customName,
        string? author,
        ProtoId<NCPharmaReactionProfilePrototype>? reaction = null)
    {
        return new NCPharmaFormulaPrototype
        {
            ID = id,
            Name = name,
            Version = version,
            Compound = compound,
            DeliveryForm = deliveryForm,
            TargetConcentration = targetConcentration,
            Modifiers = modifiers,
            DefaultProductionProfile = productionProfile,
            BaseStability = baseStability,
            RegulatoryStatus = status,
            DerivedFrom = derivedFrom,
            CustomName = customName,
            Author = author,
            Reaction = reaction,
        };
    }

    /// <summary>Same formula with another regulatory status, used when a version is approved or recalled.</summary>
    public NCPharmaFormulaPrototype WithStatus(NCPharmaRegulatoryStatus status) => Create(
        ID, Name, Version, Compound, DeliveryForm, TargetConcentration, [.. Modifiers],
        DefaultProductionProfile, BaseStability, status, DerivedFrom, CustomName, Author, Reaction);
}
