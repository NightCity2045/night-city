// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using Content.Server._NC.Medical.Bleeding;
using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Medical.Pharmacology;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Anatomy.Detachment;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Verbs;
using Robust.Shared.Containers;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Surgery;

/// <summary>
/// Server-authoritative player surgery. Clients submit only bounded normalized geometry and opaque session
/// handles; the server re-reads the held item, body graph, range, revision and resources for every mutation.
/// </summary>
public sealed partial class NCFreeformSurgerySystem : EntitySystem
{
    private const string AttachmentContainerId = "nc-freeform-surgical-attachments";
    private const int MaximumBodyOrgans = 96;
    private const int MaximumCyberwarePerRegion = 16;

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private NCAnatomySystem _anatomy = default!;
    [Dependency] private NCPharmacologySystem _pharmacology = default!;
    [Dependency] private NCMedicalBodySystem _medicalBodies = default!;
    [Dependency] private NCFreeformSurgeryEffectSystem _effects = default!;
    [Dependency] private NCCavityFluidSystem _cavityFluid = default!;
    [Dependency] private NCCyberwareSystem _cyberware = default!;

    public override void Initialize()
    {
        base.Initialize();
        // Diagnostics already owns the medical-body verb subscription. Robust permits only one directed
        // subscription for a component/event pair, so surgery binds through the body's physiology marker.
        SubscribeLocalEvent<NCPhysiologyComponent, GetVerbsEvent<AlternativeVerb>>(OnGetVerbs);
        SubscribeNetworkEvent<NCFreeformSurgeryRefreshRequest>(OnRefresh);
        SubscribeNetworkEvent<NCFreeformSurgerySelectionRequest>(OnSelection);
        SubscribeNetworkEvent<NCFreeformSurgeryCapabilityRequest>(OnCapability);
        SubscribeNetworkEvent<NCFreeformSurgeryStrokeRequest>(OnStroke);
        SubscribeNetworkEvent<NCFreeformSurgeryAttachRequest>(OnAttach);
        SubscribeNetworkEvent<NCFreeformSurgeryDetachRequest>(OnDetach);
        SubscribeNetworkEvent<NCFreeformSurgeryCloseRequest>(OnClose);
        SubscribeLocalEvent<NCFreeformSurgicalAttachmentComponent, ComponentShutdown>(OnAttachmentShutdown);
    }

    private void OnGetVerbs(
        Entity<NCPhysiologyComponent> patient,
        ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract ||
            !TryComp<NCMedicalBodyComponent>(patient, out var medical) ||
            !_prototypes.TryIndex(medical.Profile, out NCAnatomyProfilePrototype? anatomy) ||
            !_prototypes.HasIndex<NCFreeformSurgeryProfilePrototype>(anatomy.SurgicalField))
        {
            return;
        }

