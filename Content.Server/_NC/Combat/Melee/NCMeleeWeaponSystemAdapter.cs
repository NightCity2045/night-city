// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Server._NC.Combat.Melee;
using Content.Shared.Damage;

namespace Content.Server.Weapons.Melee;

public sealed partial class MeleeWeaponSystem
{
    [Dependency] private NCMeleeAttackAdapterSystem _ncMeleeAdapter = default!;

    protected override void NCBeginAuthoritativeMedicalAttack(
        EntityUid user,
        EntityUid weapon,
        EntityUid? intendedTarget)
    {
        _ncMeleeAdapter.BeginAttack(user, weapon, intendedTarget);
    }

    protected override bool NCTryApplyAuthoritativeMedicalImpact(
        EntityUid user,
        EntityUid weapon,
        EntityUid target,
        DamageSpecifier damage,
        Vector2? direction,
        out DamageSpecifier feedbackDamage)
    {
        return _ncMeleeAdapter.TryApplyImpact(user, weapon, target, damage, direction, out feedbackDamage);
    }

    protected override void NCEndAuthoritativeMedicalAttack(EntityUid user, EntityUid weapon)
    {
        _ncMeleeAdapter.EndAttack(user, weapon);
    }
}
