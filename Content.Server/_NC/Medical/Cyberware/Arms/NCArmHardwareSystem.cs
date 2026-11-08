// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Anatomy.Detachment;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Abilities;
using Content.Shared._NC.Medical.Cyberware.Arms;
using Content.Shared.Body;
using Content.Shared.DoAfter;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.ActionBlocker;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Robust.Shared.Timing;
using Robust.Shared.Containers;

namespace Content.Server._NC.Medical.Cyberware.Arms;

public sealed partial class NCArmHardwareSystem : EntitySystem
{
    [Dependency] private NCCyberwareSystem _cyberware = default!;
    [Dependency] private Abilities.NCCyberwareAbilitySystem _abilities = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private NCMedicalBodySystem _medical = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private Robust.Shared.Prototypes.IPrototypeManager _prototypes = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private OrganRelationSystem _organRelations = default!;
    private TimeSpan _nextUpdate;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCArmServiceBodyComponent, GetVerbsEvent<AlternativeVerb>>(OnServiceVerbs);
        SubscribeLocalEvent<NCArmServiceBodyComponent, NCArmServiceDoAfterEvent>(OnServiceComplete);
        SubscribeLocalEvent<NCArmFrameComponent, MapInitEvent>(OnFrameMapInit);
        InitializeCombat();
    }

    private void OnFrameMapInit(Entity<NCArmFrameComponent> ent, ref MapInitEvent args)
    {
        if (ent.Comp.HandInitialized || ent.Comp.DefaultHand is not { } prototype)
            return;

        ent.Comp.HandInitialized = true;
        // A loaded anatomy subtree already owns its children.
        if (!TryComp<ParentOrganComponent>(ent, out var parent) || parent.Children.Count != 0)
            return;

        var hand = Spawn(prototype, Transform(ent).Coordinates);
        var container = _containers.EnsureContainer<Container>(ent, "nc-arm-hand");
        if (!_containers.Insert(hand, container))
        {
            Del(hand);
            return;
        }

        // The arm root owns graft compatibility for this fresh subtree. A child is
        // not a separately detached graft; retaining its factory payload blocks later amputation.
        RemComp<NCDetachedOrganMedicalDataComponent>(hand);
        _organRelations.Relate((ent.Owner, parent), hand);
    }

    private IEnumerable<EntityUid> Arms(EntityUid body) => _cyberware.CollectCyberware(body).Where(HasComp<NCArmFrameComponent>);

    private bool CanService(EntityUid user, EntityUid body, EntityUid arm, EntityUid tool)
    {
        return !TerminatingOrDeleted(arm) && !TerminatingOrDeleted(tool) &&
               _cyberware.TryGetOwningBody(arm, out var owner) && owner == body &&
               HasComp<NCArmFrameComponent>(arm) && _hands.IsHolding(user, tool) &&
               _blocker.CanInteract(user, body) && _interaction.InRangeUnobstructed(user, body, 2f);
    }

    private void OnServiceVerbs(Entity<NCArmServiceBodyComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || args.Using is not { } held) return;
        var user = args.User;
        var verbs = args.Verbs;
        var body = ent.Owner;
        foreach (var arm in Arms(body))
        {
            if (!CanService(user, body, arm, held)) continue;
            var frame = Comp<NCArmFrameComponent>(arm);
            var side = Loc.GetString(Comp<OrganComponent>(arm).Category == "ArmLeft" ? "nc-arm-left" : "nc-arm-right");
            if (HasComp<NCArmServiceToolComponent>(held))
            {
                Add(frame.PanelOpen ? NCArmServiceOperation.Close : NCArmServiceOperation.Open, arm, "", "");
                if (!frame.PanelOpen)
                {
                    if (TryComp<NCCyberwareSlotHostComponent>(arm, out var closedHost) && closedHost.Modules != null)
                        foreach (var module in closedHost.Modules.ContainedEntities)
                            if (CanQuickService(arm, module, NCArmServiceOperation.Remove))
                            {
                                Add(NCArmServiceOperation.Remove, module, "", Name(module));
                                Add(NCArmServiceOperation.Calibrate, module, "", Name(module));
                            }
                    continue;
                }
                Add(NCArmServiceOperation.Calibrate, arm, "", Name(arm));
                if (TryComp<NCCyberwareSlotHostComponent>(arm, out var host) && host.Modules != null)
                    foreach (var module in host.Modules.ContainedEntities)
                    {
                        Add(NCArmServiceOperation.Remove, module, "", Name(module));
                        Add(NCArmServiceOperation.Calibrate, module, "", Name(module));
                    }
                if (TryComp<NCCyberwareAssemblyComponent>(arm, out var assembly))
                    foreach (var (slot, socket) in assembly.Parts)
                        if (socket.ContainedEntity is { } part) Add(NCArmServiceOperation.RemovePart, part, slot.Id, Name(part));
            }
            else if (frame.PanelOpen && HasComp<NCCyberwarePartComponent>(held))
                Add(NCArmServiceOperation.InsertPart, held, "", Name(held));
            else if ((frame.PanelOpen || CanQuickService(arm, held, NCArmServiceOperation.Install)) && TryComp<NCCyberwareComponent>(held, out var hardware) &&
                     TryComp<NCCyberwareSlotHostComponent>(arm, out var bays) && _prototypes.TryIndex(hardware.Profile, out var profile))
                foreach (var slot in bays.Slots)
                    if (profile.CompatibleSlots.Contains(slot)) Add(NCArmServiceOperation.Install, held, slot.Id, Name(held));
            continue;

            void Add(NCArmServiceOperation op, EntityUid module, string slot, string name)
            {
                var verb = new AlternativeVerb
                {
                    Text = Loc.GetString("nc-arm-service-" + op.ToString().ToLowerInvariant(), ("side", side), ("item", name)),
                    Act = () => TryStartService(user, body, arm, held, module, slot, op),
                };
                // Several physical bays can accept the same module; label them separately.
                if (slot != "" && op == NCArmServiceOperation.Install) verb.Text += " " + Loc.GetString("nc-arm-bay-number", ("number", baysIndex(slot)));
                verbs.Add(verb);
            }
            int baysIndex(string slot) => Comp<NCCyberwareSlotHostComponent>(arm).Slots.FindIndex(value => value.Id == slot) + 1;
        }
    }

    public bool TryStartService(EntityUid user, EntityUid body, EntityUid arm, EntityUid tool, EntityUid module, string slot, NCArmServiceOperation operation)
    {
        if (!CanService(user, body, arm, tool)) return false;
        var frame = Comp<NCArmFrameComponent>(arm);
        if (operation != NCArmServiceOperation.Open && !frame.PanelOpen && !CanQuickService(arm, module, operation) ||
            operation == NCArmServiceOperation.Open && frame.PanelOpen) return false;
        if (operation is not (NCArmServiceOperation.Install or NCArmServiceOperation.InsertPart) && !HasComp<NCArmServiceToolComponent>(tool)) return false;
        if (frame.ServiceRevision == uint.MaxValue) return false;
        var ev = new NCArmServiceDoAfterEvent { Arm = GetNetEntity(arm), Tool = GetNetEntity(tool), Module = GetNetEntity(module), Slot = slot, Revision = frame.ServiceRevision, AssemblyRevision = TryComp<NCCyberwareAssemblyComponent>(arm, out var assembly) ? assembly.ConfigurationRevision : 0, Operation = operation };
        return _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, user, TimeSpan.FromSeconds(CanQuickService(arm, module, operation) ? 1 : operation is NCArmServiceOperation.Open or NCArmServiceOperation.Close ? 2 : 4), ev, body, target: body, used: tool)
        { BreakOnMove = true, BreakOnDamage = true, NeedHand = true });
    }

    private void OnServiceComplete(Entity<NCArmServiceBodyComponent> ent, ref NCArmServiceDoAfterEvent args)
    {
        if (args.Cancelled || args.Handled || !TryGetEntity(args.Arm, out var arm) || !TryGetEntity(args.Tool, out var tool) ||
            !TryGetEntity(args.Module, out var item) || !CanService(args.User, ent, arm.Value, tool.Value)) return;
        args.Handled = true;
        var frame = Comp<NCArmFrameComponent>(arm.Value);
        if (frame.ServiceRevision != args.Revision || frame.ServiceRevision == uint.MaxValue ||
            TryComp<NCCyberwareAssemblyComponent>(arm.Value, out var assembly) && assembly.ConfigurationRevision != args.AssemblyRevision) return;
        var op = args.Operation;
        if (op is not (NCArmServiceOperation.Install or NCArmServiceOperation.InsertPart) && !HasComp<NCArmServiceToolComponent>(tool.Value)) return;
        if (op != NCArmServiceOperation.Open && !frame.PanelOpen && !CanQuickService(arm.Value, item.Value, op) || op == NCArmServiceOperation.Open && frame.PanelOpen) return;
        var success = false;
        switch (op)
        {
            case NCArmServiceOperation.Open:
                frame.PanelOpen = true;
                StopArm(ent, arm.Value);
                success = true;
                break;
            case NCArmServiceOperation.Close:
                frame.PanelOpen = false;
                success = true;
                break;
            case NCArmServiceOperation.Install:
                if (item == tool && _cyberware.CanFitArmModule(arm.Value, item.Value))
                    success = _cyberware.TryInstall(new(NCActionResolutionId.New(), ent, arm.Value, item.Value, args.Slot, true)).Accepted;
                break;
            case NCArmServiceOperation.Remove:
                if (IsChild(arm.Value, item.Value) && CanRemoveSocket(arm.Value, item.Value))
                {
                    StopModule(ent, item.Value);
                    success = _cyberware.TryRemove(new(NCActionResolutionId.New(), ent, item.Value)).Accepted;
                    if (success) _hands.TryPickupAnyHand(args.User, item.Value);
                }
                break;
            case NCArmServiceOperation.Calibrate:
                if (item == arm || IsChild(arm.Value, item.Value))
                    success = _cyberware.TryCalibrateCyberware(item.Value, NCActionResolutionId.New(), out _);
                break;
            case NCArmServiceOperation.InsertPart:
                if (item == tool) success = _cyberware.TryServiceArmPart(arm.Value, args.User, item, "");
                break;
            case NCArmServiceOperation.RemovePart:
                success = _cyberware.TryServiceArmPart(arm.Value, args.User, null, args.Slot);
                break;
        }
        if (success) { frame.ServiceRevision++; _abilities.RefreshBody(ent); _medical.TryMarkDirty(ent.Owner, NCMedicalDirtyDomains.Persistence | NCMedicalDirtyDomains.Diagnostics); }
        _popup.PopupEntity(Loc.GetString(success ? "nc-arm-service-success" : "nc-arm-service-failed"), ent, args.User);
    }

    private bool CanQuickService(EntityUid arm, EntityUid module, NCArmServiceOperation operation)
    {
        if (operation is not (NCArmServiceOperation.Install or NCArmServiceOperation.Remove or NCArmServiceOperation.Calibrate) ||
            !TryComp<NCArmModuleComponent>(module, out var hand) ||
            hand.Kind is not (NCArmModuleKind.Impact or NCArmModuleKind.Claws or NCArmModuleKind.Tool or NCArmModuleKind.Medical) ||
            !TryComp<NCCyberwareSlotHostComponent>(arm, out var host) || host.Modules == null) return false;
        return host.Modules.ContainedEntities.Any(uid => TryComp<NCArmModuleComponent>(uid, out var socket) &&
            socket.Kind == NCArmModuleKind.QuickSocket && TryComp<NCCyberwareComponent>(uid, out var hardware) && hardware.Operational);
    }

    private bool IsChild(EntityUid arm, EntityUid module) => TryComp<ChildOrganComponent>(module, out var child) && child.Parent == arm;
    private bool CanRemoveSocket(EntityUid arm, EntityUid module) =>
        !TryComp<NCArmModuleComponent>(module, out var part) || part.Kind != NCArmModuleKind.QuickSocket ||
        !Comp<NCCyberwareSlotHostComponent>(arm).Modules!.ContainedEntities.Any(uid => uid != module && TryComp<NCArmModuleComponent>(uid, out var other) && other.RequiresQuickSocket);
    private void StopModule(EntityUid body, EntityUid module) { if (HasComp<NCCyberwareAbilityComponent>(module)) _abilities.TrySetEnabled(body, module, false); }
    private void StopArm(EntityUid body, EntityUid arm)
    {
        if (TryComp<NCCyberwareSlotHostComponent>(arm, out var slots) && slots.Modules != null)
            foreach (var module in slots.Modules.ContainedEntities) StopModule(body, module);
    }

    public override void Update(float frameTime)
    {
        if (_timing.CurTime < _nextUpdate) return;
        _nextUpdate = _timing.CurTime + TimeSpan.FromSeconds(.1);
        var frames = EntityQueryEnumerator<NCArmFrameComponent>();
        while (frames.MoveNext(out var uid, out _))
            if (_cyberware.TryGetOwningBody(uid, out var body)) EnsureComp<NCArmServiceBodyComponent>(body);
        var pockets = EntityQueryEnumerator<NCArmPocketComponent>();
        while (pockets.MoveNext(out var uid, out var pocket))
        {
            EntityUid? owner = _cyberware.TryGetOwningBody(uid, out var body) && _abilities.CanOperate(body, uid) ? body : null;
            if (owner == pocket.Body) continue;
            _ui.CloseUis(uid);
            pocket.Body = owner;
            Dirty(uid, pocket);
        }
        UpdateVisuals();
    }
}
