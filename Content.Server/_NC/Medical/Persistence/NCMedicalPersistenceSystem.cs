// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using Content.Server._NC.Medical.Anatomy.Detachment;
using Content.Server._NC.Medical.Bleeding;
using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Medical.Pharmacology;
using Content.Server._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Persistence;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared._NC.Medical.Systemic;
using Content.Shared.Body;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Persistence;

/// <summary>
/// Converts the live authoritative human medical graph to and from a bounded semantic snapshot.
/// Persistence never stores runtime entity identity: organ EntityUids are resolved only while applying
/// category-keyed state to the target body's own materialized graph.
/// </summary>
public sealed partial class NCMedicalPersistenceSystem : EntitySystem
{
    public const int MaxOrganRecords = 64;
    public const int MaxCyberwareRecords = 64;
    public const int MaxMissingOrganCategories = 64;
    public const int MaxWoundRecords = 64;
    public const int MaxScarRecords = 128;
    public const int MaxOverflowZones = 32;
    public const int MaxBleedingSources = 128;
    public const int MaxTourniquets = 16;
    public const int MaxTemporaryDevicesPerSite = 16;
    public const int MaxIncisionsPerSite = 64;
    public const int MaxIncisionPoints = 32;
    public const int MaxPersistentStringLength = 64;
    public const int MaxPersistentPrototypeIdLength = 128;
    public const int MaxStableOwnerKeyLength = 128;

    [Dependency] private IComponentFactory _componentFactory = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private OrganRelationSystem _relations = default!;
    [Dependency] private NCOrganDetachmentSystem _detachment = default!;
    [Dependency] private NCCirculationBloodSystem _circulationBlood = default!;
    [Dependency] private NCCyberwareSystem _cyberware = default!;
    [Dependency] private NCPharmacologySystem _pharmacology = default!;
    [Dependency] private NCMedicalBodySystem _medicalBodies = default!;
    [Dependency] private NCPhysiologySchedulerSystem _scheduler = default!;
    [Dependency] private IGameTiming _timing = default!;

    /// <summary>
    /// Captures one body's semantic medical state. Null means the body or its live organ graph is invalid
    /// for the current persistence version. The first capture may assign missing stable organ provenance;
    /// no physiological state is changed.
    /// </summary>
    public NCMedicalStateSnapshot? SerializeMedicalState(EntityUid character, bool allowTerminating = false)
    {
        if (!TryGetBodyContext(character, out var medical, out _, out var initial, allowTerminating) ||
            !TryCollectOrganMap(character, initial, out var organs))
        {
            return null;
        }

        var initialOrgans = initial.Organs;
        var expected = initialOrgans.Keys.ToHashSet();
        if (!_pharmacology.TryCapturePersistentState(character, _timing.CurTime, out var pharmacology))
            return null;

        var snapshot = new NCMedicalStateSnapshot
        {
            Version = NCMedicalSnapshotVersions.Current,
            CapturedAt = _timing.CurTime,
            AnatomyProfile = medical.Profile,
            MissingOrganCategories = expected
                .Where(category => !organs.ContainsKey(category))
                .OrderBy(category => category.Id, StringComparer.Ordinal)
                .ToList(),
            Wounds = CaptureWounds(character),
            Bleeding = CaptureBleeding(character),
            Treatment = CaptureTreatment(character),
            EmergencyCare = CaptureEmergencyCare(character),
            Systemic = CaptureSystemic(character),
            Pharmacology = pharmacology,
            Circulation = CaptureCirculation(character),
            Physiology = CapturePhysiology(character),
            CyberwareModules = _cyberware.CaptureInstalledModules(character),
        };

        foreach (var (category, organ) in organs.OrderBy(entry => entry.Key.Id, StringComparer.Ordinal))
            snapshot.Organs.Add(category, CaptureOrgan(character, organ));

        return ValidateCurrentSnapshot(character, snapshot) ? snapshot : null;
    }

