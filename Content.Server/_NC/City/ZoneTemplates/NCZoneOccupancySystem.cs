// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Numerics;
using Content.Shared._NC.City.ZoneTemplates.Components;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Prototypes;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.Missions.Components;
using Content.Shared.Mobs.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Player;
using Robust.Server.GameObjects;

namespace Content.Server._NC.City.ZoneTemplates;

/// <summary>
/// Performs explicit, spatially bounded safety checks before a zone is rebuilt or restored.
/// </summary>
public sealed partial class NCZoneOccupancySystem : EntitySystem
{
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private MapSystem _maps = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private Zones.NCZoneSystem _zones = default!;

    private readonly List<NCZoneGeometryInfo> _geometry = [];
    private readonly HashSet<EntityUid> _candidates = [];

    public bool IsOccupied(NCZoneId zoneId, out EntityUid blocker)
    {
        blocker = EntityUid.Invalid;
        if (!TryCollectCandidates(zoneId))
            return true;

        foreach (var uid in _candidates)
        {
            // Existing template-owned content is the cleanup target, not an external occupancy blocker.
            if (HasComp<NCZoneDeploymentComponent>(uid))
                continue;
            if (!HasComp<MobStateComponent>(uid) &&
                !HasComp<ActorComponent>(uid) &&
                !HasComp<NCMissionOccupancyBlockerComponent>(uid))
                continue;
            if (!_coordinates.TryGetCoordinates(uid, out var coordinates) || !_zones.Contains(zoneId, coordinates))
                continue;
            blocker = uid;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Collects actors and mobs that came from the persistent world rather than the active deployment.
    /// The caller supplies the result set so no internal spatial-query buffer escapes this system.
    /// </summary>
    public bool CollectExternalMobOccupants(NCZoneId zoneId, HashSet<EntityUid> result)
    {
        result.Clear();
        if (!TryCollectCandidates(zoneId))
            return false;

        foreach (var uid in _candidates)
        {
            if (HasComp<NCZoneDeploymentComponent>(uid) ||
                (!HasComp<MobStateComponent>(uid) && !HasComp<ActorComponent>(uid)) ||
                !_coordinates.TryGetCoordinates(uid, out var coordinates) ||
                !_zones.Contains(zoneId, coordinates))
                continue;

            result.Add(uid);
        }

        return true;
    }

    private bool TryCollectCandidates(NCZoneId zoneId)
    {
        if (!_zones.TryGetZoneNetwork(zoneId, out var networkId) || !_zones.GetGeometry(zoneId, _geometry))
            return false;

        _candidates.Clear();
        foreach (var fragment in _geometry)
        {
            if (fragment.Kind == NCZoneGeometryKind.TileMask)
            {
                foreach (var chunk in fragment.Chunks)
                    Collect(networkId, chunk.Z, new Box2(chunk.Origin, chunk.Origin + new Vector2(64f, chunk.Rows.Count)));
                continue;
            }

            if (fragment.Vertices.Count == 0)
                continue;
            var bounds = Bounds(fragment.Vertices);
            var minimum = fragment.Kind == NCZoneGeometryKind.Volume ? fragment.MinZ : fragment.Z;
            var maximum = fragment.Kind == NCZoneGeometryKind.Volume ? fragment.MaxZ : fragment.Z;
            for (var z = minimum; z <= maximum; z++)
                Collect(networkId, z, bounds);
        }

        return true;
    }

    private void Collect(NCZNetworkId networkId, int z, Box2 bounds)
    {
        if (!_coordinates.TryGetMap(networkId, z, out var mapUid))
            return;

        var center = new MapCoordinates(bounds.Center, Transform(mapUid).MapID);
        if (!_maps.TryFindGridAt(mapUid, center.Position, out var gridUid, out MapGridComponent? _))
            return;
        _lookup.GetEntitiesIntersecting(gridUid, bounds, _candidates, LookupFlags.Uncontained | LookupFlags.Dynamic);
    }

    private static Box2 Bounds(IReadOnlyList<Vector2> vertices)
    {
        var min = vertices[0];
        var max = vertices[0];
        foreach (var point in vertices)
        {
            min = Vector2.Min(min, point);
            max = Vector2.Max(max, point);
        }
        return new Box2(min, max);
    }
}
