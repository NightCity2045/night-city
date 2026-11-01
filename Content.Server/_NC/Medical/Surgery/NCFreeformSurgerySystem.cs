// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using Content.Server._NC.Medical.Bleeding;
using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Medical.Pharmacology;
using Content.Server._NC.Medical.Wounds;
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
using Content.Shared.DoAfter;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Verbs;
using Robust.Shared.Containers;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Surgery;

/// <summary>
/// Server-authoritative interactive player surgery. Clients submit only one normalized contact point and opaque
/// session handles; the server authors geometry and re-reads the held item, anatomy and resources per mutation.
/// </summary>
public sealed partial class NCFreeformSurgerySystem : EntitySystem
{
    private const string AttachmentContainerId = "nc-freeform-surgical-attachments";
    private const int MaximumBodyOrgans = 96;
    private const int MaximumCyberwarePerRegion = 16;

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private NCAnatomySystem _anatomy = default!;
    [Dependency] private NCWoundSystem _wounds = default!;
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
        SubscribeNetworkEvent<NCFreeformSurgerySiteRequest>(OnSite);
        SubscribeNetworkEvent<NCInteractiveSurgeryActionRequest>(OnInteraction);
        SubscribeLocalEvent<NCSurgeryToolComponent, NCFreeformSurgeryDoAfterEvent>(OnInteractionDoAfter);
        SubscribeNetworkEvent<NCFreeformSurgeryAttachRequest>(OnAttach);
        SubscribeLocalEvent<NCSurgeryToolComponent, NCFreeformSurgeryAttachDoAfterEvent>(OnAttachDoAfter);
        SubscribeNetworkEvent<NCFreeformSurgeryDetachRequest>(OnDetach);
        SubscribeLocalEvent<NCFreeformSurgicalAttachmentComponent, NCFreeformSurgeryDetachDoAfterEvent>(OnDetachDoAfter);
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

        // Staging a graft or clicking another instrument reopens this same field. Preserve the token and
        // pending DoAfters instead of invalidating requests already sent by the surgeon's window.
        if (TryComp<NCFreeformSurgerySessionComponent>(surgeon, out var existing) &&
            existing.Patient == patient && existing.SessionId.IsValid)
        {
            SendState(surgeon, existing, actor.PlayerSession);
            return true;
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
        session.ActiveIncisionId = null;
        // NC: There is no manual front/rear selector. Healthy-skin work starts on the body side physically
        // facing the surgeon; clicking an existing wound later supplies that wound's immutable impact surface.
        session.Surface = ResolveAccessibleSurface(surgeon, patient);
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
        if (session.ActiveIncisionId == null)
            session.Surface = ResolveAccessibleSurface(surgeon, session.Patient);
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

        // NC: Selection packets now carry only slider state. Surface selection is derived from world position or
        // the authoritative wound/incision hit and cannot be changed by the removed direction buttons.
        var changedSite = session.TargetZone != request.Zone || session.Region != region;
        session.TargetZone = request.Zone;
        session.Region = region;
        if (changedSite)
            session.ActiveIncisionId = null;
        session.Depth = Math.Clamp(request.Depth, 0f, 1f);
        session.StructureHandles.Clear();
        session.AttachmentHandles.Clear();
        SendState(surgeon, session, args.SenderSession);
    }

