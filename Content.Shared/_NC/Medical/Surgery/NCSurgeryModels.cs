// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Surgery;

public readonly record struct NCSurgeryStartRequest(
    NCActionResolutionId ResolutionId,
    EntityUid Patient,
    EntityUid Surgeon,
    EntityUid TargetRegion,
    ProtoId<NCSurgeryProcedurePrototype> Procedure,
    EntityUid? Facility = null,
    EntityUid? Environment = null,
    Guid? TargetWoundId = null,
    EntityUid? Graft = null);

public readonly record struct NCSurgeryStepRequest(
    NCActionResolutionId ActionResolutionId,
    EntityUid Surgeon,
    EntityUid? Tool = null);

public readonly record struct NCSurgeryStepResolution(
    NCActionResolutionId ActionResolutionId,
    NCSurgeryStepResolutionStatus Status,
    NCSurgeryStepFailure Failure,
    ProtoId<NCSurgeryStepPrototype>? Step,
    int StepIndex,
    NCSurgeryStepOutcome? Outcome,
    TimeSpan Duration,
    NCSurgeryConditionSnapshot? Conditions);

/// <summary>
/// Server-observed inputs retained with every applied transition for deterministic audit and replay.
/// </summary>
[DataDefinition]
public sealed partial class NCSurgeryConditionSnapshot
{
    [DataField]
    public float ToolQuality;

    [DataField]
    public float ToolSterility;

    [DataField]
    public float FacilityQuality;

    [DataField]
    public float FacilitySterility;

    [DataField]
    public bool FacilityPowered;

    [DataField]
    public bool FacilityStable;

    [DataField]
    public float EnvironmentSterility;

    [DataField]
    public float EnvironmentLighting;

    [DataField]
    public float EnvironmentMotion;

    [DataField]
    public float EnvironmentContamination;

    [DataField]
    public float PatientOxygenation = 1f;

    [DataField]
    public NCShockStage PatientShock;

    [DataField]
    public NCConsciousnessState PatientConsciousness = NCConsciousnessState.Alert;

    [DataField]
    public NCPhysiologicalCondition PatientCondition = NCPhysiologicalCondition.Stable;

    [DataField]
    public float PatientAnalgesia;

    [DataField]
    public float PatientSedation;

    [DataField]
    public float PatientAnesthesia;

    [DataField]
    public float PatientMovement;

    [DataField]
    public bool PatientRestrained;

    [DataField]
    public bool AirwayMonitored;

    [DataField]
    public float SiteContamination;
}

[DataDefinition]
public sealed partial class NCSurgeryStepAuditRecord
{
    [DataField]
    public NCActionResolutionId ActionResolutionId;

    [DataField]
    public ProtoId<NCSurgeryStepPrototype> Step;

    [DataField]
    public int StepIndex;

    [DataField]
    public NCSurgeryStepOutcome Outcome;

    [DataField]
    public TimeSpan ResolvedAt;

    [DataField]
    public NCSurgeryConditionSnapshot Conditions = new();
}

/// <summary>
/// One deterministic regional autopsy result. Forensic IDs point to physical action resolutions already
/// stored by wounds; no actor, body or organ EntityUid is persisted.
/// </summary>
[DataDefinition]
public sealed partial class NCAutopsyRegionRecord
{
    [DataField]
    public NCActionResolutionId ResolutionId;

    [DataField(required: true)]
    public ProtoId<OrganCategoryPrototype> Region;

    [DataField]
    public int WoundCount;

    [DataField]
    public float TotalIntegrityDamage;

    [DataField]
    public List<NCWoundType> WoundTypes = [];

    [DataField]
    public List<Guid> ForensicResolutionIds = [];

    [DataField]
    public TimeSpan ExaminedAt;
}

public enum NCSurgerySessionState : byte
{
    Running,
    Paused,
    Completed,
    Aborted,
}

public enum NCSurgeryStepResolutionStatus : byte
{
    Resolved,
    Duplicate,
    Completed,
    Paused,
    Rejected,
}

public enum NCSurgeryStartFailure : byte
{
    None,
    InvalidResolutionId,
    EntityUnavailable,
    PatientNotAuthoritative,
    ProcedureUnavailable,
    RegionNotInPatient,
    SurgicalSiteUnavailable,
    SiteBusy,
    SurgeonBusy,
    AuthorizationMissing,
    IncompatibleBodyProfile,
    IncompatibleTarget,
    TargetWoundUnavailable,
    StartingAccessMismatch,
    FacilityUnavailable,
    FacilityCapabilityMissing,
    EnvironmentUnavailable,
    EnvironmentUnsafe,
    PatientConditionNotMet,
    GraftUnavailable,
    GraftIncompatible,
}

public enum NCSurgeryStepFailure : byte
{
    None,
    InvalidResolutionId,
    SessionUnavailable,
    SessionNotRunning,
    WrongSurgeon,
    ProcedureUnavailable,
    StepUnavailable,
    AccessChanged,
    TargetChanged,
    GraftChanged,
    ToolUnavailable,
    ToolCapabilityMissing,
    ToolQualityInsufficient,
    ToolSterilityInsufficient,
    ToolExhausted,
    FacilityUnavailable,
    FacilityCapabilityMissing,
    EnvironmentUnavailable,
    EnvironmentUnsafe,
    PatientConditionNotMet,
    EffectContractInvalid,
}

public enum NCSurgeryAbortFailure : byte
{
    None,
    InvalidResolutionId,
    SessionUnavailable,
    WrongSurgeon,
    AlreadyTerminal,
}

public enum NCSurgeryInterruptionReason : byte
{
    None,
    Manual,
    SurgeonUnavailable,
    PatientMoved,
    PatientUnstable,
    FacilityUnavailable,
    EnvironmentUnsafe,
    ConditionsChanged,
}

public enum NCSurgeryAbortReason : byte
{
    Manual,
    PatientUnavailable,
    TargetRegionUnavailable,
    IrrecoverableConditions,
}
