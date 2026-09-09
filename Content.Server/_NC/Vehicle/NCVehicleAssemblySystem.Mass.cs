// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Vehicle.Assembly;
using Content.Shared._NC.Vehicle.Components;
using Content.Server.Chemistry.EntitySystems;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Body;
using Content.Shared.Inventory;
using Robust.Shared.Containers;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Timing;

namespace Content.Server._NC.Vehicle;

public sealed partial class NCVehicleAssemblySystem
{
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private InventorySystem _inventory = default!;
    private readonly HashSet<EntityUid> _massDirty = new();
    private readonly List<EntityUid> _massReady = new();
    private readonly Stack<EntityUid> _payload = new();
    private readonly HashSet<EntityUid> _countedPayload = new();

    private void InitializeMass()
    {
        SubscribeLocalEvent<NCVehicleComponent, MapInitEvent>(OnMassMapInit,
            after: new[] { typeof(SolutionContainerSystem), typeof(NCVehicleOccupantSystem) });
        SubscribeLocalEvent<NCVehicleComponent, SolutionChangedEvent>(OnMassSolutionChanged);
        // Broadcast container notifications also cover equipment added to a seated passenger's inventory.
        SubscribeLocalEvent<EntInsertedIntoContainerMessage>(OnMassInserted);
        SubscribeLocalEvent<EntRemovedFromContainerMessage>(OnMassRemoved);
        SubscribeLocalEvent<PhysicsComponent, MassDataChangedEvent>(OnPayloadMassChanged);
    }

    private void OnMassMapInit(Entity<NCVehicleComponent> ent, ref MapInitEvent args)
    {
        // Reservoir children are spawned by chemistry during MapInit. Publish the initial loaded mass
        // after chemistry and seats, rather than allowing the first repair/impact to add it belatedly.
        if (TryComp<NCVehicleAssemblyComponent>(ent, out var assembly) && assembly.ProfileValid)
            RefreshDrive(ent, assembly);
    }

    private void OnMassSolutionChanged(Entity<NCVehicleComponent> ent, ref SolutionChangedEvent args) => _massDirty.Add(ent);
    private void OnMassInserted(EntInsertedIntoContainerMessage args) => QueueParentMass(args.Container.Owner);
    private void OnMassRemoved(EntRemovedFromContainerMessage args) => QueueParentMass(args.Container.Owner);
    private void OnPayloadMassChanged(Entity<PhysicsComponent> ent, ref MassDataChangedEvent args)
    {
        if (args.MassChanged)
            QueueParentMass(Transform(ent).ParentUid);
    }

