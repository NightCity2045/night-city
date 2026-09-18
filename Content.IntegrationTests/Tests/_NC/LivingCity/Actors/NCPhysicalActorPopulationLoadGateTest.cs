// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Neural;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.ZLevels.Core;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Configuration;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.Preferences;
using Content.Shared._NC.ZLevels.Core.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Actors;

/// <summary>
/// Opt-in integration load gate for the v3 invariant that every living resident remains one physical ECS body.
/// This deliberately tests production persistence, lifecycle, perception and brain scheduling without a provider.
/// </summary>
[TestFixture]
[NonParallelizable]
[TestOf(typeof(NCPhysicalActorLifecycleSystem))]
public sealed class NCPhysicalActorPopulationLoadGateTest : GameTest
{
    [TestPrototypes]
    private const string LoadGatePrototypes = """
        - type: ncZoneSet
          id: NCPhysicalActorPopulationLoadGateZones
          networkId: c98005db-dfec-4e91-9ccb-670cc07799d0
          zones:
          - id: fd675ee4-4a0f-4211-839e-eae7ccb37602
            kind: District
            name: Physical Population Load Gate District
            geometry:
            - kind: Polygon
              global: true
              vertices:
              - 0,0
              - 64,0
              - 64,32
              - 0,32
          - id: ec938af7-6bd3-4ce3-8f0b-d9884dfc15da
            kind: Building
            parent: fd675ee4-4a0f-4211-839e-eae7ccb37602
            name: Physical Population Load Gate Building
            geometry:
            - kind: Polygon
              z: 0
              vertices:
              - 0,0
              - 64,0
              - 64,32
              - 0,32

        - type: ncCityActorArchetype
          id: NCPhysicalActorPopulationLoadGateResident
          entityPrototype: MobHuman
          spawnCandidateProfile: NCPedestrianSpawnCandidates
          travelMode: Pedestrian
        """;

