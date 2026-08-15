// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.Combat.Armor;
using Content.Shared.Charges.Components;
using Content.Shared.Charges.Systems;
using Content.Shared.DoAfter;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Combat.Armor;

/// <summary>
/// Drives bounded, consumable field repair of a single armor entity.
/// Every completion revalidates reach, held tool, charges, compatibility and the live section state.
/// </summary>
public sealed partial class NCArmorRepairSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedChargesSystem _charges = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedPopupSystem _popup = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCArmorRepairToolComponent, AfterInteractEvent>(OnAfterInteract);
        SubscribeLocalEvent<NCArmorRepairToolComponent, NCArmorRepairDoAfterEvent>(OnDoAfter);
    }

    private void OnAfterInteract(Entity<NCArmorRepairToolComponent> tool, ref AfterInteractEvent args)
    {
        if (args.Handled || args.Target is not { } armor || !HasComp<NCArmorComponent>(armor))
            return;

        args.Handled = true;
        var preview = PreviewRepair(tool, args.User, armor);
        if (preview.Outcome != NCArmorRepairOutcome.Applied)
        {
            ShowFailure(preview.Outcome, armor, args.User);
            return;
        }

        var doAfter = new DoAfterArgs(
            EntityManager,
            args.User,
            tool.Comp.RepairDelay,
            new NCArmorRepairDoAfterEvent(),
            tool.Owner,
            target: armor,
            used: tool.Owner)
        {
            NeedHand = true,
            BreakOnMove = true,
            BreakOnDamage = true,
        };

        if (_doAfter.TryStartDoAfter(doAfter))
            _popup.PopupEntity(Loc.GetString("nc-armor-repair-started"), armor, args.User);
    }

    private void OnDoAfter(Entity<NCArmorRepairToolComponent> tool, ref NCArmorRepairDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || args.Target is not { } armor || args.Used != tool.Owner)
            return;

        args.Handled = true;
        var result = TryRepairNow(tool, args.User, armor);
        if (result.Outcome != NCArmorRepairOutcome.Applied)
        {
            ShowFailure(result.Outcome, armor, args.User);
            return;
        }

        _popup.PopupEntity(
            Loc.GetString(
                "nc-armor-repair-complete",
                ("amount", (result.CurrentSp - result.PreviousSp).Float())),
            armor,
            args.User);
    }

    /// <summary>
    /// Applies the same authoritative completion path used by the DoAfter. This is public for workshops and tests.
    /// </summary>
    public NCArmorRepairResult TryRepairNow(
        Entity<NCArmorRepairToolComponent> tool,
        EntityUid user,
        EntityUid armor)
    {
        var candidate = PreviewRepair(tool, user, armor);
        if (candidate.Outcome != NCArmorRepairOutcome.Applied)
            return candidate;

        if (candidate.Section is not { } sectionId ||
            !TryComp<NCArmorComponent>(armor, out var armorComponent) ||
            !TryFindState(armorComponent, sectionId, out var state) ||
            !_prototypes.TryIndex(sectionId, out var section) ||
            section.RepairProfile is not { } repairProfileId ||
            !_prototypes.TryIndex(repairProfileId, out var repairProfile))
        {
            return Empty(NCArmorRepairOutcome.InvalidTarget);
        }

        // Recompute the bounded delta before consuming the resource. No failed or already-complete repair can spend a charge.
        var maximum = FixedPoint2.Clamp(
            state.MaxSp * Math.Clamp(repairProfile.MaximumRepairFraction, 0f, 1f),
            FixedPoint2.Zero,
            state.MaxSp);
        var previous = FixedPoint2.Clamp(state.CurrentSp, FixedPoint2.Zero, state.MaxSp);
        var current = FixedPoint2.Min(maximum, previous + FixedPoint2.Max(FixedPoint2.Zero, repairProfile.RepairPerUse));
        if (current <= previous)
            return candidate with { Outcome = NCArmorRepairOutcome.AtRepairLimit };

        if (!TryComp<LimitedChargesComponent>(tool.Owner, out var charges) ||
            !_charges.TryUseCharges((tool.Owner, charges), tool.Comp.ChargeCost))
        {
            return candidate with { Outcome = NCArmorRepairOutcome.Depleted };
        }

        state.CurrentSp = current;
        unchecked
        {
            armorComponent.Revision++;
        }

        RaiseLocalEvent(armor, new NCArmorRepairedEvent(sectionId, previous, current));
        return new NCArmorRepairResult(
            NCArmorRepairOutcome.Applied,
            sectionId,
            previous,
            current,
            tool.Comp.ChargeCost);
    }

    /// <summary>
    /// Performs a bounded read over this item's profile sections and chooses the most damaged compatible section.
    /// </summary>
    public NCArmorRepairResult PreviewRepair(
        Entity<NCArmorRepairToolComponent> tool,
        EntityUid user,
        EntityUid armor)
    {
        if (Deleted(user) ||
            Deleted(armor) ||
            tool.Comp.RepairDelay <= TimeSpan.Zero ||
            !float.IsFinite(tool.Comp.InteractionRange) ||
            tool.Comp.InteractionRange <= 0f ||
            tool.Comp.ChargeCost <= 0 ||
            tool.Comp.Capabilities.Count == 0 ||
            !_hands.IsHolding(user, tool.Owner) ||
            !_interaction.InRangeAndAccessible(user, armor, tool.Comp.InteractionRange) ||
            !TryComp<NCArmorComponent>(armor, out var armorComponent) ||
            !_prototypes.TryIndex(armorComponent.Profile, out var armorProfile))
        {
            return Empty(NCArmorRepairOutcome.InvalidTarget);
        }

        if (!TryComp<LimitedChargesComponent>(tool.Owner, out var charges) ||
            !_charges.HasCharges((tool.Owner, charges), tool.Comp.ChargeCost))
        {
            return Empty(NCArmorRepairOutcome.Depleted);
        }

        var compatible = false;
        NCArmorSectionState? selectedState = null;
        ProtoId<NCArmorSectionPrototype>? selectedSection = null;
        var selectedMissing = FixedPoint2.Zero;

        foreach (var sectionId in armorProfile.Sections)
        {
            if (!_prototypes.TryIndex(sectionId, out var section) ||
                section.RepairProfile is not { } repairProfileId ||
                !_prototypes.TryIndex(repairProfileId, out var repairProfile) ||
                !CapabilitiesMatch(tool.Comp.Capabilities, repairProfile.AcceptedCapabilities) ||
                !TryFindState(armorComponent, sectionId, out var state))
            {
                continue;
            }

            compatible = true;
            var current = FixedPoint2.Clamp(state.CurrentSp, FixedPoint2.Zero, state.MaxSp);
            if (current <= FixedPoint2.Zero && !repairProfile.CanRepairDestroyed)
                continue;

            var maximum = FixedPoint2.Clamp(
                state.MaxSp * Math.Clamp(repairProfile.MaximumRepairFraction, 0f, 1f),
                FixedPoint2.Zero,
                state.MaxSp);
            var missing = maximum - current;
            if (missing <= FixedPoint2.Zero || (selectedState != null && missing <= selectedMissing))
                continue;

            selectedState = state;
            selectedSection = sectionId;
            selectedMissing = missing;
        }

        if (selectedState == null || selectedSection == null)
            return Empty(compatible ? NCArmorRepairOutcome.AtRepairLimit : NCArmorRepairOutcome.NoCompatibleSection);

        return new NCArmorRepairResult(
            NCArmorRepairOutcome.Applied,
            selectedSection,
            selectedState.CurrentSp,
            selectedState.CurrentSp,
            0);
    }

    private static bool CapabilitiesMatch(IReadOnlyList<string> tool, IReadOnlyList<string> accepted)
    {
        foreach (var capability in tool)
        {
            if (accepted.Contains(capability, StringComparer.Ordinal))
                return true;
        }

        return false;
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

    private void ShowFailure(NCArmorRepairOutcome outcome, EntityUid armor, EntityUid user)
    {
        var message = outcome switch
        {
            NCArmorRepairOutcome.Depleted => "nc-armor-repair-depleted",
            NCArmorRepairOutcome.AtRepairLimit => "nc-armor-repair-limit",
            NCArmorRepairOutcome.NoCompatibleSection => "nc-armor-repair-incompatible",
            _ => "nc-armor-repair-invalid",
        };
        _popup.PopupEntity(Loc.GetString(message), armor, user);
    }

    private static NCArmorRepairResult Empty(NCArmorRepairOutcome outcome)
    {
        return new NCArmorRepairResult(outcome, null, FixedPoint2.Zero, FixedPoint2.Zero, 0);
    }
}
