// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Content.Shared._NC.City.ZoneTemplates;
using Content.Shared._NC.City.ZoneTemplates.Components;
using Content.Shared._NC.City.ZoneTemplates.Prototypes;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Prototypes;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Identity;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.City.ZoneTemplates;

public sealed partial class NCZoneTemplateSystem
{
    /// <summary>
    /// Queues one idempotent deployment. The caller receives an identity immediately and observes state events.
    /// </summary>
    public bool TryRequestDeployment(
        NCZoneDeploymentRequest request,
        out NCZoneDeploymentId deploymentId,
        out string error)
    {
        deploymentId = request.DeploymentId.IsValid ? request.DeploymentId : NCZoneDeploymentId.New();
        error = string.Empty;

        if (_deployments.TryGetValue(deploymentId, out var existing))
        {
            if (existing.ZoneId == request.TargetZoneId && existing.TemplateId == request.TemplateId)
                return true;

            error = "Deployment identity is already used by another request.";
            return false;
        }

        if (!_zones.TryGetZone(request.TargetZoneId, out var zone))
        {
            error = "Target zone does not exist.";
            return false;
        }

        if (!_prototypes.TryIndex(request.TemplateId, out var template))
        {
            error = "Zone template does not exist.";
            return false;
        }

        if (template.TargetZoneId != request.TargetZoneId)
        {
            error = $"Template {template.ID} is bound to zone {template.TargetZoneId}, not {request.TargetZoneId}.";
            return false;
        }

        if (!template.CompatibleZoneKinds.Contains(zone.Kind))
        {
            error = $"Template {template.ID} is incompatible with zone kind {zone.Kind}.";
            return false;
        }

        if (template.Floors.Count == 0 || template.Floors.Select(floor => floor.RelativeZ).Distinct().Count() != template.Floors.Count)
        {
            error = "Template must contain at least one floor and cannot repeat relative Z values.";
            return false;
        }

        var job = new DeploymentJob(
            deploymentId,
            request.TargetZoneId,
            request.TemplateId,
            template.Revision,
            request.Purpose,
            request.OwnerCharacterId,
            request.DungeonRunId);

        _deployments.Add(deploymentId, job);
        _deploymentOrder.Add(deploymentId);
        QueueTemplateCache(template);
        return true;
    }

    public bool TryGetDeployment(NCZoneDeploymentId id, out NCZoneDeploymentStatus status)
    {
        if (_deployments.TryGetValue(id, out var job))
        {
            status = new NCZoneDeploymentStatus(
                job.Id,
                job.ZoneId,
                job.TemplateId,
                job.TemplateRevision,
                job.Purpose,
                job.State,
                job.Error);
            return true;
        }

        status = default;
        return false;
    }

    public bool TryCloseDeployment(NCZoneDeploymentId id, out string error)
    {
        error = string.Empty;
        if (!_deployments.TryGetValue(id, out var job))
        {
            error = "Deployment does not exist.";
            return false;
        }

        if (job.State != NCZoneDeploymentState.Active)
        {
            error = $"Deployment cannot close from state {job.State}.";
            return false;
        }

        SetState(job, NCZoneDeploymentState.ClosingEntrances);
        return true;
    }

    private void ProcessDeployments(NCZoneTemplateBudgetPrototype budget, Stopwatch stopwatch)
    {
        var remainingTiles = budget.MaxTileChangesPerTick;
        var remainingEntities = budget.MaxEntitySpawnsPerTick;
        var active = 0;

        foreach (var id in _deploymentOrder.ToArray())
        {
            if (active >= budget.MaxParallelDeployments || !IsTimeBudgetAvailable(budget, stopwatch))
                break;
            if (!_deployments.TryGetValue(id, out var job) || IsTerminal(job.State))
                continue;

            active++;
            ProcessDeployment(job, ref remainingTiles, ref remainingEntities, budget, stopwatch);
            if (remainingTiles <= 0 && remainingEntities <= 0)
                break;
        }
    }