    /// <summary>
    /// Validates and migrates the complete snapshot before touching the target. Version, prototype,
    /// cardinality or semantic failures therefore cannot partially mutate a live character.
    /// </summary>
    public bool RestoreMedicalState(EntityUid character, NCMedicalStateSnapshot snapshot)
    {
        if (!TryMigrateSnapshot(snapshot, out var current) ||
            !TryGetBodyContext(character, out var medical, out _, out var initial) ||
            !CompleteLegacyOrganIdentity(initial, current, snapshot.Version) ||
            !ValidateCurrentSnapshot(character, current))
        {
            return false;
        }

        // Graph mutations can fail for physical container reasons. The graph helper rolls back any
        // detach/spawn work before returning false, so semantic component writes only start after success.
        if (!TryMatchOrganGraph(character, initial, current, out var detachedEntities) ||
            !TryCollectOrganMap(character, initial, out var organs))
        {
            return false;
        }

        if (!ApplyOrganState(character, current, organs))
            return false;
        if (!_cyberware.TryRestoreInstalledModules(character, current.CyberwareModules, _timing.CurTime))
            return false;
        ApplyWoundState(character, current.Wounds);
        ApplyBleedingState(character, current.Bleeding);
        ApplyTreatmentState(character, current.Treatment);
        ApplyEmergencyCareState(character, current.EmergencyCare);
        ApplySystemicState(character, current.Systemic);
        if (!_pharmacology.TryRestorePersistentState(character, current.Pharmacology, _timing.CurTime))
            return false;
        ApplyCirculationState(character, current.Circulation);
        _circulationBlood.SynchronizeSolution(character);
        ApplyPhysiologyState(character, current.Physiology);

        if (!_medicalBodies.TryRestoreSnapshotMetadata(
                (character, medical),
                current.AnatomyProfile,
                NCMedicalSnapshotVersions.Current))
        {
            return false;
        }

        // Unsaved sessions and action-deduplication windows never cross a persistence boundary.
        RemComp<NCSurgeryPatientStateComponent>(character);
        RemComp<NCCyberwareBodyStateComponent>(character);
        foreach (var organ in organs.Values)
            RemComp<NCSurgerySessionComponent>(organ);

        _medicalBodies.TryMarkDirty(character, NCMedicalDirtyDomains.All);
        _scheduler.ProcessNow(character, TimeSpan.Zero);

        // These are newly materialized canonical organs removed to reproduce the snapshot's missing
        // subtrees. Separate persisted detached-organ entities, if any, are restored by their own owner.
        foreach (var detached in detachedEntities.Distinct())
        {
            if (Exists(detached))
                QueueDel(detached);
        }

        return true;
    }

