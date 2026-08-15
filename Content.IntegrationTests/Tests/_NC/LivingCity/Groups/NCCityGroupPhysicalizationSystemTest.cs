// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using System.Collections.Generic;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Groups;
using Content.Server._NC.LivingCity.Groups.Components;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.ZLevels.Core;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Groups;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.ZLevels.Core.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Groups;

[TestFixture]
[TestOf(typeof(NCCityGroupPhysicalizationSystem))]
public sealed class NCCityGroupPhysicalizationSystemTest : GameTest
{
    [TestPrototypes]
    private const string Prototypes = """
        - type: ncZoneSet
          id: NCCityGroupPhysicalizationIntegrationZones
          networkId: 566e2c0e-91c0-4fbe-b648-74f611a50bc9
          zones:
          - id: 96b71dcc-313d-460f-81bc-3b2c92a5b3bd
            kind: District
            name: Group Physicalization Test District
            geometry:
            - kind: Polygon
              global: true
              vertices:
              - 0,0
              - 10,0
              - 10,10
              - 0,10
          - id: 61c92f50-5234-44aa-b624-b844f61d2840
            kind: Building
            parent: 96b71dcc-313d-460f-81bc-3b2c92a5b3bd
            name: Group Physicalization Test Building
            geometry:
            - kind: Polygon
              z: 0
              vertices:
              - 0,0
              - 10,0
              - 10,10
              - 0,10

        - type: ncCityRoutineProfile
          id: NCCityGroupPhysicalizationIntegrationRoutine
          cycleDuration: 24h
          entries:
          - at: 0s
            activity: patrol
            goal: hold-position
            target: Home
            availability: Available

        - type: ncCityActorArchetype
          id: NCCityGroupPhysicalizationIntegrationActor
          entityPrototype: MobHuman
          spawnCandidateProfile: NCPedestrianSpawnCandidates
          travelMode: Pedestrian
          cognitionTier: Utility
          allowedHomeContexts: [Building]
          allowedWorkContexts: [Building]
          routineProfile: NCCityGroupPhysicalizationIntegrationRoutine

        - type: ncCityGroupProfile
          id: NCCityGroupPhysicalizationIntegrationGroup
          maximumMembers: 4
          maximumSnapshotHistory: 16
          allowSplit: true
          allowMerge: true
          materializeTogether: true
          formationPolicy: Column
          initialMorale: 0.5
        """;

