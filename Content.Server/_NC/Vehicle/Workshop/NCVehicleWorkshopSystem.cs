// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Assembly;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared._NC.Vehicle.Prototypes;
using Content.Shared._NC.Vehicle.Workshop;
using Content.Shared.ActionBlocker;
using Content.Shared.DoAfter;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Stacks;
using Content.Shared.Tools.Systems;
using Content.Server.Stack;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Map;
using Robust.Shared.Player;

namespace Content.Server._NC.Vehicle.Workshop;

/// <summary>Authoritative visual workshop. A drawn point is a request, never permission to mutate a slot.</summary>
public sealed partial class NCVehicleWorkshopSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private SharedToolSystem _tools = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private StackSystem _stacks = default!;
    [Dependency] private NCVehicleAssemblySystem _assembly = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private NCVehiclePartPhysicsSystem _partPhysics = default!;
    [Dependency] private NCVehicleFluidsSystem _fluids = default!;
    private readonly Dictionary<EntityUid, HashSet<EntityUid>> _observers = new();

    public override void Initialize()
    {
        SubscribeLocalEvent<NCVehicleComponent, BoundUIOpenedEvent>(OnOpen);
        SubscribeLocalEvent<NCVehicleComponent, BoundUIClosedEvent>(OnClose);
        SubscribeLocalEvent<NCVehicleComponent, NCVehicleWorkshopRefresh>(OnRefresh);
        SubscribeLocalEvent<NCVehicleComponent, NCVehicleWorkshopTorqueRequest>(OnTorqueRequest);
        SubscribeLocalEvent<NCVehicleComponent, NCVehicleWorkshopRequest>(OnRequest);
        SubscribeLocalEvent<NCVehicleComponent, NCVehicleWorkshopDoAfter>(OnComplete);
        SubscribeLocalEvent<NCVehicleComponent, NCVehicleAssemblyChangedEvent>(OnChanged);
        SubscribeLocalEvent<NCVehicleAssemblyComponent, EntityTerminatingEvent>(OnVehicleDeleted);
        SubscribeLocalEvent<NCVehicleWorkshopSessionComponent, ComponentShutdown>(OnSessionShutdown);
        SubscribeLocalEvent<NCVehicleWorkshopSessionComponent, PlayerDetachedEvent>(OnPlayerDetached);
    }

    private bool TryProfile(EntityUid uid, out NCVehicleChassisPrototype chassis, out NCVehicleWorkshopPrototype profile)
    {
        chassis = default!;
        profile = default!;
        return TryComp<NCVehicleComponent>(uid, out var vehicle) && _prototypes.TryIndex(vehicle.Chassis, out chassis!) &&
            chassis.Workshop is { } id && _prototypes.TryIndex(id, out profile!);
    }

    private void OnOpen(Entity<NCVehicleComponent> ent, ref BoundUIOpenedEvent args)
    {
        if (!Equals(args.UiKey, NCVehicleUiKey.Service) || !TryProfile(ent, out _, out _))
            return;
        var session = EnsureComp<NCVehicleWorkshopSessionComponent>(args.Actor);
        if (session.Vehicle != ent.Owner)
        {
            Release(args.Actor, session.Vehicle);
            session.Vehicle = ent;
            session.Measurements.Clear();
            session.Sequence = 0;
            session.Pending = false;
        }
        if (!_observers.TryGetValue(ent, out var observers))
            _observers[ent] = observers = new();
        observers.Add(args.Actor);
        SendSnapshot(ent, args.Actor);
    }

    private void OnClose(Entity<NCVehicleComponent> ent, ref BoundUIClosedEvent args)
    {
        if (!Equals(args.UiKey, NCVehicleUiKey.Service))
            return;
        Release(args.Actor, ent);
        if (TryComp<NCVehicleWorkshopSessionComponent>(args.Actor, out var session))
            session.Pending = false;
    }

    private void OnSessionShutdown(Entity<NCVehicleWorkshopSessionComponent> ent, ref ComponentShutdown args) => Release(ent, ent.Comp.Vehicle);
    private void OnPlayerDetached(Entity<NCVehicleWorkshopSessionComponent> ent, ref PlayerDetachedEvent args)
    {
        // A disconnected mechanic cannot leave a reservation behind or finish an unattended operation.
        Release(ent, ent.Comp.Vehicle);
        ent.Comp.Pending = false;
    }
    private void OnVehicleDeleted(Entity<NCVehicleAssemblyComponent> ent, ref EntityTerminatingEvent args)
    {
        if (!_observers.Remove(ent, out var observers)) return;
        foreach (var user in observers)
            if (TryComp<NCVehicleWorkshopSessionComponent>(user, out var session) && session.Vehicle == ent.Owner)
            { session.Pending = false; session.Vehicle = null; }
    }
    private void Release(EntityUid user, EntityUid? vehicle)
    {
        if (vehicle is not { } car) return;
        if (_observers.TryGetValue(car, out var observers))
        {
            observers.Remove(user);
            if (observers.Count == 0)
                _observers.Remove(car);
        }
        if (TryComp<NCVehicleAssemblyComponent>(car, out var assembly))
            foreach (var (slot, worker) in assembly.ReservedSlots.ToArray())
                if (worker == user)
                    assembly.ReservedSlots.Remove(slot);
    }

    private void OnRefresh(Entity<NCVehicleComponent> ent, ref NCVehicleWorkshopRefresh args)
    {
        if (!TryComp<NCVehicleWorkshopSessionComponent>(args.Actor, out var session) || session.Vehicle != ent.Owner ||
            _timing.CurTime < session.NextRefresh || !TryProfile(ent, out _, out var profile))
            return;
        session.NextRefresh = _timing.CurTime + TimeSpan.FromSeconds(profile.RefreshSeconds);
        SendSnapshot(ent, args.Actor);
    }

    private void OnChanged(Entity<NCVehicleComponent> ent, ref NCVehicleAssemblyChangedEvent args) => RefreshObservers(ent);

    public void RefreshObservers(EntityUid uid)
    {
        if (!_observers.TryGetValue(uid, out var observers))
            return;
        foreach (var user in observers.ToArray())
        {
            if (TerminatingOrDeleted(user) || !_ui.IsUiOpen(uid, NCVehicleUiKey.Service, user))
                Release(user, uid);
            else
                SendSnapshot(uid, user);
        }
    }

    public void SendSnapshot(EntityUid uid, EntityUid user)
    {
        if (!TryProfile(uid, out var chassis, out var profile) || !TryComp<NCVehicleAssemblyComponent>(uid, out var assembly) ||
            !TryComp<NCVehicleWorkshopSessionComponent>(user, out var session) || session.Vehicle != uid)
            return;
        var parts = new List<NCVehicleWorkshopPartState>();
        foreach (var slot in chassis.Slots.Keys)
        {
            var installed = assembly.Installed.TryGetValue(slot, out var part);
            var revision = assembly.SlotRevisions.GetValueOrDefault(slot);
            var measured = session.Measurements.TryGetValue(slot, out var measurement);
            parts.Add(new(slot, installed, revision, part.WorkPoints == null ? new() : new(part.WorkPoints),
                part.PointTorque == null ? new() : new(part.PointTorque),
                measured ? measurement.Condition : null, measurement.Exact, measured && measurement.Revision != revision,
                assembly.ReservedSlots.ContainsKey(slot)));
        }
        var choices = new List<NCVehicleCalibrationChoice>();
        foreach (var id in chassis.Calibrations)
            if (_prototypes.TryIndex(id, out var tuning))
                choices.Add(new(id, tuning.Name));
        EntProtoId? tool = _hands.GetActiveItem(user) is { } held ? MetaData(held).EntityPrototype?.ID : null;
        NCVehicleTorqueToolComponent? torqueTool = null;
        if (_hands.GetActiveItem(user) is { } torqueUid)
            TryComp(torqueUid, out torqueTool);
        // Addressed content event mirrors surgery's private observations and does not depend on BUI state replication order.
        RaiseNetworkEvent(new NCVehicleWorkshopSnapshot(GetNetEntity(uid), profile.ID, parts.ToArray(),
            assembly.HoodOpen, assembly.Lift != null, session.Feedback, session.Sequence, session.Pending, tool,
            torqueTool?.Torque, torqueTool?.Minimum, torqueTool?.Maximum, torqueTool?.Step,
            choices.ToArray(), _fluids.GetWorkshopState(uid)), user);
    }

    private void OnTorqueRequest(Entity<NCVehicleComponent> ent, ref NCVehicleWorkshopTorqueRequest args)
    {
        if (!float.IsFinite(args.Torque) || !TryComp<NCVehicleWorkshopSessionComponent>(args.Actor, out var session) ||
            session.Vehicle != ent.Owner || session.Pending || !_ui.IsUiOpen(ent.Owner, NCVehicleUiKey.Service, args.Actor) ||
            _hands.GetActiveItem(args.Actor) is not { } held || !TryComp<NCVehicleTorqueToolComponent>(held, out var tool))
            return;
        var step = Math.Max(1, tool.Step);
        tool.Torque = Math.Clamp(MathF.Round(args.Torque / step) * step, tool.Minimum, tool.Maximum);
        Dirty(held, tool);
        session.Feedback = "nc-workshop-torque-set";
        SendSnapshot(ent, args.Actor);
    }

    private void OnRequest(Entity<NCVehicleComponent> ent, ref NCVehicleWorkshopRequest args) => TryStart(ent, args.Actor, args);

    public bool TryStart(EntityUid uid, EntityUid user, NCVehicleWorkshopRequest request)
    {
        if (!TryComp<NCVehicleWorkshopSessionComponent>(user, out var session) || session.Vehicle != uid ||
            !_ui.IsUiOpen(uid, NCVehicleUiKey.Service, user) || !NCVehicleControlSystem.Newer(request.Sequence, session.Sequence) || session.Pending)
            return false;
        session.Sequence = request.Sequence;
        if (!CanOperate(uid, user, request, out var failure) || !TryProfile(uid, out var chassis, out var profile))
        {
            session.Feedback = failure;
            SendSnapshot(uid, user);
            return false;
        }
        var node = profile.Nodes[request.Node];
        var used = _hands.GetActiveItem(user);
        var seconds = request.Action == NCVehicleWorkshopAction.Inspect ? profile.InspectSeconds :
            request.Point.Length > 0 ? profile.PointSeconds : profile.TransferSeconds;
        var ev = new NCVehicleWorkshopDoAfter { View = request.View, Node = request.Node, Point = request.Point,
            Position = request.Position, Action = request.Action, Revision = request.Revision, Sequence = request.Sequence };
        if (!_doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, user, seconds, ev, uid, uid, used)
            { BreakOnMove = true, BreakOnDamage = true, NeedHand = true, DistanceThreshold = chassis.ServiceRange }))
        {
            session.Feedback = "nc-workshop-busy";
            SendSnapshot(uid, user);
            return false;
        }
        var assembly = Comp<NCVehicleAssemblyComponent>(uid);
        foreach (var slot in TransferSlots(uid, node.Slot, request.Action))
            assembly.ReservedSlots[slot] = user;
        session.Pending = true;
        session.Feedback = "nc-workshop-working";
        RefreshObservers(uid);
        return true;
    }

    public bool CanOperate(EntityUid uid, EntityUid user, NCVehicleWorkshopRequest request, out LocId failure)
    {
        failure = "nc-workshop-unavailable";
        if (string.IsNullOrEmpty(request.View) || string.IsNullOrEmpty(request.Node) || request.Point == null ||
            request.View.Length > 64 || request.Node.Length > 64 || request.Point.Length > 64 ||
            !float.IsFinite(request.Position.X) || !float.IsFinite(request.Position.Y) ||
            !TryProfile(uid, out var chassis, out var profile) || !profile.Views.TryGetValue(request.View, out var view) ||
            !profile.Nodes.TryGetValue(request.Node, out var node) || node.View != request.View ||
            node.DetachedOnly ||
            !TryComp<NCVehicleAssemblyComponent>(uid, out var assembly) || !chassis.Slots.TryGetValue(node.Slot, out var slot) ||
            !_blocker.CanConsciouslyPerformAction(user) || HasComp<NCVehicleOccupantComponent>(user) ||
            !_interaction.InRangeUnobstructed(user, uid, range: chassis.ServiceRange) ||
            !TryComp<NCVehicleDriveComponent>(uid, out var drive) || drive.EngineRunning || drive.Cranking ||
            !TryComp<PhysicsComponent>(uid, out var body) || body.LinearVelocity.Length() > chassis.ServiceMaxSpeed)
            return false;
        var accessPosition = NCVehicleWorkshopGeometry.AccessPosition(profile, request.View, view);
        var accessRange = NCVehicleWorkshopGeometry.AccessRange(profile, request.View, view);
        var access = _transform.ToMapCoordinates(new EntityCoordinates(uid, accessPosition));
        if (Vector2.DistanceSquared(_transform.GetWorldPosition(user), access.Position) > accessRange * accessRange)
        { failure = "nc-workshop-side"; return false; }
        if ((view.RequiresHood || slot.RequiresOpenHood) && !assembly.HoodOpen)
        { failure = "nc-workshop-hood"; return false; }
        if ((view.RequiresLift || slot.RequiresLift) && (assembly.Lift is not { } lift || TerminatingOrDeleted(lift) || !Transform(lift).Anchored))
        { failure = "nc-workshop-lift"; return false; }
        if (!NCVehicleWorkshopGeometry.Exposed(slot, assembly.Installed.ContainsKey))
        { failure = "nc-workshop-covered"; return false; }
        if (assembly.SlotRevisions.GetValueOrDefault(node.Slot) != request.Revision)
        { failure = "nc-workshop-stale"; return false; }
        if (assembly.ReservedSlots.TryGetValue(node.Slot, out var worker) && worker != user || assembly.ReservedSlots.ContainsKey(string.Empty))
        { failure = "nc-workshop-busy"; return false; }
        foreach (var id in TransferSlots(uid, node.Slot, request.Action))
            if (assembly.ReservedSlots.TryGetValue(id, out var owner) && owner != user)
            { failure = "nc-workshop-busy"; return false; }
        if (!NCVehicleWorkshopGeometry.Contains(profile, request.Node, node, request.Position))
        { failure = "nc-workshop-target"; return false; }
        // Same authored draw order and exposed geometry as the client; a forged lower node cannot bypass a cover.
        foreach (var (otherId, other) in profile.Nodes)
            if (!other.DetachedOnly && other.View == node.View && (other.Layer > node.Layer ||
                    other.Layer == node.Layer && string.CompareOrdinal(otherId, request.Node) > 0) && assembly.Installed.ContainsKey(other.Slot) &&
                NCVehicleWorkshopGeometry.Contains(profile, otherId, other, request.Position))
            { failure = "nc-workshop-covered"; return false; }
        var held = _hands.GetActiveItem(user);
        var occupied = assembly.Installed.TryGetValue(node.Slot, out var part);
        if (request.Action == NCVehicleWorkshopAction.Fill)
        {
            failure = "nc-workshop-fluid-container";
            return occupied && node.Fluid is { } fluid &&
                TryComp<NCVehicleFluidBottleComponent>(held, out var bottle) && bottle.Circuit == fluid;
        }
        if (request.Action == NCVehicleWorkshopAction.Drain)
        {
            failure = "nc-workshop-drain-pan";
            return occupied && node.Fluid != null && HasComp<NCVehicleFluidDrainPanComponent>(held);
        }
        if (request.Action == NCVehicleWorkshopAction.Inspect)
        {
            failure = "nc-workshop-diagnostic-tool";
            return occupied && _prototypes.TryIndex(part.Prototype, out var module) &&
                TryGetDiagnosticLevel(uid, held, module.RepairCategory, out _);
        }
        if (request.Action == NCVehicleWorkshopAction.Install)
        {
            failure = "nc-workshop-compatible-part";
            return !occupied && TryComp<NCVehiclePartItemComponent>(held, out var item) &&
                (!slot.RequiresHoist || FindHoist(uid, chassis) != null) &&
                TryInstallChanges(uid, held!.Value, node.Slot, out var changes) && _assembly.CanSetParts(uid, changes);
        }
        if (!occupied)
        { failure = "nc-workshop-empty"; return false; }
        if (request.Action == NCVehicleWorkshopAction.Remove)
        {
            failure = "nc-workshop-release-points";
            if (part.WorkPoints == null || part.WorkPoints.Values.Any(attached => attached))
                return false;
            failure = "nc-workshop-dependent";
            if (!_assembly.CanSetParts(uid, RemovalChanges(uid, node.Slot)))
                return false;
            foreach (var (id, definition) in chassis.Slots)
                if (assembly.ReservedSlots.TryGetValue(id, out var owner) && owner != user &&
                    (definition.Requires.Contains(node.Slot) || definition.BlockedBy.Contains(node.Slot)))
                    return false;
            failure = "nc-workshop-hoist";
            if (slot.RequiresHoist && FindHoist(uid, chassis) == null)
                return false;
            return _prototypes.Index(part.Prototype).Item != null;
        }
        if (request.Action != NCVehicleWorkshopAction.Work || !slot.WorkPoints.TryGetValue(request.Point, out var point))
        { failure = "nc-workshop-target"; return false; }
        var local = NCVehicleWorkshopGeometry.LocalPoint(profile, request.Node, node, request.Position);
        if (Vector2.DistanceSquared(local, point.Position) > point.Radius * point.Radius)
        { failure = "nc-workshop-target"; return false; }
        if (held == null || !_tools.HasQuality(held.Value, point.ToolQuality))
        { failure = "nc-workshop-tool"; return false; }
        var attachedNow = part.WorkPoints?.GetValueOrDefault(request.Point) ?? false;
        if (point.Kind == NCVehicleWorkshopPointKind.Fastener && attachedNow &&
            slot.WorkPoints.Any(p => p.Value.Kind == NCVehicleWorkshopPointKind.Connector && part.WorkPoints!.GetValueOrDefault(p.Key)))
        { failure = "nc-workshop-disconnect-first"; return false; }
        if (point.Kind == NCVehicleWorkshopPointKind.Connector && !attachedNow && !part.Fastened)
        { failure = "nc-workshop-fasten-first"; return false; }
        if (point.Kind == NCVehicleWorkshopPointKind.Fastener && !attachedNow && FindFasteners(user, chassis) == null)
        { failure = "nc-workshop-fasteners-needed"; return false; }
        var targetTorque = point.TargetTorque > 0 ? point.TargetTorque : slot.FastenerTorque;
        if (point.Kind == NCVehicleWorkshopPointKind.Fastener && !attachedNow && targetTorque > 0 &&
            !TryComp<NCVehicleTorqueToolComponent>(held, out _))
        { failure = "nc-workshop-torque-tool-needed"; return false; }
        return true;
    }

    private EntityUid? FindFasteners(EntityUid user, NCVehicleChassisPrototype chassis)
    {
        foreach (var held in _hands.EnumerateHeld(user))
            if (TryComp<StackComponent>(held, out var stack) && stack.StackTypeId == chassis.Fasteners && stack.Count > 0 && !stack.Unlimited)
                return held;
        return null;
    }

    private EntityUid? FindHoist(EntityUid uid, NCVehicleChassisPrototype chassis)
    {
        foreach (var equipment in _lookup.GetEntitiesInRange<NCVehicleWorkshopEquipmentComponent>(
                     Transform(uid).Coordinates, chassis.WorkshopSearchRange))
            if (equipment.Comp.Hoist && Transform(equipment).Anchored &&
                _interaction.InRangeUnobstructed(uid, equipment.Owner, range: equipment.Comp.Range))
                return equipment;
        return null;
    }

    private bool TryGetDiagnosticLevel(EntityUid uid, EntityUid? held, string category, out int level)
    {
        level = 0;
        if (held is { } tool && TryComp<NCVehicleDiagnosticToolComponent>(tool, out var instrument) &&
            instrument.Level > 0 && (instrument.Categories.Count == 0 || instrument.Categories.Contains(category)))
        {
            level = instrument.Level;
            return true;
        }
        if (!TryProfile(uid, out var chassis, out _))
            return false;
        foreach (var equipment in _lookup.GetEntitiesInRange<NCVehicleWorkshopEquipmentComponent>(
                     Transform(uid).Coordinates, chassis.WorkshopSearchRange))
        {
            if (equipment.Comp.DiagnosticLevel <= level || !Transform(equipment).Anchored ||
                equipment.Comp.DiagnosticCategories.Count != 0 && !equipment.Comp.DiagnosticCategories.Contains(category) ||
                !_interaction.InRangeUnobstructed(uid, equipment.Owner, range: equipment.Comp.Range))
                continue;
            level = equipment.Comp.DiagnosticLevel;
        }
        return level > 0;
    }

    private HashSet<string> TransferSlots(EntityUid uid, string root, NCVehicleWorkshopAction action)
    {
        var result = new HashSet<string> { root };
        if (action is not (NCVehicleWorkshopAction.Remove or NCVehicleWorkshopAction.Install) || !TryProfile(uid, out var chassis, out _))
            return result;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var slot))
            foreach (var child in chassis.Slots[slot].ExtractWith)
                if (result.Add(child)) pending.Push(child);
        return result;
    }

    private Dictionary<string, NCVehicleInstalledPart?> RemovalChanges(EntityUid uid, string root)
        => TransferSlots(uid, root, NCVehicleWorkshopAction.Remove).ToDictionary(id => id, _ => (NCVehicleInstalledPart?) null);

    private bool TryInstallChanges(EntityUid uid, EntityUid itemUid, string root, out Dictionary<string, NCVehicleInstalledPart?> changes)
    {
        changes = new();
        if (!TryComp<NCVehiclePartItemComponent>(itemUid, out var item)) return false;
        changes[root] = new(item.Part, item.Condition, false, false, item.StoredCharge);
        if (!TryComp<NCVehicleDetachedAssemblyComponent>(itemUid, out var bundle)) return true;
        if (bundle.Chassis != Comp<NCVehicleComponent>(uid).Chassis || bundle.RootSlot != root) return false;
        var slots = TransferSlots(uid, root, NCVehicleWorkshopAction.Install);
        var assembly = Comp<NCVehicleAssemblyComponent>(uid);
        foreach (var (slot, part) in bundle.Parts)
        {
            if (!slots.Contains(slot) || assembly.Installed.ContainsKey(slot)) return false;
            changes[slot] = slot == root ? part with { Condition = item.Condition, StoredCharge = item.StoredCharge } : part;
        }
        return true;
    }

    private void OnComplete(Entity<NCVehicleComponent> ent, ref NCVehicleWorkshopDoAfter args)
    {
        if (args.Handled || !TryComp<NCVehicleWorkshopSessionComponent>(args.User, out var session) || session.Vehicle != ent.Owner ||
            !session.Pending || session.Sequence != args.Sequence || !TryProfile(ent, out var chassis, out var profile) ||
            !profile.Nodes.TryGetValue(args.Node, out var node) || !TryComp<NCVehicleAssemblyComponent>(ent, out var assembly))
            return;
        args.Handled = true;
        session.Pending = false;
        var request = new NCVehicleWorkshopRequest(args.View, args.Position, args.Node, args.Point, args.Revision, args.Sequence, args.Action);
        var valid = CanOperate(ent, args.User, request, out var failure);
        foreach (var (slot, worker) in assembly.ReservedSlots.ToArray())
            if (worker == args.User) assembly.ReservedSlots.Remove(slot);
        if (args.Cancelled || !_ui.IsUiOpen(ent.Owner, NCVehicleUiKey.Service, args.User) || !valid ||
            args.Used != _hands.GetActiveItem(args.User))
        {
            session.Feedback = args.Cancelled ? "nc-workshop-cancelled" : failure;
            RefreshObservers(ent);
            return;
        }
        session.Feedback = "nc-workshop-done";
        var part = assembly.Installed.GetValueOrDefault(node.Slot);
        if (args.Action == NCVehicleWorkshopAction.Inspect &&
            _prototypes.TryIndex(part.Prototype, out var inspectedPart) &&
            TryGetDiagnosticLevel(ent, args.Used, inspectedPart.RepairCategory, out var diagnosticLevel))
        {
            var exact = diagnosticLevel >= 2;
            session.Measurements[node.Slot] = new(exact ? part.Condition : MathF.Round(part.Condition * 4) / 4, exact, args.Revision);
        }
        else if (args.Action == NCVehicleWorkshopAction.Work)
        {
            var definition = chassis.Slots[node.Slot].WorkPoints[args.Point];
            var points = new Dictionary<string, bool>(part.WorkPoints!);
            var torque = part.PointTorque == null ? new Dictionary<string, float>() : new(part.PointTorque);
            var attached = !points[args.Point];
            if (definition.Kind == NCVehicleWorkshopPointKind.Fastener && attached &&
                (FindFasteners(args.User, chassis) is not { } fasteners || !_stacks.TryUse(fasteners, 1)))
                session.Feedback = "nc-workshop-fasteners-needed";
            else
            {
                points[args.Point] = attached;
                if (definition.Kind == NCVehicleWorkshopPointKind.Fastener)
                    torque[args.Point] = attached && args.Used is { } used && TryComp<NCVehicleTorqueToolComponent>(used, out var torqueTool)
                        ? torqueTool.Torque
                        : 0;
                _assembly.TrySetPart(ent, node.Slot, part with { WorkPoints = points, PointTorque = torque });
                if (definition.Kind == NCVehicleWorkshopPointKind.Fastener && !attached)
                    _stacks.SpawnAtPosition(1, chassis.Fasteners, Transform(args.User).Coordinates);
            }
        }
        else if (args.Action == NCVehicleWorkshopAction.Fill && args.Used is { } fill && node.Fluid is { } fillCircuit)
        {
            if (!_fluids.TryFillAtPort(ent, fill, fillCircuit))
                session.Feedback = "nc-workshop-fluid-transfer-failed";
        }
        else if (args.Action == NCVehicleWorkshopAction.Drain && args.Used is { } drain && node.Fluid is { } drainCircuit)
        {
            if (!_fluids.TryDrainAtPort(ent, drain, drainCircuit))
                session.Feedback = "nc-workshop-fluid-transfer-failed";
        }
        else if (args.Action == NCVehicleWorkshopAction.Remove)
        {
            var destination = chassis.Slots[node.Slot].RequiresHoist ? FindHoist(ent, chassis)!.Value : args.User;
            var dropped = Spawn(_prototypes.Index(part.Prototype).Item!.Value, Transform(destination).Coordinates);
            var item = EnsureComp<NCVehiclePartItemComponent>(dropped);
            item.Part = part.Prototype;
            item.Condition = part.Condition;
            item.StoredCharge = part.StoredCharge;
            Dirty(dropped, item);
            var bundle = EnsureComp<NCVehicleDetachedAssemblyComponent>(dropped);
            bundle.Chassis = ent.Comp.Chassis;
            bundle.RootSlot = node.Slot;
            var changes = RemovalChanges(ent, node.Slot);
            foreach (var slot in changes.Keys)
                if (assembly.Installed.TryGetValue(slot, out var child)) bundle.Parts[slot] = child;
            _partPhysics.TryRefresh(dropped);
            if (!_assembly.TrySetParts(ent, changes))
                QueueDel(dropped);
            else if (!chassis.Slots[node.Slot].RequiresHoist)
                _hands.TryPickupAnyHand(args.User, dropped);
        }
        else if (args.Action == NCVehicleWorkshopAction.Install && args.Used is { } source)
        {
            if (TryInstallChanges(ent, source, node.Slot, out var changes) && _assembly.TrySetParts(ent, changes))
                Del(source);
        }
        RefreshObservers(ent);
    }
}
