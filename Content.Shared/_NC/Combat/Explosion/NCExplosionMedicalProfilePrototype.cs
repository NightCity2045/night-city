// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Damage;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Explosion;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Combat.Explosion;

/// <summary>
/// Chooses whether one channel of a distributed explosion stays on its primary region or reaches another region.
/// </summary>
public enum NCExplosionZoneSelection : byte
{
    Primary,
    Neighbor,
}

/// <summary>
/// One data-driven channel of an explosion. A channel filters the already resistance-adjusted vanilla payload;
/// it never performs another hit or cover check.
/// </summary>
[DataDefinition]
public sealed partial class NCExplosionImpactDefinition
{
    [DataField(required: true)]
    public ProtoId<NCCombatDamageProfilePrototype> DamageProfile { get; private set; }

    [DataField(required: true)]
    public List<ProtoId<DamageTypePrototype>> SourceDamageTypes { get; private set; } = [];

    [DataField(required: true)]
    public float DamageMultiplier { get; private set; }

    [DataField]
    public int Count { get; private set; } = 1;

    [DataField]
    public NCExplosionZoneSelection ZoneSelection { get; private set; }
}

/// <summary>
/// Parallel NC interpretation of an upstream explosion prototype. Existing explosion propagation remains the
/// authority for cover, resistance, distance and whether the target was reached.
/// </summary>
[Prototype("ncExplosionMedicalProfile")]
public sealed partial class NCExplosionMedicalProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public bool DefaultForHumans { get; private set; }

    [DataField]
    public HashSet<ProtoId<ExplosionPrototype>> ExplosionPrototypes { get; private set; } = [];

    [DataField(required: true)]
    public NCExplosionImpactDefinition PrimaryImpact { get; private set; } = default!;

    [DataField]
    public List<NCExplosionImpactDefinition> SecondaryImpacts { get; private set; } = [];

    /// <summary>
    /// Caps repeated ablation of the same physical armor section by all channels of one explosion.
    /// </summary>
    [DataField(required: true)]
    public int MaxAblationApplicationsPerSection { get; private set; }
}

/// <summary>
/// Side-effect-free explosion payload arithmetic used by the server adapter and tests.
/// </summary>
public static class NCExplosionMedicalRules
{
    public static bool TryBuildDamage(
        NCExplosionImpactDefinition definition,
        DamageSpecifier source,
        out DamageSpecifier damage)
    {
        damage = new DamageSpecifier();
        if (!float.IsFinite(definition.DamageMultiplier) || definition.DamageMultiplier <= 0f)
            return false;

        var multiplier = FixedPoint2.New(definition.DamageMultiplier);
        foreach (var type in definition.SourceDamageTypes)
        {
            if (!source.DamageDict.TryGetValue(type, out var value) || value <= FixedPoint2.Zero)
                continue;

            damage.DamageDict[type] = value * multiplier;
        }

        return damage.AnyPositive();
    }
}
