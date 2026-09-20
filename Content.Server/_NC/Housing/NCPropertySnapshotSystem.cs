// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Server._NC.City.ZoneTemplates;
using Content.Server._NC.Housing.Components;
using Content.Server._NC.LivingCity.Housing;
using Content.Server._NC.Persistence.RoundSnapshot;
using Content.Shared._NC.City.ZoneTemplates;
using Content.Shared._NC.City.ZoneTemplates.Components;
using Content.Shared._NC.City.ZoneTemplates.Prototypes;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Prototypes;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.Housing.Prototypes;
using Content.Shared._NC.Housing;
using Content.Shared._NC.LivingCity.Housing;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Tag;
using Robust.Server.GameObjects;
using Robust.Shared.Asynchronous;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Housing;

/// <summary>
/// Captures and restores bounded apartment entity graphs without ever scanning the entire world.
/// Snapshot YAML is parsed on the paused template storage map, never on the live deployment hot path.
/// </summary>
public sealed partial class NCPropertySnapshotSystem : EntitySystem
{
    private const int SnapshotFormatVersion = 1;
    private static readonly ProtoId<NCPropertySnapshotPolicyPrototype> DefaultPolicy = "Default";
    private static readonly ProtoId<TagPrototype> WallTag = "Wall";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCPropertyContentSnapshotSystem _content = default!;
    [Dependency] private NCZoneTemplateSystem _templates = default!;
    [Dependency] private City.Zones.NCZoneSystem _zones = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private MapLoaderSystem _loader = default!;
    [Dependency] private MapSystem _maps = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private TagSystem _tags = default!;
    [Dependency] private ITaskManager _taskManager = default!;

    private readonly Dictionary<NCApartmentId, NCPropertyContentSnapshot> _snapshots = [];
    private readonly List<NCZoneGeometryInfo> _geometry = [];
    private readonly HashSet<EntityUid> _candidates = [];
    private readonly HashSet<EntityUid> _roots = [];
    private readonly HashSet<EntityUid> _graph = [];

    private bool _capturing;

    public bool IsReady { get; private set; }

    public override void Initialize()
    {
        base.Initialize();
        _loader.OnIsSerializable += OnIsSerializable;
    }

    public override void Shutdown()
    {
        _loader.OnIsSerializable -= OnIsSerializable;
        base.Shutdown();
    }

    public void BeginRoundLoad()
    {
        IsReady = false;
    }

    /// <summary>
    /// Loads durable snapshots after any previous round's writes have completed and decorates prepared templates.
    /// </summary>
    public async Task LoadPreparedSnapshotsAsync(IReadOnlyCollection<NCPropertyAggregateRecord> properties)
    {
        try
        {
            var loaded = new Dictionary<NCApartmentId, NCPropertyContentSnapshot>();
            foreach (var property in properties)
            {
                var content = await _content.GetLatestRoundSnapshotAsync(property.Snapshot.PropertyId);
                if (content?.Snapshot.Present == true)
                    loaded[property.Snapshot.PropertyId] = content.Snapshot;
            }

            // Database continuations run on worker threads. Cache/entity changes must be applied on the Robust main
            // thread because rebuilding a prepared template can delete and create ECS entities.
            await RunOnMainThreadAsync(() => ApplyPreparedSnapshots(properties, loaded));
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to prepare persistent apartment snapshots: {exception}");
        }
    }

