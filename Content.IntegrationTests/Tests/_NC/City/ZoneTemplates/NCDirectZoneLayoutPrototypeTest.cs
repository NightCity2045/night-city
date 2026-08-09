// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.City.ZoneTemplates;
using Content.Server._NC.Housing;
using Content.Server._NC.Housing.Components;
using Content.Server._NC.ZLevels.Core;
using Content.Server.Database;
using Content.Shared._NC.City.ZoneTemplates;
using Content.Shared._NC.City.ZoneTemplates.Components;
using Content.Shared._NC.City.ZoneTemplates.Prototypes;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Dungeons.Components;
using Content.Shared._NC.Housing.Components;
using Content.Shared._NC.Housing.Prototypes;
using Content.Shared.Maps;
using Content.Shared.CCVar;
using Robust.Server.GameObjects;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.City.ZoneTemplates;

[TestFixture]
[NonParallelizable]
public sealed class NCDirectZoneLayoutPrototypeTest : GameTest
{
    private static readonly ProtoId<NCZoneTemplatePrototype> ApartmentLayout = "NCApartmentLayout5x5";
    private static readonly ProtoId<NCZoneTemplatePrototype> DungeonLayout = "NCDungeonLayout8x8";
    private static readonly ProtoId<NCPropertyListingPrototype> ApartmentListing = "NCGlen1Apartment01";
    private static readonly NCZNetworkId CityNetworkId =
        new(Guid.Parse("f7466602-81be-4ad9-9338-9c181a31e552"));
    private static readonly NCZoneId ApartmentZoneId =
        new(Guid.Parse("67566dd3-93b9-4ec6-a823-772e5fc376fd"));

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
            Assert.That(prototypes.TryIndex(listing.Interiors[0].RentPolicy, out NCRentPolicyPrototype? rentPolicy),
                Is.True);
            Assert.That(rentPolicy!.RentAmount, Is.Zero);

            Assert.That(prototypes.TryIndex(DungeonLayout, out var dungeon), Is.True);
            Assert.That(dungeon!.TargetZoneId,
                Is.EqualTo(new NCZoneId(Guid.Parse("8d7b2c44-9a31-4f67-b8e2-6c5d4a3f1808"))));
            Assert.Multiple(() =>
            {
                Assert.That(dungeon.TilePolicy, Is.EqualTo(NCZoneTemplateTilePolicy.ReplaceNonEmpty));
                Assert.That(dungeon.EntityPolicy, Is.EqualTo(NCZoneTemplateEntityPolicy.InteriorOnly));
                Assert.That(dungeon.CachePolicy, Is.EqualTo(NCZoneTemplateCachePolicy.Always));
            });
            AssertLayout<NCDungeonEntranceComponent>(entities, loader, maps, dungeon, 64, 0, 9);
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
        var database = server.ResolveDependency<IServerDbManager>();
        var tiles = server.ResolveDependency<ITileDefinitionManager>();
        var maps = entities.System<MapSystem>();
        var zLevels = entities.System<NCZLevelsSystem>();
        var templates = entities.System<NCZoneTemplateSystem>();
        var snapshots = entities.System<NCPropertySnapshotSystem>();

        EntityUid gridUid = default;
        EntityUid residentUid = default;
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

        var propertyId = new NCApartmentId(ApartmentZoneId);
        var property = await database.EnsureNCPropertyAsync(new NCPropertySeedData(
            propertyId,
            new NCBuildingId(new NCZoneId(Guid.NewGuid())),
            [ApartmentZoneId],
            0,
            "TestRentPolicy",
            "TestAccessPolicy"));
        Assert.That(property, Is.Not.Null);
        property = await database.SetNCPropertyInteriorAsync(
            propertyId,
            deploymentId,
            ApartmentLayout.Id,
            1,
            "active",
            property!.Version);
        Assert.That(property, Is.Not.Null);

        await server.WaitAssertion(() =>
        {
            residentUid = entities.SpawnEntity(null, new EntityCoordinates(gridUid, new Vector2(-2.5f, 23.5f)));
            snapshots.CaptureRoundSnapshots([new NCPropertySnapshotCapture(property!, deploymentId)]);
            Assert.That(entities.HasComponent<NCPropertyResidentItemComponent>(residentUid), Is.True);
        });
        await snapshots.WaitForPendingSaveAsync();

        var stored = (await database.GetNCPropertySnapshotsAsync())
            .Single(value => value.PropertyId == propertyId);
        Assert.Multiple(() =>
        {
            Assert.That(stored.TemplateId, Is.EqualTo(ApartmentLayout.Id));
            Assert.That(stored.TemplateRevision, Is.EqualTo(1));
            Assert.That(stored.EntityCount, Is.GreaterThan(0));
            Assert.That(stored.ResidentEntityCount, Is.EqualTo(1));
            Assert.That(stored.Payload, Does.Contain("EntityYaml"));
        });

        await server.WaitAssertion(() =>
            Assert.That(templates.TryCloseDeployment(deploymentId, out var error), Is.True, error));
        await WaitForState(server, templates, deploymentId, NCZoneDeploymentState.Completed);
        await server.WaitAssertion(() => entities.DeleteEntity(residentUid));

        await snapshots.LoadPreparedSnapshotsAsync([property!]);
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

        await server.WaitAssertion(() => snapshots.ConfiscateLiveContents(property!));
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
}