    /// <summary>
    /// NC: Binds the magnified view to one server-owned incision. The client can identify a visible incision by
    /// its opaque id, but cannot redirect the session to an unrelated organ or fabricate an operative opening.
    /// </summary>
    private void OnSite(NCFreeformSurgerySiteRequest request, EntitySessionEventArgs args)
    {
        if (!TryResolveSession(request.SessionId, args, out var surgeon, out var session, out _, out var failure))
        {
            SendFailure(args.SenderSession, request.SessionId, failure);
            return;
        }

        if (!Enum.IsDefined(request.Surface) ||
            !_prototypes.TryIndex(request.Zone, out NCTargetZonePrototype? requested) || !requested.Selectable ||
            !TryResolveFieldRegion(surgeon, session.Patient, request.Zone, out _, out var region) ||
            !TryComp<NCSurgicalSiteComponent>(region, out var site))
        {
            SendState(surgeon, session, args.SenderSession, false, NCFreeformSurgeryFailure.InvalidRegion);
            return;
        }

        if (request.IncisionId is { } incisionId)
        {
            var incision = FindIncision(site, incisionId);
            if (incision == null || incision.TargetZone != request.Zone || incision.Surface != request.Surface)
            {
                SendState(surgeon, session, args.SenderSession, false, NCFreeformSurgeryFailure.NoApplicableTarget);
                return;
            }
        }

        session.TargetZone = request.Zone;
        session.Region = region;
        session.Surface = request.Surface;
        session.ActiveIncisionId = request.IncisionId;
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

    private void OnInteraction(NCInteractiveSurgeryActionRequest request, EntitySessionEventArgs args)
    {
        if (!TryResolveSession(request.SessionId, args, out var surgeon, out var session, out var profile, out var failure))
        {
            SendFailure(args.SenderSession, request.SessionId, failure);
            return;
        }
        if (!ValidateInteraction(request) ||
            !_hands.TryGetActiveItem(surgeon, out var held) || held is not { } tool ||
            !TryComp<NCSurgeryToolComponent>(tool, out var surgicalTool) || surgicalTool.Broken ||
            !TryResolveCapability(session, profile, surgicalTool, out var capability, out var definition, out _))
        {
            SendState(surgeon, session, args.SenderSession, false, NCFreeformSurgeryFailure.ToolUnavailable);
            return;
        }

        var pending = new NCFreeformSurgeryDoAfterEvent(request, capability);
        if (request.StructureHandle is { } handle && session.StructureHandles.TryGetValue(handle, out var structure))
            pending.ExpectedStructure = GetNetEntity(structure);
        if (definition.Action is NCFreeformSurgeryAction.Cyberware or NCFreeformSurgeryAction.Transplant &&
            TryComp<NCSurgeryGameplaySelectionComponent>(surgeon, out var selection) &&
            selection.Patient == session.Patient && selection.Graft is { } graft)
            pending.ExpectedGraft = GetNetEntity(graft);
        if (definition.Action is NCFreeformSurgeryAction.Cyberware or NCFreeformSurgeryAction.CalibrateCyberware or
            NCFreeformSurgeryAction.DisconnectCyberware or NCFreeformSurgeryAction.RepairCyberware &&
            pending.ExpectedGraft == null && pending.ExpectedStructure == null)
        {
            SendState(surgeon, session, args.SenderSession, false, NCFreeformSurgeryFailure.NoApplicableTarget);
            return;
        }
        var configurationTarget = pending.ExpectedGraft ?? pending.ExpectedStructure;
        if (configurationTarget is { } configured &&
            TryComp<NCCyberwareAssemblyComponent>(GetEntity(configured), out var assembly))
            pending.ExpectedConfiguration = assembly.ConfigurationRevision;

        var doAfter = new DoAfterArgs(
            EntityManager,
            surgeon,
            definition.Duration,
            pending,
            tool,
            target: session.Patient,
            used: tool)
        {
            NeedHand = true,
            BreakOnMove = true,
            BreakOnDamage = true,
            DistanceThreshold = profile.InteractionRange,
        };
        if (!_doAfter.TryStartDoAfter(doAfter))
        {
            SendState(surgeon, session, args.SenderSession, false, NCFreeformSurgeryFailure.RateLimited);
        }
    }

    private void OnInteractionDoAfter(
        Entity<NCSurgeryToolComponent> tool,
        ref NCFreeformSurgeryDoAfterEvent args)
    {
        if (args.Handled || args.Used != tool.Owner)
            return;
        args.Handled = true;

        if (!TryComp<ActorComponent>(args.User, out var actor) ||
            !TryResolveSession(args.SessionId, args.User, out var session, out var profile, out var failure))
        {
            return;
        }
        if (args.Cancelled || args.Target != session.Patient ||
            !_hands.TryGetActiveItem(args.User, out var held) || held != tool.Owner || tool.Comp.Broken ||
            !TryResolveCapability(session, profile, tool.Comp, out var capability, out _, out _) ||
            capability != args.Capability)
        {
            SendState(args.User, session, actor.PlayerSession, false,
                args.Cancelled ? NCFreeformSurgeryFailure.NoApplicableTarget : NCFreeformSurgeryFailure.CapabilityUnavailable);
            return;
        }

        CompleteInteraction(args.ToRequest(), args.User, actor.PlayerSession, args);
    }

    private void CompleteInteraction(
        NCInteractiveSurgeryActionRequest request,
        EntityUid surgeon,
        ICommonSession recipient,
        NCFreeformSurgeryDoAfterEvent pending)
    {
        if (!TryResolveSession(request.SessionId, surgeon, out var session, out var profile, out var failure))
        {
            SendFailure(recipient, request.SessionId, failure);
            return;
        }

        if (_timing.CurTime - session.LastInputAt < profile.MinimumInputInterval)
        {
            SendState(surgeon, session, recipient, false, NCFreeformSurgeryFailure.RateLimited);
            return;
        }

        if (!ValidateInteraction(request) || request.Sequence != session.LastSequence + 1 ||
            !_prototypes.TryIndex(request.Zone, out NCTargetZonePrototype? requestedZone) ||
            !requestedZone.Selectable ||
            !profile.BodyZones.TryGetValue(request.Zone, out var zoneDefinition) ||
            !TryResolveFieldRegion(surgeon, session.Patient, request.Zone, out _, out var requestedRegion) ||
            !ValidateRequestedWound(session.Patient, request))
        {
            SendState(surgeon, session, recipient, false, NCFreeformSurgeryFailure.InvalidInput);
            return;
        }

        // The zone under the first stroke pixel becomes authoritative as part of the same request. A newly hit
        // region has no client-known revision yet; same-region actions retain the strict stale-state check.
        var regionChanged = request.Zone != session.TargetZone || requestedRegion != session.Region;
        var selectionChanged = regionChanged || request.Surface != session.Surface;
        if (selectionChanged)
        {
            session.TargetZone = request.Zone;
            session.Region = requestedRegion;
            session.Surface = request.Surface;
            session.ActiveIncisionId = null;
            session.StructureHandles.Clear();
            session.AttachmentHandles.Clear();
        }

        if (!TryComp<NCSurgicalSiteComponent>(session.Region, out var site) ||
            !regionChanged && request.ExpectedRevision != site.SpatialRevision)
        {
            SendState(surgeon, session, recipient, false, NCFreeformSurgeryFailure.StaleRevision);
            return;
        }

        if (!_hands.TryGetActiveItem(surgeon, out var held) || held is not { } tool ||
            !TryComp<NCSurgeryToolComponent>(tool, out var surgicalTool))
        {
            SendState(surgeon, session, recipient, false, NCFreeformSurgeryFailure.ToolUnavailable);
            return;
        }
        if (surgicalTool.Broken)
        {
            SendState(surgeon, session, recipient, false, NCFreeformSurgeryFailure.ToolBroken);
            return;
        }

        if (!TryResolveCapability(session, profile, surgicalTool, out var capability, out var definition, out var quality))
        {
            SendState(surgeon, session, recipient, false, NCFreeformSurgeryFailure.CapabilityUnavailable);
            return;
        }

        session.SelectedCapability = capability;
        // NC: A discrete tool click always reaches at least that tool's authored working layer. The depth slider
        // may request a deeper layer, but leaving it at zero must not make a retractor or clamp mysteriously fail.
        session.Depth = Math.Clamp(request.Depth, definition.MinimumDepth, definition.MaximumDepth);
        // NC: Cutting can expose, but never tunnel through, an intact skull, rib cage or pelvic casing. Clamp the
        // authoritative cut to the first data-driven protective structure; a later saw DoAfter opens that barrier.
        if (definition.Action is NCFreeformSurgeryAction.Incise or NCFreeformSurgeryAction.Autopsy &&
            !IsBoneCasingOpen(site, session) &&
            FindProtectiveBoneDepth(
                session.Patient,
                session.Region,
                session.TargetZone,
                profile) is { } protectiveDepth)
        {
            session.Depth = MathF.Min(session.Depth, protectiveDepth);
        }
        // NC: The cursor supplies only a contact point. Canonical geometry comes from the server-loaded body
        // profile, which makes every incision a discrete interaction and removes client-authored freehand cuts.
        var points = BuildInteractionPath(request.Position, zoneDefinition, definition.Action, profile);
        var contact = request.Position;
        // NC: The incision under a point-tool click becomes the active visual site before structure resolution.
        // This lets one authoritative click both operate and focus the local cavity without a UI-only target mode.
        var contactedIncision = FindOperativeSite(site, session);
        if (contactedIncision != null)
            session.ActiveIncisionId = contactedIncision.IncisionId;
        // NC: Surgical geometry is static, but structure interaction remains pixel-specific. Even a scalpel may
        // contact only a structure the current incision and bone barriers actually expose at the clicked point.
        // NC: A sprite click carries the opaque handle emitted by the preceding authoritative state. Resolve that
        // same entity after the DoAfter; never fall through to a larger overlapping organ such as a lung when the
        // player actually clicked the heart. Cyberware staging retains its dedicated target resolver below.
        var stagedCyberware = definition.Action == NCFreeformSurgeryAction.Cyberware &&
                              HasStagedInternalCyberware(surgeon, session);
        var structure = session.Depth >= definition.MinimumDepth
            ? request.StructureHandle is { } structureHandle &&
              !stagedCyberware
                ? ResolveStructureHandleAt(session, profile, site, contact, session.Depth, structureHandle)
                : ResolveStructureAt(surgeon, session, profile, site, contact, session.Depth, true)
            : null;
        if (request.StructureHandle != null &&
            !stagedCyberware &&
            structure == null)
        {
            session.LastSequence = request.Sequence;
            session.LastInputAt = _timing.CurTime;
            SendState(surgeon, session, recipient, false, NCFreeformSurgeryFailure.NoApplicableTarget);
            return;
        }
        EntityUid? graft = null;
        if (definition.Action is NCFreeformSurgeryAction.Cyberware or NCFreeformSurgeryAction.Transplant &&
            TryComp<NCSurgeryGameplaySelectionComponent>(surgeon, out var selected) && selected.Patient == session.Patient)
            graft = selected.Graft;
        var configurationTarget = graft ?? structure;
        if (pending.ExpectedStructure is { } expected && structure != GetEntity(expected) ||
            pending.ExpectedGraft != (graft is { } staged ? GetNetEntity(staged) : (NetEntity?) null) ||
            pending.ExpectedConfiguration is { } revision &&
            (configurationTarget == null || !TryComp<NCCyberwareAssemblyComponent>(configurationTarget.Value, out var assembly) ||
             assembly.ConfigurationRevision != revision))
        {
            session.LastSequence = request.Sequence;
            SendState(surgeon, session, recipient, false, NCFreeformSurgeryFailure.StaleRevision);
            return;
        }
        if (definition.Action == NCFreeformSurgeryAction.Cyberware &&
            HasStagedInternalCyberware(surgeon, session) &&
             (!TryResolveStagedCyberwareTarget(surgeon, session, profile, out var cyberwareTarget, out _) ||
              session.Depth < cyberwareTarget.MinimumDepth ||
              !Contains(cyberwareTarget, contact) ||
             !IsExposed(site, session, cyberwareTarget, profile.MinimumOpenRetraction)))
        {
            session.LastSequence = request.Sequence;
            session.LastInputAt = _timing.CurTime;
            SendState(surgeon, session, recipient, false, NCFreeformSurgeryFailure.NoApplicableTarget);
            return;
        }
        var hasBone = structure is { } structureUid && HasComp<NCBoneComponent>(structureUid);
        float? structureMinimumDepth = null;
        var structureDetachableBySeparation = false;
        var structureBlocksDeeperStructures = false;
        if (structure is { } resolvedStructure &&
            TryGetStructureDefinition(resolvedStructure, session, profile, false, out var structureDefinition))
        {
            structureMinimumDepth = structureDefinition.MinimumDepth;
            structureDetachableBySeparation = structureDefinition.DetachableBySeparation;
            structureBlocksDeeperStructures = structureDefinition.BlocksDeeperStructures;
        }
        else if (definition.Action == NCFreeformSurgeryAction.Cyberware &&
                 TryResolveStagedCyberwareTarget(surgeon, session, profile, out var stagedDefinition, out _))
        {
            structureMinimumDepth = stagedDefinition.MinimumDepth;
        }
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
            profile.MinimumOpenRetraction,
            structure,
            hasBone,
            cranial,
            profile.IncidentalTendonRuptureThreshold,
            profile.IncidentalBoneCrackThreshold,
            profile.IncidentalBoneBreakThreshold,
            contact,
            structureMinimumDepth,
            structureDetachableBySeparation,
            structureBlocksDeeperStructures,
            request.WoundId,
            new NCActionResolutionId(request.SessionId.DeriveGuid(0x53555247U, request.Sequence))));