    private void ApplyPreparedSnapshots(
        IReadOnlyCollection<NCPropertyAggregateRecord> properties,
        IReadOnlyDictionary<NCApartmentId, NCPropertyContentSnapshot> loaded)
    {
        _snapshots.Clear();
        foreach (var (propertyId, snapshot) in loaded)
            _snapshots[propertyId] = snapshot;

        var prepared = new Dictionary<ProtoId<NCZoneTemplatePrototype>, NCZoneTemplateEntitySnapshot>();
        foreach (var record in properties)
        {
            var property = record.Snapshot;
            if (property.CurrentTemplateId is not { } templateId ||
                property.CurrentTemplateRevision is not { } revision ||
                !loaded.TryGetValue(property.PropertyId, out var snapshot) ||
                snapshot.FormatVersion != SnapshotFormatVersion ||
                snapshot.TemplateId != templateId ||
                snapshot.TemplateRevision != revision ||
                !_prototypes.TryIndex<NCZoneTemplatePrototype>(templateId, out var template) ||
                template.TargetZoneId != property.PropertyId.ZoneId ||
                !TryReadDocument(snapshot.Payload, out var document))
            {
                continue;
            }

            var floors = document.Floors.ToDictionary(
                floor => floor.RelativeZ,
                floor => new NCZoneTemplateEntitySnapshotFloor(
                    floor.RelativeZ,
                    floor.SavedLocalOriginX,
                    floor.SavedLocalOriginY,
                    floor.EntityYaml));
            prepared[new ProtoId<NCZoneTemplatePrototype>(templateId)] =
                new NCZoneTemplateEntitySnapshot(revision, floors);
        }

        _templates.ReplacePreparedEntitySnapshots(prepared);
        IsReady = true;
    }

    /// <summary>
    /// Adds apartment graphs to the common round snapshot without issuing per-property database commands.
    /// </summary>
    public void CaptureRoundSnapshotSections(
        IEnumerable<NCPropertySnapshotCapture> captures,
        NCRoundSnapshotCaptureEvent collector)
    {
        foreach (var capture in captures)
        {
            var property = capture.Property;
            if (property.CurrentTemplateId == null || property.CurrentTemplateRevision == null ||
                !IsActiveDeployment(capture.DeploymentId))
            {
                continue;
            }

            if (!TryCapture(property, out var captured, out var error))
            {
                collector.Fail($"Property {property.PropertyId} snapshot failed: {error}");
                continue;
            }

            var snapshot = captured with
            {
                CapturedAt = collector.ActiveWorldTime,
                DataVersion = 1,
                LastAppliedEventId = collector.SnapshotId,
            };
            collector.TryAddSection(
                NCPropertyContentSnapshotSystem.RoundSnapshotSectionType,
                NCPropertyContentSnapshotSystem.RoundSnapshotOwnerType,
                property.PropertyId.Value.ToString("D"),
                SnapshotFormatVersion,
                snapshot.EntityCount,
                JsonSerializer.Serialize(snapshot));
        }
    }

    /// <summary>
    /// Captures one live property before a mid-round legal unload or renovation. The bounded zone query runs on the
    /// main thread; only the immutable payload is persisted asynchronously.
    /// </summary>
    public async Task<bool> CapturePropertySnapshotAsync(NCPropertyAggregateSnapshot property)
    {
        if (!TryCapture(property, out var snapshot, out var error))
        {
            Log.Error($"Property {property.PropertyId} snapshot was not captured: {error}");
            return false;
        }
        if (!await _content.UpsertAsync(snapshot, "housing-live-content-capture"))
            return false;
        var persisted = await _content.GetAsync(property.PropertyId, true);
        if (persisted?.Snapshot.Present != true)
            return false;
        _snapshots[property.PropertyId] = persisted.Snapshot;
        return true;
    }

    public bool HasResidentItems(NCPropertyAggregateSnapshot property)
    {
        if (IsActiveDeployment(property.ActiveDeploymentId))
        {
            // A failed bounded scan is treated conservatively so a sale can never delete unknown live content.
            if (!TryFindLiveResidentItems(property, out var found))
                return true;
            return found;
        }

        return _snapshots.TryGetValue(property.PropertyId, out var snapshot) &&
               snapshot.ResidentEntityCount > 0;
    }

    public void ForgetSnapshot(NCPropertyAggregateSnapshot property)
    {
        _snapshots.Remove(property.PropertyId);
        if (property.CurrentTemplateId is { } templateId)
            _templates.RemovePreparedEntitySnapshot(new ProtoId<NCZoneTemplatePrototype>(templateId));
    }

