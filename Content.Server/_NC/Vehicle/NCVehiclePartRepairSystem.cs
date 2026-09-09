// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared.DoAfter;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Vehicle;

/// <summary>Consumable repair of a removed module at a physical workbench.</summary>
public sealed partial class NCVehiclePartRepairSystem : EntitySystem
{
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCVehiclePartItemComponent, InteractUsingEvent>(OnInteract);
        SubscribeLocalEvent<NCVehiclePartItemComponent, NCVehicleRepairDoAfterEvent>(OnComplete);
    }

    private void OnInteract(Entity<NCVehiclePartItemComponent> ent, ref InteractUsingEvent args)
    {
        if (!args.Handled)
            args.Handled = TryRepair(ent, args.User, args.Used);
    }

    public bool CanRepair(EntityUid uid, EntityUid user, EntityUid used)
    {
        if (!TryComp<NCVehiclePartItemComponent>(uid, out var part) || !_prototypes.TryIndex(part.Part, out var prototype) ||
            !TryComp<NCVehicleRepairKitComponent>(used, out var kit) || kit.Uses <= 0 ||
            !float.IsFinite(kit.RestoreAmount) || kit.RestoreAmount <= 0 ||
            !float.IsFinite(kit.MaximumCondition) || kit.MaximumCondition is <= 0 or > 1 ||
            !float.IsFinite(kit.Seconds) || kit.Seconds < 0 || !float.IsFinite(part.Condition) ||
            part.Condition < prototype.MinimumRepairCondition || part.Condition >= Math.Min(kit.MaximumCondition, prototype.MaximumRepairCondition) ||
            prototype.RepairCategory != kit.Category ||
            (kit.Mount != null && kit.Mount != prototype.Mount) || _hands.GetActiveItem(user) != used ||
            !_interaction.InRangeUnobstructed(user, uid))
            return false;
        foreach (var bench in _lookup.GetEntitiesInRange<NCVehicleWorkshopEquipmentComponent>(Transform(uid).Coordinates, kit.WorkbenchRange))
            if (bench.Comp.Workbench && Transform(bench).Anchored &&
                _interaction.InRangeUnobstructed(user, bench.Owner, range: kit.WorkbenchRange))
                return true;
        return false;
    }

    public bool TryRepair(EntityUid uid, EntityUid user, EntityUid used)
    {
        if (!CanRepair(uid, user, used))
            return false;
        var kit = Comp<NCVehicleRepairKitComponent>(used);
        return _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, user, kit.Seconds,
            new NCVehicleRepairDoAfterEvent(), uid, uid, used)
        { BreakOnMove = true, BreakOnDamage = true, NeedHand = true });
    }

    private void OnComplete(Entity<NCVehiclePartItemComponent> ent, ref NCVehicleRepairDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || args.Used is not { } used || !CanRepair(ent, args.User, used))
            return;
        args.Handled = true;
        var kit = Comp<NCVehicleRepairKitComponent>(used);
        var prototype = _prototypes.Index(ent.Comp.Part);
        ent.Comp.Condition = Math.Min(Math.Min(kit.MaximumCondition, prototype.MaximumRepairCondition), ent.Comp.Condition + kit.RestoreAmount);
        kit.Uses--;
        Dirty(ent);
        Dirty(used, kit);
        if (kit.Uses == 0)
            Del(used);
    }
}