        session.LastSequence = request.Sequence;
        session.LastInputAt = _timing.CurTime;
        if (result.Accepted)
        {
            // NC: A newly created cut and every later point-tool interaction keep the same in-body aperture
            // focused. The server still resolves the region and incision from real medical state.
            var opened = FindOperativeSite(site, session);
            if (opened != null)
                session.ActiveIncisionId = opened.IncisionId;
        }
        if (result.Accepted && !TryKeepSessionAnchored(surgeon, session))
        {
            SendFailure(recipient, session.SessionId, NCFreeformSurgeryFailure.InvalidRegion);
            return;
        }
        SendState(
            surgeon,
            session,
            recipient,
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
        if (!Enum.IsDefined(request.Surface) || !FiniteNormalized(request.Position) ||
            !_prototypes.TryIndex(request.Zone, out NCTargetZonePrototype? requestedZone) ||
            !requestedZone.Selectable ||
            !TryResolveFieldRegion(surgeon, session.Patient, request.Zone, out _, out var requestedRegion) ||
            !TryComp<NCSurgicalSiteComponent>(requestedRegion, out var site) ||
            request.Zone == session.TargetZone && requestedRegion == session.Region &&
            request.ExpectedRevision != site.SpatialRevision ||
            !_hands.TryGetActiveItem(surgeon, out var held) || held is not { } tool ||
            !TryComp<NCSurgeryToolComponent>(tool, out var surgicalTool) || surgicalTool.Broken ||
            !TryResolveCapability(session, profile, surgicalTool, out var capability, out var definition, out _) ||
            !definition.Attachable)
        {
            SendState(surgeon, session, args.SenderSession, false, NCFreeformSurgeryFailure.AttachmentUnavailable);
            return;
        }

        var incision = FindOperativeSite(site, request.Zone, request.Surface);
        if (incision == null || incision.Sutured || incision.Depth + 0.001f < definition.MinimumDepth ||
            definition.Action == NCFreeformSurgeryAction.DrainCavity &&
            !HasComp<NCCavityFluidComponent>(requestedRegion))
        {
            SendState(surgeon, session, args.SenderSession, false, NCFreeformSurgeryFailure.AttachmentUnavailable);
            return;
        }

        var doAfter = new DoAfterArgs(
            EntityManager,
            surgeon,
            definition.Duration,
            new NCFreeformSurgeryAttachDoAfterEvent(request, capability),
            tool,
            target: session.Patient,
            used: tool)
        {
            NeedHand = true,
            BreakOnMove = true,
            BreakOnDamage = true,
            DistanceThreshold = profile.InteractionRange,
        };
        if (!_doAfter.TryStartDoAfter(doAfter))
            SendState(surgeon, session, args.SenderSession, false, NCFreeformSurgeryFailure.RateLimited);
    }

    private void OnAttachDoAfter(
        Entity<NCSurgeryToolComponent> tool,
        ref NCFreeformSurgeryAttachDoAfterEvent args)
    {
        if (args.Handled || args.Used != tool.Owner)
            return;
        args.Handled = true;

        if (!TryComp<ActorComponent>(args.User, out var actor) ||
            !TryResolveSession(args.SessionId, args.User, out var session, out var profile, out _))
        {
            return;
        }
        if (args.Cancelled || args.Target != session.Patient ||
            !_hands.TryGetActiveItem(args.User, out var held) || held != tool.Owner || tool.Comp.Broken ||
            !TryResolveCapability(session, profile, tool.Comp, out var capability, out var definition, out _) ||
            capability != args.Capability || !definition.Attachable)
        {
            SendState(args.User, session, actor.PlayerSession, false, NCFreeformSurgeryFailure.AttachmentUnavailable);
            return;
        }

        CompleteAttach(args.ToRequest(), args.Capability, args.User, actor.PlayerSession);
    }