    public async Task DeleteSnapshotAsync(NCPropertyAggregateSnapshot property)
    {
        ForgetSnapshot(property);
        if (!await _content.DeleteAsync(property.PropertyId, "housing-content-delete"))
            Log.Error($"Canonical content snapshot for property {property.PropertyId} could not be deleted.");
    }

    /// <summary>
    /// Deletes renter-owned contents from a live apartment after a completed non-payment eviction.
    /// The bounded zone lookup preserves the permanent shell, mapper deployment and all living actors/mobs.
    /// </summary>
    public void ConfiscateLiveContents(NCPropertyAggregateSnapshot property)
    {
        if (property.CurrentTemplateId is not { } templateId ||
            !_prototypes.TryIndex<NCZoneTemplatePrototype>(templateId, out var template) ||
            !TryBuildFloorContexts(property.PropertyId.ZoneId, template, out var floors, out _))
            return;

        var confiscated = new HashSet<EntityUid>();
        foreach (var floor in floors)
        {
            CollectFloorRoots(property.PropertyId, floor, markResidents: false);
            foreach (var root in _roots)
                CollectConfiscatedRoots(root, ancestorConfiscated: false, confiscated);
        }

        foreach (var uid in confiscated)
        {
            if (Exists(uid))
                Del(uid);
        }
    }

