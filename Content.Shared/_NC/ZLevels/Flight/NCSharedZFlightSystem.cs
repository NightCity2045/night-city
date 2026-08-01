/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Shared._NC.ZLevels.Core.Components;
using Content.Shared._NC.ZLevels.Core.EntitySystems;
using Content.Shared._NC.ZLevels.Flight.Components;
using Content.Shared.Actions;
using Content.Shared.Audio;
using Content.Shared.Damage.Systems;
using Content.Shared.DoAfter;
using Content.Shared.Gravity;
using Content.Shared.Mobs;
using Content.Shared.Stunnable;
using JetBrains.Annotations;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.ZLevels.Flight;

public abstract partial class NCSharedZFlightSystem : EntitySystem
{
    [Dependency] private NCSharedZLevelsSystem _zLevel = default!;
    [Dependency] private SharedAmbientSoundSystem _ambient = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private SharedActionsSystem _actions = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedGravitySystem _gravity = default!;

    protected EntityQuery<NCZPhysicsComponent> ZPhyzQuery;

    public override void Initialize()
    {
        base.Initialize();
        InitializeControllable();

        ZPhyzQuery = GetEntityQuery<NCZPhysicsComponent>();

        SubscribeLocalEvent<NCZPhysicsComponent, NCFlightStartedEvent>(OnStartFlight);
        SubscribeLocalEvent<NCZPhysicsComponent, NCFlightStoppedEvent>(OnStopFlight);
        SubscribeLocalEvent<NCZFlyerComponent, NCGetZVelocityEvent>(OnGetZVelocity);
        SubscribeLocalEvent<NCZFlyerComponent, NCCheckGravityEvent>(OnGetGravity);
        SubscribeLocalEvent<NCZFlyerComponent, IsWeightlessEvent>(CheckWeightless);

        SubscribeLocalEvent<NCZFlyerComponent, StunnedEvent>(OnStunned);
        SubscribeLocalEvent<NCZFlyerComponent, KnockedDownEvent>(OnKnockDowned);
        SubscribeLocalEvent<NCZFlyerComponent, MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<NCZFlyerComponent, DamageChangedEvent>(OnDamageChanged);
    }

    private void CheckWeightless(Entity<NCZFlyerComponent> ent, ref IsWeightlessEvent args)
    {
        if (!ent.Comp.Active || args.Handled)
            return;

        args.IsWeightless = true;
        args.Handled = true;
    }

    private void OnDamageChanged(Entity<NCZFlyerComponent> ent, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased)
            return;

        if (!args.InterruptsDoAfters)
            return;

