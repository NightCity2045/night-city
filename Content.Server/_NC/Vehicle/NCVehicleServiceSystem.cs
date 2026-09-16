// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Server.Stack;
using Content.Shared.Stacks;
using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Assembly;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared._NC.Vehicle.Prototypes;
using Content.Shared.ActionBlocker;
using Content.Shared.DoAfter;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Tools.Systems;
using Content.Shared.Verbs;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Vehicle;

/// <summary>Physical, interruptible workshop operations. Items change hands only after final validation.</summary>
public sealed partial class NCVehicleServiceSystem : EntitySystem
{
    [Dependency] private NCVehicleAssemblySystem _assembly = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedToolSystem _tools = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private NCVehicleDamageSystem _damage = default!;
    [Dependency] private NCVehicleAccessSystem _access = default!;
    [Dependency] private StackSystem _stacks = default!;
    private readonly HashSet<EntityUid> _lifted = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCVehicleComponent, GetVerbsEvent<InteractionVerb>>(OnVerbs);
        SubscribeLocalEvent<NCVehicleComponent, NCVehicleServiceMessage>(OnMessage);
        SubscribeLocalEvent<NCVehicleComponent, NCVehicleServiceDoAfterEvent>(OnComplete);
        SubscribeLocalEvent<NCVehicleComponent, NCVehicleRightDoAfterEvent>(OnRightComplete);
        SubscribeLocalEvent<NCVehicleComponent, NCVehicleTuneMessage>(OnTune);
        SubscribeLocalEvent<NCVehicleComponent, NCVehicleTuneDoAfterEvent>(OnTuneComplete);
        SubscribeLocalEvent<NCVehicleWorkshopEquipmentComponent, EntityTerminatingEvent>(OnEquipmentDeleted);
        SubscribeLocalEvent<NCVehicleWorkshopEquipmentComponent, AnchorStateChangedEvent>(OnEquipmentAnchored);
        SubscribeLocalEvent<NCVehicleComponent, EntityTerminatingEvent>(OnVehicleDeleted);
    }

    private void OnVerbs(Entity<NCVehicleComponent> ent, ref GetVerbsEvent<InteractionVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract)
            return;
        var user = args.User;
        args.Verbs.Add(new InteractionVerb { Text = Loc.GetString("nc-vehicle-service"), Act = () => TryOpen(ent, user) });
    }

    public bool TryOpen(EntityUid uid, EntityUid user)
    {
        if (!TryComp<NCVehicleComponent>(uid, out var vehicle) || !_prototypes.TryIndex(vehicle.Chassis, out var chassis) ||
            !_interaction.InRangeUnobstructed(user, uid, range: chassis.ServiceRange) || !_blocker.CanConsciouslyPerformAction(user))
            return false;
        Refresh(uid);
        return _ui.TryOpenUi(uid, NCVehicleUiKey.Service, user);
    }

    private void OnMessage(Entity<NCVehicleComponent> ent, ref NCVehicleServiceMessage args)
    {
        // A forged old list-UI packet cannot bypass individual workshop points or occlusion.
        if (_prototypes.TryIndex(ent.Comp.Chassis, out var chassis) && chassis.Workshop != null &&
            args.Action is not (NCVehicleServiceAction.Hood or NCVehicleServiceAction.Lift or NCVehicleServiceAction.Right))
            return;
        TryStart(ent, args.Actor, args.Slot, args.Action);
    }

    public bool TryStart(EntityUid uid, EntityUid user, string slot, NCVehicleServiceAction action)
    {
        if (!TryComp<NCVehicleAssemblyComponent>(uid, out var assembly) ||
            !TryComp<NCVehicleComponent>(uid, out var vehicle) || !_prototypes.TryIndex(vehicle.Chassis, out var chassis) ||
            !CanWork(uid, user, chassis))
            return false;
        if (action == NCVehicleServiceAction.Hood)
        {
            if (!_access.CanOpen(uid))
                return false;
            assembly.HoodOpen = !assembly.HoodOpen;
            // Hood position is externally observable even though the detailed assembly stays private.
            vehicle.HoodOpen = assembly.HoodOpen;
            Dirty(uid, vehicle);
            Refresh(uid);
            return true;
        }
        if (action == NCVehicleServiceAction.Right)
        {
            if (!CanRight(uid, user, chassis) || assembly.ReservedSlots.ContainsKey(string.Empty))
                return false;
            if (!_doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, user, chassis.RightingSeconds,
                new NCVehicleRightDoAfterEvent(), uid, uid)
                { BreakOnMove = true, BreakOnDamage = true, NeedHand = true }))
                return false;
            assembly.ReservedSlots[string.Empty] = user;
            return true;
        }
        if (action == NCVehicleServiceAction.Lift)
        {
            var lift = FindEquipment(uid, chassis, false);
            if (assembly.Lift == null && lift == null)
                return false;
            assembly.Lift = assembly.Lift == null ? lift : null;
            if (assembly.Lift == null)
                _lifted.Remove(uid);
            else
                _lifted.Add(uid);
            // A lift holds the existing rigid body in place; it does not teleport or anchor to tile centers.
            _physics.SetBodyType(uid, assembly.Lift == null ? BodyType.Dynamic : BodyType.Static);
            _assembly.TryRebuild(uid);
            Refresh(uid);
            return true;
        }
        if (assembly.ReservedSlots.TryGetValue(slot, out var worker))
        {
            if (!TerminatingOrDeleted(worker))
                return false;
            assembly.ReservedSlots.Remove(slot);
        }
        var used = _hands.GetActiveItem(user);
        if (used == null || !CanService(uid, user, slot, action, used.Value))
            return false;
        var ev = new NCVehicleServiceDoAfterEvent { Slot = slot, Action = action, Revision = assembly.Revision };
        var args = new DoAfterArgs(EntityManager, user, chassis.Slots[slot].ServiceSeconds, ev, uid, uid, used)
        { BreakOnMove = true, BreakOnDamage = true, NeedHand = true };
        if (!_doAfter.TryStartDoAfter(args))
            return false;
        assembly.ReservedSlots.Add(slot, user);
        return true;
    }

    private bool CanWork(EntityUid uid, EntityUid user, NCVehicleChassisPrototype chassis)
    {
        return !TerminatingOrDeleted(uid) && _blocker.CanConsciouslyPerformAction(user) &&
            _interaction.InRangeUnobstructed(user, uid, range: chassis.ServiceRange) &&
            !HasComp<NCVehicleOccupantComponent>(user) &&
            TryComp<NCVehicleDriveComponent>(uid, out var drive) && !drive.EngineRunning && !drive.Cranking &&
            TryComp<PhysicsComponent>(uid, out var body) && body.LinearVelocity.Length() <= chassis.ServiceMaxSpeed;
    }

    private bool CanRight(EntityUid uid, EntityUid user, NCVehicleChassisPrototype chassis)
    {
        if (!CanWork(uid, user, chassis) || !Comp<NCVehicleDriveComponent>(uid).Overturned ||
            Comp<NCVehicleDriveComponent>(uid).Driver != null || FindEquipment(uid, chassis, true) == null)
            return false;
        if (TryComp<Robust.Shared.Containers.ContainerManagerComponent>(uid, out var containers))
            foreach (var id in chassis.Seats.Keys)
                if (containers.Containers.TryGetValue("nc-vehicle-" + id, out var seat) && seat.ContainedEntities.Count != 0)
                    return false;
        return true;
    }

    private void OnRightComplete(Entity<NCVehicleComponent> ent, ref NCVehicleRightDoAfterEvent args)
    {
        if (args.Handled || !TryComp<NCVehicleAssemblyComponent>(ent, out var assembly) ||
            !assembly.ReservedSlots.TryGetValue(string.Empty, out var worker) || worker != args.User)
            return;
        assembly.ReservedSlots.Remove(string.Empty);
        args.Handled = true;
        if (!args.Cancelled && _prototypes.TryIndex(ent.Comp.Chassis, out var chassis) && CanRight(ent, args.User, chassis))
            _damage.TryRight(ent);
        Refresh(ent);
    }

    public bool CanService(EntityUid uid, EntityUid user, string slot, NCVehicleServiceAction action, EntityUid used)
    {
        if (!TryComp<NCVehicleAssemblyComponent>(uid, out var assembly) ||
            !TryComp<NCVehicleComponent>(uid, out var vehicle) || !_prototypes.TryIndex(vehicle.Chassis, out var chassis) ||
            !chassis.Slots.TryGetValue(slot, out var definition) || !CanWork(uid, user, chassis) ||
            _hands.GetActiveItem(user) != used || !float.IsFinite(definition.ServiceSeconds) || definition.ServiceSeconds < 0)
            return false;
        var modulePosition = _transform.ToMapCoordinates(new Robust.Shared.Map.EntityCoordinates(uid, definition.Position));
        if (Vector2.DistanceSquared(_transform.GetWorldPosition(user), modulePosition.Position) > chassis.ServiceRange * chassis.ServiceRange ||
            (definition.RequiresOpenHood && !assembly.HoodOpen) ||
            (definition.RequiresLift && (assembly.Lift is not { } lift || !IsEquipmentAvailable(uid, lift, false))) ||
            (definition.RequiresHoist && action is NCVehicleServiceAction.Remove or NCVehicleServiceAction.Install &&
                FindEquipment(uid, chassis, true) == null))
            return false;
        var occupied = assembly.Installed.TryGetValue(slot, out var part);
        if (action == NCVehicleServiceAction.Inspect)
            return occupied && TryComp<NCVehicleDiagnosticToolComponent>(used, out var instrument) && instrument.Level > 0 &&
                _prototypes.TryIndex(part.Prototype, out var diagnosedPart) &&
                (instrument.Categories.Count == 0 || instrument.Categories.Contains(diagnosedPart.RepairCategory));
        if (action == NCVehicleServiceAction.Install)
            return !occupied && TryComp<NCVehiclePartItemComponent>(used, out var item) &&
                _assembly.CanSetPart(uid, slot, new NCVehicleInstalledPart(item.Part, item.Condition, false));
        if (!_tools.HasQuality(used, definition.ToolQuality))
            return false;
        return action switch
        {
            NCVehicleServiceAction.Inspect => true,
            NCVehicleServiceAction.Disconnect => occupied && part.Connected,
            NCVehicleServiceAction.Connect => occupied && part.Fastened && !part.Connected,
            NCVehicleServiceAction.Fasten => occupied && !part.Fastened && !part.Connected && FindFasteners(user, chassis) != null,
            NCVehicleServiceAction.Unfasten => occupied && part.Fastened && !part.Connected,
            NCVehicleServiceAction.Remove => occupied && !part.Fastened && !part.Connected && _assembly.CanSetPart(uid, slot, null) &&
                _prototypes.TryIndex(part.Prototype, out var prototype) && prototype.Item != null,
            _ => false,
        };
    }

    private EntityUid? FindFasteners(EntityUid user, NCVehicleChassisPrototype chassis)
    {
        foreach (var held in _hands.EnumerateHeld(user))
            if (TryComp<StackComponent>(held, out var stack) && stack.StackTypeId == chassis.Fasteners && stack.Count > 0 && !stack.Unlimited)
                return held;
        return null;
    }

    private EntityUid? FindEquipment(EntityUid uid, NCVehicleChassisPrototype chassis, bool hoist)
    {
        foreach (var equipment in _lookup.GetEntitiesInRange<NCVehicleWorkshopEquipmentComponent>(
                     Transform(uid).Coordinates, chassis.WorkshopSearchRange))
        {
            if (IsEquipmentAvailable(uid, equipment.Owner, hoist))
                return equipment.Owner;
        }
        return null;
    }

    private bool IsEquipmentAvailable(EntityUid uid, EntityUid equipment, bool hoist)
        => !TerminatingOrDeleted(equipment) && TryComp<NCVehicleWorkshopEquipmentComponent>(equipment, out var comp) &&
            (hoist ? comp.Hoist : comp.Lift) && Transform(equipment).Anchored &&
            _interaction.InRangeUnobstructed(uid, equipment, range: comp.Range);

    private void OnComplete(Entity<NCVehicleComponent> ent, ref NCVehicleServiceDoAfterEvent args)
    {
        if (args.Handled || !TryComp<NCVehicleAssemblyComponent>(ent, out var assembly) ||
            !assembly.ReservedSlots.TryGetValue(args.Slot, out var worker) || worker != args.User)
            return;
        assembly.ReservedSlots.Remove(args.Slot);
        args.Handled = true;
        if (args.Cancelled || assembly.Revision != args.Revision || args.Used is not { } used ||
            !CanService(ent, args.User, args.Slot, args.Action, used))
            return;
        switch (args.Action)
        {
            case NCVehicleServiceAction.Fasten:
                var chassis = _prototypes.Index(ent.Comp.Chassis);
                if (FindFasteners(args.User, chassis) is { } fasteners &&
                    _stacks.TryUse(fasteners, 1))
                    _assembly.TrySetPart(ent, args.Slot, assembly.Installed[args.Slot] with { Fastened = true });
                break;
            case NCVehicleServiceAction.Unfasten:
                if (_assembly.TrySetPart(ent, args.Slot, assembly.Installed[args.Slot] with { Fastened = false }))
                    _stacks.SpawnAtPosition(1, _prototypes.Index(ent.Comp.Chassis).Fasteners, Transform(args.User).Coordinates);
                break;
            case NCVehicleServiceAction.Inspect:
                assembly.DiagnosedSlots.Add(args.Slot);
                var exact = Comp<NCVehicleDiagnosticToolComponent>(used).Level >= 2;
                var measured = assembly.Installed[args.Slot].Condition;
                assembly.InspectedConditions[args.Slot] = exact ? measured : MathF.Round(measured * 4) / 4;
                if (exact)
                    assembly.PreciselyDiagnosedSlots.Add(args.Slot);
                else
                    assembly.PreciselyDiagnosedSlots.Remove(args.Slot);
                break;
            case NCVehicleServiceAction.Disconnect:
            case NCVehicleServiceAction.Connect:
                _assembly.TrySetPart(ent, args.Slot, assembly.Installed[args.Slot] with
                { Connected = args.Action == NCVehicleServiceAction.Connect });
                break;
            case NCVehicleServiceAction.Remove:
                var part = assembly.Installed[args.Slot];
                var prototype = _prototypes.Index(part.Prototype);
                // Spawn first. A failed graph mutation deletes the new item, preserving the installed module.
                var dropped = Spawn(prototype.Item!.Value, Transform(args.User).Coordinates);
                var item = EnsureComp<NCVehiclePartItemComponent>(dropped);
                item.Part = part.Prototype;
                item.Condition = part.Condition;
                item.StoredCharge = part.StoredCharge;
                Dirty(dropped, item);
                if (!_assembly.TrySetPart(ent, args.Slot, null))
                    QueueDel(dropped);
                break;
            case NCVehicleServiceAction.Install:
                var source = Comp<NCVehiclePartItemComponent>(used);
                if (_assembly.TrySetPart(ent, args.Slot, new NCVehicleInstalledPart(source.Part, source.Condition, false, false, source.StoredCharge)))
                    Del(used);
                break;
        }
        Refresh(ent);
    }

    private void Refresh(EntityUid uid)
    {
        if (!TryComp<NCVehicleAssemblyComponent>(uid, out var assembly) || !TryComp<NCVehicleComponent>(uid, out var vehicle) ||
            !_prototypes.TryIndex(vehicle.Chassis, out var chassis))
            return;
        if (chassis.Workshop != null)
        {
            var changed = new NCVehicleAssemblyChangedEvent(string.Empty);
            RaiseLocalEvent(uid, ref changed);
            return;
        }
        var rows = new List<NCVehicleServiceRow>();
        foreach (var (slot, definition) in chassis.Slots)
        {
            var occupied = assembly.Installed.TryGetValue(slot, out var part);
            rows.Add(new NCVehicleServiceRow(slot, definition.Name, occupied, occupied && part.Connected,
                occupied && assembly.InspectedConditions.TryGetValue(slot, out var measured) ? measured : null,
                assembly.PreciselyDiagnosedSlots.Contains(slot), occupied && part.Fastened));
        }
        var calibrations = new List<NCVehicleCalibrationChoice>();
        foreach (var id in chassis.Calibrations)
            if (_prototypes.TryIndex(id, out var calibration))
                calibrations.Add(new NCVehicleCalibrationChoice(id, calibration.Name));
        _ui.SetUiState(uid, NCVehicleUiKey.Service, new NCVehicleServiceState(rows.ToArray(), assembly.HoodOpen, assembly.Lift != null, calibrations.ToArray()));
    }

    private void OnVehicleDeleted(Entity<NCVehicleComponent> ent, ref EntityTerminatingEvent args) => _lifted.Remove(ent);

    private void OnEquipmentDeleted(Entity<NCVehicleWorkshopEquipmentComponent> ent, ref EntityTerminatingEvent args)
        => ReleaseEquipment(ent);

    private void OnEquipmentAnchored(Entity<NCVehicleWorkshopEquipmentComponent> ent, ref AnchorStateChangedEvent args)
    {
        if (!args.Anchored)
            ReleaseEquipment(ent);
    }

    private void ReleaseEquipment(EntityUid uid)
    {
        foreach (var car in _lifted.ToArray())
        {
            if (!TryComp<NCVehicleAssemblyComponent>(car, out var assembly) || assembly.Lift != uid)
                continue;
            assembly.Lift = null;
            _lifted.Remove(car);
            _physics.SetBodyType(car, BodyType.Dynamic);
            _assembly.TryRebuild(car);
            Refresh(car);
        }
    }
}
