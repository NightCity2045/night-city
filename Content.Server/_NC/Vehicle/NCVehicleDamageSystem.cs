// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical.Trauma;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Assembly;
using Content.Shared.Damage.Systems;
using Content.Shared.Atmos.Components;
using Content.Shared._NC.Vehicle.Prototypes;
using Robust.Shared.Prototypes;
using Content.Server.Atmos.EntitySystems;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Content.Shared.Examine;
using Robust.Shared.Containers;
using Robust.Shared.Physics.Events;
using Robust.Shared.Timing;

namespace Content.Server._NC.Vehicle;

/// <summary>Resolves real contacts into local deformation and the existing authoritative medical impact API.</summary>
public sealed partial class NCVehicleDamageSystem : EntitySystem
{
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private NCVehicleAssemblySystem _assembly = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private DamageableSystem _damageable = default!;
    [Dependency] private FlammableSystem _flammable = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCVehicleFootprintSystem _footprint = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCVehicleBodyComponent, StartCollideEvent>(OnCollision);
        SubscribeLocalEvent<NCVehicleBodyComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<NCVehicleBodyComponent, DamageDealtEvent>(OnWeaponDamage);
        SubscribeLocalEvent<NCVehicleBodyComponent, NCVehicleMaintenanceEvent>(OnMaintenance);
        SubscribeLocalEvent<NCVehicleBodyComponent, NCVehicleRolloverRiskEvent>(OnRolloverRisk);
        InitializeRepair();
    }

    private void OnWeaponDamage(Entity<NCVehicleBodyComponent> ent, ref DamageDealtEvent args)
    {
        var damage = 0f;
        foreach (var value in args.Damage.DamageDict.Values)
            damage += Math.Max(0, value.Float());
        var local = Vector2.UnitY;
        if (args.Origin is { } origin && !TerminatingOrDeleted(origin) && Transform(origin).MapUid == Transform(ent).MapUid)
            local = (-_transform.GetWorldRotation(ent)).RotateVec(_transform.GetWorldPosition(origin) - _transform.GetWorldPosition(ent));
        if (local.LengthSquared() > 0 && _footprint.TryGetHalfExtents(ent, out var half))
        {
            var direction = Vector2.Normalize(local);
            // Project the incoming direction onto this model's hull, including long recovery vehicles.
            var distance = Math.Min(direction.X == 0 ? float.PositiveInfinity : half.X / Math.Abs(direction.X),
                direction.Y == 0 ? float.PositiveInfinity : half.Y / Math.Abs(direction.Y));
            local = direction * distance;
        }
        // The ordinary weapon pipeline supplies resistance-adjusted damage. No compatibility HP pool is maintained.
        TryImpact(ent, local, -_transform.GetWorldRotation(ent).RotateVec(local), damage * ent.Comp.WeaponEnergyPerDamage, 0);
        if (!args.Damage.DamageDict.TryGetValue(ent.Comp.PenetratingDamageType, out var piercing) || piercing <= FixedPoint2.Zero ||
            !TryComp<ContainerManagerComponent>(ent, out var containers))
            return;
        NCVehicleBodyZone? exposed = null;
        var nearest = float.PositiveInfinity;
        foreach (var zone in ent.Comp.Zones.Values)
            if (zone.Exterior && Vector2.DistanceSquared(zone.Position, local) < nearest)
            {
                exposed = zone;
                nearest = Vector2.DistanceSquared(zone.Position, local);
            }
        if (exposed == null || !float.IsFinite(exposed.CabinDamageTransfer) || exposed.CabinDamageTransfer <= 0)
            return;
        var penetrating = new DamageSpecifier();
        penetrating.DamageDict[ent.Comp.PenetratingDamageType] = piercing * Math.Clamp(exposed.CabinDamageTransfer, 0, 1);
        foreach (var seat in exposed.ExposedSeats)
            if (containers.Containers.TryGetValue("nc-vehicle-" + seat, out var occupants))
                foreach (var occupant in occupants.ContainedEntities)
                    _damageable.ChangeDamage(occupant, penetrating);
    }

    private void OnRolloverRisk(Entity<NCVehicleBodyComponent> ent, ref NCVehicleRolloverRiskEvent args)
    {
        if (float.IsFinite(args.LateralAcceleration) && args.Speed >= ent.Comp.RolloverMinimumSpeed &&
            Math.Abs(args.LateralAcceleration) > ent.Comp.RolloverAcceleration)
            TryOverturn(ent);
    }

    public bool TryOverturn(EntityUid uid)
    {
        if (!TryComp<NCVehicleDriveComponent>(uid, out var drive) || drive.Overturned)
            return false;
        drive.Overturned = true;
        drive.EngineRunning = false;
        drive.Cranking = false;
        drive.Gear = 0;
        _assembly.TryRebuild(uid);
        Dirty(uid, drive);
        return true;
    }

    public bool TryRight(EntityUid uid)
    {
        if (!TryComp<NCVehicleDriveComponent>(uid, out var drive) || !drive.Overturned)
            return false;
        drive.Overturned = false;
        _assembly.TryRebuild(uid);
        Dirty(uid, drive);
        return true;
    }

    private void OnMaintenance(Entity<NCVehicleBodyComponent> ent, ref NCVehicleMaintenanceEvent args)
    {
        if (!TryComp<NCVehicleDriveComponent>(ent, out var drive) ||
            !TryComp<NCVehicleConditionComponent>(ent, out var condition) || !TryComp<FlammableComponent>(ent, out var fire))
            return;
        if (condition.EngineTemperature >= ent.Comp.FireIgnitionTemperature && drive.Fuel > 0)
            _flammable.Ignite(ent, ent, fire);
        ent.Comp.Burning = fire.OnFire;
        if (fire.OnFire)
        {
            drive.EngineRunning = false;
            drive.Cranking = false;
            if (TryComp<NCVehicleAssemblyComponent>(ent, out var assembly))
                foreach (var slot in ent.Comp.FireDamageSlots)
                    if (assembly.Installed.TryGetValue(slot, out var part))
                        _assembly.TrySetPart(ent, slot, part with { Condition = Math.Max(0,
                            part.Condition - ent.Comp.FirePartWearPerSecond * args.Seconds) });
            if (TryComp<ContainerManagerComponent>(ent, out var seats))
                foreach (var container in seats.Containers.Values)
                    foreach (var occupant in container.ContainedEntities)
                        if (HasComp<NCVehicleOccupantComponent>(occupant))
                            _damageable.ChangeDamage(occupant, ent.Comp.CabinFireDamage * args.Seconds, origin: ent);
            Dirty(ent.Owner, drive);
        }
        Dirty(ent);
    }

    private void OnCollision(Entity<NCVehicleBodyComponent> ent, ref StartCollideEvent args)
    {
        if (!args.OurFixture.Hard || !args.OtherFixture.Hard || args.PointCount == 0)
            return;
        var ourPosition = _transform.GetWorldPosition(ent);
        var theirPosition = _transform.GetWorldPosition(args.OtherEntity);
        var point = args.WorldPoints[0];
        var normal = args.WorldNormal;
        // Robust reports the same A-to-B normal to both participants; orient it away from this chassis.
        if (Vector2.Dot(normal, theirPosition - ourPosition) < 0)
            normal = -normal;
        // Linear velocity belongs to the center of mass, which can move when parts are replaced.
        var ours = point - ourPosition - _transform.GetWorldRotation(ent).RotateVec(args.OurBody.LocalCenter);
        var theirs = point - theirPosition - _transform.GetWorldRotation(args.OtherEntity).RotateVec(args.OtherBody.LocalCenter);
        var relative = args.OurBody.LinearVelocity + new Vector2(-ours.Y, ours.X) * args.OurBody.AngularVelocity -
            args.OtherBody.LinearVelocity - new Vector2(-theirs.Y, theirs.X) * args.OtherBody.AngularVelocity;
        var closingSpeed = Vector2.Dot(relative, normal);
        var inverseMass = args.OurBody.InvMass + args.OtherBody.InvMass;
        if (closingSpeed <= ent.Comp.MinimumImpactSpeed || inverseMass <= 0)
            return;
        var energy = 0.5f * closingSpeed * closingSpeed / inverseMass;
        // Both vehicles receive a contact. Split their shared collision-energy budget rather than duplicating it.
        if (HasComp<NCVehicleBodyComponent>(args.OtherEntity))
            energy *= 0.5f;
        var local = (-_transform.GetWorldRotation(ent)).RotateVec(point - ourPosition);
        var deltaV = closingSpeed * args.OurBody.InvMass / inverseMass;
        if (!HasComp<NCVehicleBodyComponent>(args.OtherEntity))
            ApplyInjury(args.OtherEntity, ent.Comp, false, closingSpeed * ent.Comp.PedestrianTransfer,
                normal, NCActionResolutionId.New(), 1);
        TryImpact(ent, local, normal, energy, deltaV);
    }

    public bool TryImpact(EntityUid uid, Vector2 localPoint, Vector2 direction, float energy, float deltaV, string? zoneId = null)
    {
        if (!TryComp<NCVehicleBodyComponent>(uid, out var body) ||
            !float.IsFinite(energy) || energy <= 0 || !float.IsFinite(deltaV) || deltaV < 0 ||
            !float.IsFinite(localPoint.X) || !float.IsFinite(localPoint.Y) ||
            !float.IsFinite(direction.X) || !float.IsFinite(direction.Y))
            return false;
        string? selected = zoneId;
        var distance = float.PositiveInfinity;
        foreach (var (id, zone) in body.Zones)
        {
            if (zoneId != null || !zone.Exterior)
                continue;
            var candidate = Vector2.DistanceSquared(zone.Position, localPoint);
            if (candidate < distance)
            {
                selected = id;
                distance = candidate;
            }
        }
        if (selected == null || !body.Zones.ContainsKey(selected))
            return false;
        var definition = body.Zones[selected];
        if (!float.IsFinite(definition.EnergyCapacity) || definition.EnergyCapacity <= 0 ||
            !float.IsFinite(definition.ModuleEnergyCapacity) || definition.ModuleEnergyCapacity <= 0)
            return false;
        var deformation = body.Deformation.GetValueOrDefault(selected);
        var absorbed = Math.Min(energy, (1 - deformation) * definition.EnergyCapacity);
        body.Deformation[selected] = Math.Clamp(deformation + absorbed / definition.EnergyCapacity, 0, 1);
        var residual = energy - absorbed;
        if (residual > 0 && TryComp<NCVehicleAssemblyComponent>(uid, out var assembly))
        {
            // Every affected module consumes a share of the remaining budget; damage never removes its mass.
            var count = definition.ProtectedSlots.Count;
            foreach (var slot in definition.ProtectedSlots)
                if (assembly.Installed.TryGetValue(slot, out var part))
                    _assembly.TrySetPart(uid, slot, part with
                    { Condition = Math.Max(0, part.Condition - residual / definition.ModuleEnergyCapacity / Math.Max(1, count)) });
        }
        if (TryComp<ContainerManagerComponent>(uid, out var seats))
        {
            var airbag = !body.AirbagsDeployed && deltaV >= body.AirbagDeltaV &&
                TryComp<NCVehicleDriveComponent>(uid, out var drive) &&
                NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Airbags) > 0 &&
                TryComp<NCVehicleConditionComponent>(uid, out var condition) && condition.BatteryCharge > 0;
            if (airbag)
            {
                body.AirbagsDeployed = true;
                if (TryComp<NCVehicleComponent>(uid, out var vehicle) && TryComp<NCVehicleAssemblyComponent>(uid, out var installed))
                    foreach (var (slot, mount) in _prototypes.Index(vehicle.Chassis).Slots)
                        if (mount.Functions.Contains(NCVehicleFunction.Airbags) && installed.Installed.TryGetValue(slot, out var part))
                            _assembly.TrySetPart(uid, slot, part with { Condition = 0 });
            }
            var resolution = NCActionResolutionId.New();
            uint sequence = 0;
            foreach (var container in seats.Containers.Values)
                foreach (var occupant in container.ContainedEntities)
                    if (TryComp<NCVehicleOccupantComponent>(occupant, out var seat) && seat.Vehicle == uid)
                        ApplyInjury(occupant, body, seat.Belted, deltaV * (airbag && seat.Belted ? body.AirbagTransfer : 1),
                            direction, resolution, ++sequence);
        }
        Dirty(uid, body);
        return true;
    }

    private void OnExamined(Entity<NCVehicleBodyComponent> ent, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;
        foreach (var (id, deformation) in ent.Comp.Deformation)
            if (deformation > 0 && ent.Comp.Zones.TryGetValue(id, out var zone))
                args.PushMarkup(Loc.GetString("nc-vehicle-body-damaged-zone", ("zone", Loc.GetString(zone.Name))));
    }

    private void ApplyInjury(EntityUid occupant, NCVehicleBodyComponent body, bool belted, float deltaV,
        Vector2 direction, NCActionResolutionId resolution, uint sequence)
    {
        var value = Math.Max(0, deltaV * (belted ? body.BeltTransfer : body.UnbeltedTransfer) - body.InjuryThreshold) * body.TraumaPerDeltaV;
        if (!float.IsFinite(value) || value <= 0 || !float.IsFinite(body.MaximumTrauma) || body.MaximumTrauma <= 0)
            return;
        var trauma = FixedPoint2.New(Math.Min(value, body.MaximumTrauma));
        var request = new NCCombatTraumaRequest(resolution, 0, sequence, occupant, null, null, null,
            body.InjuryZone, body.InjuryRegion, body.TraumaProfile, new DamageSpecifier(), trauma,
            FixedPoint2.Zero, trauma, FixedPoint2.Zero, Vector2.Zero, direction, NCTraumaFlags.None, 0, _timing.CurTime);
        var ev = new NCCombatTraumaRequestedEvent(request);
        RaiseLocalEvent(occupant, ref ev);
    }
}
