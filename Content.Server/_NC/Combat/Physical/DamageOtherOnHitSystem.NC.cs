// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Combat.Physical;
using Content.Shared._NC.Combat.Physical;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Throwing;
using Robust.Shared.Physics.Components;

namespace Content.Server.Damage.Systems;

public sealed partial class DamageOtherOnHitSystem
{
    [Dependency] private NCPhysicalImpactAdapterSystem _ncPhysicalImpacts = default!;

    private bool TryApplyNcThrownImpact(
        EntityUid thrown,
        DamageOtherOnHitComponent component,
        ThrowDoHitEvent args,
        out DamageSpecifier feedback)
    {
        feedback = new DamageSpecifier();
        var direction = TryComp<PhysicsComponent>(thrown, out var physics)
            ? physics.LinearVelocity
            : default;
        var request = new NCPhysicalImpactRequestedEvent(
            NCPhysicalImpactSourceKind.ThrownObject,
            component.Damage * _damageable.UniversalThrownDamageModifier,
            args.Component.Thrower,
            thrown,
            direction);
        if (!_ncPhysicalImpacts.TryApply(args.Target, ref request))
            return false;

        feedback = request.FeedbackDamage ?? new DamageSpecifier();
        return true;
    }
}