    private void QueueParentMass(EntityUid uid)
    {
        // Walk only the changed entity's ancestry. No world-wide or proximity scan is necessary.
        while (TryComp(uid, out TransformComponent? transform))
        {
            if (HasComp<NCVehicleComponent>(uid))
            {
                _massDirty.Add(uid);
                return;
            }
            uid = transform.ParentUid;
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        _massReady.Clear();
        foreach (var uid in _massDirty)
        {
            if (TerminatingOrDeleted(uid) || !TryComp<NCVehicleAssemblyComponent>(uid, out var assembly))
            {
                _massReady.Add(uid);
                continue;
            }
            if (MetaData(uid).EntityPaused || _timing.CurTime < assembly.NextMassRefresh)
                continue;
            _massReady.Add(uid);
        }
        foreach (var uid in _massReady)
        {
            _massDirty.Remove(uid);
            if (TerminatingOrDeleted(uid) || !TryComp<NCVehicleAssemblyComponent>(uid, out var assembly) ||
                !assembly.ProfileValid || !TryComp<NCVehicleComponent>(uid, out var vehicle) ||
                !_prototypes.TryIndex(vehicle.Chassis, out var chassis))
                continue;
            RefreshDrive(uid, assembly);
            assembly.NextMassRefresh = _timing.CurTime + TimeSpan.FromSeconds(chassis.MassRefreshInterval);
        }
    }

    private NCVehicleAssemblyProfile CompileLoadedProfile(EntityUid uid, NCVehicleAssemblyProfile dry)
    {
        if (!TryComp<NCVehicleComponent>(uid, out var vehicle) || !_prototypes.TryIndex(vehicle.Chassis, out var chassis))
            return dry;
        double mass = dry.Mass;
        double weightedX = dry.Mass * dry.CenterOfMass.X;
        double weightedY = dry.Mass * dry.CenterOfMass.Y;
        // Accumulate inertia about the chassis origin, then move it to the final center of mass once.
        double originInertia = dry.Inertia + dry.Mass * dry.CenterOfMass.LengthSquared();
        foreach (var (solutionId, definition) in chassis.FluidMass)
            if (_solutions.TryGetSolution(uid, solutionId, out _, out var solution))
                AddMass(solution.Volume.Float() * definition.KilogramsPerUnit, definition.Position, 0,
                    ref mass, ref weightedX, ref weightedY, ref originInertia);
        _countedPayload.Clear();
        if (TryComp<ContainerManagerComponent>(uid, out var containers))
        {
            foreach (var (id, definition) in chassis.Seats)
                if (containers.Containers.TryGetValue("nc-vehicle-" + id, out var seat))
                    AddContainerMass(seat, definition.Position, ref mass, ref weightedX, ref weightedY, ref originInertia);
            foreach (var (id, position) in chassis.CargoMass)
                if (containers.Containers.TryGetValue(id, out var cargo))
                    AddContainerMass(cargo, position, ref mass, ref weightedX, ref weightedY, ref originInertia);
        }
        var center = new Vector2((float) (weightedX / mass), (float) (weightedY / mass));
        var inertia = originInertia - mass * center.LengthSquared();
        if (!double.IsFinite(mass) || mass <= 0 || mass > float.MaxValue || !double.IsFinite(inertia) ||
            inertia <= 0 || inertia > float.MaxValue || !float.IsFinite(center.X) || !float.IsFinite(center.Y))
            return dry;
        return new NCVehicleAssemblyProfile((float) mass, center, (float) inertia, dry.RequiredSlotsFilled);
    }

    private void AddContainerMass(BaseContainer container, Vector2 position,
        ref double mass, ref double x, ref double y, ref double inertia)
    {
        _payload.Clear();
        foreach (var uid in container.ContainedEntities)
            _payload.Push(uid);
        while (_payload.TryPop(out var uid))
        {
            if (!_countedPayload.Add(uid) || TerminatingOrDeleted(uid))
                continue;
            if (TryComp<PhysicsComponent>(uid, out var body))
                AddMass(body.Mass, position, body.InvI > 0 ? 1 / body.InvI : 0, ref mass, ref x, ref y, ref inertia);
            // A loaded car's body already includes its fuel, modules and trunk contents.
            if (HasComp<NCVehicleComponent>(uid))
                continue;
            if (HasComp<BodyComponent>(uid))
            {
                // A character's physics mass already represents its body. Count worn/held gear,
                // but do not add internal organs and limbs a second time.
                foreach (var item in _inventory.GetHandOrInventoryEntities(uid))
                    _payload.Push(item);
                continue;
            }
            // Contained inventory shares its seat/trunk position; its items are counted exactly once.
            if (TryComp<ContainerManagerComponent>(uid, out var children))
                foreach (var child in children.Containers.Values)
                    foreach (var item in child.ContainedEntities)
                        _payload.Push(item);
        }
    }

    private static void AddMass(float amount, Vector2 position, float ownInertia,
        ref double mass, ref double x, ref double y, ref double inertia)
    {
        if (!float.IsFinite(amount) || amount <= 0 || !float.IsFinite(position.X) || !float.IsFinite(position.Y))
            return;
        mass += amount;
        x += amount * position.X;
        y += amount * position.Y;
        inertia += ownInertia + amount * position.LengthSquared();
    }
}