        var surgeon = args.User;
        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString("nc-medical-freeform-surgery-verb"),
            Priority = 4,
            Act = () => TryOpenSession(surgeon, patient.Owner),
        });
    }

    /// <summary>
    /// Opens the player-operated field from either the explicit verb or a physical surgical-tool click.
    /// Non-player operators return false so the retained legacy resolver remains available to NPC adapters.
    /// </summary>
    public bool TryOpenSession(EntityUid surgeon, EntityUid patient)
    {
        if (!TryComp<ActorComponent>(surgeon, out var actor) ||
            !_interaction.InRangeUnobstructed(surgeon, patient) ||
            !TryComp<NCMedicalBodyComponent>(patient, out var medical) ||
            !_prototypes.TryIndex(medical.Profile, out NCAnatomyProfilePrototype? anatomy))
        {
            return false;
        }

        // Freeform surgery derives the actual anatomical region from the operative-field pixel. Combat
        // targeting must not preselect or redirect the first surgical gesture.
        var requestedZone = anatomy.AutomaticFallbackZone;
        if (!TryResolveFieldRegion(surgeon, patient, requestedZone, out var zone, out var region))
            return false;

        var session = EnsureComp<NCFreeformSurgerySessionComponent>(surgeon);
        session.SessionId = NCActionResolutionId.New();
        session.Patient = patient;
        session.Region = region;
        session.TargetZone = zone;
        session.Surface = NCWoundSurface.Front;
        session.Depth = 0f;
        session.SelectedCapability = null;
        session.LastSequence = 0;
        session.LastInputAt = TimeSpan.Zero;
        session.LastRefreshAt = TimeSpan.Zero;
        session.StartedAt = _timing.CurTime;
        session.StructureHandles.Clear();
        session.AttachmentHandles.Clear();
        SendState(surgeon, session, actor.PlayerSession);
        return true;
    }

    private void OnRefresh(NCFreeformSurgeryRefreshRequest request, EntitySessionEventArgs args)
    {
        if (!TryResolveSession(request.SessionId, args, out var surgeon, out var session, out var profile, out var failure))
        {
            SendFailure(args.SenderSession, request.SessionId, failure);
            return;
        }

        if (_timing.CurTime - session.LastRefreshAt < profile.RefreshInterval)
            return;
        session.LastRefreshAt = _timing.CurTime;
        SendState(surgeon, session, args.SenderSession);
    }

    private void OnSelection(NCFreeformSurgerySelectionRequest request, EntitySessionEventArgs args)
    {
        if (!TryResolveSession(request.SessionId, args, out var surgeon, out var session, out _, out var failure))
        {
            SendFailure(args.SenderSession, request.SessionId, failure);
            return;
        }

        if (!Enum.IsDefined(request.Surface) || !float.IsFinite(request.Depth) ||
            !_prototypes.TryIndex(request.Zone, out NCTargetZonePrototype? requested) || !requested.Selectable ||
            !TryResolveFieldRegion(surgeon, session.Patient, request.Zone, out var zone, out var region))
        {
            SendState(surgeon, session, args.SenderSession, false, NCFreeformSurgeryFailure.InvalidRegion);
            return;
        }

        session.TargetZone = request.Zone;
        session.Region = region;
        session.Surface = request.Surface;
        session.Depth = Math.Clamp(request.Depth, 0f, 1f);
        session.StructureHandles.Clear();
        session.AttachmentHandles.Clear();
        SendState(surgeon, session, args.SenderSession);
    }

    /// <summary>
    /// Resolves a normal live zone or the receiving parent of a server-staged graft. The latter allows a
    /// missing limb to retain its doll zone while all mutations remain anchored to a live patient organ.
    /// </summary>
    private bool TryResolveFieldRegion(
        EntityUid surgeon,
        EntityUid patient,
        ProtoId<NCTargetZonePrototype> requestedZone,
        out ProtoId<NCTargetZonePrototype> actualZone,
        out EntityUid region)
    {
        actualZone = default;
        region = default;
        if (TryComp<NCSurgeryGameplaySelectionComponent>(surgeon, out var selection) &&
            selection.Patient == patient && selection.Graft != null && selection.TargetZone == requestedZone &&
            !Deleted(selection.TargetRegion) &&
            TryComp<OrganComponent>(selection.TargetRegion, out var organ) && organ.Body == patient &&
            HasComp<NCSurgicalSiteComponent>(selection.TargetRegion))
        {
            actualZone = requestedZone;
            region = selection.TargetRegion;
            return true;
        }

        return _anatomy.TryResolveSelectedZone(patient, requestedZone, out actualZone, out region);
    }

    private void OnCapability(NCFreeformSurgeryCapabilityRequest request, EntitySessionEventArgs args)
    {
        if (!TryResolveSession(request.SessionId, args, out var surgeon, out var session, out var profile, out var failure))
        {
            SendFailure(args.SenderSession, request.SessionId, failure);
            return;
        }

        if (!_hands.TryGetActiveItem(surgeon, out var held) || held is not { } tool ||
            !TryComp<NCSurgeryToolComponent>(tool, out var surgicalTool) ||
            !surgicalTool.Capabilities.ContainsKey(request.Capability) ||
            !profile.Tools.ContainsKey(request.Capability))
        {
            SendState(surgeon, session, args.SenderSession, false, NCFreeformSurgeryFailure.CapabilityUnavailable);
            return;
        }

        session.SelectedCapability = request.Capability;
        SendState(surgeon, session, args.SenderSession);
    }

    private void OnStroke(NCFreeformSurgeryStrokeRequest request, EntitySessionEventArgs args)
    {
        if (!TryResolveSession(request.SessionId, args, out var surgeon, out var session, out var profile, out var failure))
        {
            SendFailure(args.SenderSession, request.SessionId, failure);
            return;
        }

        if (_timing.CurTime - session.LastInputAt < profile.MinimumInputInterval)
        {
            SendState(surgeon, session, args.SenderSession, false, NCFreeformSurgeryFailure.RateLimited);
            return;
        }

        if (!ValidateStroke(request, profile) || request.Sequence != session.LastSequence + 1 ||
            request.Surface != session.Surface ||
            !_prototypes.TryIndex(request.Zone, out NCTargetZonePrototype? requestedZone) ||
            !requestedZone.Selectable ||
            !TryResolveFieldRegion(surgeon, session.Patient, request.Zone, out _, out var requestedRegion))
        {
            SendState(surgeon, session, args.SenderSession, false, NCFreeformSurgeryFailure.InvalidInput);
            return;
        }

        // The zone under the first stroke pixel becomes authoritative as part of the same request. A newly hit
        // region has no client-known revision yet; same-region actions retain the strict stale-state check.
        var regionChanged = request.Zone != session.TargetZone || requestedRegion != session.Region;
        if (regionChanged)
        {
            session.TargetZone = request.Zone;
            session.Region = requestedRegion;
            session.StructureHandles.Clear();
            session.AttachmentHandles.Clear();
        }

        if (!TryComp<NCSurgicalSiteComponent>(session.Region, out var site) ||
            !regionChanged && request.ExpectedRevision != site.SpatialRevision)
        {
            SendState(surgeon, session, args.SenderSession, false, NCFreeformSurgeryFailure.StaleRevision);
            return;
        }

        if (!_hands.TryGetActiveItem(surgeon, out var held) || held is not { } tool ||
            !TryComp<NCSurgeryToolComponent>(tool, out var surgicalTool))
        {
            SendState(surgeon, session, args.SenderSession, false, NCFreeformSurgeryFailure.ToolUnavailable);
            return;
        }
        if (surgicalTool.Broken)
        {
            SendState(surgeon, session, args.SenderSession, false, NCFreeformSurgeryFailure.ToolBroken);
            return;
        }

        if (!TryResolveCapability(session, profile, surgicalTool, out var capability, out var definition, out var quality))
        {
            SendState(surgeon, session, args.SenderSession, false, NCFreeformSurgeryFailure.CapabilityUnavailable);
            return;
        }

        session.SelectedCapability = capability;
        session.Depth = Math.Clamp(request.Depth, 0f, definition.MaximumDepth);
        var points = ApplyPatientMotion(request.Points, session, profile, ResolveMovement(session.Patient));
        var center = StrokeCenter(points);
        var canContactHiddenStructure = definition.Action is
            NCFreeformSurgeryAction.Incise or NCFreeformSurgeryAction.Autopsy;
        var structure = session.Depth >= definition.MinimumDepth
            ? ResolveStructureAt(surgeon, session, profile, site, center, session.Depth, !canContactHiddenStructure)
            : null;
        if (definition.Action == NCFreeformSurgeryAction.Cyberware &&
            HasStagedInternalCyberware(surgeon, session) &&
            (!TryResolveStagedCyberwareTarget(surgeon, session, profile, out var cyberwareTarget, out _) ||
             session.Depth < cyberwareTarget.MinimumDepth ||
             !Contains(cyberwareTarget, center) ||
             !IsExposed(site, session, cyberwareTarget)))
        {
            session.LastSequence = request.Sequence;
            session.LastInputAt = _timing.CurTime;
            SendState(surgeon, session, args.SenderSession, false, NCFreeformSurgeryFailure.NoApplicableTarget);
            return;
        }
        var hasBone = structure is { } structureUid && HasComp<NCBoneComponent>(structureUid);
        var cranial = TryComp<NCVascularRegionComponent>(session.Region, out var vascular) &&
                      vascular.Cavity == NCBodyCavity.Cranial;
        var result = _effects.ApplyStroke(new NCFreeformSurgeryEffectContext(
            surgeon,
            session.Patient,
            session.Region,
            tool,
            surgicalTool,
            session.TargetZone,
            session.Surface,
            session.Depth,
            points,
            definition,
            quality,
            surgicalTool.Sterility,
            profile.UnpreparedFieldContamination,
            profile.MaximumPersistentIncisions,
            structure,
            hasBone,
            cranial,
            profile.IncidentalTendonRuptureThreshold,
            profile.IncidentalBoneCrackThreshold,
            profile.IncidentalBoneBreakThreshold));

        session.LastSequence = request.Sequence;
        session.LastInputAt = _timing.CurTime;
        if (result.Accepted && !TryKeepSessionAnchored(surgeon, session))
        {
            SendFailure(args.SenderSession, session.SessionId, NCFreeformSurgeryFailure.InvalidRegion);
            return;
        }
        SendState(
            surgeon,
            session,
            args.SenderSession,
            result.Accepted,
            result.Failure ?? NCFreeformSurgeryFailure.None);
    }

    private void OnAttach(NCFreeformSurgeryAttachRequest request, EntitySessionEventArgs args)
    {
        if (!TryResolveSession(request.SessionId, args, out var surgeon, out var session, out var profile, out var failure))
        {
            SendFailure(args.SenderSession, request.SessionId, failure);
            return;
        }
        if (!FiniteNormalized(request.Position) ||
            !_prototypes.TryIndex(request.Zone, out NCTargetZonePrototype? requestedZone) ||
            !requestedZone.Selectable ||
            !TryResolveFieldRegion(surgeon, session.Patient, request.Zone, out _, out var requestedRegion))
        {
            SendState(surgeon, session, args.SenderSession, false, NCFreeformSurgeryFailure.AttachmentUnavailable);
            return;
        }

        // RMB attachment follows the same pixel-derived targeting contract as a normal stroke.
        var regionChanged = request.Zone != session.TargetZone || requestedRegion != session.Region;
        if (regionChanged)
        {
            session.TargetZone = request.Zone;
            session.Region = requestedRegion;
            session.StructureHandles.Clear();
            session.AttachmentHandles.Clear();
        }

        if (!TryComp<NCSurgicalSiteComponent>(session.Region, out var site) ||
            !regionChanged && request.ExpectedRevision != site.SpatialRevision ||
            !_hands.TryGetActiveItem(surgeon, out var held) || held is not { } tool ||
            !TryComp<NCSurgeryToolComponent>(tool, out var surgicalTool) ||
            !TryResolveCapability(session, profile, surgicalTool, out _, out var definition, out var quality) ||
            !definition.Attachable)
        {
            SendState(surgeon, session, args.SenderSession, false, NCFreeformSurgeryFailure.AttachmentUnavailable);
            return;
        }

        var incision = FindIncision(site, session, request.Position, definition.Radius);
        if (incision == null || incision.Sutured || incision.Depth + 0.001f < definition.MinimumDepth ||
            definition.Action == NCFreeformSurgeryAction.DrainCavity &&
            !HasComp<NCCavityFluidComponent>(session.Region))
        {
            SendState(surgeon, session, args.SenderSession, false, NCFreeformSurgeryFailure.AttachmentUnavailable);
            return;
        }

        var container = _containers.EnsureContainer<Container>(session.Patient, AttachmentContainerId);
        if (definition.Action == NCFreeformSurgeryAction.DrainCavity &&
            container.ContainedEntities.Any(contained =>
                TryComp<NCFreeformSurgicalAttachmentComponent>(contained, out var existing) &&
                existing.Region == session.Region &&
                existing.Action == NCFreeformSurgeryAction.DrainCavity))
        {
            SendState(surgeon, session, args.SenderSession, false, NCFreeformSurgeryFailure.AttachmentUnavailable);
            return;
        }
        if (!_hands.TryDropIntoContainer(surgeon, tool, container))
        {
            SendState(surgeon, session, args.SenderSession, false, NCFreeformSurgeryFailure.AttachmentUnavailable);
            return;
        }

        var attachment = EnsureComp<NCFreeformSurgicalAttachmentComponent>(tool);
        attachment.Patient = session.Patient;
        attachment.Region = session.Region;
        attachment.IncisionId = incision.IncisionId;
        attachment.Position = request.Position;
        attachment.Action = definition.Action;
        attachment.ContinuousDrainRate = 0f;
        if (definition.Action == NCFreeformSurgeryAction.DrainCavity &&
            definition.ContinuousDrainRate > 0f)
        {
            attachment.ContinuousDrainRate = definition.ContinuousDrainRate * quality;
            _cavityFluid.TrySetDrainRate(
                session.Patient,
                session.Region,
                attachment.ContinuousDrainRate);
        }
        unchecked
        {
            site.SpatialRevision++;
        }
        MarkSiteChanged(session.Patient);
        SendState(surgeon, session, args.SenderSession);
    }

    private void OnDetach(NCFreeformSurgeryDetachRequest request, EntitySessionEventArgs args)
    {
        if (!TryResolveSession(request.SessionId, args, out var surgeon, out var session, out _, out var failure))
        {
            SendFailure(args.SenderSession, request.SessionId, failure);
            return;
        }
        if (!TryComp<NCSurgicalSiteComponent>(session.Region, out var site) ||
            request.ExpectedRevision != site.SpatialRevision ||
            !session.AttachmentHandles.TryGetValue(request.Handle, out var tool) ||
            !TryComp<NCFreeformSurgicalAttachmentComponent>(tool, out var attachment) ||
            attachment.Patient != session.Patient || attachment.Region != session.Region ||
            !_containers.TryGetContainingContainer((tool, null, null), out var containing) ||
            !_containers.Remove(tool, containing, destination: Transform(session.Patient).Coordinates))
        {
            SendState(surgeon, session, args.SenderSession, false, NCFreeformSurgeryFailure.AttachmentUnavailable);
            return;
        }

        if (attachment.Action == NCFreeformSurgeryAction.DrainCavity)
            _cavityFluid.TrySetDrainRate(session.Patient, session.Region, 0f);
        if (attachment.Action == NCFreeformSurgeryAction.Retract &&
            FindIncision(site, attachment.IncisionId) is { } incision &&
            !containing.ContainedEntities.Any(contained =>
                contained != tool &&
                TryComp<NCFreeformSurgicalAttachmentComponent>(contained, out var other) &&
                other.Patient == session.Patient && other.Region == session.Region &&
                other.IncisionId == attachment.IncisionId &&
                other.Action == NCFreeformSurgeryAction.Retract))
        {
            incision.EdgeSeparation = 0f;
            incision.ModifiedAt = _timing.CurTime;
            site.TissuesRetracted = site.Incisions.Any(value => !value.Sutured && value.EdgeSeparation >= 0.2f);
        }
        RemComp<NCFreeformSurgicalAttachmentComponent>(tool);
        _hands.TryPickupAnyHand(surgeon, tool);
        unchecked
        {
            site.SpatialRevision++;
        }
        MarkSiteChanged(session.Patient);
        SendState(surgeon, session, args.SenderSession);
    }

    private void OnAttachmentShutdown(
        Entity<NCFreeformSurgicalAttachmentComponent> attachment,
        ref ComponentShutdown args)
    {
        // Runtime attachment state is deliberately not reconstructed from character persistence. Deleting
        // a real device therefore removes its ongoing physical effect just like detaching it normally.
        if (TerminatingOrDeleted(attachment.Comp.Patient) ||
            TerminatingOrDeleted(attachment.Comp.Region))
        {
            return;
        }

        if (attachment.Comp.Action == NCFreeformSurgeryAction.DrainCavity)
            _cavityFluid.TrySetDrainRate(attachment.Comp.Patient, attachment.Comp.Region, 0f);
        if (attachment.Comp.Action != NCFreeformSurgeryAction.Retract ||
            !TryComp<NCSurgicalSiteComponent>(attachment.Comp.Region, out var site) ||
            FindIncision(site, attachment.Comp.IncisionId) is not { } incision ||
            incision.EdgeSeparation < 0.2f)
        {
            return;
        }

        var hasOtherRetractor = _containers.TryGetContainer(
            attachment.Comp.Patient,
            AttachmentContainerId,
            out var container) && container.ContainedEntities.Any(contained =>
                contained != attachment.Owner &&
                TryComp<NCFreeformSurgicalAttachmentComponent>(contained, out var other) &&
                other.Region == attachment.Comp.Region && other.IncisionId == attachment.Comp.IncisionId &&
                other.Action == NCFreeformSurgeryAction.Retract);
        if (hasOtherRetractor)
            return;

        incision.EdgeSeparation = 0f;
        incision.ModifiedAt = _timing.CurTime;
        site.TissuesRetracted = site.Incisions.Any(value => !value.Sutured && value.EdgeSeparation >= 0.2f);
        unchecked
        {
            site.SpatialRevision++;
        }
        MarkSiteChanged(attachment.Comp.Patient);
    }

    private void OnClose(NCFreeformSurgeryCloseRequest request, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is not { } surgeon ||
            !TryComp<NCFreeformSurgerySessionComponent>(surgeon, out var session) ||
            session.SessionId != request.SessionId)
        {
            return;
        }
        RemCompDeferred<NCFreeformSurgerySessionComponent>(surgeon);
    }

    private bool TryResolveSession(
        NCActionResolutionId sessionId,
        EntitySessionEventArgs args,
        out EntityUid surgeon,
        out NCFreeformSurgerySessionComponent session,
        out NCFreeformSurgeryProfilePrototype profile,
        out NCFreeformSurgeryFailure failure)
    {
        surgeon = default;
        session = default!;
        profile = default!;
        failure = NCFreeformSurgeryFailure.SessionUnavailable;
        if (!sessionId.IsValid || args.SenderSession.AttachedEntity is not { } attached ||
            !TryComp<NCFreeformSurgerySessionComponent>(attached, out var resolvedSession) ||
            resolvedSession.SessionId != sessionId)
        {
            return false;
        }
        session = resolvedSession;
        surgeon = attached;
        if (!TryComp<NCMedicalBodyComponent>(session.Patient, out var medical) ||
            TerminatingOrDeleted(session.Patient))
        {
            failure = NCFreeformSurgeryFailure.PatientUnavailable;
            return false;
        }
        if (!_interaction.InRangeUnobstructed(surgeon, session.Patient))
        {
            failure = NCFreeformSurgeryFailure.OutOfRange;
            return false;
        }
        if (!TryKeepSessionAnchored(surgeon, session))
        {
            failure = NCFreeformSurgeryFailure.InvalidRegion;
            return false;
        }
        if (!_prototypes.TryIndex(medical.Profile, out NCAnatomyProfilePrototype? anatomy) ||
            !_prototypes.TryIndex(anatomy.SurgicalField, out NCFreeformSurgeryProfilePrototype? resolvedProfile))
        {
            failure = NCFreeformSurgeryFailure.PatientUnavailable;
            return false;
        }
        profile = resolvedProfile;
        failure = NCFreeformSurgeryFailure.None;
        return true;
    }

    private bool TryKeepSessionAnchored(EntityUid surgeon, NCFreeformSurgerySessionComponent session)
    {
        if (TryComp<OrganComponent>(session.Region, out var regionOrgan) &&
            regionOrgan.Body == session.Patient)
        {
            return true;
        }

        // An amputation can remove the region while its stroke is in flight. Rebind through the configured
        // zone fallback chain instead of allowing later requests to mutate the detached anatomical entity.
        if (!TryResolveFieldRegion(
                surgeon,
                session.Patient,
                session.TargetZone,
                out var reboundZone,
                out var reboundRegion))
        {
            return false;
        }

        session.TargetZone = reboundZone;
        session.Region = reboundRegion;
        session.StructureHandles.Clear();
        session.AttachmentHandles.Clear();
        return true;
    }

    private void SendState(
        EntityUid surgeon,
        NCFreeformSurgerySessionComponent session,
        ICommonSession recipient,
        bool accepted = true,
        NCFreeformSurgeryFailure failure = NCFreeformSurgeryFailure.None)
    {
        if (!TryComp<NCMedicalBodyComponent>(session.Patient, out var medical) ||
            !_prototypes.TryIndex(medical.Profile, out NCAnatomyProfilePrototype? anatomy) ||
            !_prototypes.TryIndex(anatomy.SurgicalField, out NCFreeformSurgeryProfilePrototype? profile) ||
            !TryComp<NCSurgicalSiteComponent>(session.Region, out var site))
        {
            SendFailure(recipient, session.SessionId, NCFreeformSurgeryFailure.PatientUnavailable);
            return;
        }

        var capabilities = BuildCapabilities(
            surgeon,
            session,
            profile,
            out var toolName,
            out var toolPrototype,
            out var sterility);
        var layers = BuildLayers(session.Region, site, session);
        var structures = BuildStructures(surgeon, session, profile, site);
        var incisions = BuildIncisions(session);
        var wounds = BuildWounds(session);
        var attachments = BuildAttachments(session);
        TryComp<NCCirculationComponent>(session.Patient, out var circulation);
        TryComp<NCPhysiologyComponent>(session.Patient, out var physiology);

        RaiseNetworkEvent(new NCFreeformSurgeryState(
            session.SessionId,
            site.SpatialRevision,
            session.LastSequence,
            accepted,
            failure,
            medical.Profile,
            GetNetEntity(session.Patient),
            session.TargetZone,
            session.Surface,
            Name(session.Patient),
            toolName,
            toolPrototype,
            sterility,
            physiology?.Condition ?? NCPhysiologicalCondition.Stable,
            physiology?.Consciousness ?? NCConsciousnessState.Alert,
            physiology?.Oxygenation ?? 1f,
            circulation?.Perfusion ?? 1f,
            physiology?.TotalPain ?? 0f,
            physiology?.Shock ?? 0f,
            ResolveMovement(session.Patient),
            session.Depth,
            site.Contamination,
            site.AccessDepth,
            capabilities,
            layers,
            structures,
            incisions,
            wounds,
            attachments), recipient);
    }

    private NCFreeformSurgeryCapabilitySummary[] BuildCapabilities(
        EntityUid surgeon,
        NCFreeformSurgerySessionComponent session,
        NCFreeformSurgeryProfilePrototype profile,
        out string toolName,
        out string toolPrototype,
        out float sterility)
    {
        toolName = Loc.GetString("nc-medical-freeform-surgery-empty-hand");
        toolPrototype = string.Empty;
        sterility = 0f;
        if (!_hands.TryGetActiveItem(surgeon, out var held) || held is not { } tool ||
            !TryComp<NCSurgeryToolComponent>(tool, out var surgicalTool))
        {
            session.SelectedCapability = null;
            return [];
        }

        toolName = Name(tool);
        toolPrototype = MetaData(tool).EntityPrototype?.ID ?? string.Empty;
        sterility = surgicalTool.Sterility;
        var available = surgicalTool.Capabilities.Keys
            .Where(profile.Tools.ContainsKey)
            .OrderBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
        if (session.SelectedCapability is not { } selected || !available.Contains(selected))
            session.SelectedCapability = available.FirstOrDefault();

        return available.Select(capability =>
        {
            var definition = profile.Tools[capability];
            var name = _prototypes.TryIndex(capability, out NCSurgeryToolCapabilityPrototype? prototype)
                ? prototype.Name
                : capability.Id;
            return new NCFreeformSurgeryCapabilitySummary(
                capability,
                name,
                _prototypes.TryIndex(capability, out NCSurgeryToolCapabilityPrototype? toolPrototype)
                    ? toolPrototype.RepresentativeTool.Id
                    : string.Empty,
                definition.Action,
                session.SelectedCapability == capability,
                definition.Attachable);
        }).ToArray();
    }

    private NCFreeformSurgeryLayerSummary[] BuildLayers(
        EntityUid region,
        NCSurgicalSiteComponent site,
        NCFreeformSurgerySessionComponent session)
    {
        if (!TryComp<NCAnatomicalRegionComponent>(region, out var anatomical) ||
            !_prototypes.TryIndex(anatomical.TissueProfile, out NCTissueProfilePrototype? profile) ||
            profile.Layers.Count == 0)
        {
            return [];
        }

        var result = new NCFreeformSurgeryLayerSummary[profile.Layers.Count];
        var step = 1f / profile.Layers.Count;
        for (var i = 0; i < profile.Layers.Count; i++)
        {
            var start = i * step;
            var end = (i + 1) * step;
            var exposed = i == 0 || site.Incisions.Any(incision =>
                incision.TargetZone == session.TargetZone && incision.Surface == session.Surface &&
                !incision.Sutured && incision.Depth >= start);
            result[i] = new NCFreeformSurgeryLayerSummary(profile.Layers[i].Kind, start, end, exposed);
        }
        return result;
    }

    private NCFreeformSurgeryStructureSummary[] BuildStructures(
        EntityUid surgeon,
        NCFreeformSurgerySessionComponent session,
        NCFreeformSurgeryProfilePrototype profile,
        NCSurgicalSiteComponent site)
    {
        session.StructureHandles.Clear();
        var candidates = CollectStructureCandidates(session, profile, true)
            .OrderBy(uid => StructureSortKey(uid), StringComparer.Ordinal)
            .ThenBy(uid => uid.Id)
            .ToArray();
        var result = new List<NCFreeformSurgeryStructureSummary>(candidates.Length);
        var handle = 1;
        foreach (var candidate in candidates)
        {
            if (!TryGetStructureDefinition(candidate, session, profile, true, out var definition))
                continue;
            var exposed = candidate == session.Region || IsExposed(site, session, definition);
            if (!exposed)
                continue;
            session.StructureHandles.Add(handle, candidate);
            var integrity = TryComp<NCOrganIntegrityComponent>(candidate, out var integrityComp)
                ? integrityComp.CurrentIntegrity / MathF.Max(1f, integrityComp.MaxIntegrity)
                : 1f;
            var function = TryComp<NCOrganFunctionComponent>(candidate, out var functionComp)
                ? functionComp.CurrentCapacity / MathF.Max(0.001f, functionComp.BaseCapacity)
                : integrity;
            result.Add(new NCFreeformSurgeryStructureSummary(
                handle,
                Name(candidate),
                StructureKind(candidate),
                definition.Visual,
                definition.Position,
                definition.Size,
                definition.MinimumDepth,
                Math.Clamp(integrity, 0f, 1f),
                Math.Clamp(function, 0f, 1f),
                true));
            handle++;
        }

        // A staged implant is not yet part of the body graph. Once its host bay is locally exposed, disclose
        // a synthetic atlas target backed by the real host organ so the player has a precise installation area.
        if (TryResolveStagedCyberwareTarget(surgeon, session, profile, out var target, out var targetName) &&
            IsExposed(site, session, target))
        {
            session.StructureHandles.Add(handle, session.Region);
            result.Add(new NCFreeformSurgeryStructureSummary(
                handle,
                targetName,
                NCFreeformSurgeryStructureKind.Cyberware,
                target.Visual,
                target.Position,
                target.Size,
                target.MinimumDepth,
                1f,
                1f,
                true));
        }
        return result.ToArray();
    }

    private NCFreeformSurgeryIncisionSummary[] BuildIncisions(NCFreeformSurgerySessionComponent session)
    {
        if (!TryComp<BodyComponent>(session.Patient, out var body) || body.Organs == null)
            return [];

        // The body organ container bounds this query to one patient. Sending every visible-side incision keeps
        // old surgical traces stable while the operator works on another region.
        var result = new List<(TimeSpan CreatedAt, NCFreeformSurgeryIncisionSummary Summary)>();
        foreach (var region in body.Organs.ContainedEntities.Take(MaximumBodyOrgans))
        {
            if (!TryComp<NCSurgicalSiteComponent>(region, out var site))
                continue;
            foreach (var incision in site.Incisions)
            {
                if (incision.Surface != session.Surface)
                    continue;
                result.Add((
                    incision.CreatedAt,
                    new NCFreeformSurgeryIncisionSummary(
                        incision.IncisionId,
                        incision.TargetZone,
                        incision.Polyline.ToArray(),
                        incision.Depth,
                        incision.Width,
                        incision.EdgeSeparation,
                        incision.Contamination,
                        incision.Sutured)));
            }
        }

        return result
            .OrderBy(value => value.CreatedAt)
            .ThenBy(value => value.Summary.IncisionId)
            .Select(value => value.Summary)
            .ToArray();
    }

    private NCFreeformSurgeryWoundSummary[] BuildWounds(NCFreeformSurgerySessionComponent session)
    {
        if (!TryComp<NCWoundContainerComponent>(session.Patient, out var container))
            return [];
        return container.Wounds.Values
            .Where(wound => wound.Surface == session.Surface)
            .OrderBy(wound => wound.CreatedAt)
            .ThenBy(wound => wound.WoundId)
            .Select(wound => new NCFreeformSurgeryWoundSummary(
                wound.TargetZone,
                new Vector2(wound.VisualX / 255f, wound.VisualY / 255f),
                wound.WoundType,
                wound.Severity,
                wound.TissueDepth,
                wound.ExternalBleedRate,
                wound.InternalBleedRate,
                wound.EmbeddedObjectId != null))
            .ToArray();
    }

    private NCFreeformSurgeryAttachmentSummary[] BuildAttachments(NCFreeformSurgerySessionComponent session)
    {
        session.AttachmentHandles.Clear();
        if (!_containers.TryGetContainer(session.Patient, AttachmentContainerId, out var container))
            return [];
        var result = new List<NCFreeformSurgeryAttachmentSummary>();
        var handle = 1;
        foreach (var tool in container.ContainedEntities.OrderBy(uid => uid.Id))
        {
            if (!TryComp<NCFreeformSurgicalAttachmentComponent>(tool, out var attachment) ||
                attachment.Region != session.Region)
            {
                continue;
            }
            session.AttachmentHandles.Add(handle, tool);
            result.Add(new NCFreeformSurgeryAttachmentSummary(
                handle,
                Name(tool),
                attachment.Position,
                attachment.Action));
            handle++;
        }
        return result.ToArray();
    }

    private EntityUid? ResolveStructureAt(
        EntityUid surgeon,
        NCFreeformSurgerySessionComponent session,
        NCFreeformSurgeryProfilePrototype profile,
        NCSurgicalSiteComponent site,
        Vector2 point,
        float depth,
        bool requireExposure)
    {
        EntityUid? best = null;
        var bestDepth = -1f;
        if (TryResolveStagedCyberwareTarget(surgeon, session, profile, out var staged, out _) &&
            depth >= staged.MinimumDepth && Contains(staged, point) &&
            (!requireExposure || IsExposed(site, session, staged)))
        {
            best = session.Region;
            bestDepth = staged.MinimumDepth;
        }
        foreach (var candidate in CollectStructureCandidates(session, profile, false))
        {
            if (!TryGetStructureDefinition(candidate, session, profile, false, out var definition) ||
                depth < definition.MinimumDepth ||
                requireExposure && !IsExposed(site, session, definition))
            {
                continue;
            }
            var half = definition.Size * 0.5f;
            if (point.X < definition.Position.X - half.X || point.X > definition.Position.X + half.X ||
                point.Y < definition.Position.Y - half.Y || point.Y > definition.Position.Y + half.Y ||
                definition.MinimumDepth < bestDepth)
            {
                continue;
            }
            best = candidate;
            bestDepth = definition.MinimumDepth;
        }
        return best;
    }

    private bool HasStagedInternalCyberware(EntityUid surgeon, NCFreeformSurgerySessionComponent session)
    {
        return TryComp<NCSurgeryGameplaySelectionComponent>(surgeon, out var selection) &&
               selection.Patient == session.Patient && selection.Graft is { } module &&
               HasComp<NCCyberwareComponent>(module) &&
               !HasComp<NCDetachedOrganMedicalDataComponent>(module);
    }

    private bool TryResolveStagedCyberwareTarget(
        EntityUid surgeon,
        NCFreeformSurgerySessionComponent session,
        NCFreeformSurgeryProfilePrototype profile,
        out NCFreeformSurgeryStructureDefinition definition,
        out string name)
    {
        definition = default!;
        name = string.Empty;
        if (!HasStagedInternalCyberware(surgeon, session) ||
            !TryComp<NCSurgeryGameplaySelectionComponent>(surgeon, out var selection) ||
            selection.Graft is not { } module ||
            !_cyberware.TryFindInstallSlot(session.Patient, session.Region, module, out var slot) ||
            !profile.CyberwareSlots.TryGetValue(slot, out definition!) ||
            !_prototypes.TryIndex(slot, out NCCyberwareSlotPrototype? slotPrototype))
        {
            return false;
        }

        name = $"{slotPrototype.Name} — implant target";
        return true;
    }

    private static bool Contains(NCFreeformSurgeryStructureDefinition definition, Vector2 point)
    {
        var half = definition.Size * 0.5f;
        return point.X >= definition.Position.X - half.X && point.X <= definition.Position.X + half.X &&
               point.Y >= definition.Position.Y - half.Y && point.Y <= definition.Position.Y + half.Y;
    }

    private List<EntityUid> CollectStructureCandidates(
        NCFreeformSurgerySessionComponent session,
        NCFreeformSurgeryProfilePrototype profile,
        bool includeRegion)
    {
        var result = new List<EntityUid>();
        if (!TryComp<BodyComponent>(session.Patient, out var body) || body.Organs == null)
            return result;

        var cyberwareCount = 0;
        foreach (var candidate in body.Organs.ContainedEntities.Take(MaximumBodyOrgans))
        {
            if (candidate != session.Region && !IsDescendantOf(candidate, session.Region))
                continue;
            if ((includeRegion || candidate != session.Region) &&
                TryGetStructureDefinition(candidate, session, profile, includeRegion, out _))
            {
                result.Add(candidate);
            }

            if (!TryComp<NCCyberwareSlotHostComponent>(candidate, out var host) || host.Modules == null)
                continue;
            foreach (var module in host.Modules.ContainedEntities.OrderBy(uid => uid.Id))
            {
                if (cyberwareCount++ >= MaximumCyberwarePerRegion)
                    break;
                if (TryGetStructureDefinition(module, session, profile, false, out _))
                    result.Add(module);
            }
        }

        return result;
    }

    private bool TryGetStructureDefinition(
        EntityUid candidate,
        NCFreeformSurgerySessionComponent session,
        NCFreeformSurgeryProfilePrototype profile,
        bool allowRegionFallback,
        out NCFreeformSurgeryStructureDefinition definition)
    {
        if (TryComp<OrganComponent>(candidate, out var organ) && organ.Category is { } category &&
            profile.Structures.TryGetValue(category, out definition!))
        {
            return true;
        }
        if (TryComp<NCCyberwareComponent>(candidate, out var cyberware) &&
            cyberware.InstalledSlot is { } slot && profile.CyberwareSlots.TryGetValue(slot, out definition!))
        {
            return true;
        }
        if (allowRegionFallback && candidate == session.Region)
        {
            definition = new NCFreeformSurgeryStructureDefinition
            {
                Position = new Vector2(0.5f, 0.5f),
                Size = new Vector2(0.94f, 0.94f),
                MinimumDepth = 0f,
                Visual = NCFreeformSurgeryStructureVisual.Region,
            };
            return true;
        }

        definition = default!;
        return false;
    }

    private string StructureSortKey(EntityUid uid)
    {
        if (TryComp<OrganComponent>(uid, out var organ) && organ.Category is { } category)
            return $"0:{category.Id}";
        if (TryComp<NCCyberwareComponent>(uid, out var cyberware) && cyberware.InstalledSlot is { } slot)
            return $"1:{slot.Id}:{cyberware.Profile.Id}";
        return "2:";
    }

    private static bool IsExposed(
        NCSurgicalSiteComponent site,
        NCFreeformSurgerySessionComponent session,
        NCFreeformSurgeryStructureDefinition structure)
    {
        var half = structure.Size * 0.5f;
        return site.Incisions.Any(incision =>
            incision.TargetZone == session.TargetZone && incision.Surface == session.Surface &&
            !incision.Sutured && incision.Depth >= structure.MinimumDepth &&
            PolylineIntersectsBox(
                incision.Polyline,
                structure.Position - half,
                structure.Position + half));
    }

    private static bool PolylineIntersectsBox(
        IReadOnlyList<Vector2> polyline,
        Vector2 minimum,
        Vector2 maximum)
    {
        if (polyline.Count == 0)
            return false;
        for (var index = 0; index < polyline.Count; index++)
        {
            var point = polyline[index];
            if (point.X >= minimum.X && point.X <= maximum.X &&
                point.Y >= minimum.Y && point.Y <= maximum.Y)
            {
                return true;
            }
            if (index == 0 || !SegmentIntersectsBox(polyline[index - 1], point, minimum, maximum))
                continue;
            return true;
        }
        return false;
    }

    private static bool SegmentIntersectsBox(Vector2 start, Vector2 end, Vector2 minimum, Vector2 maximum)
    {
        var direction = end - start;
        var enter = 0f;
        var exit = 1f;
        return ClipAxis(start.X, direction.X, minimum.X, maximum.X, ref enter, ref exit) &&
               ClipAxis(start.Y, direction.Y, minimum.Y, maximum.Y, ref enter, ref exit);
    }

    private static bool ClipAxis(
        float origin,
        float direction,
        float minimum,
        float maximum,
        ref float enter,
        ref float exit)
    {
        if (MathF.Abs(direction) <= 0.000001f)
            return origin >= minimum && origin <= maximum;
        var first = (minimum - origin) / direction;
        var second = (maximum - origin) / direction;
        if (first > second)
            (first, second) = (second, first);
        enter = MathF.Max(enter, first);
        exit = MathF.Min(exit, second);
        return enter <= exit;
    }

    private bool IsDescendantOf(EntityUid candidate, EntityUid parent)
    {
        var current = candidate;
        var visited = 0;
        while (visited++ < MaximumBodyOrgans && TryComp<ChildOrganComponent>(current, out var child) &&
               child.Parent is { } next)
        {
            if (next == parent)
                return true;
            current = next;
        }
        return false;
    }

    private NCFreeformSurgeryStructureKind StructureKind(EntityUid uid)
    {
        if (HasComp<NCCyberwareComponent>(uid))
            return NCFreeformSurgeryStructureKind.Cyberware;
        if (HasComp<NCBoneComponent>(uid))
            return NCFreeformSurgeryStructureKind.Bone;
        if (HasComp<NCTendonComponent>(uid))
            return NCFreeformSurgeryStructureKind.Tendon;
        if (HasComp<NCNerveComponent>(uid) && !HasComp<NCOrganFunctionComponent>(uid))
            return NCFreeformSurgeryStructureKind.Nerve;
        return NCFreeformSurgeryStructureKind.Organ;
    }

    private bool TryResolveCapability(
        NCFreeformSurgerySessionComponent session,
        NCFreeformSurgeryProfilePrototype profile,
        NCSurgeryToolComponent tool,
        out ProtoId<NCSurgeryToolCapabilityPrototype> capability,
        out NCFreeformSurgeryToolDefinition definition,
        out float quality)
    {
        capability = default;
        definition = default!;
        quality = 0f;
        var candidates = tool.Capabilities
            .Where(pair => profile.Tools.ContainsKey(pair.Key) && float.IsFinite(pair.Value) && pair.Value > 0f)
            .OrderBy(pair => pair.Key.Id, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0)
            return false;
        var selected = session.SelectedCapability is { } selectedCapability
            ? candidates.FirstOrDefault(pair => pair.Key == selectedCapability)
            : default;
        var pair = selected.Key.Id != null ? selected : candidates[0];
        capability = pair.Key;
        definition = profile.Tools[pair.Key];
        quality = pair.Value;
        return true;
    }

    private float ResolveMovement(EntityUid patient)
    {
        if (TryComp<NCSurgeryPatientStateComponent>(patient, out var prepared))
            return Math.Clamp(prepared.Movement, 0f, 1f);
        var modifiers = _pharmacology.GetModifiers(patient);
        var consciousness = TryComp<NCPhysiologyComponent>(patient, out var physiology)
            ? physiology.Consciousness
            : NCConsciousnessState.Alert;
        if (consciousness >= NCConsciousnessState.Unconscious)
            return 0f;
        return Math.Clamp((1f - modifiers.Anesthesia) * modifiers.MotorControlMultiplier, 0f, 1f);
    }

    private static Vector2[] ApplyPatientMotion(
        Vector2[] source,
        NCFreeformSurgerySessionComponent session,
        NCFreeformSurgeryProfilePrototype profile,
        float movement)
    {
        var result = new Vector2[source.Length];
        var magnitude = Math.Clamp(movement, 0f, 1f) * profile.MovementDisplacement;
        var angle = (session.LastSequence + 1) * 2.3999632f;
        var displacement = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * magnitude;
        for (var i = 0; i < source.Length; i++)
            result[i] = Vector2.Clamp(source[i] + displacement, Vector2.Zero, Vector2.One);
        return result;
    }

    private static bool ValidateStroke(
        NCFreeformSurgeryStrokeRequest request,
        NCFreeformSurgeryProfilePrototype profile)
    {
        if (!Enum.IsDefined(request.Surface) || !float.IsFinite(request.Depth) ||
            request.Depth is < 0f or > 1f || request.Points.Length is < 2 ||
            request.Points.Length > profile.MaximumStrokePoints)
        {
            return false;
        }
        var length = 0f;
        for (var i = 0; i < request.Points.Length; i++)
        {
            if (!FiniteNormalized(request.Points[i]))
                return false;
            if (i > 0)
                length += Vector2.Distance(request.Points[i - 1], request.Points[i]);
        }
        return float.IsFinite(length) && length >= profile.MinimumStrokeLength && length <= profile.MaximumStrokeLength;
    }

    private static bool FiniteNormalized(Vector2 point)
    {
        return float.IsFinite(point.X) && float.IsFinite(point.Y) &&
               point.X is >= 0f and <= 1f && point.Y is >= 0f and <= 1f;
    }

    private static Vector2 StrokeCenter(IReadOnlyList<Vector2> points)
    {
        var sum = Vector2.Zero;
        foreach (var point in points)
            sum += point;
        return sum / Math.Max(1, points.Count);
    }

    private static NCSurgicalIncisionRecord? FindIncision(
        NCSurgicalSiteComponent site,
        NCFreeformSurgerySessionComponent session,
        Vector2 point,
        float radius)
    {
        NCSurgicalIncisionRecord? best = null;
        var bestDistance = radius * radius;
        foreach (var incision in site.Incisions)
        {
            if (incision.TargetZone != session.TargetZone || incision.Surface != session.Surface ||
                incision.Polyline.Count == 0)
            {
                continue;
            }
            var distance = DistanceSquaredToPolyline(point, incision.Polyline);
            if (distance > bestDistance)
                continue;
            bestDistance = distance;
            best = incision;
        }
        return best;
    }

    private static NCSurgicalIncisionRecord? FindIncision(NCSurgicalSiteComponent site, Guid incisionId)
    {
        foreach (var incision in site.Incisions)
        {
            if (incision.IncisionId == incisionId)
                return incision;
        }
        return null;
    }

    private static float DistanceSquaredToPolyline(Vector2 point, IReadOnlyList<Vector2> polyline)
    {
        if (polyline.Count == 0)
            return float.MaxValue;
        if (polyline.Count == 1)
            return Vector2.DistanceSquared(point, polyline[0]);

        // Incisions are bounded to the profile's small point limit, so exact segment distance is cheap and
        // prevents long cuts from being interactable only around their arithmetic centre.
        var best = float.MaxValue;
        for (var index = 1; index < polyline.Count; index++)
        {
            var start = polyline[index - 1];
            var segment = polyline[index] - start;
            var lengthSquared = segment.LengthSquared();
            var fraction = lengthSquared <= 0.000001f
                ? 0f
                : Math.Clamp(Vector2.Dot(point - start, segment) / lengthSquared, 0f, 1f);
            best = MathF.Min(best, Vector2.DistanceSquared(point, start + segment * fraction));
        }
        return best;
    }

    private void MarkSiteChanged(EntityUid patient)
    {
        _medicalBodies.TryMarkDirty(
            (patient, Comp<NCMedicalBodyComponent>(patient)),
            NCMedicalDirtyDomains.Wounds |
            NCMedicalDirtyDomains.Vascular |
            NCMedicalDirtyDomains.Diagnostics |
            NCMedicalDirtyDomains.Persistence);
    }

    private void SendFailure(
        ICommonSession recipient,
        NCActionResolutionId sessionId,
        NCFreeformSurgeryFailure failure)
    {
        RaiseNetworkEvent(new NCFreeformSurgeryState(
            sessionId,
            0,
            0,
            false,
            failure,
            default,
            NetEntity.Invalid,
            default,
            NCWoundSurface.Front,
            string.Empty,
            string.Empty,
            string.Empty,
            0f,
            NCPhysiologicalCondition.Stable,
            NCConsciousnessState.Alert,
            0f,
            0f,
            0f,
            0f,
            0f,
            0f,
            0f,
            NCSurgicalAccessDepth.Closed,
            [],
            [],
            [],
            [],
            [],
            []), recipient);
    }
}
