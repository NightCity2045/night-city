// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.Server._NC.City.Connectivity;
using Content.Server._NC.ZLevels.Core;
using Content.Server.NPC.Pathfinding;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.ZLevels.Core.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.City.Connectivity;

[TestFixture]
[TestOf(typeof(NCRoomPortalGraphSystem))]
public sealed class NCRoomPortalGraphSystemTest : GameTest
{
    [TestPrototypes]
    private const string ZoneSetPrototype = """
        - type: ncZoneSet
          id: NCRoomPortalIntegrationZones
          networkId: 63f150d6-f68a-477b-881c-3d214d23a034
          zones:
          - id: 53c15485-99f2-44ae-9436-68631aeb4e5e
            kind: District
            name: Room Graph District
            geometry:
            - kind: Polygon
              global: true
              vertices:
              - 0,0
              - 8,0
              - 8,2
              - 0,2
          - id: 75cc0f93-41de-4ee5-85bb-3f2784208771
            kind: Street
            parent: 53c15485-99f2-44ae-9436-68631aeb4e5e
            name: Room Graph Street
            geometry:
            - kind: Polygon
              z: 0
              vertices:
              - 0,0
              - 8,0
              - 8,2
              - 0,2
          - id: 404c94ce-b029-4248-a5e9-857d35ee6825
            kind: Building
            parent: 75cc0f93-41de-4ee5-85bb-3f2784208771
            name: Room A
            geometry:
            - kind: Polygon
              z: 0
              vertices:
              - 0,0
              - 3,0
              - 3,2
              - 0,2
          - id: 1531bdda-8459-4792-8cf0-a014020d8b21
            kind: Building
            parent: 75cc0f93-41de-4ee5-85bb-3f2784208771
            name: Room B
            geometry:
            - kind: Polygon
              z: 0
              vertices:
              - 5,0
              - 8,0
              - 8,2
              - 5,2
        """;

