// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.Combat.Armor;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared.FixedPoint;
using Content.Shared.Inventory.Events;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Combat.Armor;

/// <summary>
/// Initializes per-instance sections and filters only caller-provided armor entities for one actual zone.
/// This system deliberately has no entity query, inventory traversal or update loop.
/// </summary>
public sealed partial class NCArmorCoverageSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCArmorComponent, ComponentStartup>(OnArmorStartup);
        SubscribeLocalEvent<NCArmorComponent, GotEquippedEvent>(OnArmorEquipped);
        SubscribeLocalEvent<NCArmorComponent, GotUnequippedEvent>(OnArmorUnequipped);
        SubscribeLocalEvent<NCArmorBoundToComponent, ComponentShutdown>(OnBindingShutdown);
    }

    /// <summary>
    /// Binds an armor source to one target. Inventory and cyberware systems call this when ownership changes.
    /// </summary>
    public bool TryBindSource(EntityUid source, EntityUid target)
    {
        if (!Exists(source) || !Exists(target) || !HasComp<NCArmorComponent>(source))
            return false;

        var binding = EnsureComp<NCArmorBoundToComponent>(source);
        if (binding.Target.Valid && binding.Target != target)
            RemoveFromCache(binding.Target, source);

        binding.Target = target;
        var cache = EnsureComp<NCArmorRuntimeCacheComponent>(target);
        if (!cache.Sources.Contains(source))
        {
            cache.Sources.Add(source);
            unchecked
            {
                cache.Revision++;
            }
        }
        return true;
    }

    public void UnbindSource(EntityUid source)
    {
        if (TryComp<NCArmorBoundToComponent>(source, out var binding))
            RemoveFromCache(binding.Target, source);

        RemComp<NCArmorBoundToComponent>(source);
    }

    /// <summary>
    /// Collects the target's maintained source index. The target itself is checked for installed armor first.
    /// </summary>
    public void CollectBound(
        in NCArmorResolveRequest request,
        List<NCArmorSectionSnapshot> output)
    {
        output.Clear();
        if (!Exists(request.Target))
            return;

        CollectSource(request.Target, request.ActualZone, request.DamageClass, output);
        if (!TryComp<NCArmorRuntimeCacheComponent>(request.Target, out var cache))
            return;

        for (var i = cache.Sources.Count - 1; i >= 0; i--)
        {
            var source = cache.Sources[i];
            if (!Exists(source) ||
                !TryComp<NCArmorBoundToComponent>(source, out var binding) ||
                binding.Target != request.Target)
            {
                cache.Sources.RemoveAt(i);
                unchecked
                {
                    cache.Revision++;
                }
                continue;
            }

            CollectSource(source, request.ActualZone, request.DamageClass, output);
        }
    }

    /// <summary>
    /// Collects explicitly trusted sources. The caller already established that these sources protect the target.
    /// </summary>
    public void CollectExplicit(
        in NCArmorResolveRequest request,
        IReadOnlyList<EntityUid> candidateSources,
        List<NCArmorSectionSnapshot> output)
    {
        output.Clear();
        if (!Exists(request.Target))
            return;

        var seenSources = new HashSet<EntityUid>();
        foreach (var source in candidateSources)
        {
            if (!seenSources.Add(source))
                continue;

            CollectSource(source, request.ActualZone, request.DamageClass, output);
        }
    }

    /// <summary>
    /// Collects only candidates explicitly bound to this target. Armor installed directly on the target is valid too.
    /// </summary>
    public void CollectBound(
        in NCArmorResolveRequest request,
        IReadOnlyList<EntityUid> candidateSources,
        List<NCArmorSectionSnapshot> output)
    {
        output.Clear();
        if (!Exists(request.Target))
            return;

        var seenSources = new HashSet<EntityUid>();
        foreach (var source in candidateSources)
        {
            if (!seenSources.Add(source) || !Exists(source))
                continue;

            if (source != request.Target &&
                (!TryComp<NCArmorBoundToComponent>(source, out var binding) || binding.Target != request.Target))
            {
                continue;
            }

            CollectSource(source, request.ActualZone, request.DamageClass, output);
        }
    }

    private void OnArmorStartup(EntityUid uid, NCArmorComponent component, ComponentStartup args)
    {
        if (!_prototypes.TryIndex(component.Profile, out var profile))
            return;

        var allowed = profile.Sections.ToHashSet();
        var seen = new HashSet<ProtoId<NCArmorSectionPrototype>>();

        // Persisted entries are normalized against current profile data and duplicate/foreign entries are discarded.
        for (var i = component.Sections.Count - 1; i >= 0; i--)
        {
            var state = component.Sections[i];
            if (!allowed.Contains(state.Section) ||
                !seen.Add(state.Section) ||
                !_prototypes.TryIndex(state.Section, out var definition))
            {
                component.Sections.RemoveAt(i);
                continue;
            }

            state.MaxSp = FixedPoint2.Max(FixedPoint2.Zero, definition.MaxSp);
            state.CurrentSp = FixedPoint2.Clamp(state.CurrentSp, FixedPoint2.Zero, state.MaxSp);
        }

        foreach (var sectionId in profile.Sections)
        {
            if (seen.Contains(sectionId) || !_prototypes.TryIndex(sectionId, out var definition))
                continue;

            var maxSp = FixedPoint2.Max(FixedPoint2.Zero, definition.MaxSp);
            component.Sections.Add(new NCArmorSectionState
            {
                Section = sectionId,
                MaxSp = maxSp,
                CurrentSp = maxSp,
            });
            seen.Add(sectionId);
        }
    }

    private void OnArmorEquipped(EntityUid uid, NCArmorComponent component, GotEquippedEvent args)
    {
        // Only authoritative human medical bodies use local NC armor. Other entities retain upstream armor.
        if (!HasComp<NCMedicalBodyComponent>(args.EquipTarget))
            return;

        TryBindSource(uid, args.EquipTarget);
    }

    private void OnArmorUnequipped(EntityUid uid, NCArmorComponent component, GotUnequippedEvent args)
    {
        if (!TryComp<NCArmorBoundToComponent>(uid, out var binding) || binding.Target != args.EquipTarget)
            return;

        UnbindSource(uid);
    }

    private void OnBindingShutdown(EntityUid uid, NCArmorBoundToComponent component, ComponentShutdown args)
    {
        RemoveFromCache(component.Target, uid);
    }

    private void RemoveFromCache(EntityUid target, EntityUid source)
    {
        if (!target.Valid || !TryComp<NCArmorRuntimeCacheComponent>(target, out var cache))
            return;

        if (!cache.Sources.Remove(source))
            return;

        unchecked
        {
            cache.Revision++;
        }
    }

    private void CollectSource(
        EntityUid source,
        ProtoId<Content.Shared._NC.Combat.Targeting.NCTargetZonePrototype> actualZone,
        NCArmorDamageClass damageClass,
        List<NCArmorSectionSnapshot> output)
    {
        if (!TryComp<NCArmorComponent>(source, out var armor) ||
            !_prototypes.TryIndex(armor.Profile, out var profile))
        {
            return;
        }

        foreach (var sectionId in profile.Sections)
        {
            if (!_prototypes.TryIndex(sectionId, out var definition) ||
                !definition.CoveredZones.Contains(actualZone) ||
                !definition.ApplicableDamageClasses.Contains(damageClass) ||
                !TryFindState(armor, sectionId, out var state))
            {
                continue;
            }

            var maxSp = FixedPoint2.Max(FixedPoint2.Zero, state.MaxSp);
            var currentSp = FixedPoint2.Clamp(state.CurrentSp, FixedPoint2.Zero, maxSp);
            if (currentSp <= FixedPoint2.Zero)
                continue;

            output.Add(new NCArmorSectionSnapshot(
                new NCArmorSectionHandle(source, sectionId),
                maxSp,
                currentSp,
                definition.Rigid,
                definition.Sealed));
        }
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
