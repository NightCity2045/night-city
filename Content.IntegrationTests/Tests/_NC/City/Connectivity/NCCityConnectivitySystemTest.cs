// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.Server._NC.City.Connectivity;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.City.Connectivity;

[TestFixture]
[TestOf(typeof(NCCityConnectivitySystem))]
public sealed class NCCityConnectivitySystemTest : GameTest
{
    [TestPrototypes]
    private const string ZoneSetPrototype = """
        - type: ncZoneSet
          id: NCCityConnectivityIntegrationZones
          networkId: f51ddf03-7ca4-46f4-b4ac-b8eb3ff53054
          zones:
          - id: 4b4ab0fd-38c8-4680-b7ab-a040a52ef846
            kind: District
            name: Connectivity District
            geometry:
            - kind: Polygon
              global: true
              vertices:
              - 0,0
              - 20,0
              - 20,20
              - 0,20
          - id: e5d521e7-609f-4dd8-a58f-4af1b0c6fab0
            kind: Street
            parent: 4b4ab0fd-38c8-4680-b7ab-a040a52ef846
            name: Connectivity Street
            geometry:
            - kind: Polygon
              z: 0
              vertices:
              - 0,8
              - 20,8
              - 20,12
              - 0,12
          - id: c68b7c5a-48f1-444e-a410-b087bb15d358
            kind: Building
            parent: e5d521e7-609f-4dd8-a58f-4af1b0c6fab0
            name: Building A
            geometry:
            - kind: Polygon
              z: 0
              vertices:
              - 1,9
              - 5,9
              - 5,11
              - 1,11
          - id: a102cc67-ef2a-49d6-ad29-579d29e63fd6
            kind: Building
            parent: e5d521e7-609f-4dd8-a58f-4af1b0c6fab0
            name: Building B
            geometry:
            - kind: Polygon
              z: 0
              vertices:
              - 15,9
              - 19,9
              - 19,11
              - 15,11
        """;

    private static readonly NCZNetworkId NetworkId =
        new(Guid.Parse("f51ddf03-7ca4-46f4-b4ac-b8eb3ff53054"));
    private static readonly NCZoneId StreetId =
        new(Guid.Parse("e5d521e7-609f-4dd8-a58f-4af1b0c6fab0"));
    private static readonly NCZoneId DistrictId =
        new(Guid.Parse("4b4ab0fd-38c8-4680-b7ab-a040a52ef846"));
    private static readonly NCZoneId BuildingA =
        new(Guid.Parse("c68b7c5a-48f1-444e-a410-b087bb15d358"));
    private static readonly NCZoneId BuildingB =
        new(Guid.Parse("a102cc67-ef2a-49d6-ad29-579d29e63fd6"));

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    public async Task SemanticContainmentAndTraversablePortalsRemainSeparate()
    {
        var server = Pair.Server;
        var connectivity = server.System<NCCityConnectivitySystem>();

        await server.WaitAssertion(() =>
        {
            Assert.That(connectivity.ContainsNode(BuildingA), Is.True);
            Assert.That(connectivity.ContainsNode(BuildingB), Is.True);
            Assert.That(connectivity.TryGetParent(BuildingA, out var parent), Is.True);
            Assert.That(parent, Is.EqualTo(StreetId));

            var noRoute = new List<NCCityRouteStep>();
            Assert.That(connectivity.TryFindRoute(
                BuildingA,
                BuildingB,
                NCCityTravelMode.Pedestrian,
                noRoute), Is.False, "Containment must not silently become a traversable edge.");

            var registration = new NCCityPortalRegistration(
                BuildingA,
                BuildingB,
                new NCMapCoordinates(NetworkId, new Vector2(5f, 10f), 0),
                new NCMapCoordinates(NetworkId, new Vector2(15f, 10f), 0),
                NCCityPortalKind.OpenBoundary,
                new ProtoId<NCCityTraversalProfilePrototype>("NCTraversalStairs"),
                default);
            Assert.That(connectivity.TryRegisterPortal(registration, out var portalId), Is.True);

            var pedestrian = new List<NCCityRouteStep>();
            Assert.That(connectivity.TryFindRoute(
                BuildingA,
                BuildingB,
                NCCityTravelMode.Pedestrian,
                pedestrian), Is.True);
            Assert.That(pedestrian, Has.Count.EqualTo(1));
            Assert.That(pedestrian[0].PortalId, Is.EqualTo(portalId));

            var vehicle = new List<NCCityRouteStep>();
            Assert.That(connectivity.TryFindRoute(
                BuildingA,
                BuildingB,
                NCCityTravelMode.Vehicle,
                vehicle), Is.False);

            Assert.That(connectivity.SetPortalEnabled(portalId, false), Is.True);
            Assert.That(connectivity.TryFindRoute(
                BuildingA,
                BuildingB,
                NCCityTravelMode.Pedestrian,
                pedestrian), Is.False);
            Assert.That(connectivity.SetPortalEnabled(portalId, true), Is.True);
            Assert.That(connectivity.TryFindRoute(
                BuildingA,
                BuildingB,
                NCCityTravelMode.Pedestrian,
                pedestrian), Is.True);

            var actorFiltered = new List<NCCityRouteStep>();
            Assert.That(connectivity.TryFindRoute(
                BuildingA,
                BuildingB,
                NCCityTravelMode.Pedestrian,
                actorFiltered,
                _ => false), Is.False, "Actor-specific access must be able to reject a controller edge.");
        });
    }

