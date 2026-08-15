// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._NC.City.Zones;
using Content.Server.NPC.Pathfinding;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared.Doors.Components;
using Content.Shared.NPC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Events;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.City.Connectivity;

/// <summary>
/// Builds a round-local Room/Portal cache from vanilla Robust pathfinding polygons. It never replaces or executes
/// tile pathfinding; it only narrows a city route to bounded local targets for materialized NPCs.
/// </summary>
public sealed class NCRoomPortalGraphSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly NCCityConnectivitySystem _city = default!;
    [Dependency] private readonly NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private readonly NCZoneSystem _zones = default!;
    [Dependency] private readonly PathfindingSystem _pathfinding = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private readonly Dictionary<EntityUid, GridGraph> _gridGraphs = [];
    private readonly Dictionary<NCRoomNodeId, RuntimeRoom> _rooms = [];
    private readonly Dictionary<NCRoomPortalId, RuntimePortal> _portals = [];
    private readonly Dictionary<NCRoomNodeId, List<NCRoomPortalId>> _outgoing = [];
    private readonly Dictionary<NCLocalNavigationNodeKey, NCRoomNodeId> _nodeRooms = [];
    private readonly HashSet<EntityUid> _structurallyDirtyGrids = [];
    private readonly List<NCLocalNavigationNodeSnapshot> _navigationSnapshot = [];
    private readonly HashSet<EntityUid> _lookupCandidates = [];

    private long _nextRoomId = 1;
    private long _nextPortalId = 1;

    public long GraphVersion { get; private set; }

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCLocalPathfindingGraphUpdatedEvent>(OnPathfindingGraphUpdated);
        SubscribeLocalEvent<GridInitializeEvent>(OnGridInitialized);
        SubscribeLocalEvent<GridRemovalEvent>(OnGridRemoved);
        SubscribeLocalEvent<TileChangedEvent>(OnTileChanged);
        SubscribeLocalEvent<CollisionChangeEvent>(OnCollisionChanged);
        SubscribeLocalEvent<DoorComponent, ComponentStartup>(OnDoorStartup);
        SubscribeLocalEvent<DoorComponent, ComponentShutdown>(OnDoorShutdown);
        SubscribeLocalEvent<NCZoneIndexRebuiltEvent>(OnZoneIndexRebuilt);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
    }

    public bool TryGetRoom(EntityCoordinates coordinates, out NCRoomNodeId roomId)
    {
        if (_pathfinding.TryGetNCCityNavigationNode(coordinates, out var key) &&
            _nodeRooms.TryGetValue(key, out roomId))
        {
            return true;
        }

        roomId = default;
        return false;
    }

    public bool TryGetRoomInfo(NCRoomNodeId roomId, out NCRoomNodeInfo room)
    {
        if (_rooms.TryGetValue(roomId, out var runtime))
        {
            room = ToInfo(runtime);
            return true;
        }

        room = default;
        return false;
    }

    public void GetRooms(NCZoneId zoneId, List<NCRoomNodeInfo> result)
    {
        result.Clear();
        foreach (var room in _rooms.Values)
        {
            if (room.ZoneId == zoneId)
                result.Add(ToInfo(room));
        }

        result.Sort(static (left, right) => left.Id.Value.CompareTo(right.Id.Value));
    }

    /// <summary>
    /// Copies every derived room for explicit diagnostics. This is never used from Update().
    /// </summary>
    public void GetRooms(List<NCRoomNodeInfo> result)
    {
        result.Clear();
        foreach (var room in _rooms.Values)
            result.Add(ToInfo(room));

        result.Sort(static (left, right) => left.Id.Value.CompareTo(right.Id.Value));
    }

    public void GetPortals(NCRoomNodeId roomId, List<NCRoomPortalInfo> result)
    {
        result.Clear();
        if (!_outgoing.TryGetValue(roomId, out var portals))
            return;

        foreach (var portalId in portals)
        {
            if (_portals.TryGetValue(portalId, out var portal))
                result.Add(ToInfo(portal));
        }
    }

    /// <summary>
    /// Copies every derived local portal once for explicit diagnostics.
    /// </summary>
    public void GetPortals(List<NCRoomPortalInfo> result)
    {
        result.Clear();
        foreach (var portal in _portals.Values)
            result.Add(ToInfo(portal));

        result.Sort(static (left, right) => left.Id.Value.CompareTo(right.Id.Value));
    }

    /// <summary>
    /// Copies navigation samples for one affected grid so spawn indices can rebuild without inspecting pathfinding
    /// components or scanning unrelated entities.
    /// </summary>
    public void GetNavigationSamples(EntityUid gridUid, List<NCRoomNavigationSample> result)
    {
        result.Clear();
        if (!_gridGraphs.TryGetValue(gridUid, out var graph))
            return;

        result.AddRange(graph.NavigationSamples);
    }

    /// <summary>
    /// Finds only the local portal sequence. Callers send each EntryTarget to vanilla Robust tile pathfinding.
    /// </summary>
    public bool TryFindRoute(
        NCRoomNodeId fromRoom,
        NCRoomNodeId toRoom,
        List<NCRoomRouteStep> result,
        Func<NCRoomPortalInfo, bool>? canTraverse = null)
    {
        result.Clear();
        if (!_rooms.ContainsKey(fromRoom) || !_rooms.ContainsKey(toRoom))
            return false;
        if (fromRoom == toRoom)
            return true;

        var frontier = new Queue<NCRoomNodeId>();
        var previous = new Dictionary<NCRoomNodeId, (NCRoomNodeId Previous, NCRoomPortalId Portal)>();
        var visited = new HashSet<NCRoomNodeId> { fromRoom };
        frontier.Enqueue(fromRoom);

        while (frontier.TryDequeue(out var current))
        {
            if (!_outgoing.TryGetValue(current, out var outgoing))
                continue;

            foreach (var portalId in outgoing)
            {
                var portal = _portals[portalId];
                if (canTraverse != null && !canTraverse(ToInfo(portal)))
                    continue;

                var next = portal.RoomA == current ? portal.RoomB : portal.RoomA;
                if (!visited.Add(next))
                    continue;

                previous[next] = (current, portalId);
                if (next == toRoom)
                    break;
                frontier.Enqueue(next);
            }

            if (visited.Contains(toRoom))
                break;
        }

        if (!previous.ContainsKey(toRoom))
            return false;

        var reversed = new List<NCRoomRouteStep>();
        var cursor = toRoom;
        while (cursor != fromRoom)
        {
            var predecessor = previous[cursor];
            var portal = _portals[predecessor.Portal];
            var fromA = portal.RoomA == predecessor.Previous;
            reversed.Add(new NCRoomRouteStep(
                portal.Id,
                predecessor.Previous,
                cursor,
                fromA ? portal.TargetA : portal.TargetB,
                fromA ? portal.TargetB : portal.TargetA,
                portal.Kind,
                portal.Controller));
            cursor = predecessor.Previous;
        }

        reversed.Reverse();
        result.AddRange(reversed);
        return true;
    }

    private void OnGridInitialized(GridInitializeEvent args)
    {
        _structurallyDirtyGrids.Add(args.EntityUid);
    }

    private void OnGridRemoved(GridRemovalEvent args)
    {
        _structurallyDirtyGrids.Remove(args.EntityUid);
        RemoveGridGraph(args.EntityUid, true);
    }

    private void OnTileChanged(ref TileChangedEvent args)
    {
        MarkStructurallyDirty(args.Entity.Owner);
    }

    private void OnCollisionChanged(ref CollisionChangeEvent args)
    {
        // Door state is evaluated by the eventual actor-specific traversal rule; opening a normal door must not
        // destroy and recreate room topology. Door creation/deletion is handled by component lifecycle events.
        if (HasComp<DoorComponent>(args.BodyUid) || !TryComp(args.BodyUid, out TransformComponent? transform))
            return;
        if (transform.GridUid is { } gridUid)
            MarkStructurallyDirty(gridUid);
    }

    private void OnDoorStartup(Entity<DoorComponent> entity, ref ComponentStartup args)
    {
        MarkDoorGridDirty(entity.Owner);
    }

    private void OnDoorShutdown(Entity<DoorComponent> entity, ref ComponentShutdown args)
    {
        MarkDoorGridDirty(entity.Owner);
    }

    private void OnPathfindingGraphUpdated(ref NCLocalPathfindingGraphUpdatedEvent args)
    {
        if (!_structurallyDirtyGrids.Remove(args.GridUid) && _gridGraphs.ContainsKey(args.GridUid))
            return;

        RebuildGrid(args.GridUid, args.Revision);
    }

    private void OnZoneIndexRebuilt(ref NCZoneIndexRebuiltEvent args)
    {
        foreach (var gridUid in _gridGraphs.Keys.ToArray())
        {
            RemoveGridGraph(gridUid, false);
            _structurallyDirtyGrids.Add(gridUid);
            if (_pathfinding.TryGetNCCityNavigationSnapshot(gridUid, _navigationSnapshot, out var revision))
                RebuildGrid(gridUid, revision);
        }

        InvalidateGraph();
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (!args.WasModified<NCCityConnectivityRulesPrototype>())
            return;

        foreach (var gridUid in _gridGraphs.Keys.ToArray())
        {
            RemoveGridGraph(gridUid, false);
            if (_pathfinding.TryGetNCCityNavigationSnapshot(gridUid, _navigationSnapshot, out var revision))
                RebuildGrid(gridUid, revision);
        }

        InvalidateGraph();
    }

    private void MarkDoorGridDirty(EntityUid doorUid)
    {
        if (TryComp(doorUid, out TransformComponent? transform) && transform.GridUid is { } gridUid)
            MarkStructurallyDirty(gridUid);
    }

    private void MarkStructurallyDirty(EntityUid gridUid)
    {
        _structurallyDirtyGrids.Add(gridUid);
        // Structural invalidation fails closed until vanilla pathfinding publishes its rebuilt graph.
        RemoveGridGraph(gridUid, true);
    }

    private void RebuildGrid(EntityUid gridUid, long expectedRevision)
    {
        if (!TryGetRules(out var rules) ||
            !float.IsFinite(rules.DoorDetectionRadius) ||
            rules.DoorDetectionRadius <= 0f ||
            !_pathfinding.TryGetNCCityNavigationSnapshot(gridUid, _navigationSnapshot, out var revision) ||
            revision != expectedRevision)
        {
            return;
        }

        RemoveGridGraph(gridUid, false);
        var graph = new GridGraph(gridUid, revision);
        var nodes = BuildNavigationNodes(gridUid, rules.DoorDetectionRadius, out var doors);
        BuildRooms(graph, nodes);
        foreach (var node in nodes.Values)
        {
            if (!node.IsDoor && graph.NodeRooms.TryGetValue(node.Snapshot.Key, out var roomId))
            {
                graph.NavigationSamples.Add(new NCRoomNavigationSample(
                    roomId,
                    gridUid,
                    node.ZoneId,
                    node.CityCoordinates));
            }
        }
        var doorDetectionRadiusSquared = rules.DoorDetectionRadius * rules.DoorDetectionRadius;
        BuildDoorPortals(graph, nodes, doors, doorDetectionRadiusSquared);
        BuildOpenBoundaryPortals(graph, nodes, doors, doorDetectionRadiusSquared);
        RegisterCityPortals(graph, rules);
        _gridGraphs[gridUid] = graph;
        InvalidateGraph(gridUid);
    }

    private Dictionary<NCLocalNavigationNodeKey, NavigationNode> BuildNavigationNodes(
        EntityUid gridUid,
        float doorDetectionRadius,
        out List<DoorPoint> doors)
    {
        doors = CollectDoors(gridUid);
        var radiusSquared = doorDetectionRadius * doorDetectionRadius;
        var nodes = new Dictionary<NCLocalNavigationNodeKey, NavigationNode>();
        foreach (var snapshot in _navigationSnapshot)
        {
            var mapCoordinates = _transform.ToMapCoordinates(snapshot.Coordinates);
            if (!_coordinates.TryConvert(mapCoordinates, out var cityCoordinates))
                continue;

            var zoneId = _zones.GetLocationContext(cityCoordinates).MostSpecific;
            if (!zoneId.IsValid)
                continue;

            var controller = FindNearestDoor(mapCoordinates.Position, doors, radiusSquared);
            var isDoor = (snapshot.Flags & PathfindingBreadcrumbFlag.Door) != 0 || controller.IsValid();
            nodes.Add(snapshot.Key, new NavigationNode(snapshot, cityCoordinates, zoneId, isDoor, controller));
        }

        return nodes;
    }

    private List<DoorPoint> CollectDoors(EntityUid gridUid)
    {
        var doors = new List<DoorPoint>();
        if (!TryComp(gridUid, out MapGridComponent? grid))
            return doors;

        _lookupCandidates.Clear();
        _lookup.GetEntitiesIntersecting(gridUid, grid.LocalAABB, _lookupCandidates, LookupFlags.Static | LookupFlags.Uncontained);
        foreach (var uid in _lookupCandidates)
        {
            if (!HasComp<DoorComponent>(uid) || !TryComp(uid, out TransformComponent? transform))
                continue;
            doors.Add(new DoorPoint(uid, _transform.GetMapCoordinates(uid, transform).Position));
        }

        doors.Sort(static (left, right) => left.Uid.Id.CompareTo(right.Uid.Id));
        return doors;
    }

    private void BuildRooms(GridGraph graph, Dictionary<NCLocalNavigationNodeKey, NavigationNode> nodes)
    {
        var visited = new HashSet<NCLocalNavigationNodeKey>();
        foreach (var seed in nodes.Values.OrderBy(static node => node.Snapshot.Key.ChunkOrigin.X)
                     .ThenBy(static node => node.Snapshot.Key.ChunkOrigin.Y)
                     .ThenBy(static node => node.Snapshot.Key.TileIndex))
        {
            if (seed.IsDoor || !visited.Add(seed.Snapshot.Key))
                continue;

            var roomId = NextRoomId();
            var room = new RuntimeRoom(roomId, graph.GridUid, seed.ZoneId, seed.CityCoordinates);
            var frontier = new Queue<NCLocalNavigationNodeKey>();
            frontier.Enqueue(seed.Snapshot.Key);
            while (frontier.TryDequeue(out var key))
            {
                var node = nodes[key];
                room.Nodes.Add(key);
                graph.NodeRooms.Add(key, roomId);
                _nodeRooms.Add(key, roomId);
                foreach (var neighborKey in node.Snapshot.Neighbors)
                {
                    if (!nodes.TryGetValue(neighborKey, out var neighbor) ||
                        neighbor.IsDoor ||
                        neighbor.ZoneId != room.ZoneId ||
                        !visited.Add(neighborKey))
                    {
                        continue;
                    }

                    frontier.Enqueue(neighborKey);
                }
            }

            graph.Rooms.Add(roomId);
            _rooms.Add(roomId, room);
        }
    }

    private void BuildDoorPortals(
        GridGraph graph,
        Dictionary<NCLocalNavigationNodeKey, NavigationNode> nodes,
        IReadOnlyList<DoorPoint> doors,
        float doorDetectionRadiusSquared)
    {
        var visited = new HashSet<NCLocalNavigationNodeKey>();
        foreach (var seed in nodes.Values)
        {
            if (!seed.IsDoor || !visited.Add(seed.Snapshot.Key))
                continue;

            var frontier = new Queue<NCLocalNavigationNodeKey>();
            var adjacentRooms = new Dictionary<NCRoomNodeId, NavigationNode>();
            var controller = seed.Controller;
            frontier.Enqueue(seed.Snapshot.Key);
            while (frontier.TryDequeue(out var key))
            {
                var node = nodes[key];
                if (!controller.IsValid() && node.Controller.IsValid())
                    controller = node.Controller;
                foreach (var neighborKey in node.Snapshot.Neighbors)
                {
                    if (!nodes.TryGetValue(neighborKey, out var neighbor))
                        continue;
                    if (neighbor.IsDoor)
                    {
                        if (visited.Add(neighborKey))
                            frontier.Enqueue(neighborKey);
                        continue;
                    }

                    if (graph.NodeRooms.TryGetValue(neighborKey, out var roomId))
                        adjacentRooms.TryAdd(roomId, neighbor);
                }
            }

            AddRoomPairs(
                graph,
                adjacentRooms,
                NCRoomPortalKind.Door,
                controller,
                doors,
                doorDetectionRadiusSquared);
        }
    }

    private void BuildOpenBoundaryPortals(
        GridGraph graph,
        Dictionary<NCLocalNavigationNodeKey, NavigationNode> nodes,
        IReadOnlyList<DoorPoint> doors,
        float doorDetectionRadiusSquared)
    {
        var seen = new HashSet<RoomPair>();
        foreach (var node in nodes.Values)
        {
            if (node.IsDoor || !graph.NodeRooms.TryGetValue(node.Snapshot.Key, out var roomA))
                continue;
            foreach (var neighborKey in node.Snapshot.Neighbors)
            {
                if (!nodes.TryGetValue(neighborKey, out var neighbor) ||
                    neighbor.IsDoor ||
                    !graph.NodeRooms.TryGetValue(neighborKey, out var roomB) ||
                    roomA == roomB)
                {
                    continue;
                }

                var pair = RoomPair.Create(roomA, roomB);
                if (!seen.Add(pair))
                    continue;

                var midpoint = (node.CityCoordinates.Position + neighbor.CityCoordinates.Position) * 0.5f;
                var controller = FindNearestDoor(midpoint, doors, doorDetectionRadiusSquared);
                var kind = controller.IsValid() ? NCRoomPortalKind.Door : NCRoomPortalKind.OpenBoundary;
                AddPortal(graph, roomA, roomB, node.CityCoordinates, neighbor.CityCoordinates, kind, controller);
            }
        }
    }

    private void AddRoomPairs(
        GridGraph graph,
        Dictionary<NCRoomNodeId, NavigationNode> adjacentRooms,
        NCRoomPortalKind kind,
        EntityUid controller,
        IReadOnlyList<DoorPoint> doors,
        float doorDetectionRadiusSquared)
    {
        var rooms = adjacentRooms.Keys.OrderBy(static id => id.Value).ToArray();
        for (var i = 0; i < rooms.Length; i++)
        {
            for (var j = i + 1; j < rooms.Length; j++)
            {
                var nodeA = adjacentRooms[rooms[i]];
                var nodeB = adjacentRooms[rooms[j]];
                if (!controller.IsValid())
                {
                    var midpoint = (nodeA.CityCoordinates.Position + nodeB.CityCoordinates.Position) * 0.5f;
                    controller = FindNearestDoor(midpoint, doors, doorDetectionRadiusSquared);
                }

                AddPortal(
                    graph,
                    rooms[i],
                    rooms[j],
                    nodeA.CityCoordinates,
                    nodeB.CityCoordinates,
                    kind,
                    controller);
            }
        }
    }

    private void AddPortal(
        GridGraph graph,
        NCRoomNodeId roomA,
        NCRoomNodeId roomB,
        NCMapCoordinates targetA,
        NCMapCoordinates targetB,
        NCRoomPortalKind kind,
        EntityUid controller)
    {
        if (roomA == roomB)
            return;

        var portalId = NextPortalId();
        var portal = new RuntimePortal(portalId, roomA, roomB, targetA, targetB, kind, controller);
        _portals.Add(portalId, portal);
        AddOutgoing(roomA, portalId);
        AddOutgoing(roomB, portalId);
        graph.Portals.Add(portalId);
    }

    private void RegisterCityPortals(GridGraph graph, NCCityConnectivityRulesPrototype rules)
    {
        foreach (var roomPortalId in graph.Portals)
        {
            var portal = _portals[roomPortalId];
            var roomA = _rooms[portal.RoomA];
            var roomB = _rooms[portal.RoomB];
            if (roomA.ZoneId == roomB.ZoneId)
                continue;

            var profile = portal.Kind == NCRoomPortalKind.Door
                ? rules.DoorProfile
                : rules.OpenBoundaryProfile;
            var kind = portal.Kind == NCRoomPortalKind.Door
                ? NCCityPortalKind.Door
                : NCCityPortalKind.OpenBoundary;

            if (_city.TryRegisterPortal(new NCCityPortalRegistration(
                    roomA.ZoneId,
                    roomB.ZoneId,
                    portal.TargetA,
                    portal.TargetB,
                    kind,
                    profile,
                    default,
                    portal.Controller),
                out var forward))
            {
                graph.CityPortals.Add(forward);
            }

            if (_city.TryRegisterPortal(new NCCityPortalRegistration(
                    roomB.ZoneId,
                    roomA.ZoneId,
                    portal.TargetB,
                    portal.TargetA,
                    kind,
                    profile,
                    default,
                    portal.Controller),
                out var reverse))
            {
                graph.CityPortals.Add(reverse);
            }
        }
    }

    private void RemoveGridGraph(EntityUid gridUid, bool invalidate)
    {
        if (!_gridGraphs.Remove(gridUid, out var graph))
            return;

        foreach (var cityPortal in graph.CityPortals)
            _city.RemovePortal(cityPortal);
        foreach (var portalId in graph.Portals)
        {
            if (!_portals.Remove(portalId, out var portal))
                continue;
            RemoveOutgoing(portal.RoomA, portalId);
            RemoveOutgoing(portal.RoomB, portalId);
        }
        foreach (var roomId in graph.Rooms)
            _rooms.Remove(roomId);
        foreach (var key in graph.NodeRooms.Keys)
            _nodeRooms.Remove(key);

        if (invalidate)
            InvalidateGraph(gridUid);
    }

    private bool TryGetRules(out NCCityConnectivityRulesPrototype rules)
    {
        var loaded = _prototypes.EnumeratePrototypes<NCCityConnectivityRulesPrototype>().ToArray();
        if (loaded.Length == 1)
        {
            rules = loaded[0];
            return true;
        }

        Log.Error($"Expected exactly one city connectivity rules prototype, found {loaded.Length}.");
        rules = default!;
        return false;
    }

    private static EntityUid FindNearestDoor(Vector2 position, IReadOnlyList<DoorPoint> doors, float maximumDistanceSquared)
    {
        var best = EntityUid.Invalid;
        var bestDistance = maximumDistanceSquared;
        foreach (var door in doors)
        {
            var distance = Vector2.DistanceSquared(position, door.Position);
            if (distance > bestDistance)
                continue;
            if (distance.Equals(bestDistance) && best.IsValid() && door.Uid.Id >= best.Id)
                continue;
            best = door.Uid;
            bestDistance = distance;
        }

        return best;
    }

    private void AddOutgoing(NCRoomNodeId roomId, NCRoomPortalId portalId)
    {
        if (!_outgoing.TryGetValue(roomId, out var outgoing))
            _outgoing[roomId] = outgoing = [];
        outgoing.Add(portalId);
    }

    private void RemoveOutgoing(NCRoomNodeId roomId, NCRoomPortalId portalId)
    {
        if (!_outgoing.TryGetValue(roomId, out var outgoing))
            return;
        outgoing.Remove(portalId);
        if (outgoing.Count == 0)
            _outgoing.Remove(roomId);
    }

    private NCRoomNodeId NextRoomId()
    {
        if (_nextRoomId == long.MaxValue)
            throw new InvalidOperationException("The round-local room ID space has been exhausted.");
        return new NCRoomNodeId(_nextRoomId++);
    }

    private NCRoomPortalId NextPortalId()
    {
        if (_nextPortalId == long.MaxValue)
            throw new InvalidOperationException("The round-local room portal ID space has been exhausted.");
        return new NCRoomPortalId(_nextPortalId++);
    }

    private void InvalidateGraph(EntityUid affectedGrid = default)
    {
        var oldVersion = GraphVersion;
        GraphVersion = GraphVersion == long.MaxValue ? 1 : GraphVersion + 1;
        var ev = new NCRoomPortalGraphChangedEvent(oldVersion, GraphVersion, affectedGrid);
        RaiseLocalEvent(ref ev);
    }

    private static NCRoomNodeInfo ToInfo(RuntimeRoom room)
    {
        return new NCRoomNodeInfo(room.Id, room.GridUid, room.ZoneId, room.Anchor, room.Nodes.Count);
    }

    private static NCRoomPortalInfo ToInfo(RuntimePortal portal)
    {
        return new NCRoomPortalInfo(
            portal.Id,
            portal.RoomA,
            portal.RoomB,
            portal.TargetA,
            portal.TargetB,
            portal.Kind,
            portal.Controller);
    }

    private sealed class GridGraph(EntityUid gridUid, long pathfindingRevision)
    {
        public readonly EntityUid GridUid = gridUid;
        public readonly long PathfindingRevision = pathfindingRevision;
        public readonly HashSet<NCRoomNodeId> Rooms = [];
        public readonly HashSet<NCRoomPortalId> Portals = [];
        public readonly HashSet<NCCityPortalId> CityPortals = [];
        public readonly Dictionary<NCLocalNavigationNodeKey, NCRoomNodeId> NodeRooms = [];
        public readonly List<NCRoomNavigationSample> NavigationSamples = [];
    }

    private sealed class RuntimeRoom(
        NCRoomNodeId id,
        EntityUid gridUid,
        NCZoneId zoneId,
        NCMapCoordinates anchor)
    {
        public readonly NCRoomNodeId Id = id;
        public readonly EntityUid GridUid = gridUid;
        public readonly NCZoneId ZoneId = zoneId;
        public readonly NCMapCoordinates Anchor = anchor;
        public readonly HashSet<NCLocalNavigationNodeKey> Nodes = [];
    }

    private sealed record RuntimePortal(
        NCRoomPortalId Id,
        NCRoomNodeId RoomA,
        NCRoomNodeId RoomB,
        NCMapCoordinates TargetA,
        NCMapCoordinates TargetB,
        NCRoomPortalKind Kind,
        EntityUid Controller);

    private sealed record NavigationNode(
        NCLocalNavigationNodeSnapshot Snapshot,
        NCMapCoordinates CityCoordinates,
        NCZoneId ZoneId,
        bool IsDoor,
        EntityUid Controller);

    private readonly record struct DoorPoint(EntityUid Uid, Vector2 Position);

    private readonly record struct RoomPair(NCRoomNodeId First, NCRoomNodeId Second)
    {
        public static RoomPair Create(NCRoomNodeId left, NCRoomNodeId right)
        {
            return left.Value < right.Value ? new RoomPair(left, right) : new RoomPair(right, left);
        }
    }
}