    private void ProcessDeployment(
        DeploymentJob job,
        ref int remainingTiles,
        ref int remainingEntities,
        NCZoneTemplateBudgetPrototype budget,
        Stopwatch stopwatch)
    {
        switch (job.State)
        {
            case NCZoneDeploymentState.Pending:
                // Lease the prepared instance exclusively. Its live entities will be moved, not cloned.
                if (_cache.Remove(job.TemplateId, out var cached))
                {
                    job.Template = cached;
                    SetState(job, NCZoneDeploymentState.Validating);

                    // One prepared instance is leased by exactly one deployment. Only queue another copy when
                    // a second request is already waiting, so normal apartment activation performs no map load.
                    if (_deployments.Values.Any(other =>
                            other != job &&
                            other.TemplateId == job.TemplateId &&
                            other.State == NCZoneDeploymentState.Pending) &&
                        _prototypes.TryIndex(job.TemplateId, out var template))
                    {
                        QueueTemplateCache(template);
                    }
                }
                break;
            case NCZoneDeploymentState.Validating:
                if (!TryPrepareDeployment(job, out var validationError))
                {
                    SetState(job, NCZoneDeploymentState.FailedClean, validationError);
                    return;
                }
                SetState(job, NCZoneDeploymentState.LockingZone);
                break;
            case NCZoneDeploymentState.LockingZone:
                if (_zoneLocks.TryGetValue(job.ZoneId, out var lockOwner) && lockOwner != job.Id)
                {
                    if (!_deployments.TryGetValue(lockOwner, out var previous) ||
                        previous.State != NCZoneDeploymentState.Active ||
                        job.Purpose == NCZoneDeploymentPurpose.DungeonRun)
                    {
                        SetState(job, NCZoneDeploymentState.FailedClean, "Target zone is already reserved.");
                        return;
                    }

                    // Apartment/admin replacement is an explicit hand-off. The root baseline remains owned by the zone.
                    DeleteDeploymentEntities(previous.Id);
                    previous.State = NCZoneDeploymentState.Completed;
                    var replaced = new NCZoneDeploymentStateChangedEvent(
                        previous.Id, previous.ZoneId, NCZoneDeploymentState.Active,
                        NCZoneDeploymentState.Completed, "Superseded by a validated deployment.");
                    RaiseLocalEvent(ref replaced);
                }
                _zoneLocks[job.ZoneId] = job.Id;
                SetState(job, NCZoneDeploymentState.CapturingBaseline);
                break;
            case NCZoneDeploymentState.CapturingBaseline:
                if (_zoneBaselines.TryGetValue(job.ZoneId, out var baseline))
                    job.BaselineTiles.AddRange(baseline);
                else
                {
                    CaptureTileBaseline(job);
                    _zoneBaselines[job.ZoneId] = job.BaselineTiles.ToList();
                }
                SetState(job, NCZoneDeploymentState.Clearing);
                break;
            case NCZoneDeploymentState.Clearing:
                DeletePreviousDeploymentEntities(job.ZoneId, job.Id);
                SetState(job, NCZoneDeploymentState.ApplyingTiles);
                break;
            case NCZoneDeploymentState.ApplyingTiles:
                ApplyTiles(job, ref remainingTiles, budget, stopwatch);
                break;
            case NCZoneDeploymentState.SpawningStaticEntities:
                SpawnEntities(job, ref remainingEntities, budget, stopwatch);
                break;
            case NCZoneDeploymentState.ResolvingVerticalLinks:
                if (!ValidateVerticalLinks(job, out var linkError))
                {
                    SetState(job, NCZoneDeploymentState.RollingBack, linkError);
                    return;
                }
                SetState(job, NCZoneDeploymentState.SpawningDynamicContent);
                break;
            case NCZoneDeploymentState.SpawningDynamicContent:
                SetState(job, NCZoneDeploymentState.Initializing);
                break;
            case NCZoneDeploymentState.Initializing:
                SetState(job, NCZoneDeploymentState.Active);
                break;
            case NCZoneDeploymentState.ClosingEntrances:
                SetState(job, NCZoneDeploymentState.Cleaning);
                break;
            case NCZoneDeploymentState.Cleaning:
                DeleteDeploymentEntities(job.Id);
                SetState(job, NCZoneDeploymentState.RestoringBaseline);
                break;
            case NCZoneDeploymentState.RestoringBaseline:
                RestoreTileBaseline(job, ref remainingTiles, budget, stopwatch);
                break;
            case NCZoneDeploymentState.RollingBack:
                DeleteDeploymentEntities(job.Id);
                job.TileIndex = 0;
                SetState(job, NCZoneDeploymentState.RestoringBaseline);
                break;
        }
    }