    private void CompleteAttach(
        NCFreeformSurgeryAttachRequest request,
        ProtoId<NCSurgeryToolCapabilityPrototype> expectedCapability,
        EntityUid surgeon,
        ICommonSession recipient)
    {
        if (!TryResolveSession(request.SessionId, surgeon, out var session, out var profile, out var failure))
        {
            SendFailure(recipient, request.SessionId, failure);
            return;
        }
        if (!Enum.IsDefined(request.Surface) || !FiniteNormalized(request.Position) ||
            !_prototypes.TryIndex(request.Zone, out NCTargetZonePrototype? requestedZone) ||
            !requestedZone.Selectable ||
            !TryResolveFieldRegion(surgeon, session.Patient, request.Zone, out _, out var requestedRegion))
        {
            SendState(surgeon, session, recipient, false, NCFreeformSurgeryFailure.AttachmentUnavailable);
            return;
        }

        // RMB attachment follows the same server-authored zone and incision targeting contract as other actions.
        var regionChanged = request.Zone != session.TargetZone || requestedRegion != session.Region;
        var selectionChanged = regionChanged || request.Surface != session.Surface;
        if (selectionChanged)
        {
            session.TargetZone = request.Zone;
            session.Region = requestedRegion;
            session.Surface = request.Surface;
            session.ActiveIncisionId = null;
            session.StructureHandles.Clear();
            session.AttachmentHandles.Clear();
        }

        if (!TryComp<NCSurgicalSiteComponent>(session.Region, out var site) ||
            !regionChanged && request.ExpectedRevision != site.SpatialRevision ||
            !_hands.TryGetActiveItem(surgeon, out var held) || held is not { } tool ||
            !TryComp<NCSurgeryToolComponent>(tool, out var surgicalTool) || surgicalTool.Broken ||
            !TryResolveCapability(session, profile, surgicalTool, out var capability, out var definition, out var quality) ||
            capability != expectedCapability || !definition.Attachable)
        {
            SendState(surgeon, session, recipient, false, NCFreeformSurgeryFailure.AttachmentUnavailable);
            return;
        }

        var incision = FindOperativeSite(site, session);
        if (incision == null || incision.Sutured || incision.Depth + 0.001f < definition.MinimumDepth ||
            definition.Action == NCFreeformSurgeryAction.DrainCavity &&
            !HasComp<NCCavityFluidComponent>(session.Region))
        {
            SendState(surgeon, session, recipient, false, NCFreeformSurgeryFailure.AttachmentUnavailable);
            return;
        }

        var container = _containers.EnsureContainer<Container>(session.Patient, AttachmentContainerId);
        if (definition.Action == NCFreeformSurgeryAction.DrainCavity &&
            container.ContainedEntities.Any(contained =>
                TryComp<NCFreeformSurgicalAttachmentComponent>(contained, out var existing) &&
                existing.Region == session.Region &&
                existing.Action == NCFreeformSurgeryAction.DrainCavity))
        {
            SendState(surgeon, session, recipient, false, NCFreeformSurgeryFailure.AttachmentUnavailable);
            return;
        }
        if (!_hands.TryDropIntoContainer(surgeon, tool, container))
        {
            SendState(surgeon, session, recipient, false, NCFreeformSurgeryFailure.AttachmentUnavailable);
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
        SendState(surgeon, session, recipient);
    }

    private void OnDetach(NCFreeformSurgeryDetachRequest request, EntitySessionEventArgs args)
    {
        if (!TryResolveSession(request.SessionId, args, out var surgeon, out var session, out var profile, out var failure))
        {
            SendFailure(args.SenderSession, request.SessionId, failure);
            return;
        }
        if (!TryComp<NCSurgicalSiteComponent>(session.Region, out var site) ||
            request.ExpectedRevision != site.SpatialRevision ||
            !session.AttachmentHandles.TryGetValue(request.Handle, out var tool) ||
            !TryComp<NCFreeformSurgicalAttachmentComponent>(tool, out var attachment) ||
            attachment.Patient != session.Patient || attachment.Region != session.Region)
        {
            SendState(surgeon, session, args.SenderSession, false, NCFreeformSurgeryFailure.AttachmentUnavailable);
            return;
        }

        var doAfter = new DoAfterArgs(
            EntityManager,
            surgeon,
            profile.AttachmentRemovalDuration,
            new NCFreeformSurgeryDetachDoAfterEvent(request),
            tool,
            target: session.Patient,
            used: tool)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            DistanceThreshold = profile.InteractionRange,
        };
        if (!_doAfter.TryStartDoAfter(doAfter))
            SendState(surgeon, session, args.SenderSession, false, NCFreeformSurgeryFailure.RateLimited);
    }

    private void OnDetachDoAfter(
        Entity<NCFreeformSurgicalAttachmentComponent> tool,
        ref NCFreeformSurgeryDetachDoAfterEvent args)
    {
        if (args.Handled || args.Used != tool.Owner)
            return;
        args.Handled = true;

        if (!TryComp<ActorComponent>(args.User, out var actor) ||
            !TryResolveSession(args.SessionId, args.User, out var session, out _, out _))
        {
            return;
        }
        if (args.Cancelled || args.Target != session.Patient ||
            !session.AttachmentHandles.TryGetValue(args.Handle, out var resolvedTool) || resolvedTool != tool.Owner ||
            tool.Comp.Patient != session.Patient || tool.Comp.Region != session.Region)
        {
            SendState(args.User, session, actor.PlayerSession, false, NCFreeformSurgeryFailure.AttachmentUnavailable);
            return;
        }

        CompleteDetach(args.ToRequest(), args.User, actor.PlayerSession);
    }

    private void CompleteDetach(
        NCFreeformSurgeryDetachRequest request,
        EntityUid surgeon,
        ICommonSession recipient)
    {
        if (!TryResolveSession(request.SessionId, surgeon, out var session, out var profile, out var failure))
        {
            SendFailure(recipient, request.SessionId, failure);
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
            SendState(surgeon, session, recipient, false, NCFreeformSurgeryFailure.AttachmentUnavailable);
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
            site.TissuesRetracted = site.Incisions.Any(value =>
                !value.Sutured && value.EdgeSeparation >= profile.MinimumOpenRetraction);
        }
        RemComp<NCFreeformSurgicalAttachmentComponent>(tool);
        _hands.TryPickupAnyHand(surgeon, tool);
        unchecked
        {
            site.SpatialRevision++;
        }
        MarkSiteChanged(session.Patient);
        SendState(surgeon, session, recipient);
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
            !TryResolveSurgeryProfile(attachment.Comp.Patient, out var profile) ||
            incision.EdgeSeparation < profile.MinimumOpenRetraction)
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
        site.TissuesRetracted = site.Incisions.Any(value =>
            !value.Sutured && value.EdgeSeparation >= profile.MinimumOpenRetraction);
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

