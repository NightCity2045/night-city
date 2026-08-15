// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Damage;
using Content.Shared.Damage.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Combat.Melee;

/// <summary>
/// Data-driven mapping from an existing melee payload's dominant damage type to one NC impact profile.
/// The default map is selected by data, so adapters never hardcode damage-type semantics or balance values.
/// </summary>
[Prototype("ncMeleeDamageProfileMap")]
public sealed partial class NCMeleeDamageProfileMapPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public bool DefaultForHumans { get; private set; }

    [DataField(required: true)]
    public Dictionary<ProtoId<DamageTypePrototype>, ProtoId<NCCombatDamageProfilePrototype>> Profiles { get; private set; } = [];
}

/// <summary>
/// Optional per-weapon override for mono-weapons, cyberlimbs, powered tools and other special melee sources.
/// </summary>
[RegisterComponent]
public sealed partial class NCMeleeDamageProfileComponent : Component
{
    [DataField(required: true)]
    public ProtoId<NCCombatDamageProfilePrototype> Profile;

    /// <summary>
    /// Surface contamination delivered with a wound, normalized by the authoritative delivery system.
    /// </summary>
    [DataField]
    public float Contamination;
}
