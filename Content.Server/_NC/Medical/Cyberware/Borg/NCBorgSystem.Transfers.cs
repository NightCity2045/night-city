// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Medical.Anatomy.Detachment;
using Content.Server._NC.Medical.Persistence;
using Content.Server._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Borg;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared.Body;
using Content.Shared.Body.Components;
using Content.Shared.Buckle.Components;
using Content.Shared.DoAfter;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Cyberware.Borg;

public sealed partial class NCBorgSystem
{
    [Dependency] private NCAnatomySystem _anatomy = default!;
    [Dependency] private NCOrganDetachmentSystem _detachment = default!;
    [Dependency] private NCMedicalPersistenceSystem _persistence = default!;
    [Dependency] private NCPhysiologySchedulerSystem _scheduler = default!;
    [Dependency] private NCCyberwareSystem _cyberware = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private IPrototypeManager _borgPrototypes = default!;

    private void InitializeTransfers()
    {
        SubscribeLocalEvent<NCBorgCoreComponent, AfterInteractEvent>(OnCoreAfterInteract);
        SubscribeLocalEvent<NCBorgCoreComponent, GetVerbsEvent<InteractionVerb>>(OnCoreVerbs);
        SubscribeLocalEvent<NCBorgCoreComponent, NCBorgTransferDoAfterEvent>(OnTransferDoAfter);
    }