    private static readonly NCZNetworkId NetworkId =
        new(Guid.Parse("63f150d6-f68a-477b-881c-3d214d23a034"));
    private static readonly NCZoneId BuildingA =
        new(Guid.Parse("404c94ce-b029-4248-a5e9-857d35ee6825"));
    private static readonly NCZoneId BuildingB =
        new(Guid.Parse("1531bdda-8459-4792-8cf0-a014020d8b21"));
    private static readonly NCZoneId Street =
        new(Guid.Parse("75cc0f93-41de-4ee5-85bb-3f2784208771"));

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    public async Task OpenZoneBoundaryBecomesRoomAndCityPortalWithoutReplacingVanillaGraph()
    {
        var server = Pair.Server;
        var maps = server.System<SharedMapSystem>();
        var entities = server.ResolveDependency<IEntityManager>();
        var zLevels = server.System<NCZLevelsSystem>();
        var pathfinding = server.System<PathfindingSystem>();
        var rooms = server.System<NCRoomPortalGraphSystem>();
        var city = server.System<NCCityConnectivitySystem>();
        var planner = server.System<NCCityNavigationPlannerSystem>();
        var spawns = server.System<NCCitySpawnCandidateSystem>();
        var validation = server.System<NCCityConnectivityValidationSystem>();
        Entity<NCZMapNetworkComponent> network = default;
        Entity<MapGridComponent> grid = default;

        await server.WaitAssertion(() =>
        {
            maps.CreateMap(out var mapId);
            network = zLevels.CreateMapNetwork(NetworkId);
            Assert.That(zLevels.TryAddMapsIntoNetwork(network, new Dictionary<EntityUid, int>
            {
                [maps.GetMap(mapId)] = 0,
            }), Is.True);

            grid = maps.CreateGridEntity(mapId);
            var tiles = new List<(Vector2i, Tile)>();
            for (var x = 0; x < 8; x++)
            {
                for (var y = 0; y < 2; y++)
                    tiles.Add((new Vector2i(x, y), new Tile(1)));
            }
            maps.SetTiles(grid, tiles);
        });

        // Vanilla pathfinding batches dirty chunks for 0.45 seconds before publishing the immutable NC snapshot.
        await server.WaitRunTicks(45);

        await server.WaitAssertion(() =>
        {
            var start = new EntityCoordinates(grid, new Vector2(1.5f, 0.5f));
            var end = new EntityCoordinates(grid, new Vector2(6.5f, 0.5f));
            Assert.That(pathfinding.GetPoly(start), Is.Not.Null);
            Assert.That(pathfinding.GetPoly(end), Is.Not.Null);
            Assert.That(rooms.TryGetRoom(start, out var roomA), Is.True);
            Assert.That(rooms.TryGetRoom(end, out var roomB), Is.True);
            Assert.That(roomB, Is.Not.EqualTo(roomA));
            Assert.That(rooms.TryGetRoomInfo(roomA, out var roomAInfo), Is.True);
            Assert.That(rooms.TryGetRoomInfo(roomB, out var roomBInfo), Is.True);
            Assert.That(roomAInfo.ZoneId, Is.EqualTo(BuildingA));
            Assert.That(roomBInfo.ZoneId, Is.EqualTo(BuildingB));

            var localRoute = new List<NCRoomRouteStep>();
            Assert.That(rooms.TryFindRoute(roomA, roomB, localRoute), Is.True);
            Assert.That(localRoute, Has.Count.EqualTo(2));
            Assert.That(localRoute, Has.All.Matches<NCRoomRouteStep>(step =>
                step.Kind == NCRoomPortalKind.OpenBoundary));

            var cityRoute = new List<NCCityRouteStep>();
            Assert.That(city.TryFindRoute(
                BuildingA,
                BuildingB,
                NCCityTravelMode.Pedestrian,
                cityRoute), Is.True);
            Assert.That(cityRoute, Has.Count.EqualTo(2));
            Assert.That(cityRoute, Has.All.Matches<NCCityRouteStep>(step => !step.Controller.IsValid()));

            var actor = entities.SpawnEntity(null, start);
            var plan = new List<NCCityNavigationWaypoint>();
            Assert.That(planner.TryPlan(
                actor,
                new NCMapCoordinates(NetworkId, new Vector2(6.5f, 0.5f), 0),
                NCCityTravelMode.Pedestrian,
                plan,
                out var version), Is.True);
            Assert.That(version.CityGraphVersion, Is.EqualTo(city.GraphVersion));
            Assert.That(version.RoomGraphVersion, Is.EqualTo(rooms.GraphVersion));
            Assert.That(plan, Has.Count.EqualTo(5));
            Assert.That(plan[0].Kind, Is.EqualTo(NCCityNavigationWaypointKind.CityPortalEntry));
            Assert.That(plan[1].Kind, Is.EqualTo(NCCityNavigationWaypointKind.CityPortalExit));
            Assert.That(plan[2].Kind, Is.EqualTo(NCCityNavigationWaypointKind.CityPortalEntry));
            Assert.That(plan[3].Kind, Is.EqualTo(NCCityNavigationWaypointKind.CityPortalExit));
            Assert.That(plan[4].Kind, Is.EqualTo(NCCityNavigationWaypointKind.Destination));

            var pedestrianProfile = new ProtoId<NCCitySpawnCandidateProfilePrototype>(
                "NCPedestrianSpawnCandidates");
            var candidates = new List<NCCitySpawnCandidateInfo>();
            spawns.GetCandidates(pedestrianProfile, BuildingA, candidates);
            Assert.That(candidates, Is.Not.Empty);
            Assert.That(candidates, Has.All.Matches<NCCitySpawnCandidateInfo>(candidate =>
                candidate.ZoneId == BuildingA));
            Assert.That(spawns.TrySelectCandidate(
                pedestrianProfile,
                BuildingA,
                17,
                out var firstSelection), Is.True);
            Assert.That(spawns.TrySelectCandidate(
                pedestrianProfile,
                BuildingA,
                17,
                out var repeatedSelection), Is.True);
            Assert.That(repeatedSelection.Target, Is.EqualTo(firstSelection.Target));

            var issues = new List<NCCityConnectivityValidationIssue>();
            validation.Validate(issues);
            var localErrors = issues.Where(issue =>
                issue.Severity == NCCityConnectivityIssueSeverity.Error &&
                issue.ZoneId is var zone && (zone == BuildingA || zone == BuildingB || zone == Street));
            Assert.That(localErrors, Is.Empty,
                string.Join(Environment.NewLine, localErrors));
        });

        await server.WaitPost(() => zLevels.DeleteMapNetwork(network));
        await server.WaitRunTicks(1);
    }
}
