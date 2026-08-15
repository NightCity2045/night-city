// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Damage;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Combat.Projectiles;

/// <summary>
/// Maps the positive damage carried by a physical projectile to an authoritative NC impact profile.
/// Exact ammunition overrides remain components on their projectile prototypes.
/// </summary>
[Prototype("ncProjectileDamageProfileMap")]
public sealed partial class NCProjectileDamageProfileMapPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>
    /// Exactly one map supplies the default fallback for projectiles that hit human medical bodies.
    /// </summary>
    [DataField]
    public bool DefaultForHumans { get; private set; }

    [DataField(required: true)]
    public Dictionary<ProtoId<DamageTypePrototype>, ProtoId<NCCombatDamageProfilePrototype>> Profiles
    {
        get;
        private set;
    } = new();
}

/// <summary>
/// Pure deterministic fallback selection shared by the server adapter and semantic tests.
/// </summary>
public static class NCProjectileDamageProfileRules
{
    /// <summary>
    /// Selects the largest positive mapped entry. Equal magnitudes use ordinal damage-type order so YAML or
    /// dictionary insertion order can never change the resulting medical mechanism. Zero-damage utility payloads
    /// intentionally return false and stay entirely in the vanilla collision-effects path.
    /// </summary>
    public static bool TryResolve(
        NCProjectileDamageProfileMapPrototype map,
        DamageSpecifier damage,
        out ProtoId<NCCombatDamageProfilePrototype> profile)
    {
        var bestAmount = FixedPoint2.Zero;
        ProtoId<DamageTypePrototype>? bestType = null;
        ProtoId<NCCombatDamageProfilePrototype> bestProfile = default;

        foreach (var (damageType, amount) in damage.DamageDict)
        {
            if (amount <= FixedPoint2.Zero ||
                !map.Profiles.TryGetValue(damageType, out var mappedProfile))
            {
                continue;
            }

            if (amount < bestAmount ||
                amount == bestAmount && bestType is { } current &&
                StringComparer.Ordinal.Compare(damageType.Id, current.Id) >= 0)
            {
                continue;
            }

            bestAmount = amount;
            bestType = damageType;
            bestProfile = mappedProfile;
        }

        profile = bestProfile;
        return bestType != null;
    }
}
