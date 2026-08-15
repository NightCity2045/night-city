// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.FixedPoint;

namespace Content.Shared._NC.Combat.Armor;

/// <summary>
/// Side-effect-free armor arithmetic shared by server resolution and deterministic tests.
/// </summary>
public static class NCArmorRules
{
    /// <summary>
    /// Resolves already filtered sections. SP is always the maximum and never the sum.
    /// </summary>
    public static NCArmorResolution Resolve(
        in NCArmorResolveRequest request,
        IReadOnlyList<NCArmorSectionSnapshot> collectedSections)
    {
        var rawDamage = FixedPoint2.Max(FixedPoint2.Zero, request.RawDamage);
        var unique = new HashSet<NCArmorSectionHandle>();
        var participating = new List<NCArmorSectionSnapshot>(collectedSections.Count);
        var effectiveSp = FixedPoint2.Zero;
        NCArmorSectionHandle? effectiveSection = null;

        foreach (var section in collectedSections)
        {
            if (!unique.Add(section.Handle))
                continue;

            var maxSp = FixedPoint2.Max(FixedPoint2.Zero, section.MaxSp);
            var currentSp = FixedPoint2.Clamp(section.CurrentSp, FixedPoint2.Zero, maxSp);
            if (currentSp <= FixedPoint2.Zero)
                continue;

            var normalized = section with
            {
                MaxSp = maxSp,
                CurrentSp = currentSp,
            };
            participating.Add(normalized);

            if (effectiveSection != null && currentSp <= effectiveSp)
                continue;

            effectiveSp = currentSp;
            effectiveSection = section.Handle;
        }

        var hasArmor = participating.Count > 0;
        var stopped = hasArmor && rawDamage <= effectiveSp;
        var penetrated = hasArmor && rawDamage > effectiveSp;
        var postArmorDamage = stopped
            ? FixedPoint2.Zero
            : FixedPoint2.Max(FixedPoint2.Zero, rawDamage - effectiveSp);
        var ablation = penetrated
            ? Math.Max(0, request.AblationAmount)
            : 0;

        var resolvedSections = new NCArmorSectionResolution[participating.Count];
        for (var i = 0; i < participating.Count; i++)
        {
            var section = participating[i];
            var currentAfter = ablation == 0
                ? section.CurrentSp
                : FixedPoint2.Max(FixedPoint2.Zero, section.CurrentSp - ablation);

            resolvedSections[i] = new NCArmorSectionResolution(
                section.Handle,
                section.MaxSp,
                section.CurrentSp,
                currentAfter,
                section.Rigid,
                section.Sealed,
                section.Handle == effectiveSection);
        }

        return new NCArmorResolution(
            request.Target,
            request.ActualZone,
            request.DamageClass,
            rawDamage,
            effectiveSp,
            postArmorDamage,
            stopped,
            penetrated,
            ablation,
            effectiveSection,
            resolvedSections);
    }

    /// <summary>
    /// Resolves a condition stage from YAML thresholds without relying on enum ordering.
    /// </summary>
    public static NCArmorConditionStage GetConditionStage(
        FixedPoint2 currentSp,
        FixedPoint2 maxSp,
        IReadOnlyList<NCArmorConditionStageDefinition> stages)
    {
        if (maxSp <= FixedPoint2.Zero || currentSp <= FixedPoint2.Zero)
            return NCArmorConditionStage.Destroyed;

        var fraction = (float) (FixedPoint2.Clamp(currentSp, FixedPoint2.Zero, maxSp) / maxSp);
        var selected = NCArmorConditionStage.Destroyed;
        var selectedThreshold = float.NegativeInfinity;

        foreach (var definition in stages)
        {
            if (!float.IsFinite(definition.MinimumFraction) ||
                definition.MinimumFraction < 0f ||
                definition.MinimumFraction > 1f ||
                fraction < definition.MinimumFraction ||
                definition.MinimumFraction <= selectedThreshold)
            {
                continue;
            }

            selected = definition.Stage;
            selectedThreshold = definition.MinimumFraction;
        }

        return selected;
    }
}
