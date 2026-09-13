// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared.ActionBlocker;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.DoAfter;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Assembly;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared._NC.Vehicle.Prototypes;
using Content.Shared._NC.Vehicle.Workshop;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Vehicle.Workshop;

/// <summary>Operates on the real detached assembly contained by a physical workshop machine.</summary>
public sealed partial class NCVehicleWorkbenchSystem : EntitySystem
{
    public const string AssemblySlot = "assembly";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private ItemSlotsSystem _itemSlots = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private NCVehiclePartPhysicsSystem _partPhysics = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCVehicleWorkshopEquipmentComponent, BoundUIOpenedEvent>(OnOpened);
        SubscribeLocalEvent<NCVehicleWorkshopEquipmentComponent, NCVehicleWorkbenchRefresh>(OnRefresh);
        SubscribeLocalEvent<NCVehicleWorkshopEquipmentComponent, NCVehicleWorkbenchRequest>(OnRequest);
        SubscribeLocalEvent<NCVehicleWorkshopEquipmentComponent, NCVehicleWorkbenchDoAfter>(OnComplete);
        SubscribeLocalEvent<NCVehicleWorkshopEquipmentComponent, EntInsertedIntoContainerMessage>(OnContainerChanged);
        SubscribeLocalEvent<NCVehicleWorkshopEquipmentComponent, EntRemovedFromContainerMessage>(OnContainerChanged);
    }

    private void OnOpened(Entity<NCVehicleWorkshopEquipmentComponent> ent, ref BoundUIOpenedEvent args)
    {
        if (Equals(args.UiKey, NCVehicleUiKey.Workbench))
            UpdateState(ent, args.Actor, "nc-workbench-ready");
    }

    private void OnRefresh(Entity<NCVehicleWorkshopEquipmentComponent> ent, ref NCVehicleWorkbenchRefresh args)
        => UpdateState(ent, args.Actor, "nc-workbench-ready");

    private void OnRequest(Entity<NCVehicleWorkshopEquipmentComponent> ent, ref NCVehicleWorkbenchRequest args)
    {
        TryStart(ent, args.Actor, args, out var feedback);
        UpdateState(ent, args.Actor, feedback);
    }

    public bool TryStart(Entity<NCVehicleWorkshopEquipmentComponent> ent, EntityUid user,
        NCVehicleWorkbenchRequest request, out LocId feedback)
    {
        if (TryComp<NCVehicleWorkbenchSessionComponent>(user, out var active) && active.Pending)
        {
            feedback = "nc-workbench-busy";
            return false;
        }
        if (!CanOperate(ent, user, request, out _, out _, out _, out var profile, out _, out feedback))
            return false;
        var used = _hands.GetActiveItem(user);
        var seconds = request.Action switch
        {
            NCVehicleWorkbenchAction.Inspect => profile.InspectSeconds,
            NCVehicleWorkbenchAction.Repair when used is { } kit => Comp<NCVehicleRepairKitComponent>(kit).Seconds,
            _ => profile.TransferSeconds,
        };
        var ev = new NCVehicleWorkbenchDoAfter
        {
            Node = request.Node,
            Position = request.Position,
            Revision = request.Revision,
            Action = request.Action,
        };
        if (!_doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, user, seconds, ev, ent, ent, used)
            { BreakOnMove = true, BreakOnDamage = true, NeedHand = true, DistanceThreshold = ent.Comp.Range }))
        {
            feedback = "nc-workbench-busy";
            return false;
        }
        var session = EnsureComp<NCVehicleWorkbenchSessionComponent>(user);
        session.Bench = ent;
        session.Pending = true;
        feedback = "nc-workbench-working";
        return true;
    }

    private void OnComplete(Entity<NCVehicleWorkshopEquipmentComponent> ent, ref NCVehicleWorkbenchDoAfter args)
    {
        if (args.Handled || !TryComp<NCVehicleWorkbenchSessionComponent>(args.User, out var session) ||
            !session.Pending || session.Bench != ent.Owner)
            return;
        args.Handled = true;
        session.Pending = false;
        var feedback = (LocId) "nc-workbench-cancelled";
        if (!args.Cancelled && args.Used == _hands.GetActiveItem(args.User))
        {
            var request = new NCVehicleWorkbenchRequest(args.Node, args.Position, args.Revision, args.Action);
            TryOperate(ent, args.User, request, out feedback);
        }
        UpdateState(ent, args.User, feedback);
    }

    private void OnContainerChanged(Entity<NCVehicleWorkshopEquipmentComponent> ent, ref EntInsertedIntoContainerMessage args)
    {
        if (args.Container.ID == AssemblySlot)
            UpdateState(ent, null, "nc-workbench-ready");
    }

    private void OnContainerChanged(Entity<NCVehicleWorkshopEquipmentComponent> ent, ref EntRemovedFromContainerMessage args)
    {
        if (args.Container.ID == AssemblySlot)
            UpdateState(ent, null, "nc-workbench-ready");
    }

    public bool TryOperate(Entity<NCVehicleWorkshopEquipmentComponent> ent, EntityUid user,
        NCVehicleWorkbenchRequest request, out LocId feedback)
    {
        feedback = "nc-workbench-unavailable";
        if (!CanOperate(ent, user, request, out var item, out var bundle,
                out var chassis, out var profile, out var node, out feedback))
            return false;

        if (request.Action == NCVehicleWorkbenchAction.Inspect)
        {
            feedback = "nc-workbench-inspected";
            return true;
        }

        if (request.Action == NCVehicleWorkbenchAction.Repair)
        {
            var state = bundle.Parts[node.Slot];
            var kitUid = _hands.GetActiveItem(user)!.Value;
            var kit = Comp<NCVehicleRepairKitComponent>(kitUid);
            var definition = _prototypes.Index(state.Prototype);
            var condition = Math.Min(Math.Min(kit.MaximumCondition, definition.MaximumRepairCondition),
                state.Condition + kit.RestoreAmount);
            bundle.Parts[node.Slot] = state with { Condition = condition };
            if (node.Slot == bundle.RootSlot)
            {
                var root = Comp<NCVehiclePartItemComponent>(item);
                root.Condition = condition;
                Dirty(item, root);
            }
            kit.Uses--;
            Dirty(kitUid, kit);
            if (kit.Uses == 0)
                Del(kitUid);
            bundle.Revision++;
            feedback = "nc-workbench-repaired";
            return true;
        }

        if (request.Action == NCVehicleWorkbenchAction.Remove)
        {
            var state = bundle.Parts[node.Slot];
            var definition = _prototypes.Index(state.Prototype);
            if (definition.Item is not { } itemPrototype)
                return false;

            var removed = Spawn(itemPrototype, Transform(ent).Coordinates);
            var partItem = EnsureComp<NCVehiclePartItemComponent>(removed);
            partItem.Part = state.Prototype;
            partItem.Condition = state.Condition;
            partItem.StoredCharge = state.StoredCharge;
            Dirty(removed, partItem);

            // Moving a carrier moves its authored descendants as one physical assembly.
            var removedBundle = EnsureComp<NCVehicleDetachedAssemblyComponent>(removed);
            removedBundle.Chassis = bundle.Chassis;
            removedBundle.RootSlot = node.Slot;
            foreach (var slot in CollectTree(chassis, node.Slot))
                if (bundle.Parts.Remove(slot, out var child))
                    removedBundle.Parts[slot] = child;
            bundle.Revision++;
            _partPhysics.TryRefresh(item);
            _partPhysics.TryRefresh(removed);
            _hands.TryPickupAnyHand(user, removed);
            feedback = "nc-workbench-removed";
            return true;
        }

        var held = _hands.GetActiveItem(user)!.Value;
        var heldItem = Comp<NCVehiclePartItemComponent>(held);
        var changes = new Dictionary<string, NCVehicleInstalledPart>
        {
            [node.Slot] = new(heldItem.Part, heldItem.Condition, StoredCharge: heldItem.StoredCharge),
        };
        if (TryComp<NCVehicleDetachedAssemblyComponent>(held, out var heldBundle))
            foreach (var (slot, part) in heldBundle.Parts)
                changes[slot] = slot == node.Slot
                    ? part with { Condition = heldItem.Condition, StoredCharge = heldItem.StoredCharge }
                    : part;
        foreach (var (slot, part) in changes)
            bundle.Parts[slot] = part;
        bundle.Revision++;
        Del(held);
        _partPhysics.TryRefresh(item);
        feedback = "nc-workbench-installed";
        return true;
    }

    public bool CanOperate(Entity<NCVehicleWorkshopEquipmentComponent> ent, EntityUid user,
        NCVehicleWorkbenchRequest request, out EntityUid item, out NCVehicleDetachedAssemblyComponent bundle,
        out NCVehicleChassisPrototype chassis, out NCVehicleWorkshopPrototype profile,
        out NCVehicleWorkshopNode node, out LocId feedback)
    {
        item = default;
        bundle = default!;
        chassis = default!;
        profile = default!;
        node = default!;
        feedback = "nc-workbench-unavailable";
        if (!ent.Comp.Workbench || !Transform(ent).Anchored ||
            !_blocker.CanConsciouslyPerformAction(user) ||
            !_interaction.InRangeUnobstructed(user, ent.Owner, range: ent.Comp.Range) ||
            !_ui.IsUiOpen(ent.Owner, NCVehicleUiKey.Workbench, user) || request.Node.Length > 64 ||
            !float.IsFinite(request.Position.X) || !float.IsFinite(request.Position.Y) ||
            _itemSlots.GetItemOrNull(ent, AssemblySlot) is not { } target ||
            !TryComp(target, out bundle!) || !_prototypes.TryIndex(bundle.Chassis, out chassis!) ||
            chassis.Workshop is not { } workshop || !_prototypes.TryIndex(workshop, out profile!) ||
            !profile.Nodes.TryGetValue(request.Node, out node!) ||
            !CollectTree(chassis, bundle.RootSlot).Contains(node.Slot) ||
            !NCVehicleWorkshopGeometry.Contains(profile, request.Node, node, request.Position) || request.Revision != bundle.Revision)
            return false;
        item = target;

        if (request.Action == NCVehicleWorkbenchAction.Inspect)
        {
            if (_hands.GetActiveItem(user) is { } gauge && HasComp<NCVehicleDiagnosticToolComponent>(gauge))
                return true;
            feedback = "nc-workbench-diagnostic-needed";
            return false;
        }
        if (request.Action == NCVehicleWorkbenchAction.Repair)
        {
            if (!bundle.Parts.TryGetValue(node.Slot, out var state) ||
                _hands.GetActiveItem(user) is not { } kitUid ||
                !TryComp<NCVehicleRepairKitComponent>(kitUid, out var kit) || kit.Uses <= 0 ||
                !_prototypes.TryIndex(state.Prototype, out var definition) || !float.IsFinite(state.Condition) ||
                state.Condition < definition.MinimumRepairCondition ||
                state.Condition >= Math.Min(kit.MaximumCondition, definition.MaximumRepairCondition) ||
                definition.RepairCategory != kit.Category || kit.Mount != null && kit.Mount != definition.Mount)
            {
                feedback = "nc-workbench-repair-kit";
                return false;
            }
            return true;
        }
        if (!ent.Comp.Assembly || node.Slot == bundle.RootSlot)
        {
            feedback = "nc-workbench-child-only";
            return false;
        }
        if (request.Action == NCVehicleWorkbenchAction.Remove)
        {
            if (!bundle.Parts.ContainsKey(node.Slot))
            {
                feedback = "nc-workbench-empty";
                return false;
            }
            if (_hands.GetActiveItem(user) == null)
                return true;
            feedback = "nc-workbench-empty-hand";
            return false;
        }
        if (request.Action != NCVehicleWorkbenchAction.Install || bundle.Parts.ContainsKey(node.Slot) ||
            _hands.GetActiveItem(user) is not { } held || !TryComp<NCVehiclePartItemComponent>(held, out var heldItem) ||
            !_prototypes.TryIndex(heldItem.Part, out var heldDefinition) ||
            heldDefinition.Mount != chassis.Slots[node.Slot].Mount)
        {
            feedback = "nc-workbench-compatible-part";
            return false;
        }
        if (!TryComp<NCVehicleDetachedAssemblyComponent>(held, out var heldBundle))
            return true;
        var allowed = CollectTree(chassis, node.Slot);
        var currentParts = bundle.Parts;
        if (heldBundle.Chassis != bundle.Chassis || heldBundle.RootSlot != node.Slot ||
            heldBundle.Parts.Keys.Any(slot => !allowed.Contains(slot) || currentParts.ContainsKey(slot)))
        {
            feedback = "nc-workbench-compatible-part";
            return false;
        }
        return true;
    }

    public HashSet<string> CollectTree(NCVehicleChassisPrototype chassis, string root)
    {
        var result = new HashSet<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var slot) && result.Add(slot))
            foreach (var child in chassis.Slots[slot].ExtractWith)
                pending.Push(child);
        return result;
    }

    public void UpdateState(Entity<NCVehicleWorkshopEquipmentComponent> ent, EntityUid? user, LocId feedback)
    {
        var tool = user is { } actor && _hands.GetActiveItem(actor) is { } held
            ? MetaData(held).EntityPrototype?.ID
            : null;
        if (_itemSlots.GetItemOrNull(ent, AssemblySlot) is not { } item ||
            !TryComp<NCVehicleDetachedAssemblyComponent>(item, out var bundle) ||
            !_prototypes.TryIndex(bundle.Chassis, out var chassis) || chassis.Workshop is not { } workshop ||
            !_prototypes.TryIndex(workshop, out _))
        {
            _ui.SetUiState(ent.Owner, NCVehicleUiKey.Workbench,
                new NCVehicleWorkbenchState(null, null, string.Empty, [], 0, "nc-workbench-empty-machine", tool,
                    ent.Comp.Assembly, user is { } emptyUser && TryComp<NCVehicleWorkbenchSessionComponent>(emptyUser, out var emptySession) && emptySession.Pending));
            return;
        }
        var parts = new List<NCVehicleWorkshopPartState>();
        foreach (var slot in CollectTree(chassis, bundle.RootSlot))
        {
            var installed = bundle.Parts.TryGetValue(slot, out var part);
            parts.Add(new(slot, installed, bundle.Revision, part.WorkPoints == null ? new() : new(part.WorkPoints),
                part.PointTorque == null ? new() : new(part.PointTorque),
                installed ? part.Condition : null, true, false, false));
        }
        _ui.SetUiState(ent.Owner, NCVehicleUiKey.Workbench, new NCVehicleWorkbenchState(bundle.Chassis, workshop,
            bundle.RootSlot, parts.ToArray(), bundle.Revision, feedback, tool, ent.Comp.Assembly,
            user is { } actorUser && TryComp<NCVehicleWorkbenchSessionComponent>(actorUser, out var session) && session.Pending));
    }
}