    /// <summary>
    /// Revalidates a timed action without trusting the network session captured when the DoAfter began.
    /// </summary>
    private bool TryResolveSession(
        NCActionResolutionId sessionId,
        EntityUid surgeon,
        out NCFreeformSurgerySessionComponent session,
        out NCFreeformSurgeryProfilePrototype profile,
        out NCFreeformSurgeryFailure failure)
    {
        session = default!;
        profile = default!;
        failure = NCFreeformSurgeryFailure.SessionUnavailable;
        if (!sessionId.IsValid ||
            !TryComp<NCFreeformSurgerySessionComponent>(surgeon, out var resolvedSession) ||
            resolvedSession.SessionId != sessionId)
        {
            return false;
        }

        session = resolvedSession;
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

    /// <summary>
    /// NC: Resolves the data-driven surgical thresholds for a live medical body. Attachment shutdown does not
    /// have a surgeon session, so it must derive the same profile directly from the patient.
    /// </summary>
    private bool TryResolveSurgeryProfile(
        EntityUid patient,
        out NCFreeformSurgeryProfilePrototype profile)
    {
        profile = default!;
        if (!TryComp<NCMedicalBodyComponent>(patient, out var medical) ||
            !_prototypes.TryIndex(medical.Profile, out NCAnatomyProfilePrototype? anatomy) ||
            !_prototypes.TryIndex(anatomy.SurgicalField, out NCFreeformSurgeryProfilePrototype? resolvedProfile))
        {
            return false;
        }

        profile = resolvedProfile;
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
        session.ActiveIncisionId = null;
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

        // NC: Migrate legacy freehand/motion-shifted records at the patient boundary. This is bounded to the
        // patient's organ container and makes the network state expose one stable site per zone and surface.
        NormalizeSurgicalSites(session, profile);

        var capabilities = BuildCapabilities(
            surgeon,
            session,
            profile,
            out var toolName,
            out var toolPrototype,
            out var sterility);
        var layers = BuildLayers(session.Region, site, session);
        var structures = BuildStructures(surgeon, session, profile, site);
        var incisions = BuildIncisions(session, profile);
        var wounds = BuildWounds(surgeon, session, profile);
        var attachments = BuildAttachments(session);
        var missingZones = BuildMissingZones(session.Patient, profile);
        ProtoId<NCTargetZonePrototype>? stagedGraftZone = null;
        if (TryComp<NCSurgeryGameplaySelectionComponent>(surgeon, out var staged) &&
            staged.Patient == session.Patient && staged.Graft is { } graft && !TerminatingOrDeleted(graft) &&
            missingZones.Contains(staged.TargetZone) &&
            TryResolveFieldRegion(surgeon, session.Patient, staged.TargetZone, out _, out _))
            stagedGraftZone = staged.TargetZone;
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
            session.ActiveIncisionId,
            session.Surface,
            missingZones,
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
            attachments,
            stagedGraftZone), recipient);
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
        if (!_hands.TryGetActiveItem(surgeon, out var held) || held is not { } tool)
        {
            session.SelectedCapability = null;
            return [];
        }

