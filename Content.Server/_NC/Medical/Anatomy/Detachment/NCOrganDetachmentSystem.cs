// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.Medical.Bleeding;
using Content.Server._NC.Medical.Physiology;
using Content.Server._NC.Medical.Persistence;
using Content.Server._NC.Medical.Wounds;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Anatomy.Detachment;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Persistence;
using Content.Shared._NC.Medical.Treatment;
using Content.Shared._NC.Medical.Transplantation;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Anatomy.Detachment;

/// <summary>
/// Detaches and reattaches the materialized SS14 organ relation subtree of an authoritative NC human.
/// Container operations are deliberately used instead of hand/mind special cases: BodySystem then raises
/// the normal organ events consumed by BrainSystem, HandOrganSystem and other engine integrations.
/// </summary>
public sealed partial class NCOrganDetachmentSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private OrganRelationSystem _relations = default!;
    [Dependency] private NCAnatomySystem _anatomy = default!;
    [Dependency] private NCMedicalBodySystem _medicalBodies = default!;
    [Dependency] private NCBleedingSystem _bleeding = default!;
    [Dependency] private NCWoundSystem _wounds = default!;
    [Dependency] private NCPhysiologySchedulerSystem _physiology = default!;

    /// <summary>
    /// Removes the organ identified by <paramref name="category"/> and every live OrganRelation descendant
    /// from the body's organ container. Internal subtree relationships and all organ components survive.
    /// </summary>
    public bool TryDetachOrgan(
        EntityUid body,
        ProtoId<OrganCategoryPrototype> category,
        out EntityUid organRoot)
    {
        organRoot = default;
        if (!TryGetAuthoritativeBody(body, out var bodyComponent) ||
            !_anatomy.TryGetOrgan(body, category, out var root) ||
            !TryComp<ChildOrganComponent>(root, out var boundary) ||
            boundary.Parent is not { } boundaryParent ||
            !TryComp<OrganComponent>(boundaryParent, out var parentOrgan) ||
            parentOrgan.Category is not { } originalParentCategory ||
            parentOrgan.Body != body ||
            !TryCollectSubtree(root, out var subtree, out var categories) ||
            !ValidateAttachedSubtree(body, bodyComponent, subtree))
        {
            return false;
        }

        if (TryComp<NCDetachedOrganMedicalDataComponent>(root, out var existingPayload) &&
            HasMedicalPayload(existingPayload))
        {
            return false;
        }

        var organContainer = bodyComponent.Organs!;
        foreach (var organ in subtree)
        {
            if (!_containers.CanRemove(organ, organContainer))
                return false;
        }

        // Every flat body-container member is placed at the body's current world position. OrganRelation,
        // not transform parenting, remains the sole hierarchy for the detached anatomical subtree.
        var destination = Transform(body).Coordinates;
        var removed = new List<EntityUid>(subtree.Count);
        foreach (var organ in subtree)
        {
            if (_containers.Remove(organ, organContainer, destination: destination))
            {
                removed.Add(organ);
                continue;
            }

            RollbackDetach(body, organContainer, removed);
            return false;
        }

        // Only the boundary edge is broken; all relations whose endpoints are in the subtree remain live.
        _relations.Orphan(root);
        ExtractMedicalPayload(body, root, categories, originalParentCategory);
        AssignDetachedPersistenceOwner(body, root, subtree);
        MarkBodyChanged(body);
        organRoot = root;
        return true;
    }

    /// <summary>
    /// Read-only preflight used by surgery effect contracts. It checks the same live graph and container
    /// boundaries without modifying relationships or moving medical state.
    /// </summary>
    public bool CanDetachOrgan(
        EntityUid body,
        ProtoId<OrganCategoryPrototype> category,
        out EntityUid organRoot)
    {
        organRoot = default;
        if (!TryGetAuthoritativeBody(body, out var bodyComponent) ||
            !_anatomy.TryGetOrgan(body, category, out var root) ||
            !TryComp<ChildOrganComponent>(root, out var boundary) ||
            boundary.Parent is not { } boundaryParent ||
            !TryComp<OrganComponent>(boundaryParent, out var parentOrgan) ||
            parentOrgan.Body != body ||
            !TryCollectSubtree(root, out var subtree, out _) ||
            !ValidateAttachedSubtree(body, bodyComponent, subtree) ||
            TryComp<NCDetachedOrganMedicalDataComponent>(root, out var payload) && HasMedicalPayload(payload))
        {
            return false;
        }

        foreach (var organ in subtree)
        {
            if (!_containers.CanRemove(organ, bodyComponent.Organs!))
                return false;
        }

        organRoot = root;
        return true;
    }

    /// <summary>
    /// Inserts the exact detached subtree into an authoritative body and relates its root to the requested
    /// live parent category. Duplicate categories or persistence IDs reject the operation before mutation.
    /// </summary>
    public bool TryAttachOrgan(
        EntityUid body,
        ProtoId<OrganCategoryPrototype> parentCategory,
        EntityUid organRoot)
    {
        if (!TryGetAuthoritativeBody(body, out var bodyComponent) ||
            !_anatomy.TryGetOrgan(body, parentCategory, out var parent) ||
            !TryCollectSubtree(organRoot, out var subtree, out var categories) ||
            !ValidateDetachedSubtree(subtree) ||
            !ValidateCategoryAvailability(bodyComponent, categories) ||
            !ValidateBoundaryCategory(organRoot, parentCategory) ||
            !ValidateMedicalMerge(body, organRoot, categories))
        {
            return false;
        }

        if (!HasComp<ParentOrganComponent>(parent) ||
            !TryComp<ChildOrganComponent>(organRoot, out var rootChild) ||
            rootChild.Parent != null)
        {
            return false;
        }

        var organContainer = bodyComponent.Organs!;
        var placements = new Dictionary<EntityUid, OriginalPlacement>(subtree.Count);
        foreach (var organ in subtree)
        {
            BaseContainer? oldContainer = null;
            if (_containers.TryGetContainingContainer((organ, null, null), out var containing))
            {
                oldContainer = containing;
                if (!_containers.CanRemove(organ, containing))
                    return false;
            }

            if (!_containers.CanInsert(organ, organContainer, containerXform: Transform(body)))
                return false;

            var transform = Transform(organ);
            placements.Add(organ, new OriginalPlacement(oldContainer, transform.Coordinates, transform.LocalRotation));
        }

        var inserted = new List<EntityUid>(subtree.Count);
        foreach (var organ in subtree)
        {
            if (_containers.Insert(organ, organContainer, containerXform: Transform(body)))
            {
                inserted.Add(organ);
                continue;
            }

            RollbackAttach(organContainer, inserted, placements);
            return false;
        }

        _relations.Relate(parent, organRoot);
        RestoreMedicalPayload(body, organRoot);
        RemComp<NCDetachedOrganPersistenceOwnerComponent>(organRoot);
        var attached = new NCOrganAttachedEvent(organRoot);
        RaiseLocalEvent(body, ref attached);
        MarkBodyChanged(body);
        return true;
    }

    /// <summary>
    /// Read-only preflight used by reattachment and transplant surgery.
    /// </summary>
    public bool CanAttachOrgan(
        EntityUid body,
        ProtoId<OrganCategoryPrototype> parentCategory,
        EntityUid organRoot)
    {
        if (!TryGetAuthoritativeBody(body, out var bodyComponent) ||
            !_anatomy.TryGetOrgan(body, parentCategory, out var parent) ||
            !TryCollectSubtree(organRoot, out var subtree, out var categories) ||
            !ValidateDetachedSubtree(subtree) ||
            !ValidateCategoryAvailability(bodyComponent, categories) ||
            !ValidateBoundaryCategory(organRoot, parentCategory) ||
            !ValidateMedicalMerge(body, organRoot, categories) ||
            !HasComp<ParentOrganComponent>(parent) ||
            !TryComp<ChildOrganComponent>(organRoot, out var rootChild) ||
            rootChild.Parent != null)
        {
            return false;
        }

        var organContainer = bodyComponent.Organs!;
        foreach (var organ in subtree)
        {
            if (_containers.TryGetContainingContainer((organ, null, null), out var containing) &&
                !_containers.CanRemove(organ, containing) ||
                !_containers.CanInsert(organ, organContainer, containerXform: Transform(body)))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryGetAuthoritativeBody(EntityUid body, out BodyComponent component)
    {
        component = default!;
        if (!HasComp<NCMedicalBodyComponent>(body) ||
            !TryComp(body, out BodyComponent? resolved) ||
            resolved.Organs == null ||
            TerminatingOrDeleted(body))
        {
            return false;
        }

        component = resolved;
        return true;
    }

    /// <summary>
    /// Traverses only direct ParentOrgan children. A visited set makes malformed cycles fail closed rather
    /// than turning a corrupt graph into an unbounded traversal.
    /// </summary>
    private bool TryCollectSubtree(
        EntityUid root,
        out List<EntityUid> subtree,
        out HashSet<ProtoId<OrganCategoryPrototype>> categories)
    {
        subtree = [];
        categories = [];
        if (TerminatingOrDeleted(root))
            return false;

        var pending = new Stack<EntityUid>();
        var visited = new HashSet<EntityUid>();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current) ||
                TerminatingOrDeleted(current) ||
                !TryComp<OrganComponent>(current, out var organ) ||
                organ.Category is not { } category ||
                !categories.Add(category))
            {
                return false;
            }

            subtree.Add(current);
            if (!TryComp<ParentOrganComponent>(current, out var parent))
                continue;

            foreach (var child in parent.Children.OrderBy(uid => uid.Id))
            {
                if (!TryComp<ChildOrganComponent>(child, out var childRelation) ||
                    childRelation.Parent != current)
                {
                    return false;
                }

                pending.Push(child);
            }
        }

        return subtree.Count > 0;
    }

    private bool ValidateAttachedSubtree(
        EntityUid body,
        BodyComponent bodyComponent,
        IReadOnlyList<EntityUid> subtree)
    {
        var organContainer = bodyComponent.Organs!;
        foreach (var organ in subtree)
        {
            if (!organContainer.Contains(organ) ||
                !TryComp<OrganComponent>(organ, out var organComponent) ||
                organComponent.Body != body)
            {
                return false;
            }
        }

        return true;
    }

    private bool ValidateDetachedSubtree(IReadOnlyList<EntityUid> subtree)
    {
        foreach (var organ in subtree)
        {
            if (!TryComp<OrganComponent>(organ, out var organComponent) || organComponent.Body != null)
                return false;
        }

        return true;
    }

    private bool ValidateCategoryAvailability(
        BodyComponent bodyComponent,
        IReadOnlySet<ProtoId<OrganCategoryPrototype>> categories)
    {
        foreach (var organ in bodyComponent.Organs!.ContainedEntities)
        {
            if (TryComp<OrganComponent>(organ, out var component) &&
                component.Category is { } category &&
                categories.Contains(category))
            {
                return false;
            }
        }

        return true;
    }

    private bool ValidateBoundaryCategory(
        EntityUid organRoot,
        ProtoId<OrganCategoryPrototype> parentCategory)
    {
        return !TryComp<NCDetachedOrganMedicalDataComponent>(organRoot, out var payload) ||
               string.IsNullOrWhiteSpace(payload.RequiredParentCategory.Id) ||
               payload.RequiredParentCategory == parentCategory;
    }

    private bool ValidateMedicalMerge(
        EntityUid body,
        EntityUid organRoot,
        IReadOnlySet<ProtoId<OrganCategoryPrototype>> categories)
    {
        if (!TryComp<NCDetachedOrganMedicalDataComponent>(organRoot, out var payload))
            return true;

        if (!TryComp<NCMedicalBodyComponent>(body, out var medical) ||
            string.IsNullOrWhiteSpace(payload.SourceBodyProfile.Id) ||
            payload.SourceBodyProfile != medical.Profile)
        {
            return false;
        }

        if (!float.IsFinite(payload.OverflowPain) || payload.OverflowPain < 0f ||
            !float.IsFinite(payload.OverflowExternalBleedRate) || payload.OverflowExternalBleedRate < 0f ||
            !float.IsFinite(payload.OverflowInternalBleedRate) || payload.OverflowInternalBleedRate < 0f ||
            payload.OverflowInternalBleedRatesByRegion.Any(entry =>
                !categories.Contains(entry.Key) || !float.IsFinite(entry.Value) || entry.Value < 0f) ||
            payload.OverflowTraumaByZone.Values.Any(value => !float.IsFinite(value) || value < 0f))
        {
            return false;
        }

        if ((payload.Wounds.Count > 0 || payload.Scars.Count > 0 || payload.OverflowTraumaByZone.Count > 0) &&
            (string.IsNullOrWhiteSpace(payload.WoundProfile.Id) ||
             !_prototypes.HasIndex(payload.WoundProfile)))
        {
            return false;
        }

        if (payload.Wounds.Values.Any(wound =>
                !categories.Contains(wound.RegionCategory) ||
                !ZoneBelongsToCategories(wound.TargetZone, categories)) ||
            payload.Scars.Any(scar =>
                !categories.Contains(scar.RegionCategory) ||
                !ZoneBelongsToCategories(scar.TargetZone, categories)) ||
            payload.OverflowTraumaByZone.Keys.Any(zone => !ZoneBelongsToCategories(zone, categories)) ||
            payload.BleedingSources.Any(source =>
                !categories.Contains(source.RegionCategory) ||
                !ZoneBelongsToCategories(source.TargetZone, categories)) ||
            payload.WoundTreatments.Keys.Any(woundId => !payload.Wounds.ContainsKey(woundId)) ||
            payload.Tourniquets.Keys.Any(zone => !ZoneBelongsToCategories(zone, categories)))
        {
            return false;
        }

        if ((payload.WoundTreatments.Count > 0 || payload.Tourniquets.Count > 0) &&
            (string.IsNullOrWhiteSpace(payload.TreatmentProfile.Id) ||
             !_prototypes.HasIndex(payload.TreatmentProfile)))
        {
            return false;
        }

        if (TryComp<NCWoundContainerComponent>(body, out var wounds))
        {
            if (!string.IsNullOrWhiteSpace(payload.WoundProfile.Id) &&
                !string.IsNullOrWhiteSpace(wounds.Profile.Id) &&
                payload.WoundProfile != wounds.Profile)
            {
                return false;
            }

            if (payload.Wounds.Keys.Any(wounds.Wounds.ContainsKey))
                return false;
            if (payload.Scars.Any(incoming => wounds.Scars.Any(existing =>
                    existing.ScarId == incoming.ScarId ||
                    existing.SourceWoundId == incoming.SourceWoundId)))
                return false;
        }

        if (TryComp<NCBleedingComponent>(body, out var bleeding))
        {
            var sourceIds = bleeding.Sources.Select(source => source.SourceId).ToHashSet();
            if (payload.BleedingSources.Any(source =>
                    source.SourceId == Guid.Empty ||
                    !payload.Wounds.ContainsKey(source.WoundId) ||
                    !sourceIds.Add(source.SourceId)))
            {
                return false;
            }
        }
        else if (payload.BleedingSources.Any(source =>
                     source.SourceId == Guid.Empty || !payload.Wounds.ContainsKey(source.WoundId)) ||
                 payload.BleedingSources.Select(source => source.SourceId).Distinct().Count() !=
                 payload.BleedingSources.Count)
        {
            return false;
        }

        if (!TryComp<NCTreatmentStateComponent>(body, out var treatment))
            return true;

        if (!string.IsNullOrWhiteSpace(payload.TreatmentProfile.Id) &&
            !string.IsNullOrWhiteSpace(treatment.Profile.Id) &&
            payload.TreatmentProfile != treatment.Profile)
        {
            return false;
        }

        return !payload.WoundTreatments.Keys.Any(treatment.WoundTreatments.ContainsKey) &&
               !payload.Tourniquets.Keys.Any(treatment.Tourniquets.ContainsKey);
    }

    private void ExtractMedicalPayload(
        EntityUid body,
        EntityUid root,
        IReadOnlySet<ProtoId<OrganCategoryPrototype>> categories,
        ProtoId<OrganCategoryPrototype> requiredParentCategory)
    {
        var payload = EnsureComp<NCDetachedOrganMedicalDataComponent>(root);
        payload.SourceBodyProfile = Comp<NCMedicalBodyComponent>(body).Profile;
        payload.RequiredParentCategory = requiredParentCategory;
        var movedWoundIds = new HashSet<Guid>();
        if (TryComp<NCWoundContainerComponent>(body, out var wounds))
        {
            payload.WoundProfile = wounds.Profile;
            foreach (var (woundId, wound) in wounds.Wounds.ToArray())
            {
                if (!categories.Contains(wound.RegionCategory))
                    continue;

                wounds.Wounds.Remove(woundId);
                payload.Wounds.Add(woundId, wound);
                movedWoundIds.Add(woundId);
            }

            foreach (var scar in wounds.Scars.ToArray())
            {
                if (!categories.Contains(scar.RegionCategory))
                    continue;

                wounds.Scars.Remove(scar);
                payload.Scars.Add(scar);
            }

            var totalOverflowTrauma = wounds.OverflowTraumaByZone.Values
                .Sum(value => MathF.Max(0f, value));
            var movedOverflowTrauma = 0f;
            foreach (var (zone, trauma) in wounds.OverflowTraumaByZone.ToArray())
            {
                if (!ZoneBelongsToCategories(zone, categories))
                    continue;

                wounds.OverflowTraumaByZone.Remove(zone);
                payload.OverflowTraumaByZone.Add(zone, trauma);
                movedOverflowTrauma += MathF.Max(0f, trauma);
            }

            if (movedOverflowTrauma > 0f && totalOverflowTrauma > 0f)
            {
                // Overflow pain/bleeding are legacy aggregates. Attribute their exact round-trip share by
                // the only persisted per-zone scalar, preserving totals without inventing another graph.
                var fraction = Math.Clamp(movedOverflowTrauma / totalOverflowTrauma, 0f, 1f);
                payload.OverflowPain = MathF.Max(0f, wounds.OverflowPain) * fraction;
                wounds.OverflowPain = MathF.Max(0f, wounds.OverflowPain - payload.OverflowPain);
                var overflowBleeding = _bleeding.ExtractOverflowFraction(body, fraction, categories);
                payload.OverflowExternalBleedRate = overflowBleeding.External;
                payload.OverflowInternalBleedRate = overflowBleeding.Internal;
                foreach (var (region, rate) in overflowBleeding.InternalByRegion)
                    payload.OverflowInternalBleedRatesByRegion.Add(region, rate);
            }
        }

        payload.BleedingSources.AddRange(_bleeding.ExtractWoundSources(body, movedWoundIds));

        if (!TryComp<NCTreatmentStateComponent>(body, out var treatment))
            return;

        payload.TreatmentProfile = treatment.Profile;
        foreach (var (woundId, record) in treatment.WoundTreatments.ToArray())
        {
            if (!movedWoundIds.Contains(woundId))
                continue;

            treatment.WoundTreatments.Remove(woundId);
            payload.WoundTreatments.Add(woundId, record);
        }

        foreach (var (zone, record) in treatment.Tourniquets.ToArray())
        {
            if (ZoneBelongsToCategories(zone, categories))
            {
                treatment.Tourniquets.Remove(zone);
                payload.Tourniquets.Add(zone, record);
                continue;
            }

            // A proximal tourniquet can reference a subsequently detached distal wound. Keep the physical
            // treatment with its proximal body zone, but remove stale references to the detached payload.
            record.CoveredWoundIds.RemoveAll(movedWoundIds.Contains);
        }
    }

    private void RestoreMedicalPayload(EntityUid body, EntityUid root)
    {
        if (!TryComp<NCDetachedOrganMedicalDataComponent>(root, out var payload))
            return;

        if (payload.Wounds.Count > 0 || payload.Scars.Count > 0 || payload.OverflowTraumaByZone.Count > 0)
        {
            var wounds = _wounds.EnsureInitialized(body);
            foreach (var (woundId, wound) in payload.Wounds)
                wounds.Wounds.Add(woundId, wound);
            wounds.Scars.AddRange(payload.Scars);
            foreach (var (zone, trauma) in payload.OverflowTraumaByZone)
                wounds.OverflowTraumaByZone[zone] = wounds.OverflowTraumaByZone.GetValueOrDefault(zone) + trauma;
            wounds.OverflowPain += MathF.Max(0f, payload.OverflowPain);

            _bleeding.RestoreWoundSources(body, payload.BleedingSources);
            _bleeding.RestoreOverflow(
                body,
                payload.OverflowExternalBleedRate,
                payload.OverflowInternalBleedRate,
                payload.OverflowInternalBleedRatesByRegion);
            foreach (var wound in payload.Wounds.Values)
            {
                if (payload.BleedingSources.All(source => source.WoundId != wound.WoundId))
                    _bleeding.SynchronizeWoundSources(body, wound);
            }
        }

        if (payload.WoundTreatments.Count > 0 || payload.Tourniquets.Count > 0)
        {
            var treatment = EnsureComp<NCTreatmentStateComponent>(body);
            if (string.IsNullOrWhiteSpace(treatment.Profile.Id))
                treatment.Profile = payload.TreatmentProfile;
            foreach (var (woundId, record) in payload.WoundTreatments)
                treatment.WoundTreatments.Add(woundId, record);
            foreach (var (zone, record) in payload.Tourniquets)
                treatment.Tourniquets.Add(zone, record);
        }

        RemComp<NCDetachedOrganMedicalDataComponent>(root);
    }

    private bool ZoneBelongsToCategories(
        ProtoId<NCTargetZonePrototype> zone,
        IReadOnlySet<ProtoId<OrganCategoryPrototype>> categories)
    {
        return _prototypes.TryIndex(zone, out NCTargetZonePrototype? zonePrototype) &&
               categories.Contains(zonePrototype.MedicalRegionCategory);
    }

    private void MarkBodyChanged(EntityUid body)
    {
        _medicalBodies.TryMarkDirty(
            body,
            NCMedicalDirtyDomains.Anatomy |
            NCMedicalDirtyDomains.Wounds |
            NCMedicalDirtyDomains.Vascular |
            NCMedicalDirtyDomains.Pain |
            NCMedicalDirtyDomains.OrganFunction |
            NCMedicalDirtyDomains.Consciousness |
            NCMedicalDirtyDomains.Diagnostics |
            NCMedicalDirtyDomains.Persistence);

        // Zero elapsed time recalculates current organ function and consciousness without inventing
        // off-screen blood loss. Future bleeding remains owned by the central scheduler.
        _physiology.ProcessNow(body, TimeSpan.Zero);
    }

    private void AssignDetachedPersistenceOwner(
        EntityUid body,
        EntityUid organRoot,
        IReadOnlyList<EntityUid> subtree)
    {
        var detachedOwner = EnsureComp<NCDetachedOrganPersistenceOwnerComponent>(organRoot);
        detachedOwner.RequiresExternalStorage = true;
        if (!TryComp<NCMedicalPersistenceOwnerComponent>(body, out var bodyOwner))
        {
            detachedOwner.Kind = NCMedicalStableOwnerKind.Unassigned;
            detachedOwner.Key = string.Empty;
            EnsureStableOrganIds(subtree, null);
            return;
        }

        detachedOwner.Kind = bodyOwner.Kind;
        detachedOwner.Key = bodyOwner.Key;
        EnsureStableOrganIds(subtree, bodyOwner);
    }

    private void EnsureStableOrganIds(
        IReadOnlyList<EntityUid> subtree,
        NCMedicalPersistenceOwnerComponent? donor)
    {
        foreach (var organ in subtree)
        {
            var identity = EnsureComp<NCOrganPersistenceIdentityComponent>(organ);
            if (identity.StableOrganId == Guid.Empty)
                identity.StableOrganId = Guid.NewGuid();
            if (donor == null || identity.DonorKind != NCMedicalStableOwnerKind.Unassigned)
                continue;

            identity.DonorKind = donor.Kind;
            identity.DonorKey = donor.Key;
        }
    }

    private void RollbackDetach(EntityUid body, BaseContainer organContainer, IReadOnlyList<EntityUid> removed)
    {
        var bodyTransform = Transform(body);
        foreach (var organ in removed)
            _containers.Insert(organ, organContainer, containerXform: bodyTransform, force: true);
    }

    private void RollbackAttach(
        BaseContainer organContainer,
        IReadOnlyList<EntityUid> inserted,
        IReadOnlyDictionary<EntityUid, OriginalPlacement> placements)
    {
        foreach (var organ in inserted.Reverse())
        {
            var placement = placements[organ];
            _containers.Remove(
                organ,
                organContainer,
                force: true,
                destination: placement.Coordinates,
                localRotation: placement.Rotation);
            if (placement.Container != null)
                _containers.Insert(organ, placement.Container, force: true);
        }
    }

    private static bool HasMedicalPayload(NCDetachedOrganMedicalDataComponent payload)
    {
        return payload.Wounds.Count > 0 ||
               payload.Scars.Count > 0 ||
               !string.IsNullOrWhiteSpace(payload.SourceBodyProfile.Id) ||
               !string.IsNullOrWhiteSpace(payload.RequiredParentCategory.Id) ||
               payload.OverflowTraumaByZone.Count > 0 ||
               payload.OverflowPain > 0f ||
               payload.OverflowExternalBleedRate > 0f ||
               payload.OverflowInternalBleedRate > 0f ||
               payload.OverflowInternalBleedRatesByRegion.Count > 0 ||
               payload.BleedingSources.Count > 0 ||
               payload.WoundTreatments.Count > 0 ||
               payload.Tourniquets.Count > 0;
    }

    private readonly record struct OriginalPlacement(
        BaseContainer? Container,
        EntityCoordinates Coordinates,
        Angle Rotation);
}
