// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.ActionResolution;
using Content.Server._NC.Medical.Anatomy.Detachment;
using Content.Server._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Anatomy.Detachment;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Surgery;

/// <summary>
/// Resolves explicit surgical procedures against server-observed state. There are no skill rolls,
/// random checks, world scans or client-owned medical values in this system.
/// </summary>
public sealed partial class NCSurgerySystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCMedicalBodySystem _medicalBodies = default!;
    [Dependency] private NCOrganDetachmentSystem _detachment = default!;
    [Dependency] private NCPharmacologySystem _pharmacology = default!;

    public override void Initialize()
    {
        base.Initialize();

        // Locks are stored on their owners, so cleanup is constant-time and never searches every session.
        SubscribeLocalEvent<NCSurgeryPractitionerComponent, EntityTerminatingEvent>(OnPractitionerTerminating);
        SubscribeLocalEvent<NCSurgerySessionComponent, EntityTerminatingEvent>(OnSessionTerminating);
    }

    /// <summary>
    /// Read-only gameplay/UI preflight for one exact live region. It deterministically binds the worst
    /// compatible wound and applies the same anatomy, access, pathology and graft gates as StartSurgery.
    /// Facility, environment and practitioner authorization are validated when the physical action starts.
    /// </summary>
    public bool TryPrepareProcedureTarget(
        EntityUid patient,
        EntityUid targetRegion,
        ProtoId<NCSurgeryProcedurePrototype> procedureId,
        EntityUid? graft,
        out Guid? targetWoundId,
        out NCSurgeryStartFailure failure)
    {
        targetWoundId = null;
        failure = NCSurgeryStartFailure.None;
        if (Deleted(patient) || Deleted(targetRegion))
        {
            failure = NCSurgeryStartFailure.EntityUnavailable;
            return false;
        }

        if (!TryComp<NCMedicalBodyComponent>(patient, out var medical))
        {
            failure = NCSurgeryStartFailure.PatientNotAuthoritative;
            return false;
        }

        if (!_prototypes.TryIndex(procedureId, out NCSurgeryProcedurePrototype? procedure))
        {
            failure = NCSurgeryStartFailure.ProcedureUnavailable;
            return false;
        }

        if (!procedure.CompatibleBodyProfiles.Contains(medical.Profile))
        {
            failure = NCSurgeryStartFailure.IncompatibleBodyProfile;
            return false;
        }

        if (!RegionBelongsToPatient(patient, targetRegion))
        {
            failure = NCSurgeryStartFailure.RegionNotInPatient;
            return false;
        }

        if (!TryComp<NCSurgicalSiteComponent>(targetRegion, out var site))
        {
            failure = NCSurgeryStartFailure.SurgicalSiteUnavailable;
            return false;
        }

        if (site.ActiveProcedure != null)
        {
            failure = NCSurgeryStartFailure.SiteBusy;
            return false;
        }

        if (!procedure.StartingAccessDepths.Contains(site.AccessDepth))
        {
            failure = NCSurgeryStartFailure.StartingAccessMismatch;
            return false;
        }

        if (procedure.RequiresTargetWound &&
            !TrySelectTargetWound(patient, targetRegion, procedure, out targetWoundId))
        {
            failure = NCSurgeryStartFailure.TargetWoundUnavailable;
            return false;
        }

        if (!ValidateTarget(patient, targetRegion, targetWoundId, procedure))
        {
            failure = procedure.RequiresTargetWound
                ? NCSurgeryStartFailure.TargetWoundUnavailable
                : NCSurgeryStartFailure.IncompatibleTarget;
            return false;
        }

        if (!ValidateGraft(patient, targetRegion, graft, procedure))
        {
            failure = graft == null
                ? NCSurgeryStartFailure.GraftUnavailable
                : NCSurgeryStartFailure.GraftIncompatible;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Starts one explicitly selected procedure on a materialized region.
    /// </summary>
    public bool StartSurgery(
        in NCSurgeryStartRequest request,
        out EntityUid sessionUid,
        out NCSurgeryStartFailure failure)
    {
        sessionUid = default;
        failure = NCSurgeryStartFailure.None;

        if (!request.ResolutionId.IsValid)
        {
            failure = NCSurgeryStartFailure.InvalidResolutionId;
            return false;
        }

        if (Deleted(request.Patient) ||
            Deleted(request.Surgeon) ||
            Deleted(request.TargetRegion))
        {
            failure = NCSurgeryStartFailure.EntityUnavailable;
            return false;
        }

        if (!TryComp<NCMedicalBodyComponent>(request.Patient, out var medical))
        {
            failure = NCSurgeryStartFailure.PatientNotAuthoritative;
            return false;
        }

        if (!_prototypes.TryIndex(request.Procedure, out var procedure))
        {
            failure = NCSurgeryStartFailure.ProcedureUnavailable;
            return false;
        }

        if (!procedure.CompatibleBodyProfiles.Contains(medical.Profile))
        {
            failure = NCSurgeryStartFailure.IncompatibleBodyProfile;
            return false;
        }

        if (!RegionBelongsToPatient(request.Patient, request.TargetRegion))
        {
            failure = NCSurgeryStartFailure.RegionNotInPatient;
            return false;
        }

        if (!TryComp<NCSurgicalSiteComponent>(request.TargetRegion, out var site))
        {
            failure = NCSurgeryStartFailure.SurgicalSiteUnavailable;
            return false;
        }

        if (site.ActiveProcedure != null ||
            TryComp<NCSurgerySessionComponent>(request.TargetRegion, out var activeSession) &&
            IsActive(activeSession.State))
        {
            failure = NCSurgeryStartFailure.SiteBusy;
            return false;
        }

        if (!procedure.StartingAccessDepths.Contains(site.AccessDepth))
        {
            failure = NCSurgeryStartFailure.StartingAccessMismatch;
            return false;
        }

        if (!ValidateTarget(request.Patient, request.TargetRegion, request.TargetWoundId, procedure))
        {
            failure = procedure.RequiresTargetWound
                ? NCSurgeryStartFailure.TargetWoundUnavailable
                : NCSurgeryStartFailure.IncompatibleTarget;
            return false;
        }

        if (!ValidateGraft(request.Patient, request.TargetRegion, request.Graft, procedure))
        {
            failure = request.Graft == null
                ? NCSurgeryStartFailure.GraftUnavailable
                : NCSurgeryStartFailure.GraftIncompatible;
            return false;
        }

        if (!ValidateSurgeonAuthorization(request.Surgeon, procedure))
        {
            failure = NCSurgeryStartFailure.AuthorizationMissing;
            return false;
        }

        if (TryComp<NCSurgeryPractitionerComponent>(request.Surgeon, out var existingPractitioner) &&
            existingPractitioner.ActiveSession is { } existingUid &&
            SessionIsLive(existingUid))
        {
            failure = NCSurgeryStartFailure.SurgeonBusy;
            return false;
        }

        var conditionFailure = ValidateBoundConditions(
            request.Patient,
            request.Facility,
            request.Environment,
            procedure.Facility,
            procedure.Environment,
            procedure.Patient);

        if (conditionFailure != NCSurgeryConditionFailure.None)
        {
            failure = MapStartFailure(conditionFailure);
            return false;
        }

        // Every validation above is read-only. Locks and session state are published together only now.
        var practitioner = EnsureComp<NCSurgeryPractitionerComponent>(request.Surgeon);
        var session = EnsureComp<NCSurgerySessionComponent>(request.TargetRegion);
        session.ResolutionId = request.ResolutionId;
        session.Patient = request.Patient;
        session.Surgeon = request.Surgeon;
        session.TargetRegion = request.TargetRegion;
        session.Facility = request.Facility;
        session.Environment = request.Environment;
        session.TargetWoundId = request.TargetWoundId;
        session.Graft = request.Graft;
        session.DetachedResult = null;
        session.Procedure = request.Procedure;
        session.CurrentStepIndex = 0;
        session.State = NCSurgerySessionState.Running;
        session.PauseReason = NCSurgeryInterruptionReason.None;
        session.StartedAt = _timing.CurTime;
        session.LastTransitionAt = _timing.CurTime;
        session.TerminalResolutionId = null;
        session.History.Clear();

        practitioner.ActiveSession = request.TargetRegion;
        site.ActiveProcedure = request.ResolutionId.Value;
        sessionUid = request.TargetRegion;

        var started = new NCSurgeryStartedEvent(
            request.ResolutionId,
            request.Surgeon,
            request.TargetRegion,
            request.Procedure);
        RaiseLocalEvent(request.Patient, ref started);
        return true;
    }

    /// <summary>
    /// Resolves the current step exactly once. The action ID makes retry delivery idempotent.
    /// Conditions are observed again here, after any external timed action has finished.
    /// </summary>
    public NCSurgeryStepResolution ResolveStep(EntityUid sessionUid, in NCSurgeryStepRequest request)
    {
        if (!request.ActionResolutionId.IsValid)
        {
            return Rejected(
                request.ActionResolutionId,
                NCSurgeryStepFailure.InvalidResolutionId);
        }

        if (!TryComp<NCSurgerySessionComponent>(sessionUid, out var session))
        {
            return Rejected(
                request.ActionResolutionId,
                NCSurgeryStepFailure.SessionUnavailable);
        }

        // A delivered retry returns the original outcome and never applies effects or consumption twice.
        foreach (var record in session.History)
        {
            if (record.ActionResolutionId != request.ActionResolutionId)
                continue;

            var replayStep = _prototypes.TryIndex(record.Step, out var replayPrototype)
                ? replayPrototype.Duration
                : TimeSpan.Zero;
            return new NCSurgeryStepResolution(
                request.ActionResolutionId,
                NCSurgeryStepResolutionStatus.Duplicate,
                NCSurgeryStepFailure.None,
                record.Step,
                record.StepIndex,
                record.Outcome,
                replayStep,
                record.Conditions);
        }

        if (session.State != NCSurgerySessionState.Running)
        {
            return Rejected(
                request.ActionResolutionId,
                NCSurgeryStepFailure.SessionNotRunning,
                session.CurrentStepIndex);
        }

        if (request.Surgeon != session.Surgeon)
        {
            return Rejected(
                request.ActionResolutionId,
                NCSurgeryStepFailure.WrongSurgeon,
                session.CurrentStepIndex);
        }

        if (!_prototypes.TryIndex(session.Procedure, out var procedure))
        {
            PauseInternal(sessionUid, session, NCSurgeryInterruptionReason.ConditionsChanged);
            return Rejected(
                request.ActionResolutionId,
                NCSurgeryStepFailure.ProcedureUnavailable,
                session.CurrentStepIndex,
                NCSurgeryStepResolutionStatus.Paused);
        }

        if (session.CurrentStepIndex < 0 || session.CurrentStepIndex >= procedure.Steps.Count)
        {
            PauseInternal(sessionUid, session, NCSurgeryInterruptionReason.ConditionsChanged);
            return Rejected(
                request.ActionResolutionId,
                NCSurgeryStepFailure.StepUnavailable,
                session.CurrentStepIndex,
                NCSurgeryStepResolutionStatus.Paused);
        }

        var stepId = procedure.Steps[session.CurrentStepIndex];
        if (!_prototypes.TryIndex(stepId, out var step))
        {
            PauseInternal(sessionUid, session, NCSurgeryInterruptionReason.ConditionsChanged);
            return Rejected(
                request.ActionResolutionId,
                NCSurgeryStepFailure.StepUnavailable,
                session.CurrentStepIndex,
                NCSurgeryStepResolutionStatus.Paused,
                stepId);
        }

        if (!TryComp<NCSurgicalSiteComponent>(session.TargetRegion, out var site) ||
            site.ActiveProcedure != session.ResolutionId.Value ||
            !AccessMatches(site.AccessDepth, step.RequiredAccessDepth))
        {
            PauseInternal(sessionUid, session, NCSurgeryInterruptionReason.ConditionsChanged);
            return Rejected(
                request.ActionResolutionId,
                NCSurgeryStepFailure.AccessChanged,
                session.CurrentStepIndex,
                NCSurgeryStepResolutionStatus.Paused,
                stepId,
                step.Duration);
        }

        if (Deleted(session.Patient) ||
            Deleted(session.TargetRegion) ||
            !RegionBelongsToPatient(session.Patient, session.TargetRegion) ||
            !ValidateTarget(
                session.Patient,
                session.TargetRegion,
                session.TargetWoundId,
                procedure,
                validateInitialPathology: session.CurrentStepIndex == 0))
        {
            PauseInternal(sessionUid, session, NCSurgeryInterruptionReason.PatientMoved);
            return Rejected(
                request.ActionResolutionId,
                NCSurgeryStepFailure.TargetChanged,
                session.CurrentStepIndex,
                NCSurgeryStepResolutionStatus.Paused,
                stepId,
                step.Duration);
        }


        if (!ValidateGraft(session.Patient, session.TargetRegion, session.Graft, procedure))
        {
            PauseInternal(sessionUid, session, NCSurgeryInterruptionReason.ConditionsChanged);
            return Rejected(
                request.ActionResolutionId,
                NCSurgeryStepFailure.GraftChanged,
                session.CurrentStepIndex,
                NCSurgeryStepResolutionStatus.Paused,
                stepId,
                step.Duration);
        }

        var toolFailure = ResolveTool(request.Tool, step, out var tool, out var toolRequirement, out var toolQuality);
        if (toolFailure != NCSurgeryStepFailure.None)
        {
            return Rejected(
                request.ActionResolutionId,
                toolFailure,
                session.CurrentStepIndex,
                NCSurgeryStepResolutionStatus.Rejected,
                stepId,
                step.Duration);
        }

        var conditions = BuildConditionSnapshot(session, site, tool, toolQuality);
        var conditionFailure = ValidateBoundConditions(
            session.Patient,
            session.Facility,
            session.Environment,
            procedure.Facility,
            procedure.Environment,
            procedure.Patient);
        if (conditionFailure == NCSurgeryConditionFailure.None)
        {
            conditionFailure = ValidateBoundConditions(
                session.Patient,
                session.Facility,
                session.Environment,
                step.Facility,
                step.Environment,
                step.Patient);
        }

        if (conditionFailure != NCSurgeryConditionFailure.None)
        {
            PauseInternal(sessionUid, session, MapPauseReason(conditionFailure));
            return Rejected(
                request.ActionResolutionId,
                MapStepFailure(conditionFailure),
                session.CurrentStepIndex,
                NCSurgeryStepResolutionStatus.Paused,
                stepId,
                step.Duration,
                conditions);
        }

        var outcome = ResolveOutcome(step, conditions);
        // A compromised result is still a completed deterministic transition. Its effects are applied
        // after the ordinary transition effects, so YAML only declares the additional complication.
        var effects = step.SuccessEffects;
        var complicationEffects = outcome == NCSurgeryStepOutcome.Compromised
            ? step.CompromisedEffects
            : [];
        var isFinalStep = session.CurrentStepIndex == procedure.Steps.Count - 1;

        if (!CanApplyEffects(session, effects) ||
            !CanApplyEffects(session, complicationEffects) ||
            isFinalStep && !CanApplyEffects(session, procedure.CompletionEffects))
        {
            PauseInternal(sessionUid, session, NCSurgeryInterruptionReason.ConditionsChanged);
            return Rejected(
                request.ActionResolutionId,
                NCSurgeryStepFailure.EffectContractInvalid,
                session.CurrentStepIndex,
                NCSurgeryStepResolutionStatus.Paused,
                stepId,
                step.Duration,
                conditions);
        }

        if (!ApplyEffects(session, effects) ||
            !ApplyEffects(session, complicationEffects) ||
            isFinalStep && !ApplyEffects(session, procedure.CompletionEffects))
        {
            PauseInternal(sessionUid, session, NCSurgeryInterruptionReason.ConditionsChanged);
            return Rejected(
                request.ActionResolutionId,
                NCSurgeryStepFailure.EffectContractInvalid,
                session.CurrentStepIndex,
                NCSurgeryStepResolutionStatus.Paused,
                stepId,
                step.Duration,
                conditions);
        }

        // Physical consumables are charged only after every requested effect was actually applied.
        if (tool != null && toolRequirement != null && toolRequirement.ConsumedUses > 0 && tool.UsesRemaining >= 0)
            tool.UsesRemaining -= toolRequirement.ConsumedUses;
        MarkMedicalDirty(session.Patient);

        var resolvedIndex = session.CurrentStepIndex;
        session.History.Add(new NCSurgeryStepAuditRecord
        {
            ActionResolutionId = request.ActionResolutionId,
            Step = stepId,
            StepIndex = resolvedIndex,
            Outcome = outcome,
            ResolvedAt = _timing.CurTime,
            Conditions = conditions,
        });
        session.CurrentStepIndex++;
        session.LastTransitionAt = _timing.CurTime;

        var stepResolved = new NCSurgeryStepResolvedEvent(
            session.ResolutionId,
            request.ActionResolutionId,
            stepId,
            resolvedIndex,
            outcome);
        RaiseLocalEvent(session.Patient, ref stepResolved);

        var changed = new NCSurgeryMedicalStateChangedEvent(session.ResolutionId, session.TargetRegion);
        RaiseLocalEvent(session.Patient, ref changed);

        var status = NCSurgeryStepResolutionStatus.Resolved;
        if (isFinalStep)
        {
            session.State = NCSurgerySessionState.Completed;
            session.TerminalResolutionId = request.ActionResolutionId;
            ReleaseLocks(sessionUid, session);
            status = NCSurgeryStepResolutionStatus.Completed;

            var completed = new NCSurgeryCompletedEvent(session.ResolutionId, session.Procedure);
            RaiseLocalEvent(session.Patient, ref completed);
        }

        return new NCSurgeryStepResolution(
            request.ActionResolutionId,
            status,
            NCSurgeryStepFailure.None,
            stepId,
            resolvedIndex,
            outcome,
            step.Duration,
            conditions);
    }

    /// <summary>
    /// Pauses a recoverable session without undoing incision, access depth, bleeding or contamination.
    /// </summary>
    public bool Pause(EntityUid sessionUid, NCSurgeryInterruptionReason reason)
    {
        if (!TryComp<NCSurgerySessionComponent>(sessionUid, out var session) || !IsActive(session.State))
            return false;

        PauseInternal(sessionUid, session, reason);
        return true;
    }

    /// <summary>
    /// Resumes a paused session after validating current patient, facility and environment state.
    /// Another authorized surgeon may take over only when the previous surgeon entity no longer exists.
    /// </summary>
    public bool TryResume(
        EntityUid sessionUid,
        EntityUid surgeon,
        out NCSurgeryStartFailure failure)
    {
        failure = NCSurgeryStartFailure.None;
        if (Deleted(surgeon) ||
            !TryComp<NCSurgerySessionComponent>(sessionUid, out var session) ||
            session.State != NCSurgerySessionState.Paused ||
            !_prototypes.TryIndex(session.Procedure, out var procedure))
        {
            failure = NCSurgeryStartFailure.EntityUnavailable;
            return false;
        }

        if (surgeon != session.Surgeon && !Deleted(session.Surgeon))
        {
            failure = NCSurgeryStartFailure.SurgeonBusy;
            return false;
        }

        if (!ValidateSurgeonAuthorization(surgeon, procedure))
        {
            failure = NCSurgeryStartFailure.AuthorizationMissing;
            return false;
        }

        if (TryComp<NCSurgeryPractitionerComponent>(surgeon, out var incoming) &&
            incoming.ActiveSession is { } active &&
            active != sessionUid &&
            SessionIsLive(active))
        {
            failure = NCSurgeryStartFailure.SurgeonBusy;
            return false;
        }

        var conditionFailure = ValidateBoundConditions(
            session.Patient,
            session.Facility,
            session.Environment,
            procedure.Facility,
            procedure.Environment,
            procedure.Patient);
        if (conditionFailure != NCSurgeryConditionFailure.None)
        {
            failure = MapStartFailure(conditionFailure);
            return false;
        }

        if (surgeon != session.Surgeon)
            ReleasePractitionerLock(session.Surgeon, sessionUid);

        session.Surgeon = surgeon;
        session.State = NCSurgerySessionState.Running;
        session.PauseReason = NCSurgeryInterruptionReason.None;
        session.LastTransitionAt = _timing.CurTime;
        EnsureComp<NCSurgeryPractitionerComponent>(surgeon).ActiveSession = sessionUid;
        return true;
    }

    /// <summary>
    /// Terminates a session exactly once. Abort effects are deterministic; physical access state is never
    /// rolled back implicitly.
    /// </summary>
    public bool Abort(
        EntityUid sessionUid,
        EntityUid actor,
        NCActionResolutionId actionResolutionId,
        NCSurgeryAbortReason reason,
        out NCSurgeryAbortFailure failure)
    {
        return AbortInternal(sessionUid, actor, actionResolutionId, reason, false, out failure);
    }

    private bool AbortInternal(
        EntityUid sessionUid,
        EntityUid actor,
        NCActionResolutionId actionResolutionId,
        NCSurgeryAbortReason reason,
        bool force,
        out NCSurgeryAbortFailure failure)
    {
        failure = NCSurgeryAbortFailure.None;
        if (!actionResolutionId.IsValid)
        {
            failure = NCSurgeryAbortFailure.InvalidResolutionId;
            return false;
        }

        if (!TryComp<NCSurgerySessionComponent>(sessionUid, out var session))
        {
            failure = NCSurgeryAbortFailure.SessionUnavailable;
            return false;
        }

        if (!IsActive(session.State))
        {
            if (session.TerminalResolutionId == actionResolutionId)
                return true;

            failure = NCSurgeryAbortFailure.AlreadyTerminal;
            return false;
        }

        if (!force && actor != session.Surgeon)
        {
            failure = NCSurgeryAbortFailure.WrongSurgeon;
            return false;
        }

        var mutated = false;
        if (_prototypes.TryIndex(session.Procedure, out var procedure) &&
            CanApplyEffects(session, procedure.AbortEffects))
        {
            mutated = procedure.AbortEffects.Count > 0 && ApplyEffects(session, procedure.AbortEffects);
            if (procedure.AbortEffects.Count > 0 && !mutated)
                Logger.Error($"Failed to apply validated abort effects for surgery {session.ResolutionId}.");
            if (mutated)
                MarkMedicalDirty(session.Patient);
        }

        session.State = NCSurgerySessionState.Aborted;
        session.TerminalResolutionId = actionResolutionId;
        session.LastTransitionAt = _timing.CurTime;
        ReleaseLocks(sessionUid, session);

        if (!Deleted(session.Patient))
        {
            var aborted = new NCSurgeryAbortedEvent(
                session.ResolutionId,
                actionResolutionId,
                reason);
            RaiseLocalEvent(session.Patient, ref aborted);

            if (mutated)
            {
                var changed = new NCSurgeryMedicalStateChangedEvent(session.ResolutionId, session.TargetRegion);
                RaiseLocalEvent(session.Patient, ref changed);
            }
        }

        return true;
    }

    private void OnPractitionerTerminating(
        Entity<NCSurgeryPractitionerComponent> entity,
        ref EntityTerminatingEvent args)
    {
        if (entity.Comp.ActiveSession is not { } sessionUid ||
            !TryComp<NCSurgerySessionComponent>(sessionUid, out var session) ||
            session.Surgeon != entity.Owner)
        {
            return;
        }

        PauseInternal(sessionUid, session, NCSurgeryInterruptionReason.SurgeonUnavailable);
    }

    private void OnSessionTerminating(Entity<NCSurgerySessionComponent> entity, ref EntityTerminatingEvent args)
    {
        ReleasePractitionerLock(entity.Comp.Surgeon, entity.Owner);
    }

    private void PauseInternal(
        EntityUid sessionUid,
        NCSurgerySessionComponent session,
        NCSurgeryInterruptionReason reason)
    {
        if (!IsActive(session.State))
            return;

        session.State = NCSurgerySessionState.Paused;
        session.PauseReason = reason;
        session.LastTransitionAt = _timing.CurTime;

        if (Deleted(session.Patient))
            return;

        var paused = new NCSurgeryPausedEvent(session.ResolutionId, reason);
        RaiseLocalEvent(session.Patient, ref paused);
    }

    private NCSurgeryStepFailure ResolveTool(
        EntityUid? toolUid,
        NCSurgeryStepPrototype step,
        out NCSurgeryToolComponent? tool,
        out NCSurgeryToolRequirement? matched,
        out float quality)
    {
        tool = null;
        matched = null;
        quality = 0f;
        if (step.Tools.Count == 0)
            return NCSurgeryStepFailure.None;

        if (toolUid is not { } uid || Deleted(uid) || !TryComp(uid, out tool) || tool.Broken)
            return NCSurgeryStepFailure.ToolUnavailable;

        var sawCapability = false;
        var sawQuality = false;
        var sawSterility = false;
        foreach (var candidate in step.Tools)
        {
            if (!tool.Capabilities.TryGetValue(candidate.Capability, out var candidateQuality))
                continue;

            sawCapability = true;
            if (!float.IsFinite(candidateQuality) || candidateQuality < candidate.MinimumQuality)
                continue;

            sawQuality = true;
            if (!float.IsFinite(tool.Sterility) || tool.Sterility < candidate.MinimumSterility)
                continue;

            sawSterility = true;
            if (candidate.ConsumedUses > 0 && tool.UsesRemaining >= 0 &&
                tool.UsesRemaining < candidate.ConsumedUses)
            {
                continue;
            }

            matched = candidate;
            quality = candidateQuality;
            return NCSurgeryStepFailure.None;
        }

        if (!sawCapability)
            return NCSurgeryStepFailure.ToolCapabilityMissing;
        if (!sawQuality)
            return NCSurgeryStepFailure.ToolQualityInsufficient;
        if (!sawSterility)
            return NCSurgeryStepFailure.ToolSterilityInsufficient;
        return NCSurgeryStepFailure.ToolExhausted;
    }

    private NCSurgeryConditionSnapshot BuildConditionSnapshot(
        NCSurgerySessionComponent session,
        NCSurgicalSiteComponent site,
        NCSurgeryToolComponent? tool,
        float toolQuality)
    {
        var result = new NCSurgeryConditionSnapshot
        {
            ToolQuality = toolQuality,
            ToolSterility = tool?.Sterility ?? 0f,
            SiteContamination = site.Contamination,
        };

        if (session.Facility is { } facilityUid &&
            TryComp<NCSurgeryFacilityComponent>(facilityUid, out var facility))
        {
            result.FacilityQuality = facility.Quality;
            result.FacilitySterility = facility.Sterility;
            result.FacilityPowered = facility.Powered;
            result.FacilityStable = facility.StableSurface;
        }

        if (session.Environment is { } environmentUid &&
            TryComp<NCSurgeryEnvironmentComponent>(environmentUid, out var environment))
        {
            result.EnvironmentSterility = environment.Sterility;
            result.EnvironmentLighting = environment.Lighting;
            result.EnvironmentMotion = environment.Motion;
            result.EnvironmentContamination = environment.Contamination;
        }

        if (TryComp<NCPhysiologyComponent>(session.Patient, out var physiology))
        {
            result.PatientOxygenation = physiology.Oxygenation;
            result.PatientShock = physiology.ShockStage;
            result.PatientConsciousness = physiology.Consciousness;
            result.PatientCondition = physiology.Condition;
        }

        // Drug state is recomputed by the authoritative pharmacology system. A table or client message can
        // never manufacture anesthesia, sedation or analgesia for a surgical transition.
        var pharmacology = _pharmacology.GetModifiers(session.Patient);
        result.PatientAnalgesia = pharmacology.Analgesia;
        result.PatientSedation = pharmacology.Sedation;
        result.PatientAnesthesia = pharmacology.Anesthesia;

        if (TryComp<NCSurgeryPatientStateComponent>(session.Patient, out var patient))
        {
            result.PatientMovement = patient.Movement;
            result.PatientRestrained = patient.Restrained;
            result.AirwayMonitored = patient.AirwayMonitored;
        }

        return result;
    }

    private NCSurgeryConditionFailure ValidateBoundConditions(
        EntityUid patientUid,
        EntityUid? facilityUid,
        EntityUid? environmentUid,
        NCSurgeryFacilityRequirement facilityRequirement,
        NCSurgeryEnvironmentRequirement environmentRequirement,
        NCSurgeryPatientRequirement patientRequirement)
    {
        var facilityFailure = ValidateFacility(patientUid, facilityUid, facilityRequirement);
        if (facilityFailure != NCSurgeryConditionFailure.None)
            return facilityFailure;

        var environmentFailure = ValidateEnvironment(patientUid, environmentUid, environmentRequirement);
        if (environmentFailure != NCSurgeryConditionFailure.None)
            return environmentFailure;

        return ValidatePatient(patientUid, patientRequirement);
    }

    private NCSurgeryConditionFailure ValidateFacility(
        EntityUid patientUid,
        EntityUid? facilityUid,
        NCSurgeryFacilityRequirement requirement)
    {
        var hasRequirements = requirement.Required ||
                              requirement.Capabilities.Count > 0 ||
                              requirement.MinimumQuality > 0f ||
                              requirement.MinimumSterility > 0f ||
                              requirement.RequirePower ||
                              requirement.RequireStableSurface;
        if (!hasRequirements && facilityUid == null)
            return NCSurgeryConditionFailure.None;

        if (facilityUid is not { } uid ||
            Deleted(uid) ||
            !TryComp<NCSurgeryFacilityComponent>(uid, out var facility))
        {
            return NCSurgeryConditionFailure.FacilityUnavailable;
        }

        if (requirement.Required &&
            (!TryComp<NCSurgeryPatientStateComponent>(patientUid, out var patient) ||
             patient.CurrentFacility != uid))
        {
            return NCSurgeryConditionFailure.PatientMoved;
        }

        foreach (var capability in requirement.Capabilities)
        {
            if (!facility.Capabilities.Contains(capability))
                return NCSurgeryConditionFailure.FacilityCapabilityMissing;
        }

        if (!float.IsFinite(facility.Quality) ||
            !float.IsFinite(facility.Sterility) ||
            facility.Quality < requirement.MinimumQuality ||
            facility.Sterility < requirement.MinimumSterility ||
            requirement.RequirePower && !facility.Powered ||
            requirement.RequireStableSurface && !facility.StableSurface)
        {
            return NCSurgeryConditionFailure.FacilityUnavailable;
        }

        return NCSurgeryConditionFailure.None;
    }

    private NCSurgeryConditionFailure ValidateEnvironment(
        EntityUid patientUid,
        EntityUid? environmentUid,
        NCSurgeryEnvironmentRequirement requirement)
    {
        var hasRequirements = requirement.Required ||
                              requirement.MinimumSterility > 0f ||
                              requirement.MinimumLighting > 0f ||
                              requirement.MaximumMotion < 1f ||
                              requirement.MaximumContamination < 1f;
        if (!hasRequirements && environmentUid == null)
            return NCSurgeryConditionFailure.None;

        if (environmentUid is not { } uid ||
            Deleted(uid) ||
            !TryComp<NCSurgeryEnvironmentComponent>(uid, out var environment))
        {
            return NCSurgeryConditionFailure.EnvironmentUnavailable;
        }

        if (requirement.Required &&
            (!TryComp<NCSurgeryPatientStateComponent>(patientUid, out var patient) ||
             patient.CurrentEnvironment != uid))
        {
            return NCSurgeryConditionFailure.PatientMoved;
        }

        if (!float.IsFinite(environment.Sterility) ||
            !float.IsFinite(environment.Lighting) ||
            !float.IsFinite(environment.Motion) ||
            !float.IsFinite(environment.Contamination) ||
            environment.Sterility < requirement.MinimumSterility ||
            environment.Lighting < requirement.MinimumLighting ||
            environment.Motion > requirement.MaximumMotion ||
            environment.Contamination > requirement.MaximumContamination)
        {
            return NCSurgeryConditionFailure.EnvironmentUnsafe;
        }

        return NCSurgeryConditionFailure.None;
    }

    private NCSurgeryConditionFailure ValidatePatient(
        EntityUid patientUid,
        NCSurgeryPatientRequirement requirement)
    {
        var oxygenation = 1f;
        var shock = NCShockStage.None;
        var consciousness = NCConsciousnessState.Alert;
        var condition = NCPhysiologicalCondition.Stable;
        if (TryComp<NCPhysiologyComponent>(patientUid, out var physiology))
        {
            oxygenation = physiology.Oxygenation;
            shock = physiology.ShockStage;
            consciousness = physiology.Consciousness;
            condition = physiology.Condition;
        }

        var pharmacology = _pharmacology.GetModifiers(patientUid);
        var analgesia = pharmacology.Analgesia;
        var sedation = pharmacology.Sedation;
        var anesthesia = pharmacology.Anesthesia;
        var movement = 0f;
        var restrained = false;
        var airwayMonitored = false;
        if (TryComp<NCSurgeryPatientStateComponent>(patientUid, out var patient))
        {
            movement = patient.Movement;
            restrained = patient.Restrained;
            airwayMonitored = patient.AirwayMonitored;
        }

        if (!float.IsFinite(oxygenation) ||
            !float.IsFinite(analgesia) ||
            !float.IsFinite(sedation) ||
            !float.IsFinite(anesthesia) ||
            !float.IsFinite(movement) ||
            oxygenation < requirement.MinimumOxygenation ||
            shock > requirement.MaximumShockStage ||
            requirement.AllowedConsciousness.Count > 0 &&
            !requirement.AllowedConsciousness.Contains(consciousness) ||
            requirement.AllowedConditions.Count > 0 &&
            !requirement.AllowedConditions.Contains(condition) ||
            analgesia < requirement.MinimumAnalgesia ||
            sedation < requirement.MinimumSedation ||
            anesthesia < requirement.MinimumAnesthesia ||
            movement > requirement.MaximumMovement ||
            requirement.RequireRestraintWhenConscious &&
            consciousness < NCConsciousnessState.Unconscious &&
            !restrained ||
            requirement.RequireAirwayMonitoring && !airwayMonitored)
        {
            return NCSurgeryConditionFailure.PatientUnstable;
        }

        return NCSurgeryConditionFailure.None;
    }

    private static NCSurgeryStepOutcome ResolveOutcome(
        NCSurgeryStepPrototype step,
        NCSurgeryConditionSnapshot snapshot)
    {
        foreach (var rule in step.OutcomeRules)
        {
            if (RuleMatches(rule, snapshot))
                return rule.Outcome;
        }

        return step.DefaultOutcome;
    }

    private static bool RuleMatches(NCSurgeryOutcomeRule rule, NCSurgeryConditionSnapshot value)
    {
        return (rule.MaximumToolQuality == null || value.ToolQuality <= rule.MaximumToolQuality) &&
               (rule.MaximumToolSterility == null || value.ToolSterility <= rule.MaximumToolSterility) &&
               (rule.MaximumFacilityQuality == null || value.FacilityQuality <= rule.MaximumFacilityQuality) &&
               (rule.MaximumFacilitySterility == null || value.FacilitySterility <= rule.MaximumFacilitySterility) &&
               (rule.MaximumEnvironmentSterility == null || value.EnvironmentSterility <= rule.MaximumEnvironmentSterility) &&
               (rule.MinimumEnvironmentContamination == null || value.EnvironmentContamination >= rule.MinimumEnvironmentContamination) &&
               (rule.MinimumPatientMovement == null || value.PatientMovement >= rule.MinimumPatientMovement) &&
               (rule.MinimumShockStage == null || value.PatientShock >= rule.MinimumShockStage) &&
               (rule.MinimumSiteContamination == null || value.SiteContamination >= rule.MinimumSiteContamination);
    }

    private bool TrySelectTargetWound(
        EntityUid patient,
        EntityUid region,
        NCSurgeryProcedurePrototype procedure,
        out Guid? woundId)
    {
        woundId = null;
        if (!procedure.RequiresTargetWound)
            return true;
        if (!TryGetRegionCategory(region, out var category) ||
            !TryComp<NCWoundContainerComponent>(patient, out var wounds))
        {
            return false;
        }

        var selected = wounds.Wounds.Values
            .Where(wound => wound.RegionCategory == category &&
                            (procedure.CompatibleWoundTypes.Count == 0 ||
                             procedure.CompatibleWoundTypes.Contains(wound.WoundType)) &&
                            ValidateTargetState(patient, region, wound, procedure.TargetState))
            .OrderByDescending(wound => wound.Severity)
            .ThenByDescending(wound => wound.IntegrityDamage)
            .ThenBy(wound => wound.CreatedAt)
            .ThenBy(wound => wound.WoundId)
            .FirstOrDefault();
        if (selected == null)
            return false;

        woundId = selected.WoundId;
        return true;
    }

    private bool ValidateTarget(
        EntityUid patientUid,
        EntityUid regionUid,
        Guid? woundId,
        NCSurgeryProcedurePrototype procedure,
        bool validateInitialPathology = true)
    {
        if (!TryGetRegionCategory(regionUid, out var targetCategory) ||
            procedure.CompatibleTargetCategories.Count > 0 &&
            !procedure.CompatibleTargetCategories.Contains(targetCategory))
        {
            return false;
        }

        var requirements = procedure.TargetRequirements;
        if (requirements.HasFlag(NCSurgeryTargetRequirements.Integrity) &&
            !HasComp<NCOrganIntegrityComponent>(regionUid) ||
            requirements.HasFlag(NCSurgeryTargetRequirements.Function) &&
            !HasComp<NCOrganFunctionComponent>(regionUid) ||
            requirements.HasFlag(NCSurgeryTargetRequirements.Bone) &&
            !HasComp<NCBoneComponent>(regionUid) ||
            requirements.HasFlag(NCSurgeryTargetRequirements.Tendon) &&
            !HasComp<NCTendonComponent>(regionUid) ||
            requirements.HasFlag(NCSurgeryTargetRequirements.Nerve) &&
            !HasComp<NCNerveComponent>(regionUid) ||
            requirements.HasFlag(NCSurgeryTargetRequirements.Vascular) &&
            !HasComp<NCVascularRegionComponent>(regionUid))
        {
            return false;
        }

        if (TryComp<NCOrganIntegrityComponent>(regionUid, out var integrity) &&
            !procedure.CompatibleMaterials.Contains(integrity.Material))
        {
            return false;
        }

        NCWoundRecord? wound = null;
        if (procedure.RequiresTargetWound &&
            (woundId == null ||
             !TryComp<NCWoundContainerComponent>(patientUid, out var wounds) ||
             !wounds.Wounds.TryGetValue(woundId.Value, out wound) ||
             !TryGetRegionCategory(regionUid, out var regionCategory) ||
             wound.RegionCategory != regionCategory))
        {
            return false;
        }

        if (wound != null &&
            procedure.CompatibleWoundTypes.Count > 0 &&
            !procedure.CompatibleWoundTypes.Contains(wound.WoundType))
        {
            return false;
        }

        return !validateInitialPathology ||
               ValidateTargetState(patientUid, regionUid, wound, procedure.TargetState);
    }

    private bool ValidateTargetState(
        EntityUid patient,
        EntityUid region,
        NCWoundRecord? wound,
        NCSurgeryTargetStateRequirement requirement)
    {
        if (requirement.MaximumIntegrityRatio is { } maxIntegrity)
        {
            if (!float.IsFinite(maxIntegrity) ||
                !TryComp<NCOrganIntegrityComponent>(region, out var integrity) ||
                integrity.MaxIntegrity <= 0f ||
                integrity.CurrentIntegrity / integrity.MaxIntegrity > maxIntegrity)
            {
                return false;
            }
        }

        if (requirement.MaximumFunctionRatio is { } maxFunction)
        {
            if (!float.IsFinite(maxFunction) ||
                !TryComp<NCOrganFunctionComponent>(region, out var function) ||
                function.BaseCapacity <= 0f ||
                function.CurrentCapacity / function.BaseCapacity > maxFunction)
            {
                return false;
            }
        }

        if (requirement.AllowedBoneStates.Count > 0 &&
            (!TryComp<NCBoneComponent>(region, out var bone) ||
             !requirement.AllowedBoneStates.Contains(bone.State)))
        {
            return false;
        }

        if (requirement.MaximumTendonIntegrity is { } maxTendon &&
            (!float.IsFinite(maxTendon) ||
             !TryComp<NCTendonComponent>(region, out var tendon) ||
             tendon.Integrity > maxTendon))
        {
            return false;
        }

        if (requirement.MaximumNerveIntegrity is { } maxNerve &&
            (!float.IsFinite(maxNerve) ||
             !TryComp<NCNerveComponent>(region, out var nerve) ||
             nerve.NeuralIntegrity > maxNerve))
        {
            return false;
        }

        if (requirement.RequireActiveBleeding &&
            (wound == null ||
             !TryComp<NCBleedingComponent>(patient, out var bleeding) ||
             bleeding.Sources.All(source => source.WoundId != wound.WoundId || source.CurrentRate <= 0f)))
        {
            return false;
        }

        if (requirement.RequireEmbeddedObject && wound?.EmbeddedObjectId == null)
            return false;

        if (requirement.MinimumWoundContamination is { } minimumContamination &&
            (!float.IsFinite(minimumContamination) || wound == null ||
             wound.Contamination < minimumContamination))
        {
            return false;
        }

        if (requirement.MinimumCavityBloodVolume is { } minimumCavityBlood &&
            (!float.IsFinite(minimumCavityBlood) ||
             !TryComp<NCCavityFluidComponent>(region, out var cavity) ||
             cavity.BloodVolume < minimumCavityBlood))
        {
            return false;
        }

        return true;
    }

    private bool ValidateGraft(
        EntityUid patient,
        EntityUid targetRegion,
        EntityUid? graftUid,
        NCSurgeryProcedurePrototype procedure)
    {
        if (!procedure.RequiresGraft)
            return graftUid == null;

        if (graftUid is not { } graft ||
            Deleted(graft) ||
            !TryComp<OrganComponent>(graft, out var organ) ||
            organ.Body != null ||
            organ.Category is not { } graftCategory ||
            procedure.CompatibleGraftCategories.Count > 0 &&
            !procedure.CompatibleGraftCategories.Contains(graftCategory) ||
            !TryComp<NCOrganIntegrityComponent>(graft, out var integrity) ||
            !procedure.CompatibleGraftMaterials.Contains(integrity.Material) ||
            !TryComp<NCDetachedOrganMedicalDataComponent>(graft, out var payload) ||
            !TryComp<NCMedicalBodyComponent>(patient, out var medical) ||
            payload.SourceBodyProfile != medical.Profile ||
            !TryGetRegionCategory(targetRegion, out var parentCategory))
        {
            return false;
        }

        return _detachment.CanAttachOrgan(patient, parentCategory, graft);
    }

    private bool CanApplyEffects(
        NCSurgerySessionComponent session,
        IReadOnlyList<NCSurgeryEffectDefinition> effects)
    {
        foreach (var effect in effects)
        {
            if (!float.IsFinite(effect.Amount) ||
                !TryResolveEffectTarget(session, effect.Target, out var target))
                return false;

            switch (effect.Kind)
            {
                case NCSurgeryEffectKind.SetAccessDepth:
                case NCSurgeryEffectKind.AddSiteContamination:
                case NCSurgeryEffectKind.SetBleedingControlled:
                case NCSurgeryEffectKind.SetTissuesRetracted:
                case NCSurgeryEffectKind.SetBoneCasingOpen:
                    if (!HasComp<NCSurgicalSiteComponent>(target))
                        return false;
                    break;
                case NCSurgeryEffectKind.AdjustIntegrity:
                    if (!HasComp<NCOrganIntegrityComponent>(target))
                        return false;
                    break;
                case NCSurgeryEffectKind.AdjustOrganFunction:
                    if (!HasComp<NCOrganFunctionComponent>(target))
                        return false;
                    break;
                case NCSurgeryEffectKind.SetBoneState:
                case NCSurgeryEffectKind.SetBoneStability:
                    if (!HasComp<NCBoneComponent>(target))
                        return false;
                    break;
                case NCSurgeryEffectKind.AdjustTendonIntegrity:
                    if (!HasComp<NCTendonComponent>(target))
                        return false;
                    break;
                case NCSurgeryEffectKind.AdjustNerveIntegrity:
                    if (!HasComp<NCNerveComponent>(target))
                        return false;
                    break;
                case NCSurgeryEffectKind.AddWoundTreatmentFlags:
                case NCSurgeryEffectKind.SetWoundClosure:
                case NCSurgeryEffectKind.AdjustWoundContamination:
                case NCSurgeryEffectKind.RemoveEmbeddedObject:
                    if (effect.Target != NCSurgeryEffectTarget.TargetRegion ||
                        !TryCollectWounds(session, effect.WoundScope, out var records) || records.Count == 0)
                        return false;
                    break;
                case NCSurgeryEffectKind.ControlBleeding:
                    if (effect.Target != NCSurgeryEffectTarget.TargetRegion ||
                        !TryCollectBleedingSources(session, effect.WoundScope, out var sources) ||
                        sources.Count == 0)
                        return false;
                    break;
                case NCSurgeryEffectKind.AdjustCavityBloodVolume:
                case NCSurgeryEffectKind.SetCavityDrainRate:
                    if (!HasComp<NCCavityFluidComponent>(target))
                        return false;
                    break;
                case NCSurgeryEffectKind.DetachTargetOrgan:
                    if (effect.Target != NCSurgeryEffectTarget.TargetRegion ||
                        !TryGetRegionCategory(target, out var detachCategory) ||
                        !_detachment.CanDetachOrgan(session.Patient, detachCategory, out var detachable) ||
                        detachable != target)
                        return false;
                    break;
                case NCSurgeryEffectKind.AttachGraft:
                    if (effect.Target != NCSurgeryEffectTarget.Graft ||
                        session.Graft != target ||
                        !TryGetRegionCategory(session.TargetRegion, out var parentCategory) ||
                        !_detachment.CanAttachOrgan(session.Patient, parentCategory, target))
                        return false;
                    break;
                case NCSurgeryEffectKind.RecordAutopsy:
                    if (effect.Target != NCSurgeryEffectTarget.TargetRegion ||
                        !TryGetRegionCategory(target, out _))
                        return false;
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Resolves an effect target from the unsaved surgery session without storing entity identifiers in prototypes.
    /// </summary>
    private bool TryResolveEffectTarget(
        NCSurgerySessionComponent session,
        NCSurgeryEffectTarget effectTarget,
        out EntityUid target)
    {
        switch (effectTarget)
        {
            case NCSurgeryEffectTarget.TargetRegion:
                target = session.TargetRegion;
                return !Deleted(target);
            case NCSurgeryEffectTarget.Graft when session.Graft is { } graft:
                target = graft;
                return !Deleted(target);
            default:
                target = default;
                return false;
        }
    }

    private bool ApplyEffects(
        NCSurgerySessionComponent session,
        IReadOnlyList<NCSurgeryEffectDefinition> effects)
    {
        foreach (var effect in effects)
        {
            if (!TryResolveEffectTarget(session, effect.Target, out var target))
                return false;

            switch (effect.Kind)
            {
                case NCSurgeryEffectKind.SetAccessDepth:
                    Comp<NCSurgicalSiteComponent>(target).AccessDepth = effect.AccessDepth;
                    break;
                case NCSurgeryEffectKind.AddSiteContamination:
                {
                    var site = Comp<NCSurgicalSiteComponent>(target);
                    site.Contamination = Math.Clamp(site.Contamination + effect.Amount, 0f, 1f);
                    break;
                }
                case NCSurgeryEffectKind.SetBleedingControlled:
                    Comp<NCSurgicalSiteComponent>(target).BleedingControlled = effect.Enabled;
                    break;
                case NCSurgeryEffectKind.SetTissuesRetracted:
                    Comp<NCSurgicalSiteComponent>(target).TissuesRetracted = effect.Enabled;
                    break;
                case NCSurgeryEffectKind.SetBoneCasingOpen:
                    Comp<NCSurgicalSiteComponent>(target).BoneCasingOpen = effect.Enabled;
                    break;
                case NCSurgeryEffectKind.AdjustIntegrity:
                {
                    var integrity = Comp<NCOrganIntegrityComponent>(target);
                    integrity.CurrentIntegrity = Math.Clamp(
                        integrity.CurrentIntegrity + effect.Amount,
                        0f,
                        MathF.Max(0f, integrity.MaxIntegrity));
                    integrity.FunctionalEfficiency = integrity.MaxIntegrity > 0f
                        ? Math.Clamp(integrity.CurrentIntegrity / integrity.MaxIntegrity, 0f, 1f)
                        : 0f;
                    break;
                }
                case NCSurgeryEffectKind.AdjustOrganFunction:
                {
                    var function = Comp<NCOrganFunctionComponent>(target);
                    function.CurrentCapacity = Math.Clamp(
                        function.CurrentCapacity + effect.Amount,
                        0f,
                        MathF.Max(0f, function.BaseCapacity));
                    break;
                }
                case NCSurgeryEffectKind.SetBoneState:
                {
                    var bone = Comp<NCBoneComponent>(target);
                    bone.State = effect.BoneState;
                    bone.RequiresFixation = effect.BoneState is NCBoneState.BrokenDisplaced or NCBoneState.Shattered;
                    break;
                }
                case NCSurgeryEffectKind.SetBoneStability:
                    Comp<NCBoneComponent>(target).Stability = Math.Clamp(effect.Amount, 0f, 1f);
                    break;
                case NCSurgeryEffectKind.AdjustTendonIntegrity:
                {
                    var tendon = Comp<NCTendonComponent>(target);
                    tendon.Integrity = Math.Clamp(tendon.Integrity + effect.Amount, 0f, 1f);
                    tendon.Ruptured = tendon.Integrity <= 0f;
                    tendon.MovementContribution = tendon.Integrity;
                    break;
                }
                case NCSurgeryEffectKind.AdjustNerveIntegrity:
                {
                    var nerve = Comp<NCNerveComponent>(target);
                    nerve.NeuralIntegrity = Math.Clamp(nerve.NeuralIntegrity + effect.Amount, 0f, 1f);
                    nerve.MotorFunction = nerve.NeuralIntegrity;
                    break;
                }
                case NCSurgeryEffectKind.AddWoundTreatmentFlags:
                    if (!TryCollectWounds(session, effect.WoundScope, out var treated))
                        return false;
                    foreach (var wound in treated)
                    {
                        wound.TreatmentFlags |= effect.TreatmentFlags;
                        wound.Stage = NCWoundStage.Treated;
                        wound.ModifiedAt = _timing.CurTime;
                    }
                    break;
                case NCSurgeryEffectKind.SetWoundClosure:
                    if (!TryCollectWounds(session, effect.WoundScope, out var closed))
                        return false;
                    foreach (var wound in closed)
                    {
                        wound.ClosureState = effect.ClosureState;
                        wound.Stage = effect.ClosureState == NCWoundClosureState.Open
                            ? NCWoundStage.Reopened
                            : NCWoundStage.Closed;
                        wound.ModifiedAt = _timing.CurTime;
                    }
                    break;
                case NCSurgeryEffectKind.AdjustWoundContamination:
                    if (!TryCollectWounds(session, effect.WoundScope, out var cleaned))
                        return false;
                    foreach (var wound in cleaned)
                    {
                        wound.Contamination = Math.Clamp(wound.Contamination + effect.Amount, 0f, 1f);
                        wound.ModifiedAt = _timing.CurTime;
                    }
                    break;
                case NCSurgeryEffectKind.RemoveEmbeddedObject:
                    if (!TryCollectWounds(session, effect.WoundScope, out var extracted))
                        return false;
                    foreach (var wound in extracted)
                    {
                        wound.EmbeddedObjectId = null;
                        wound.ModifiedAt = _timing.CurTime;
                    }
                    break;
                case NCSurgeryEffectKind.ControlBleeding:
                    if (!TryCollectBleedingSources(session, effect.WoundScope, out var sources))
                        return false;
                    foreach (var source in sources)
                    {
                        source.Clamped = effect.Enabled;
                        source.CurrentRate = effect.Enabled ? 0f : source.BaseRate;
                    }

                    RecalculateBleeding(session.Patient);
                    break;
                case NCSurgeryEffectKind.AdjustCavityBloodVolume:
                {
                    var cavity = Comp<NCCavityFluidComponent>(target);
                    cavity.BloodVolume = Math.Clamp(
                        cavity.BloodVolume + effect.Amount,
                        0f,
                        MathF.Max(0f, cavity.MaximumBloodVolume));
                    break;
                }
                case NCSurgeryEffectKind.SetCavityDrainRate:
                {
                    var cavity = Comp<NCCavityFluidComponent>(target);
                    cavity.DrainRate = Math.Clamp(effect.Amount, 0f, MathF.Max(0f, cavity.MaximumBloodVolume));
                    break;
                }
                case NCSurgeryEffectKind.DetachTargetOrgan:
                {
                    if (!TryGetRegionCategory(target, out var category) ||
                        !_detachment.TryDetachOrgan(session.Patient, category, out var detached) ||
                        detached != target)
                    {
                        return false;
                    }

                    session.DetachedResult = detached;
                    break;
                }
                case NCSurgeryEffectKind.AttachGraft:
                {
                    if (effect.Target != NCSurgeryEffectTarget.Graft ||
                        session.Graft != target ||
                        !TryGetRegionCategory(session.TargetRegion, out var parentCategory) ||
                        !_detachment.TryAttachOrgan(session.Patient, parentCategory, target))
                    {
                        return false;
                    }

                    break;
                }
                case NCSurgeryEffectKind.RecordAutopsy:
                    if (!RecordAutopsy(session, target))
                        return false;
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Records a bounded, persistence-safe regional finding from the current authoritative wound set.
    /// </summary>
    private bool RecordAutopsy(NCSurgerySessionComponent session, EntityUid target)
    {
        if (!TryGetRegionCategory(target, out var category))
            return false;

        var record = new NCAutopsyRegionRecord
        {
            ResolutionId = session.ResolutionId,
            Region = category,
            ExaminedAt = _timing.CurTime,
        };

        if (TryComp<NCWoundContainerComponent>(session.Patient, out var wounds))
        {
            foreach (var wound in wounds.Wounds.Values)
            {
                if (wound.RegionCategory != category)
                    continue;

                record.WoundCount++;
                record.TotalIntegrityDamage += wound.IntegrityDamage.Float();
                if (!record.WoundTypes.Contains(wound.WoundType))
                    record.WoundTypes.Add(wound.WoundType);
                if (wound.ForensicResolutionId != Guid.Empty &&
                    !record.ForensicResolutionIds.Contains(wound.ForensicResolutionId))
                {
                    record.ForensicResolutionIds.Add(wound.ForensicResolutionId);
                }
            }
        }

        record.WoundTypes.Sort();
        record.ForensicResolutionIds.Sort();
        EnsureComp<NCAutopsyRecordComponent>(session.Patient).Regions[category] = record;
        return true;
    }

    private bool TryCollectWounds(
        NCSurgerySessionComponent session,
        NCSurgeryWoundScope scope,
        out List<NCWoundRecord> records)
    {
        records = [];
        if (!TryComp<NCWoundContainerComponent>(session.Patient, out var wounds))
            return false;

        if (scope == NCSurgeryWoundScope.TargetWound)
        {
            if (session.TargetWoundId is not { } woundId ||
                !wounds.Wounds.TryGetValue(woundId, out var wound))
            {
                return false;
            }

            records.Add(wound);
            return true;
        }

        if (!TryGetRegionCategory(session.TargetRegion, out var regionCategory))
            return false;

        foreach (var wound in wounds.Wounds.Values)
        {
            if (wound.RegionCategory == regionCategory)
                records.Add(wound);
        }

        return true;
    }

    private bool TryCollectBleedingSources(
        NCSurgerySessionComponent session,
        NCSurgeryWoundScope scope,
        out List<NCBleedingSourceRecord> records)
    {
        records = [];
        if (!TryComp<NCBleedingComponent>(session.Patient, out var bleeding))
            return false;

        if (scope == NCSurgeryWoundScope.TargetWound)
        {
            if (session.TargetWoundId is not { } woundId)
                return false;

            foreach (var source in bleeding.Sources)
            {
                if (source.WoundId == woundId)
                    records.Add(source);
            }

            return true;
        }

        if (!TryGetRegionCategory(session.TargetRegion, out var regionCategory))
            return false;

        foreach (var source in bleeding.Sources)
        {
            if (source.RegionCategory == regionCategory)
                records.Add(source);
        }

        return true;
    }

    private void RecalculateBleeding(EntityUid patientUid)
    {
        if (!TryComp<NCBleedingComponent>(patientUid, out var bleeding))
            return;

        var external = MathF.Max(0f, bleeding.OverflowExternalRate);
        var internalRate = MathF.Max(0f, bleeding.OverflowInternalRate);
        var arterial = 0f;
        foreach (var source in bleeding.Sources)
        {
            var rate = MathF.Max(0f, source.CurrentRate);
            if (source.Channel == NCBleedingChannel.Internal)
            {
                internalRate += rate;
                continue;
            }

            external += rate;
            if (source.Channel == NCBleedingChannel.ExternalArterial)
                arterial += rate;
        }

        bleeding.ActiveExternalRate = external;
        bleeding.ActiveInternalRate = internalRate;
        bleeding.ActiveArterialRate = arterial;
    }

    private void MarkMedicalDirty(EntityUid patientUid)
    {
        if (!TryComp<NCMedicalBodyComponent>(patientUid, out var medical))
            return;

        _medicalBodies.TryMarkDirty(
            (patientUid, medical),
            NCMedicalDirtyDomains.Wounds |
            NCMedicalDirtyDomains.Vascular |
            NCMedicalDirtyDomains.Pain |
            NCMedicalDirtyDomains.OrganFunction |
            NCMedicalDirtyDomains.Consciousness |
            NCMedicalDirtyDomains.Diagnostics |
            NCMedicalDirtyDomains.Persistence);
    }

    private bool RegionBelongsToPatient(EntityUid patientUid, EntityUid regionUid)
    {
        if (!TryComp<BodyComponent>(patientUid, out var body) || body.Organs == null)
            return false;

        foreach (var organ in body.Organs.ContainedEntities)
        {
            if (organ == regionUid)
                return true;
        }

        return false;
    }

    private bool TryGetRegionCategory(
        EntityUid regionUid,
        out ProtoId<OrganCategoryPrototype> category)
    {
        category = default;
        if (!TryComp<OrganComponent>(regionUid, out var organ) || organ.Category is not { } found)
            return false;

        category = found;
        return true;
    }

    private bool ValidateSurgeonAuthorization(
        EntityUid surgeonUid,
        NCSurgeryProcedurePrototype procedure)
    {
        if (procedure.RequiredAuthorizations.Count == 0)
            return true;

        if (!TryComp<NCSurgeryPractitionerComponent>(surgeonUid, out var practitioner))
            return false;

        foreach (var authorization in procedure.RequiredAuthorizations)
        {
            if (!practitioner.Authorizations.Contains(authorization))
                return false;
        }

        return true;
    }

    private bool SessionIsLive(EntityUid sessionUid)
    {
        return !Deleted(sessionUid) &&
               TryComp<NCSurgerySessionComponent>(sessionUid, out var session) &&
               IsActive(session.State);
    }

    private static bool IsActive(NCSurgerySessionState state)
    {
        return state is NCSurgerySessionState.Running or NCSurgerySessionState.Paused;
    }

    private static bool AccessMatches(NCSurgicalAccessDepth actual, NCSurgicalAccessDepth required)
    {
        // A fully closed postoperative site can be prepared and re-incised exactly like intact skin,
        // while remaining distinguishable for diagnostics and persistence before that transition.
        return actual == required ||
               actual == NCSurgicalAccessDepth.ClosedPostOp && required == NCSurgicalAccessDepth.Closed;
    }

    private void ReleaseLocks(EntityUid sessionUid, NCSurgerySessionComponent session)
    {
        if (TryComp<NCSurgicalSiteComponent>(session.TargetRegion, out var site) &&
            site.ActiveProcedure == session.ResolutionId.Value)
        {
            site.ActiveProcedure = null;
        }

        ReleasePractitionerLock(session.Surgeon, sessionUid);
    }

    private void ReleasePractitionerLock(EntityUid surgeonUid, EntityUid sessionUid)
    {
        if (!Deleted(surgeonUid) &&
            TryComp<NCSurgeryPractitionerComponent>(surgeonUid, out var practitioner) &&
            practitioner.ActiveSession == sessionUid)
        {
            practitioner.ActiveSession = null;
        }
    }

    private static NCSurgeryStepResolution Rejected(
        NCActionResolutionId actionResolutionId,
        NCSurgeryStepFailure failure,
        int stepIndex = -1,
        NCSurgeryStepResolutionStatus status = NCSurgeryStepResolutionStatus.Rejected,
        ProtoId<NCSurgeryStepPrototype>? step = null,
        TimeSpan duration = default,
        NCSurgeryConditionSnapshot? conditions = null)
    {
        return new NCSurgeryStepResolution(
            actionResolutionId,
            status,
            failure,
            step,
            stepIndex,
            null,
            duration,
            conditions);
    }

    private static NCSurgeryStartFailure MapStartFailure(NCSurgeryConditionFailure failure)
    {
        return failure switch
        {
            NCSurgeryConditionFailure.FacilityCapabilityMissing => NCSurgeryStartFailure.FacilityCapabilityMissing,
            NCSurgeryConditionFailure.FacilityUnavailable or NCSurgeryConditionFailure.PatientMoved =>
                NCSurgeryStartFailure.FacilityUnavailable,
            NCSurgeryConditionFailure.EnvironmentUnavailable => NCSurgeryStartFailure.EnvironmentUnavailable,
            NCSurgeryConditionFailure.EnvironmentUnsafe => NCSurgeryStartFailure.EnvironmentUnsafe,
            NCSurgeryConditionFailure.PatientUnstable => NCSurgeryStartFailure.PatientConditionNotMet,
            _ => NCSurgeryStartFailure.None,
        };
    }

    private static NCSurgeryStepFailure MapStepFailure(NCSurgeryConditionFailure failure)
    {
        return failure switch
        {
            NCSurgeryConditionFailure.FacilityCapabilityMissing => NCSurgeryStepFailure.FacilityCapabilityMissing,
            NCSurgeryConditionFailure.FacilityUnavailable or NCSurgeryConditionFailure.PatientMoved =>
                NCSurgeryStepFailure.FacilityUnavailable,
            NCSurgeryConditionFailure.EnvironmentUnavailable => NCSurgeryStepFailure.EnvironmentUnavailable,
            NCSurgeryConditionFailure.EnvironmentUnsafe => NCSurgeryStepFailure.EnvironmentUnsafe,
            NCSurgeryConditionFailure.PatientUnstable => NCSurgeryStepFailure.PatientConditionNotMet,
            _ => NCSurgeryStepFailure.None,
        };
    }

    private static NCSurgeryInterruptionReason MapPauseReason(NCSurgeryConditionFailure failure)
    {
        return failure switch
        {
            NCSurgeryConditionFailure.PatientMoved => NCSurgeryInterruptionReason.PatientMoved,
            NCSurgeryConditionFailure.PatientUnstable => NCSurgeryInterruptionReason.PatientUnstable,
            NCSurgeryConditionFailure.EnvironmentUnavailable or NCSurgeryConditionFailure.EnvironmentUnsafe =>
                NCSurgeryInterruptionReason.EnvironmentUnsafe,
            NCSurgeryConditionFailure.FacilityUnavailable or NCSurgeryConditionFailure.FacilityCapabilityMissing =>
                NCSurgeryInterruptionReason.FacilityUnavailable,
            _ => NCSurgeryInterruptionReason.ConditionsChanged,
        };
    }

    private enum NCSurgeryConditionFailure : byte
    {
        None,
        FacilityUnavailable,
        FacilityCapabilityMissing,
        EnvironmentUnavailable,
        EnvironmentUnsafe,
        PatientMoved,
        PatientUnstable,
    }
}
