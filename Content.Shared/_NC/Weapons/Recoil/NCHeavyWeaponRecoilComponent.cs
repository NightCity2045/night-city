// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Damage;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared.Damage;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Weapons.Recoil;

/// <summary>
/// Data-only configuration for weapons whose recoil is physically dangerous to the shooter.
/// The authoritative trauma and impulse are applied by the server system after a confirmed shot.
/// </summary>
[RegisterComponent]
public sealed partial class NCHeavyWeaponRecoilComponent : Component
{
    /// <summary>
    /// Extra backwards impulse added after the ordinary gun recoil.
    /// </summary>
    [DataField]
    public float AdditionalImpulse = 75f;

    /// <summary>
    /// Trauma applied to each supporting hand while the weapon is wielded.
    /// </summary>
    [DataField]
    public DamageSpecifier WieldedHandDamage = new();

    /// <summary>
    /// Trauma applied to the holding hand when the weapon is fired without a two-handed grip.
    /// </summary>
    [DataField]
    public DamageSpecifier SingleHandDamage = new();

    /// <summary>
    /// Combat profile used to route recoil through the NC armor and wound pipeline.
    /// </summary>
    [DataField]
    public ProtoId<NCCombatDamageProfilePrototype> DamageProfile = "NCProjectileBluntStandard";

    [DataField]
    public ProtoId<NCTargetZonePrototype> LeftHandZone = "NCZoneHandLeft";

    [DataField]
    public ProtoId<NCTargetZonePrototype> RightHandZone = "NCZoneHandRight";
}
