// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.Combat.Targeting;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Content.Shared.Buckle.Components;
using Content.Shared.DoAfter;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Surgery;

/// <summary>
/// Orchestrates explicit surgery sessions through physical tools and cancellable timed steps. The core
/// resolver remains the only medical mutator; this boundary owns interaction, preparation and feedback.
/// </summary>
public sealed partial class NCSurgeryInteractionSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private NCAnatomySystem _anatomy = default!;
    [Dependency] private NCTargetingSelectionSystem _targeting = default!;
    [Dependency] private NCSurgerySystem _surgery = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCSurgeryToolComponent, NCSurgeryStepDoAfterEvent>(OnDoAfter);
    }

    /// <summary>
    /// Starts the current server-bound step from the operating-console UI. The client supplies no tool or
    /// patient entity: the active hand and the physically buckled patient are resolved before every attempt.
    /// </summary>
    public NCSurgeryConsoleNotice TryStartCurrentStep(
        EntityUid user,
        EntityUid patient,
        EntityUid facility,
        out string stepName)
    {
        stepName = string.Empty;
        if (!HasComp<NCMedicalBodyComponent>(patient) ||
            !_hands.TryGetActiveItem(user, out var held) || held is not { } toolUid ||
            !TryComp<NCSurgeryToolComponent>(toolUid, out var toolComponent))
        {
            return NCSurgeryConsoleNotice.HoldRequiredTool;
        }

        var tool = new Entity<NCSurgeryToolComponent>(toolUid, toolComponent);
        if (!ValidateHeldAndRange(tool, user, patient))
            return NCSurgeryConsoleNotice.StepUnavailable;

        SynchronizePreparation(patient);
        SynchronizeAuthorization(user, tool.Owner);
        if (!TryComp<NCSurgeryPatientStateComponent>(patient, out var preparation) ||
            preparation.CurrentFacility != facility ||
            !TryComp<NCSurgeryGameplaySelectionComponent>(user, out var selection) ||
            selection.Patient != patient || selection.Facility != facility)
        {
            return NCSurgeryConsoleNotice.SelectionRejected;
        }

        if (selection.StepInProgress)
            return NCSurgeryConsoleNotice.StepUnavailable;

        if (!TryGetOrStartSession(tool, user, patient, out var region, out var session, out _))
            return NCSurgeryConsoleNotice.StepUnavailable;

        if (session.State == NCSurgerySessionState.Paused &&
            !_surgery.TryResume(region, user, out _))
            return NCSurgeryConsoleNotice.StepUnavailable;

        if (!ValidateSessionRange(session, user))
            return NCSurgeryConsoleNotice.StepUnavailable;

        if (!TryGetCurrentStep(session, out var step) || !CanToolPerform(tool.Owner, tool.Comp, step))
            return NCSurgeryConsoleNotice.ToolUnsuitable;

        var eventArgs = new NCSurgeryStepDoAfterEvent(
            session.ResolutionId,
            NCActionResolutionId.New(),
            GetNetEntity(region),
            session.CurrentStepIndex);
        var doAfter = new DoAfterArgs(
            EntityManager,
            user,
            step.Duration,
            eventArgs,
            tool.Owner,
            target: patient,
            used: tool.Owner)
        {
            NeedHand = true,
            BreakOnMove = true,
            BreakOnDamage = true,
            DistanceThreshold = SharedInteractionSystem.InteractionRange,
        };

        if (!_doAfter.TryStartDoAfter(doAfter))
            return NCSurgeryConsoleNotice.StepUnavailable;

        selection.StepInProgress = true;
        selection.ExecutingStepIndex = session.CurrentStepIndex;
        selection.StepStartedAt = _timing.CurTime;
        selection.StepEndsAt = _timing.CurTime + step.Duration;
        stepName = step.Name;
        var changed = new NCSurgeryStepExecutionChangedEvent(user, true, false);
        RaiseLocalEvent(patient, ref changed);
        return NCSurgeryConsoleNotice.StepStarted;
    }

    private void OnDoAfter(Entity<NCSurgeryToolComponent> tool, ref NCSurgeryStepDoAfterEvent args)
    {
        if (args.Handled || args.Target is not { } patient || args.Used != tool.Owner)
            return;

        args.Handled = true;
        if (TryComp<NCSurgeryGameplaySelectionComponent>(args.User, out var selection))
        {
            selection.StepInProgress = false;
            selection.ExecutingStepIndex = -1;
            selection.StepStartedAt = TimeSpan.Zero;
            selection.StepEndsAt = TimeSpan.Zero;
        }

        if (args.Cancelled)
        {
            var cancelled = new NCSurgeryStepExecutionChangedEvent(args.User, false, true);
            RaiseLocalEvent(patient, ref cancelled);
            return;
        }

        var region = GetEntity(args.TargetRegion);
        if (!ValidateHeldAndRange(tool, args.User, patient) ||
            !TryComp<NCSurgerySessionComponent>(region, out var session) ||
            session.ResolutionId != args.SessionResolutionId ||
            session.CurrentStepIndex != args.StepIndex ||
            session.Patient != patient ||
            session.Surgeon != args.User ||
            session.State != NCSurgerySessionState.Running ||
            !ValidateSessionRange(session, args.User) ||
            !TryGetCurrentStep(session, out var step) ||
            !CanToolPerform(tool.Owner, tool.Comp, step))
        {
            PopupInvalid(patient, args.User);
            var invalidated = new NCSurgeryStepExecutionChangedEvent(args.User, false, true);
            RaiseLocalEvent(patient, ref invalidated);
            return;
        }

        // Buckle/facility/preparation are live facts and are refreshed immediately before resolution.
        SynchronizePreparation(patient);
        var result = _surgery.ResolveStep(
            region,
            new NCSurgeryStepRequest(args.ActionResolutionId, args.User, tool.Owner));
        var key = result.Status is NCSurgeryStepResolutionStatus.Resolved or NCSurgeryStepResolutionStatus.Completed
            ? "nc-medical-surgery-step-complete"
            : "nc-medical-surgery-step-failed";
        _popup.PopupEntity(Loc.GetString(key, ("reason", result.Failure)), patient, args.User);
        var procedureCompleted = result.Status == NCSurgeryStepResolutionStatus.Completed;
        if (procedureCompleted)
            RemComp<NCSurgeryGameplaySelectionComponent>(args.User);
        var completed = new NCSurgeryStepExecutionChangedEvent(args.User, false, false, procedureCompleted);
        RaiseLocalEvent(patient, ref completed);
    }

    private bool TryGetOrStartSession(
        Entity<NCSurgeryToolComponent> tool,
        EntityUid surgeon,
        EntityUid patient,
        out EntityUid region,
        out NCSurgerySessionComponent session,
        out NCSurgeryStartFailure failure)
    {
        region = default;
        session = default!;
        failure = NCSurgeryStartFailure.IncompatibleTarget;

        // An existing surgeon lock is a constant-time session handle and takes precedence over doll state.
        if (TryComp<NCSurgeryPractitionerComponent>(surgeon, out var practitioner) &&
            practitioner.ActiveSession is { } activeRegion &&
            TryComp<NCSurgerySessionComponent>(activeRegion, out var active) &&
            active.Patient == patient &&
            active.Surgeon == surgeon &&
            active.State is NCSurgerySessionState.Running or NCSurgerySessionState.Paused)
        {
            region = activeRegion;
            session = active;
            return true;
        }

        NCSurgeryGameplaySelectionComponent? boundSelection = null;
        if (TryComp<NCSurgeryGameplaySelectionComponent>(surgeon, out var selected) &&
            selected.Patient == patient)
        {
            boundSelection = selected;
            region = selected.TargetRegion;
            var prepared = CompOrNull<NCSurgeryPatientStateComponent>(patient);
            if (Deleted(region) || Deleted(selected.Facility) ||
                prepared?.CurrentFacility != selected.Facility)
            {
                failure = NCSurgeryStartFailure.FacilityUnavailable;
                return false;
            }
        }
        else if (!TryResolveSelectedRegion(surgeon, patient, out region))
        {
            return false;
        }

        var procedures = GetRequestedProcedures(tool.Comp, surgeon, patient);
        foreach (var procedureId in procedures)
        {
            if (!_prototypes.TryIndex(procedureId, out NCSurgeryProcedurePrototype? procedure) ||
                procedure.Steps.Count == 0 ||
                !_prototypes.TryIndex(procedure.Steps[0], out NCSurgeryStepPrototype? firstStep) ||
                !CanToolPerform(tool.Owner, tool.Comp, firstStep) ||
                !_surgery.TryPrepareProcedureTarget(
                    patient,
                    region,
                    procedureId,
                    boundSelection?.Graft,
                    out var woundId,
                    out failure))
            {
                continue;
            }

            var prepared = CompOrNull<NCSurgeryPatientStateComponent>(patient);
            var request = new NCSurgeryStartRequest(
                NCActionResolutionId.New(),
                patient,
                surgeon,
                region,
                procedureId,
                boundSelection?.Facility ?? prepared?.CurrentFacility,
                boundSelection?.Facility ?? prepared?.CurrentEnvironment,
                woundId,
                boundSelection?.Graft);
            if (!_surgery.StartSurgery(request, out var sessionUid, out failure) ||
                !TryComp<NCSurgerySessionComponent>(sessionUid, out session!))
            {
                continue;
            }

            region = sessionUid;
            return true;
        }

        return false;
    }

    private IReadOnlyList<ProtoId<NCSurgeryProcedurePrototype>> GetRequestedProcedures(
        NCSurgeryToolComponent tool,
        EntityUid surgeon,
        EntityUid patient)
    {
        if (TryComp<NCSurgeryGameplaySelectionComponent>(surgeon, out var selection) &&
            selection.Patient == patient)
        {
            return [selection.Procedure];
        }

        return tool.StartingProcedures;
    }

    private bool TryResolveSelectedRegion(EntityUid surgeon, EntityUid patient, out EntityUid region)
    {
        region = default;
        var selection = _targeting.GetSelection(surgeon);
        if (selection.Mode == NCTargetingMode.SelectedZone && selection.SelectedZone is { } selectedZone)
            return _anatomy.TryResolveSelectedZone(patient, selectedZone, out _, out region);

        if (TryComp<NCWoundContainerComponent>(patient, out var wounds))
        {
            var worst = wounds.Wounds.Values
                .OrderByDescending(wound => wound.Severity)
                .ThenByDescending(wound => wound.IntegrityDamage)
                .ThenBy(wound => wound.CreatedAt)
                .ThenBy(wound => wound.WoundId)
                .FirstOrDefault();
            if (worst != null && _anatomy.TryGetOrgan(patient, worst.RegionCategory, out region))
                return true;
        }

        return TryComp<NCMedicalBodyComponent>(patient, out var medical) &&
               _anatomy.TryGetProfile((patient, medical), out var profile) &&
               _anatomy.TryResolveSelectedZone(patient, profile.AutomaticFallbackZone, out _, out region);
    }

    private bool TryGetCurrentStep(
        NCSurgerySessionComponent session,
        out NCSurgeryStepPrototype step)
    {
        step = default!;
        return _prototypes.TryIndex(session.Procedure, out NCSurgeryProcedurePrototype? procedure) &&
               session.CurrentStepIndex >= 0 &&
               session.CurrentStepIndex < procedure.Steps.Count &&
               _prototypes.TryIndex(procedure.Steps[session.CurrentStepIndex], out step!);
    }

    public bool CanToolPerform(EntityUid toolUid, NCSurgeryToolComponent tool, NCSurgeryStepPrototype step)
    {
        return _surgery.CanToolPerform(toolUid, tool, step);
    }

    /// <summary>
    /// Copies only physical state from the strap actually holding the patient. Unbuckling immediately
    /// removes facility, restraint and airway support at the next validation boundary. Drug state is read
    /// independently from authoritative pharmacology by the surgery resolver.
    /// </summary>
    public bool SynchronizePreparation(EntityUid patient)
    {
        var state = EnsureComp<NCSurgeryPatientStateComponent>(patient);
        state.CurrentFacility = null;
        state.CurrentEnvironment = null;
        state.Movement = 1f;
        state.Restrained = false;
        state.AirwayMonitored = false;

        if (!TryComp<BuckleComponent>(patient, out var buckle) ||
            !buckle.Buckled ||
            buckle.BuckledTo is not { } facility ||
            !TryComp<NCSurgeryFacilityComponent>(facility, out _) ||
            !TryComp<NCSurgeryEnvironmentComponent>(facility, out _) ||
            !TryComp<NCSurgeryPreparationProviderComponent>(facility, out var provider))
        {
            return false;
        }

        state.CurrentFacility = facility;
        state.CurrentEnvironment = facility;
        state.Movement = Math.Clamp(provider.Movement, 0f, 1f);
        state.Restrained = provider.Restrained;
        state.AirwayMonitored = provider.AirwayMonitored;
        return true;
    }

    private void SynchronizeAuthorization(EntityUid surgeon, EntityUid tool)
    {
        if (!TryComp<NCSurgeryAuthorizationProviderComponent>(tool, out var provider))
            return;

        EnsureComp<NCSurgeryPractitionerComponent>(surgeon).Authorizations.UnionWith(provider.Authorizations);
    }

    private bool ValidateHeldAndRange(
        Entity<NCSurgeryToolComponent> tool,
        EntityUid surgeon,
        EntityUid patient)
    {
        return !Deleted(surgeon) &&
               !Deleted(patient) &&
               !tool.Comp.Broken &&
               _hands.IsHolding(surgeon, tool.Owner) &&
               _interaction.InRangeUnobstructed(surgeon, patient);
    }

    private bool ValidateSessionRange(NCSurgerySessionComponent session, EntityUid surgeon)
    {
        if (session.Facility is { } facility &&
            (!_interaction.InRangeUnobstructed(surgeon, facility) ||
             !TryComp<NCSurgeryPatientStateComponent>(session.Patient, out var patient) ||
             patient.CurrentFacility != facility))
        {
            return false;
        }

        if (session.Graft is not { } graft ||
            !_interaction.InRangeUnobstructed(surgeon, graft))
        {
            return session.Graft == null;
        }

        return session.Facility is not { } graftFacility ||
               _interaction.InRangeUnobstructed(graftFacility, graft);
    }

    private void PopupInvalid(EntityUid patient, EntityUid user)
    {
        _popup.PopupEntity(Loc.GetString("nc-medical-interaction-invalidated"), patient, user);
    }
}
