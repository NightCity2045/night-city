// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Eye.Blinding.Systems;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction.Events;
using Content.Shared.Item;
using Content.Shared.Movement.Systems;
using Content.Shared.Speech;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Function;

/// <summary>
/// Applies the sanitized aggregate on both client and server so movement and hand checks are predictable.
/// The authoritative anatomical calculation lives in the server system.
/// </summary>
public sealed partial class SharedNCBodyFunctionSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private MovementSpeedModifierSystem _movement = default!;
    [Dependency] private SharedHandsSystem _hands = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCBodyFunctionComponent, AfterAutoHandleStateEvent>(OnAfterState);
        SubscribeLocalEvent<NCBodyFunctionComponent, RefreshMovementSpeedModifiersEvent>(OnRefreshMovement);
        SubscribeLocalEvent<NCBodyFunctionComponent, PickupAttemptEvent>(OnPickupAttempt);
        SubscribeLocalEvent<NCBodyFunctionComponent, UseAttemptEvent>(OnUseAttempt);
        SubscribeLocalEvent<NCBodyFunctionComponent, SpeakAttemptEvent>(OnSpeakAttempt);
        SubscribeLocalEvent<NCBodyFunctionComponent, ListenAttemptEvent>(OnListenAttempt);
        SubscribeLocalEvent<NCBodyFunctionComponent, CanSeeAttemptEvent>(OnCanSeeAttempt);
    }

    private void OnAfterState(Entity<NCBodyFunctionComponent> entity, ref AfterAutoHandleStateEvent args)
    {
        // The generated state handler has already installed the new sanitized values.
        _movement.RefreshMovementSpeedModifiers(entity.Owner);
    }

    private void OnRefreshMovement(
        Entity<NCBodyFunctionComponent> entity,
        ref RefreshMovementSpeedModifiersEvent args)
    {
        if (!_prototypes.TryIndex(entity.Comp.Profile, out var profile))
            return;

        var movement = ClampUnit(entity.Comp.Movement);
        var sprint = movement >= profile.SprintFunctionThreshold
            ? movement
            : movement * ClampUnit(profile.RestrictedSprintMultiplier);
        args.ModifySpeed(movement, sprint);
    }

    private void OnPickupAttempt(Entity<NCBodyFunctionComponent> entity, ref PickupAttemptEvent args)
    {
        if (!CanUseActiveHand(entity.Owner, entity.Comp))
            args.Cancel();
    }

    private void OnUseAttempt(Entity<NCBodyFunctionComponent> entity, ref UseAttemptEvent args)
    {
        if (!CanUseActiveHand(entity.Owner, entity.Comp))
            args.Cancel();
    }

    private void OnSpeakAttempt(Entity<NCBodyFunctionComponent> entity, ref SpeakAttemptEvent args)
    {
        if (_prototypes.TryIndex(entity.Comp.Profile, out var profile) &&
            ClampUnit(entity.Comp.Speech) < profile.FunctionalSpeechThreshold)
        {
            args.Cancel();
        }
    }

    private void OnListenAttempt(Entity<NCBodyFunctionComponent> entity, ref ListenAttemptEvent args)
    {
        if (_prototypes.TryIndex(entity.Comp.Profile, out var profile) &&
            ClampUnit(entity.Comp.Hearing) < profile.FunctionalHearingThreshold)
        {
            args.Cancel();
        }
    }

    private void OnCanSeeAttempt(Entity<NCBodyFunctionComponent> entity, ref CanSeeAttemptEvent args)
    {
        if (_prototypes.TryIndex(entity.Comp.Profile, out var profile) &&
            ClampUnit(entity.Comp.Vision) < profile.FunctionalVisionThreshold)
        {
            args.Cancel();
        }
    }

    /// <summary>
    /// Checks the hand selected by the player. A physically detached hand is already removed by the body hand-organ
    /// system; this check covers a present but medically nonfunctional hand without duplicating that behavior.
    /// </summary>
    public bool CanUseActiveHand(EntityUid entity, NCBodyFunctionComponent? component = null)
    {
        if (!Resolve(entity, ref component, false) ||
            !_prototypes.TryIndex(component.Profile, out var profile) ||
            !TryComp<HandsComponent>(entity, out var hands) ||
            _hands.GetActiveHand((entity, hands)) is not { } active ||
            !_hands.TryGetHand((entity, hands), active, out var hand))
        {
            return false;
        }

        var grip = hand.Value.Location switch
        {
            HandLocation.Left => component.LeftGrip,
            HandLocation.Right => component.RightGrip,
            _ => MathF.Max(component.LeftGrip, component.RightGrip),
        };
        return ClampUnit(grip) >= profile.FunctionalGripThreshold;
    }

    /// <summary>
    /// Installs a server-derived aggregate through the component's owning shared system.
    /// </summary>
    public bool SetAggregate(Entity<NCBodyFunctionComponent?> entity, in NCBodyFunctionAggregate aggregate)
    {
        if (!Resolve(entity, ref entity.Comp, false))
            return false;

        var next = Sanitize(aggregate);
        var previous = GetAggregate((entity.Owner, entity.Comp));
        if (Close(previous, next))
            return false;

        entity.Comp.Movement = next.Movement;
        entity.Comp.LeftGrip = next.LeftGrip;
        entity.Comp.RightGrip = next.RightGrip;
        entity.Comp.Vision = next.Vision;
        entity.Comp.Hearing = next.Hearing;
        entity.Comp.Speech = next.Speech;
        unchecked
        {
            entity.Comp.Revision++;
        }

        Dirty(entity);
        _movement.RefreshMovementSpeedModifiers(entity.Owner);
        var changed = new NCBodyFunctionChangedEvent(previous, next);
        RaiseLocalEvent(entity.Owner, ref changed);
        return true;
    }

    public NCBodyFunctionAggregate GetAggregate(Entity<NCBodyFunctionComponent?> entity)
    {
        if (!Resolve(entity, ref entity.Comp, false))
            return default;

        return Sanitize(new NCBodyFunctionAggregate(
            entity.Comp.Movement,
            entity.Comp.LeftGrip,
            entity.Comp.RightGrip,
            entity.Comp.Vision,
            entity.Comp.Hearing,
            entity.Comp.Speech));
    }

    private static NCBodyFunctionAggregate Sanitize(in NCBodyFunctionAggregate value)
    {
        return new NCBodyFunctionAggregate(
            ClampUnit(value.Movement),
            ClampUnit(value.LeftGrip),
            ClampUnit(value.RightGrip),
            ClampUnit(value.Vision),
            ClampUnit(value.Hearing),
            ClampUnit(value.Speech));
    }

    private static bool Close(in NCBodyFunctionAggregate left, in NCBodyFunctionAggregate right)
    {
        return MathHelper.CloseTo(left.Movement, right.Movement) &&
               MathHelper.CloseTo(left.LeftGrip, right.LeftGrip) &&
               MathHelper.CloseTo(left.RightGrip, right.RightGrip) &&
               MathHelper.CloseTo(left.Vision, right.Vision) &&
               MathHelper.CloseTo(left.Hearing, right.Hearing) &&
               MathHelper.CloseTo(left.Speech, right.Speech);
    }

    private static float ClampUnit(float value)
    {
        return float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
    }
}
