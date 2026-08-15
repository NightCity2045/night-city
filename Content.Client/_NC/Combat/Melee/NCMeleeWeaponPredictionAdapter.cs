// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared.Damage;

namespace Content.Client.Weapons.Melee;

public sealed partial class MeleeWeaponSystem
{
    /// <summary>
    /// Prediction keeps immediate hit feedback and stamina behavior, but never predicts legacy global HP on NC humans.
    /// Armor, wounds and physiology remain server authoritative and reconcile through their own network state.
    /// </summary>
    protected override bool NCTryApplyAuthoritativeMedicalImpact(
        EntityUid user,
        EntityUid weapon,
        EntityUid target,
        DamageSpecifier damage,
        Vector2? direction,
        out DamageSpecifier feedbackDamage)
    {
        if (!HasComp<NCMedicalBodyComponent>(target))
        {
            feedbackDamage = new DamageSpecifier();
            return false;
        }

        feedbackDamage = new DamageSpecifier(damage);
        return true;
    }
}
