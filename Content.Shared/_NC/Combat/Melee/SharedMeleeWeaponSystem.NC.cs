// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared.Damage;

namespace Content.Shared.Weapons.Melee;

public abstract partial class SharedMeleeWeaponSystem
{
    /// <summary>
    /// Authoritative medical bodies remain valid physical melee targets even if compatibility HP is later removed.
    /// </summary>
    protected bool NCIsAuthoritativeMedicalTarget(EntityUid target)
    {
        return HasComp<NCMedicalBodyComponent>(target);
    }

    /// <summary>
    /// Gives the server adapter one accepted attack boundary in which to snapshot private targeting intent.
    /// </summary>
    protected virtual void NCBeginAuthoritativeMedicalAttack(
        EntityUid user,
        EntityUid weapon,
        EntityUid? intendedTarget)
    {
    }

    /// <summary>
    /// Replaces only the legacy Damageable mutation after vanilla melee has established a real contact.
    /// </summary>
    protected virtual bool NCTryApplyAuthoritativeMedicalImpact(
        EntityUid user,
        EntityUid weapon,
        EntityUid target,
        DamageSpecifier damage,
        Vector2? direction,
        out DamageSpecifier feedbackDamage)
    {
        feedbackDamage = new DamageSpecifier();
        return false;
    }

    /// <summary>
    /// Closes the synchronous attack context so no targeting state can leak into a later swing.
    /// </summary>
    protected virtual void NCEndAuthoritativeMedicalAttack(EntityUid user, EntityUid weapon)
    {
    }
}
