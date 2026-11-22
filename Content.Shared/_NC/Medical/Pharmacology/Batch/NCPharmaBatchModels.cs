// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

using Content.Shared._NC.Medical.Pharmacology.Lab;

namespace Content.Shared._NC.Medical.Pharmacology.Batch;

/// <summary>Market-facing class of a batch. It describes measured parameters, it never multiplies strength.</summary>
[Serializable, NetSerializable]
public enum NCPharmaGrade : byte
{
    Street,
    Medical,
    Corporate,
}

/// <summary>Legal status of a formula. It affects licensing and enforcement, never physiology.</summary>
[Serializable, NetSerializable]
public enum NCPharmaRegulatoryStatus : byte
{
    Unknown,
    FreeSale,
    Prescription,
    Controlled,
    ClinicalOnly,
    Corporate,
    Banned,
    Recalled,
    Experimental,
}

/// <summary>How far the real composition of a batch has been confirmed by laboratory work.</summary>
[Serializable, NetSerializable]
public enum NCPharmaQcState : byte
{
    Untested,
    PartialQc,
    Certified,
    Rejected,
    Recalled,
}

[Flags]
[Serializable, NetSerializable]
public enum NCPharmaQcTests : byte
{
    None = 0,
    Concentration = 1 << 0,
    Purity = 1 << 1,
    Sterility = 1 << 2,
    Homogeneity = 1 << 3,
    Stability = 1 << 4,
    All = Concentration | Purity | Sterility | Homogeneity | Stability,
}

/// <summary>What happened to a formula under development. The journal is the lab notebook of its lineage.</summary>
[Serializable, NetSerializable]
public enum NCPharmaJournalKind : byte
{
    VersionCreated,
    Produced,
    QcPerformed,
    Administered,
    Approved,
    Experiment,
    Note,
}

/// <summary>The five branches of the formula tree.</summary>
[Serializable, NetSerializable]
public enum NCPharmaNodeBranch : byte
{
    Core,
    Delivery,
    Kinetics,
    Safety,
    Production,
}

/// <summary>How much a laboratory knows about one node of the formula tree.</summary>
[Serializable, NetSerializable]
public enum NCPharmaNodeState : byte
{
    Unknown,
    Suspected,
    Confirmed,
}

/// <summary>What the laboratory knows about one node. Nodes that are not listed are unknown.</summary>
[Serializable, NetSerializable]
public readonly record struct NCPharmaNodeKnowledge(string Id, NCPharmaNodeState State);

/// <summary>Result of a research request to the formula registry.</summary>
[Serializable, NetSerializable]
public enum NCPharmaRdNotice : byte
{
    None,
    Created,
    Approved,
    InvalidRequest,
    InvalidName,
    TooManyFormulas,
    SameCore,
    NotExperimental,
    NotCertified,
    Unidentified,
    NoteSaved,
    InvalidNote,
}

public enum NCPharmaAuditKind : byte
{
    Created,
    QcPerformed,
    LabelChanged,
    Transferred,
    Dispensed,
    Administered,
    Recalled,
    AdminInspected,
    Degraded,
    AdminAdjusted,
    Treated,
}

/// <summary>
/// The five independent measured parameters of a batch. Purity and sterility are deliberately separate:
/// a chemically pure batch can still be medically dangerous.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCPharmaQuality(
    float Concentration,
    float Purity,
    float Sterility,
    float Homogeneity,
    float Stability);

/// <summary>What the producer claims. Players only ever see this until a laboratory measures the batch.</summary>
[Serializable, NetSerializable]
public readonly record struct NCPharmaLabel(
    string BatchCode,
    string FormulaId,
    int FormulaVersion,
    float Concentration,
    float Purity,
    bool Sterile,
    NCPharmaGrade Grade);

/// <summary>Raw material or equipment quality feeding one production run.</summary>
public readonly record struct NCPharmaProductionInputs(
    float InputUnits,
    float InputPurity,
    float EquipmentSterility,
    float EquipmentMixing,
    float CalibrationError,
    float Contaminants = 0f);

public readonly record struct NCPharmaProductionOutcome(
    NCPharmaQuality Quality,
    int Doses,
    float ModifierToxicity,
    float CostMultiplier,
    TimeSpan Duration);

/// <summary>Physiological consequences of exactly one administered dose.</summary>
public readonly record struct NCPharmaDoseEffect(
    float EffectiveConcentration,
    float ToxinBurden,
    float SepsisBurden);

/// <summary>
/// What one laboratory run revealed. Only the tests that were actually performed carry a value.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCPharmaQcReport(
    Guid BatchId,
    NCPharmaQcTests Performed,
    NCPharmaQcTests Failed,
    float? Concentration,
    float? Purity,
    float? Sterility,
    float? Homogeneity,
    float? Stability);

/// <summary>
/// Expected behavior of a formula before anything is produced. It is derived from the same data the real
/// production uses, so a designer can see the price of a change before paying for it.
/// </summary>
public readonly record struct NCPharmaForecast(
    float PeakConcentration,
    float ToxicMargin,
    float Stability,
    float ToxicityMultiplier,
    TimeSpan Absorption,
    float CostMultiplier);

/// <summary>Plain, replicated description of one formula. Prototype formulas and derived versions look alike.</summary>
[Serializable, NetSerializable]
public readonly record struct NCPharmaFormulaSummary(
    string Id,
    int Version,
    string? NameKey,
    string? CustomName,
    string Compound,
    string DeliveryForm,
    float TargetConcentration,
    string[] Modifiers,
    string DefaultProfile,
    NCPharmaRegulatoryStatus Status,
    string? DerivedFrom,
    bool CanApprove,
    float ToxicConcentration = 0f,
    NCPharmaFeedstockOption[]? Feedstocks = null,
    string? SecondCompound = null);

[Serializable, NetSerializable]
public readonly record struct NCPharmaJournalSummary(
    NCPharmaJournalKind Kind,
    string FormulaId,
    string BatchCode,
    string Actor,
    string Detail);

/// <summary>What a study of a dose did to a test subject.</summary>
public enum NCPharmaStudyOutcome : byte
{
    Tolerated,
    Strained,
    Lost,
}

/// <summary>Result of giving one dose to a test subject. Peak is the concentration the subject reached.</summary>
public readonly record struct NCPharmaStudyResult(
    NCPharmaStudyOutcome Outcome,
    float Peak,
    float ToxicRatio,
    float Toxin,
    float Sepsis);

/// <summary>Stable text ids of the nodes of the formula tree, shared by the server and the client.</summary>
public static class NCPharmaNodeIds
{
    public static string Core(string id) => "core:" + id;

    public static string Form(string id) => "form:" + id;

    public static string Modifier(string id) => "mod:" + id;

    public static string Profile(string id) => "profile:" + id;
}