    private static readonly NCZNetworkId NetworkId =
        new(Guid.Parse("566e2c0e-91c0-4fbe-b648-74f611a50bc9"));
    private static readonly NCZoneId HomeZone =
        new(Guid.Parse("61c92f50-5234-44aa-b624-b844f61d2840"));
    private static readonly ProtoId<NCCityActorArchetypePrototype> Archetype =
        new("NCCityGroupPhysicalizationIntegrationActor");
    private static readonly ProtoId<NCCityGroupProfilePrototype> GroupProfile =
        new("NCCityGroupPhysicalizationIntegrationGroup");

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    public async Task CompleteFormationMovesTogetherBetweenPhysicalAndStrategicLayers()
    {
        var server = Pair.Server;
        var maps = server.System<SharedMapSystem>();
        var entities = server.ResolveDependency<IEntityManager>();
        var zLevels = server.System<NCZLevelsSystem>();
        var clock = server.System<NCActiveWorldTimeSystem>();
        var actors = server.System<NCCityActorRegistrySystem>();
        var groups = server.System<NCCityGroupRegistrySystem>();
        var physicalization = server.System<NCCityGroupPhysicalizationSystem>();
        var formations = server.System<NCCityGroupRuntimeFormationSystem>();
        var runtime = server.System<NCCityActorRuntimeIndexSystem>();
        var dematerialization = server.System<NCCityActorDematerializationSystem>();
        Entity<NCZMapNetworkComponent> network = default;

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady && physicalization.IsActive,
            "The active-world group physicalization boundary did not start.");

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
            for (var x = 0; x < 10; x++)
            {
                for (var y = 0; y < 10; y++)
                    tiles.Add((new Vector2i(x, y), new Tile(1)));
            }
            maps.SetTiles(grid, tiles);
        });
        await server.WaitRunTicks(45);

        var actorScope = Guid.NewGuid();
        var leader = await actors.CreateAsync(new NCCityActorCreateRequest(
            Guid.NewGuid(), actorScope, "formation-leader", "Rosa Pike", [], Archetype,
            HomeZone, HomeZone, Guid.NewGuid(), AuditSource: "integration.group-physicalization"));
        var follower = await actors.CreateAsync(new NCCityActorCreateRequest(
            Guid.NewGuid(), actorScope, "formation-follower", "Jin Vale", [], Archetype,
            HomeZone, HomeZone, Guid.NewGuid(), AuditSource: "integration.group-physicalization"));
        Assert.That(new[] { leader.Status, follower.Status }, Is.All.EqualTo(NCCityActorMutationStatus.Applied));

        var createdGroup = await groups.CreateAsync(new NCCityGroupCreateRequest(
            Guid.NewGuid(), Guid.NewGuid(), "formation-crew", "Formation Crew", GroupProfile, default,
            leader.ActorId, "leader", HomeZone, "integration-patrol", Guid.NewGuid(),
            AuditSource: "integration.group-physicalization"));
        Assert.That(createdGroup.Status, Is.EqualTo(NCCityGroupMutationStatus.Applied), createdGroup.Error);
        var added = await groups.AddMemberAsync(new NCCityGroupAddMemberRequest(
            Guid.NewGuid(), createdGroup.GroupId, createdGroup.Groups.Single().AggregateVersion,
            follower.ActorId, "wing", Guid.NewGuid(), AuditSource: "integration.group-physicalization"));
        Assert.That(added.Status, Is.EqualTo(NCCityGroupMutationStatus.Applied), added.Error);

        var materializeId = Guid.NewGuid();
        NCCityGroupPhysicalizationRequestState materializeSubmitted = null!;
        await server.WaitPost(() => materializeSubmitted = physicalization.Request(
            new NCCityGroupPhysicalizationRequest(
                materializeId,
                createdGroup.GroupId,
                NCCityGroupPhysicalizationDirection.Materialize,
                NCCityGroupPhysicalizationReason.Admin,
                900,
                17,
                TimeSpan.FromMinutes(1),
                "integration.group-physicalization")));
        Assert.That(materializeSubmitted.Status, Is.EqualTo(NCCityGroupPhysicalizationStatus.Queued));

        NCCityGroupPhysicalizationRequestState materialized = null!;
        await WaitUntil(server, () =>
        {
            if (!physicalization.TryGetRequest(materializeId, out var state) || IsActive(state.Status))
                return false;
            materialized = state;
            return true;
        }, "The group did not reach a terminal materialization state.", 900);

        EntityUid leaderEntity = default;
        EntityUid followerEntity = default;
        await server.WaitAssertion(() =>
        {
            Assert.That(materialized.Status, Is.EqualTo(NCCityGroupPhysicalizationStatus.Materialized),
                materialized.Error);
            Assert.That(runtime.TryGetEntity(leader.ActorId, out leaderEntity), Is.True);
            Assert.That(runtime.TryGetEntity(follower.ActorId, out followerEntity), Is.True);
            Assert.That(formations.TryGetGroup(createdGroup.GroupId, out var formation), Is.True);
            Assert.That(formation.Members, Has.Count.EqualTo(2));
            Assert.That(formation.Policy, Is.EqualTo(NCCityGroupFormationPolicy.Column));
            Assert.That(formation.LeaderEntity, Is.EqualTo(leaderEntity));
            var leaderSlot = entities.GetComponent<NCCityGroupFormationComponent>(leaderEntity);
            var followerSlot = entities.GetComponent<NCCityGroupFormationComponent>(followerEntity);
            Assert.That(leaderSlot.IsLeader, Is.True);
            Assert.That(leaderSlot.DesiredOffset, Is.EqualTo(Vector2.Zero));
            Assert.That(followerSlot.IsLeader, Is.False);
            Assert.That(followerSlot.DesiredOffset.Y, Is.LessThan(0f));
            entities.AddComponent<NCCityRelevanceAnchorComponent>(leaderEntity);
        });

        var dematerializeId = Guid.NewGuid();
        await server.WaitRunTicks(451);
        await server.WaitAssertion(() =>
        {
            entities.GetComponent<NCCityRelevanceAnchorComponent>(leaderEntity).Enabled = false;
            Assert.That(dematerialization.IsEligible(leader.ActorId, leaderEntity, out var error), Is.False);
            Assert.That(error, Does.Contain("group hand-off"));
            var submitted = physicalization.Request(new NCCityGroupPhysicalizationRequest(
                dematerializeId,
                createdGroup.GroupId,
                NCCityGroupPhysicalizationDirection.Dematerialize,
                NCCityGroupPhysicalizationReason.Admin,
                900,
                29,
                TimeSpan.FromMinutes(1),
                "integration.group-physicalization"));
            Assert.That(submitted.Status, Is.EqualTo(NCCityGroupPhysicalizationStatus.Queued));
        });

        NCCityGroupPhysicalizationRequestState dematerialized = null!;
        await WaitUntil(server, () =>
        {
            if (!physicalization.TryGetRequest(dematerializeId, out var state) || IsActive(state.Status))
                return false;
            dematerialized = state;
            return true;
        }, "The group did not reach a terminal dematerialization state.", 2400);

        await server.WaitRunTicks(1);
        await server.WaitAssertion(() =>
        {
            Assert.That(dematerialized.Status, Is.EqualTo(NCCityGroupPhysicalizationStatus.Dematerialized),
                dematerialized.Error);
            Assert.That(runtime.TryGetEntity(leader.ActorId, out _), Is.False);
            Assert.That(runtime.TryGetEntity(follower.ActorId, out _), Is.False);
            Assert.That(entities.EntityExists(leaderEntity), Is.False);
            Assert.That(entities.EntityExists(followerEntity), Is.False);
            Assert.That(formations.TryGetGroup(createdGroup.GroupId, out _), Is.False);
        });

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
        await server.WaitPost(() => zLevels.DeleteMapNetwork(network));
        await server.WaitRunTicks(1);
    }

    private static bool IsActive(NCCityGroupPhysicalizationStatus status) =>
        status is NCCityGroupPhysicalizationStatus.Queued or
            NCCityGroupPhysicalizationStatus.Loading or
            NCCityGroupPhysicalizationStatus.Retrying or
            NCCityGroupPhysicalizationStatus.MaterializingMembers or
            NCCityGroupPhysicalizationStatus.ApplyingFormation or
            NCCityGroupPhysicalizationStatus.DematerializingMembers or
            NCCityGroupPhysicalizationStatus.RollingBack;

    private static async Task WaitUntil(
        RobustIntegrationTest.ServerIntegrationInstance server,
        Func<bool> predicate,
        string failure,
        int attempts = 120)
    {
        for (var i = 0; i < attempts; i++)
        {
            var completed = false;
            await server.WaitAssertion(() => completed = predicate());
            if (completed)
                return;
            await server.WaitRunTicks(1);
        }
        Assert.Fail(failure);
    }
}
