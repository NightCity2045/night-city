// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
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
/// Directed per-user planner snapshot. Patient identity is display text only; the server keeps the actual
/// patient, facility, target region, wound and graft bindings in private runtime state.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCSurgeryConsoleUpdateMessage(
    string patientName,
    string facilityName,
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
    NCSurgeryConsoleNotice notice) : BoundUserInterfaceMessage
{
    public readonly string PatientName = patientName;
    public readonly string FacilityName = facilityName;
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
    public readonly NCSurgeryConsoleNotice Notice = notice;
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
