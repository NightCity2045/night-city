// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared._NC.Vehicle.Prototypes;
using Content.Shared.ActionBlocker;
using Content.Shared.Interaction;
using Content.Shared.Verbs;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Vehicle;

/// <summary>Round-local driver seat; the actor's entity remains alive inside its normal engine container.</summary>
public sealed partial class NCVehicleOccupantSystem : EntitySystem
{
    private const string DriverContainer = "nc-vehicle-driver";
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCVehicleAssemblySystem _assemblies = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private NCVehicleAccessSystem _access = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCVehicleDriveComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<NCVehicleDriveComponent, GetVerbsEvent<AlternativeVerb>>(OnVerbs);
        SubscribeLocalEvent<NCVehicleDriveComponent, NCVehicleExitMessage>(OnExit);
        SubscribeLocalEvent<NCVehicleDriveComponent, NCVehicleBeltMessage>(OnBelt);
        SubscribeLocalEvent<NCVehicleDriveComponent, EntRemovedFromContainerMessage>(OnRemoved);
        SubscribeLocalEvent<NCVehicleDriveComponent, EntityTerminatingEvent>(OnTerminating);
        SubscribeLocalEvent<NCVehicleDriveComponent, NCVehicleDriveShutdownEvent>(OnShutdown);
        SubscribeLocalEvent<NCVehicleDriveComponent, BoundUIClosedEvent>(OnUiClosed);
    }

    private void OnMapInit(Entity<NCVehicleDriveComponent> ent, ref MapInitEvent args)
    {
        if (!_prototypes.TryIndex(ent.Comp.Handling, out var tuning) || !NCVehicleDriveSystem.Valid(tuning))
            return;
        _containers.EnsureContainer<ContainerSlot>(ent, DriverContainer);
        if (TryGetChassis(ent, out var chassis))
            foreach (var id in chassis!.Seats.Keys)
                _containers.EnsureContainer<ContainerSlot>(ent, "nc-vehicle-" + id);
        ent.Comp.WheelSpeeds = new float[tuning.Wheels.Count];
        _assemblies.TryRebuild(ent);
        Dirty(ent);
    }

    private void OnVerbs(Entity<NCVehicleDriveComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract)
            return;
        var user = args.User;
        if (TryComp<NCVehicleOccupantComponent>(user, out var occupant) && occupant.Vehicle == ent.Owner)
        {
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-vehicle-exit"), Act = () => TryExit(ent, user) });
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-vehicle-belt"), Act = () => TryToggleBelt(ent, user) });
            if (ent.Comp.Driver == user)
                args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-vehicle-dashboard"),
                    Act = () => _ui.TryOpenUi(ent.Owner, NCVehicleUiKey.Driver, user) });
            return;
        }
        if (!TryGetChassis(ent, out var chassis))
            return;
        foreach (var (id, definition) in chassis!.Seats)
        {
            var seatId = id;
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString(definition.Name),
                Act = () => TryEnter(ent, user, seatId) });
        }
    }

    public bool CanEnter(EntityUid uid, EntityUid user, string seatId = "driver")
    {
        return !_containers.IsEntityInContainer(uid) && _access.CanOpen(uid) && HasComp<ActorComponent>(user) && _blocker.CanConsciouslyPerformAction(user) &&
            !HasComp<NCVehicleOccupantComponent>(user) && TryGetChassis(uid, out var chassis) &&
            chassis!.Seats.TryGetValue(seatId, out var definition) &&
            TryComp<NCVehicleDriveComponent>(uid, out var drive) && (!definition.Driver || drive.Driver == null) &&
            _containers.TryGetContainer(uid, "nc-vehicle-" + seatId, out var container) && container.ContainedEntities.Count == 0 &&
            _interaction.InRangeUnobstructed(user, uid);
    }

    public bool TryEnter(EntityUid uid, EntityUid user, string seatId = "driver")
    {
        if (!CanEnter(uid, user, seatId) || !TryComp<NCVehicleDriveComponent>(uid, out var drive) ||
            !TryGetChassis(uid, out var chassis) || !chassis!.Seats.TryGetValue(seatId, out var definition) ||
            !_containers.TryGetContainer(uid, "nc-vehicle-" + seatId, out var seat) || !_containers.Insert(user, seat))
            return false;
        var occupant = EnsureComp<NCVehicleOccupantComponent>(user);
        occupant.Vehicle = uid;
        occupant.Seat = seatId;
        Dirty(user, occupant);
        _transform.SetCoordinates(user, new EntityCoordinates(uid, definition.Position));
        if (!definition.Driver)
            return true;
        drive.Driver = user;
        drive.Authority++;
        drive.LastInput = 0;
        drive.SteeringTarget = 0;
        Dirty(uid, drive);
        if (!_ui.TryOpenUi(uid, NCVehicleUiKey.Driver, user))
        {
            TryExit(uid, user, true);
            return false;
        }
        return true;
    }

    public bool TryExit(EntityUid uid, EntityUid user, bool force = false)
    {
        if (!TryComp<NCVehicleDriveComponent>(uid, out var drive))
            return false;
        // The fallback also evacuates a legacy seat while a component is being removed.
        var hasLink = TryComp<NCVehicleOccupantComponent>(user, out var occupant) && occupant.Vehicle == uid;
        if ((!hasLink && drive.Driver != user) ||
            !_containers.TryGetContainer(uid, hasLink ? "nc-vehicle-" + occupant!.Seat : DriverContainer, out var seat))
            return false;
        // A removed/hot-reloaded prototype must never prevent emergency evacuation on deletion.
        var configured = _prototypes.TryIndex(drive.Handling, out var tuning);
        if (!force && !configured)
            return false;
        if (!force && TryComp<PhysicsComponent>(uid, out var body) &&
            body.LinearVelocity.Length() > tuning!.ExitSpeed)
            return false;
        var xform = Transform(uid);
        var exitOffset = tuning?.ExitOffset ?? Vector2.Zero;
        if (hasLink && TryGetChassis(uid, out var chassis) && chassis!.Seats.TryGetValue(occupant!.Seat, out var definition))
            exitOffset = definition.Exit;
        var exit = _transform.ToMapCoordinates(new EntityCoordinates(uid, exitOffset));
        if (!force && !CanUseExit(uid, user, exit, tuning!.ExitClearance))
        {
            // If a wall blocks the assigned door, try the matching door on the other side.
            var mirrored = new Vector2(-exitOffset.X, exitOffset.Y);
            exit = _transform.ToMapCoordinates(new EntityCoordinates(uid, mirrored));
            if (!CanUseExit(uid, user, exit, tuning.ExitClearance))
                return false;
        }
        var destination = _transform.ToCoordinates(xform.ParentUid, exit);
        if (!seat.Contains(user))
        {
            if (!force)
                return false;
            // A stale authority link must not force-remove an unrelated entity from an empty seat.
            if (hasLink)
                RemCompDeferred<NCVehicleOccupantComponent>(user);
            ClearDriver((uid, drive), user);
            return true;
        }
        // Let container removal resolve its normal map/grid parent first. Character movement hooks may
        // choose a different grid, so passing exact destination coordinates into Remove is not safe.
        if (!_containers.Remove(user, seat, force: force))
            return false;
        if (!TerminatingOrDeleted(user))
            _transform.SetCoordinates(user, destination);
        return true;
    }

    private bool CanUseExit(EntityUid vehicle, EntityUid user, MapCoordinates exit, float clearance)
    {
        // Query only the two door-sized areas, never all entities on the map.
        var radius = new Vector2(clearance);
        var bounds = new Box2(exit.Position - radius, exit.Position + radius);
        foreach (var candidate in _lookup.GetEntitiesIntersecting(exit.MapId, bounds))
        {
            if (candidate == vehicle || candidate == user || !TryComp<PhysicsComponent>(candidate, out var obstacle) ||
                !obstacle.CanCollide || !TryComp<FixturesComponent>(candidate, out var fixtures))
                continue;
            foreach (var fixture in fixtures.Fixtures.Values)
                if (fixture.Hard && fixture.CollisionLayer != 0)
                    return false;
        }

        return true;
    }

    private void OnExit(Entity<NCVehicleDriveComponent> ent, ref NCVehicleExitMessage args)
    {
        if (args.Authority == ent.Comp.Authority)
            TryExit(ent, args.Actor);
    }

    private void OnBelt(Entity<NCVehicleDriveComponent> ent, ref NCVehicleBeltMessage args)
    {
        if (args.Authority == ent.Comp.Authority && ent.Comp.Driver == args.Actor)
            TryToggleBelt(ent, args.Actor);
    }

    private void OnRemoved(Entity<NCVehicleDriveComponent> ent, ref EntRemovedFromContainerMessage args)
    {
        if (TryComp<NCVehicleOccupantComponent>(args.Entity, out var occupant) && occupant.Vehicle == ent.Owner)
            RemCompDeferred<NCVehicleOccupantComponent>(args.Entity);
        ClearDriver(ent, args.Entity);
    }

    private void ClearDriver(Entity<NCVehicleDriveComponent> ent, EntityUid user)
    {
        // Driver seats may have any chassis-defined ID; authority follows the occupant.
        if (ent.Comp.Driver != user)
            return;
        ent.Comp.Driver = null;
        ent.Comp.Cranking = false;
        ent.Comp.Authority++;
        ent.Comp.PendingShift = null;
        ent.Comp.ShiftStatus = NCVehicleShiftStatus.None;
        ent.Comp.GasHeld = false;
        ent.Comp.BrakeHeld = false;
        ent.Comp.ClutchHeld = false;
        _ui.CloseUi(ent.Owner, NCVehicleUiKey.Driver, user);
        Dirty(ent);
    }

    private void OnUiClosed(Entity<NCVehicleDriveComponent> ent, ref BoundUIClosedEvent args)
    {
        if (ent.Comp.Driver != args.Actor)
            return;
        ent.Comp.GasHeld = false;
        ent.Comp.BrakeHeld = false;
        ent.Comp.ClutchHeld = false;
        Dirty(ent);
        ent.Comp.SteeringHeld = false;
    }

    private void OnTerminating(Entity<NCVehicleDriveComponent> ent, ref EntityTerminatingEvent args)
        => Evacuate(ent);

    private void OnShutdown(Entity<NCVehicleDriveComponent> ent, ref NCVehicleDriveShutdownEvent args)
        => Evacuate(ent);

    private void Evacuate(Entity<NCVehicleDriveComponent> ent)
    {
        // There is no surviving destination during world teardown. Normal recursive deletion owns that case.
        if (Transform(ent).MapUid is not { } map || TerminatingOrDeleted(map))
            return;
        // Eject before container shutdown so deleting the vehicle cannot delete its driver.
        if (ent.Comp.Driver is { } driver && Exists(driver))
            TryExit(ent, driver, true);
        // Use actual containers, not a prototype that may have been removed by hot reload.
        if (TryComp<ContainerManagerComponent>(ent, out var manager))
            foreach (var (id, seat) in manager.Containers)
                if (id.StartsWith("nc-vehicle-", StringComparison.Ordinal))
                    foreach (var passenger in seat.ContainedEntities.ToArray())
                        TryExit(ent, passenger, true);
    }

    public bool TryToggleBelt(EntityUid uid, EntityUid user)
    {
        if (!_blocker.CanConsciouslyPerformAction(user) ||
            !TryComp<NCVehicleOccupantComponent>(user, out var occupant) || occupant.Vehicle != uid ||
            !TryGetChassis(uid, out var chassis) || !chassis!.Seats.TryGetValue(occupant.Seat, out var seat) || !seat.HasBelt)
            return false;
        occupant.Belted = !occupant.Belted;
        Dirty(user, occupant);
        return true;
    }

    private bool TryGetChassis(EntityUid uid, out NCVehicleChassisPrototype? chassis)
    {
        chassis = null;
        return TryComp<NCVehicleComponent>(uid, out var vehicle) && _prototypes.TryIndex(vehicle.Chassis, out chassis);
    }
}
