// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.GameTicking;
using Content.Server._NC.City.ZoneTemplates;
using Content.Server._NC.Housing;
using Content.Server._NC.Housing.Components;
using Content.Server._NC.LivingCity.Housing;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Missions;
using Content.Server._NC.ZLevels.Core;
using Content.Server.Database;
using Content.Shared._NC.City.ZoneTemplates;
using Content.Shared._NC.City.ZoneTemplates.Components;
using Content.Shared._NC.City.ZoneTemplates.Prototypes;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Missions.Components;
using Content.Shared._NC.Missions.Prototypes;
using Content.Shared._NC.Housing.Components;
using Content.Shared._NC.Housing.Prototypes;
using Content.Shared._NC.Identity;
using Content.Shared._NC.LivingCity.Housing;
using Content.Shared._NC.Missions;
using Content.Shared.Maps;
using Content.Shared.CCVar;
using Content.Shared.Preferences;
using Robust.Server.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.City.ZoneTemplates;

[TestFixture]
[NonParallelizable]
public sealed class NCDirectZoneLayoutPrototypeTest : GameTest
{
    [TestPrototypes]
    private const string ExpiringMissionPrototype = """
        - type: ncMissionDefinition
          id: NCMissionExpiryIntegration
          template: NCMissionLayout8x8
          maximumParticipants: 1
          runDuration: 2s
          evacuationTime: 1s
        - type: ncZoneSet
          id: NCZoneTemplateIntegrationZones
          networkId: 4da240d7-48ad-4f95-ae96-aeb7b58b855d
          zones:
          - id: 33ec6662-1362-4a4d-a24e-3377dfaf0e80
            kind: District
            name: Template Test District
            geometry:
            - kind: Volume
              global: true
              vertices:
              - -10,10
              - 10,10
              - 10,32
              - -10,32
          - id: 85b1e2e2-6bea-4203-9621-106fd901d814
            kind: Building
            parent: 33ec6662-1362-4a4d-a24e-3377dfaf0e80
            name: Template Test Building
            geometry:
            - kind: Volume
              minZ: 0
              maxZ: 1
              vertices:
              - -6,16
              - 6,16
              - 6,28
              - -6,28
          - id: 67566dd3-93b9-4ec6-a823-772e5fc376fd
            kind: Apartment
            parent: 85b1e2e2-6bea-4203-9621-106fd901d814
            name: Template Test Apartment
            geometry:
            - kind: Polygon
              z: 0
              vertices:
              - -4,21
              - 1,21
              - 1,26
              - -4,26
          - id: 8d7b2c44-9a31-4f67-b8e2-6c5d4a3f1808
            kind: MissionSlot
            parent: 85b1e2e2-6bea-4203-9621-106fd901d814
            name: Template Test Mission Slot
            geometry:
            - kind: Polygon
              z: 0
              vertices:
              - -4,18
              - 4,18
              - 4,26
              - -4,26
            - kind: Polygon
              z: 1
              vertices:
              - -4,18
              - 4,18
              - 4,26
              - -4,26
        """;

