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

namespace Content.Server._NC.Medical.Surgery;

/// <summary>
/// Orchestrates explicit surgery sessions through physical tools and cancellable timed steps. The core
/// resolver remains the only medical mutator; this boundary owns interaction, preparation and feedback.
/// </summary>
public sealed partial class NCSurgeryInteractionSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
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

        SubscribeLocalEvent<NCSurgeryToolComponent, AfterInteractEvent>(OnAfterInteract);
        SubscribeLocalEvent<NCSurgeryToolComponent, NCSurgeryStepDoAfterEvent>(OnDoAfter);
    }

    private void OnAfterInteract(Entity<NCSurgeryToolComponent> tool, ref AfterInteractEvent args)
    {
        if (args.Handled || args.Target is not { } patient || !HasComp<NCMedicalBodyComponent>(patient))
            return;

        args.Handled = true;
        if (!ValidateHeldAndRange(tool, args.User, patient))
        {
            PopupInvalid(patient, args.User);
            return;
        }

        SynchronizePreparation(patient);
        SynchronizeAuthorization(args.User, tool.Owner);

        if (!TryGetOrStartSession(tool, args.User, patient, out var region, out var session, out var failure))
        {
            _popup.PopupEntity(
                Loc.GetString("nc-medical-surgery-start-failed", ("reason", failure)),
                patient,
                args.User);
            return;
        }

        if (session.State == NCSurgerySessionState.Paused &&
            !_surgery.TryResume(region, args.User, out failure))
        {
            _popup.PopupEntity(
                Loc.GetString("nc-medical-surgery-start-failed", ("reason", failure)),
                patient,
                args.User);
            return;
        }

        if (!ValidateSessionRange(session, args.User))
        {
            PopupInvalid(patient, args.User);
            return;
        }

        if (!TryGetCurrentStep(session, out var step) || !CanToolPerform(tool.Comp, step))
        {
            _popup.PopupEntity(Loc.GetString("nc-medical-surgery-wrong-tool"), patient, args.User);
            return;
        }

        var eventArgs = new NCSurgeryStepDoAfterEvent(
            session.ResolutionId,
            NCActionResolutionId.New(),
            GetNetEntity(region),
            session.CurrentStepIndex);
        var doAfter = new DoAfterArgs(
            EntityManager,
            args.User,
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

        if (_doAfter.TryStartDoAfter(doAfter))
        {
            _popup.PopupEntity(
                Loc.GetString("nc-medical-surgery-step-started", ("step", step.Name)),
                patient,
                args.User);
        }
    }

    private void OnDoAfter(Entity<NCSurgeryToolComponent> tool, ref NCSurgeryStepDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || args.Target is not { } patient || args.Used != tool.Owner)
            return;

        args.Handled = true;
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
            !CanToolPerform(tool.Comp, step))
        {
            PopupInvalid(patient, args.User);
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
                !CanToolPerform(tool.Comp, firstStep) ||
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

    private static bool CanToolPerform(NCSurgeryToolComponent tool, NCSurgeryStepPrototype step)
    {
        foreach (var requirement in step.Tools)
        {
            if (!tool.Capabilities.TryGetValue(requirement.Capability, out var quality) ||
                quality < requirement.MinimumQuality ||
                tool.Sterility < requirement.MinimumSterility ||
                tool.Broken ||
                requirement.ConsumedUses > 0 && tool.UsesRemaining >= 0 &&
                tool.UsesRemaining < requirement.ConsumedUses)
            {
                continue;
            }

            return true;
        }

        return step.Tools.Count == 0;
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
