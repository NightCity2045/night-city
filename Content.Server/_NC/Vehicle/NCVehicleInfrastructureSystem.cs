// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared.ActionBlocker;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Lock;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.Server._NC.Vehicle;

/// <summary>Current-round parking and whole-entity map transfers. No NPC graph or persistence dependency.</summary>
public sealed partial class NCVehicleInfrastructureSystem : EntitySystem
{
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private NCVehicleAccessSystem _access = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private LockSystem _locks = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private NCVehicleFootprintSystem _footprint = default!;
    private readonly Dictionary<string, HashSet<EntityUid>> _gates = new();

    public override void Initialize()
    {
        SubscribeLocalEvent<NCVehicleGateComponent, MapInitEvent>(OnGateInit);
        SubscribeLocalEvent<NCVehicleGateComponent, ComponentShutdown>(OnGateShutdown);
        SubscribeLocalEvent<NCVehicleDriveComponent, NCVehicleInfrastructureMessage>(OnRequest);
        SubscribeLocalEvent<NCVehicleParkingComponent, GetVerbsEvent<AlternativeVerb>>(OnParkingVerbs);
        SubscribeLocalEvent<NCVehicleParkingComponent, ExaminedEvent>(OnParkingExamined);
    }

    private void OnGateInit(Entity<NCVehicleGateComponent> ent, ref MapInitEvent args)
    {
        if (string.IsNullOrWhiteSpace(ent.Comp.GateId))
            return;
        if (!_gates.TryGetValue(ent.Comp.GateId, out var gates))
            _gates[ent.Comp.GateId] = gates = new HashSet<EntityUid>();
        gates.Add(ent);
    }

    private void OnGateShutdown(Entity<NCVehicleGateComponent> ent, ref ComponentShutdown args)
    {
        if (_gates.TryGetValue(ent.Comp.GateId, out var gates))
        {
            gates.Remove(ent);
            if (gates.Count == 0)
                _gates.Remove(ent.Comp.GateId);
        }
    }

    private void OnRequest(Entity<NCVehicleDriveComponent> ent, ref NCVehicleInfrastructureMessage args)
    {
        if (ent.Comp.Authority != args.Authority || ent.Comp.Driver != args.Actor ||
            !TryComp<NCVehicleComponent>(ent, out var vehicle))
            return;
        // A bounded spatial lookup selects nearby authored infrastructure, not a world-wide entity scan.
        if (args.Travel)
        {
            foreach (var gate in _lookup.GetEntitiesInRange<NCVehicleGateComponent>(Transform(ent).Coordinates, vehicle.InfrastructureSearchRange))
                if (TryTravel(ent, gate, args.Actor))
                    return;
        }
        else
        {
            foreach (var bay in _lookup.GetEntitiesInRange<NCVehicleParkingComponent>(Transform(ent).Coordinates, vehicle.InfrastructureSearchRange))
                if (TryPark(ent, bay, args.Actor))
                    return;
        }
        _popup.PopupEntity(Loc.GetString("nc-vehicle-infrastructure-unavailable"), ent, args.Actor);
    }

    private bool CanMove(EntityUid car, EntityUid user, float maximumSpeed)
        => !TerminatingOrDeleted(car) && _blocker.CanConsciouslyPerformAction(user) &&
            TryComp<NCVehicleDriveComponent>(car, out var drive) && drive.Driver == user &&
            !drive.EngineRunning && !drive.Cranking && !drive.Overturned && !_containers.IsEntityInContainer(car) &&
            TryComp<PhysicsComponent>(car, out var body) && body.LinearVelocity.Length() <= maximumSpeed &&
            (!TryComp<NCVehicleRecoveryComponent>(car, out var deck) || deck.Cargo == null || deck.Secured);

