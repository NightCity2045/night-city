// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Armor;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared.Examine;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Combat.Armor;

/// <summary>
/// Shows exact residual SP only when a player examines the concrete armor item at details range.
/// Runtime section state remains server-authoritative and is never component-state broadcast.
/// </summary>
public sealed partial class NCArmorExamineSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCArmorComponent, ExaminedEvent>(OnExamined);
    }

    private void OnExamined(Entity<NCArmorComponent> armor, ref ExaminedEvent args)
    {
        // Installed/internal armor is private medical state; only concrete equipment items expose exact SP this way.
        if (!args.IsInDetailsRange ||
            HasComp<NCMedicalBodyComponent>(armor.Owner) ||
            !_prototypes.TryIndex(armor.Comp.Profile, out var profile))
            return;

        using var _ = args.PushGroup(nameof(NCArmorComponent));
        foreach (var sectionId in profile.Sections)
        {
            if (!_prototypes.TryIndex(sectionId, out var section) ||
                !_prototypes.TryIndex(section.ConditionProfile, out var conditionProfile) ||
                !TryFindState(armor.Comp, sectionId, out var state))
            {
                continue;
            }

            var maximum = FixedPoint2.Max(FixedPoint2.Zero, state.MaxSp);
            var current = FixedPoint2.Clamp(state.CurrentSp, FixedPoint2.Zero, maximum);
            var condition = NCArmorRules.GetConditionStage(current, maximum, conditionProfile.Stages);
            args.PushMarkup(Loc.GetString(
                "nc-armor-examine-section",
                ("section", Loc.GetString(section.DisplayName)),
                ("condition", Loc.GetString(ConditionLocId(condition))),
                ("current", current.Float()),
                ("maximum", maximum.Float())));
        }
    }

    private static LocId ConditionLocId(NCArmorConditionStage stage)
    {
        return stage switch
        {
            NCArmorConditionStage.Intact => "nc-armor-condition-intact",
            NCArmorConditionStage.Worn => "nc-armor-condition-worn",
            NCArmorConditionStage.Damaged => "nc-armor-condition-damaged",
            NCArmorConditionStage.Critical => "nc-armor-condition-critical",
            _ => "nc-armor-condition-destroyed",
        };
    }

    private static bool TryFindState(
        NCArmorComponent armor,
        ProtoId<NCArmorSectionPrototype> sectionId,
        out NCArmorSectionState state)
    {
        foreach (var candidate in armor.Sections)
        {
            if (candidate.Section != sectionId)
                continue;

            state = candidate;
            return true;
        }

        state = default!;
        return false;
    }
}