    private Task RunOnMainThreadAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _taskManager.RunOnMainThread(() =>
        {
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        return completion.Task;
    }

    private bool TryCapture(
        NCPropertyAggregateSnapshot property,
        out NCPropertyContentSnapshot snapshot,
        out string error)
    {
        snapshot = default!;
        error = string.Empty;
        if (!_prototypes.TryIndex(DefaultPolicy, out var policy) ||
            property.CurrentTemplateId is not { } templateId ||
            property.CurrentTemplateRevision is not { } revision ||
            !_prototypes.TryIndex<NCZoneTemplatePrototype>(templateId, out var template) ||
            template.Revision != revision)
        {
            error = "Snapshot policy or selected template is invalid.";
            return false;
        }

        if (!TryBuildFloorContexts(property.PropertyId.ZoneId, template, out var floors, out error))
            return false;

        var documents = new List<SnapshotFloorDocument>(floors.Count);
        var rootCount = 0;
        var entityCount = 0;
        var residentCount = 0;
        var fixtureCount = 0;
        var includedCount = 0;
        var personalCount = 0;
        _capturing = true;
        try
        {
            foreach (var floor in floors)
            {
                CollectFloorRoots(property.PropertyId, floor, markResidents: true);
                rootCount += _roots.Count;
                if (rootCount > policy.MaxRootEntities)
                {
                    error = $"Root entity limit {policy.MaxRootEntities} was exceeded.";
                    return false;
                }

                _graph.Clear();
                foreach (var root in _roots)
                    CollectSerializableTree(root, property.PropertyId, _graph, ref residentCount,
                        ref fixtureCount, ref includedCount, ref personalCount);
                entityCount += _graph.Count;
                if (entityCount > policy.MaxTotalEntities)
                {
                    error = $"Entity limit {policy.MaxTotalEntities} was exceeded.";
                    return false;
                }

                var yaml = string.Empty;
                if (_roots.Count != 0)
                {
                    using var writer = new StringWriter();
                    var options = SerializationOptions.Default with
                    {
                        MissingEntityBehaviour = MissingEntityBehaviour.Ignore,
                        ErrorOnOrphan = false,
                        LogAutoInclude = null,
                    };
                    if (!_loader.TrySaveGeneric(_roots, writer, out _, options))
                    {
                        error = $"Entity serialization failed at relative Z={floor.RelativeZ}.";
                        return false;
                    }
                    yaml = writer.ToString();
                }

                documents.Add(new SnapshotFloorDocument(
                    floor.RelativeZ,
                    floor.LocalOrigin.X,
                    floor.LocalOrigin.Y,
                    yaml));
            }
        }
        finally
        {
            _capturing = false;
        }

        var payload = JsonSerializer.Serialize(new SnapshotDocument(SnapshotFormatVersion, documents));
        if (Encoding.UTF8.GetByteCount(payload) > policy.MaxPayloadBytes)
        {
            error = $"Payload limit {policy.MaxPayloadBytes} bytes was exceeded.";
            return false;
        }

        snapshot = new NCPropertyContentSnapshot(
            property.PropertyId,
            true,
            templateId,
            revision,
            SnapshotFormatVersion,
            payload,
            entityCount,
            residentCount,
            fixtureCount,
            includedCount,
            personalCount,
            default,
            0,
            Guid.Empty);
        return true;
    }

    private bool TryFindLiveResidentItems(NCPropertyAggregateSnapshot property, out bool found)
    {
        found = false;
        if (property.CurrentTemplateId is not { } templateId ||
            !_prototypes.TryIndex<NCZoneTemplatePrototype>(templateId, out var template) ||
            !TryBuildFloorContexts(property.PropertyId.ZoneId, template, out var floors, out _))
            return false;

        foreach (var floor in floors)
        {
            CollectFloorRoots(property.PropertyId, floor, markResidents: false);
            foreach (var root in _roots)
            {
                if (TreeHasResidentContent(root))
                {
                    found = true;
                    return true;
                }
            }
        }

        return true;
    }

    private bool TreeHasResidentContent(EntityUid root)
    {
        var pending = new Queue<EntityUid>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var uid))
        {
            if (!Exists(uid) || IsExcludedSubtree(uid))
                continue;
            if ((TryComp<NCPropertyResidentItemComponent>(uid, out var resident) &&
                 resident.AssetClass == NCPropertyAssetClass.PersonalProperty) ||
                !HasComp<NCZoneDeploymentComponent>(uid))
                return true;

            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                pending.Enqueue(child);
        }
        return false;
    }

    private void CollectConfiscatedRoots(
        EntityUid uid,
        bool ancestorConfiscated,
        HashSet<EntityUid> result)
    {
        if (!Exists(uid) || IsExcludedSubtree(uid))
            return;

        var residentContent = (TryComp<NCPropertyResidentItemComponent>(uid, out var resident) &&
                               resident.AssetClass == NCPropertyAssetClass.PersonalProperty) ||
                              !HasComp<NCZoneDeploymentComponent>(uid);
        if (residentContent && !ancestorConfiscated)
        {
            result.Add(uid);
            return;
        }

        var children = Transform(uid).ChildEnumerator;
        while (children.MoveNext(out var child))
            CollectConfiscatedRoots(child, ancestorConfiscated || residentContent, result);
    }

    private void CollectSerializableTree(
        EntityUid root,
        NCApartmentId propertyId,
        HashSet<EntityUid> result,
        ref int residentCount,
        ref int fixtureCount,
        ref int includedCount,
        ref int personalCount)
    {
        if (!Exists(root) || IsExcludedSubtree(root))
            return;

        result.Add(root);
        if (!HasComp<NCZoneDeploymentComponent>(root))
        {
            var resident = EnsureComp<NCPropertyResidentItemComponent>(root);
            resident.PropertyId = propertyId.ZoneId;
        }
        if (HasComp<NCPropertyResidentItemComponent>(root))
        {
            residentCount++;
            var classification = Comp<NCPropertyResidentItemComponent>(root).AssetClass;
            if (classification == NCPropertyAssetClass.IncludedAsset)
                includedCount++;
            else
                personalCount++;
        }
        else
            fixtureCount++;

        var children = Transform(root).ChildEnumerator;
        while (children.MoveNext(out var child))
            CollectSerializableTree(child, propertyId, result, ref residentCount, ref fixtureCount,
                ref includedCount, ref personalCount);
    }

    private void CollectFloorRoots(NCApartmentId propertyId, FloorContext floor, bool markResidents)
    {
        _roots.Clear();
        _candidates.Clear();
        _lookup.GetEntitiesIntersecting(floor.GridUid, floor.LocalBounds, _candidates, LookupFlags.Uncontained);
        foreach (var uid in _candidates)
        {
            if (!Exists(uid) || Transform(uid).ParentUid != floor.GridUid || IsExcludedSubtree(uid) ||
                !_coordinates.TryGetCoordinates(uid, out var coordinates) ||
                !_zones.Contains(propertyId.ZoneId, coordinates) || IsPermanentShell(uid))
                continue;

            if (markResidents && !HasComp<NCZoneDeploymentComponent>(uid))
            {
                var resident = EnsureComp<NCPropertyResidentItemComponent>(uid);
                resident.PropertyId = propertyId.ZoneId;
            }
            _roots.Add(uid);
        }
    }

    private bool TryBuildFloorContexts(
        NCZoneId zoneId,
        NCZoneTemplatePrototype template,
        out List<FloorContext> contexts,
        out string error)
    {
        contexts = [];
        error = string.Empty;
        if (!_zones.TryGetZoneNetwork(zoneId, out var networkId) || !_zones.GetGeometry(zoneId, _geometry))
        {
            error = "Zone geometry is unavailable.";
            return false;
        }

        var zLevels = new SortedSet<int>();
        foreach (var fragment in _geometry)
        {
            switch (fragment.Kind)
            {
                case NCZoneGeometryKind.Polygon when !fragment.Global:
                    zLevels.Add(fragment.Z);
                    break;
                case NCZoneGeometryKind.Volume:
                    for (var z = fragment.MinZ; z <= fragment.MaxZ; z++)
                        zLevels.Add(z);
                    break;
                case NCZoneGeometryKind.TileMask:
                    foreach (var chunk in fragment.Chunks)
                        zLevels.Add(chunk.Z);
                    break;
            }
        }

        var templateFloors = template.Floors.OrderBy(value => value.RelativeZ).ToArray();
        if (zLevels.Count != templateFloors.Length)
        {
            error = "Zone and template floor counts do not match.";
            return false;
        }

        var targetLevels = zLevels.ToArray();
        for (var i = 0; i < targetLevels.Length; i++)
        {
            if (!TryBuildFloorContext(zoneId, networkId, targetLevels[i], templateFloors[i].RelativeZ,
                    out var context, out error))
                return false;
            contexts.Add(context);
        }
        return true;
    }

    private bool TryBuildFloorContext(
        NCZoneId zoneId,
        NCZNetworkId networkId,
        int z,
        int relativeZ,
        out FloorContext context,
        out string error)
    {
        context = default;
        error = string.Empty;
        if (!_coordinates.TryGetMap(networkId, z, out var mapUid))
        {
            error = $"Target map Z={z} is unavailable.";
            return false;
        }

        var footprint = BuildFootprint(zoneId, networkId, z);
        if (footprint.Count == 0)
        {
            error = $"Zone footprint Z={z} is empty.";
            return false;
        }

        var min = new Vector2i(footprint.Min(value => value.X), footprint.Min(value => value.Y));
        var max = new Vector2i(footprint.Max(value => value.X) + 1, footprint.Max(value => value.Y) + 1);
        var center = new Vector2(min.X + 0.5f, min.Y + 0.5f);
        if (!_maps.TryFindGridAt(mapUid, center, out var gridUid, out MapGridComponent? _))
        {
            error = $"Zone footprint Z={z} has no target grid.";
            return false;
        }

        var mapId = Transform(mapUid).MapID;
        var localMin = _transform.ToCoordinates((gridUid, null), new MapCoordinates(min, mapId)).Position;
        var localMax = _transform.ToCoordinates((gridUid, null), new MapCoordinates(max, mapId)).Position;
        var bounds = new Box2(Vector2.Min(localMin, localMax), Vector2.Max(localMin, localMax));
        context = new FloorContext(relativeZ, gridUid, localMin, bounds);
        return true;
    }

    private HashSet<Vector2i> BuildFootprint(NCZoneId zoneId, NCZNetworkId networkId, int z)
    {
        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;
        foreach (var fragment in _geometry)
        {
            var applies = fragment.Kind switch
            {
                NCZoneGeometryKind.Polygon => !fragment.Global && fragment.Z == z,
                NCZoneGeometryKind.Volume => z >= fragment.MinZ && z <= fragment.MaxZ,
                NCZoneGeometryKind.TileMask => fragment.Chunks.Any(chunk => chunk.Z == z),
                _ => false,
            };
            if (!applies)
                continue;

            if (fragment.Kind == NCZoneGeometryKind.TileMask)
            {
                foreach (var chunk in fragment.Chunks.Where(value => value.Z == z))
                {
                    for (var row = 0; row < chunk.Rows.Count; row++)
                    {
                        var bits = chunk.Rows[row];
                        for (var column = 0; column < NCTileMaskChunk.Size; column++)
                        {
                            if ((bits & (1UL << column)) == 0)
                                continue;
                            var x = chunk.Origin.X + column;
                            var y = chunk.Origin.Y + row;
                            minX = Math.Min(minX, x);
                            minY = Math.Min(minY, y);
                            maxX = Math.Max(maxX, x);
                            maxY = Math.Max(maxY, y);
                        }
                    }
                }
                continue;
            }

            foreach (var vertex in fragment.Vertices)
            {
                minX = Math.Min(minX, (int) MathF.Floor(vertex.X));
                minY = Math.Min(minY, (int) MathF.Floor(vertex.Y));
                maxX = Math.Max(maxX, (int) MathF.Ceiling(vertex.X) - 1);
                maxY = Math.Max(maxY, (int) MathF.Ceiling(vertex.Y) - 1);
            }
        }

        var footprint = new HashSet<Vector2i>();
        if (minX > maxX || minY > maxY)
            return footprint;
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                if (_zones.Contains(zoneId, new NCMapCoordinates(networkId, new Vector2(x + 0.5f, y + 0.5f), z)))
                    footprint.Add(new Vector2i(x, y));
            }
        }
        return footprint;
    }

    private bool IsActiveDeployment(NCZoneDeploymentId deploymentId)
    {
        return deploymentId.IsValid && _templates.TryGetDeployment(deploymentId, out var deployment) &&
               deployment.State == NCZoneDeploymentState.Active;
    }

    private bool IsExcludedSubtree(EntityUid uid)
    {
        return HasComp<ActorComponent>(uid) ||
               TryComp(uid, out MobStateComponent? mob) && mob.CurrentState != MobState.Dead;
    }

    private bool IsPermanentShell(EntityUid uid)
    {
        if (HasComp<NCZoneDeploymentComponent>(uid))
            return false;
        return HasComp<Content.Shared._NC.Housing.Components.NCPropertyLockComponent>(uid) ||
               _tags.HasTag(uid, WallTag);
    }

    private void OnIsSerializable(Entity<MetaDataComponent> entity, ref bool serializable)
    {
        if (!_capturing || !serializable)
            return;
        if (IsExcludedSubtree(entity.Owner) || IsPermanentShell(entity.Owner))
            serializable = false;
    }

    private static bool TryReadDocument(string payload, out SnapshotDocument document)
    {
        try
        {
            document = JsonSerializer.Deserialize<SnapshotDocument>(payload)!;
            return document is { Version: SnapshotFormatVersion, Floors: not null };
        }
        catch (JsonException)
        {
            document = default!;
            return false;
        }
    }

    private readonly record struct FloorContext(
        int RelativeZ,
        EntityUid GridUid,
        Vector2 LocalOrigin,
        Box2 LocalBounds);

    private sealed record SnapshotDocument(int Version, List<SnapshotFloorDocument> Floors);

    private sealed record SnapshotFloorDocument(
        int RelativeZ,
        float SavedLocalOriginX,
        float SavedLocalOriginY,
        string EntityYaml);
}

/// <summary>
/// Couples persistent property metadata with the live round deployment that actually owns its materialized interior.
/// </summary>
public readonly record struct NCPropertySnapshotCapture(
    NCPropertyAggregateSnapshot Property,
    NCZoneDeploymentId DeploymentId);
