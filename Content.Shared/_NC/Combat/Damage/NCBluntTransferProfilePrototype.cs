// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Armor;
using Content.Shared._NC.Medical.Trauma;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Combat.Damage;

/// <summary>
/// Discrete deterministic trauma transferred through armor that stopped the ordinary impact.
/// Rules are prototype thresholds rather than a hidden energy formula or a random check.
/// </summary>
[Prototype("ncBluntTransferProfile")]
public sealed partial class NCBluntTransferProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public ProtoId<NCTraumaProfilePrototype> TraumaProfile { get; private set; }

    [DataField(required: true)]
    public List<NCBluntTransferRule> Rules { get; private set; } = [];
}

[DataDefinition]
public sealed partial class NCBluntTransferRule
{
    [DataField(required: true)]
    public FixedPoint2 MinimumRawDamage { get; private set; }

    [DataField(required: true)]
    public FixedPoint2 AppliedTrauma { get; private set; }

    [DataField]
    public NCBluntTransferArmorKind ArmorKind { get; private set; }
}

public enum NCBluntTransferArmorKind : byte
{
    Any,
    Soft,
    Rigid,
}

/// <summary>
/// Side-effect-free selection of the highest matching stopped-hit threshold.
/// </summary>
public static class NCBluntTransferRules
{
    public static FixedPoint2 Resolve(
        NCBluntTransferProfilePrototype profile,
        NCArmorResolution armor)
    {
        if (!armor.Stopped || armor.EffectiveSection is not { } effectiveSection)
            return FixedPoint2.Zero;

        var rigid = false;
        var foundEffective = false;
        foreach (var section in armor.ParticipatingSections)
        {
            if (section.Handle != effectiveSection)
                continue;

            rigid = section.Rigid;
            foundEffective = true;
            break;
        }

        if (!foundEffective)
            return FixedPoint2.Zero;

        var applied = FixedPoint2.Zero;
        FixedPoint2? selectedThreshold = null;
        foreach (var rule in profile.Rules)
        {
            if (rule.MinimumRawDamage < FixedPoint2.Zero ||
                rule.AppliedTrauma <= FixedPoint2.Zero ||
                armor.RawDamage < rule.MinimumRawDamage ||
                selectedThreshold is { } currentThreshold && rule.MinimumRawDamage <= currentThreshold ||
                !MatchesArmorKind(rule.ArmorKind, rigid))
            {
                continue;
            }

            selectedThreshold = rule.MinimumRawDamage;
            applied = rule.AppliedTrauma;
        }

        return applied;
    }

    private static bool MatchesArmorKind(NCBluntTransferArmorKind kind, bool rigid)
    {
        return kind switch
        {
            NCBluntTransferArmorKind.Any => true,
            NCBluntTransferArmorKind.Soft => !rigid,
            NCBluntTransferArmorKind.Rigid => rigid,
            _ => false,
        };
    }
}
