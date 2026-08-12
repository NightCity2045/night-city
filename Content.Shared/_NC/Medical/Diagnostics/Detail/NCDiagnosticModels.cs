// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared._NC.Medical.Transplantation;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Diagnostics.Detail;

[Serializable, NetSerializable]
public enum NCDiagnosticTier : byte
{
    SelfSensations,
    Visual,
    Manual,
    HandheldApproximate,
    ScannerInternal,
    SurgicalConsole,
    AdminExact,
}

/// <summary>
/// Cumulative categories a server-observed capability may disclose. YAML profiles define the actual policy.
/// </summary>
[Flags, Serializable, NetSerializable]
public enum NCDiagnosticDisclosure : uint
{
    None = 0,
    SubjectiveSymptoms = 1 << 0,
    VisibleTrauma = 1 << 1,
    ExternalBleeding = 1 << 2,
    ManualVitals = 1 << 3,
    StructuralInstability = 1 << 4,
    ApproximateVitals = 1 << 5,
    ProbableRegions = 1 << 6,
    InternalBleeding = 1 << 7,
    Fractures = 1 << 8,
    DetailedWounds = 1 << 9,
    Organs = 1 << 10,
    Infection = 1 << 11,
    ForeignBodies = 1 << 12,
    SurgicalState = 1 << 13,
    Cyberware = 1 << 14,
    Drugs = 1 << 15,
}

[Flags, Serializable, NetSerializable]
public enum NCDiagnosticFindingFlags : ushort
{
    None = 0,
    Painful = 1 << 0,
    OpenWound = 1 << 1,
    ExternalBleeding = 1 << 2,
    Deformity = 1 << 3,
    Burn = 1 << 4,
    ProbableInjury = 1 << 5,
    FractureSuspected = 1 << 6,
    FractureConfirmed = 1 << 7,
    InternalBleeding = 1 << 8,
    OrganImpairment = 1 << 9,
    OrganFailure = 1 << 10,
    NeurologicalDeficit = 1 << 11,
    ForeignBody = 1 << 12,
    Infection = 1 << 13,
    SurgicalAccess = 1 << 14,
    MechanicalDamage = 1 << 15,
}

[Serializable, NetSerializable]
public enum NCDiagnosticBand : byte
{
    None,
    Low,
    Moderate,
    High,
    Critical,
}

[Serializable, NetSerializable]
public enum NCDiagnosticWarning : byte
{
    Pain,
    Weakness,
    RespiratoryDistress,
    Dizziness,
    FunctionalLoss,
    ActiveExternalBleeding,
    SuspectedInternalBleeding,
    ConfirmedInternalBleeding,
    FractureSuspected,
    FractureConfirmed,
    OrganImpairment,
    OrganFailure,
    Hypoxia,
    Hypoperfusion,
    Shock,
    ForeignBody,
    Infection,
    ActiveSurgery,
    CriticalCondition,
    CyberwareOffline,
    CyberwareOverheated,
    CyberwareEmpDisabled,
    NeuralInterfaceFault,
    Sedated,
    Anesthetized,
    DrugOverdose,
    Sepsis,
    TransplantRejection,
}

/// <summary>
/// Quantized inclusive range. Units are fixed by the field carrying it (millilitres, permille or milliunits/s).
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCDiagnosticNumericRange(int Minimum, int Maximum);

[Serializable, NetSerializable]
public readonly record struct NCMedicalVitalsSummary(
    NCDiagnosticBand Pain,
    NCDiagnosticBand Shock,
    NCConsciousnessState Consciousness,
    NCPhysiologicalCondition Condition,
    NCBloodPressureState? BloodPressure,
    NCHeartRhythm? HeartRhythm,
    NCDiagnosticNumericRange? OxygenationPermille,
    NCDiagnosticNumericRange? BloodVolumeMl,
    NCDiagnosticNumericRange? PerfusionPermille,
    NCDiagnosticNumericRange? ExternalBleedMilliunits,
    NCDiagnosticNumericRange? InternalBleedMilliunits);

[Serializable, NetSerializable]
public readonly record struct NCMedicalRegionSummary(
    ProtoId<NCTargetZonePrototype> Zone,
    NCDiagnosticFindingFlags Findings,
    NCDiagnosticBand Burden,
    byte? WoundCount,
    NCBoneState? BoneState,
    NCDiagnosticBand Function);