        toolName = Name(tool);
        toolPrototype = MetaData(tool).EntityPrototype?.ID ?? string.Empty;
        if (!TryComp<NCSurgeryToolComponent>(tool, out var surgicalTool))
        {
            session.SelectedCapability = null;
            return [];
        }
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
                ? prototype.NameId is { } nameId ? Loc.GetString(nameId) : prototype.Name
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
                !incision.Sutured && (incision.BoneCasingOpen || incision.Depth >= start));
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
        float? blockingBoneDepth = null;
        if (!IsBoneCasingOpen(site, session))
        {
            foreach (var candidate in candidates)
            {
                if (!HasComp<NCBoneComponent>(candidate) ||
                    !TryGetStructureDefinition(candidate, session, profile, true, out var boneDefinition) ||
                    !boneDefinition.BlocksDeeperStructures ||
                    !IsExposed(site, session, boneDefinition, profile.MinimumOpenRetraction))
                {
                    continue;
                }

                blockingBoneDepth = blockingBoneDepth is { } current
                    ? MathF.Min(current, boneDefinition.MinimumDepth)
                    : boneDefinition.MinimumDepth;
            }
        }
        var result = new List<NCFreeformSurgeryStructureSummary>(candidates.Length);
        var handle = 1;
        foreach (var candidate in candidates)
        {
            if (!TryGetStructureDefinition(candidate, session, profile, true, out var definition))
                continue;
            var exposed = candidate == session.Region ||
                          IsExposed(site, session, definition, profile.MinimumOpenRetraction);
            if (blockingBoneDepth is { } barrier &&
                !HasComp<NCBoneComponent>(candidate) &&
                definition.MinimumDepth > barrier + 0.001f)
            {
                exposed = false;
            }
            if (!exposed)
                continue;
            session.StructureHandles.Add(handle, candidate);
            var integrity = TryComp<NCOrganIntegrityComponent>(candidate, out var integrityComp)
                ? integrityComp.CurrentIntegrity / MathF.Max(1f, integrityComp.MaxIntegrity)
                : 1f;
            var function = TryComp<NCOrganFunctionComponent>(candidate, out var functionComp)
                ? functionComp.CurrentCapacity / MathF.Max(0.001f, functionComp.BaseCapacity)
                : integrity;
            var normalizedIntegrity = Math.Clamp(integrity, 0f, 1f);
            var normalizedFunction = Math.Clamp(function, 0f, 1f);
            result.Add(new NCFreeformSurgeryStructureSummary(
                handle,
                Name(candidate),
                StructureKind(candidate),
                definition.Visual,
                definition.Position,
                definition.Size,
                definition.MinimumDepth,
                normalizedIntegrity,
                normalizedFunction,
                ResolveDamageVisual(candidate, normalizedIntegrity, normalizedFunction),
                definition.BlocksDeeperStructures,
                true));
            handle++;
        }

        // A staged implant is not yet part of the body graph. Once its host bay is locally exposed, disclose
        // a synthetic atlas target backed by the real host organ so the player has a precise installation area.
        if (TryResolveStagedCyberwareTarget(surgeon, session, profile, out var target, out var targetName) &&
            IsExposed(site, session, target, profile.MinimumOpenRetraction))
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
                NCFreeformSurgeryDamageVisualStage.Healthy,
                false,
                true));
        }
        return result.ToArray();
    }

    /// <summary>
    /// NC: The silhouette is a registered atlas, not a second anatomy model. Only the exact materialized zone
    /// resolver decides whether a limb is still attached; fallback zones must not make an amputated limb visible.
    /// </summary>
    private ProtoId<NCTargetZonePrototype>[] BuildMissingZones(
        EntityUid patient,
        NCFreeformSurgeryProfilePrototype profile)
    {
        return profile.BodyZones.Keys
            .Where(zone => !_anatomy.TryGetZone((patient, null), zone, out _, out _))
            .OrderBy(zone => zone.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private NCFreeformSurgeryIncisionSummary[] BuildIncisions(
        NCFreeformSurgerySessionComponent session,
        NCFreeformSurgeryProfilePrototype profile)
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
            foreach (var incision in site.Incisions
                         .GroupBy(value => (value.TargetZone, value.Surface))
                         .Select(group => group
                             .OrderBy(value => value.CreatedAt)
                             .ThenBy(value => value.IncisionId)
                             .First()))
            {
                var visibleLayer = ResolveVisibleLayer(region, incision.Depth);
                var protectiveBoneDepth = incision.BoneCasingOpen
                    ? null
                    : FindProtectiveBoneDepth(session.Patient, region, incision.TargetZone, profile);
                var stage = ResolveSiteStage(
                    incision,
                    visibleLayer,
                    profile.MinimumOpenRetraction,
                    protectiveBoneDepth);
                result.Add((
                    incision.CreatedAt,
                    new NCFreeformSurgeryIncisionSummary(
                        incision.IncisionId,
                        incision.TargetZone,
                        incision.Surface,
                        incision.Polyline.ToArray(),
                        incision.Depth,
                        incision.Width,
                        incision.EdgeSeparation,
                        incision.Contamination,
                        incision.Sutured,
                        incision.BoneCasingOpen,
                        visibleLayer,
                        stage)));
            }
        }

        return result
            .OrderBy(value => value.CreatedAt)
            .ThenBy(value => value.Summary.IncisionId)
            .Select(value => value.Summary)
            .ToArray();
    }

    private NCTissueLayerKind ResolveVisibleLayer(EntityUid region, float depth)
    {
        if (!TryComp<NCAnatomicalRegionComponent>(region, out var anatomical) ||
            !_prototypes.TryIndex(anatomical.TissueProfile, out NCTissueProfilePrototype? tissue) ||
            tissue.Layers.Count == 0)
        {
            return NCTissueLayerKind.Surface;
        }

        var index = Math.Clamp(
            (int) MathF.Floor(Math.Clamp(depth, 0f, 0.9999f) * tissue.Layers.Count),
            0,
            tissue.Layers.Count - 1);
        return tissue.Layers[index].Kind;
    }

    private static NCInteractiveSurgerySiteStage ResolveSiteStage(
        NCSurgicalIncisionRecord incision,
        NCTissueLayerKind visibleLayer,
        float minimumOpenRetraction,
        float? protectiveBoneDepth)
    {
        if (incision.Sutured)
            return NCInteractiveSurgerySiteStage.Sutured;
        if (incision.EdgeSeparation < minimumOpenRetraction)
            return NCInteractiveSurgerySiteStage.Incised;
        // NC: Deep cuts cannot visually or interactively tunnel through an unopened anatomical casing.
        if (!incision.BoneCasingOpen && protectiveBoneDepth is { } barrier)
        {
            return incision.Depth >= barrier - 0.001f
                ? NCInteractiveSurgerySiteStage.StructuralExposed
                : NCInteractiveSurgerySiteStage.Retracted;
        }
        if (incision.BoneCasingOpen || visibleLayer is NCTissueLayerKind.Cavity or
            NCTissueLayerKind.InternalOrgan or NCTissueLayerKind.Mechanical)
        {
            return NCInteractiveSurgerySiteStage.CavityOpen;
        }
        if (visibleLayer == NCTissueLayerKind.Structural)
            return NCInteractiveSurgerySiteStage.StructuralExposed;
        return NCInteractiveSurgerySiteStage.Retracted;
    }

    private NCFreeformSurgeryWoundSummary[] BuildWounds(
        EntityUid surgeon,
        NCFreeformSurgerySessionComponent session,
        NCFreeformSurgeryProfilePrototype profile)
    {
        if (!TryComp<NCWoundContainerComponent>(session.Patient, out var container))
            return [];

        // NC: The unified body view receives every impact surface. WoundId preserves exact target identity while
        // Position remains the immutable hit coordinate; neither is replaced by the static incision anchor.
        return container.Wounds.Values
            .OrderBy(wound => wound.CreatedAt)
            .ThenBy(wound => wound.WoundId)
            .Select(wound => new NCFreeformSurgeryWoundSummary(
                wound.WoundId,
                wound.TargetZone,
                wound.Surface,
                new Vector2(wound.VisualX / 255f, wound.VisualY / 255f),
                wound.WoundType,
                wound.Severity,
                wound.TissueDepth,
                wound.ExternalBleedRate,
                wound.InternalBleedRate,
                wound.EmbeddedObjectId != null,
                wound.EmbeddedObjectId != null && IsForeignBodyExposed(surgeon, session, profile, wound),
                wound.ClosureState))
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
        var candidates = CollectStructureCandidates(session, profile, false);
        float? blockingBoneDepth = null;
        if (!IsBoneCasingOpen(site, session))
        {
            foreach (var candidate in candidates)
            {
                if (!HasComp<NCBoneComponent>(candidate) ||
                    !TryGetStructureDefinition(candidate, session, profile, false, out var bone) ||
                    !bone.BlocksDeeperStructures ||
                    depth < bone.MinimumDepth || !Contains(bone, point) ||
                    requireExposure && !IsExposed(site, session, bone, profile.MinimumOpenRetraction))
                {
                    continue;
                }

                blockingBoneDepth = blockingBoneDepth is { } current
                    ? MathF.Min(current, bone.MinimumDepth)
                    : bone.MinimumDepth;
            }
        }
        if (TryResolveStagedCyberwareTarget(surgeon, session, profile, out var staged, out _) &&
            depth >= staged.MinimumDepth && Contains(staged, point) &&
            (!requireExposure || IsExposed(site, session, staged, profile.MinimumOpenRetraction)) &&
            (blockingBoneDepth == null || staged.MinimumDepth <= blockingBoneDepth + 0.001f))
        {
            best = session.Region;
            bestDepth = staged.MinimumDepth;
        }
        foreach (var candidate in candidates)
        {
            if (!TryGetStructureDefinition(candidate, session, profile, false, out var definition) ||
                depth < definition.MinimumDepth ||
                requireExposure && !IsExposed(site, session, definition, profile.MinimumOpenRetraction) ||
                blockingBoneDepth is { } barrier && !HasComp<NCBoneComponent>(candidate) &&
                definition.MinimumDepth > barrier + 0.001f)
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

    /// <summary>
    /// NC: Resolves a server-issued atlas handle back to the exact current body entity and rechecks geometry,
    /// exposure, depth and protective bone at DoAfter completion. Invalid handles fail instead of retargeting.
    /// </summary>
    private EntityUid? ResolveStructureHandleAt(
        NCFreeformSurgerySessionComponent session,
        NCFreeformSurgeryProfilePrototype profile,
        NCSurgicalSiteComponent site,
        Vector2 point,
        float depth,
        int handle)
    {
        if (!session.StructureHandles.TryGetValue(handle, out var candidate) || Deleted(candidate) ||
            candidate != session.Region && !CollectStructureCandidates(session, profile, false).Contains(candidate) ||
            !TryGetStructureDefinition(candidate, session, profile, candidate == session.Region, out var definition) ||
            depth + 0.001f < definition.MinimumDepth || !Contains(definition, point) ||
            !IsExposed(site, session, definition, profile.MinimumOpenRetraction))
        {
            return null;
        }

        // An unopened bone casing remains authoritative even if a stale client frame still showed a deeper sprite.
        if (!HasComp<NCBoneComponent>(candidate) && !IsBoneCasingOpen(site, session))
        {
            foreach (var blocker in CollectStructureCandidates(session, profile, false))
            {
                if (!HasComp<NCBoneComponent>(blocker) ||
                    !TryGetStructureDefinition(blocker, session, profile, false, out var bone) ||
                    !bone.BlocksDeeperStructures || definition.MinimumDepth <= bone.MinimumDepth + 0.001f ||
                    !Contains(bone, point) ||
                    !IsExposed(site, session, bone, profile.MinimumOpenRetraction))
                {
                    continue;
                }

                return null;
            }
        }

        return candidate;
    }

    private float? FindProtectiveBoneDepth(
        EntityUid patient,
        EntityUid region,
        ProtoId<NCTargetZonePrototype> zone,
        NCFreeformSurgeryProfilePrototype profile)
    {
        if (!TryComp<BodyComponent>(patient, out var body) || body.Organs == null)
            return null;

        float? result = null;
        foreach (var candidate in body.Organs.ContainedEntities.Take(MaximumBodyOrgans))
        {
            if (candidate != region && !IsDescendantOf(candidate, region) ||
                !HasComp<NCBoneComponent>(candidate) ||
                !TryComp<OrganComponent>(candidate, out var organ) || organ.Category is not { } category ||
                !profile.Structures.TryGetValue(category, out var definition) ||
                definition.Zones.Count > 0 && !definition.Zones.Contains(zone) ||
                !definition.BlocksDeeperStructures)
            {
                continue;
            }

            result = result is { } current
                ? MathF.Min(current, definition.MinimumDepth)
                : definition.MinimumDepth;
        }
        return result;
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
            definition.Zones.Count > 0 && !definition.Zones.Contains(session.TargetZone) ||
            !_prototypes.TryIndex(slot, out NCCyberwareSlotPrototype? slotPrototype))
        {
            return false;
        }

        var slotName = slotPrototype.NameId is { } nameId ? Loc.GetString(nameId) : slotPrototype.Name;
        name = Loc.GetString("nc-cyberware-power-implant-target", ("slot", slotName));
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
            return definition.Zones.Count == 0 || definition.Zones.Contains(session.TargetZone);
        }
        if (TryComp<NCCyberwareComponent>(candidate, out var cyberware) &&
            cyberware.InstalledSlot is { } slot && profile.CyberwareSlots.TryGetValue(slot, out definition!))
        {
            return definition.Zones.Count == 0 || definition.Zones.Contains(session.TargetZone);
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
        NCFreeformSurgeryStructureDefinition structure,
        float minimumOpenRetraction)
    {
        // NC: A retracted Ocular-style site exposes the authored contents of the whole operative window.
        // Structure positions are interactive sprite coordinates, not a requirement to draw a cut through them.
        return site.Incisions.Any(incision =>
            incision.TargetZone == session.TargetZone && incision.Surface == session.Surface &&
            !incision.Sutured && incision.EdgeSeparation >= minimumOpenRetraction &&
            (incision.BoneCasingOpen || incision.Depth >= structure.MinimumDepth));
    }

    private static bool IsBoneCasingOpen(
        NCSurgicalSiteComponent site,
        NCFreeformSurgerySessionComponent session)
    {
        return site.Incisions.Any(incision =>
            incision.TargetZone == session.TargetZone && incision.Surface == session.Surface &&
            !incision.Sutured && incision.BoneCasingOpen);
    }

    private bool IsForeignBodyExposed(
        EntityUid surgeon,
        NCFreeformSurgerySessionComponent session,
        NCFreeformSurgeryProfilePrototype profile,
        NCWoundRecord wound)
    {
        if (!TryResolveFieldRegion(surgeon, session.Patient, wound.TargetZone, out _, out var region) ||
            !TryComp<NCSurgicalSiteComponent>(region, out var site))
        {
            return false;
        }

        var requiredDepth = wound.TissueDepth switch
        {
            NCTissueDepth.SoftTissue => 0.2f,
            NCTissueDepth.Vascular => 0.4f,
            NCTissueDepth.Structural => 0.5f,
            NCTissueDepth.Cavity => 0.65f,
            NCTissueDepth.InternalOrgan => 0.75f,
            _ => 0f,
        };
        var protectiveDepth = FindProtectiveBoneDepth(
            session.Patient,
            region,
            wound.TargetZone,
            profile);
        return site.Incisions.Any(incision =>
            incision.TargetZone == wound.TargetZone && incision.Surface == wound.Surface &&
            !incision.Sutured && incision.EdgeSeparation >= profile.MinimumOpenRetraction &&
            incision.Depth + 0.001f >= requiredDepth &&
            (protectiveDepth is not { } barrier || requiredDepth <= barrier + 0.001f || incision.BoneCasingOpen));
    }

    private NCFreeformSurgeryDamageVisualStage ResolveDamageVisual(
        EntityUid structure,
        float integrity,
        float function)
    {
        if (TryComp<NCBoneComponent>(structure, out var bone))
        {
            if (!bone.RequiresFixation && bone.State != NCBoneState.Intact && bone.Stability >= 0.8f)
                return NCFreeformSurgeryDamageVisualStage.Fixed;
            return bone.State switch
            {
                NCBoneState.Bruised => NCFreeformSurgeryDamageVisualStage.Bruised,
                NCBoneState.Cracked => NCFreeformSurgeryDamageVisualStage.Cracked,
                NCBoneState.BrokenStable => NCFreeformSurgeryDamageVisualStage.Fractured,
                NCBoneState.BrokenDisplaced => NCFreeformSurgeryDamageVisualStage.Displaced,
                NCBoneState.Shattered => NCFreeformSurgeryDamageVisualStage.Shattered,
                _ => NCFreeformSurgeryDamageVisualStage.Healthy,
            };
        }
        if (TryComp<NCTendonComponent>(structure, out var tendon))
            return tendon.Ruptured
                ? NCFreeformSurgeryDamageVisualStage.Ruptured
                : tendon.Integrity < 0.85f
                    ? NCFreeformSurgeryDamageVisualStage.Damaged
                    : NCFreeformSurgeryDamageVisualStage.Healthy;
        if (TryComp<NCNerveComponent>(structure, out var nerve))
            return nerve.NeuralIntegrity < 0.25f
                ? NCFreeformSurgeryDamageVisualStage.Critical
                : nerve.NeuralIntegrity < 0.85f
                    ? NCFreeformSurgeryDamageVisualStage.Damaged
                    : NCFreeformSurgeryDamageVisualStage.Healthy;
        if (integrity <= 0.2f || function <= 0.1f)
            return NCFreeformSurgeryDamageVisualStage.Critical;
        if (integrity < 0.8f || function < 0.7f)
            return NCFreeformSurgeryDamageVisualStage.Damaged;
        return NCFreeformSurgeryDamageVisualStage.Healthy;
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

    /// <summary>
    /// NC: Converts the surgeon-to-patient world vector into the anatomical surface physically facing the tool.
    /// Existing wound clicks bypass this fallback by carrying their server-issued surface in the request.
    /// </summary>
    private NCWoundSurface ResolveAccessibleSurface(EntityUid surgeon, EntityUid patient)
    {
        var travel = _transform.GetWorldPosition(patient) - _transform.GetWorldPosition(surgeon);
        return _wounds.ResolveImpactSurface(patient, travel);
    }

    /// <summary>
    /// NC: A WoundId is an opaque stable handle, not permission to redirect an action. It must still belong to
    /// this patient and to the exact zone/surface shown under the cursor.
    /// </summary>
    private bool ValidateRequestedWound(EntityUid patient, NCInteractiveSurgeryActionRequest request)
    {
        if (request.WoundId is not { } woundId)
            return true;
        return TryComp<NCWoundContainerComponent>(patient, out var container) &&
               container.Wounds.TryGetValue(woundId, out var wound) &&
               wound.TargetZone == request.Zone && wound.Surface == request.Surface;
    }

    private static bool ValidateInteraction(NCInteractiveSurgeryActionRequest request)
    {
        if (!Enum.IsDefined(request.Surface) || !float.IsFinite(request.Depth) ||
            request.Depth is < 0f or > 1f || !FiniteNormalized(request.Position) ||
            request.StructureHandle is <= 0)
        {
            return false;
        }
        return true;
    }

    /// <summary>
    /// Creates a bounded two-point contact path entirely from server-owned prototype data. Cutting tools receive
    /// the authored incision axis and length; point tools receive the minimum path required by the effect model.
    /// </summary>
    private static Vector2[] BuildInteractionPath(
        Vector2 position,
        NCFreeformSurgeryBodyZoneDefinition zone,
        NCFreeformSurgeryAction action,
        NCFreeformSurgeryProfilePrototype profile)
    {
        var contact = action is NCFreeformSurgeryAction.Incise or NCFreeformSurgeryAction.Autopsy or
            NCFreeformSurgeryAction.Retract
            ? Vector2.Clamp(zone.InteractionAnchor, Vector2.Zero, Vector2.One)
            : position;
        var direction = zone.IncisionDirection;
        if (direction.LengthSquared() <= 0.000001f)
            direction = Vector2.UnitY;
        else
            direction = Vector2.Normalize(direction);

        var length = action is NCFreeformSurgeryAction.Incise or NCFreeformSurgeryAction.Autopsy
            ? zone.IncisionLength
            : profile.MinimumStrokeLength * 1.25f;
        length = Math.Clamp(length, profile.MinimumStrokeLength, profile.MaximumStrokeLength);
        var half = direction * length * 0.5f;
        var start = contact - half;
        var end = contact + half;

        // NC: Shift both endpoints together so edge interactions preserve canonical power and direction.
        ShiftInside(ref start.X, ref end.X);
        ShiftInside(ref start.Y, ref end.Y);
        return [
            Vector2.Clamp(start, Vector2.Zero, Vector2.One),
            Vector2.Clamp(end, Vector2.Zero, Vector2.One),
        ];
    }

    private static void ShiftInside(ref float start, ref float end)
    {
        if (start < 0f)
        {
            end -= start;
            start = 0f;
        }
        if (end > 1f)
        {
            start -= end - 1f;
            end = 1f;
        }
    }

    private static bool FiniteNormalized(Vector2 point)
    {
        return float.IsFinite(point.X) && float.IsFinite(point.Y) &&
               point.X is >= 0f and <= 1f && point.Y is >= 0f and <= 1f;
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

    private static NCSurgicalIncisionRecord? FindOperativeSite(
        NCSurgicalSiteComponent site,
        NCFreeformSurgerySessionComponent session)
    {
        return FindOperativeSite(site, session.TargetZone, session.Surface);
    }

    private static NCSurgicalIncisionRecord? FindOperativeSite(
        NCSurgicalSiteComponent site,
        ProtoId<NCTargetZonePrototype> targetZone,
        NCWoundSurface surface)
    {
        return site.Incisions
            .Where(incision => incision.TargetZone == targetZone && incision.Surface == surface &&
                               !incision.Sutured)
            .OrderBy(incision => incision.CreatedAt)
            .ThenBy(incision => incision.IncisionId)
            .FirstOrDefault();
    }

    /// <summary>
    /// NC: Collapses pre-slot surgery data into deterministic zone/surface sites and rewrites their presentation
    /// geometry from prototype anchors. Physiological wound records are retained and linked to the survivor.
    /// </summary>
    private void NormalizeSurgicalSites(
        NCFreeformSurgerySessionComponent session,
        NCFreeformSurgeryProfilePrototype profile)
    {
        if (!TryComp<BodyComponent>(session.Patient, out var body) || body.Organs == null)
            return;

        var patientChanged = false;
        foreach (var region in body.Organs.ContainedEntities.Take(MaximumBodyOrgans))
        {
            if (!TryComp<NCSurgicalSiteComponent>(region, out var site) || site.Incisions.Count == 0)
                continue;

            var siteChanged = false;
            // NC: One-time migration from the former region-wide flag. The deepest open incision is the only
            // defensible local owner; later saves persist the explicit per-incision state.
            if (site.BoneCasingOpen && !site.Incisions.Any(value => value.BoneCasingOpen))
            {
                var legacyOwner = site.Incisions
                    .Where(value => !value.Sutured)
                    .OrderByDescending(value => value.Depth)
                    .ThenBy(value => value.CreatedAt)
                    .FirstOrDefault();
                if (legacyOwner != null)
                {
                    legacyOwner.BoneCasingOpen = true;
                    siteChanged = true;
                }
            }
            foreach (var group in site.Incisions
                         .GroupBy(incision => (Zone: incision.TargetZone, incision.Surface))
                         .ToArray())
            {
                var records = group
                    .OrderBy(incision => incision.CreatedAt)
                    .ThenBy(incision => incision.IncisionId)
                    .ToArray();
                var survivor = records[0];

                foreach (var duplicate in records.Skip(1))
                {
                    survivor.Depth = MathF.Max(survivor.Depth, duplicate.Depth);
                    survivor.Width = MathF.Max(survivor.Width, duplicate.Width);
                    survivor.EdgeSeparation = MathF.Max(survivor.EdgeSeparation, duplicate.EdgeSeparation);
                    survivor.Contamination = MathF.Max(survivor.Contamination, duplicate.Contamination);
                    survivor.Sutured &= duplicate.Sutured;
                    survivor.BoneCasingOpen |= duplicate.BoneCasingOpen;
                    if (duplicate.CreatedAt < survivor.CreatedAt)
                        survivor.CreatedAt = duplicate.CreatedAt;
                    if (duplicate.ModifiedAt > survivor.ModifiedAt)
                        survivor.ModifiedAt = duplicate.ModifiedAt;
                    MergeWoundLinks(survivor, duplicate);
                    if (session.ActiveIncisionId == duplicate.IncisionId)
                        session.ActiveIncisionId = survivor.IncisionId;
                    site.Incisions.Remove(duplicate);
                    siteChanged = true;
                }

                if (profile.BodyZones.TryGetValue(group.Key.Zone, out var zoneDefinition))
                {
                    var canonical = BuildInteractionPath(
                        zoneDefinition.InteractionAnchor,
                        zoneDefinition,
                        NCFreeformSurgeryAction.Incise,
                        profile);
                    if (!survivor.Polyline.SequenceEqual(canonical))
                    {
                        survivor.Polyline = canonical.ToList();
                        siteChanged = true;
                    }
                }
            }

            if (!siteChanged)
                continue;
            site.BoneCasingOpen = site.Incisions.Any(value => !value.Sutured && value.BoneCasingOpen);
            unchecked
            {
                site.SpatialRevision++;
            }
            patientChanged = true;
        }

        if (patientChanged)
            MarkSiteChanged(session.Patient);
    }

    private static void MergeWoundLinks(
        NCSurgicalIncisionRecord survivor,
        NCSurgicalIncisionRecord duplicate)
    {
        AddWoundLink(survivor, duplicate.WoundId);
        foreach (var woundId in duplicate.LinkedWoundIds)
            AddWoundLink(survivor, woundId);
    }

    private static void AddWoundLink(NCSurgicalIncisionRecord incision, Guid? woundId)
    {
        if (woundId is not { } linked || incision.WoundId == linked || incision.LinkedWoundIds.Contains(linked))
            return;
        if (incision.WoundId == null)
            incision.WoundId = linked;
        else
            incision.LinkedWoundIds.Add(linked);
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
            null,
            NCWoundSurface.Front,
            [],
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
