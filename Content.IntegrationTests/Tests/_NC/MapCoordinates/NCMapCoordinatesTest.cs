// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.Server._NC.ZLevels.Core;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Persistence;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.ZLevels.Core.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;

namespace Content.IntegrationTests.Tests._NC.Coordinates;

[TestFixture]
[TestOf(typeof(NCMapCoordinatesSystem))]
public sealed class NCMapCoordinatesTest : GameTest
{
    private static readonly NCZNetworkId PersistentNetworkId =
        new(Guid.Parse("a03411cc-5d58-4810-bcee-a19c041dfda7"));

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    public async Task CoordinatesSurviveRuntimeNetworkRecreation()
    {
        var server = Pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var maps = server.System<SharedMapSystem>();
        var zLevels = server.System<NCZLevelsSystem>();
        var coordinatesSystem = server.System<NCMapCoordinatesSystem>();

        Entity<NCZMapNetworkComponent> firstNetwork = default;
        NCMapCoordinates storedCoordinates = default;
        EntityUid firstMap = default;

        await server.WaitAssertion(() =>
        {
            maps.CreateMap(out var mapId);
            firstMap = maps.GetMap(mapId);
            firstNetwork = zLevels.CreateMapNetwork(PersistentNetworkId);

            Assert.That(zLevels.TryAddMapsIntoNetwork(firstNetwork, new Dictionary<EntityUid, int>
            {
                [firstMap] = 2,
            }), Is.True);

            var entity = entities.SpawnEntity(null, new MapCoordinates(new Vector2(128f, 64f), mapId));
            Assert.That(coordinatesSystem.TryGetCoordinates(entity, out storedCoordinates), Is.True);
            Assert.That(storedCoordinates, Is.EqualTo(
                new NCMapCoordinates(PersistentNetworkId, new Vector2(128f, 64f), 2)));
        });

        await server.WaitPost(() => zLevels.DeleteMapNetwork(firstNetwork));
        await server.WaitRunTicks(2);

        Entity<NCZMapNetworkComponent> secondNetwork = default;
        EntityUid secondMap = default;

        await server.WaitAssertion(() =>
        {
            maps.CreateMap(out var mapId);
            secondMap = maps.GetMap(mapId);
            secondNetwork = zLevels.CreateMapNetwork(PersistentNetworkId);

            Assert.That(secondNetwork.Owner, Is.Not.EqualTo(firstNetwork.Owner));
            Assert.That(secondMap, Is.Not.EqualTo(firstMap));
            Assert.That(zLevels.TryAddMapsIntoNetwork(secondNetwork, new Dictionary<EntityUid, int>
            {
                [secondMap] = 2,
            }), Is.True);

            Assert.That(coordinatesSystem.TryResolve(storedCoordinates, out var restored), Is.True);
            Assert.That(restored.MapId, Is.EqualTo(mapId));
            Assert.That(restored.Position, Is.EqualTo(new Vector2(128f, 64f)));
        });

        await server.WaitPost(() => zLevels.DeleteMapNetwork(secondNetwork));
        await server.WaitRunTicks(1);
    }

    [Test]
    public async Task FloorTransitionsAndRotatedMovingGridsUseMapSpace()
    {
        var server = Pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var maps = server.System<SharedMapSystem>();
        var transform = server.System<SharedTransformSystem>();
        var zLevels = server.System<NCZLevelsSystem>();
        var coordinatesSystem = server.System<NCMapCoordinatesSystem>();

        Entity<NCZMapNetworkComponent> network = default;
        EntityUid entity = default;
        MapId upperId = default;
        NCMapCoordinates onGround = default;

        await server.WaitAssertion(() =>
        {
            maps.CreateMap(out var groundId);
            maps.CreateMap(out upperId);
            var groundMap = maps.GetMap(groundId);
            var upperMap = maps.GetMap(upperId);
            network = zLevels.CreateMapNetwork();

            Assert.That(zLevels.TryAddMapsIntoNetwork(network, new Dictionary<EntityUid, int>
            {
                [groundMap] = 0,
                [upperMap] = 1,
            }), Is.True);

            var grid = maps.CreateGridEntity(groundId);
            maps.SetTiles(grid, new List<(Vector2i, Tile)>
            {
                (new Vector2i(2, 3), new Tile(1)),
            });
            transform.SetLocalPosition(grid, new Vector2(10f, 20f));
            transform.SetLocalRotation(grid, Angle.FromDegrees(90));

            entity = entities.SpawnAttachedTo(null, new EntityCoordinates(grid, new Vector2(2f, 3f)));
            var engineMapCoordinates = transform.GetMapCoordinates(entity);

            Assert.That(coordinatesSystem.TryGetCoordinates(entity, out onGround), Is.True);
            Assert.That(onGround.Position, Is.EqualTo(engineMapCoordinates.Position).Using(Vector2Comparer));
            Assert.That(onGround.Z, Is.Zero);
            Assert.That(coordinatesSystem.TryResolve(onGround, out var resolvedGround), Is.True);
            Assert.That(resolvedGround.Position, Is.EqualTo(engineMapCoordinates.Position).Using(Vector2Comparer));

            // Moving the parent grid changes the captured city position without leaking grid-local coordinates.
            transform.SetLocalPosition(grid, new Vector2(30f, 40f));
        });

        // Grid world matrices are refreshed on the following simulation tick.
        await server.WaitRunTicks(1);

        await server.WaitAssertion(() =>
        {
            var movedMapCoordinates = transform.GetMapCoordinates(entity);
            Assert.That(coordinatesSystem.TryGetCoordinates(entity, out var moved), Is.True);
            Assert.That(moved.Position, Is.EqualTo(movedMapCoordinates.Position).Using(Vector2Comparer));
            Assert.That(moved.Position, Is.Not.EqualTo(onGround.Position).Using(Vector2Comparer));

            // A floor transition keeps map-space X/Y and changes only the logical Z depth.
            transform.SetMapCoordinates(entity, new MapCoordinates(moved.Position, upperId));
            Assert.That(coordinatesSystem.TryGetCoordinates(entity, out var upstairs), Is.True);
            Assert.That(upstairs.Position, Is.EqualTo(moved.Position).Using(Vector2Comparer));
            Assert.That(upstairs.Z, Is.EqualTo(1));
            Assert.That(upstairs.NetworkId, Is.EqualTo(onGround.NetworkId));
        });

        await server.WaitPost(() => zLevels.DeleteMapNetwork(network));
        await server.WaitRunTicks(1);
    }

