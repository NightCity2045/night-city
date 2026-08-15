// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Server.GameTicking;
using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Actors.Domains;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.ZLevels.Core;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.ZLevels.Core.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Actors;

[TestFixture]
[TestOf(typeof(NCCityActorMaterializationSystem))]
public sealed class NCCityActorMaterializationSystemTest : GameTest
{
    [TestPrototypes]
    private const string MaterializationPrototypes = """
        - type: ncZoneSet
          id: NCCityMaterializationIntegrationZones
          networkId: 264293f2-8918-4728-9c22-e3240567cabe
          zones:
          - id: f50e6005-0e6c-4c1c-8b70-98223971d8de
            kind: District
            name: Materialization Test District
            geometry:
            - kind: Polygon
              global: true
              vertices:
              - 0,0
              - 8,0
              - 8,8
              - 0,8
          - id: 28a2ef85-4254-4d6c-84f9-086db4f69e86
            kind: Building
            parent: f50e6005-0e6c-4c1c-8b70-98223971d8de
            name: Materialization Test Building
            geometry:
            - kind: Polygon
              z: 0
              vertices:
              - 0,0
              - 8,0
              - 8,8
              - 0,8

        - type: ncCityRoutineProfile
          id: NCCityMaterializationIntegrationRoutine
          cycleDuration: 24h
          entries:
          - at: 0s
            activity: at-home
            goal: remain-available
            target: Home
            availability: Available

        - type: ncCityActorArchetype
          id: NCCityMaterializationIntegrationCivilian
          entityPrototype: MobHuman
          spawnCandidateProfile: NCPedestrianSpawnCandidates
          travelMode: Pedestrian
          cognitionTier: Utility
          allowedHomeContexts: [Building]
          allowedWorkContexts: [Building]
          routineProfile: NCCityMaterializationIntegrationRoutine
        """;

    private static readonly NCZNetworkId NetworkId =
        new(Guid.Parse("264293f2-8918-4728-9c22-e3240567cabe"));
    private static readonly NCZoneId HomeZone =
        new(Guid.Parse("28a2ef85-4254-4d6c-84f9-086db4f69e86"));
    private static readonly ProtoId<NCCityActorArchetypePrototype> Archetype =
        new("NCCityMaterializationIntegrationCivilian");

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    public async Task PersistentActorRehydratesIntoItsExactZoneAndKeepsOneRuntimeBinding()
    {
        var server = Pair.Server;
        var maps = server.System<SharedMapSystem>();
        var entities = server.ResolveDependency<IEntityManager>();
        var zLevels = server.System<NCZLevelsSystem>();
        var clock = server.System<NCActiveWorldTimeSystem>();
        var actors = server.System<NCCityActorRegistrySystem>();
        var materialization = server.System<NCCityActorMaterializationSystem>();
        var dematerialization = server.System<NCCityActorDematerializationSystem>();
        var domains = server.System<NCCityActorDomainRegistrySystem>();
        var runtime = server.System<NCCityActorRuntimeIndexSystem>();
        var coordinates = server.System<NCMapCoordinatesSystem>();
        var zones = server.System<NCZoneSystem>();
        Entity<NCZMapNetworkComponent> network = default;

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady && materialization.IsActive,
            "The active-world materialization boundary did not start.");

        await server.WaitAssertion(() =>
        {
            maps.CreateMap(out var mapId);
            network = zLevels.CreateMapNetwork(NetworkId);
            Assert.That(zLevels.TryAddMapsIntoNetwork(network, new Dictionary<EntityUid, int>
            {
                [maps.GetMap(mapId)] = 0,
            }), Is.True);

            var grid = maps.CreateGridEntity(mapId);
            var tiles = new List<(Vector2i, Tile)>();
            for (var x = 0; x < 8; x++)
            {
                for (var y = 0; y < 8; y++)
                    tiles.Add((new Vector2i(x, y), new Tile(1)));
            }

            maps.SetTiles(grid, tiles);
        });

        // Vanilla pathfinding publishes its dirty grid snapshot after its normal batching delay.
        await server.WaitRunTicks(45);

