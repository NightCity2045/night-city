// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Armor;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Combat.Armor;

/// <summary>
/// Resolves max-SP armor and applies authoritative ablation after a physical hit.
/// </summary>
public sealed partial class NCArmorResolutionSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCArmorCoverageSystem _coverage = default!;

    /// <summary>
    /// Pure resolution for an adapter that already collected applicable sections.
    /// </summary>
    public NCArmorResolution ResolveProvidedSections(
        in NCArmorResolveRequest request,
        IReadOnlyList<NCArmorSectionSnapshot> sections)
    {
        return NCArmorRules.Resolve(request, sections);
    }

    /// <summary>
    /// Resolves explicitly trusted armor entities without mutating their SP.
    /// </summary>
    public NCArmorResolution ResolveExplicit(
        in NCArmorResolveRequest request,
        IReadOnlyList<EntityUid> candidateSources)
    {
        var sections = new List<NCArmorSectionSnapshot>();
        _coverage.CollectExplicit(request, candidateSources, sections);
        return NCArmorRules.Resolve(request, sections);
    }

    /// <summary>
    /// Resolves only armor entities whose runtime binding points at the request target.
    /// </summary>
    public NCArmorResolution ResolveBound(
        in NCArmorResolveRequest request,
        IReadOnlyList<EntityUid> candidateSources)
    {
        var sections = new List<NCArmorSectionSnapshot>();
        _coverage.CollectBound(request, candidateSources, sections);
        return NCArmorRules.Resolve(request, sections);
    }

    /// <summary>
    /// Resolves the target's maintained bound-source index without an inventory or world scan.
    /// </summary>
    public NCArmorResolution ResolveBound(in NCArmorResolveRequest request)
    {
        var sections = new List<NCArmorSectionSnapshot>();
        _coverage.CollectBound(request, sections);
        return NCArmorRules.Resolve(request, sections);
    }

    /// <summary>
    /// Convenience atomic pipeline for a physical-hit adapter.
    /// </summary>
    public NCArmorResolution ResolveAndApplyBound(
        in NCArmorResolveRequest request,
        IReadOnlyList<EntityUid> candidateSources)
    {
        var resolution = ResolveBound(request, candidateSources);
        ApplyAblation(resolution);
        return resolution;
    }

    /// <summary>
    /// Resolves and mutates the target's maintained bound-source index.
    /// </summary>
    public NCArmorResolution ResolveAndApplyBound(in NCArmorResolveRequest request)
    {
        var resolution = ResolveBound(request);
        ApplyAblation(resolution);
        return resolution;
    }

    /// <summary>
    /// Applies the resolution's ablation to every still-valid participating section and clamps SP at zero.
    /// </summary>
    public int ApplyAblation(
        NCArmorResolution resolution,
        IReadOnlySet<NCArmorSectionHandle>? excludedSections = null)
    {
        if (!resolution.Penetrated || resolution.AblationApplied <= 0)
            return 0;

        var changed = 0;
        foreach (var section in resolution.ParticipatingSections)
        {
            if (excludedSections?.Contains(section.Handle) == true)
                continue;

            if (!TryComp<NCArmorComponent>(section.Handle.Source, out var armor) ||
                !TryFindState(armor, section.Handle.Section, out var state) ||
                !_prototypes.TryIndex(section.Handle.Section, out var definition) ||
                !_prototypes.TryIndex(definition.ConditionProfile, out var conditionProfile))
            {
                continue;
            }

            var previousSp = FixedPoint2.Clamp(state.CurrentSp, FixedPoint2.Zero, state.MaxSp);
            var currentSp = FixedPoint2.Max(FixedPoint2.Zero, previousSp - resolution.AblationApplied);
            if (currentSp == previousSp)
                continue;

            var previousCondition = NCArmorRules.GetConditionStage(previousSp, state.MaxSp, conditionProfile.Stages);
            var currentCondition = NCArmorRules.GetConditionStage(currentSp, state.MaxSp, conditionProfile.Stages);
            state.CurrentSp = currentSp;
            unchecked
            {
                armor.Revision++;
            }
            changed++;

            RaiseLocalEvent(
                section.Handle.Source,
                new NCArmorAblatedEvent(
                    section.Handle.Section,
                    previousSp,
                    currentSp,
                    previousCondition,
                    currentCondition));
        }

        return changed;
    }

    /// <summary>
    /// Repair/admin API. Values are clamped to the instance MaxSP and invalidate the armor revision.
    /// </summary>
    public bool TrySetCurrentSp(
        EntityUid source,
        ProtoId<NCArmorSectionPrototype> sectionId,
        FixedPoint2 requestedSp)
    {
        if (!TryComp<NCArmorComponent>(source, out var armor) ||
            !TryFindState(armor, sectionId, out var state))
        {
            return false;
        }

        var normalized = FixedPoint2.Clamp(requestedSp, FixedPoint2.Zero, state.MaxSp);
        if (normalized == state.CurrentSp)
            return true;

        state.CurrentSp = normalized;
        unchecked
        {
            armor.Revision++;
        }
        return true;
    }

    public bool TryGetCurrentSp(
        EntityUid source,
        ProtoId<NCArmorSectionPrototype> sectionId,
        out FixedPoint2 currentSp)
    {
        if (TryComp<NCArmorComponent>(source, out var armor) &&
            TryFindState(armor, sectionId, out var state))
        {
            currentSp = FixedPoint2.Clamp(state.CurrentSp, FixedPoint2.Zero, state.MaxSp);
            return true;
        }

        currentSp = FixedPoint2.Zero;
        return false;
    }

    public bool TryGetConditionStage(
        EntityUid source,
        ProtoId<NCArmorSectionPrototype> sectionId,
        out NCArmorConditionStage condition)
    {
        if (TryComp<NCArmorComponent>(source, out var armor) &&
            TryFindState(armor, sectionId, out var state) &&
            _prototypes.TryIndex(sectionId, out var definition) &&
            _prototypes.TryIndex(definition.ConditionProfile, out var profile))
        {
            condition = NCArmorRules.GetConditionStage(state.CurrentSp, state.MaxSp, profile.Stages);
            return true;
        }

        condition = NCArmorConditionStage.Destroyed;
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
}