    private bool TryPrepareDeployment(DeploymentJob job, out string error)
    {
        error = string.Empty;
        if (job.Template == null || job.Template.Revision != job.TemplateRevision)
        {
            error = "Cached template revision no longer matches the request.";
            return false;
        }

        if (!_zones.TryGetZoneNetwork(job.ZoneId, out var networkId))
        {
            error = "Target zone has no Z-network.";
            return false;
        }

        if (_occupancy.IsOccupied(job.ZoneId, out var blocker))
        {
            error = $"Target zone is occupied by {ToPrettyString(blocker)}.";
            return false;
        }

        var targetZLevels = new List<int>();
        if (!TryGetTargetZLevels(job.ZoneId, targetZLevels, out error))
            return false;

        var sourceFloors = job.Template.Floors.OrderBy(floor => floor.RelativeZ).ToArray();
        if (sourceFloors.Length != targetZLevels.Count)
        {
            error =
                $"Template has {sourceFloors.Length} floor(s), but zone {job.ZoneId} has " +
                $"{targetZLevels.Count} explicit Z-level footprint(s).";
            return false;
        }

        var baseZ = targetZLevels[0] - sourceFloors[0].RelativeZ;
        for (var i = 0; i < sourceFloors.Length; i++)
        {
            if (baseZ + sourceFloors[i].RelativeZ == targetZLevels[i])
                continue;

            error = "Template relative Z offsets do not match the target zone Z-levels.";
            return false;
        }

        foreach (var floor in sourceFloors)
        {
            var targetZ = baseZ + floor.RelativeZ;
            if (!_coordinates.TryGetMap(networkId, targetZ, out var mapUid))
            {
                error = $"Target Z={targetZ} does not exist in the zone network.";
                return false;
            }

            var targetFootprint = new HashSet<Vector2i>();
            if (!TryGetTargetFootprint(job.ZoneId, networkId, targetZ, targetFootprint, out error))
                return false;

            var targetOrigin = new Vector2i(
                targetFootprint.Min(tile => tile.X),
                targetFootprint.Min(tile => tile.Y));
            var sourceFootprint = floor.Tiles
                .Select(tile => tile.Indices - floor.SourceOrigin)
                .ToHashSet();
            var normalizedTarget = targetFootprint
                .Select(tile => tile - targetOrigin)
                .ToHashSet();
            if (!sourceFootprint.SetEquals(normalizedTarget))
            {
                error =
                    $"Template floor {floor.RelativeZ} shape does not exactly match zone {job.ZoneId} " +
                    $"at Z={targetZ} (source={sourceFootprint.Count} tiles, target={normalizedTarget.Count} tiles).";
                return false;
            }

            var targetCenter = new Vector2(targetOrigin.X + 0.5f, targetOrigin.Y + 0.5f);
            if (!_maps.TryFindGridAt(mapUid, targetCenter, out var gridUid, out var grid))
            {
                error = $"No target grid exists under zone {job.ZoneId} at Z={targetZ}.";
                return false;
            }

            foreach (var targetTile in targetFootprint)
            {
                var center = new Vector2(targetTile.X + 0.5f, targetTile.Y + 0.5f);
                if (_maps.TryFindGridAt(mapUid, center, out var candidateGrid, out _) && candidateGrid == gridUid)
                    continue;

                error = $"Zone {job.ZoneId} spans multiple target grids at Z={targetZ}.";
                return false;
            }

            var mapCoordinates = new MapCoordinates(targetCenter, Transform(mapUid).MapID);
            var localCenter = _transform.ToCoordinates((gridUid, null), mapCoordinates).Position;
            var localOrigin = new Vector2i(
                (int) MathF.Floor(localCenter.X),
                (int) MathF.Floor(localCenter.Y));
            var runtime = new DeploymentFloor(floor, targetZ, gridUid, grid, localOrigin);

            foreach (var sourceTile in floor.Tiles)
            {
                var targetIndices = localOrigin + (sourceTile.Indices - floor.SourceOrigin);
                if (ShouldApplyTile(job.Template.TilePolicy, sourceTile.Tile))
                    runtime.Tiles.Add(new DeploymentTile(targetIndices, sourceTile.Tile));
            }

            if (job.Template.EntityPolicy != NCZoneTemplateEntityPolicy.None)
            {
                foreach (var sourceEntity in floor.Entities)
                {
                    var sourceTile = new Vector2i(
                        (int) MathF.Floor(sourceEntity.Position.X),
                        (int) MathF.Floor(sourceEntity.Position.Y));
                    if (!sourceFootprint.Contains(sourceTile - floor.SourceOrigin))
                    {
                        error = $"Template floor {floor.RelativeZ} contains an entity outside its tile footprint.";
                        return false;
                    }

                    var target = new Vector2(localOrigin.X, localOrigin.Y) +
                                 (sourceEntity.Position - new Vector2(floor.SourceOrigin.X, floor.SourceOrigin.Y));
                    runtime.Entities.Add(new DeploymentEntity(sourceEntity, target, sourceEntity.Rotation));
                }
            }

            job.Floors.Add(runtime);
        }

        return true;
    }