    public bool TryTravel(EntityUid car, EntityUid gate, EntityUid user)
    {
        if (!TryComp<NCVehicleGateComponent>(gate, out var entrance) || !CanMove(car, user, entrance.MaximumSpeed) ||
            Transform(car).MapID != Transform(gate).MapID ||
            Vector2.Distance(_transform.GetWorldPosition(car), _transform.GetWorldPosition(gate)) > entrance.Radius ||
            (TryComp<JointComponent>(car, out var joints) && joints.JointCount != 0) ||
            !_gates.TryGetValue(entrance.Destination, out var destinations) || destinations.Count != 1)
            return false;
        EntityUid? destination = null;
        foreach (var candidate in destinations)
            destination = candidate;
        if (destination is not { } exit || exit == gate || TerminatingOrDeleted(exit) ||
            !TryComp<NCVehicleGateComponent>(exit, out var exitGate) || Transform(exit).MapUid is not { } map ||
            TerminatingOrDeleted(map))
            return false;
        var arrival = _transform.ToMapCoordinates(new EntityCoordinates(exit, exitGate.ArrivalOffset));
        // Conservative rotated footprint includes the whole chassis; blocked exits never displace pedestrians.
        if (!_footprint.TryGetHalfExtents(car, out var half) ||
            half.X > entrance.MaximumHalfExtents.X || half.Y > entrance.MaximumHalfExtents.Y ||
            half.X > exitGate.MaximumHalfExtents.X || half.Y > exitGate.MaximumHalfExtents.Y)
            return false;
        var rotation = _transform.GetWorldRotation(exit);
        var extent = Vector2.Abs(rotation.RotateVec(new Vector2(half.X, 0))) +
            Vector2.Abs(rotation.RotateVec(new Vector2(0, half.Y)));
        foreach (var other in _lookup.GetEntitiesIntersecting(arrival.MapId, new Box2(arrival.Position - extent, arrival.Position + extent)))
        {
            if (other == car || other == gate || other == exit || !TryComp<PhysicsComponent>(other, out var obstacle) || !obstacle.CanCollide ||
                !TryComp<FixturesComponent>(other, out var fixtures))
                continue;
            foreach (var fixture in fixtures.Fixtures.Values)
                if (fixture.Hard && fixture.CollisionLayer != 0)
                    return false;
        }
        var drive = Comp<NCVehicleDriveComponent>(car);
        drive.Authority++;
        drive.LastInput = 0;
        drive.GasHeld = false;
        drive.BrakeHeld = false;
        drive.PendingShift = null;
        drive.PreviousVelocity = Vector2.Zero;
        _physics.SetLinearVelocity(car, Vector2.Zero);
        _physics.SetAngularVelocity(car, 0);
        // Moving the root transform carries seats, people, cargo and chemical reservoirs together.
        _transform.SetCoordinates(car, _transform.ToCoordinates(arrival));
        _transform.SetWorldRotation(car, _transform.GetWorldRotation(exit));
        Dirty(car, drive);
        return true;
    }

    public bool TryPark(EntityUid car, EntityUid bay, EntityUid user)
    {
        if (!TryComp<NCVehicleParkingComponent>(bay, out var parking) || !CanMove(car, user, parking.MaximumSpeed) ||
            Transform(car).MapID != Transform(bay).MapID ||
            Vector2.Distance(_transform.GetWorldPosition(car), _transform.GetWorldPosition(bay)) > parking.Radius ||
            (parking.Owner != null && parking.Owner != user) || !_access.HasKey(car, user) ||
            !TryComp<NCVehicleAccessComponent>(car, out var access) || access.Owner != user)
            return false;
        parking.Owner = user;
        parking.Vehicle = car;
        _locks.Lock(car, user);
        Dirty(bay, parking);
        return true;
    }

    private void OnParkingVerbs(Entity<NCVehicleParkingComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || ent.Comp.Owner != args.User)
            return;
        var user = args.User;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-vehicle-parking-release"), Act = () => TryReleaseParking(ent, user) });
    }

    public bool TryReleaseParking(EntityUid bay, EntityUid user)
    {
        if (!TryComp<NCVehicleParkingComponent>(bay, out var parking) || parking.Owner != user ||
            !_blocker.CanConsciouslyPerformAction(user) || !_interaction.InRangeUnobstructed(user, bay))
            return false;
        parking.Owner = null;
        parking.Vehicle = null;
        Dirty(bay, parking);
        return true;
    }

    private void OnParkingExamined(Entity<NCVehicleParkingComponent> ent, ref ExaminedEvent args)
        => args.PushMarkup(Loc.GetString(ent.Comp.Owner == null ? "nc-vehicle-parking-free" : "nc-vehicle-parking-reserved"));
}
