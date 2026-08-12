// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Diagnostics;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared.Body;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Surgery;

[Serializable, NetSerializable]
public enum NCSurgeryConsoleUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public enum NCSurgeryConsoleNotice : byte
{
    None,
    NoPatient,
    OutOfRange,
    FacilityUnavailable,
    SelectTargetZone,
    NoCompatibleProcedure,
    SelectionRejected,
    SelectionBound,
    GraftBound,
    SelectProcedure,
    HoldRequiredTool,
    ToolUnsuitable,
    StepStarted,
    StepCancelled,
    StepUnavailable,
    ProcedureCompleted,
}

[Serializable, NetSerializable]
public enum NCSurgeryConsoleStepState : byte
{
    Completed,
    Current,
    Upcoming,
    Blocked,
}

/// <summary>
/// Sanitized procedure row. It exposes stable prototype categories, not an EntityUid, wound Guid, graft
/// identity or any hidden exact medical value.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCSurgeryConsoleProcedureEntry(
    ProtoId<NCSurgeryProcedurePrototype> Procedure,
    ProtoId<OrganCategoryPrototype> TargetCategory,
    string TargetName,
    bool RequiresGraft);

/// <summary>
/// One concrete, player-facing tool requirement. The capability remains the authoritative rule while the
/// representative item gives the player an immediately recognizable instrument name.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCSurgeryConsoleToolRequirementEntry(
    ProtoId<NCSurgeryToolCapabilityPrototype> Capability,
    string CapabilityName,
    EntProtoId RepresentativeTool,
    string RepresentativeToolName,
    float MinimumQuality,
    float MinimumSterility,
    int ConsumedUses,
    bool HeldToolMatches);

[Serializable, NetSerializable]
public readonly record struct NCSurgeryConsoleStepEntry(
    ProtoId<NCSurgeryStepPrototype> Step,
    string Name,
    int Index,
    TimeSpan Duration,
    NCSurgicalAccessDepth RequiredAccessDepth,
    NCSurgeryConsoleStepState State,
    List<NCSurgeryConsoleToolRequirementEntry> Tools,
    bool CanExecute);

/// <summary>
/// Sanitized operating-console observations. Detailed wounds, stable identifiers and exact hidden records
/// remain server-only; these values are sufficient for surgical decisions and immediate feedback.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCSurgeryConsolePatientSummary(
    float Perfusion,
    float Oxygenation,
    float Shock,
    float Pain,
    NCPhysiologicalCondition Condition,
    NCConsciousnessState Consciousness,
    float Analgesia,
    float Sedation,
    float Anesthesia);

/// <summary>
/// Directed per-user planner snapshot. Patient identity is display text only; the server keeps the actual
/// patient, facility, target region, wound and graft bindings in private runtime state.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCSurgeryConsoleUpdateMessage(
    string patientName,
    string facilityName,
    ProtoId<NCAnatomyProfilePrototype>? anatomyProfile,
    List<NCMedicalZoneStatus> zoneStatuses,
    ProtoId<NCTargetZonePrototype>? selectedZone,
    ProtoId<OrganCategoryPrototype>? selectedRegion,
    NCSurgicalAccessDepth accessDepth,
    string? graftName,
    ProtoId<NCSurgeryProcedurePrototype>? selectedProcedure,
    ProtoId<OrganCategoryPrototype>? selectedTarget,
    ProtoId<NCSurgeryProcedurePrototype>? activeProcedure,
    int activeStepIndex,
    NCSurgerySessionState? activeState,
    List<NCSurgeryConsoleProcedureEntry> procedures,
    List<NCSurgeryConsoleStepEntry> steps,
    NCSurgeryConsolePatientSummary patientSummary,
    string? heldToolName,
    bool stepInProgress,
    TimeSpan stepElapsed,
    TimeSpan stepDuration,
    NCSurgeryConsoleNotice notice) : BoundUserInterfaceMessage
{
    public readonly string PatientName = patientName;
    public readonly string FacilityName = facilityName;
    public readonly ProtoId<NCAnatomyProfilePrototype>? AnatomyProfile = anatomyProfile;
    public readonly List<NCMedicalZoneStatus> ZoneStatuses = zoneStatuses;
    public readonly ProtoId<NCTargetZonePrototype>? SelectedZone = selectedZone;
    public readonly ProtoId<OrganCategoryPrototype>? SelectedRegion = selectedRegion;
    public readonly NCSurgicalAccessDepth AccessDepth = accessDepth;
    public readonly string? GraftName = graftName;
    public readonly ProtoId<NCSurgeryProcedurePrototype>? SelectedProcedure = selectedProcedure;
    public readonly ProtoId<OrganCategoryPrototype>? SelectedTarget = selectedTarget;
    public readonly ProtoId<NCSurgeryProcedurePrototype>? ActiveProcedure = activeProcedure;
    public readonly int ActiveStepIndex = activeStepIndex;
    public readonly NCSurgerySessionState? ActiveState = activeState;
    public readonly List<NCSurgeryConsoleProcedureEntry> Procedures = procedures;
    public readonly List<NCSurgeryConsoleStepEntry> Steps = steps;
    public readonly NCSurgeryConsolePatientSummary PatientSummary = patientSummary;
    public readonly string? HeldToolName = heldToolName;
    public readonly bool StepInProgress = stepInProgress;
    public readonly TimeSpan StepElapsed = stepElapsed;
    public readonly TimeSpan StepDuration = stepDuration;
    public readonly NCSurgeryConsoleNotice Notice = notice;
}

/// <summary>
/// Untrusted anatomical planning intent. The server resolves the zone against the patient physically bound
/// to this console and never accepts a patient or organ entity from the client.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCSurgeryConsoleSelectZoneMessage(
    ProtoId<NCTargetZonePrototype> zone) : BoundUserInterfaceMessage
{
    public readonly ProtoId<NCTargetZonePrototype> Zone = zone;
}

/// <summary>
/// Untrusted intent. Patient, facility, live region, wound and graft are deliberately absent and are
/// resolved again from the open console's physical server state.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCSurgeryConsoleSelectMessage(
    ProtoId<NCSurgeryProcedurePrototype> procedure,
    ProtoId<OrganCategoryPrototype> targetCategory) : BoundUserInterfaceMessage
{
    public readonly ProtoId<NCSurgeryProcedurePrototype> Procedure = procedure;
    public readonly ProtoId<OrganCategoryPrototype> TargetCategory = targetCategory;
}

[Serializable, NetSerializable]
public sealed class NCSurgeryConsoleClearSelectionMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class NCSurgeryConsoleRefreshMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class NCSurgeryConsoleExecuteStepMessage : BoundUserInterfaceMessage;
