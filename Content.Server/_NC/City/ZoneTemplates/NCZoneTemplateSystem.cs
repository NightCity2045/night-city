// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Diagnostics;
using Content.Server.GameTicking;
using Content.Shared._NC.City.ZoneTemplates;
using Content.Shared._NC.City.ZoneTemplates.Components;
using Content.Shared._NC.City.ZoneTemplates.Prototypes;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared.GameTicking;
using Robust.Server.GameObjects;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.City.ZoneTemplates;

/// <summary>
/// Server-authoritative, budgeted deployment engine shared by apartments and physical city missions.
/// It processes only explicit jobs and component indices; it never scans every entity from Update().
/// </summary>
public sealed partial class NCZoneTemplateSystem : EntitySystem
{
    private static readonly ProtoId<NCZoneTemplateBudgetPrototype> DefaultBudget = "Default";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private MapLoaderSystem _loader = default!;
    [Dependency] private MapSystem _maps = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private Zones.NCZoneSystem _zones = default!;
    [Dependency] private NCZoneOccupancySystem _occupancy = default!;

    private readonly Dictionary<ProtoId<NCZoneTemplatePrototype>, CachedTemplate> _cache = [];
    private readonly Queue<TemplateCacheJob> _cacheQueue = [];
    private readonly HashSet<ProtoId<NCZoneTemplatePrototype>> _cacheQueued = [];
    private readonly Dictionary<NCZoneDeploymentId, DeploymentJob> _deployments = [];
    private readonly List<NCZoneDeploymentId> _deploymentOrder = [];
    private readonly Dictionary<NCZoneId, NCZoneDeploymentId> _zoneLocks = [];
    private readonly Dictionary<NCZoneDeploymentId, HashSet<EntityUid>> _deploymentEntities = [];
    private readonly Dictionary<NCZoneId, List<BaselineTile>> _zoneBaselines = [];
    private readonly List<(Vector2i GridIndices, Tile Tile)> _tileBatch = [];

    private EntityUid _storageMapUid = EntityUid.Invalid;
    private MapId _storageMapId = MapId.Nullspace;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PostGameMapLoad>(OnPostGameMapLoad);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        SubscribeLocalEvent<NCZoneDeploymentComponent, ComponentStartup>(OnDeploymentEntityStartup);
        SubscribeLocalEvent<NCZoneDeploymentComponent, ComponentShutdown>(OnDeploymentEntityShutdown);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_prototypes.TryIndex(DefaultBudget, out var budget))
            return;

        var stopwatch = Stopwatch.StartNew();
        ProcessCacheQueue(budget, stopwatch);
        ProcessDeployments(budget, stopwatch);
    }

    private void OnPostGameMapLoad(PostGameMapLoad ev)
    {
        EnsureStorageMap();

        foreach (var template in _prototypes.EnumeratePrototypes<NCZoneTemplatePrototype>())
        {
            if (template.CachePolicy is NCZoneTemplateCachePolicy.Always or NCZoneTemplateCachePolicy.Warm)
                QueueTemplateCache(template);
        }
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        Log.Info($"NCZoneTemplate round restart cleanup started for {_deployments.Count} deployment(s).");

        foreach (var deployment in _deployments.Values)
            DeleteDeploymentEntities(deployment.Id);

        _deployments.Clear();
        _deploymentOrder.Clear();
        _zoneLocks.Clear();
        _deploymentEntities.Clear();
        _zoneBaselines.Clear();
        _cache.Clear();
        _cacheQueue.Clear();
        _cacheQueued.Clear();
        _preparedEntitySnapshots.Clear();

        if (Exists(_storageMapUid))
            Del(_storageMapUid);

        _storageMapUid = EntityUid.Invalid;
        _storageMapId = MapId.Nullspace;

        Log.Info("NCZoneTemplate round restart cleanup finished.");
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (!args.WasModified<NCZoneTemplatePrototype>())
            return;

        // Active jobs keep their leased immutable instance. Only unused prepared instances are rebuilt.
        ClearPreparedTemplateCache();
        foreach (var template in _prototypes.EnumeratePrototypes<NCZoneTemplatePrototype>())
        {
            if (template.CachePolicy is NCZoneTemplateCachePolicy.Always or NCZoneTemplateCachePolicy.Warm)
                QueueTemplateCache(template);
        }
    }

    private void OnDeploymentEntityStartup(Entity<NCZoneDeploymentComponent> entity, ref ComponentStartup args)
    {
        var id = new NCZoneDeploymentId(entity.Comp.DeploymentId);
        if (!_deploymentEntities.TryGetValue(id, out var entities))
            _deploymentEntities[id] = entities = [];
        entities.Add(entity.Owner);
    }

    private void OnDeploymentEntityShutdown(Entity<NCZoneDeploymentComponent> entity, ref ComponentShutdown args)
    {
        var id = new NCZoneDeploymentId(entity.Comp.DeploymentId);
        if (!_deploymentEntities.TryGetValue(id, out var entities))
            return;

        entities.Remove(entity.Owner);
        if (entities.Count == 0)
            _deploymentEntities.Remove(id);
    }

    /// <summary>
    /// Copies the bounded entity set owned by one deployment. Callers never scan the global entity collection.
    /// </summary>
    public bool CollectDeploymentEntities(NCZoneDeploymentId id, HashSet<EntityUid> result)
    {
        result.Clear();
        if (!_deploymentEntities.TryGetValue(id, out var entities))
            return false;

        foreach (var uid in entities)
        {
            if (Exists(uid))
                result.Add(uid);
        }

        return true;
    }

    private void EnsureStorageMap()
    {
        if (Exists(_storageMapUid))
            return;

        _storageMapUid = _maps.CreateMap(out _storageMapId, runMapInit: false);
        _maps.SetPaused(_storageMapId, true);
    }

    private bool IsTimeBudgetAvailable(NCZoneTemplateBudgetPrototype budget, Stopwatch stopwatch)
    {
        return stopwatch.Elapsed.TotalMilliseconds < budget.MaxDeploymentMillisecondsPerTick;
    }
}

/// <summary>
/// Raised after a deployment changes state so apartment, mission, persistence and admin systems can react.
/// </summary>
[ByRefEvent]
public readonly record struct NCZoneDeploymentStateChangedEvent(
    NCZoneDeploymentId DeploymentId,
    NCZoneId ZoneId,
    NCZoneDeploymentState OldState,
    NCZoneDeploymentState NewState,
    string? Error);
