// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Armor;
using Content.Shared._NC.Medical.Trauma;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Combat.Damage;

/// <summary>
/// Discrete bridge from an existing SS14 damage payload to zonal armor and authoritative trauma.
/// </summary>
[Prototype("ncCombatDamageProfile")]
public sealed partial class NCCombatDamageProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public ProtoId<NCTraumaProfilePrototype> TraumaProfile { get; private set; }

    [DataField(required: true)]
    public NCArmorDamageClass ArmorDamageClass { get; private set; }

    [DataField]
    public int AblationAmount { get; private set; } = 1;

    /// <summary>
    /// Optional deterministic trauma transferred through armor that fully stopped this impact.
    /// </summary>
    [DataField]
    public ProtoId<NCBluntTransferProfilePrototype>? BluntTransferProfile { get; private set; }

    [DataField]
    public NCTraumaFlags TraumaFlags { get; private set; } = NCTraumaFlags.Penetrating;

    [DataField]
    public bool CanOverpenetrateBody { get; private set; }
}

/// <summary>
/// Assigns a combat profile to a physical projectile prototype.
/// </summary>
[RegisterComponent]
public sealed partial class NCProjectileDamageProfileComponent : Component
{
    [DataField(required: true)]
    public ProtoId<NCCombatDamageProfilePrototype> Profile;
}