        var scope = Guid.Parse("5148d738-0492-46da-9abf-0bd2b7b2dabb");
        var create = await actors.CreateAsync(new NCCityActorCreateRequest(
            Guid.NewGuid(),
            scope,
            "resident.001",
            "Mara Velez",
            ["Switch"],
            Archetype,
            HomeZone,
            HomeZone,
            Guid.NewGuid(),
            AuditSource: "integration.materialization"));
        Assert.That(create.Status, Is.EqualTo(NCCityActorMutationStatus.Applied), create.Error);

        var requestId = Guid.NewGuid();
        NCCityMaterializationRequestState submitted = null!;
        await server.WaitPost(() => submitted = materialization.Request(new NCCityMaterializationRequest(
            requestId,
            create.ActorId,
            NCCityMaterializationReason.Admin,
            100,
            17,
            TimeSpan.FromSeconds(10),
            "integration.materialization")));
        Assert.That(submitted.Status, Is.EqualTo(NCCityMaterializationRequestStatus.Queued));

        NCCityMaterializationRequestState completed = null!;
        await WaitUntil(server, () =>
        {
            if (!materialization.TryGetRequest(requestId, out var state) ||
                state.Status is NCCityMaterializationRequestStatus.Queued or
                    NCCityMaterializationRequestStatus.Loading or
                    NCCityMaterializationRequestStatus.Retrying)
            {
                return false;
            }

            completed = state;
            return true;
        }, "The persistent actor did not reach a terminal materialization state.", 240);

        await server.WaitAssertion(() =>
        {
            Assert.That(completed.Status, Is.EqualTo(NCCityMaterializationRequestStatus.Materialized),
                completed.Error);
            Assert.That(completed.EntityUid.IsValid(), Is.True);
            Assert.That(entities.EntityExists(completed.EntityUid), Is.True);
            Assert.That(runtime.MaterializedActorCount, Is.EqualTo(1));
            Assert.That(runtime.TryGetEntity(create.ActorId, out var bound), Is.True);
            Assert.That(bound, Is.EqualTo(completed.EntityUid));
            Assert.That(runtime.TryGetActor(completed.EntityUid, out var boundActor), Is.True);
            Assert.That(boundActor, Is.EqualTo(create.ActorId));
            Assert.That(entities.GetComponent<MetaDataComponent>(completed.EntityUid).EntityName,
                Is.EqualTo("Mara Velez"));

            Assert.That(coordinates.TryGetCoordinates(completed.EntityUid, out var stable), Is.True);
            var context = zones.GetLocationContext(stable);
            Assert.That(context.BuildingId.ZoneId, Is.EqualTo(HomeZone));

            var replay = materialization.Request(new NCCityMaterializationRequest(
                requestId,
                create.ActorId,
                NCCityMaterializationReason.Incident,
                1,
                0,
                TimeSpan.FromSeconds(1),
                "must-not-replace-retained-result"));
            Assert.That(replay, Is.EqualTo(completed));

            var second = materialization.Request(new NCCityMaterializationRequest(
                Guid.NewGuid(),
                create.ActorId,
                NCCityMaterializationReason.Admin,
                100,
                19,
                TimeSpan.FromSeconds(10),
                "integration.materialization"));
            Assert.That(second.Status, Is.EqualTo(NCCityMaterializationRequestStatus.AlreadyMaterialized));
            Assert.That(second.EntityUid, Is.EqualTo(completed.EntityUid));
            Assert.That(runtime.MaterializedActorCount, Is.EqualTo(1));

            var rejectedId = Guid.NewGuid();
            var rejected = materialization.Request(new NCCityMaterializationRequest(
                rejectedId,
                create.ActorId,
                NCCityMaterializationReason.Admin,
                int.MaxValue,
                0,
                TimeSpan.FromSeconds(10),
                "integration.invalid-priority"));
            var rejectedReplay = materialization.Request(new NCCityMaterializationRequest(
                rejectedId,
                create.ActorId,
                NCCityMaterializationReason.Admin,
                1,
                0,
                TimeSpan.FromSeconds(10),
                "must-not-correct-an-existing-request"));
            Assert.That(rejected.Status, Is.EqualTo(NCCityMaterializationRequestStatus.Rejected));
            Assert.That(rejectedReplay, Is.EqualTo(rejected));
        });

