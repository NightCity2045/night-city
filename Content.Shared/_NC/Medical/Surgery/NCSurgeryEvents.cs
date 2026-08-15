// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Content.Shared.DoAfter;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Surgery;

[ByRefEvent]
public readonly record struct NCSurgeryStartedEvent(
    NCActionResolutionId ResolutionId,
    EntityUid Surgeon,
    EntityUid TargetRegion,
    ProtoId<NCSurgeryProcedurePrototype> Procedure);

[ByRefEvent]
public readonly record struct NCSurgeryStepResolvedEvent(
    NCActionResolutionId ResolutionId,
    NCActionResolutionId ActionResolutionId,
    ProtoId<NCSurgeryStepPrototype> Step,
    int StepIndex,
    NCSurgeryStepOutcome Outcome);

[ByRefEvent]
public readonly record struct NCSurgeryPausedEvent(
    NCActionResolutionId ResolutionId,
    NCSurgeryInterruptionReason Reason);

[ByRefEvent]
public readonly record struct NCSurgeryCompletedEvent(
    NCActionResolutionId ResolutionId,
    ProtoId<NCSurgeryProcedurePrototype> Procedure);

[ByRefEvent]
public readonly record struct NCSurgeryAbortedEvent(
    NCActionResolutionId ResolutionId,
    NCActionResolutionId ActionResolutionId,
    NCSurgeryAbortReason Reason);

/// <summary>
/// Round-local forensic finding produced by a completed autopsy step. Hidden wound/action identifiers remain
/// server-only; diagnostic clients receive only this sanitized anatomical summary.
/// </summary>
[ByRefEvent]
public readonly record struct NCAutopsyRecordedEvent(
    ProtoId<OrganCategoryPrototype> Region,
    int WoundCount,
    IReadOnlyList<NCWoundType> WoundTypes);

/// <summary>
/// Raised after any applied surgical mutation so physiology, diagnostics and persistence adapters can
/// invalidate their own domains without the surgery system owning those subsystems.
/// </summary>
[ByRefEvent]
public readonly record struct NCSurgeryMedicalStateChangedEvent(
    NCActionResolutionId ResolutionId,
    EntityUid TargetRegion);

/// <summary>
/// Local UI refresh boundary for a timed surgical interaction. It carries only the practitioner identity and
/// execution state; the console resolves patient, facility and the live step again from authoritative ECS state.
/// </summary>
[ByRefEvent]
public readonly record struct NCSurgeryStepExecutionChangedEvent(
    EntityUid Practitioner,
    bool Running,
    bool Cancelled,
    bool ProcedureCompleted = false);

/// <summary>
/// One explicit surgery step carried through the engine DoAfter. The session/step tuple prevents a
/// stale completion from advancing a different procedure that later reused the same region.
/// </summary>
[Serializable, NetSerializable]
public sealed partial class NCSurgeryStepDoAfterEvent : DoAfterEvent
{
    [DataField]
    public NCActionResolutionId SessionResolutionId;

    [DataField]
    public NCActionResolutionId ActionResolutionId;

    [DataField]
    public NetEntity TargetRegion;

    [DataField]
    public int StepIndex;

    private NCSurgeryStepDoAfterEvent()
    {
    }

    public NCSurgeryStepDoAfterEvent(
        NCActionResolutionId sessionResolutionId,
        NCActionResolutionId actionResolutionId,
        NetEntity targetRegion,
        int stepIndex)
    {
        SessionResolutionId = sessionResolutionId;
        ActionResolutionId = actionResolutionId;
        TargetRegion = targetRegion;
        StepIndex = stepIndex;
    }

    public override DoAfterEvent Clone()
    {
        return new NCSurgeryStepDoAfterEvent(
            SessionResolutionId,
            ActionResolutionId,
            TargetRegion,
            StepIndex);
    }

    public override bool IsDuplicate(DoAfterEvent other)
    {
        return other is NCSurgeryStepDoAfterEvent step && step.ActionResolutionId == ActionResolutionId;
    }
}
