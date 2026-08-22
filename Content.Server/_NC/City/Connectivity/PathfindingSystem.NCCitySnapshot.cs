// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Linq;
using Content.Shared.NPC;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;

namespace Content.Server.NPC.Pathfinding;

/// <summary>
/// Immutable key for one round-local vanilla pathfinding polygon.
/// Consumers must discard it whenever the owning grid revision changes.
/// </summary>
public readonly record struct NCLocalNavigationNodeKey(
    EntityUid GraphUid,
    Vector2i ChunkOrigin,
    byte TileIndex,
    Box2 Bounds);

/// <summary>
/// Read-only projection used to build the Living City Room/Portal cache without exposing pathfinding components.
/// </summary>
public readonly record struct NCLocalNavigationNodeSnapshot(
    NCLocalNavigationNodeKey Key,
    EntityCoordinates Coordinates,
    PathfindingBreadcrumbFlag Flags,
    NCLocalNavigationNodeKey[] Neighbors);

/// <summary>
/// Raised after vanilla pathfinding has atomically replaced all dirty chunks for one grid.
/// </summary>
[ByRefEvent]
public readonly record struct NCLocalPathfindingGraphUpdatedEvent(EntityUid GridUid, long Revision);

public sealed partial class PathfindingSystem
{
    private readonly Dictionary<EntityUid, long> _ncCityGraphRevisions = [];

    /// <summary>
    /// Copies a bounded immutable view of one grid's current vanilla navigation graph.
    /// This is called only in response to map/pathfinding invalidation events, never per frame.
    /// </summary>
    public bool TryGetNCCityNavigationSnapshot(
        EntityUid gridUid,
        List<NCLocalNavigationNodeSnapshot> result,
        out long revision)
    {
        result.Clear();
        revision = 0;
        if (!TryComp<GridPathfindingComponent>(gridUid, out var graph) ||
            !_ncCityGraphRevisions.TryGetValue(gridUid, out revision))
        {
            return false;
        }

        var polygons = new HashSet<PathPoly>();
        foreach (var chunk in graph.Chunks.Values)
        {
            foreach (var tile in chunk.Polygons)
            {
                foreach (var polygon in tile)
                {
                    if (polygon.IsValid())
                        polygons.Add(polygon);
                }
            }
        }

        var ordered = polygons
            .OrderBy(static polygon => polygon.ChunkOrigin.X)
            .ThenBy(static polygon => polygon.ChunkOrigin.Y)
            .ThenBy(static polygon => polygon.TileIndex)
            .ThenBy(static polygon => polygon.Box.Center.X)
            .ThenBy(static polygon => polygon.Box.Center.Y)
            .ToArray();
        var keys = ordered.ToDictionary(static polygon => polygon, ToNCNodeKey);

        foreach (var polygon in ordered)
        {
            var neighbors = polygon.Neighbors
                .Where(neighbor => neighbor.IsValid() && keys.ContainsKey(neighbor))
                .Select(neighbor => keys[neighbor])
                .OrderBy(static key => key.ChunkOrigin.X)
                .ThenBy(static key => key.ChunkOrigin.Y)
                .ThenBy(static key => key.TileIndex)
                .ThenBy(static key => key.Bounds.Center.X)
                .ThenBy(static key => key.Bounds.Center.Y)
                .ToArray();
            result.Add(new NCLocalNavigationNodeSnapshot(
                keys[polygon],
                polygon.Coordinates,
                polygon.Data.Flags,
                neighbors));
        }

        return true;
    }

    /// <summary>
    /// Resolves an engine coordinate to the key used by the current Living City snapshot.
    /// </summary>
    public bool TryGetNCCityNavigationNode(EntityCoordinates coordinates, out NCLocalNavigationNodeKey key)
    {
        var polygon = GetPoly(coordinates);
        if (polygon == null || !polygon.IsValid())
        {
            key = default;
            return false;
        }

        key = ToNCNodeKey(polygon);
        return true;
    }

    /// <summary>
    /// Reports whether a static physics entity contributes hard geometry to the vanilla navigation graph. Static
    /// presentation entities such as blood puddles have soft fixtures and must never invalidate RoomGraph topology.
    /// </summary>
    public bool IsNCCityStructuralCollisionBody(EntityUid uid)
    {
        return TryComp<PhysicsComponent>(uid, out var body) &&
               body.BodyType == BodyType.Static &&
               TryComp<FixturesComponent>(uid, out var fixtures) &&
               IsBodyRelevant(fixtures);
    }

    private void RaiseNCCityPathGraphUpdated(EntityUid gridUid)
    {
        var oldRevision = _ncCityGraphRevisions.GetValueOrDefault(gridUid);
        var revision = oldRevision == long.MaxValue ? 1 : oldRevision + 1;
        _ncCityGraphRevisions[gridUid] = revision;
        var ev = new NCLocalPathfindingGraphUpdatedEvent(gridUid, revision);
        RaiseLocalEvent(ref ev);
    }

    private void RemoveNCCityPathGraphRevision(EntityUid gridUid)
    {
        _ncCityGraphRevisions.Remove(gridUid);
    }

    private static NCLocalNavigationNodeKey ToNCNodeKey(PathPoly polygon)
    {
        return new NCLocalNavigationNodeKey(
            polygon.GraphUid,
            polygon.ChunkOrigin,
            polygon.TileIndex,
            polygon.Box);
    }
}