    /// <summary>
    /// Explicit migration entry point. Version 1 currently needs only a defensive deep copy; later cases
    /// can transform older semantic records here without exposing runtime entities to the DTO layer.
    /// </summary>
    public bool TryMigrateSnapshot(
        NCMedicalStateSnapshot snapshot,
        out NCMedicalStateSnapshot migrated)
    {
        migrated = default!;
        if (snapshot == null || !HasNonNullSnapshotShape(snapshot))
            return false;

        switch (snapshot.Version)
        {
            case NCMedicalSnapshotVersions.InitialSemanticGraph:
            case NCMedicalSnapshotVersions.StableOrganIdentity:
                migrated = CloneSnapshot(snapshot);
                InitializeLegacyLongitudinalWounds(migrated);
                InitializeLegacySystemicChannels(migrated);
                InitializeLegacyWoundVisualization(migrated);
                migrated.Version = NCMedicalSnapshotVersions.Current;
                return true;
            case NCMedicalSnapshotVersions.LongitudinalWounds:
                migrated = CloneSnapshot(snapshot);
                InitializeLegacySystemicChannels(migrated);
                InitializeLegacyWoundVisualization(migrated);
                migrated.Version = NCMedicalSnapshotVersions.Current;
                return true;
            case NCMedicalSnapshotVersions.CyberwareGraph:
                migrated = CloneSnapshot(snapshot);
                migrated.Pharmacology = null;
                InitializeLegacySystemicChannels(migrated);
                InitializeLegacyWoundVisualization(migrated);
                migrated.Version = NCMedicalSnapshotVersions.Current;
                return true;
            case NCMedicalSnapshotVersions.PharmacologyGraph:
                migrated = CloneSnapshot(snapshot);
                InitializeLegacySystemicChannels(migrated);
                InitializeLegacyWoundVisualization(migrated);
                migrated.Version = NCMedicalSnapshotVersions.Current;
                return true;
            case NCMedicalSnapshotVersions.SystemicSepsis:
                migrated = CloneSnapshot(snapshot);
                InitializeLegacyWoundVisualization(migrated);
                migrated.Version = NCMedicalSnapshotVersions.Current;
                return true;
            case NCMedicalSnapshotVersions.TransplantRejection:
                migrated = CloneSnapshot(snapshot);
                InitializeLegacyWoundVisualization(migrated);
                migrated.Version = NCMedicalSnapshotVersions.Current;
                return true;
            case NCMedicalSnapshotVersions.WoundVisualization:
            case NCMedicalSnapshotVersions.CavityFluidLedger:
                migrated = CloneSnapshot(snapshot);
                migrated.Version = NCMedicalSnapshotVersions.Current;
                return true;
            case NCMedicalSnapshotVersions.Current:
                migrated = CloneSnapshot(snapshot);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Legacy wounds did not preserve an impact point. Stable wound identifiers provide a deterministic
    /// fallback so upgraded characters retain fixed marks instead of reshuffling on every diagnostic scan.
    /// </summary>
    private static void InitializeLegacyWoundVisualization(NCMedicalStateSnapshot snapshot)
    {
        if (snapshot.Wounds is not { } wounds)
            return;

        foreach (var wound in wounds.Records.Values)
            InitializeLegacyWoundVisualization(wound.WoundId, wound);

        foreach (var scar in wounds.Scars)
        {
            var bytes = scar.ScarId.ToByteArray();
            scar.VisualX = bytes[0];
            scar.VisualY = bytes[1];
            scar.VisualDirection = bytes[2];
        }
    }

    private static void InitializeLegacyWoundVisualization(Guid id, NCWoundRecordSnapshot wound)
    {
        var bytes = id.ToByteArray();
        wound.VisualX = bytes[0];
        wound.VisualY = bytes[1];
        wound.VisualDirection = bytes[2];
    }

    /// <summary>
    /// Version 1/2 wounds predate explicit healing baselines and compact scars. Their current untreated
    /// values are the only semantic source of truth, so migration promotes those values without guessing.
    /// </summary>
    private static void InitializeLegacyLongitudinalWounds(NCMedicalStateSnapshot snapshot)
    {
        if (snapshot.Wounds is not { } wounds)
            return;

        wounds.Scars ??= [];
        foreach (var wound in wounds.Records.Values)
        {
            wound.InitialExternalBleedRate = MathF.Max(
                wound.InitialExternalBleedRate,
                wound.ExternalBleedRate);
            wound.InitialInternalBleedRate = MathF.Max(
                wound.InitialInternalBleedRate,
                wound.InternalBleedRate);
            wound.InitialPain = MathF.Max(wound.InitialPain, wound.Pain);
            wound.HealingProgress = Math.Clamp(wound.HealingProgress, 0f, 1f);
            wound.PeakHealingProgress = MathF.Max(
                wound.PeakHealingProgress,
                wound.HealingProgress);
            wound.Age = snapshot.CapturedAt > wound.CreatedAt
                ? snapshot.CapturedAt - wound.CreatedAt
                : TimeSpan.Zero;
        }
    }

    private static void InitializeLegacySystemicChannels(NCMedicalStateSnapshot snapshot)
    {
        if (snapshot.Systemic is not { } systemic)
            return;

        foreach (var channel in Enum.GetValues<NCSystemicChannel>())
            systemic.Burdens.TryAdd(channel, 0f);
    }

    private bool TryGetBodyContext(
        EntityUid character,
        out NCMedicalBodyComponent medical,
        out BodyComponent body,
        out InitialBodyComponent initial,
        bool allowTerminating = false)
    {
        medical = default!;
        body = default!;
        initial = default!;
        if (!Exists(character) ||
            (!allowTerminating && TerminatingOrDeleted(character)) ||
            !TryComp<NCMedicalBodyComponent>(character, out var foundMedical) ||
            !TryComp<BodyComponent>(character, out var foundBody) ||
            foundBody.Organs == null ||
            !TryComp<InitialBodyComponent>(character, out var foundInitial))
        {
            return false;
        }

        var initialOrgans = foundInitial.Organs;
        if (initialOrgans.Count is <= 0 or > MaxOrganRecords)
            return false;

        medical = foundMedical;
        body = foundBody;
        initial = foundInitial;
        return true;
    }

    /// <summary>
    /// Enumerates only one body's bounded organ container and verifies its live category relationships.
    /// No world scan is used by persistence.
    /// </summary>
    private bool TryCollectOrganMap(
        EntityUid character,
        InitialBodyComponent initial,
        out Dictionary<ProtoId<OrganCategoryPrototype>, EntityUid> organs)
    {
        organs = new Dictionary<ProtoId<OrganCategoryPrototype>, EntityUid>();
        if (!TryComp<BodyComponent>(character, out var body) || body.Organs == null)
            return false;

        var initialOrgans = initial.Organs;
        foreach (var organUid in body.Organs.ContainedEntities)
        {
            if (organs.Count >= MaxOrganRecords ||
                !TryComp<OrganComponent>(organUid, out var organ) ||
                organ.Body != character ||
                organ.Category is not { } category ||
                !initialOrgans.ContainsKey(category) ||
                !organs.TryAdd(category, organUid))
            {
                organs.Clear();
                return false;
            }
        }

        if (!TryBuildParentMap(initial, out var parents))
            return false;

        foreach (var category in initialOrgans.Keys)
        {
            if (!organs.TryGetValue(category, out var organUid))
                continue;

            if (!parents.TryGetValue(category, out var parentCategory))
            {
                if (TryComp<ChildOrganComponent>(organUid, out var rootChild) && rootChild.Parent != null)
                    return false;
                continue;
            }

            // A live child beneath a missing parent is not a valid materialized subtree.
            if (!organs.TryGetValue(parentCategory, out var parentUid) ||
                !TryComp<ChildOrganComponent>(organUid, out var child) ||
                child.Parent != parentUid)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryBuildParentMap(
        InitialBodyComponent initial,
        out Dictionary<ProtoId<OrganCategoryPrototype>, ProtoId<OrganCategoryPrototype>> parents)
    {
        parents = new Dictionary<ProtoId<OrganCategoryPrototype>, ProtoId<OrganCategoryPrototype>>();
        var initialOrgans = initial.Organs;
        var relationships = initial.Relationships;
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

        return true;
    }

    private bool TryMatchOrganGraph(
        EntityUid character,
        InitialBodyComponent initial,
        NCMedicalStateSnapshot snapshot,
        out List<EntityUid> detachedEntities)
    {
        detachedEntities = [];
        if (!TryCollectOrganMap(character, initial, out var current) ||
            !TryBuildParentMap(initial, out var parents) ||
            !TryComp<BodyComponent>(character, out var body) ||
            body.Organs == null)
        {
            return false;
        }

        var initialOrgans = initial.Organs;
        var desiredMissing = snapshot.MissingOrganCategories.ToHashSet();
        var replacementCategories = current
            .Where(entry => snapshot.Organs.TryGetValue(entry.Key, out var state) &&
                            !string.Equals(
                                MetaData(entry.Value).EntityPrototype?.ID,
                                state.EntityPrototype,
                                StringComparison.Ordinal))
            .Select(entry => entry.Key)
            .ToHashSet();
        if (replacementCategories.Any(category => !parents.ContainsKey(category)))
            return false;

        var replacementRoots = replacementCategories
            .Where(category => !parents.TryGetValue(category, out var parent) ||
                               !replacementCategories.Contains(parent));
        var detachRoots = desiredMissing
            .Where(category => current.ContainsKey(category) &&
                               (!parents.TryGetValue(category, out var parent) || !desiredMissing.Contains(parent)))
            .Concat(replacementRoots)
            .Distinct()
            .OrderByDescending(category => GetCategoryDepth(category, parents))
            .ThenBy(category => category.Id, StringComparer.Ordinal)
            .ToList();
        var detachedRoots = new List<DetachedRoot>();

        foreach (var rootCategory in detachRoots)
        {
            // Rootless anatomy cannot be represented by an extant character entity.
            if (!parents.TryGetValue(rootCategory, out var parentCategory))
            {
                RollbackGraph(character, detachedRoots, []);
                return false;
            }

            var replacing = replacementCategories.Contains(rootCategory);
            var subtreeUids = current.Keys
                .Where(category => IsDescendantOrSelf(category, rootCategory, parents) &&
                                   (replacing || desiredMissing.Contains(category)))
                .Select(category => current[category])
                .ToList();
            if (!_detachment.TryDetachOrgan(character, rootCategory, out var detachedRoot))
            {
                RollbackGraph(character, detachedRoots, []);
                return false;
            }

            detachedRoots.Add(new DetachedRoot(detachedRoot, parentCategory, subtreeUids));
            detachedEntities.AddRange(subtreeUids);
        }

        if (!TryCollectOrganMap(character, initial, out current) ||
            !_containers.TryGetContainer(character, BodyComponent.ContainerID, out var organContainer))
        {
            RollbackGraph(character, detachedRoots, []);
            detachedEntities.Clear();
            return false;
        }

        var spawned = new List<EntityUid>();
        var desiredLive = initialOrgans.Keys.Where(category => !desiredMissing.Contains(category)).ToHashSet();
        foreach (var category in desiredLive
                     .Where(category => !current.ContainsKey(category))
                     .OrderBy(category => GetCategoryDepth(category, parents))
                     .ThenBy(category => category.Id, StringComparer.Ordinal))
        {
            var organPrototype = snapshot.Organs[category].EntityPrototype;
            var spawnedOrgan = Spawn(organPrototype, new EntityCoordinates(character, Vector2.Zero));
            if (!_containers.Insert(spawnedOrgan, organContainer, containerXform: Transform(character)))
            {
                Del(spawnedOrgan);
                RollbackGraph(character, detachedRoots, spawned);
                detachedEntities.Clear();
                return false;
            }

            spawned.Add(spawnedOrgan);
        }

        if (!TryCollectLooseOrganMap(character, initial, out current))
        {
            RollbackGraph(character, detachedRoots, spawned);
            detachedEntities.Clear();
            return false;
        }

        foreach (var (childCategory, parentCategory) in parents
                     .OrderBy(entry => GetCategoryDepth(entry.Key, parents))
                     .ThenBy(entry => entry.Key.Id, StringComparer.Ordinal))
        {
            if (!desiredLive.Contains(childCategory) || !desiredLive.Contains(parentCategory))
                continue;

            var childUid = current[childCategory];
            var parentUid = current[parentCategory];
            if (!TryComp<ChildOrganComponent>(childUid, out var child) ||
                !TryComp<ParentOrganComponent>(parentUid, out var parent))
            {
                RollbackGraph(character, detachedRoots, spawned);
                detachedEntities.Clear();
                return false;
            }

            if (child.Parent == null)
                _relations.Relate((parentUid, parent), (childUid, child));
            else if (child.Parent != parentUid)
            {
                RollbackGraph(character, detachedRoots, spawned);
                detachedEntities.Clear();
                return false;
            }
        }

        if (!TryCollectOrganMap(character, initial, out var result) ||
            !result.Keys.ToHashSet().SetEquals(desiredLive))
        {
            RollbackGraph(character, detachedRoots, spawned);
            detachedEntities.Clear();
            return false;
        }

        return true;
    }

    /// <summary>
    /// Collects category identity while freshly spawned organs are inserted but not related yet.
    /// Relationship validation is intentionally deferred until all declared edges have been restored.
    /// </summary>
    private bool TryCollectLooseOrganMap(
        EntityUid character,
        InitialBodyComponent initial,
        out Dictionary<ProtoId<OrganCategoryPrototype>, EntityUid> organs)
    {
        organs = new Dictionary<ProtoId<OrganCategoryPrototype>, EntityUid>();
        if (!TryComp<BodyComponent>(character, out var body) || body.Organs == null)
            return false;

        var initialOrgans = initial.Organs;
        foreach (var organUid in body.Organs.ContainedEntities)
        {
            if (organs.Count >= MaxOrganRecords ||
                !TryComp<OrganComponent>(organUid, out var organ) ||
                organ.Body != character ||
                organ.Category is not { } category ||
                !initialOrgans.ContainsKey(category) ||
                !organs.TryAdd(category, organUid))
            {
                organs.Clear();
                return false;
            }
        }

        return true;
    }

    private void RollbackGraph(
        EntityUid character,
        IReadOnlyList<DetachedRoot> detachedRoots,
        IReadOnlyList<EntityUid> spawned)
    {
        foreach (var spawnedOrgan in spawned.AsEnumerable().Reverse())
        {
            if (Exists(spawnedOrgan))
                Del(spawnedOrgan);
        }

        foreach (var detached in detachedRoots.Reverse())
        {
            if (Exists(detached.Root))
                _detachment.TryAttachOrgan(character, detached.ParentCategory, detached.Root);
        }
    }

    private static bool IsDescendantOrSelf(
        ProtoId<OrganCategoryPrototype> category,
        ProtoId<OrganCategoryPrototype> ancestor,
        IReadOnlyDictionary<ProtoId<OrganCategoryPrototype>, ProtoId<OrganCategoryPrototype>> parents)
    {
        var current = category;
        for (var depth = 0; depth <= MaxOrganRecords; depth++)
        {
            if (current == ancestor)
                return true;
            if (!parents.TryGetValue(current, out current))
                return false;
        }

        return false;
    }

    private static int GetCategoryDepth(
        ProtoId<OrganCategoryPrototype> category,
        IReadOnlyDictionary<ProtoId<OrganCategoryPrototype>, ProtoId<OrganCategoryPrototype>> parents)
    {
        var depth = 0;
        while (parents.TryGetValue(category, out category) && depth <= MaxOrganRecords)
            depth++;
        return depth;
    }

    private readonly record struct DetachedRoot(
        EntityUid Root,
        ProtoId<OrganCategoryPrototype> ParentCategory,
        IReadOnlyList<EntityUid> Subtree);

    private static bool CompleteLegacyOrganIdentity(
        InitialBodyComponent initial,
        NCMedicalStateSnapshot snapshot,
        ushort sourceVersion)
    {
        if (sourceVersion != NCMedicalSnapshotVersions.InitialSemanticGraph)
            return true;

        var initialOrgans = initial.Organs;
        foreach (var (category, state) in snapshot.Organs)
        {
            if (string.IsNullOrWhiteSpace(state.EntityPrototype))
            {
                if (!initialOrgans.TryGetValue(category, out var prototype))
                    return false;
                state.EntityPrototype = prototype;
            }

            if (state.StableOrganId == Guid.Empty)
                state.StableOrganId = Guid.NewGuid();
        }

        return true;
    }
}
