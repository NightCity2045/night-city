// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle.Components;
using Content.Shared._NC.Vehicle.Prototypes;
using Content.Shared.ActionBlocker;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Physics;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Robust.Shared.Containers;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Vehicle;

/// <summary>Player-installed breakable distance joints. Robust owns the constraint and its replicated state.</summary>
public sealed partial class NCVehicleTowSystem : EntitySystem
{
    private const string CableContainer = "nc-vehicle-tow-cable";
    [Dependency] private SharedJointSystem _joints = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedPopupSystem _popup = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCVehicleComponent, InteractUsingEvent>(OnUse);
        SubscribeLocalEvent<NCVehicleTowLinkComponent, GetVerbsEvent<AlternativeVerb>>(OnVerbs);
        SubscribeLocalEvent<NCVehicleTowLinkComponent, JointRemovedEvent>(OnJointRemoved);
        SubscribeLocalEvent<NCVehicleTowLinkComponent, EntityTerminatingEvent>(OnTerminating);
        SubscribeLocalEvent<NCVehicleTowCableComponent, EntityTerminatingEvent>(OnCableDeleted);
    }

    private void OnUse(Entity<NCVehicleComponent> ent, ref InteractUsingEvent args)
    {
        if (!args.Handled && TryUseCable(ent, args.User, args.Used))
        {
            args.Handled = true;
            _popup.PopupEntity(Loc.GetString(HasComp<NCVehicleTowLinkComponent>(ent)
                ? "nc-vehicle-tow-attached" : "nc-vehicle-tow-first"), ent, args.User);
        }
    }

    public bool TryUseCable(EntityUid uid, EntityUid user, EntityUid cable)
    {
        if (!TryComp<NCVehicleTowCableComponent>(cable, out var rope) || _hands.GetActiveItem(user) != cable ||
            !_blocker.CanConsciouslyPerformAction(user) || !_interaction.InRangeUnobstructed(user, uid) ||
            !CanAttach(uid, rope.AttachSpeed) || !float.IsFinite(rope.MaximumLength) || rope.MaximumLength <= 0 ||
            !float.IsFinite(rope.MinimumLength) || rope.MinimumLength < 0 || rope.MinimumLength > rope.MaximumLength ||
            !float.IsFinite(rope.Stiffness) || rope.Stiffness < 0 || !float.IsFinite(rope.Breakpoint) || rope.Breakpoint <= 0)
            return false;
        if (rope.First is not { } first || TerminatingOrDeleted(first))
        {
            rope.First = uid;
            return true;
        }
        if (first == uid)
        {
            rope.First = null;
            return true;
        }
        if (!CanAttach(first, rope.AttachSpeed) || Transform(first).MapID != Transform(uid).MapID ||
            !TryChassis(first, out var front) || !TryChassis(uid, out var rear))
            return false;
        var firstPoint = new Robust.Shared.Map.EntityCoordinates(first, front!.RearTowPoint);
        var secondPoint = new Robust.Shared.Map.EntityCoordinates(uid, rear!.FrontTowPoint);
        var length = System.Numerics.Vector2.Distance(_transform.ToMapCoordinates(firstPoint).Position,
            _transform.ToMapCoordinates(secondPoint).Position);
        if (length > rope.MaximumLength || length < rope.MinimumLength ||
            !_interaction.InRangeUnobstructed(first, uid, range: rope.MaximumLength))
            return false;
        var container = _containers.EnsureContainer<ContainerSlot>(first, CableContainer);
        if (!_hands.TryDropIntoContainer(user, cable, container))
            return false;
        var id = $"nc-tow-{GetNetEntity(cable)}";
        var joint = _joints.CreateDistanceJoint(first, uid, front.RearTowPoint, rear.FrontTowPoint, id: id);
        joint.MinLength = rope.MinimumLength;
        joint.MaxLength = rope.MaximumLength;
        joint.Stiffness = rope.Stiffness;
        joint.Breakpoint = rope.Breakpoint;
        joint.CollideConnected = true;
        var a = EnsureComp<NCVehicleTowLinkComponent>(first);
        a.Other = uid; a.Cable = cable; a.Holder = first; a.Joint = id;
        var b = EnsureComp<NCVehicleTowLinkComponent>(uid);
        b.Other = first; b.Cable = cable; b.Holder = first; b.Joint = id;
        var visuals = EnsureComp<JointVisualsComponent>(first);
        visuals.Sprite = rope.RopeSprite;
        visuals.Target = uid;
        visuals.OffsetA = front.RearTowPoint;
        visuals.OffsetB = rear.FrontTowPoint;
        Dirty(first, visuals);
        rope.First = null;
        rope.Holder = first;
        return true;
    }

    private bool CanAttach(EntityUid uid, float speed)
        => float.IsFinite(speed) && speed >= 0 && !TerminatingOrDeleted(uid) &&
            !HasComp<NCVehicleTowLinkComponent>(uid) && HasComp<NCVehicleComponent>(uid) &&
            TryComp<PhysicsComponent>(uid, out var body) && body.BodyType == BodyType.Dynamic && body.LinearVelocity.Length() <= speed;

    private bool TryChassis(EntityUid uid, out NCVehicleChassisPrototype? chassis)
    {
        chassis = null;
        return TryComp<NCVehicleComponent>(uid, out var vehicle) && _prototypes.TryIndex(vehicle.Chassis, out chassis);
    }

    private void OnVerbs(Entity<NCVehicleTowLinkComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract)
            return;
        var user = args.User;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-vehicle-tow-detach"), Act = () => TryDetach(ent, user) });
    }

    public bool TryDetach(EntityUid uid, EntityUid user)
    {
        if (!_blocker.CanConsciouslyPerformAction(user) || !_interaction.InRangeUnobstructed(user, uid) ||
            !TryComp<NCVehicleTowLinkComponent>(uid, out var link) ||
            !TryComp<NCVehicleTowCableComponent>(link.Cable, out var rope) ||
            !TryComp<PhysicsComponent>(uid, out var body) || body.LinearVelocity.Length() > rope.AttachSpeed ||
            !TryComp<PhysicsComponent>(link.Other, out var other) || other.LinearVelocity.Length() > rope.AttachSpeed)
            return false;
        Release(uid);
        return true;
    }

    private void OnJointRemoved(Entity<NCVehicleTowLinkComponent> ent, ref JointRemovedEvent args)
    {
        if (args.Joint.ID == ent.Comp.Joint)
            Release(ent);
    }
    private void OnTerminating(Entity<NCVehicleTowLinkComponent> ent, ref EntityTerminatingEvent args) => Release(ent);
    private void OnCableDeleted(Entity<NCVehicleTowCableComponent> ent, ref EntityTerminatingEvent args)
    {
        if (ent.Comp.Holder is { } holder)
            Release(holder);
    }

    private void Release(EntityUid uid)
    {
        if (!TryComp<NCVehicleTowLinkComponent>(uid, out var link))
            return;
        var other = link.Other;
        var holder = link.Holder;
        var cable = link.Cable;
        var joint = link.Joint;
        if (TryComp<NCVehicleTowCableComponent>(cable, out var rope))
            rope.Holder = null;
        // Clear both endpoints before removing the joint; the engine synchronously notifies both bodies.
        RemComp<NCVehicleTowLinkComponent>(uid);
        RemComp<NCVehicleTowLinkComponent>(other);
        _joints.RemoveJoint(uid, joint);
        if (TryComp<JointVisualsComponent>(holder, out var visuals))
        {
            visuals.Target = null;
            Dirty(holder, visuals);
            RemCompDeferred<JointVisualsComponent>(holder);
        }
        if (!Deleted(holder) && _containers.TryGetContainer(holder, CableContainer, out var container) &&
            !TerminatingOrDeleted(cable))
            _containers.Remove(cable, container, force: true, destination: Transform(holder).Coordinates);
    }
}