    private static bool ShouldApplyTile(NCZoneTemplateTilePolicy policy, Tile tile)
    {
        return policy switch
        {
            NCZoneTemplateTilePolicy.Preserve => false,
            NCZoneTemplateTilePolicy.ReplaceNonEmpty => !tile.IsEmpty,
            NCZoneTemplateTilePolicy.ReplaceAllAllowedTiles => true,
            _ => false,
        };
    }

    private bool TryGetTargetZLevels(NCZoneId zoneId, List<int> zLevels, out string error)
    {
        zLevels.Clear();
        var geometry = new List<NCZoneGeometryInfo>();
        if (!_zones.GetGeometry(zoneId, geometry))
        {
            error = "Target zone geometry does not exist.";
            return false;
        }

        var unique = new HashSet<int>();
        foreach (var fragment in geometry)
        {
            switch (fragment.Kind)
            {
                case NCZoneGeometryKind.Polygon when fragment.Global:
                    error = "Direct apartment and dungeon layouts require explicit Z geometry, not a global polygon.";
                    return false;
                case NCZoneGeometryKind.Polygon:
                    unique.Add(fragment.Z);
                    break;
                case NCZoneGeometryKind.Volume:
                    for (var z = fragment.MinZ; z <= fragment.MaxZ; z++)
                        unique.Add(z);
                    break;
                case NCZoneGeometryKind.TileMask:
                    foreach (var chunk in fragment.Chunks)
                        unique.Add(chunk.Z);
                    break;
            }
        }

        zLevels.AddRange(unique.Order());
        if (zLevels.Count != 0)
        {
            error = string.Empty;
            return true;
        }

        error = "Target zone has no explicit Z-level geometry.";
        return false;
    }

