// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using Content.Server._NC.Medical.Bleeding;
using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Medical.Pharmacology;
using Content.Server._NC.Medical.Physiology;
using Content.Server._NC.Medical.Systemic;
using Content.Server._NC.Medical.Wounds;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Emergency;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared._NC.Medical.Treatment;
using Content.Shared.Body;
using Content.Shared.Rejuvenate;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Recovery;

/// <summary>
/// Trusted full-reset boundary used only by RejuvenateEvent or an explicitly authorized server caller.
/// Ordinary negative DamageSpecifier values never enter this path.
/// </summary>
public sealed partial class NCMedicalRejuvenationSystem : EntitySystem
{
    private const int MaximumOrganCount = 64;

    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private OrganRelationSystem _relations = default!;
    [Dependency] private NCMedicalBodySystem _medicalBodies = default!;
    [Dependency] private NCBleedingSystem _bleeding = default!;
    [Dependency] private NCCyberwareSystem _cyberware = default!;
    [Dependency] private NCCirculationBloodSystem _blood = default!;
    [Dependency] private NCWoundSystem _wounds = default!;
    [Dependency] private NCSystemicSystem _systemic = default!;
    [Dependency] private NCPharmacologySystem _pharmacology = default!;
    [Dependency] private NCPhysiologySystem _physiology = default!;
    [Dependency] private NCPhysiologySchedulerSystem _scheduler = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCMedicalBodyComponent, RejuvenateEvent>(OnRejuvenate);
    }

    private void OnRejuvenate(Entity<NCMedicalBodyComponent> body, ref RejuvenateEvent args)
    {
        TryRejuvenateMedicalState(body.Owner);
    }

    /// <summary>
    /// Fully restores one authoritative medical body, including canonically missing organs. Callers are
    /// trusted server code and must perform their own admin/command authorization before invoking this API.
    /// </summary>
    public bool TryRejuvenateMedicalState(EntityUid body)
    {
        if (!TryComp<NCMedicalBodyComponent>(body, out var medical) ||
            !TryRestoreMissingAnatomy(body, out var organs))
        {
            return false;
        }

        foreach (var organ in organs)
            ResetOrgan(organ);

        _wounds.ResetBody(body);
        if (!_cyberware.ResetBody(body))
            return false;
        _bleeding.ResetBody(body);
        _blood.ResetCavities(body);
        _systemic.ResetBody(body);
        _pharmacology.ResetBody(body);
        RemComp<NCTreatmentStateComponent>(body);
        RemComp<NCEmergencyCareStateComponent>(body);
        RemComp<NCSurgeryPatientStateComponent>(body);
        _physiology.ResetBody(body);
        if (!_blood.SynchronizeSolution(body))
            return false;

        _medicalBodies.TryMarkDirty((body, medical), NCMedicalDirtyDomains.All);
        return _scheduler.ProcessNow(body, TimeSpan.Zero);
    }

    private bool TryRestoreMissingAnatomy(EntityUid body, out List<EntityUid> organs)
    {
        organs = [];
        if (!TryComp<BodyComponent>(body, out var bodyComponent) ||
            bodyComponent.Organs == null ||
            !TryComp<InitialBodyComponent>(body, out var initial))
        {
            return false;
        }

        // Keep the immutable prototype graph in local aliases. Besides avoiding repeated restricted-component
        // access, this makes every validation and spawn in this transaction use the same graph references.
        var initialOrgans = initial.Organs;
        var initialRelationships = initial.Relationships;
        if (initialOrgans.Count is <= 0 or > MaximumOrganCount ||
            !_containers.TryGetContainer(body, BodyComponent.ContainerID, out var container))
        {
            return false;
        }

        var current = new Dictionary<ProtoId<OrganCategoryPrototype>, EntityUid>();
        foreach (var organ in bodyComponent.Organs.ContainedEntities)
        {
            if (!TryComp<OrganComponent>(organ, out var organComponent) ||
                organComponent.Category is not { } category ||
                !initialOrgans.ContainsKey(category) ||
                !current.TryAdd(category, organ))
            {
                return false;
            }
        }

        if (!TryBuildParentMap(initialOrgans, initialRelationships, out var parents))
            return false;

        // Reject a corrupt extant relationship before any spawn mutates the graph.
        foreach (var (childCategory, parentCategory) in parents)
        {
            if (!current.TryGetValue(childCategory, out var child) ||
                !current.TryGetValue(parentCategory, out var parent))
            {
                continue;
            }

            if (!TryComp<ChildOrganComponent>(child, out var relation) || relation.Parent != parent)
                return false;
        }

        var spawned = new List<EntityUid>();
        var coordinates = new EntityCoordinates(body, Vector2.Zero);
        var bodyTransform = Transform(body);
        foreach (var category in initialOrgans.Keys
                     .Where(category => !current.ContainsKey(category))
                     .OrderBy(category => GetDepth(category, parents))
                     .ThenBy(category => category.Id, StringComparer.Ordinal))
        {
            var organ = Spawn(initialOrgans[category], coordinates);
            if (!_containers.Insert(organ, container, containerXform: bodyTransform))
            {
                Del(organ);
                RollBackSpawned(spawned);
                return false;
            }

            spawned.Add(organ);
            current[category] = organ;
        }

        foreach (var (childCategory, parentCategory) in parents
                     .OrderBy(edge => GetDepth(edge.Key, parents))
                     .ThenBy(edge => edge.Key.Id, StringComparer.Ordinal))
        {
            var child = current[childCategory];
            var parent = current[parentCategory];
            if (!HasComp<ParentOrganComponent>(parent) ||
                !TryComp<ChildOrganComponent>(child, out var childRelation))
            {
                RollBackSpawned(spawned);
                return false;
            }

            if (childRelation.Parent == null)
            {
                _relations.Relate(parent, child);
                if (childRelation.Parent != parent)
                {
                    RollBackSpawned(spawned);
                    return false;
                }
            }
            else if (childRelation.Parent != parent)
            {
                RollBackSpawned(spawned);
                return false;
            }
        }

        organs = current
            .OrderBy(entry => GetDepth(entry.Key, parents))
            .ThenBy(entry => entry.Key.Id, StringComparer.Ordinal)
            .Select(entry => entry.Value)
            .ToList();
        return organs.Count == initialOrgans.Count;
    }

    private static bool TryBuildParentMap(
        IReadOnlyDictionary<ProtoId<OrganCategoryPrototype>, EntProtoId<OrganComponent>> initialOrgans,
        IReadOnlyDictionary<ProtoId<OrganCategoryPrototype>, HashSet<ProtoId<OrganCategoryPrototype>>>? relationships,
        out Dictionary<ProtoId<OrganCategoryPrototype>, ProtoId<OrganCategoryPrototype>> parents)
    {
        parents = [];
        if (relationships == null)
            return initialOrgans.Count == 1;

        foreach (var (parent, children) in relationships)
        {
            if (!initialOrgans.ContainsKey(parent))
                return false;
            foreach (var child in children)
            {
                if (!initialOrgans.ContainsKey(child) || !parents.TryAdd(child, parent))
                    return false;
            }
        }

        // A canonical body is one connected acyclic tree. Rejecting malformed prototype graphs here keeps
        // the trusted reset transactional and prevents depth walks or OrganRelation recursion from cycling.
        var parentMap = parents;
        if (parentMap.Count != initialOrgans.Count - 1 ||
            initialOrgans.Keys.Count(category => !parentMap.ContainsKey(category)) != 1)
        {
            return false;
        }

        foreach (var start in initialOrgans.Keys)
        {
            var visited = new HashSet<ProtoId<OrganCategoryPrototype>> { start };
            var current = start;
            while (parentMap.TryGetValue(current, out var parent))
            {
                if (!visited.Add(parent))
                    return false;
                current = parent;
            }
        }

        return true;
    }

    private static int GetDepth(
        ProtoId<OrganCategoryPrototype> category,
        IReadOnlyDictionary<ProtoId<OrganCategoryPrototype>, ProtoId<OrganCategoryPrototype>> parents)
    {
        var depth = 0;
        while (parents.TryGetValue(category, out category) && depth <= MaximumOrganCount)
            depth++;
        return depth;
    }

    private void RollBackSpawned(IEnumerable<EntityUid> spawned)
    {
        foreach (var organ in spawned.Reverse())
        {
            if (Exists(organ))
                Del(organ);
        }
    }

    private void ResetOrgan(EntityUid organ)
    {
        RemComp<NCSurgerySessionComponent>(organ);
        if (TryComp<NCOrganIntegrityComponent>(organ, out var integrity))
        {
            integrity.CurrentIntegrity = MathF.Max(0f, integrity.MaxIntegrity);
            integrity.FunctionalEfficiency = integrity.MaxIntegrity > 0f ? 1f : 0f;
            integrity.Necrosis = 0f;
        }

        if (TryComp<NCOrganFunctionComponent>(organ, out var function))
        {
            function.CurrentCapacity = MathF.Max(0f, function.BaseCapacity);
            function.Necrosis = 0f;
        }

        if (TryComp<NCBoneComponent>(organ, out var bone))
        {
            bone.State = NCBoneState.Intact;
            bone.Stability = 1f;
            bone.Displacement = 0f;
            bone.OpenFracture = false;
            bone.SecondaryDamageRisk = 0f;
            bone.RequiresFixation = false;
            bone.Splinted = false;
            bone.SplintedAt = TimeSpan.Zero;
        }

        if (TryComp<NCTendonComponent>(organ, out var tendon))
        {
            tendon.Integrity = 1f;
            tendon.Ruptured = false;
        }

        if (TryComp<NCJointComponent>(organ, out var joint))
        {
            joint.Stability = 1f;
            joint.Dislocated = false;
            joint.MovementRestriction = 0f;
            joint.Reduced = false;
        }

        if (TryComp<NCNerveComponent>(organ, out var nerve))
        {
            nerve.NeuralIntegrity = 1f;
            nerve.Sensitivity = 1f;
            nerve.MotorFunction = 1f;
        }

        if (TryComp<NCSurgicalSiteComponent>(organ, out var site))
        {
            site.AccessDepth = NCSurgicalAccessDepth.Closed;
            site.BleedingControlled = false;
            site.BoneCasingOpen = false;
            site.TissuesRetracted = false;
            site.Contamination = 0f;
            site.ActiveProcedure = null;
            site.TemporaryDevices.Clear();
        }
    }
}