    private static readonly int[] Milestones = [100, 200, 250, 300, 350];
    private static readonly NCZNetworkId NetworkId =
        new(Guid.Parse("c98005db-dfec-4e91-9ccb-670cc07799d0"));
    private static readonly NCZoneId HomeZone =
        new(Guid.Parse("ec938af7-6bd3-4ce3-8f0b-d9884dfc15da"));
    // This ID is installed by the fixture, so keep it out of the production YAML static-field validation pass.
    private readonly ProtoId<NCCityActorArchetypePrototype> Archetype =
        new("NCPhysicalActorPopulationLoadGateResident");
    private static readonly ProtoId<NCNeuralPersonaProfilePrototype> Persona =
        new("NCDefaultResidentPersona");

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
        Dirty = true,
    };

    [Test]
    public async Task PopulationRemainsPhysicalThroughThreeHundredFiftyActors()
    {
        var server = Pair.Server;
        var maps = server.System<SharedMapSystem>();
        var entities = server.ResolveDependency<IEntityManager>();
        var serialization = server.ResolveDependency<ISerializationManager>();
        var zLevels = server.System<NCZLevelsSystem>();
        var clock = server.System<NCActiveWorldTimeSystem>();
        var actors = server.System<NCCityActorRegistrySystem>();
        var lifecycle = server.System<NCPhysicalActorLifecycleSystem>();
        var runtime = server.System<NCCityActorRuntimeIndexSystem>();
        var perception = server.System<NCCityLocalPerceptionSystem>();
        var brain = server.System<NCPhysicalActorBrainSystem>();

        server.CfgMan.SetCVar(NCCVars.NeuralEnabled, false);
        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server,
            () => clock.IsReady && lifecycle.IsActive,
            "The load-gate active-world session did not start.",
            600);

        NCSessionEpoch epoch = default;
        await server.WaitAssertion(() =>
        {
            Assert.That(clock.TryGetCurrentTime(out epoch, out _), Is.True);
            maps.CreateMap(out var mapId);
            var network = zLevels.CreateMapNetwork(NetworkId);
            Assert.That(zLevels.TryAddMapsIntoNetwork(network, new Dictionary<EntityUid, int>
            {
                [maps.GetMap(mapId)] = 0,
            }), Is.True);

            var grid = maps.CreateGridEntity(mapId);
            var tiles = new List<(Vector2i, Tile)>(64 * 32);
            for (var x = 0; x < 64; x++)
            {
                for (var y = 0; y < 32; y++)
                    tiles.Add((new Vector2i(x, y), new Tile(1)));
            }
            maps.SetTiles(grid, tiles);
        });
        await server.WaitRunTicks(45);

        HumanoidCharacterProfile profile = null!;
        await server.WaitPost(() => profile = HumanoidCharacterProfile.Random());
        var humanoidPayload = NCHumanoidProfilePayload.Serialize(serialization, profile);
        var actorIds = new List<NCActorId>(Milestones[^1]);
        var observedLifecycleWork = false;

        foreach (var milestone in Milestones)
        {
            await CreateThroughMilestone(server, actors, humanoidPayload, actorIds, milestone);
            await WaitUntil(server,
                () =>
                {
                    observedLifecycleWork |= lifecycle.QueuedCount > 0 || lifecycle.LoadsInFlight > 0;
                    var perceptionCounters = perception.GetCounters();
                    return runtime.PhysicalActorCount == milestone &&
                           lifecycle.QueuedCount == 0 &&
                           lifecycle.LoadsInFlight == 0 &&
                           perceptionCounters.TrackedActors == milestone &&
                           perceptionCounters.ScheduledUpdates == milestone &&
                           brain.ScheduledActors + brain.SubmissionsInFlight == milestone;
                },
                $"The physical population did not converge at milestone {milestone}.",
                6000,
                () => Diagnostics(milestone, runtime, lifecycle, perception, brain));

            await server.WaitAssertion(() =>
            {
                Assert.That(clock.TryGetCurrentTime(out var currentEpoch, out _), Is.True);
                Assert.That(currentEpoch, Is.EqualTo(epoch), "A load milestone crossed active-world sessions.");
                AssertPopulation(actorIds, milestone, actors, runtime, entities);
            });
        }

        Assert.That(observedLifecycleWork, Is.True,
            "The fixture never observed the production lifecycle queue or an authoritative domain load.");

        var stableBodies = new Dictionary<NCActorId, EntityUid>(actorIds.Count);
        long evaluationsBefore = 0;
        await server.WaitAssertion(() =>
        {
            evaluationsBefore = perception.GetCounters().Evaluations;
            foreach (var actorId in actorIds)
            {
                Assert.That(runtime.TryGetEntity(actorId, out var body), Is.True);
                Assert.That(stableBodies.TryAdd(actorId, body), Is.True);
            }
        });

        await server.WaitRunTicks(300);
        await WaitUntil(server,
            () => lifecycle.QueuedCount == 0 && lifecycle.LoadsInFlight == 0 &&
                  perception.GetCounters().TrackedActors == Milestones[^1] &&
                  brain.ScheduledActors + brain.SubmissionsInFlight == Milestones[^1],
            "The settled population stopped being scheduled after survival ticks.",
            600,
            () => Diagnostics(Milestones[^1], runtime, lifecycle, perception, brain));
        await server.WaitAssertion(() =>
        {
            Assert.That(runtime.PhysicalActorCount, Is.EqualTo(Milestones[^1]));
            Assert.That(perception.GetCounters().Evaluations, Is.GreaterThan(evaluationsBefore),
                "Tracked perception made no progress while 350 bodies remained alive.");
            foreach (var (actorId, originalBody) in stableBodies)
            {
                Assert.That(runtime.TryGetEntity(actorId, out var currentBody), Is.True);
                Assert.That(currentBody, Is.EqualTo(originalBody),
                    $"Actor {actorId} disappeared or changed its permanent body after the load gate settled.");
                Assert.That(entities.EntityExists(currentBody), Is.True);
            }
        });

        TestContext.Progress.WriteLine(
            $"Physical population load gate passed: milestones={string.Join("->", Milestones)}, " +
            $"bodies={runtime.PhysicalActorCount}, perception={perception.GetCounters().TrackedActors}, " +
            $"brain-scheduled={brain.ScheduledActors}, brain-in-flight={brain.SubmissionsInFlight}.");
        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
    }

    private async Task CreateThroughMilestone(
        RobustIntegrationTest.ServerIntegrationInstance server,
        NCCityActorRegistrySystem actors,
        string humanoidPayload,
        List<NCActorId> actorIds,
        int milestone)
    {
        const int persistenceBatchSize = 20;
        while (actorIds.Count < milestone)
        {
            var count = Math.Min(persistenceBatchSize, milestone - actorIds.Count);
            var tasks = new List<Task<NCCityActorMutationResult>>(count);
            for (var offset = 0; offset < count; offset++)
            {
                var index = actorIds.Count + offset;
                var stableKey = $"resident-{index:D3}";
                var actorId = new NCActorId(NCPersistentIdentity.Command(
                    "integration-physical-population",
                    "actor",
                    stableKey));
                var commandId = NCPersistentIdentity.Command(
                    "integration-physical-population",
                    "create",
                    stableKey);
                var x = 2f + index % 30 * 2f;
                var y = 2f + index / 30 * 2f;
                tasks.Add(actors.CreateResidentAsync(new NCCityResidentCreateRequest(
                    commandId,
                    actorId,
                    $"Load Resident {index:D3}",
                    Archetype,
                    Persona,
                    humanoidPayload,
                    HomeZone,
                    new NCCityActorPhysicalRecoveryState(NetworkId, x, y, 0, HomeZone),
                    commandId,
                    AuditSource: "integration.physical-population-load-gate")));
            }

            var results = await Task.WhenAll(tasks);
            foreach (var result in results)
            {
                Assert.That(result.Status, Is.EqualTo(NCCityActorMutationStatus.Applied), result.Error);
                Assert.That(actorIds.Contains(result.ActorId), Is.False, "A load batch repeated a stable ActorId.");
                actorIds.Add(result.ActorId);
            }

            // Persisted notifications are marshalled back to the ECS thread and become lifecycle requests here.
            await server.WaitRunTicks(1);
            await Task.Delay(1);
        }
    }

    private static void AssertPopulation(
        IReadOnlyList<NCActorId> actorIds,
        int expected,
        NCCityActorRegistrySystem actors,
        NCCityActorRuntimeIndexSystem runtime,
        IEntityManager entities)
    {
        Assert.That(actorIds, Has.Count.EqualTo(expected));
        var bodies = new HashSet<EntityUid>();
        foreach (var actorId in actorIds)
        {
            Assert.That(actors.TryGetCached(actorId, out var actor), Is.True);
            Assert.That(actor.Snapshot.Lifecycle.Status, Is.EqualTo(NCCityActorStatus.Active));
            Assert.That(runtime.TryGetEntity(actorId, out var body), Is.True);
            Assert.That(entities.EntityExists(body), Is.True);
            Assert.That(bodies.Add(body), Is.True, "Two persistent ActorIds shared one physical body.");
            Assert.That(entities.HasComponent<NCCityActorBindingComponent>(body), Is.True);
            Assert.That(entities.HasComponent<NCCityLocalPerceptionComponent>(body), Is.True);
        }
        Assert.That(bodies, Has.Count.EqualTo(expected));
    }

    private static string Diagnostics(
        int expected,
        NCCityActorRuntimeIndexSystem runtime,
        NCPhysicalActorLifecycleSystem lifecycle,
        NCCityLocalPerceptionSystem perception,
        NCPhysicalActorBrainSystem brain)
    {
        var perceptionCounters = perception.GetCounters();
        return $"expected={expected}, physical={runtime.PhysicalActorCount}, queued={lifecycle.QueuedCount}, " +
               $"loads={lifecycle.LoadsInFlight}, perception={perceptionCounters.TrackedActors}, " +
               $"perception-scheduled={perceptionCounters.ScheduledUpdates}, " +
               $"brain-scheduled={brain.ScheduledActors}, brain-in-flight={brain.SubmissionsInFlight}";
    }

    private static async Task WaitUntil(
        RobustIntegrationTest.ServerIntegrationInstance server,
        Func<bool> predicate,
        string message,
        int maximumTicks,
        Func<string>? diagnostics = null)
    {
        const int tickBatch = 5;
        for (var elapsed = 0; elapsed < maximumTicks; elapsed += tickBatch)
        {
            if (predicate())
                return;
            await server.WaitRunTicks(Math.Min(tickBatch, maximumTicks - elapsed));
            // Database/domain loads complete on worker threads while integration ticks advance without wall delay.
            await Task.Delay(10);
        }
        if (predicate())
            return;
        Assert.Fail(diagnostics == null ? message : $"{message} {diagnostics()}");
    }
}
