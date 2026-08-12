// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.Combat.Targeting;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Anatomy.Detachment;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared.Body;
using Content.Shared.Buckle.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Robust.Server.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Surgery;

/// <summary>
/// Owns the private surgical planner. Patient and graft identities are derived from physical server state;
/// the client can select only a procedure/category pair from its latest directed snapshot.
/// </summary>
public sealed partial class NCSurgeryConsoleSystem : EntitySystem
{
    private const int OrganTraversalLimit = 64;

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private NCAnatomySystem _anatomy = default!;
    [Dependency] private NCTargetingSelectionSystem _targeting = default!;
    [Dependency] private NCSurgeryInteractionSystem _interactions = default!;
    [Dependency] private NCSurgerySystem _surgery = default!;

    private readonly HashSet<(EntityUid Console, EntityUid User)> _openViews = [];

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCSurgeryConsoleComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<NCSurgeryConsoleComponent, BoundUIClosedEvent>(OnUiClosed);
        SubscribeLocalEvent<NCSurgeryConsoleComponent, NCSurgeryConsoleSelectMessage>(OnSelect);
        SubscribeLocalEvent<NCSurgeryConsoleComponent, NCSurgeryConsoleClearSelectionMessage>(OnClear);
        SubscribeLocalEvent<NCSurgeryConsoleComponent, NCSurgeryConsoleRefreshMessage>(OnRefresh);
        SubscribeLocalEvent<NCDetachedOrganMedicalDataComponent, AfterInteractEvent>(OnGraftAfterInteract);
    }

    private void OnUiOpened(EntityUid uid, NCSurgeryConsoleComponent component, BoundUIOpenedEvent args)
    {
        if (args.Actor is not { Valid: true } user)
            return;

        _openViews.Add((uid, user));
        SendView(uid, component, user, NCSurgeryConsoleNotice.None);
    }

    private void OnUiClosed(EntityUid uid, NCSurgeryConsoleComponent component, BoundUIClosedEvent args)
    {
        if (args.Actor is { Valid: true } user)
            _openViews.Remove((uid, user));
    }

    private void OnSelect(
        EntityUid uid,
        NCSurgeryConsoleComponent component,
        NCSurgeryConsoleSelectMessage args)
    {
        if (!TryGetOpenActor(uid, args.Actor, out var user))
            return;

        if (!TryResolveBoundPatient(uid, component, user, out var facility, out var patient, out var notice) ||
            !TryBuildOptions(
                user,
                patient,
                facility,
                component.FacilitySearchRange,
                out _,
                out _,
                out var candidates,
                out _) ||
            candidates.All(candidate => candidate.Entry.Procedure != args.Procedure ||
                                        candidate.Entry.TargetCategory != args.TargetCategory))
        {
            SendView(uid, component, user, notice == NCSurgeryConsoleNotice.None
                ? NCSurgeryConsoleNotice.SelectionRejected
                : notice);
            return;
        }

        var selected = candidates.First(candidate =>
            candidate.Entry.Procedure == args.Procedure &&
            candidate.Entry.TargetCategory == args.TargetCategory);
        var state = EnsureComp<NCSurgeryGameplaySelectionComponent>(user);
        state.Patient = patient;
        state.Procedure = selected.Entry.Procedure;
        state.TargetRegion = selected.Region;
        state.Facility = facility;
        state.Graft = selected.Graft;
        SendView(uid, component, user, NCSurgeryConsoleNotice.SelectionBound);
    }

    private void OnClear(
        EntityUid uid,
        NCSurgeryConsoleComponent component,
        NCSurgeryConsoleClearSelectionMessage args)
    {
        if (!TryGetOpenActor(uid, args.Actor, out var user))
            return;

        RemComp<NCSurgeryGameplaySelectionComponent>(user);
        SendView(uid, component, user, NCSurgeryConsoleNotice.None);
    }

    private void OnRefresh(
        EntityUid uid,
        NCSurgeryConsoleComponent component,
        NCSurgeryConsoleRefreshMessage args)
    {
        if (TryGetOpenActor(uid, args.Actor, out var user))
            SendView(uid, component, user, NCSurgeryConsoleNotice.None);
    }

    /// <summary>
    /// A detached graft is put on the operating table through an ordinary held-item interaction. The table
    /// keeps only a runtime reference and every planner/action boundary validates that it remains nearby.
    /// </summary>
    private void OnGraftAfterInteract(
        Entity<NCDetachedOrganMedicalDataComponent> graft,
        ref AfterInteractEvent args)
    {
        if (args.Handled || args.Target is not { } facility ||
            !HasComp<NCSurgeryFacilityComponent>(facility) ||
            !_hands.IsHolding(args.User, graft.Owner) ||
            !_interaction.InRangeUnobstructed(args.User, facility))
        {
            return;
        }

        // The binding has a physical world consequence: the graft leaves the user's hand and is dropped
        // at the selected facility. A mere client-side selection can therefore never stage a transplant.
        if (!_hands.TryDrop(
                args.User,
                graft.Owner,
                targetDropLocation: Transform(facility).Coordinates))
        {
            return;
        }

        var slot = EnsureComp<NCSurgeryGraftSlotComponent>(facility);
        slot.Graft = graft.Owner;
        slot.BoundBy = args.User;
        args.Handled = true;

        foreach (var (console, user) in _openViews.ToArray())
        {
            if (user != args.User || !TryComp<NCSurgeryConsoleComponent>(console, out var consoleComponent))
                continue;
            SendView(console, consoleComponent, user, NCSurgeryConsoleNotice.GraftBound);
        }
    }

    private void SendView(
        EntityUid console,
        NCSurgeryConsoleComponent component,
        EntityUid user,
        NCSurgeryConsoleNotice requestedNotice)
    {
        if (!_openViews.Contains((console, user)))
            return;

        if (!TryResolveBoundPatient(console, component, user, out var facility, out var patient, out var failure))
        {
            _ui.ServerSendUiMessage(
                console,
                NCSurgeryConsoleUiKey.Key,
                EmptyMessage(failure == NCSurgeryConsoleNotice.None ? requestedNotice : failure),
                user);
            return;
        }

        var hasOptions = TryBuildOptions(
            user,
            patient,
            facility,
            component.FacilitySearchRange,
            out var selectedZone,
            out var selectedRegion,
            out var candidates,
            out var buildNotice);
        var selection = CompOrNull<NCSurgeryGameplaySelectionComponent>(user);
        if (selection != null &&
            (selection.Patient != patient || selection.Facility != facility || Deleted(selection.TargetRegion)))
        {
            RemComp<NCSurgeryGameplaySelectionComponent>(user);
            selection = null;
        }

        var selectedSite = selectedRegion is { } selectedCategory &&
                           _anatomy.TryGetOrgan(patient, selectedCategory, out var selectedUid)
            ? CompOrNull<NCSurgicalSiteComponent>(selectedUid)
            : null;
        var active = FindActiveSession(patient);
        var graft = GetValidGraft(facility, user, component.FacilitySearchRange);
        var notice = requestedNotice != NCSurgeryConsoleNotice.None
            ? requestedNotice
            : hasOptions
                ? NCSurgeryConsoleNotice.None
                : buildNotice;

        _ui.ServerSendUiMessage(
            console,
            NCSurgeryConsoleUiKey.Key,
            new NCSurgeryConsoleUpdateMessage(
                MetaData(patient).EntityName,
                MetaData(facility).EntityName,
                selectedZone,
                selectedRegion,
                selectedSite?.AccessDepth ?? NCSurgicalAccessDepth.Closed,
                graft is { } graftUid ? MetaData(graftUid).EntityName : null,
                selection?.Procedure,
                selection != null && TryGetCategory(selection.TargetRegion, out var selectionCategory)
                    ? (ProtoId<OrganCategoryPrototype>?) selectionCategory
                    : (ProtoId<OrganCategoryPrototype>?) null,
                active?.Procedure,
                active?.CurrentStepIndex ?? 0,
                active?.State,
                candidates.Select(candidate => candidate.Entry).ToList(),
                notice),
            user);
    }

    private bool TryBuildOptions(
        EntityUid user,
        EntityUid patient,
        EntityUid facility,
        float graftRange,
        out ProtoId<NCTargetZonePrototype>? selectedZone,
        out ProtoId<OrganCategoryPrototype>? selectedRegion,
        out List<ProcedureCandidate> candidates,
        out NCSurgeryConsoleNotice notice)
    {
        selectedZone = null;
        selectedRegion = null;
        candidates = [];
        notice = NCSurgeryConsoleNotice.SelectTargetZone;
        var target = _targeting.GetSelection(user);
        if (target.Mode != NCTargetingMode.SelectedZone || target.SelectedZone is not { } requested ||
            !_anatomy.TryResolveSelectedZone(patient, requested, out var actual, out var root) ||
            !TryGetCategory(root, out var rootCategory))
        {
            return false;
        }

        selectedZone = actual;
        selectedRegion = rootCategory;
        var graft = GetValidGraft(facility, user, graftRange);
        var regions = CollectRegions(patient)
            .Where(entry => IsWithinRegion(entry.Region, root))
            .OrderBy(entry => entry.Category.Id, StringComparer.Ordinal)
            .ToList();

        foreach (var procedure in _prototypes.EnumeratePrototypes<NCSurgeryProcedurePrototype>()
                     .OrderBy(value => value.ID, StringComparer.Ordinal))
        {
            foreach (var region in regions)
            {
                var boundGraft = procedure.RequiresGraft ? graft : null;
                if (!_surgery.TryPrepareProcedureTarget(
                        patient,
                        region.Region,
                        procedure.ID,
                        boundGraft,
                        out _,
                        out _))
                {
                    continue;
                }

                candidates.Add(new ProcedureCandidate(
                    new NCSurgeryConsoleProcedureEntry(
                        procedure.ID,
                        region.Category,
                        MetaData(region.Region).EntityName,
                        procedure.RequiresGraft),
                    region.Region,
                    boundGraft));
            }
        }

        notice = candidates.Count > 0
            ? NCSurgeryConsoleNotice.None
            : NCSurgeryConsoleNotice.NoCompatibleProcedure;
        return candidates.Count > 0;
    }

    /// <summary>
    /// Resolves a patient only from the strap of this table or a nearby surgical facility. This bounded
    /// spatial query is used for consoles; it never enumerates all world entities.
    /// </summary>
    public bool TryResolveBoundPatient(
        EntityUid console,
        NCSurgeryConsoleComponent component,
        EntityUid user,
        out EntityUid facility,
        out EntityUid patient,
        out NCSurgeryConsoleNotice notice)
    {
        facility = default;
        patient = default;
        notice = NCSurgeryConsoleNotice.OutOfRange;
        if (Deleted(console) || Deleted(user) ||
            !_interaction.InRangeUnobstructed(user, console, component.FacilitySearchRange))
        {
            return false;
        }

        var possible = new List<EntityUid>();
        if (HasComp<NCSurgeryFacilityComponent>(console))
            possible.Add(console);
        else
        {
            foreach (var candidate in _lookup.GetEntitiesInRange<NCSurgeryFacilityComponent>(
                         Transform(console).Coordinates,
                         component.FacilitySearchRange))
            {
                possible.Add(candidate.Owner);
            }
        }

        var origin = _transform.GetWorldPosition(console);
        foreach (var candidate in possible
                     .Distinct()
                     .OrderBy(uid => (_transform.GetWorldPosition(uid) - origin).LengthSquared())
                     .ThenBy(uid => uid.Id))
        {
            if (!TryComp<NCSurgeryFacilityComponent>(candidate, out var surgicalFacility) ||
                !surgicalFacility.Powered || !surgicalFacility.StableSurface ||
                !TryGetBuckledPatient(candidate, out var occupied) ||
                !_interaction.InRangeUnobstructed(user, occupied, component.FacilitySearchRange))
            {
                continue;
            }

            _interactions.SynchronizePreparation(occupied);
            if (!TryComp<NCSurgeryPatientStateComponent>(occupied, out var prepared) ||
                prepared.CurrentFacility != candidate ||
                prepared.CurrentEnvironment != candidate)
            {
                continue;
            }

            facility = candidate;
            patient = occupied;
            notice = NCSurgeryConsoleNotice.None;
            return true;
        }

        notice = NCSurgeryConsoleNotice.NoPatient;
        return false;
    }

    private bool TryGetBuckledPatient(EntityUid facility, out EntityUid patient)
    {
        patient = default;
        if (!TryComp<StrapComponent>(facility, out var strap))
            return false;

        foreach (var candidate in strap.BuckledEntities.OrderBy(uid => uid.Id))
        {
            if (!HasComp<NCMedicalBodyComponent>(candidate) ||
                !TryComp<BuckleComponent>(candidate, out var buckle) ||
                !buckle.Buckled || buckle.BuckledTo != facility)
            {
                continue;
            }

            patient = candidate;
            return true;
        }

        return false;
    }

    private EntityUid? GetValidGraft(EntityUid facility, EntityUid user, float range)
    {
        if (!TryComp<NCSurgeryGraftSlotComponent>(facility, out var slot) || slot.Graft is not { } graft ||
            Deleted(graft) || !HasComp<NCDetachedOrganMedicalDataComponent>(graft) ||
            !_interaction.InRangeUnobstructed(user, graft, range) ||
            !_interaction.InRangeUnobstructed(facility, graft, range))
        {
            if (slot != null)
            {
                slot.Graft = null;
                slot.BoundBy = null;
            }
            return null;
        }

        return graft;
    }

    private List<RegionEntry> CollectRegions(EntityUid patient)
    {
        var result = new List<RegionEntry>();
        if (!TryComp<BodyComponent>(patient, out var body) || body.Organs == null)
            return result;

        foreach (var region in body.Organs.ContainedEntities)
        {
            if (TryGetCategory(region, out var category) && HasComp<NCSurgicalSiteComponent>(region))
                result.Add(new RegionEntry(region, category));
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

    private NCSurgerySessionComponent? FindActiveSession(EntityUid patient)
    {
        foreach (var region in CollectRegions(patient).OrderBy(entry => entry.Category.Id, StringComparer.Ordinal))
        {
            if (TryComp<NCSurgerySessionComponent>(region.Region, out var session) &&
                session.State is NCSurgerySessionState.Running or NCSurgerySessionState.Paused)
            {
                return session;
            }
        }
        return null;
    }

    private bool TryGetOpenActor(EntityUid console, EntityUid? actor, out EntityUid user)
    {
        user = actor ?? EntityUid.Invalid;
        return user.Valid && !Deleted(user) && _openViews.Contains((console, user));
    }

    private bool TryGetCategory(EntityUid region, out ProtoId<OrganCategoryPrototype> category)
    {
        category = default;
        if (!TryComp<OrganComponent>(region, out var organ) || organ.Category is not { } resolved)
            return false;
        category = resolved;
        return true;
    }

    private static NCSurgeryConsoleUpdateMessage EmptyMessage(NCSurgeryConsoleNotice notice)
    {
        return new NCSurgeryConsoleUpdateMessage(
            string.Empty,
            string.Empty,
            null,
            null,
            NCSurgicalAccessDepth.Closed,
            null,
            null,
            null,
            null,
            0,
            null,
            [],
            notice);
    }

    private readonly record struct RegionEntry(
        EntityUid Region,
        ProtoId<OrganCategoryPrototype> Category);

    private readonly record struct ProcedureCandidate(
        NCSurgeryConsoleProcedureEntry Entry,
        EntityUid Region,
        EntityUid? Graft);
}
