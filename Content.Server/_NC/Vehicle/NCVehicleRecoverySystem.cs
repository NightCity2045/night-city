// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared.ActionBlocker;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Verbs;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics.Joints;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Vehicle;

/// <summary>A powered distance-joint winch feeds a physical, mass-bearing platform container.</summary>
public sealed partial class NCVehicleRecoverySystem : EntitySystem
{
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private NCVehicleTowSystem _tow = default!;
    [Dependency] private NCVehicleAssemblySystem _assembly = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCVehicleFootprintSystem _footprint = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<NCVehicleRecoveryComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<NCVehicleRecoveryComponent, GetVerbsEvent<AlternativeVerb>>(OnVerbs);
        SubscribeLocalEvent<NCVehicleRecoveryComponent, NCVehicleMaintenanceEvent>(OnMaintenance);
        SubscribeLocalEvent<NCVehicleRecoveryComponent, NCVehicleRecoveryDoAfterEvent>(OnComplete);
        SubscribeLocalEvent<NCVehicleRecoveryComponent, EntityTerminatingEvent>(OnTerminating);
        SubscribeLocalEvent<NCVehicleRecoveryComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<NCVehicleRecoveryComponent, EntRemovedFromContainerMessage>(OnRemoved);
    }

    private void OnMapInit(Entity<NCVehicleRecoveryComponent> ent, ref MapInitEvent args)
    {
        var container = _containers.EnsureContainer<ContainerSlot>(ent, ent.Comp.Container);
        container.ShowContents = true;
    }

    private void OnVerbs(Entity<NCVehicleRecoveryComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract)
            return;
        var user = args.User;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-vehicle-winch"), Act = () => TryToggleWinch(ent, user) });
        foreach (var action in new[] { NCVehicleRecoveryAction.Load, NCVehicleRecoveryAction.Secure, NCVehicleRecoveryAction.Release, NCVehicleRecoveryAction.Unload })
        {
            var captured = action;
            var key = action switch
            {
                NCVehicleRecoveryAction.Load => "nc-vehicle-flatbed-load",
                NCVehicleRecoveryAction.Secure => "nc-vehicle-flatbed-secure",
                NCVehicleRecoveryAction.Release => "nc-vehicle-flatbed-release",
                _ => "nc-vehicle-flatbed-unload",
            };
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString(key), Act = () => TryStart(ent, user, captured) });
        }
    }

    private bool CanOperate(EntityUid uid, EntityUid user)
        => TryComp<NCVehicleRecoveryComponent>(uid, out var deck) && !TerminatingOrDeleted(user) &&
            _blocker.CanConsciouslyPerformAction(user) && !HasComp<NCVehicleOccupantComponent>(user) &&
            _interaction.InRangeUnobstructed(user, uid, range: deck.OperationRange) &&
            TryComp<PhysicsComponent>(uid, out var body) && body.LinearVelocity.Length() <= deck.MaximumSpeed &&
            TryComp<NCVehicleDriveComponent>(uid, out var drive) && !drive.EngineRunning && !drive.Cranking;

    private bool IsEmpty(EntityUid car)
    {
        if (!TryComp<NCVehicleComponent>(car, out var vehicle) || !_prototypes.TryIndex(vehicle.Chassis, out var chassis))
            return false;
        foreach (var seat in chassis.Seats.Keys)
            if (_containers.TryGetContainer(car, "nc-vehicle-" + seat, out var occupants) && occupants.ContainedEntities.Count != 0)
                return false;
        return true;
    }

    public bool TryToggleWinch(EntityUid uid, EntityUid user)
    {
        if (!CanOperate(uid, user))
            return false;
        var deck = Comp<NCVehicleRecoveryComponent>(uid);
        if (!deck.Winching && (deck.Cargo != null || !TryGetWinch(uid, out _) ||
            !TryComp<NCVehicleConditionComponent>(uid, out var battery) || battery.BatteryCharge <= 0))
            return false;
        deck.Winching = !deck.Winching;
        deck.Operator = deck.Winching ? user : null;
        Dirty(uid, deck);
        return true;
    }

    private bool TryGetWinch(EntityUid uid, out DistanceJoint? joint)
    {
        joint = null;
        if (!TryComp<NCVehicleTowLinkComponent>(uid, out var link) || link.Holder != uid ||
            !TryComp<JointComponent>(uid, out var joints) || !joints.GetJoints.TryGetValue(link.Joint, out var constraint) ||
            constraint is not DistanceJoint distance)
            return false;
        joint = distance;
        return true;
    }

    private void OnMaintenance(Entity<NCVehicleRecoveryComponent> ent, ref NCVehicleMaintenanceEvent args)
    {
        if (!ent.Comp.Winching)
            return;
        if (ent.Comp.Operator is not { } user || !CanOperate(ent, user) || !TryGetWinch(ent, out var joint) ||
            !TryComp<NCVehicleConditionComponent>(ent, out var battery) || battery.BatteryCharge <= 0)
        {
            ent.Comp.Winching = false;
            ent.Comp.Operator = null;
            Dirty(ent);
            return;
        }
        // Change the engine-owned constraint length. The solver physically pulls both existing bodies.
        joint!.MaxLength = Math.Max(joint.MinLength, joint.MaxLength - ent.Comp.WinchRate * args.Seconds);
        battery.BatteryCharge = Math.Max(0, battery.BatteryCharge - ent.Comp.WinchAmps * args.Seconds / 3600);
        Dirty(ent.Owner, battery);
        if (joint.MaxLength <= joint.MinLength)
        {
            ent.Comp.Winching = false;
            ent.Comp.Operator = null;
            Dirty(ent);
        }
    }

    public bool CanLoad(EntityUid uid, EntityUid cargo, EntityUid user)
    {
        if (!CanOperate(uid, user) || uid == cargo || TerminatingOrDeleted(cargo) || !IsEmpty(cargo) ||
            !TryComp<NCVehicleRecoveryComponent>(uid, out var deck) || deck.Cargo != null ||
            _containers.IsEntityInContainer(cargo) || Transform(uid).MapID != Transform(cargo).MapID ||
            !TryComp<NCVehicleTowLinkComponent>(uid, out var link) || link.Holder != uid || link.Other != cargo ||
            !TryComp<PhysicsComponent>(cargo, out var body) || body.Mass > deck.MaximumLoad ||
            body.LinearVelocity.Length() > deck.MaximumSpeed || !TryComp<NCVehicleDriveComponent>(cargo, out var drive) ||
            drive.EngineRunning || drive.Cranking || !TryGetWinch(uid, out var joint) ||
            Vector2.Distance(_transform.ToMapCoordinates(new EntityCoordinates(uid, joint!.LocalAnchorA)).Position,
                _transform.ToMapCoordinates(new EntityCoordinates(cargo, joint.LocalAnchorB)).Position) > deck.LoadGap)
            return false;
        var forward = _transform.GetWorldRotation(uid).RotateVec(Vector2.UnitY);
        var cargoForward = _transform.GetWorldRotation(cargo).RotateVec(Vector2.UnitY);
        return _footprint.TryGetHalfExtents(cargo, out var half) &&
            half.X <= deck.CargoHalfExtents.X && half.Y <= deck.CargoHalfExtents.Y &&
            Vector2.Dot(forward, cargoForward) > deck.MinimumAlignmentDot &&
            _containers.TryGetContainer(uid, deck.Container, out var container) && container.ContainedEntities.Count == 0 &&
            _containers.CanInsert(cargo, container);
    }

    public bool TryStart(EntityUid uid, EntityUid user, NCVehicleRecoveryAction action)
    {
        if (!CanOperate(uid, user) || !TryComp<NCVehicleRecoveryComponent>(uid, out var deck))
            return false;
        EntityUid? cargo = deck.Cargo;
        if (action == NCVehicleRecoveryAction.Load && TryComp<NCVehicleTowLinkComponent>(uid, out var link))
            cargo = link.Other;
        if (cargo is not { } target || !CanAct(uid, target, user, action))
            return false;
        return _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, user,
            action is NCVehicleRecoveryAction.Load or NCVehicleRecoveryAction.Unload ? deck.LoadingSeconds : deck.SecuringSeconds,
            new NCVehicleRecoveryDoAfterEvent { Action = action, Cargo = GetNetEntity(target) }, uid, uid)
            { BreakOnMove = true, BreakOnDamage = true, NeedHand = true });
    }

    private bool CanAct(EntityUid uid, EntityUid cargo, EntityUid user, NCVehicleRecoveryAction action)
    {
        if (action == NCVehicleRecoveryAction.Load)
            return CanLoad(uid, cargo, user);
        if (!CanOperate(uid, user) || TerminatingOrDeleted(cargo) || !TryComp<NCVehicleRecoveryComponent>(uid, out var deck) || deck.Cargo != cargo)
            return false;
        return action switch
        {
            NCVehicleRecoveryAction.Secure => !deck.Secured,
            NCVehicleRecoveryAction.Release => deck.Secured,
            NCVehicleRecoveryAction.Unload => !deck.Secured && IsUnloadClear(uid, cargo, deck),
            _ => false,
        };
    }

    private bool IsUnloadClear(EntityUid uid, EntityUid cargo, NCVehicleRecoveryComponent deck)
    {
        var destination = _transform.ToMapCoordinates(new EntityCoordinates(uid, deck.UnloadPosition));
        var rotation = _transform.GetWorldRotation(uid);
        var x = rotation.RotateVec(new Vector2(deck.CargoHalfExtents.X, 0));
        var y = rotation.RotateVec(new Vector2(0, deck.CargoHalfExtents.Y));
        var extent = Vector2.Abs(x) + Vector2.Abs(y);
        foreach (var other in _lookup.GetEntitiesIntersecting(destination.MapId, new Box2(destination.Position - extent, destination.Position + extent)))
            if (other != uid && other != cargo && TryComp<PhysicsComponent>(other, out var body) && body.CanCollide &&
                TryComp<FixturesComponent>(other, out var fixtures))
                foreach (var fixture in fixtures.Fixtures.Values)
                    if (fixture.Hard && fixture.CollisionLayer != 0)
                        return false;
        return true;
    }

    private void OnComplete(Entity<NCVehicleRecoveryComponent> ent, ref NCVehicleRecoveryDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled)
            return;
        if (TryGetEntity(args.Cargo, out var cargo))
            args.Handled = TryComplete(ent, cargo.Value, args.User, args.Action);
    }

    public bool TryComplete(EntityUid uid, EntityUid cargo, EntityUid user, NCVehicleRecoveryAction action)
    {
        if (!CanAct(uid, cargo, user, action))
            return false;
        var deck = Comp<NCVehicleRecoveryComponent>(uid);
        var container = _containers.EnsureContainer<ContainerSlot>(uid, deck.Container);
        if (action == NCVehicleRecoveryAction.Load)
        {
            if (!_tow.TryDetach(uid, user) || !_containers.Insert(cargo, container))
                return false;
            deck.Cargo = cargo;
            deck.Secured = false;
            deck.Winching = false;
            deck.Operator = null;
            _transform.SetCoordinates(cargo, new EntityCoordinates(uid, deck.DeckPosition));
            _transform.SetLocalRotation(cargo, Angle.Zero);
        }
        else if (action == NCVehicleRecoveryAction.Unload)
            ReleaseCargo(uid, false);
        else
            deck.Secured = action == NCVehicleRecoveryAction.Secure;
        Dirty(uid, deck);
        _assembly.TryRebuild(uid);
        return true;
    }

    private void OnRemoved(Entity<NCVehicleRecoveryComponent> ent, ref EntRemovedFromContainerMessage args)
    {
        if (ent.Comp.Cargo != args.Entity)
            return;
        ent.Comp.Cargo = null;
        ent.Comp.Secured = false;
        Dirty(ent);
    }

    private void OnTerminating(Entity<NCVehicleRecoveryComponent> ent, ref EntityTerminatingEvent args) => ReleaseCargo(ent, true);
    private void OnShutdown(Entity<NCVehicleRecoveryComponent> ent, ref ComponentShutdown args) => ReleaseCargo(ent, true);

    private void ReleaseCargo(EntityUid uid, bool force)
    {
        var deck = Comp<NCVehicleRecoveryComponent>(uid);
        if (deck.Cargo is not { } cargo || TerminatingOrDeleted(cargo) ||
            Transform(uid).MapUid is not { } map || TerminatingOrDeleted(map) ||
            !_containers.TryGetContainer(uid, deck.Container, out var container))
            return;
        var destination = _transform.ToMapCoordinates(new EntityCoordinates(uid, deck.UnloadPosition));
        var rotation = _transform.GetWorldRotation(uid);
        if (!_containers.Remove(cargo, container, force: force))
            return;
        _transform.SetCoordinates(cargo, _transform.ToCoordinates(Transform(uid).ParentUid, destination));
        _transform.SetWorldRotation(cargo, rotation);
        _physics.SetLinearVelocity(cargo, Vector2.Zero);
        _physics.SetAngularVelocity(cargo, 0);
    }
}