/// <summary>
/// Sanitized wound row. It deliberately has no Guid or forensic resolution identifier at any network tier.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCMedicalWoundSummary(
    ProtoId<NCTargetZonePrototype> Zone,
    ProtoId<NCWoundPrototype>? Prototype,
    NCWoundType Type,
    NCWoundSeverity Severity,
    NCWoundStage? Stage,
    NCTissueDepth? Depth,
    NCDiagnosticBand ExternalBleeding,
    NCDiagnosticBand InternalBleeding,
    NCDiagnosticBand Infection,
    bool ForeignBody);

[Serializable, NetSerializable]
public readonly record struct NCMedicalOrganSummary(
    ProtoId<OrganCategoryPrototype> Category,
    NCOrganFunctionType FunctionType,
    bool Vital,
    NCDiagnosticBand Integrity,
    NCDiagnosticBand Function,
    bool Failing,
    NCDiagnosticBand Rejection);

[Serializable, NetSerializable]
public readonly record struct NCMedicalCyberwareSummary(
    ProtoId<NCCyberwarePrototype> Profile,
    NCCyberwareForm Form,
    ProtoId<OrganCategoryPrototype>? HostCategory,
    ProtoId<NCCyberwareSlotPrototype>? Slot,
    NCDiagnosticBand Integrity,
    NCDiagnosticBand Function,
    NCDiagnosticBand Power,
    NCDiagnosticBand Heat,
    NCCyberwareNeuralState NeuralState,
    bool Operational,
    bool EmpDisabled);

[Serializable, NetSerializable]
public readonly record struct NCMedicalDrugSummary(
    ProtoId<NCDrugProfilePrototype> Profile,
    NCDrugCategory Category,
    NCDiagnosticNumericRange ConcentrationMilliunits,
    bool Therapeutic,
    bool Toxic);

[Serializable, NetSerializable]
public readonly record struct NCMedicalSurgerySummary(
    ProtoId<OrganCategoryPrototype> Region,
    ProtoId<NCSurgeryProcedurePrototype> Procedure,
    int StepIndex,
    NCSurgerySessionState State,
    NCSurgicalAccessDepth AccessDepth);

/// <summary>
/// Capability-filtered, deterministic network DTO. It contains no EntityUid, wound Guid or hidden history.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCMedicalSnapshot(
    NCActionResolutionId resolutionId,
    NCDiagnosticTier tier,
    NCDiagnosticDisclosure disclosures,
    ushort snapshotVersion,
    uint medicalRevision,
    TimeSpan observedAt,
    NCMedicalVitalsSummary vitals,
    List<NCMedicalRegionSummary> regions,
    List<NCMedicalWoundSummary> wounds,
    List<NCMedicalOrganSummary> organs,
    List<NCMedicalCyberwareSummary> cyberware,
    List<NCMedicalDrugSummary> drugs,
    NCMedicalSurgerySummary? surgery,
    List<NCDiagnosticWarning> warnings,
    ushort woundsOmitted,
    ushort organsOmitted,
    ushort cyberwareOmitted,
    ushort drugsOmitted)
{
    /// <summary>
    /// Server-authoritative correlation for this observation. It is not a wound or forensic identifier.
    /// </summary>
    public NCActionResolutionId ResolutionId { get; } = resolutionId;
    public NCDiagnosticTier Tier { get; } = tier;
    public NCDiagnosticDisclosure Disclosures { get; } = disclosures;
    public ushort SnapshotVersion { get; } = snapshotVersion;
    public uint MedicalRevision { get; } = medicalRevision;
    public TimeSpan ObservedAt { get; } = observedAt;
    public NCMedicalVitalsSummary Vitals { get; } = vitals;
    public List<NCMedicalRegionSummary> Regions { get; } = regions;
    public List<NCMedicalWoundSummary> Wounds { get; } = wounds;
    public List<NCMedicalOrganSummary> Organs { get; } = organs;
    public List<NCMedicalCyberwareSummary> Cyberware { get; } = cyberware;
    public List<NCMedicalDrugSummary> Drugs { get; } = drugs;
    public NCMedicalSurgerySummary? Surgery { get; } = surgery;
    public List<NCDiagnosticWarning> Warnings { get; } = warnings;
    public ushort WoundsOmitted { get; } = woundsOmitted;
    public ushort OrgansOmitted { get; } = organsOmitted;
    public ushort CyberwareOmitted { get; } = cyberwareOmitted;
    public ushort DrugsOmitted { get; } = drugsOmitted;
}
