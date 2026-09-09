// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared.ActionBlocker;
using Content.Shared.DoAfter;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Robust.Shared.Physics.Components;

namespace Content.Server._NC.Vehicle;

public sealed partial class NCVehicleDamageSystem
{
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;

    private void InitializeRepair()
    {
        SubscribeLocalEvent<NCVehicleBodyComponent, InteractUsingEvent>(OnBodywork);
        SubscribeLocalEvent<NCVehicleBodyComponent, NCVehicleBodyRepairDoAfterEvent>(OnBodyworkComplete);
    }

    private void OnBodywork(Entity<NCVehicleBodyComponent> ent, ref InteractUsingEvent args)
    {
        if (args.Handled || !TryComp<NCVehicleRepairKitComponent>(args.Used, out var kit) || kit.Category != "Bodywork")
            return;
        var local = (-_transform.GetWorldRotation(ent)).RotateVec(_transform.GetWorldPosition(args.User) - _transform.GetWorldPosition(ent));
        string? selected = null;
        var distance = float.PositiveInfinity;
        foreach (var (id, zone) in ent.Comp.Zones)
            if (zone.Exterior && ent.Comp.Deformation.GetValueOrDefault(id) > 1 - kit.MaximumCondition &&
                Vector2.DistanceSquared(zone.Position, local) < distance)
            {
                selected = id;
                distance = Vector2.DistanceSquared(zone.Position, local);
            }
        if (selected != null)
            args.Handled = TryStartBodywork(ent, args.User, args.Used, selected);
    }

    public bool CanRepairBody(EntityUid uid, EntityUid user, EntityUid used, string zone)
    {
        if (!TryComp<NCVehicleBodyComponent>(uid, out var body) || body.Burning ||
            !body.Zones.TryGetValue(zone, out var area) || !area.Exterior ||
            !TryComp<NCVehicleRepairKitComponent>(used, out var kit) || kit.Category != "Bodywork" || kit.Uses <= 0 ||
            !float.IsFinite(kit.Seconds) || kit.Seconds < 0 || !float.IsFinite(kit.RestoreAmount) || kit.RestoreAmount <= 0 ||
            !float.IsFinite(kit.MaximumCondition) || kit.MaximumCondition is <= 0 or > 1 ||
            body.Deformation.GetValueOrDefault(zone) <= 1 - kit.MaximumCondition ||
            _hands.GetActiveItem(user) != used || !_blocker.CanConsciouslyPerformAction(user) ||
            HasComp<NCVehicleOccupantComponent>(user) || !_interaction.InRangeUnobstructed(user, uid, range: kit.WorkbenchRange) ||
            !TryComp<NCVehicleDriveComponent>(uid, out var drive) || drive.EngineRunning || drive.Cranking ||
            !TryComp<NCVehicleComponent>(uid, out var vehicle) || !_prototypes.TryIndex(vehicle.Chassis, out var chassis) ||
            !TryComp<PhysicsComponent>(uid, out var physics) || physics.LinearVelocity.Length() > chassis.ServiceMaxSpeed)
            return false;
        foreach (var bench in _lookup.GetEntitiesInRange<NCVehicleWorkshopEquipmentComponent>(Transform(uid).Coordinates, kit.WorkbenchRange))
            if (bench.Comp.Workbench && Transform(bench).Anchored && _interaction.InRangeUnobstructed(user, bench.Owner, range: kit.WorkbenchRange))
                return true;
        return false;
    }

    public bool TryStartBodywork(EntityUid uid, EntityUid user, EntityUid used, string zone)
    {
        if (!CanRepairBody(uid, user, used, zone))
            return false;
        return _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, user, Comp<NCVehicleRepairKitComponent>(used).Seconds,
            new NCVehicleBodyRepairDoAfterEvent { Zone = zone }, uid, uid, used)
            { BreakOnMove = true, BreakOnDamage = true, NeedHand = true });
    }

    private void OnBodyworkComplete(Entity<NCVehicleBodyComponent> ent, ref NCVehicleBodyRepairDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || args.Used is not { } used)
            return;
        args.Handled = TryRepairBody(ent, args.User, used, args.Zone);
    }

    public bool TryRepairBody(EntityUid uid, EntityUid user, EntityUid used, string zone)
    {
        if (!CanRepairBody(uid, user, used, zone))
            return false;
        var body = Comp<NCVehicleBodyComponent>(uid);
        var kit = Comp<NCVehicleRepairKitComponent>(used);
        // Sheet metal repair restores only this crumple zone, never the protected engine or wheel modules.
        body.Deformation[zone] = Math.Max(1 - kit.MaximumCondition, body.Deformation[zone] - kit.RestoreAmount);
        kit.Uses--;
        Dirty(uid, body);
        Dirty(used, kit);
        if (kit.Uses == 0)
            Del(used);
        return true;
    }
}