        DeactivateFlight((ent, ent));
    }

    private void OnMobStateChanged(Entity<NCZFlyerComponent> ent, ref MobStateChangedEvent args)
    {
        DeactivateFlight((ent, ent));
    }

    private void OnKnockDowned(Entity<NCZFlyerComponent> ent, ref KnockedDownEvent args)
    {
        DeactivateFlight((ent, ent));
    }

    private void OnStunned(Entity<NCZFlyerComponent> ent, ref StunnedEvent args)
    {
        DeactivateFlight((ent, ent));
    }

    private void OnStartFlight(Entity<NCZPhysicsComponent> ent, ref NCFlightStartedEvent args)
    {
        SetTargetHeight(ent.Owner, ent.Comp.CurrentZLevel);
        StartFlightVisuals(ent.Owner);
    }

    private void OnStopFlight(Entity<NCZPhysicsComponent> ent, ref NCFlightStoppedEvent args)
    {
        StopFlightVisuals(ent.Owner);
    }

    private void OnGetZVelocity(Entity<NCZFlyerComponent> ent, ref NCGetZVelocityEvent args)
    {
        if (!ent.Comp.Active)
            return;

        var zPhys = args.Target.Comp;
        var currentPos = zPhys.CurrentZLevel + zPhys.LocalPosition;
        var targetPos = ent.Comp.TargetMapHeight + 0.2f;
        var currentVelocity = zPhys.Velocity;

        var distanceToTarget = targetPos - currentPos;

        var targetVelocity = Math.Clamp(distanceToTarget * ent.Comp.FlightSpeed, -ent.Comp.FlightSpeed, ent.Comp.FlightSpeed);
        var velocityDelta = targetVelocity - currentVelocity;

        var upperBound = ent.Comp.TargetMapHeight + 0.9f;
        var lowerBound = ent.Comp.TargetMapHeight + 0.1f;

        var newVelocity = currentVelocity + velocityDelta;
        var nextPos = currentPos + newVelocity;

        if (nextPos > upperBound)
        {
            var maxAllowedVelocity = upperBound - currentPos;
            velocityDelta = maxAllowedVelocity - currentVelocity;
        }
        else if (nextPos < lowerBound)
        {
            var maxAllowedVelocity = lowerBound - currentPos;
            velocityDelta = maxAllowedVelocity - currentVelocity;
        }

        args.VelocityDelta = velocityDelta;
    }

    private void OnGetGravity(Entity<NCZFlyerComponent> ent, ref NCCheckGravityEvent args)
    {
        if (ent.Comp.Active)
            args.Gravity *= 0;
    }

    [PublicAPI]
    public bool TryActivateFlight(Entity<NCZFlyerComponent?> ent, NCZPhysicsComponent? zPhys = null)
    {
        if (!Resolve(ent, ref ent.Comp, false))
            return false;

        if (!Resolve(ent, ref zPhys, false))
            return false;

        if (ent.Comp.Active)
            return false;

        var ev = new NCStartFlightAttemptEvent();
        RaiseLocalEvent(ent, ev);

        if (ev.Cancelled)
            return false;

        ent.Comp.Active = true;
        DirtyField(ent, ent.Comp, nameof(NCZFlyerComponent.Active));

        _zLevel.UpdateGravityState((ent, zPhys));
        _gravity.RefreshWeightless(ent.Owner);

        RaiseLocalEvent(ent, new NCFlightStartedEvent());
        return true;
    }

    [PublicAPI]
    public void DeactivateFlight(Entity<NCZFlyerComponent?> ent, NCZPhysicsComponent? zPhys = null)
    {
        if (!Resolve(ent, ref ent.Comp, false))
            return;

        if (!Resolve(ent, ref zPhys, false))
            return;

        if (!ent.Comp.Active)
            return;

        ent.Comp.Active = false;
        DirtyField(ent, ent.Comp, nameof(NCZFlyerComponent.Active));

        _zLevel.UpdateGravityState((ent, zPhys));
        _gravity.RefreshWeightless(ent.Owner);

        RaiseLocalEvent(ent, new NCFlightStoppedEvent());
    }

    [PublicAPI]
    public void SetTargetHeight(Entity<NCZFlyerComponent?> ent, int targetHeight)
    {
        if (!Resolve(ent, ref ent.Comp, false))
            return;

        ent.Comp.TargetMapHeight = targetHeight;
        DirtyField(ent, ent.Comp, nameof(NCZFlyerComponent.TargetMapHeight));
    }

    private void StartFlightVisuals(Entity<NCZFlyerComponent?> ent)
    {
        if (!Resolve(ent, ref ent.Comp, false))
            return;

        _appearance.SetData(ent, NCFlightVisuals.Active, true);
        _ambient.SetAmbience(ent, true);
    }

    private void StopFlightVisuals(Entity<NCZFlyerComponent?> ent)
    {
        if (!Resolve(ent, ref ent.Comp, false))
            return;

        _appearance.SetData(ent, NCFlightVisuals.Active, false);
        _ambient.SetAmbience(ent, false);
    }
}

/// <summary>
/// Called on an entity when it attempts to start flight mode. Subscribe and cancel this event if you want to cancel your flight for any reason.
/// </summary>
public sealed partial class NCStartFlightAttemptEvent : CancellableEntityEventArgs;

/// <summary>
/// Called on an entity when it enters flight mode
/// </summary>
public sealed partial class NCFlightStartedEvent : EntityEventArgs;

/// <summary>
/// Called on an entity when it exits flight mode
/// </summary>
public sealed partial class NCFlightStoppedEvent : EntityEventArgs;


/// <summary>
/// Instant Action, raising the target flight level by 1
/// </summary>
public sealed partial class NCZFlightActionUp : InstantActionEvent
{
}

/// <summary>
/// Instant Action, lowering the target flight level by 1
/// </summary>
public sealed partial class NCZFlightActionDown : InstantActionEvent
{
}


[Serializable, NetSerializable]
public enum NCFlightVisuals
{
    Active,
}

/// <summary>
/// DoAfter event for starting flight with a delay
/// </summary>
[Serializable, NetSerializable]
public sealed partial class NCStartFlightDoAfterEvent : SimpleDoAfterEvent
{
}