        var actorBefore = await actors.GetAsync(create.ActorId, true);
        var healthBefore = await domains.GetHealthAsync(create.ActorId, true);
        var inventoryBefore = await domains.GetInventoryAsync(create.ActorId, true);
        Assert.That(actorBefore, Is.Not.Null);
        Assert.That(healthBefore, Is.Not.Null);
        Assert.That(inventoryBefore, Is.Not.Null);

        // A gameplay lease keeps the physical actor present even though there are no player relevance anchors.
        await server.WaitAssertion(() => Assert.That(dematerialization.SetBlocker(
            completed.EntityUid,
            NCCityDematerializationBlockerFlags.Scripted,
            true), Is.True));
        var dematerializationRequestId = Guid.NewGuid();
        NCCityDematerializationRequestState dematerializationSubmitted = null!;
        await server.WaitPost(() => dematerializationSubmitted = dematerialization.Request(
            new NCCityDematerializationRequest(
                dematerializationRequestId,
                create.ActorId,
                completed.EntityUid,
                NCCityDematerializationReason.Admin,
                900,
                "integration.dematerialization")));
        Assert.That(dematerializationSubmitted.Status, Is.EqualTo(NCCityDematerializationStatus.Requested));
        await server.WaitRunTicks(5);
        await server.WaitAssertion(() => Assert.That(entities.EntityExists(completed.EntityUid), Is.True));
        await server.WaitAssertion(() => Assert.That(dematerialization.SetBlocker(
            completed.EntityUid,
            NCCityDematerializationBlockerFlags.Scripted,
            false), Is.True));

        NCCityDematerializationRequestState dematerializationCompleted = null!;
        await WaitUntil(server, () =>
        {
            if (!dematerialization.TryGetRequest(dematerializationRequestId, out var state) ||
                state.Status is NCCityDematerializationStatus.Requested or
                    NCCityDematerializationStatus.Loading or
                    NCCityDematerializationStatus.Capturing or
                    NCCityDematerializationStatus.Committing or
                    NCCityDematerializationStatus.Retrying)
            {
                return false;
            }

            dematerializationCompleted = state;
            return true;
        }, "The materialized actor did not reach a terminal dematerialization state.", 1800);

        await server.WaitRunTicks(1);
        await server.WaitAssertion(() =>
        {
            Assert.That(dematerializationCompleted.Status, Is.EqualTo(NCCityDematerializationStatus.Dematerialized),
                dematerializationCompleted.Error);
            Assert.That(runtime.TryGetEntity(create.ActorId, out _), Is.False);
            Assert.That(entities.EntityExists(completed.EntityUid), Is.False);
        });

        var actorAfter = await actors.GetAsync(create.ActorId, true);
        var healthAfter = await domains.GetHealthAsync(create.ActorId, true);
        var inventoryAfter = await domains.GetInventoryAsync(create.ActorId, true);
        Assert.That(actorAfter, Is.Not.Null);
        Assert.That(healthAfter, Is.Not.Null);
        Assert.That(inventoryAfter, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(actorAfter!.AggregateVersion, Is.EqualTo(actorBefore!.AggregateVersion + 1));
            Assert.That(actorAfter.Snapshot.Location.CurrentZone, Is.EqualTo(HomeZone));
            Assert.That(healthAfter!.AggregateVersion, Is.EqualTo(healthBefore!.AggregateVersion + 1));
            Assert.That(healthAfter.Snapshot.MedicalStatePayload, Is.Not.Null.And.Not.Empty);
            Assert.That(inventoryAfter!.AggregateVersion, Is.EqualTo(inventoryBefore!.AggregateVersion + 1));
        });

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
        await server.WaitPost(() => zLevels.DeleteMapNetwork(network));
        await server.WaitRunTicks(1);
    }

    private static async Task WaitUntil(
        RobustIntegrationTest.ServerIntegrationInstance server,
        Func<bool> predicate,
        string failure,
        int attempts = 120)
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
}