    [Test]
    public async Task InvalidQueriesAndDuplicateIdsFailCleanly()
    {
        var server = Pair.Server;
        var maps = server.System<SharedMapSystem>();
        var zLevels = server.System<NCZLevelsSystem>();
        var coordinatesSystem = server.System<NCMapCoordinatesSystem>();
        Entity<NCZMapNetworkComponent> network = default;

        await server.WaitAssertion(() =>
        {
            maps.CreateMap(out var mapId);
            var map = maps.GetMap(mapId);

            Assert.That(
                coordinatesSystem.TryConvert(new MapCoordinates(Vector2.Zero, mapId), out _),
                Is.False);

            network = zLevels.CreateMapNetwork(PersistentNetworkId);
            Assert.That(zLevels.TryAddMapsIntoNetwork(network, new Dictionary<EntityUid, int>
            {
                [map] = 0,
            }), Is.True);

            Assert.Throws<InvalidOperationException>(() => zLevels.CreateMapNetwork(PersistentNetworkId));
            Assert.That(
                coordinatesSystem.TryResolve(
                    new NCMapCoordinates(PersistentNetworkId, Vector2.Zero, 99),
                    out _),
                Is.False);
            Assert.That(
                coordinatesSystem.TryResolve(
                    new NCMapCoordinates(default, Vector2.Zero, 0),
                    out _),
                Is.False);
        });

        await server.WaitPost(() => zLevels.DeleteMapNetwork(network));
        await server.WaitRunTicks(1);
    }

    [Test]
    public async Task YamlAndDatabaseRepresentationsRoundTrip()
    {
        var server = Pair.Server;
        var serialization = server.ResolveDependency<ISerializationManager>();
        var networkSerialization = server.ResolveDependency<IRobustSerializer>();
        var original = new NCMapCoordinates(PersistentNetworkId, new Vector2(12.5f, -7.25f), -3);

        await server.WaitAssertion(() =>
        {
            var node = serialization.WriteValue(original);
            var restored = serialization.Read<NCMapCoordinates>(node);
            Assert.That(restored, Is.EqualTo(original));

            Assert.That(networkSerialization.CanSerialize(typeof(NCMapCoordinates)), Is.True);
            using var stream = new MemoryStream();
            networkSerialization.SerializeDirect(stream, original);
            stream.Position = 0;
            networkSerialization.DeserializeDirect(stream, out NCMapCoordinates networkRestored);
            Assert.That(networkRestored, Is.EqualTo(original));

            var databaseValue = NCMapCoordinatesDatabaseCodec.Encode(original);
            Assert.That(NCMapCoordinatesDatabaseCodec.TryDecode(databaseValue, out var databaseRestored), Is.True);
            Assert.That(databaseRestored, Is.EqualTo(original));
        });
    }

    private static readonly IEqualityComparer<Vector2> Vector2Comparer =
        new ApproximateVector2Comparer(0.0001f);

    private sealed class ApproximateVector2Comparer(float tolerance) : IEqualityComparer<Vector2>
    {
        public bool Equals(Vector2 left, Vector2 right)
        {
            return Vector2.DistanceSquared(left, right) <= tolerance * tolerance;
        }

        public int GetHashCode(Vector2 value)
        {
            return value.GetHashCode();
        }
    }
}