    private static readonly ProtoId<NCZoneTemplatePrototype> ApartmentLayout = "NCApartmentLayout5x5";
    private static readonly ProtoId<NCZoneTemplatePrototype> MissionLayout = "NCMissionLayout8x8";
    private static readonly ProtoId<NCMissionDefinitionPrototype> MissionDefinition = "NCMission8x8";
    private const string ExpiringMissionDefinition = "NCMissionExpiryIntegration";
    private static readonly ProtoId<NCPropertyListingPrototype> ApartmentListing = "NCGlen1Apartment01";
    private static readonly NCZNetworkId CityNetworkId =
        new(Guid.Parse("4da240d7-48ad-4f95-ae96-aeb7b58b855d"));
    private static readonly NCBuildingId ApartmentBuildingId =
        new(new NCZoneId(Guid.Parse("85b1e2e2-6bea-4203-9621-106fd901d814")));
    private static readonly NCZoneId ApartmentZoneId =
        new(Guid.Parse("67566dd3-93b9-4ec6-a823-772e5fc376fd"));
    private static readonly NCZoneId MissionZoneId =
        new(Guid.Parse("8d7b2c44-9a31-4f67-b8e2-6c5d4a3f1808"));

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.GridFill), false)]
    public async Task PreparedLayoutsLoadAndUseTheirStableZoneIds()
    {
        var server = Pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var loader = entities.System<MapLoaderSystem>();
        var maps = entities.System<MapSystem>();

        await server.WaitAssertion(() =>
        {
            Assert.That(prototypes.TryIndex(ApartmentLayout, out var apartment), Is.True);
            Assert.That(apartment!.TargetZoneId,
                Is.EqualTo(new NCZoneId(Guid.Parse("67566dd3-93b9-4ec6-a823-772e5fc376fd"))));
            Assert.Multiple(() =>
            {
                Assert.That(apartment.TilePolicy, Is.EqualTo(NCZoneTemplateTilePolicy.ReplaceNonEmpty));
                Assert.That(apartment.EntityPolicy, Is.EqualTo(NCZoneTemplateEntityPolicy.InteriorOnly));
                Assert.That(apartment.CachePolicy, Is.EqualTo(NCZoneTemplateCachePolicy.Always));
            });
            AssertLayout<NCPropertyLockComponent>(entities, loader, maps, apartment, 25, 0, 6);

            Assert.That(prototypes.TryIndex(ApartmentListing, out var listing), Is.True);
            Assert.That(listing!.ApartmentId, Is.EqualTo(new NCApartmentId(apartment.TargetZoneId)));
            Assert.That(listing.Interiors, Has.Count.EqualTo(1));
            Assert.That(listing.Interiors[0].Template, Is.EqualTo(ApartmentLayout));
            Assert.That(listing.Interiors[0].RentPolicy.Id, Is.EqualTo("StandardApartment"));
            Assert.That(prototypes.TryIndex(listing.Interiors[0].RentPolicy, out NCRentPolicyPrototype rentPolicy),
                Is.True);
            Assert.That(rentPolicy!.RentAmount, Is.EqualTo(500));

            Assert.That(prototypes.TryIndex(MissionLayout, out var mission), Is.True);
            Assert.That(mission!.TargetZoneId, Is.EqualTo(MissionZoneId));
            Assert.Multiple(() =>
            {
                Assert.That(mission.TilePolicy, Is.EqualTo(NCZoneTemplateTilePolicy.ReplaceNonEmpty));
                Assert.That(mission.EntityPolicy, Is.EqualTo(NCZoneTemplateEntityPolicy.InteriorOnly));
                Assert.That(mission.CachePolicy, Is.EqualTo(NCZoneTemplateCachePolicy.Always));
                Assert.That(mission.Floors, Has.Count.EqualTo(2));
            });
            AssertMissionFloor(entities, loader, maps, mission, 0, expectedEntities: 11, expectedEvacuationPoints: 1);
            AssertMissionFloor(entities, loader, maps, mission, 1, expectedEntities: 10, expectedEvacuationPoints: 0);

            Assert.That(prototypes.TryIndex(MissionDefinition, out var definition), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(definition!.Template, Is.EqualTo(MissionLayout));
                Assert.That(definition.RunDuration, Is.EqualTo(TimeSpan.FromMinutes(15)));
                Assert.That(definition.EvacuationTime, Is.EqualTo(TimeSpan.FromMinutes(2)));
            });
        });

        await RunUntilSynced();
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.GridFill), false)]
    public async Task PreparedApartmentMovesIntoCityAndRestoresShellFloor()
    {
        var server = Pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var tiles = server.ResolveDependency<ITileDefinitionManager>();
        var maps = entities.System<MapSystem>();
        var zLevels = entities.System<NCZLevelsSystem>();
        var templates = entities.System<NCZoneTemplateSystem>();

        EntityUid gridUid = default;
        MapGridComponent grid = default!;
        Entity<Content.Shared._NC.ZLevels.Core.Components.NCZMapNetworkComponent> network = default;
        NCZoneDeploymentId deploymentId = default;
        var concrete = new Tile(tiles["FloorConcrete"].TileId);
        var wood = new Tile(tiles["FloorWoodTile"].TileId);

        await server.WaitAssertion(() =>
        {
            maps.CreateMap(out var mapId);
            var createdGrid = maps.CreateGridEntity(mapId);
            gridUid = createdGrid.Owner;
            grid = createdGrid.Comp;

            var baseline = new List<(Vector2i GridIndices, Tile Tile)>();
            for (var y = 21; y < 26; y++)
            {
                for (var x = -4; x < 1; x++)
                    baseline.Add((new Vector2i(x, y), concrete));
            }
            maps.SetTiles(gridUid, grid, baseline);

            network = zLevels.CreateMapNetwork(CityNetworkId);
            Assert.That(zLevels.TryAddMapsIntoNetwork(network, new Dictionary<EntityUid, int>
            {
                [maps.GetMap(mapId)] = 0,
            }), Is.True);

            Assert.That(templates.TryRequestDeployment(new NCZoneDeploymentRequest(
                default,
                ApartmentZoneId,
                ApartmentLayout,
                NCZoneDeploymentPurpose.ApartmentInterior,
                null,
                null), out deploymentId, out var error), Is.True, error);
        });

        await WaitForState(server, templates, deploymentId, NCZoneDeploymentState.Active);
        await server.WaitAssertion(() =>
        {
            Assert.That(maps.GetTileRef(gridUid, grid, new Vector2i(-4, 21)).Tile.TypeId, Is.EqualTo(wood.TypeId));
            // Six mapper roots plus their six MapInit-created actions/container contents share deployment provenance.
            Assert.That(CountDeploymentEntities(entities, deploymentId), Is.EqualTo(12));
        });

        await server.WaitAssertion(() =>
            Assert.That(templates.TryCloseDeployment(deploymentId, out var error), Is.True, error));
        await WaitForState(server, templates, deploymentId, NCZoneDeploymentState.Completed);
        await server.WaitRunTicks(2);
        await server.WaitAssertion(() =>
        {
            Assert.That(maps.GetTileRef(gridUid, grid, new Vector2i(-4, 21)).Tile.TypeId,
                Is.EqualTo(concrete.TypeId));
            Assert.That(CountDeploymentEntities(entities, deploymentId), Is.Zero);
            zLevels.DeleteMapNetwork(network);
        });
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.GridFill), false)]
    public async Task PreparedMissionDeploysBothPublicFloorsAndRestoresThem()
    {
        var server = Pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var tiles = server.ResolveDependency<ITileDefinitionManager>();
        var maps = entities.System<MapSystem>();
        var zLevels = entities.System<NCZLevelsSystem>();
        var templates = entities.System<NCZoneTemplateSystem>();

        EntityUid groundGridUid = default;
        EntityUid upperGridUid = default;
        MapGridComponent groundGrid = default!;
        MapGridComponent upperGrid = default!;
        Entity<Content.Shared._NC.ZLevels.Core.Components.NCZMapNetworkComponent> network = default;
        NCZoneDeploymentId deploymentId = default;
        var wood = new Tile(tiles["FloorWoodTile"].TileId);
        var concrete = new Tile(tiles["FloorConcrete"].TileId);

        await server.WaitAssertion(() =>
        {
            maps.CreateMap(out var groundMapId);
            maps.CreateMap(out var upperMapId);
            var createdGroundGrid = maps.CreateGridEntity(groundMapId);
            var createdUpperGrid = maps.CreateGridEntity(upperMapId);
            groundGridUid = createdGroundGrid.Owner;
            upperGridUid = createdUpperGrid.Owner;
            groundGrid = createdGroundGrid.Comp;
            upperGrid = createdUpperGrid.Comp;

            var baseline = new List<(Vector2i GridIndices, Tile Tile)>();
            for (var y = 18; y < 26; y++)
            {
                for (var x = -4; x < 4; x++)
                    baseline.Add((new Vector2i(x, y), wood));
            }
            maps.SetTiles(groundGridUid, groundGrid, baseline);
            maps.SetTiles(upperGridUid, upperGrid, baseline);

            network = zLevels.CreateMapNetwork(CityNetworkId);
            Assert.That(zLevels.TryAddMapsIntoNetwork(network, new Dictionary<EntityUid, int>
            {
                [maps.GetMap(groundMapId)] = 0,
                [maps.GetMap(upperMapId)] = 1,
            }), Is.True);

            Assert.That(templates.TryRequestDeployment(new NCZoneDeploymentRequest(
                default,
                MissionZoneId,
                MissionLayout,
                NCZoneDeploymentPurpose.MissionRun,
                null,
                Guid.NewGuid()), out deploymentId, out var error), Is.True, error);
        });

        await WaitForState(server, templates, deploymentId, NCZoneDeploymentState.Active);
        await server.WaitAssertion(() =>
        {
            Assert.That(maps.GetTileRef(groundGridUid, groundGrid, new Vector2i(-4, 18)).Tile.TypeId,
                Is.EqualTo(concrete.TypeId));
            Assert.That(maps.GetTileRef(upperGridUid, upperGrid, new Vector2i(-4, 18)).Tile.TypeId,
                Is.EqualTo(concrete.TypeId));

            var sourceFloors = new HashSet<int>();
            var dynamicPoints = 0;
            var evacuationPoints = 0;
            var resolvedTargets = new HashSet<int>();
            var query = entities.EntityQueryEnumerator<NCZoneDeploymentComponent>();
            while (query.MoveNext(out var uid, out var deployment))
            {
                if (deployment.DeploymentId != deploymentId.Value)
                    continue;

                sourceFloors.Add(deployment.SourceFloor);
                if (entities.HasComponent<NCMissionDynamicPointComponent>(uid))
                    dynamicPoints++;
                if (entities.HasComponent<NCMissionEvacuationPointComponent>(uid))
                    evacuationPoints++;
                if (entities.TryGetComponent(uid, out NCResolvedVerticalLinkComponent link))
                    resolvedTargets.Add(link.Target.Z);
            }

            Assert.Multiple(() =>
            {
                Assert.That(sourceFloors, Is.EquivalentTo(new[] { 0, 1 }));
                Assert.That(dynamicPoints, Is.EqualTo(6));
                Assert.That(evacuationPoints, Is.EqualTo(1));
                Assert.That(resolvedTargets, Is.EquivalentTo(new[] { 0, 1 }));
            });
        });

        await server.WaitAssertion(() =>
            Assert.That(templates.TryCloseDeployment(deploymentId, out var error), Is.True, error));
        await WaitForState(server, templates, deploymentId, NCZoneDeploymentState.Completed);
        await server.WaitRunTicks(2);
        await server.WaitAssertion(() =>
        {
            Assert.That(maps.GetTileRef(groundGridUid, groundGrid, new Vector2i(-4, 18)).Tile.TypeId,
                Is.EqualTo(wood.TypeId));
            Assert.That(maps.GetTileRef(upperGridUid, upperGrid, new Vector2i(-4, 18)).Tile.TypeId,
                Is.EqualTo(wood.TypeId));
            Assert.That(CountDeploymentEntities(entities, deploymentId), Is.Zero);
            zLevels.DeleteMapNetwork(network);
        });
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.GridFill), false)]
    public async Task TimedMissionEvacuatesExternalMobAndRestoresBothFloors()
    {
        var server = Pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var database = server.ResolveDependency<IServerDbManager>();
        var tiles = server.ResolveDependency<ITileDefinitionManager>();
        var maps = entities.System<MapSystem>();
        var coordinates = entities.System<Content.Shared._NC.Coordinates.Systems.NCMapCoordinatesSystem>();
        var zones = entities.System<Content.Server._NC.City.Zones.NCZoneSystem>();
        var zLevels = entities.System<NCZLevelsSystem>();
        var missions = entities.System<NCMissionSystem>();

        var preference = await database.InitPrefsAsync(
            new NetUserId(Guid.NewGuid()),
            new HumanoidCharacterProfile(),
            CancellationToken.None);
        var characterId = new NCCharacterId(preference.Profiles.Single().Id);

        EntityUid groundGridUid = default;
        EntityUid upperGridUid = default;
        MapGridComponent groundGrid = default!;
        MapGridComponent upperGrid = default!;
        EntityUid externalMob = default;
        Entity<Content.Shared._NC.ZLevels.Core.Components.NCZMapNetworkComponent> network = default;
        var wood = new Tile(tiles["FloorWoodTile"].TileId);

        await server.WaitAssertion(() =>
        {
            maps.CreateMap(out var groundMapId);
            maps.CreateMap(out var upperMapId);
            var createdGroundGrid = maps.CreateGridEntity(groundMapId);
            var createdUpperGrid = maps.CreateGridEntity(upperMapId);
            groundGridUid = createdGroundGrid.Owner;
            upperGridUid = createdUpperGrid.Owner;
            groundGrid = createdGroundGrid.Comp;
            upperGrid = createdUpperGrid.Comp;

            var baseline = new List<(Vector2i GridIndices, Tile Tile)>();
            for (var y = 18; y < 26; y++)
            {
                for (var x = -4; x < 4; x++)
                    baseline.Add((new Vector2i(x, y), wood));
            }
            maps.SetTiles(groundGridUid, groundGrid, baseline);
            maps.SetTiles(upperGridUid, upperGrid, baseline);

            network = zLevels.CreateMapNetwork(CityNetworkId);
            Assert.That(zLevels.TryAddMapsIntoNetwork(network, new Dictionary<EntityUid, int>
            {
                [maps.GetMap(groundMapId)] = 0,
                [maps.GetMap(upperMapId)] = 1,
            }), Is.True);
        });

        var start = await missions.TryStartRunAsync(
            ExpiringMissionDefinition,
            [characterId],
            new NCMissionSlotId(MissionZoneId),
            seed: 1);
        Assert.That(start.Success, Is.True, start.Error);
        await WaitForMissionState(server, missions, start.RunId, NCMissionRunState.Active);

        await server.WaitAssertion(() =>
        {
            externalMob = entities.SpawnEntity("MobMonkey",
                new EntityCoordinates(upperGridUid, new Vector2(-2f, 22f)));
            Assert.That(coordinates.TryGetCoordinates(externalMob, out var before), Is.True);
            Assert.That(before.Z, Is.EqualTo(1));
            Assert.That(zones.Contains(MissionZoneId, before), Is.True);
        });

        await Task.Delay(TimeSpan.FromMilliseconds(2100));
        await WaitForMissionState(server, missions, start.RunId, NCMissionRunState.Closed);
        await server.WaitAssertion(() =>
        {
            Assert.That(coordinates.TryGetCoordinates(externalMob, out var after), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(after.Z, Is.Zero);
                Assert.That(zones.Contains(MissionZoneId, after), Is.False);
                Assert.That(maps.GetTileRef(groundGridUid, groundGrid, new Vector2i(-4, 18)).Tile.TypeId,
                    Is.EqualTo(wood.TypeId));
                Assert.That(maps.GetTileRef(upperGridUid, upperGrid, new Vector2i(-4, 18)).Tile.TypeId,
                    Is.EqualTo(wood.TypeId));
            });

            entities.DeleteEntity(externalMob);
            zLevels.DeleteMapNetwork(network);
        });
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.GridFill), false)]
    public async Task PersistedEntityGraphIsPreparedOffMapAndKeepsPositionAndContainerTree()
    {
        var server = Pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var tiles = server.ResolveDependency<ITileDefinitionManager>();
        var maps = entities.System<MapSystem>();
        var loader = entities.System<MapLoaderSystem>();
        var containers = entities.System<SharedContainerSystem>();
        var transforms = entities.System<SharedTransformSystem>();
        var zLevels = entities.System<NCZLevelsSystem>();
        var templates = entities.System<NCZoneTemplateSystem>();

        EntityUid gridUid = default;
        Entity<Content.Shared._NC.ZLevels.Core.Components.NCZMapNetworkComponent> network = default;
        NCZoneDeploymentId firstDeployment = default;
        var savedPosition = new Vector2(-1.25f, 23.75f);
        const float savedRotation = 0.37f;

        await server.WaitAssertion(() =>
        {
            maps.CreateMap(out var mapId);
            var createdGrid = maps.CreateGridEntity(mapId);
            gridUid = createdGrid.Owner;
            var concrete = new Tile(tiles["FloorConcrete"].TileId);
            var baseline = new List<(Vector2i GridIndices, Tile Tile)>();
            for (var y = 21; y < 26; y++)
            {
                for (var x = -4; x < 1; x++)
                    baseline.Add((new Vector2i(x, y), concrete));
            }
            maps.SetTiles(gridUid, createdGrid.Comp, baseline);

            network = zLevels.CreateMapNetwork(CityNetworkId);
            Assert.That(zLevels.TryAddMapsIntoNetwork(network, new Dictionary<EntityUid, int>
            {
                [maps.GetMap(mapId)] = 0,
            }), Is.True);
            Assert.That(templates.TryRequestDeployment(new NCZoneDeploymentRequest(
                default,
                ApartmentZoneId,
                ApartmentLayout,
                NCZoneDeploymentPurpose.ApartmentInterior,
                null,
                null), out firstDeployment, out var error), Is.True, error);
        });

        await WaitForState(server, templates, firstDeployment, NCZoneDeploymentState.Active);

        string entityYaml = string.Empty;
        await server.WaitAssertion(() =>
        {
            var root = entities.SpawnEntity(null, new EntityCoordinates(gridUid, savedPosition));
            var child = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            transforms.SetLocalRotation(root, new Angle(savedRotation));
            entities.AddComponent(root, new NCPropertyResidentItemComponent { PropertyId = ApartmentZoneId });
            entities.AddComponent(child, new NCPropertyResidentItemComponent { PropertyId = ApartmentZoneId });
            var container = containers.EnsureContainer<Container>(root, "snapshot-test");
            Assert.That(containers.Insert(child, container, force: true), Is.True);

            using var writer = new StringWriter();
            Assert.That(loader.TrySaveGeneric([root], writer, out _, SerializationOptions.Default with
            {
                MissingEntityBehaviour = MissingEntityBehaviour.Ignore,
                ErrorOnOrphan = false,
            }), Is.True);
            entityYaml = writer.ToString();
            entities.DeleteEntity(root);
        });

        await server.WaitAssertion(() =>
            Assert.That(templates.TryCloseDeployment(firstDeployment, out var error), Is.True, error));
        await WaitForState(server, templates, firstDeployment, NCZoneDeploymentState.Completed);

        NCZoneDeploymentId restoredDeployment = default;
        await server.WaitAssertion(() =>
        {
            templates.ReplacePreparedEntitySnapshots(new Dictionary<ProtoId<NCZoneTemplatePrototype>,
                NCZoneTemplateEntitySnapshot>
            {
                [ApartmentLayout] = new(1, new Dictionary<int, NCZoneTemplateEntitySnapshotFloor>
                {
                    [0] = new(0, -4f, 21f, entityYaml),
                }),
            });
            Assert.That(templates.TryRequestDeployment(new NCZoneDeploymentRequest(
                default,
                ApartmentZoneId,
                ApartmentLayout,
                NCZoneDeploymentPurpose.ApartmentInterior,
                null,
                null), out restoredDeployment, out var error), Is.True, error);
        });

        await WaitForState(server, templates, restoredDeployment, NCZoneDeploymentState.Active);
        await server.WaitAssertion(() =>
        {
            var residents = new List<EntityUid>();
            var query = entities.EntityQueryEnumerator<NCPropertyResidentItemComponent>();
            while (query.MoveNext(out var uid, out var resident))
            {
                if (resident.PropertyId == ApartmentZoneId)
                    residents.Add(uid);
            }

            Assert.That(residents, Has.Count.EqualTo(2));
            var restoredRoot = residents.Single(uid =>
                entities.GetComponent<TransformComponent>(uid).ParentUid == gridUid);
            var restoredChild = residents.Single(uid => uid != restoredRoot);
            var transform = entities.GetComponent<TransformComponent>(restoredRoot);
            Assert.Multiple(() =>
            {
                Assert.That(transform.LocalPosition, Is.Approximately(savedPosition));
                Assert.That(transform.LocalRotation.Theta, Is.EqualTo(savedRotation).Within(0.001f));
                Assert.That(entities.GetComponent<TransformComponent>(restoredChild).ParentUid,
                    Is.EqualTo(restoredRoot));
                Assert.That(entities.HasComponent<NCZoneDeploymentComponent>(restoredRoot), Is.True);
                Assert.That(entities.HasComponent<NCZoneDeploymentComponent>(restoredChild), Is.True);
            });
        });

        await server.WaitAssertion(() =>
            Assert.That(templates.TryCloseDeployment(restoredDeployment, out var error), Is.True, error));
        await WaitForState(server, templates, restoredDeployment, NCZoneDeploymentState.Completed);
        await server.WaitAssertion(() => zLevels.DeleteMapNetwork(network));
    }

    [Test]
    [EnsureCVar(Side.Server, typeof(CCVars), nameof(CCVars.GridFill), false)]
    public async Task ActiveApartmentCaptureReachesSnapshotDatabase()
    {
        var server = Pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var tiles = server.ResolveDependency<ITileDefinitionManager>();
        var maps = entities.System<MapSystem>();
        var zLevels = entities.System<NCZLevelsSystem>();
        var templates = entities.System<NCZoneTemplateSystem>();
        var snapshots = entities.System<NCPropertySnapshotSystem>();
        var contents = entities.System<NCPropertyContentSnapshotSystem>();
        var clock = entities.System<NCActiveWorldTimeSystem>();
        var housing = entities.System<NCHousingServiceSystem>();
        var properties = entities.System<NCPropertyRegistrySystem>();

        EntityUid gridUid = default;
        EntityUid residentUid = default;
        Task<bool> saveTask = Task.FromResult(false);
        Entity<Content.Shared._NC.ZLevels.Core.Components.NCZMapNetworkComponent> network = default;
        NCZoneDeploymentId deploymentId = default;
        await server.WaitAssertion(() =>
        {
            maps.CreateMap(out var mapId);
            var createdGrid = maps.CreateGridEntity(mapId);
            gridUid = createdGrid.Owner;
            var concrete = new Tile(tiles["FloorConcrete"].TileId);
            var baseline = new List<(Vector2i GridIndices, Tile Tile)>();
            for (var y = 21; y < 26; y++)
            {
                for (var x = -4; x < 1; x++)
                    baseline.Add((new Vector2i(x, y), concrete));
            }
            maps.SetTiles(gridUid, createdGrid.Comp, baseline);

            network = zLevels.CreateMapNetwork(CityNetworkId);
            Assert.That(zLevels.TryAddMapsIntoNetwork(network, new Dictionary<EntityUid, int>
            {
                [maps.GetMap(mapId)] = 0,
            }), Is.True);
            Assert.That(templates.TryRequestDeployment(new NCZoneDeploymentRequest(
                default,
                ApartmentZoneId,
                ApartmentLayout,
                NCZoneDeploymentPurpose.ApartmentInterior,
                null,
                null), out deploymentId, out var error), Is.True, error);
        });
        await WaitForState(server, templates, deploymentId, NCZoneDeploymentState.Active);

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady, "The active-world clock did not start.");
        Assert.That(await housing.EnsureReadyAsync("property-content-integration-test"), Is.True);

        var propertyId = new NCApartmentId(ApartmentZoneId);
        var registration = await properties.RegisterAsync(new NCRegisterPropertyRequest(
            NCHousingIdentity.Command("register-property", propertyId),
            propertyId,
            ApartmentBuildingId,
            [ApartmentZoneId],
            ApartmentListing.Id,
            housing.Party,
            0,
            "StandardApartment",
            new NCPropertyStorageBudget(128, 128, 256, 512),
            "integration-blueprint",
            NCHousingIdentity.Correlation("register-property", propertyId),
            AuditSource: "housing-round-initialization"));
        Assert.That(registration.Status,
            Is.AnyOf(NCPropertyAggregateMutationStatus.Applied, NCPropertyAggregateMutationStatus.Duplicate));
        var property = registration.Property!;
        var interior = await properties.UpdateAsync(new NCUpdatePropertyRequest(
            Guid.NewGuid(),
            property.AggregateVersion,
            property.Snapshot with
            {
                ActiveDeploymentId = deploymentId,
                CurrentTemplateId = ApartmentLayout.Id,
                CurrentTemplateRevision = 1,
                InteriorState = "active",
            },
            "living-city.property.interior-activated",
            housing.Party,
            Guid.NewGuid(),
            AuditSource: "property-content-integration-test"));
        Assert.That(interior.Status, Is.EqualTo(NCPropertyAggregateMutationStatus.Applied));
        property = interior.Property!;

        await server.WaitAssertion(() =>
        {
            residentUid = entities.SpawnEntity(null, new EntityCoordinates(gridUid, new Vector2(-2.5f, 23.5f)));
            saveTask = snapshots.CapturePropertySnapshotAsync(property.Snapshot);
            Assert.That(entities.HasComponent<NCPropertyResidentItemComponent>(residentUid), Is.True);
        });
        await WaitUntil(server, () => saveTask.IsCompleted,
            "Persistent apartment contents were not committed on the server main thread.");
        Assert.That(await saveTask, Is.True);

        var stored = (await contents.GetAsync(propertyId, true))?.Snapshot;
        Assert.That(stored, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(stored!.TemplateId, Is.EqualTo(ApartmentLayout.Id));
            Assert.That(stored.TemplateRevision, Is.EqualTo(1));
            Assert.That(stored.EntityCount, Is.GreaterThan(0));
            Assert.That(stored.ResidentEntityCount, Is.EqualTo(1));
            Assert.That(stored.Payload, Does.Contain("EntityYaml"));
        });

        await server.WaitAssertion(() =>
            Assert.That(templates.TryCloseDeployment(deploymentId, out var error), Is.True, error));
        await WaitForState(server, templates, deploymentId, NCZoneDeploymentState.Completed);
        await server.WaitAssertion(() => entities.DeleteEntity(residentUid));

        var loadTask = snapshots.LoadPreparedSnapshotsAsync([property]);
        await WaitUntil(server, () => loadTask.IsCompleted,
            "Persistent apartment contents were not applied on the server main thread.");
        await loadTask;
        NCZoneDeploymentId restoredDeployment = default;
        await server.WaitAssertion(() =>
            Assert.That(templates.TryRequestDeployment(new NCZoneDeploymentRequest(
                default,
                ApartmentZoneId,
                ApartmentLayout,
                NCZoneDeploymentPurpose.ApartmentInterior,
                null,
                null), out restoredDeployment, out var error), Is.True, error));
        await WaitForState(server, templates, restoredDeployment, NCZoneDeploymentState.Active);
        await server.WaitAssertion(() =>
        {
            var residents = new List<EntityUid>();
            var query = entities.EntityQueryEnumerator<NCPropertyResidentItemComponent>();
            while (query.MoveNext(out var uid, out var resident))
            {
                if (resident.PropertyId == ApartmentZoneId)
                    residents.Add(uid);
            }

            Assert.That(residents, Has.Count.EqualTo(1));
            Assert.That(entities.GetComponent<TransformComponent>(residents[0]).LocalPosition,
                Is.Approximately(new Vector2(-2.5f, 23.5f)));
        });

        await server.WaitAssertion(() => snapshots.ConfiscateLiveContents(property.Snapshot));
        await server.WaitRunTicks(1);
        await server.WaitAssertion(() =>
        {
            var hasResident = false;
            var query = entities.EntityQueryEnumerator<NCPropertyResidentItemComponent>();
            while (query.MoveNext(out _, out var resident))
            {
                if (resident.PropertyId == ApartmentZoneId)
                    hasResident = true;
            }
            Assert.That(hasResident, Is.False);
            Assert.That(CountDeploymentEntities(entities, restoredDeployment), Is.GreaterThan(0));
        });

        await server.WaitAssertion(() =>
            Assert.That(templates.TryCloseDeployment(restoredDeployment, out var error), Is.True, error));
        await WaitForState(server, templates, restoredDeployment, NCZoneDeploymentState.Completed);
        await server.WaitAssertion(() => zLevels.DeleteMapNetwork(network));
        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
    }

    private static async Task WaitForState(
        RobustIntegrationTest.ServerIntegrationInstance server,
        NCZoneTemplateSystem templates,
        NCZoneDeploymentId deploymentId,
        NCZoneDeploymentState expected)
    {
        for (var tick = 0; tick < 120; tick++)
        {
            await server.WaitRunTicks(1);
            var reached = false;
            await server.WaitAssertion(() =>
            {
                Assert.That(templates.TryGetDeployment(deploymentId, out var status), Is.True);
                Assert.That(status.State, Is.Not.EqualTo(NCZoneDeploymentState.FailedClean), status.Error);
                reached = status.State == expected;
            });
            if (reached)
                return;
        }

        Assert.Fail($"Deployment {deploymentId} did not reach {expected} within 120 ticks.");
    }

    private static async Task WaitUntil(
        RobustIntegrationTest.ServerIntegrationInstance server,
        Func<bool> predicate,
        string failure,
        int attempts = 180)
    {
        for (var i = 0; i < attempts; i++)
        {
            await server.WaitRunTicks(1);
            var complete = false;
            await server.WaitPost(() => complete = predicate());
            if (complete)
                return;
        }

        Assert.Fail(failure);
    }


    private static async Task WaitForMissionState(
        RobustIntegrationTest.ServerIntegrationInstance server,
        NCMissionSystem missions,
        NCMissionRunId runId,
        NCMissionRunState expected)
    {
        for (var tick = 0; tick < 180; tick++)
        {
            await server.WaitRunTicks(1);
            var reached = false;
            await server.WaitAssertion(() =>
            {
                Assert.That(missions.TryGetRun(runId, out var status), Is.True);
                Assert.That(status.State, Is.Not.EqualTo(NCMissionRunState.Failed), status.Error);
                reached = status.State == expected;
            });
            if (reached)
                return;
        }

        Assert.Fail($"Mission {runId} did not reach {expected} within 180 ticks.");
    }

    private static int CountDeploymentEntities(IEntityManager entities, NCZoneDeploymentId deploymentId)
    {
        var count = 0;
        var query = entities.EntityQueryEnumerator<NCZoneDeploymentComponent>();
        while (query.MoveNext(out _, out var deployment))
        {
            if (deployment.DeploymentId == deploymentId.Value)
                count++;
        }
        return count;
    }

    private static void AssertLayout<TComponent>(
        IEntityManager entities,
        MapLoaderSystem loader,
        MapSystem maps,
        NCZoneTemplatePrototype template,
        int expectedTiles,
        int expectedComponents,
        int expectedEntities)
        where TComponent : IComponent
    {
        Assert.That(template.Floors, Has.Count.EqualTo(1));
        maps.CreateMap(out var mapId);
        try
        {
            Assert.That(loader.TryLoadGrid(mapId, template.Floors[0].MapPath, out var loaded), Is.True);
            var (gridUid, grid) = loaded!.Value;
            Assert.That(maps.GetAllTiles(gridUid, grid, ignoreEmpty: true).Count(), Is.EqualTo(expectedTiles));

            var matchingComponents = 0;
            var entityCount = 0;
            var children = entities.GetComponent<TransformComponent>(gridUid).ChildEnumerator;
            while (children.MoveNext(out var child))
            {
                entityCount++;
                if (entities.HasComponent<TComponent>(child))
                    matchingComponents++;
            }

            Assert.Multiple(() =>
            {
                Assert.That(matchingComponents, Is.EqualTo(expectedComponents));
                Assert.That(entityCount, Is.EqualTo(expectedEntities));
            });
        }
        finally
        {
            maps.DeleteMap(mapId);
        }
    }

    private static void AssertMissionFloor(
        IEntityManager entities,
        MapLoaderSystem loader,
        MapSystem maps,
        NCZoneTemplatePrototype template,
        int floorIndex,
        int expectedEntities,
        int expectedEvacuationPoints)
    {
        maps.CreateMap(out var mapId);
        try
        {
            Assert.That(loader.TryLoadGrid(mapId, template.Floors[floorIndex].MapPath, out var loaded), Is.True);
            var (gridUid, grid) = loaded!.Value;
            Assert.That(maps.GetAllTiles(gridUid, grid, ignoreEmpty: true).Count(), Is.EqualTo(64));

            var entityCount = 0;
            var dynamicPoints = 0;
            var verticalLinks = 0;
            var evacuationPoints = 0;
            var children = entities.GetComponent<TransformComponent>(gridUid).ChildEnumerator;
            while (children.MoveNext(out var child))
            {
                entityCount++;
                if (entities.HasComponent<NCMissionDynamicPointComponent>(child))
                    dynamicPoints++;
                if (entities.HasComponent<NCTemplateVerticalLinkMarkerComponent>(child))
                    verticalLinks++;
                if (entities.HasComponent<NCMissionEvacuationPointComponent>(child))
                    evacuationPoints++;
            }

            Assert.Multiple(() =>
            {
                Assert.That(entityCount, Is.EqualTo(expectedEntities));
                Assert.That(dynamicPoints, Is.EqualTo(3));
                Assert.That(verticalLinks, Is.EqualTo(1));
                Assert.That(evacuationPoints, Is.EqualTo(expectedEvacuationPoints));
            });
        }
        finally
        {
            maps.DeleteMap(mapId);
        }
    }
}