    private bool TryGetTargetFootprint(
        NCZoneId zoneId,
        NCZNetworkId networkId,
        int z,
        HashSet<Vector2i> footprint,
        out string error)
    {
        footprint.Clear();
        var geometry = new List<NCZoneGeometryInfo>();
        if (!_zones.GetGeometry(zoneId, geometry))
        {
            error = "Target zone geometry does not exist.";
            return false;
        }

        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;
        foreach (var fragment in geometry)
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
                foreach (var chunk in fragment.Chunks)
                {
                    if (chunk.Z != z)
                        continue;
                    minX = Math.Min(minX, chunk.Origin.X);
                    minY = Math.Min(minY, chunk.Origin.Y);
                    maxX = Math.Max(maxX, chunk.Origin.X + NCTileMaskChunk.Size - 1);
                    maxY = Math.Max(maxY, chunk.Origin.Y + NCTileMaskChunk.Size - 1);
                }
                continue;
            }

            if (fragment.Vertices.Count == 0)
                continue;
            minX = Math.Min(minX, (int) MathF.Floor(fragment.Vertices.Min(vertex => vertex.X)));
            minY = Math.Min(minY, (int) MathF.Floor(fragment.Vertices.Min(vertex => vertex.Y)));
            maxX = Math.Max(maxX, (int) MathF.Ceiling(fragment.Vertices.Max(vertex => vertex.X)) - 1);
            maxY = Math.Max(maxY, (int) MathF.Ceiling(fragment.Vertices.Max(vertex => vertex.Y)) - 1);
        }

        if (minX > maxX || minY > maxY)
        {
            error = $"Target zone has no footprint at Z={z}.";
            return false;
        }

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var coordinates = new NCMapCoordinates(networkId, new Vector2(x + 0.5f, y + 0.5f), z);
                if (_zones.Contains(zoneId, coordinates))
                    footprint.Add(new Vector2i(x, y));
            }
        }

        if (footprint.Count != 0)
        {
            error = string.Empty;
            return true;
        }

        error = $"Target zone footprint is empty at Z={z}.";
        return false;
    }

    private void CaptureTileBaseline(DeploymentJob job)
    {
        foreach (var floor in job.Floors)
        {
            foreach (var tile in floor.Tiles)
            {
                var current = _maps.GetTileRef(floor.GridUid, floor.Grid, tile.Indices).Tile;
                job.BaselineTiles.Add(new BaselineTile(floor.GridUid, floor.Grid, tile.Indices, current));
            }
        }
    }

    private void ApplyTiles(
        DeploymentJob job,
        ref int remaining,
        NCZoneTemplateBudgetPrototype budget,
        Stopwatch stopwatch)
    {
        while (remaining > 0 && job.TileIndex < job.TotalTiles && IsTimeBudgetAvailable(budget, stopwatch))
        {
            var (floor, _) = job.GetTile(job.TileIndex);
            _tileBatch.Clear();
            while (remaining > 0 && job.TileIndex < job.TotalTiles)
            {
                var (candidateFloor, tile) = job.GetTile(job.TileIndex);
                if (candidateFloor.GridUid != floor.GridUid)
                    break;

                _tileBatch.Add((tile.Indices, tile.Tile));
                job.TileIndex++;
                remaining--;
            }

            // RobustToolbox batches tile events and collision regeneration for this call, avoiding one expensive
            // grid rebuild per floor tile while the outer deployment budget still caps the batch size.
            _maps.SetTiles(floor.GridUid, floor.Grid, _tileBatch);
        }

        if (job.TileIndex >= job.TotalTiles)
        {
            job.TileIndex = 0;
            SetState(job, NCZoneDeploymentState.SpawningStaticEntities);
        }
    }

    private void SpawnEntities(
        DeploymentJob job,
        ref int remaining,
        NCZoneTemplateBudgetPrototype budget,
        Stopwatch stopwatch)
    {
        while (remaining > 0 && job.EntityIndex < job.TotalEntities && IsTimeBudgetAvailable(budget, stopwatch))
        {
            var (floor, entity) = job.GetEntity(job.EntityIndex++);
            var uid = entity.Source.Uid;
            if (!Exists(uid))
            {
                SetState(job, NCZoneDeploymentState.RollingBack, "A prepared template entity no longer exists.");
                return;
            }

            // This is the hot path: the entity already exists on the paused storage map, so deployment only
            // reparents it to the city grid and performs its first MapInit. No YAML parse or Spawn occurs here.
            _transform.SetCoordinates(uid, new EntityCoordinates(floor.GridUid, entity.Position));
            _transform.SetLocalRotation(uid, entity.Rotation);
            RunPreparedEntityMapInit(uid);
            TagDeploymentTree(uid, job, floor.Source.RelativeZ);
            remaining--;
        }

        if (job.EntityIndex >= job.TotalEntities)
        {
            ReleasePreparedInstance(job);
            SetState(job, NCZoneDeploymentState.ResolvingVerticalLinks);
        }
    }

    private void RunPreparedEntityMapInit(EntityUid root)
    {
        // Prepared roots can own container contents. Initialize only this bounded transform tree, parent first,
        // because SharedMapSystem's recursive helper is internal to RobustToolbox and a global query is unnecessary.
        var pending = new Queue<EntityUid>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var uid))
        {
            if (!Exists(uid))
                continue;

            var metadata = MetaData(uid);
            if (metadata.EntityLifeStage == EntityLifeStage.Initialized)
                EntityManager.RunMapInit(uid, metadata);

            // MapInit can spawn actions and container-fill contents. Enumerate afterwards so the complete
            // mapper-authored tree is initialized before deployment provenance is attached.
            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                pending.Enqueue(child);
        }
    }

    private void TagDeploymentTree(EntityUid root, DeploymentJob job, int relativeZ)
    {
        // Every mapper-authored child is tagged as deployment content. Untagged entities encountered later are
        // resident belongings and receive a separate housing marker before a round snapshot is serialized.
        var pending = new Queue<EntityUid>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var uid))
        {
            if (!Exists(uid))
                continue;

            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                pending.Enqueue(child);

            // ComponentStartup indexes DeploymentId immediately, so all data must be populated before AddComp.
            // A stale tag from a deserialized snapshot is removed by the cache loader, but replace defensively too.
            RemComp<NCZoneDeploymentComponent>(uid);
            AddComp(uid, new NCZoneDeploymentComponent
            {
                DeploymentId = job.Id.Value,
                TargetZoneId = job.ZoneId,
                TemplateId = job.TemplateId,
                TemplateRevision = job.TemplateRevision,
                SourceFloor = relativeZ,
                CleanupPolicy = _prototypes.Index(job.TemplateId).CleanupPolicy,
            });
        }
    }

    private bool ValidateVerticalLinks(DeploymentJob job, out string error)
    {
        error = string.Empty;
        if (!_deploymentEntities.TryGetValue(job.Id, out var entities))
            entities = [];
        var links = new Dictionary<string, (EntityUid Uid, NCTemplateVerticalLinkMarkerComponent Marker)>();
        foreach (var uid in entities)
        {
            if (!TryComp(uid, out NCTemplateVerticalLinkMarkerComponent? marker))
                continue;
            if (!links.TryAdd(marker.LinkId, (uid, marker)))
            {
                error = $"Vertical link id '{marker.LinkId}' is duplicated after deployment.";
                return false;
            }
        }

        foreach (var (linkId, source) in links)
        {
            if (!links.TryGetValue(source.Marker.TargetLinkId, out var target))
            {
                if (!source.Marker.Required)
                    continue;
                error = $"Required vertical link '{linkId}' targets missing link '{source.Marker.TargetLinkId}'.";
                return false;
            }
            if (source.Marker.LinkType != target.Marker.LinkType)
            {
                error = $"Vertical link '{linkId}' and its target use different link types.";
                return false;
            }
            if (!_coordinates.TryGetCoordinates(source.Uid, out var sourceCoordinates) ||
                !_coordinates.TryGetCoordinates(target.Uid, out var targetCoordinates))
            {
                error = $"Vertical link '{linkId}' could not resolve persistent coordinates.";
                return false;
            }

            if (source.Marker.Direction is NCVerticalLinkDirection.Bidirectional or NCVerticalLinkDirection.SourceToTarget)
                ConfigureVerticalEndpoint(source.Uid, source.Marker.LinkType, targetCoordinates);
            if (source.Marker.Direction is NCVerticalLinkDirection.Bidirectional or NCVerticalLinkDirection.TargetToSource)
                ConfigureVerticalEndpoint(target.Uid, target.Marker.LinkType, sourceCoordinates);
        }

        return true;
    }

    private void ConfigureVerticalEndpoint(
        EntityUid uid,
        NCVerticalLinkType linkType,
        NCMapCoordinates target)
    {
        var resolved = EnsureComp<NCResolvedVerticalLinkComponent>(uid);
        resolved.LinkType = linkType;
        resolved.Target = target;
    }

    private void RestoreTileBaseline(
        DeploymentJob job,
        ref int remaining,
        NCZoneTemplateBudgetPrototype budget,
        Stopwatch stopwatch)
    {
        while (remaining > 0 && job.TileIndex < job.BaselineTiles.Count && IsTimeBudgetAvailable(budget, stopwatch))
        {
            var first = job.BaselineTiles[job.TileIndex];
            _tileBatch.Clear();
            while (remaining > 0 && job.TileIndex < job.BaselineTiles.Count)
            {
                var tile = job.BaselineTiles[job.TileIndex];
                if (tile.GridUid != first.GridUid)
                    break;

                _tileBatch.Add((tile.Indices, tile.Tile));
                job.TileIndex++;
                remaining--;
            }

            if (Exists(first.GridUid))
                _maps.SetTiles(first.GridUid, first.Grid, _tileBatch);
        }

        if (job.TileIndex < job.BaselineTiles.Count)
            return;

        _zoneLocks.Remove(job.ZoneId);
        SetState(job, job.Error == null ? NCZoneDeploymentState.Completed : NCZoneDeploymentState.FailedClean, job.Error);
    }

    private void DeletePreviousDeploymentEntities(NCZoneId zoneId, NCZoneDeploymentId except)
    {
        foreach (var pair in _deploymentEntities.ToArray())
        {
            if (pair.Key == except)
                continue;
            foreach (var uid in pair.Value.ToArray())
            {
                if (!Exists(uid) || pair.Value.Contains(Transform(uid).ParentUid))
                    continue;
                if (TryComp<NCZoneDeploymentComponent>(uid, out var component) && component.TargetZoneId == zoneId)
                    QueueDel(uid);
            }
        }
    }

    private void DeleteDeploymentEntities(NCZoneDeploymentId id)
    {
        if (!_deploymentEntities.TryGetValue(id, out var entities))
            return;
        foreach (var uid in entities.ToArray())
        {
            if (!Exists(uid) || entities.Contains(Transform(uid).ParentUid))
                continue;
            QueueDel(uid);
        }
    }

    private void SetState(DeploymentJob job, NCZoneDeploymentState state, string? error = null)
    {
        if (job.State == state && error == null)
            return;

        var old = job.State;
        job.State = state;
        if (error != null)
            job.Error = error;

        if (state is NCZoneDeploymentState.FailedClean or NCZoneDeploymentState.Completed)
        {
            ReleasePreparedInstance(job);
            _zoneLocks.Remove(job.ZoneId);
        }
        if (state == NCZoneDeploymentState.Completed)
            _zoneBaselines.Remove(job.ZoneId);

        var ev = new NCZoneDeploymentStateChangedEvent(job.Id, job.ZoneId, old, state, job.Error);
        RaiseLocalEvent(ref ev);
    }

    private void ReleasePreparedInstance(DeploymentJob job)
    {
        if (job.Template == null)
            return;

        // Every transferred root has already left these grids. Deleting them also discards any source entity
        // intentionally filtered by EntityPolicy.None without touching the city deployment.
        ReleaseCachedTemplate(job.Template);
        job.Template = null;
    }

    private static bool IsTerminal(NCZoneDeploymentState state)
    {
        return state is NCZoneDeploymentState.Active or
            NCZoneDeploymentState.Completed or
            NCZoneDeploymentState.FailedClean;
    }

    private sealed class DeploymentJob(
        NCZoneDeploymentId id,
        NCZoneId zoneId,
        ProtoId<NCZoneTemplatePrototype> templateId,
        int templateRevision,
        NCZoneDeploymentPurpose purpose,
        NCCharacterId? ownerCharacterId,
        Guid? dungeonRunId)
    {
        public readonly NCZoneDeploymentId Id = id;
        public readonly NCZoneId ZoneId = zoneId;
        public readonly ProtoId<NCZoneTemplatePrototype> TemplateId = templateId;
        public readonly int TemplateRevision = templateRevision;
        public readonly NCZoneDeploymentPurpose Purpose = purpose;
        public readonly NCCharacterId? OwnerCharacterId = ownerCharacterId;
        public readonly Guid? DungeonRunId = dungeonRunId;
        public readonly List<DeploymentFloor> Floors = [];
        public readonly List<BaselineTile> BaselineTiles = [];
        public CachedTemplate? Template;
        public NCZoneDeploymentState State = NCZoneDeploymentState.Pending;
        public int TileIndex;
        public int EntityIndex;
        public string? Error;

        public int TotalTiles => Floors.Sum(floor => floor.Tiles.Count);
        public int TotalEntities => Floors.Sum(floor => floor.Entities.Count);

        public (DeploymentFloor Floor, DeploymentTile Tile) GetTile(int index)
        {
            foreach (var floor in Floors)
            {
                if (index < floor.Tiles.Count)
                    return (floor, floor.Tiles[index]);
                index -= floor.Tiles.Count;
            }
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        public (DeploymentFloor Floor, DeploymentEntity Entity) GetEntity(int index)
        {
            foreach (var floor in Floors)
            {
                if (index < floor.Entities.Count)
                    return (floor, floor.Entities[index]);
                index -= floor.Entities.Count;
            }
            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    private sealed class DeploymentFloor(
        CachedFloor source,
        int targetZ,
        EntityUid gridUid,
        MapGridComponent grid,
        Vector2i localOrigin)
    {
        public readonly CachedFloor Source = source;
        public readonly int TargetZ = targetZ;
        public readonly EntityUid GridUid = gridUid;
        public readonly MapGridComponent Grid = grid;
        public readonly Vector2i LocalOrigin = localOrigin;
        public readonly List<DeploymentTile> Tiles = [];
        public readonly List<DeploymentEntity> Entities = [];
    }

    private readonly record struct DeploymentTile(Vector2i Indices, Tile Tile);
    private sealed record DeploymentEntity(CachedEntity Source, Vector2 Position, Angle Rotation);
    private readonly record struct BaselineTile(EntityUid GridUid, MapGridComponent Grid, Vector2i Indices, Tile Tile);
}

public readonly record struct NCZoneDeploymentRequest(
    NCZoneDeploymentId DeploymentId,
    NCZoneId TargetZoneId,
    ProtoId<NCZoneTemplatePrototype> TemplateId,
    NCZoneDeploymentPurpose Purpose,
    NCCharacterId? OwnerCharacterId,
    Guid? DungeonRunId);

public readonly record struct NCZoneDeploymentStatus(
    NCZoneDeploymentId DeploymentId,
    NCZoneId TargetZoneId,
    ProtoId<NCZoneTemplatePrototype> TemplateId,
    int TemplateRevision,
    NCZoneDeploymentPurpose Purpose,
    NCZoneDeploymentState State,
    string? Error);
