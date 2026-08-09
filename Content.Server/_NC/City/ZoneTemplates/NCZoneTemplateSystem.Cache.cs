// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Content.Shared._NC.City.ZoneTemplates;
using Content.Shared._NC.City.ZoneTemplates.Components;
using Content.Shared._NC.City.ZoneTemplates.Prototypes;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.City.ZoneTemplates;

public sealed partial class NCZoneTemplateSystem
{
    private void QueueTemplateCache(NCZoneTemplatePrototype template)
    {
        var id = new ProtoId<NCZoneTemplatePrototype>(template.ID);
        if (_cache.ContainsKey(id) || !_cacheQueued.Add(id))
            return;

        _cacheQueue.Enqueue(new TemplateCacheJob(
            id,
            template.Revision,
            template.TilePolicy,
            template.EntityPolicy,
            template.Floors.ToArray()));
    }

    private void ProcessCacheQueue(NCZoneTemplateBudgetPrototype budget, Stopwatch stopwatch)
    {
        var remaining = budget.MaxSerializationWorkPerTick;
        while (remaining > 0 && _cacheQueue.TryPeek(out var job) && IsTimeBudgetAvailable(budget, stopwatch))
        {
            if (job.FloorIndex >= job.Floors.Length)
            {
                _cache[job.TemplateId] = new CachedTemplate(
                    job.Revision,
                    job.TilePolicy,
                    job.EntityPolicy,
                    job.CachedFloors.ToArray());
                _cacheQueue.Dequeue();
                _cacheQueued.Remove(job.TemplateId);
                continue;
            }

            EnsureStorageMap();
            var floor = job.Floors[job.FloorIndex];
            if (!TryCacheFloor(job.TemplateId, job.Revision, floor, job.FloorIndex, out var cached, out var error))
            {
                job.Error = error;
                // Cache failures can happen without a waiting deployment, so they must still be visible to operators.
                Log.Error($"Failed to cache zone template {job.TemplateId}: {error}");
                ReleaseCachedFloors(job.CachedFloors);
                _cacheQueue.Dequeue();
                _cacheQueued.Remove(job.TemplateId);
                FailWaitingDeployments(job.TemplateId, error);
                continue;
            }

            job.CachedFloors.Add(cached);
            job.FloorIndex++;
            remaining--;
        }
    }

    private bool TryCacheFloor(
        ProtoId<NCZoneTemplatePrototype> templateId,
        int templateRevision,
        NCZoneTemplateFloor floor,
        int floorIndex,
        out CachedFloor cached,
        out string error)
    {
        cached = default!;
        error = string.Empty;

        // Prepared grids are kept alive on this paused server-only map until a deployment consumes them.
        // Large spacing avoids overlap between floors while keeping their local coordinates unchanged.
        var offset = new Vector2(floorIndex * 1024f, 0f);
        if (!_loader.TryLoadGrid(_storageMapId, floor.MapPath, out var loaded, offset: offset))
        {
            error = $"Failed to load template floor {floor.MapPath}.";
            return false;
        }

        var (gridUid, grid) = loaded.Value;
        var keepPreparedGrid = false;
        try
        {
            var tiles = new List<CachedTile>();
            foreach (var tile in _maps.GetAllTiles(gridUid, grid, ignoreEmpty: true))
                tiles.Add(new CachedTile(tile.GridIndices, tile.Tile));

            if (tiles.Count == 0)
            {
                error = $"Template {templateId} floor {floor.RelativeZ} has no non-empty tiles.";
                return false;
            }

            var sourceOrigin = new Vector2i(
                tiles.Min(tile => tile.Indices.X),
                tiles.Min(tile => tile.Indices.Y));

            if (!TryApplyPreparedEntitySnapshot(
                    templateId,
                    templateRevision,
                    floor.RelativeZ,
                    gridUid,
                    sourceOrigin,
                    out error))
            {
                return false;
            }

            var entities = new List<CachedEntity>();
            var links = new List<CachedVerticalLink>();
            var children = Transform(gridUid).ChildEnumerator;
            while (children.MoveNext(out var child))
            {
                var xform = Transform(child);
                if (TryComp<NCTemplateVerticalLinkMarkerComponent>(child, out var link))
                {
                    if (link.RelativeZ != floor.RelativeZ)
                    {
                        error =
                            $"Template {templateId} floor {floor.RelativeZ} vertical link '{link.LinkId}' " +
                            $"declares relative Z {link.RelativeZ}.";
                        return false;
                    }

                    links.Add(new CachedVerticalLink(
                        link.LinkId,
                        link.TargetLinkId,
                        link.LinkType,
                        link.Direction,
                        link.Required,
                        xform.LocalPosition,
                        xform.LocalRotation));
                }

                entities.Add(new CachedEntity(child, xform.LocalPosition, xform.LocalRotation));
            }

            cached = new CachedFloor(
                floor.RelativeZ,
                gridUid,
                sourceOrigin,
                tiles.ToArray(),
                entities.ToArray(),
                links.ToArray());
            keepPreparedGrid = true;
            return true;
        }
        finally
        {
            if (!keepPreparedGrid && Exists(gridUid))
                Del(gridUid);
        }
    }

