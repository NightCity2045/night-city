// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Damage;
using Content.Shared._NC.Medical.Systemic;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Combat.Environment;

/// <summary>
/// Maps legacy damage types to the authoritative NC trauma profile used by the compatibility boundary.
/// The map contains content policy only; it does not decide whether a physical source hit its target.
/// </summary>
[Prototype("ncEnvironmentDamageProfileMap")]
public sealed partial class NCEnvironmentDamageProfileMapPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>
    /// Exactly one default is required for authoritative human bodies.
    /// </summary>
    [DataField]
    public bool DefaultForHumans { get; private set; }

    [DataField(required: true)]
    public Dictionary<ProtoId<DamageTypePrototype>, ProtoId<NCCombatDamageProfilePrototype>> Profiles
    {
        get;
        private set;
    } = new();

    /// <summary>
    /// Whole-body effects may coexist with a local trauma mapping. Electrical damage deliberately uses both.
    /// </summary>
    [DataField(required: true)]
    public Dictionary<ProtoId<DamageTypePrototype>, ProtoId<NCSystemicDamageProfilePrototype>> SystemicProfiles
    {
        get;
        private set;
    } = new();

    /// <summary>
    /// Negative physical damage types that are deliberately rejected in favor of authoritative treatment,
    /// surgery or trusted rejuvenation. Explicit classification prevents a silent compatibility no-op.
    /// </summary>
    [DataField(required: true)]
    public HashSet<ProtoId<DamageTypePrototype>> RejectedLocalHealing
    {
        get;
        private set;
    } = [];
}

/// <summary>
/// Pure deterministic selection shared by runtime code and semantic tests.
/// </summary>
public static class NCEnvironmentDamageProfileRules
{
    /// <summary>
    /// Selects the largest positive mapped entry. Equal magnitudes use ordinal damage-type order so dictionary
    /// insertion order can never alter the resulting medical mechanism.
    /// </summary>
    public static bool TryResolve(
        NCEnvironmentDamageProfileMapPrototype map,
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

    public static bool TryResolveSystemic(
        NCEnvironmentDamageProfileMapPrototype map,
        ProtoId<DamageTypePrototype> damageType,
        out ProtoId<NCSystemicDamageProfilePrototype> profile)
    {
        return map.SystemicProfiles.TryGetValue(damageType, out profile);
    }

    public static bool RejectsLocalHealing(
        NCEnvironmentDamageProfileMapPrototype map,
        ProtoId<DamageTypePrototype> damageType)
    {
        return map.RejectedLocalHealing.Contains(damageType);
    }
}
