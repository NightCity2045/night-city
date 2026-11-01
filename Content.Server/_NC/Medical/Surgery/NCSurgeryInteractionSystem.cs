// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.Combat.Targeting;
using Content.Server._NC.Medical.Cyberware;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Anatomy.Detachment;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Content.Shared.Buckle.Components;
using Content.Shared.DoAfter;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Surgery;

/// <summary>
/// Orchestrates explicit surgery sessions through physical tools and cancellable timed steps. The core
/// resolver remains the only medical mutator; this boundary owns interaction, preparation and feedback.
/// </summary>
public sealed partial class NCSurgeryInteractionSystem : EntitySystem
{
    private const int OrganTraversalLimit = 64;
    private const int MaximumDirectCandidates = 64;
    private static readonly TimeSpan DirectChoiceLifetime = TimeSpan.FromSeconds(12);
    private readonly Dictionary<NCActionResolutionId, (Func<bool> CanCommit, TimeSpan Expires, EntityUid User, EntityUid Patient, EntityUid Tool)> _nativeStepGuards = [];

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private NCAnatomySystem _anatomy = default!;
    [Dependency] private NCTargetingSelectionSystem _targeting = default!;
    [Dependency] private NCCyberwareSystem _cyberware = default!;
    [Dependency] private NCSurgerySystem _surgery = default!;
    [Dependency] private NCFreeformSurgerySystem _freeform = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCSurgeryToolComponent, AfterInteractEvent>(OnAfterInteract);
        SubscribeLocalEvent<NCCyberwareComponent, AfterInteractEvent>(OnCyberwareAfterInteract);
        SubscribeLocalEvent<NCDetachedOrganMedicalDataComponent, AfterInteractEvent>(OnDetachedGraftAfterInteract);
        SubscribeLocalEvent<NCSurgeryToolComponent, NCSurgeryStepDoAfterEvent>(OnDoAfter);
        SubscribeNetworkEvent<NCSurgeryDirectChoiceRequest>(OnDirectChoice);
    }

    /// <summary>
    /// Detached organic grafts use the same physical click staging contract as cyberware and prostheses.
    /// </summary>
    private void OnDetachedGraftAfterInteract(
        Entity<NCDetachedOrganMedicalDataComponent> graft,
        ref AfterInteractEvent args)
    {
        if (args.Handled ||
            args.Target is not { Valid: true } patient ||
            !args.CanReach ||
            !HasComp<NCMedicalBodyComponent>(patient) ||
            !_hands.IsHolding(args.User, graft.Owner))
        {
            return;
        }

        args.Handled = true;
        var staged = HasComp<NCCyberwareComponent>(graft.Owner)
            ? TryStageCyberware(args.User, patient, graft.Owner)
            : TryStageGraft(args.User, patient, graft.Owner);
        _popup.PopupEntity(
            Loc.GetString(staged
                ? "nc-medical-surgery-graft-staged"
                : "nc-medical-surgery-graft-incompatible"),
            patient,
            args.User);
        if (staged)
            _freeform.TryOpenSession(args.User, patient);
    }

    /// <summary>
    /// Stages a detached biological or mechanical graft against the selected missing body zone. The graft does
    /// not perform surgery by itself; it creates a server-owned plan that the required physical tools advance.
    /// </summary>
    private void OnCyberwareAfterInteract(
        Entity<NCCyberwareComponent> cyberware,
        ref AfterInteractEvent args)
    {
        if (args.Handled ||
            args.Target is not { Valid: true } patient ||
            !args.CanReach ||
            !HasComp<NCMedicalBodyComponent>(patient) ||
            !_hands.IsHolding(args.User, cyberware.Owner))
        {
            return;
        }

        args.Handled = true;
        if (!TryStageCyberware(args.User, patient, cyberware.Owner))
        {
            _popup.PopupEntity(Loc.GetString("nc-medical-surgery-graft-incompatible"), patient, args.User);
            return;
        }

        _popup.PopupEntity(Loc.GetString("nc-medical-surgery-graft-staged"), patient, args.User);
        _freeform.TryOpenSession(args.User, patient);
    }

    /// <summary>
    /// Converts a physical implant/prosthesis click into one server-owned installation plan. Replacement limbs
    /// use the detached organ edge; internal modules use a compatible live host slot in the selected region.
    /// </summary>
    public bool TryStageCyberware(EntityUid user, EntityUid patient, EntityUid module)
    {
        if (!TryComp<NCCyberwareComponent>(module, out var cyberware))
            return false;

        if (HasComp<NCDetachedOrganMedicalDataComponent>(module))
            return TryStageGraft(user, patient, module);

        var targeting = _targeting.GetSelection(user);
        if (targeting.Mode != NCTargetingMode.SelectedZone ||
            targeting.SelectedZone is not { } selectedZone ||
            !_anatomy.TryResolveSelectedZone(patient, selectedZone, out _, out var targetRegion))
        {
            return false;
        }

        if (!_cyberware.TryFindInstallSlot(patient, targetRegion, module, out _))
            return false;
        // Player surgery uses physical bay compatibility, not the legacy procedure catalogue's profile allowlist.
        BindStagedProcedure(user, patient, "NCSurgeryCyberwareInstallation", selectedZone, targetRegion, module);
        return true;
    }

    /// <summary>
    /// Binds one exact matching graft to a selected missing zone. This is public for server-side gameplay
    /// adapters and deterministic integration tests; all entity identities are revalidated here.
    /// </summary>
    public bool TryStageGraft(EntityUid user, EntityUid patient, EntityUid graft)
    {
        var targeting = _targeting.GetSelection(user);
        if (Deleted(user) || Deleted(patient) || Deleted(graft) ||
            !HasComp<NCMedicalBodyComponent>(patient) ||
            targeting.Mode != NCTargetingMode.SelectedZone || targeting.SelectedZone is not { } selectedZone ||
            !TryResolveMissingGraftSite(user, patient, [graft], out var parent, out _, out var matched) ||
            matched != graft ||
            !_surgery.TryPrepareProcedureTarget(
                patient,
                parent,
                "NCSurgeryLimbReattachment",
                graft,
                out _,
                out _))
        {
            return false;
        }

        BindStagedProcedure(user, patient, "NCSurgeryLimbReattachment", selectedZone, parent, graft);
        return true;
    }

    private void BindStagedProcedure(
        EntityUid user,
        EntityUid patient,
        ProtoId<NCSurgeryProcedurePrototype> procedure,
        ProtoId<NCTargetZonePrototype> targetZone,
        EntityUid targetRegion,
        EntityUid graft)
    {
        SynchronizePreparation(patient);
        var selection = EnsureComp<NCSurgeryGameplaySelectionComponent>(user);
        selection.Patient = patient;
        selection.Procedure = procedure;
        selection.TargetZone = targetZone;
        selection.TargetRegion = targetRegion;
        selection.Facility = Comp<NCSurgeryPatientStateComponent>(patient).CurrentFacility;
        selection.Graft = graft;
        selection.InitialStepIndex = 0;
        selection.StepInProgress = false;
        selection.ExecutingStepIndex = -1;
        selection.StepStartedAt = TimeSpan.Zero;
        selection.StepEndsAt = TimeSpan.Zero;
    }

    /// <summary>
    /// Player-operated surgery opens the freeform surgical field from a physical tool click. The retained
    /// deterministic procedure resolver below remains available only to actor-less NPC and automation adapters.
    /// </summary>
    private void OnAfterInteract(Entity<NCSurgeryToolComponent> tool, ref AfterInteractEvent args)
    {
        if (args.Handled ||
            args.Target is not { Valid: true } patient ||
            !args.CanReach ||
            !HasComp<NCMedicalBodyComponent>(patient) ||
            !ValidateHeldAndRange(tool, args.User, patient))
        {
            return;
        }

        if (HasComp<ActorComponent>(args.User))
        {
            args.Handled = true;
            if (!_freeform.TryOpenSession(args.User, patient))
                PopupStartFailure(patient, args.User, NCSurgeryInteractionNotice.NoCompatibleProcedure);
            return;
        }

        args.Handled = true;
        SynchronizePreparation(patient);
        SynchronizeAuthorization(args.User, tool.Owner);
        var prepared = Comp<NCSurgeryPatientStateComponent>(patient);
        var facility = prepared.CurrentFacility;

        if (TryComp<NCSurgeryGameplaySelectionComponent>(args.User, out var existing) &&
            existing.Patient == patient)
        {
            existing.Facility = facility;
            var result = TryStartCurrentStep(args.User, patient, out var stepName);
            if (result == NCSurgeryInteractionNotice.StepStarted || HasLiveSession(args.User, patient))
            {
                PopupStartResult(patient, args.User, result, stepName);
                return;
            }

            // A bound plan without a live session can become stale when pathology, graft or target state
            // changes. Discard it and resolve the current physical interaction from authoritative state.
            RemComp<NCSurgeryGameplaySelectionComponent>(args.User);
        }

        if (!TryBuildDirectCandidates(tool, args.User, patient, facility, out var candidates))
        {
            PopupStartFailure(patient, args.User, NCSurgeryInteractionNotice.NoCompatibleProcedure);
            return;
        }

        if (candidates.Count == 1)
        {
            var result = BindAndStartDirect(args.User, patient, facility, tool.Owner, candidates[0], out var stepName);
            PopupStartResult(patient, args.User, result, stepName);
            return;
        }

        if (!TryComp<ActorComponent>(args.User, out var actor))
            return;

        var offerId = NCActionResolutionId.New();
        var pending = EnsureComp<NCSurgeryDirectChoiceComponent>(args.User);
        pending.OfferId = offerId;
        pending.Patient = patient;
        pending.Tool = tool.Owner;
        pending.Facility = facility;
        pending.ExpiresAt = _timing.CurTime + DirectChoiceLifetime;
        pending.Candidates.Clear();
        pending.Candidates.AddRange(candidates);

        var options = candidates
            .Select(candidate => new NCSurgeryDirectProcedureOption(
                candidate.Procedure,
                candidate.TargetCategory,
                MetaData(candidate.TargetRegion).EntityName))
            .ToList();
        RaiseNetworkEvent(new NCSurgeryDirectChoiceOffer(offerId, options), actor.PlayerSession);
        _popup.PopupEntity(Loc.GetString("nc-medical-surgery-direct-choose"), patient, args.User);
    }

    private void OnDirectChoice(NCSurgeryDirectChoiceRequest request, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is not { Valid: true } user ||
            !TryComp<NCSurgeryDirectChoiceComponent>(user, out var pending) ||
            pending.OfferId != request.OfferId ||
            _timing.CurTime > pending.ExpiresAt)
        {
            return;
        }

        NCSurgeryDirectCandidate? selected = null;
        foreach (var candidate in pending.Candidates)
        {
            if (candidate.Procedure == request.Procedure && candidate.TargetCategory == request.TargetCategory)
            {
                selected = candidate;
                break;
            }
        }

        var patient = pending.Patient;
        var toolUid = pending.Tool;
        var facility = pending.Facility;
        RemCompDeferred<NCSurgeryDirectChoiceComponent>(user);
        if (selected is not { } choice ||
            !TryComp<NCSurgeryToolComponent>(toolUid, out var toolComponent) ||
            !ValidateHeldAndRange((toolUid, toolComponent), user, patient))
        {
            PopupStartFailure(patient, user, NCSurgeryInteractionNotice.SelectionRejected);
            return;
        }

        SynchronizePreparation(patient);
        var prepared = Comp<NCSurgeryPatientStateComponent>(patient);
        facility = prepared.CurrentFacility;
        if (!_surgery.TryPrepareProcedureTarget(
                patient,
                choice.TargetRegion,
                choice.Procedure,
                choice.Graft,
                out _,
                out _))
        {
            PopupStartFailure(patient, user, NCSurgeryInteractionNotice.SelectionRejected);
            return;
        }

        var result = BindAndStartDirect(user, patient, facility, toolUid, choice, out var stepName);
        PopupStartResult(patient, user, result, stepName);
    }

    private bool TryBuildDirectCandidates(
        Entity<NCSurgeryToolComponent> tool,
        EntityUid surgeon,
        EntityUid patient,
        EntityUid? facility,
        out List<NCSurgeryDirectCandidate> candidates)
    {
        candidates = [];
        IReadOnlyList<EntityUid> grafts = GetCandidateGrafts(facility, surgeon);
        List<SurgicalRegion> regions;
        var missingAttachmentSite = false;
        if (TryResolveSelectedRegion(surgeon, patient, out var selectedRoot))
        {
            regions = CollectSurgicalRegions(patient)
                .Where(region => IsWithinRegion(region.Region, selectedRoot))
                .OrderBy(region => region.Category.Id, StringComparer.Ordinal)
                .ToList();
        }
        else if (TryResolveMissingGraftSite(
                     surgeon,
                     patient,
                     grafts,
                     out var parent,
                     out var parentCategory,
                     out var matchedGraft))
        {
            // A missing organ has no EntityUid to select. Its detached payload carries the severed parent edge,
            // so the live parent remains the sole authoritative operation site.
            regions = [new SurgicalRegion(parent, parentCategory)];
            grafts = [matchedGraft];
            missingAttachmentSite = true;
        }
        else
        {
            return false;
        }

        foreach (var procedure in _prototypes.EnumeratePrototypes<NCSurgeryProcedurePrototype>()
                     .OrderBy(procedure => procedure.ID, StringComparer.Ordinal))
        {
            if (missingAttachmentSite && !procedure.RequiresGraft)
                continue;

            var directStepIndex = GetDirectStartStep(procedure, tool.Owner, tool.Comp);
            if (directStepIndex < 0)
            {
                continue;
            }

            foreach (var region in regions)
            {
                EntityUid? boundGraft = null;
                if (procedure.RequiresGraft)
                {
                    foreach (var graft in grafts)
                    {
                        if (!_surgery.TryPrepareProcedureTarget(
                                patient,
                                region.Region,
                                procedure.ID,
                                graft,
                                out _,
                                out _))
                        {
                            continue;
                        }

                        boundGraft = graft;
                        break;
                    }

                    if (boundGraft == null)
                        continue;
                }
                else if (!_surgery.TryPrepareProcedureTarget(
                        patient,
                        region.Region,
                        procedure.ID,
                        null,
                        out _,
                        out _))
                {
                    continue;
                }

                candidates.Add(new NCSurgeryDirectCandidate(
                    procedure.ID,
                    region.Category,
                    region.Region,
                    boundGraft,
                    directStepIndex));
                if (candidates.Count >= MaximumDirectCandidates)
                    return true;
            }
        }

        return candidates.Count > 0;
    }

    private bool TryResolveMissingGraftSite(
        EntityUid surgeon,
        EntityUid patient,
        IReadOnlyList<EntityUid> grafts,
        out EntityUid parent,
        out ProtoId<OrganCategoryPrototype> parentCategory,
        out EntityUid matchedGraft)
    {
        parent = default;
        parentCategory = default;
        matchedGraft = default;
        var selection = _targeting.GetSelection(surgeon);
        if (selection.Mode != NCTargetingMode.SelectedZone ||
            selection.SelectedZone is not { } selectedZone ||
            !_prototypes.TryIndex(selectedZone, out NCTargetZonePrototype? zone))
        {
            return false;
        }

        foreach (var candidate in grafts.OrderBy(uid => uid.Id))
        {
            if (!TryComp<OrganComponent>(candidate, out var organ) ||
                organ.Category is not { } graftCategory ||
                graftCategory != zone.ExternalOrganCategory && graftCategory != zone.MedicalRegionCategory ||
                _anatomy.TryGetOrgan(patient, graftCategory, out _) ||
                !TryComp<NCDetachedOrganMedicalDataComponent>(candidate, out var payload) ||
                !_anatomy.TryGetOrgan(patient, payload.RequiredParentCategory, out var liveParent))
            {
                continue;
            }

            parent = liveParent;
            parentCategory = payload.RequiredParentCategory;
            matchedGraft = candidate;
            return true;
        }

        return false;
    }

    private int GetDirectStartStep(
        NCSurgeryProcedurePrototype procedure,
        EntityUid toolUid,
        NCSurgeryToolComponent tool)
    {
        for (var index = 0; index < procedure.Steps.Count; index++)
        {
            if (!_prototypes.TryIndex(procedure.Steps[index], out NCSurgeryStepPrototype? step))
                return -1;
            if (CanToolPerform(toolUid, tool, step))
                return index;
            if (!step.OptionalAtDirectStart)
                return -1;
        }

        return -1;
    }

    private NCSurgeryInteractionNotice BindAndStartDirect(
        EntityUid user,
        EntityUid patient,
        EntityUid? facility,
        EntityUid tool,
        NCSurgeryDirectCandidate candidate,
        out string stepName)
    {
        SynchronizeAuthorization(user, tool);
        var selection = EnsureComp<NCSurgeryGameplaySelectionComponent>(user);
        selection.Patient = patient;
        selection.Procedure = candidate.Procedure;
        selection.TargetZone = _targeting.GetSelection(user).SelectedZone ?? default;
        selection.TargetRegion = candidate.TargetRegion;
        selection.Facility = facility;
        selection.Graft = candidate.Graft;
        selection.InitialStepIndex = candidate.InitialStepIndex;
        selection.StepInProgress = false;
        selection.ExecutingStepIndex = -1;
        selection.StepStartedAt = TimeSpan.Zero;
        selection.StepEndsAt = TimeSpan.Zero;
        return TryStartCurrentStep(user, patient, out stepName);
    }

    private List<SurgicalRegion> CollectSurgicalRegions(EntityUid patient)
    {
        var result = new List<SurgicalRegion>();
        if (!TryComp<BodyComponent>(patient, out var body) || body.Organs == null)
            return result;

        var organs = body.Organs.ContainedEntities;
        foreach (var region in organs)
        {
            if (!HasComp<NCSurgicalSiteComponent>(region) ||
                !TryComp<OrganComponent>(region, out var organ) ||
                organ.Category is not { } category)
            {
                continue;
            }

            result.Add(new SurgicalRegion(region, category));
        }

        return result;
    }

    private bool IsWithinRegion(EntityUid candidate, EntityUid root)
    {
        EntityUid? current = candidate;
        var remaining = OrganTraversalLimit;
        while (current is { } uid && remaining-- > 0)
        {
            if (uid == root)
                return true;
            current = CompOrNull<ChildOrganComponent>(uid)?.Parent;
        }

        return false;
    }

    private IReadOnlyList<EntityUid> GetCandidateGrafts(EntityUid? facility, EntityUid surgeon)
    {
        var candidates = new HashSet<EntityUid>();
        foreach (var held in _hands.EnumerateHeld(surgeon))
        {
            if (IsPotentialGraft(held))
                candidates.Add(held);
        }

        if (facility is { } facilityUid &&
            TryComp<NCSurgeryGraftSlotComponent>(facilityUid, out var slot) &&
            slot.Graft is { Valid: true } staged &&
            IsPotentialGraft(staged) &&
            _interaction.InRangeUnobstructed(surgeon, staged))
        {
            candidates.Add(staged);
        }

        var coordinates = Transform(surgeon).Coordinates;
        foreach (var nearby in _lookup.GetEntitiesInRange<NCDetachedOrganMedicalDataComponent>(
                     coordinates,
                     SharedInteractionSystem.InteractionRange,
                     LookupFlags.Uncontained))
        {
            candidates.Add(nearby.Owner);
        }

        foreach (var nearby in _lookup.GetEntitiesInRange<NCCyberwareComponent>(
                     coordinates,
                     SharedInteractionSystem.InteractionRange,
                     LookupFlags.Uncontained))
        {
            candidates.Add(nearby.Owner);
        }

        return candidates
            .Where(candidate => !Deleted(candidate) && _interaction.InRangeUnobstructed(surgeon, candidate))
            .OrderBy(candidate => candidate.Id)
            .ToList();
    }

    private bool IsPotentialGraft(EntityUid candidate)
    {
        return !Deleted(candidate) &&
               (HasComp<NCDetachedOrganMedicalDataComponent>(candidate) ||
                HasComp<NCCyberwareComponent>(candidate));
    }

    private void PopupStartResult(
        EntityUid patient,
        EntityUid user,
        NCSurgeryInteractionNotice result,
        string stepName)
    {
        if (result == NCSurgeryInteractionNotice.StepStarted)
        {
            _popup.PopupEntity(
                Loc.GetString("nc-medical-surgery-step-started", ("step", stepName)),
                patient,
                user);
            return;
        }

        PopupStartFailure(patient, user, result);
    }

    private void PopupStartFailure(EntityUid patient, EntityUid user, NCSurgeryInteractionNotice reason)
    {
        _popup.PopupEntity(
            Loc.GetString("nc-medical-surgery-start-failed", ("reason", reason)),
            patient,
            user);
    }

    private readonly record struct SurgicalRegion(
        EntityUid Region,
        ProtoId<OrganCategoryPrototype> Category);

    /// <summary>
    /// Starts the current server-bound step from the operating-console UI. The client supplies no tool or
    /// patient entity: the active hand and the physically buckled patient are resolved before every attempt.
    /// </summary>
    public NCSurgeryInteractionNotice TryStartCurrentStep(
        EntityUid user,
        EntityUid patient,
        out string stepName,
        Func<bool>? canCommit = null)
    {
        stepName = string.Empty;
        // An expired guard must remain a denial until its DoAfter is consumed. Only deleted physical participants are safe to prune.
        foreach (var expired in _nativeStepGuards.Where(pair => !Exists(pair.Value.User) || !Exists(pair.Value.Patient) || !Exists(pair.Value.Tool)).Select(pair => pair.Key).ToArray())
            _nativeStepGuards.Remove(expired);
        if (canCommit != null && (_nativeStepGuards.Count >= 64 || !canCommit()))
            return NCSurgeryInteractionNotice.StepUnavailable;
        if (!HasComp<NCMedicalBodyComponent>(patient) ||
            !_hands.TryGetActiveItem(user, out var held) || held is not { } toolUid ||
            !TryComp<NCSurgeryToolComponent>(toolUid, out var toolComponent))
        {
            return NCSurgeryInteractionNotice.HoldRequiredTool;
        }

        var tool = new Entity<NCSurgeryToolComponent>(toolUid, toolComponent);
        if (!ValidateHeldAndRange(tool, user, patient))
            return NCSurgeryInteractionNotice.StepUnavailable;

        SynchronizePreparation(patient);
        SynchronizeAuthorization(user, tool.Owner);
        var preparation = Comp<NCSurgeryPatientStateComponent>(patient);
        if (!TryComp<NCSurgeryGameplaySelectionComponent>(user, out var selection) ||
            selection.Patient != patient)
        {
            return NCSurgeryInteractionNotice.SelectionRejected;
        }
        selection.Facility = preparation.CurrentFacility;

        if (selection.StepInProgress)
            return NCSurgeryInteractionNotice.StepUnavailable;

        if (!TryGetOrStartSession(tool, user, patient, out var region, out var session, out _))
            return NCSurgeryInteractionNotice.StepUnavailable;

        if (session.State == NCSurgerySessionState.Paused &&
            !_surgery.TryResume(region, user, out _))
            return NCSurgeryInteractionNotice.StepUnavailable;

        if (!ValidateSessionRange(session, user))
            return NCSurgeryInteractionNotice.StepUnavailable;

        if (!TryGetCurrentStep(session, out var step) || !CanToolPerform(tool.Owner, tool.Comp, step))
            return NCSurgeryInteractionNotice.ToolUnsuitable;

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

        if (canCommit != null)
            _nativeStepGuards.Add(eventArgs.ActionResolutionId, (canCommit, _timing.CurTime + TimeSpan.FromMinutes(5), user, patient, tool.Owner));
        if (!_doAfter.TryStartDoAfter(doAfter))
        {
            _nativeStepGuards.Remove(eventArgs.ActionResolutionId);
            return NCSurgeryInteractionNotice.StepUnavailable;
        }

        selection.StepInProgress = true;
        selection.ExecutingStepIndex = session.CurrentStepIndex;
        selection.StepStartedAt = _timing.CurTime;
        selection.StepEndsAt = _timing.CurTime + step.Duration;
        stepName = step.Name;
        var changed = new NCSurgeryStepExecutionChangedEvent(user, true, false);
        RaiseLocalEvent(patient, ref changed);
        return NCSurgeryInteractionNotice.StepStarted;
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

        var nativeAuthorized = !_nativeStepGuards.Remove(args.ActionResolutionId, out var guard) ||
            guard.Expires > _timing.CurTime && guard.CanCommit();
        if (args.Cancelled || !nativeAuthorized)
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
        var completed = new NCSurgeryStepExecutionChangedEvent(args.User, false, false, procedureCompleted,
            result.Status is NCSurgeryStepResolutionStatus.Resolved or NCSurgeryStepResolutionStatus.Completed);
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
            if (Deleted(region))
            {
                failure = NCSurgeryStartFailure.IncompatibleTarget;
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
                prepared?.CurrentFacility,
                prepared?.CurrentEnvironment,
                woundId,
                boundSelection?.Graft,
                boundSelection?.InitialStepIndex ?? 0);
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
        return session.Graft is not { } graft ||
               _interaction.InRangeUnobstructed(surgeon, graft);
    }

    private bool HasLiveSession(EntityUid surgeon, EntityUid patient)
    {
        return TryComp<NCSurgeryPractitionerComponent>(surgeon, out var practitioner) &&
               practitioner.ActiveSession is { } region &&
               TryComp<NCSurgerySessionComponent>(region, out var session) &&
               session.Patient == patient &&
               session.Surgeon == surgeon &&
               session.State is NCSurgerySessionState.Running or NCSurgerySessionState.Paused;
    }

    private void PopupInvalid(EntityUid patient, EntityUid user)
    {
        _popup.PopupEntity(Loc.GetString("nc-medical-interaction-invalidated"), patient, user);
    }
}
