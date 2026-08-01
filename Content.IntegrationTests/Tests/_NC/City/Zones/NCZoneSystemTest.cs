// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Tests.Helpers;
using Content.Server._NC.City.Zones;
using Content.Server._NC.ZLevels.Core;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Components;
using Content.Shared._NC.City.Zones.Persistence;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.ZLevels.Core.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Value;

namespace Content.IntegrationTests.Tests._NC.City.Zones;

[TestFixture]
[TestOf(typeof(NCZoneSystem))]
public sealed class NCZoneSystemTest : GameTest
{
    // This fixture is loaded only by the integration-test pool. Production servers therefore
    // start without placeholder Watson zones and receive city geometry solely from real maps/exports.
    [TestPrototypes]
    private const string ZoneSetPrototype = """
        - type: ncZoneSet
          id: NCZoneSystemIntegrationZones
          networkId: 4ca02a1c-5e73-48d7-af28-3f6ef5df8271
          zones:
          - id: c47fe25b-957a-43c5-9727-a789b8ce4900
            kind: District
            name: Watson Test District
            geometry:
            - kind: Volume
              global: true
              vertices:
              - -16,-16
              - 16,-16
              - 16,16
              - -16,16
          - id: 71f68dca-43bd-46e6-9ff9-d43bd43a2577
            kind: Sector
            parent: c47fe25b-957a-43c5-9727-a789b8ce4900
            name: Test Sector
            geometry:
            - kind: Volume
              minZ: -1
              maxZ: 1
              vertices:
              - -8,-8
              - 8,-8
              - 8,8
              - -8,8
          - id: 4eec2bca-d105-4cca-8a0a-0360eaac9101
            kind: Street
            parent: 71f68dca-43bd-46e6-9ff9-d43bd43a2577
            name: Test Street
            geometry:
            - kind: Polygon
              z: 0
              vertices:
              - -8,-2
              - 8,-2
              - 8,2
              - -8,2
          - id: ddf2ded2-d9ac-4201-936f-1badfc179a89
            kind: Building
            parent: 4eec2bca-d105-4cca-8a0a-0360eaac9101
            name: Test Building
            geometry:
            - kind: Volume
              minZ: 0
              maxZ: 1
              vertices:
              - 0,0
              - 6,0
              - 6,6
              - 0,6
          - id: 78fcc966-12cc-4a15-ae2d-d2f65bbd09c8
            kind: Apartment
            parent: ddf2ded2-d9ac-4201-936f-1badfc179a89
            name: Test Apartment
            geometry:
            - kind: TileMask
              chunks:
              - z: 0
                origin: 0,0
                rows:
                - 0
                - 0
                - 4
            - kind: Volume
              minZ: 0
              maxZ: 1
              vertices:
              - 2,2
              - 3,2
              - 3,3
              - 2,3
        """;

    private static readonly NCZNetworkId NetworkId =
        new(Guid.Parse("4ca02a1c-5e73-48d7-af28-3f6ef5df8271"));
    private static readonly NCZoneId DistrictId =
        new(Guid.Parse("c47fe25b-957a-43c5-9727-a789b8ce4900"));
    private static readonly NCZoneId BuildingZoneId =
        new(Guid.Parse("ddf2ded2-d9ac-4201-936f-1badfc179a89"));
    private static readonly NCZoneId ApartmentZoneId =
        new(Guid.Parse("78fcc966-12cc-4a15-ae2d-d2f65bbd09c8"));

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    public async Task ZoneSetsValidateAndResolveAllGeometryKinds()
    {
        var server = Pair.Server;
        var zones = server.System<NCZoneSystem>();

        await server.WaitAssertion(() =>
        {
            var errors = new List<NCZoneValidationError>();
            Assert.That(zones.ValidateAll(errors), Is.True,
                string.Join(Environment.NewLine, errors.Select(error => error.Message)));

            var result = new List<NCZoneInfo>();
            zones.GetZones(new NCMapCoordinates(NetworkId, new Vector2(2.5f, 2f), 0), result);

            Assert.That(result.Select(zone => zone.Kind.Id), Is.EqualTo(new[]
            {
                "District",
                "Sector",
                "Street",
                "Building",
                "Apartment",
            }));

            // The district is global, while the apartment's volume spans exactly Z=0..1.
            zones.GetZones(new NCMapCoordinates(NetworkId, new Vector2(2.5f, 2.5f), 1), result);
            Assert.That(result.Select(zone => zone.Kind.Id), Is.EqualTo(new[]
            {
                "District",
                "Sector",
                "Building",
                "Apartment",
            }));

            // A global zone is resolved on an arbitrary future floor without rebuilding per-floor cells.
            zones.GetZones(new NCMapCoordinates(NetworkId, new Vector2(2.5f, 2.5f), 100), result);
            Assert.That(result.Select(zone => zone.Kind.Id), Is.EqualTo(new[]
            {
                "District",
            }));

            zones.GetZones(new NCMapCoordinates(NetworkId, new Vector2(100f, 100f), 0), result);
            Assert.That(result, Is.Empty);
        });
    }

