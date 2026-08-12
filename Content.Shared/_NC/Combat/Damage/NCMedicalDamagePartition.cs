// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.Medical.Systemic;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Combat.Damage;

/// <summary>
/// Semantic split of one legacy payload. A type may intentionally appear in both local and systemic output
/// (electrical trauma), but an unrelated systemic scalar can never inflate the local wound magnitude.
/// </summary>
public readonly record struct NCMedicalDamagePartition(
    DamageSpecifier LocalTrauma,
    IReadOnlyList<NCSystemicDamageDelta> Systemic,
    IReadOnlyList<NCRejectedHealingDelta> RejectedHealing,
    IReadOnlyList<NCUnmappedDamageDelta> Unmapped);

public readonly record struct NCSystemicDamageDelta(
    ProtoId<DamageTypePrototype> DamageType,
    ProtoId<NCSystemicDamageProfilePrototype> Profile,
    FixedPoint2 Amount);

public readonly record struct NCRejectedHealingDelta(
    ProtoId<DamageTypePrototype> DamageType,
    FixedPoint2 Amount);

public readonly record struct NCUnmappedDamageDelta(
    ProtoId<DamageTypePrototype> DamageType,
    FixedPoint2 Amount);

/// <summary>
/// Pure data-driven partition helper shared by environment and richer source adapters.
/// </summary>
public static class NCMedicalDamagePartitionRules
{
    public static NCMedicalDamagePartition Partition(
        DamageSpecifier damage,
        IReadOnlyDictionary<ProtoId<DamageTypePrototype>, ProtoId<NCCombatDamageProfilePrototype>> traumaProfiles,
        IReadOnlyDictionary<ProtoId<DamageTypePrototype>, ProtoId<NCSystemicDamageProfilePrototype>> systemicProfiles,
        IReadOnlySet<ProtoId<DamageTypePrototype>> rejectedLocalHealing)
    {
        var local = new DamageSpecifier();
        var systemic = new List<NCSystemicDamageDelta>();
        var rejectedHealing = new List<NCRejectedHealingDelta>();
        var unmapped = new List<NCUnmappedDamageDelta>();

        foreach (var (damageType, amount) in damage.DamageDict
                     .OrderBy(entry => entry.Key.Id, StringComparer.Ordinal))
        {
            if (amount == FixedPoint2.Zero)
                continue;

            var handled = false;
            if (amount > FixedPoint2.Zero && traumaProfiles.ContainsKey(damageType))
            {
                local.DamageDict[damageType] = amount;
                handled = true;
            }

            if (systemicProfiles.TryGetValue(damageType, out var systemicProfile))
            {
                systemic.Add(new NCSystemicDamageDelta(damageType, systemicProfile, amount));
                handled = true;
            }

            if (amount < FixedPoint2.Zero &&
                rejectedLocalHealing.Contains(damageType))
            {
                rejectedHealing.Add(new NCRejectedHealingDelta(damageType, -amount));
                handled = true;
            }

            if (!handled)
                unmapped.Add(new NCUnmappedDamageDelta(damageType, amount));
        }

        return new NCMedicalDamagePartition(local, systemic, rejectedHealing, unmapped);
    }
}