    /// <summary>
    /// Deletes only unused prepared instances. Instances leased by active deployment jobs are not in _cache.
    /// </summary>
    private void ClearPreparedTemplateCache()
    {
        foreach (var cached in _cache.Values)
            ReleaseCachedTemplate(cached);
        foreach (var queued in _cacheQueue)
            ReleaseCachedFloors(queued.CachedFloors);
        _cache.Clear();
        _cacheQueue.Clear();
        _cacheQueued.Clear();
    }

    private void ReleaseCachedTemplate(CachedTemplate cached)
    {
        ReleaseCachedFloors(cached.Floors);
    }

    private void ReleaseCachedFloors(IEnumerable<CachedFloor> floors)
    {
        foreach (var floor in floors)
        {
            if (Exists(floor.SourceGridUid))
                Del(floor.SourceGridUid);
        }
    }

    private void FailWaitingDeployments(ProtoId<NCZoneTemplatePrototype> templateId, string error)
    {
        foreach (var deployment in _deployments.Values)
        {
            if (deployment.TemplateId == templateId && deployment.State == NCZoneDeploymentState.Pending)
                SetState(deployment, NCZoneDeploymentState.FailedClean, error);
        }
    }

    private sealed class TemplateCacheJob(
        ProtoId<NCZoneTemplatePrototype> templateId,
        int revision,
        NCZoneTemplateTilePolicy tilePolicy,
        NCZoneTemplateEntityPolicy entityPolicy,
        NCZoneTemplateFloor[] floors)
    {
        public readonly ProtoId<NCZoneTemplatePrototype> TemplateId = templateId;
        public readonly int Revision = revision;
        public readonly NCZoneTemplateTilePolicy TilePolicy = tilePolicy;
        public readonly NCZoneTemplateEntityPolicy EntityPolicy = entityPolicy;
        public readonly NCZoneTemplateFloor[] Floors = floors;
        public readonly List<CachedFloor> CachedFloors = [];
        public int FloorIndex;
        public string? Error;
    }

    private sealed record CachedTemplate(
        int Revision,
        NCZoneTemplateTilePolicy TilePolicy,
        NCZoneTemplateEntityPolicy EntityPolicy,
        CachedFloor[] Floors);

    private sealed record CachedFloor(
        int RelativeZ,
        EntityUid SourceGridUid,
        Vector2i SourceOrigin,
        CachedTile[] Tiles,
        CachedEntity[] Entities,
        CachedVerticalLink[] VerticalLinks);

    private readonly record struct CachedTile(Vector2i Indices, Tile Tile);
    private sealed record CachedEntity(EntityUid Uid, Vector2 Position, Angle Rotation);
    private sealed record CachedVerticalLink(
        string LinkId,
        string TargetLinkId,
        NCVerticalLinkType Type,
        NCVerticalLinkDirection Direction,
        bool Required,
        Vector2 Position,
        Angle Rotation);
}