    [Test]
    public async Task CachedContextTracksMovementFloorsAndActivity()
    {
        var server = Pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var maps = server.System<SharedMapSystem>();
        var transform = server.System<SharedTransformSystem>();
        var zLevels = server.System<NCZLevelsSystem>();
        var zones = server.System<NCZoneSystem>();
        var locations = server.System<NCCityLocationSystem>();
        var listener = server.System<NCCityZoneChangedListenerSystem>();

        Entity<NCZMapNetworkComponent> network = default;
        EntityUid entity = default;
        MapId groundId = default;
        MapId upperId = default;

        await server.WaitAssertion(() =>
        {
            maps.CreateMap(out groundId);
            maps.CreateMap(out upperId);
            network = zLevels.CreateMapNetwork(NetworkId);
            Assert.That(zLevels.TryAddMapsIntoNetwork(network, new Dictionary<EntityUid, int>
            {
                [maps.GetMap(groundId)] = 0,
                [maps.GetMap(upperId)] = 1,
            }), Is.True);

            entity = entities.SpawnEntity(null, new MapCoordinates(new Vector2(2.5f, 2f), groundId));
            entities.EnsureComponent<TestListenerComponent>(entity);
            locations.EnsureTracked(entity);

            Assert.That(locations.TryGetContext(entity, out var context), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(context.DistrictId.IsValid, Is.True);
                Assert.That(context.SectorId.IsValid, Is.True);
                Assert.That(context.StreetId.IsValid, Is.True);
                Assert.That(context.BuildingId.IsValid, Is.True);
                Assert.That(context.ApartmentId.IsValid, Is.True);
                Assert.That(context.Z, Is.Zero);
                Assert.That(listener.Count(entity), Is.EqualTo(1));
            });

            // Gameplay systems receive strongly typed identities and immutable metadata, never cache components.
            Assert.That(locations.TryGetBuilding(entity, out var buildingId), Is.True);
            Assert.That(buildingId.ZoneId, Is.EqualTo(BuildingZoneId));
            Assert.That(zones.TryGetBuilding(buildingId, out var building), Is.True);
            Assert.That(building.Name, Is.EqualTo("Test Building"));

            Assert.That(locations.TryGetApartment(entity, out var apartmentId), Is.True);
            Assert.That(apartmentId.ZoneId, Is.EqualTo(ApartmentZoneId));
            Assert.That(zones.TryGetApartment(apartmentId, out var apartment), Is.True);
            Assert.That(apartment.Name, Is.EqualTo("Test Apartment"));

            // A valid zone GUID cannot be silently used as the wrong semantic object type.
            Assert.That(zones.TryGetBuildingId(ApartmentZoneId, out _), Is.False);
            Assert.That(zones.TryGetApartmentId(ApartmentZoneId, out var checkedApartmentId), Is.True);

            // Database persistence is a stable GUID scalar and round-trips without runtime entity IDs.
            var storedApartment = NCCityObjectIdDatabaseCodec.Encode(checkedApartmentId);
            Assert.That(
                NCCityObjectIdDatabaseCodec.TryDecodeApartment(storedApartment, out var restoredApartmentId),
                Is.True);
            Assert.That(restoredApartmentId, Is.EqualTo(checkedApartmentId));

            // Typed IDs retain the same compact GUID representation in YAML.
            var serialization = server.ResolveDependency<ISerializationManager>();
            AssertYamlRoundTrip(serialization, context.DistrictId);
            AssertYamlRoundTrip(serialization, context.SectorId);
            AssertYamlRoundTrip(serialization, context.StreetId);
            AssertYamlRoundTrip(serialization, context.BuildingId);
            AssertYamlRoundTrip(serialization, context.ApartmentId);

            Assert.That(zones.SetActivityMode(DistrictId, NCZoneActivityMode.Warm), Is.True);
            Assert.That(zones.TryGetActivityMode(DistrictId, out var activity), Is.True);
            Assert.That(activity, Is.EqualTo(NCZoneActivityMode.Warm));
            Assert.That(
                zones.GetEffectiveActivityMode(context),
                Is.EqualTo(NCZoneActivityMode.Warm));
        });

        await server.WaitPost(() =>
            transform.SetMapCoordinates(entity, new MapCoordinates(new Vector2(2.5f, 2f), upperId)));
        await server.WaitRunTicks(1);

        await server.WaitAssertion(() =>
        {
            Assert.That(locations.TryGetContext(entity, out var upstairs), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(upstairs.Z, Is.EqualTo(1));
                Assert.That(upstairs.BuildingId.IsValid, Is.True);
                Assert.That(upstairs.StreetId.IsValid, Is.True);
                Assert.That(upstairs.ApartmentId.IsValid, Is.True);
                Assert.That(listener.Count(entity), Is.EqualTo(2));
            });

            Assert.That(entities.HasComponent<NCCityLocationComponent>(entity), Is.True);
            Assert.That(zones.SetActivityMode(DistrictId, NCZoneActivityMode.Active), Is.True);
        });

        await server.WaitPost(() => zLevels.DeleteMapNetwork(network));
        await server.WaitRunTicks(1);
    }

    private static void AssertYamlRoundTrip<T>(ISerializationManager serialization, T expected)
        where T : struct
    {
        var node = serialization.WriteValue(expected);
        Assert.That(node, Is.TypeOf<ValueDataNode>());
        Assert.That(serialization.ValidateNode<T>(node).GetErrors(), Is.Empty);
        Assert.That(serialization.Read<T>(node), Is.EqualTo(expected));
    }
}

public sealed class NCCityZoneChangedListenerSystem : TestListenerSystem<NCZoneChangedEvent>;
