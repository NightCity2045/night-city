// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Movement.Systems;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Cyberware.Abilities;

/// <summary>Prediction consumes sanitized authoritative channels, never hidden organs or client-authored energy.</summary>
public sealed partial class NCCyberwareAbilityModifiersSystem : EntitySystem
{
    public event Action<EntityUid>? BodyStateApplied;
    public static readonly ProtoId<NCCyberwareAbilityPolicyPrototype> PolicyId = "NCCyberwareAbilityPolicy";
    [Dependency] private MovementSpeedModifierSystem _movement = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCyberwareAbilityBodyComponent, RefreshMovementSpeedModifiersEvent>(OnMovement);
        SubscribeLocalEvent<NCCyberwareAbilityBodyComponent, AfterAutoHandleStateEvent>(OnState);
    }

    private void OnState(Entity<NCCyberwareAbilityBodyComponent> body, ref AfterAutoHandleStateEvent args)
    {
        _movement.RefreshMovementSpeedModifiers(body.Owner);
        BodyStateApplied?.Invoke(body.Owner);
    }

    private void OnMovement(Entity<NCCyberwareAbilityBodyComponent> body, ref RefreshMovementSpeedModifiersEvent args)
    {
        if (body.Comp.MotorBonus <= 0f) return;
        args.ModifySpeed(1f + body.Comp.MotorBonus);
        args.LimitSpeed(body.Comp.MaximumWalkSpeed, body.Comp.MaximumSprintSpeed);
    }

    /// <summary>Only declared manual qualities qualify. Fuel-driven tools and every other DoAfter retain their timing.</summary>
    public TimeSpan GetManualToolDelay(EntityUid user, IEnumerable<string> qualities, float fuel, TimeSpan delay)
    {
        if (fuel != 0f || !TryComp<NCCyberwareAbilityBodyComponent>(user, out var body) || body.MotorBonus <= 0f ||
            !_prototypes.TryIndex(PolicyId, out var policy) || !NCCyberwareAbilityMath.ValidatePolicy(policy)) return delay;
        var any = false;
        foreach (var quality in qualities)
        {
            any = true;
            if (!policy.ManualMotorQualities.Contains(new ProtoId<Content.Shared.Tools.ToolQualityPrototype>(quality)))
                return delay;
        }
        return any ? delay * Math.Clamp(body.ManualDurationFactor, policy.MinimumManualDurationFactor, 1f) : delay;
    }
}
