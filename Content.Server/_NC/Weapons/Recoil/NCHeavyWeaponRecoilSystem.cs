// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Server._NC.Combat.Damage;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Weapons.Recoil;
using Content.Shared.Damage;
using Content.Shared.Hands;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Content.Shared.Wieldable.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Weapons.Recoil;

/// <summary>
/// Applies server-authoritative body impulse and hand trauma after a dangerous weapon actually fires.
/// </summary>
public sealed partial class NCHeavyWeaponRecoilSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private NCCombatTraumaDeliverySystem _trauma = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCHeavyWeaponRecoilComponent, GunShotEvent>(OnGunShot);
    }

    private void OnGunShot(Entity<NCHeavyWeaponRecoilComponent> weapon, ref GunShotEvent args)
    {
        if (!Exists(args.User) || !_hands.IsHolding(args.User, weapon.Owner, out var heldHandId))
            return;

        var shotDirection = ResolveShotDirection(weapon.Owner, args.User);
        if (TryComp<PhysicsComponent>(args.User, out var body) && weapon.Comp.AdditionalImpulse > 0f)
        {
            // The stock gun impulse remains intact; this is the exceptional force unique to NC heavy weapons.
            _physics.ApplyLinearImpulse(args.User, -shotDirection * weapon.Comp.AdditionalImpulse, body: body);
        }

        var wielded = TryComp<WieldableComponent>(weapon.Owner, out var wieldable) && wieldable.Wielded;
        var resolution = NCActionResolutionId.New();

        if (wielded)
        {
            // A proper two-handed grip shares the impact, but both arms still absorb the Burya's recoil.
            DeliverHandTrauma(weapon, args.User, weapon.Comp.LeftHandZone, weapon.Comp.WieldedHandDamage, shotDirection, resolution, 0);
            DeliverHandTrauma(weapon, args.User, weapon.Comp.RightHandZone, weapon.Comp.WieldedHandDamage, shotDirection, resolution, 1);
            return;
        }

        if (!_hands.TryGetHand(args.User, heldHandId, out var heldHand))
            return;

        var zone = heldHand.Value.Location == HandLocation.Left
            ? weapon.Comp.LeftHandZone
            : weapon.Comp.RightHandZone;
        DeliverHandTrauma(weapon, args.User, zone, weapon.Comp.SingleHandDamage, shotDirection, resolution, 0);
    }

    private Vector2 ResolveShotDirection(EntityUid weapon, EntityUid shooter)
    {
        if (TryComp<GunComponent>(weapon, out var gun) && gun.ShootCoordinates is { } target)
        {
            var from = _transform.GetMapCoordinates(shooter);
            var to = _transform.ToMapCoordinates(target);
            if (from.MapId == to.MapId)
            {
                var delta = to.Position - from.Position;
                if (!delta.IsLengthZero())
                    return delta.Normalized();
            }
        }

        return _transform.GetWorldRotation(weapon).RotateVec(Vector2.UnitX);
    }

    private void DeliverHandTrauma(
        Entity<NCHeavyWeaponRecoilComponent> weapon,
        EntityUid shooter,
        ProtoId<NCTargetZonePrototype> zone,
        DamageSpecifier configuredDamage,
        Vector2 shotDirection,
        NCActionResolutionId resolution,
        uint sequence)
    {
        if (configuredDamage.GetTotal() <= 0)
            return;

        // Recoil is a real localized impact. Sending it through the shared delivery path preserves armor,
        // wound severity, fractures, bleeding and persistence instead of creating a parallel HP shortcut.
        var impact = new NCResolvedCombatImpact(
            resolution,
            0,
            sequence,
            resolution.DeriveSeed(sequence),
            shooter,
            shooter,
            weapon.Owner,
            null,
            shooter,
            NCTargetingMode.Automatic,
            null,
            weapon.Comp.DamageProfile,
            new DamageSpecifier(configuredDamage),
            Vector2.Zero,
            -shotDirection,
            0f,
            _timing.CurTime)
        {
            ForcedZone = zone,
        };

        _trauma.TryDeliver(impact, out _);
    }
}