    private void OnCoreAfterInteract(Entity<NCBorgCoreComponent> core, ref AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach || args.Target is not { } target ||
            !_hands.IsHolding(args.User, core))
            return;
        var kind = HasComp<NCBorgShellSocketComponent>(target) ? NCBorgTransferKind.Dock : NCBorgTransferKind.Convert;
        if (kind == NCBorgTransferKind.Convert && !HasComp<NCMedicalBodyComponent>(target))
            return;
        args.Handled = true;
        TryStartTransfer(core, args.User, target, kind);
    }

    private void OnCoreVerbs(Entity<NCBorgCoreComponent> core, ref GetVerbsEvent<InteractionVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !HasServiceProbe(args.User) ||
            !_anatomy.TryGetOrgan(core, "Torso", out _))
            return;
        var user = args.User;
        args.Verbs.Add(new InteractionVerb
        {
            Text = Loc.GetString("nc-borg-undock"),
            Act = () => TryStartTransfer(core, user, core, NCBorgTransferKind.Undock),
        });
    }

    private bool TryStartTransfer(Entity<NCBorgCoreComponent> core, EntityUid user, EntityUid target,
        NCBorgTransferKind kind)
    {
        if (HasComp<NCBorgPendingTransferComponent>(core))
            return false;
        var valid = kind switch
        {
            NCBorgTransferKind.Convert => CanConvert(core.AsNullable(), user, target, out _),
            NCBorgTransferKind.Dock => CanDock(core.AsNullable(), user, target),
            NCBorgTransferKind.Undock => CanUndock(core.AsNullable(), user),
            _ => false,
        };
        if (!valid)
        {
            _popup.PopupEntity(Loc.GetString("nc-borg-transfer-unavailable"), target, user);
            return false;
        }
        var organBody = kind == NCBorgTransferKind.Convert ? target : kind == NCBorgTransferKind.Dock ? target : core.Owner;
        if (!_anatomy.TryGetOrgan(organBody, kind == NCBorgTransferKind.Convert ? "Brain" : "Torso", out var organ))
            return false;
        var id = Guid.NewGuid();
        var operation = new NCBorgTransferDoAfterEvent
        {
            TransferId = id,
            Kind = kind,
            ExpectedOrgan = GetNetEntity(organ),
        };
        _hands.TryGetActiveItem(user, out var held);
        var arguments = new DoAfterArgs(EntityManager, user,
            TimeSpan.FromSeconds(kind == NCBorgTransferKind.Convert ? 12 : 4), operation,
            core, target: target, used: held)
        {
            NeedHand = true,
            BreakOnMove = true,
            BreakOnDamage = true,
            DistanceThreshold = 2f,
        };
        var pending = EnsureComp<NCBorgPendingTransferComponent>(core);
        pending.TransferId = id;
        if (_doAfter.TryStartDoAfter(arguments))
            return true;
        RemComp<NCBorgPendingTransferComponent>(core);
        return false;
    }

    private void OnTransferDoAfter(Entity<NCBorgCoreComponent> core, ref NCBorgTransferDoAfterEvent args)
    {
        if (args.Handled || !TryComp<NCBorgPendingTransferComponent>(core, out var pending) ||
            pending.TransferId != args.TransferId)
            return;
        RemComp<NCBorgPendingTransferComponent>(core);
        if (args.Cancelled || args.Target is not { } target)
            return;
        args.Handled = true;
        var body = args.Kind == NCBorgTransferKind.Undock ? core.Owner : target;
        var category = args.Kind == NCBorgTransferKind.Convert ? "Brain" : "Torso";
        var success = _anatomy.TryGetOrgan(body, category, out var current) && GetNetEntity(current) == args.ExpectedOrgan &&
                      (args.Kind switch
                      {
                          NCBorgTransferKind.Convert => TryConvert(core.AsNullable(), args.User, target),
                          NCBorgTransferKind.Dock => TryDock(core.AsNullable(), args.User, target),
                          NCBorgTransferKind.Undock => TryUndock(core.AsNullable(), args.User),
                          _ => false,
                      });
        _popup.PopupEntity(Loc.GetString(success ? "nc-borg-transfer-complete" : "nc-borg-transfer-unavailable"),
            core, args.User);
    }

    public bool CanConvert(Entity<NCBorgCoreComponent?> core, EntityUid user, EntityUid patient, out EntityUid brain)
    {
        brain = default;
        return Resolve(core, ref core.Comp, false) && !core.Comp.Occupied && user != patient &&
               !HasComp<NCBorgCoreComponent>(patient) && CanReach(user, core) && CanReach(user, patient) &&
               _hands.IsHolding(user, core) &&
               TryComp<BuckleComponent>(patient, out var buckle) && buckle.BuckledTo is { } table &&
               TryComp<NCSurgeryFacilityComponent>(table, out var facility) && facility.Powered && facility.StableSurface &&
               TryComp<NCPhysiologyComponent>(patient, out var physiology) && !physiology.Irreversible &&
               physiology.Consciousness >= NCConsciousnessState.Unconscious &&
               _anatomy.TryGetOrgan(patient, "Head", out var head) &&
               TryComp<NCSurgicalSiteComponent>(head, out var site) && site.BoneCasingOpen && site.TissuesRetracted &&
               HasBrainAccess(patient, site) &&
               _anatomy.TryGetOrgan(patient, "Brain", out brain) && HasComp<BrainComponent>(brain) &&
               TryComp<NCOrganIntegrityComponent>(brain, out var integrity) && integrity.CurrentIntegrity > 0f &&
               TryComp<NCOrganFunctionComponent>(brain, out var function) &&
               (function.Recoverable || function.CurrentCapacity > function.NonFunctionalThreshold) &&
               !_anatomy.TryGetOrgan(core, "Brain", out _) && Prepared(core) &&
               _persistence.CanTransferBorgContinuity(patient, core);
    }

    /// <summary>Converts one real, exposed living brain. The pod was supplied before the old supply is cut.</summary>
    public bool TryConvert(Entity<NCBorgCoreComponent?> core, EntityUid user, EntityUid patient)
    {
        if (!Resolve(core, ref core.Comp, false) || !CanConvert(core, user, patient, out _))
            return false;
        // Advance the existing support before rechecking the physical conversion preconditions.
        SettleCore(core);
        if (TryComp<NCCirculationComponent>(patient, out var circulation))
            _scheduler.ProcessNow(patient, _timing.CurTime - circulation.LastEvaluation);
        if (!CanConvert(core, user, patient, out _) ||
            !TryUseSupportCharge(core, 1f) ||
            !_persistence.TryTransferBrainToBorg(patient, core))
            return false;
        core.Comp.Occupied = true;
        RefreshSupport((core.Owner, core.Comp));
        _scheduler.ProcessNow(core, TimeSpan.Zero);
        return true;
    }

    public bool CanDock(Entity<NCBorgCoreComponent?> core, EntityUid user, EntityUid socket)
    {
        return Resolve(core, ref core.Comp, false) && core.Comp.Occupied &&
               CanReach(user, core) && CanReach(user, socket) &&
               HasComp<NCBorgShellSocketComponent>(socket) &&
               !_anatomy.TryGetOrgan(core, "Torso", out _) &&
               _anatomy.TryGetOrgan(socket, "Torso", out var torso) && HasComp<NCBorgStandardShellComponent>(torso) &&
               FrameReady(socket) &&
               _anatomy.TryGetOrgan(core, "Brain", out _) &&
               TryComp<NCPhysiologyComponent>(core, out var physiology) && !physiology.Irreversible;
    }

    public bool TryDock(Entity<NCBorgCoreComponent?> core, EntityUid user, EntityUid socket)
    {
        if (!Resolve(core, ref core.Comp, false) || !CanDock(core, user, socket))
            return false;
        SettleCore(core);
        _cyberware.TryAdvanceBodyPower(socket, _timing.CurTime);
        if (!CanDock(core, user, socket) ||
            !_anatomy.TryGetOrgan(socket, "Thorax", out var holder) ||
            !_cells.TryGetBatteryFromSlot(holder, out var testCell) ||
            !_battery.TryUseCharge(testCell.Value.AsNullable(), 1f) ||
            !_containers.TryRemoveFromContainer((core.Owner, null, null)) ||
            !_detachment.TryTransferOrgan(socket, core, "Torso", "Head"))
            return false;
        _transform.SetCoordinates(core, Transform(socket).Coordinates);
        RefreshPowerBinding(socket);
        RefreshPowerBinding(core);
        RefreshSupport((core.Owner, core.Comp!));
        return true;
    }

    private bool FrameReady(EntityUid socket)
    {
        if (!_anatomy.TryGetOrgan(socket, "Thorax", out var holder) ||
            !_cells.TryGetBatteryFromSlot(holder, out var cell) || _battery.GetCharge(cell.Value.AsNullable()) < 2f ||
            !TryComp<BodyComponent>(socket, out var body) || body.Organs == null)
            return false;
        foreach (var organ in body.Organs.ContainedEntities)
        {
            if (TryComp<NCCyberwareComponent>(organ, out var mechanical) &&
                (!mechanical.Operational || mechanical.FunctionalEfficiency < 0.5f))
                return false;
        }
        return true;
    }

    public bool CanUndock(Entity<NCBorgCoreComponent?> core, EntityUid user)
        => Resolve(core, ref core.Comp, false) && CanReach(user, core) && HasServiceProbe(user) &&
           _anatomy.TryGetOrgan(core, "Torso", out var torso) && HasComp<NCBorgStandardShellComponent>(torso);

    public bool TryUndock(Entity<NCBorgCoreComponent?> core, EntityUid user)
    {
        if (!Resolve(core, ref core.Comp, false) || !CanUndock(core, user))
            return false;
        SettleCore(core);
        _cyberware.TryAdvanceBodyPower(core, _timing.CurTime);
        var socket = Spawn("NCBorgEmptySocket", Transform(core).Coordinates);
        if (!_detachment.TryTransferOrgan(core, socket, "Torso", "Head"))
        {
            QueueDel(socket);
            return false;
        }
        RefreshPowerBinding(core);
        RefreshPowerBinding(socket);
        RefreshSupport((core.Owner, core.Comp!));
        return true;
    }

    private bool Prepared(Entity<NCBorgCoreComponent?> core)
    {
        // A read-only live resource check; an examine or a cached Operational flag never authorizes conversion.
        return core.Comp != null && GetSupportCharge(core) >= core.Comp.PowerPerSecond * 15f &&
               core.Comp.EmpDisabledUntil <= _timing.CurTime &&
               _solutions.TryGetSolution(core.Owner, SupportSolution, out _, out var medium) &&
               medium.GetTotalPrototypeQuantity(Perfusate).Float() >= core.Comp.PerfusatePerSecond * 15f &&
               medium.GetTotalPrototypeQuantity(Oxygen).Float() >= core.Comp.OxygenPerSecond * 15f &&
               medium.GetTotalPrototypeQuantity(Dialysate).Float() >= core.Comp.DialysatePerSecond * 15f &&
               medium.GetTotalPrototypeQuantity(Coolant).Float() >= core.Comp.CoolantPerSecond * 15f &&
               ProvidersReady(core) &&
               TryComp<Content.Shared.Nutrition.Components.HungerComponent>(core, out var hunger) &&
               _hunger.GetHunger(hunger) >= hunger.Thresholds[Content.Shared.Nutrition.Components.HungerThreshold.Starving] &&
               MathF.Abs(core.Comp.Temperature - 310.15f) < 2f;
    }

    private bool ProvidersReady(EntityUid core)
    {
        if (!TryComp<BodyComponent>(core, out var body) || body.Organs == null)
            return false;
        var count = 0;
        foreach (var organ in body.Organs.ContainedEntities)
        {
            if (!HasComp<NCBorgSupportProviderComponent>(organ))
                continue;
            if (!TryComp<NCOrganIntegrityComponent>(organ, out var integrity) || integrity.FunctionalEfficiency < 0.95f ||
                TryComp<NCCyberwareComponent>(organ, out var mechanical) && mechanical.FunctionalEfficiency < 0.95f ||
                !TryComp<NCOrganFunctionComponent>(organ, out var function) ||
                function.BaseCapacity <= 0f || function.CurrentCapacity / function.BaseCapacity < 0.95f)
                return false;
            count++;
        }
        return count == 5;
    }

    private void SettleCore(Entity<NCBorgCoreComponent?> core)
    {
        if (core.Comp != null && core.Comp.Occupied)
            _scheduler.ProcessNow(core, _timing.CurTime - core.Comp.LastAdvancedAt);
        else if (core.Comp != null)
            RefreshSupport((core.Owner, core.Comp));
    }

    private bool CanReach(EntityUid user, EntityUid target)
        => Exists(user) && !TerminatingOrDeleted(target) && _interaction.InRangeUnobstructed(user, target, 2f);

    private bool HasServiceProbe(EntityUid user)
        => _hands.TryGetActiveItem(user, out var held) && TryComp<NCSurgeryToolComponent>(held, out var tool) &&
           tool.Capabilities.ContainsKey("NCSurgeryToolCyberwareDisconnection");

    private bool HasBrainAccess(EntityUid patient, NCSurgicalSiteComponent site)
    {
        if (site.Incisions.Count == 0)
            return site.AccessDepth is NCSurgicalAccessDepth.OrganExposed or NCSurgicalAccessDepth.ProcedureInProgress or
                NCSurgicalAccessDepth.ReadyForClosure;
        if (!_anatomy.TryGetProfile(patient, out var anatomy) ||
            !_borgPrototypes.TryIndex(anatomy.SurgicalField, out var field) ||
            !field.Structures.TryGetValue("Brain", out var brain))
            return false;
        return site.Incisions.Any(incision => !incision.Sutured && incision.BoneCasingOpen &&
            incision.Depth >= brain.MinimumDepth && incision.EdgeSeparation >= field.MinimumOpenRetraction &&
            brain.Zones.Contains(incision.TargetZone));
    }
}
