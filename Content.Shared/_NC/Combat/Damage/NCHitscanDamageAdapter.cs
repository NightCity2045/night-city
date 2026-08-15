// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Damage;
using Content.Shared.Weapons.Hitscan.Events;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Combat.Damage;

/// <summary>
/// Assigns a deterministic NC damage profile to a raycast hitscan prototype.
/// </summary>
[RegisterComponent]
public sealed partial class NCHitscanDamageProfileComponent : Component
{
    [DataField(required: true)]
    public ProtoId<NCCombatDamageProfilePrototype> Profile;
}

/// <summary>
/// Shared prediction boundary raised only after the vanilla raycast established a real hit.
/// The server fills FeedbackDamage; clients suppress legacy HP from the NCMedicalBody marker alone.
/// </summary>
[ByRefEvent]
public record struct NCHitscanTraumaRequestedEvent(
    EntityUid Hitscan,
    HitscanRaycastFiredData Raycast,
    DamageSpecifier Damage,
    bool Handled = false,
    DamageSpecifier? FeedbackDamage = null);
