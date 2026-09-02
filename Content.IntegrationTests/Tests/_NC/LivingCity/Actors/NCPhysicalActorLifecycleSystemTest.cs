// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.ZLevels.Core;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Preferences;
using Content.Shared._NC.ZLevels.Core.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Actors;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(NCPhysicalActorLifecycleSystem))]
public sealed class NCPhysicalActorLifecycleSystemTest : GameTest
{
    [TestPrototypes]
    private const string LifecyclePrototypes = """
        - type: ncZoneSet
          id: NCPhysicalActorLifecycleIntegrationZones
          networkId: 264293f2-8918-4728-9c22-e3240567cabe
          zones:
          - id: f50e6005-0e6c-4c1c-8b70-98223971d8de
            kind: District
            name: Physical Actor Test District
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
            name: Physical Actor Test Building
            geometry:
            - kind: Polygon
              z: 0
              vertices:
              - 0,0
              - 8,0
              - 8,8
              - 0,8

        - type: ncCityActorArchetype
          id: NCPhysicalActorLifecycleIntegrationCivilian
          entityPrototype: MobHuman
          spawnCandidateProfile: NCPedestrianSpawnCandidates
          travelMode: Pedestrian
        """;

    private static readonly NCZNetworkId NetworkId =
        new(Guid.Parse("264293f2-8918-4728-9c22-e3240567cabe"));
    private static readonly NCZoneId HomeZone =
        new(Guid.Parse("28a2ef85-4254-4d6c-84f9-086db4f69e86"));
    private static readonly ProtoId<NCCityActorArchetypePrototype> Archetype =
        new("NCPhysicalActorLifecycleIntegrationCivilian");
    private static readonly ProtoId<NCNeuralPersonaProfilePrototype> Persona =
        new("NCDefaultResidentPersona");

    public override PoolSettings PoolSettings => new() { Connected = false };

    [Test]
    public async Task PersistentActorOwnsExactlyOnePermanentPhysicalEntity()
    {
        var server = Pair.Server;
        var maps = server.System<SharedMapSystem>();
        var entities = server.ResolveDependency<IEntityManager>();
        var zLevels = server.System<NCZLevelsSystem>();
        var clock = server.System<NCActiveWorldTimeSystem>();
        var actors = server.System<NCCityActorRegistrySystem>();
        var lifecycle = server.System<NCPhysicalActorLifecycleSystem>();
        var runtime = server.System<NCCityActorRuntimeIndexSystem>();
        Entity<NCZMapNetworkComponent> network = default;

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady && lifecycle.IsActive,
            "The physical actor lifecycle did not start.");

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
        await server.WaitRunTicks(45);

        var serialization = server.ResolveDependency<ISerializationManager>();
        var profile = HumanoidCharacterProfile.Random();
        var commandId = Guid.NewGuid();
        var create = await actors.CreateResidentAsync(new NCCityResidentCreateRequest(
            commandId,
            new NCActorId(Guid.Parse("5148d738-0492-46da-9abf-0bd2b7b2dabb")),
            "Mara Velez",
            Archetype,
            Persona,
            NCHumanoidProfilePayload.Serialize(serialization, profile),
            HomeZone,
            new NCCityActorPhysicalRecoveryState(NetworkId, 1f, 1f, 0, HomeZone),
            commandId,
            AuditSource: "integration.physical-lifecycle"));
        Assert.That(create.Status, Is.EqualTo(NCCityActorMutationStatus.Applied), create.Error);

        var requestId = Guid.NewGuid();
        NCPhysicalActorSpawnState submitted = null!;
        await server.WaitPost(() => submitted = lifecycle.Request(new NCPhysicalActorSpawnRequest(
            requestId,
            create.ActorId,
            NCPhysicalActorSpawnReason.ServiceDispatch,
            "integration.physical-lifecycle")));
        Assert.That(submitted.Status, Is.EqualTo(NCPhysicalActorSpawnStatus.Queued));

        NCPhysicalActorSpawnState completed = null!;
        await WaitUntil(server, () =>
        {
            if (!lifecycle.TryGetRequest(requestId, out var state) ||
                state.Status is NCPhysicalActorSpawnStatus.Queued or
                    NCPhysicalActorSpawnStatus.Loading or
                    NCPhysicalActorSpawnStatus.Retrying)
            {
                return false;
            }
            completed = state;
            return true;
        }, "The persistent actor did not become physical.", 240);

        await server.WaitAssertion(() =>
        {
            Assert.That(completed.Status, Is.EqualTo(NCPhysicalActorSpawnStatus.Spawned), completed.Error);
            Assert.That(completed.EntityUid.HasValue, Is.True);
            Assert.That(entities.EntityExists(completed.EntityUid!.Value), Is.True);
            Assert.That(runtime.PhysicalActorCount, Is.EqualTo(1));

            var duplicate = lifecycle.Request(new NCPhysicalActorSpawnRequest(
                Guid.NewGuid(),
                create.ActorId,
                NCPhysicalActorSpawnReason.ServiceDispatch,
                "integration.physical-lifecycle.duplicate"));
            Assert.That(duplicate.Status, Is.EqualTo(NCPhysicalActorSpawnStatus.AlreadyPhysical));
            Assert.That(duplicate.EntityUid, Is.EqualTo(completed.EntityUid));
            Assert.That(runtime.PhysicalActorCount, Is.EqualTo(1));
        });

        await server.WaitRunTicks(30);
        await server.WaitAssertion(() =>
        {
            Assert.That(runtime.TryGetEntity(create.ActorId, out var entity), Is.True);
            Assert.That(entities.EntityExists(entity), Is.True);
        });

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
        await server.WaitPost(() => zLevels.DeleteMapNetwork(network));
    }

    private static async Task WaitUntil(
        RobustIntegrationTest.ServerIntegrationInstance server,
        Func<bool> predicate,
        string message,
        int maximumTicks = 120)
    {
        for (var tick = 0; tick < maximumTicks; tick++)
        {
            if (predicate())
                return;
            await server.WaitRunTicks(1);
        }
        Assert.Fail(message);
    }
}