    [Test]
    public async Task StableLinksUpsertWhileOrdinaryPortalsStayRuntimeOnly()
    {
        var server = Pair.Server;
        var connectivity = server.System<NCCityConnectivitySystem>();

        await server.WaitAssertion(() =>
        {
            var stableId = new NCStableCityLinkId(Guid.NewGuid(), "stairs-upper");
            var first = new NCCityPortalRegistration(
                BuildingA,
                BuildingB,
                new NCMapCoordinates(NetworkId, new Vector2(4f, 10f), 0),
                new NCMapCoordinates(NetworkId, new Vector2(16f, 10f), 0),
                NCCityPortalKind.Vertical,
                new ProtoId<NCCityTraversalProfilePrototype>("NCTraversalStairs"),
                stableId);
            var updated = first with
            {
                EntryTarget = new NCMapCoordinates(NetworkId, new Vector2(4.5f, 10f), 0),
            };

            Assert.That(connectivity.TryRegisterPortal(first, out var firstId), Is.True);
            Assert.That(connectivity.TryRegisterPortal(updated, out var updatedId), Is.True);
            Assert.That(updatedId, Is.EqualTo(firstId));

            var outgoing = new List<NCCityPortalInfo>();
            connectivity.GetOutgoingPortals(BuildingA, outgoing);
            Assert.That(outgoing.Count(portal => portal.StableId == stableId), Is.EqualTo(1));
            Assert.That(connectivity.RemovePortal(firstId), Is.True);
        });
    }

    [Test]
    public async Task MaterializedRouteRequiresVerticalSelfLinkAcrossDepths()
    {
        var server = Pair.Server;
        var connectivity = server.System<NCCityConnectivitySystem>();

        await server.WaitAssertion(() =>
        {
            var origin = new NCMapCoordinates(NetworkId, new Vector2(10f, 4f), 0);
            var destination = new NCMapCoordinates(NetworkId, new Vector2(10f, 4f), 1);
            var route = new List<NCCityRouteStep>();
            Assert.That(connectivity.TryFindRoute(
                origin,
                destination,
                NCCityTravelMode.Pedestrian,
                route), Is.False,
                "Sharing one multi-floor NCZoneId must not imply physical cross-floor movement.");

            var stableId = new NCStableCityLinkId(Guid.NewGuid(), "district-stairs-0-1");
            Assert.That(connectivity.TryRegisterPortal(new NCCityPortalRegistration(
                DistrictId,
                DistrictId,
                new NCMapCoordinates(NetworkId, new Vector2(10f, 4f), 0),
                new NCMapCoordinates(NetworkId, new Vector2(10f, 4f), 1),
                NCCityPortalKind.Vertical,
                new ProtoId<NCCityTraversalProfilePrototype>("NCTraversalStairs"),
                stableId), out var portalId), Is.True);

            Assert.That(connectivity.TryFindRoute(
                origin,
                destination,
                NCCityTravelMode.Pedestrian,
                route), Is.True);
            Assert.That(route, Has.Count.EqualTo(1));
            Assert.That(route[0].PortalId, Is.EqualTo(portalId));
            Assert.That(route[0].EntryTarget.Z, Is.Zero);
            Assert.That(route[0].ExitTarget.Z, Is.EqualTo(1));
            Assert.That(connectivity.RemovePortal(portalId), Is.True);
        });
    }
}
