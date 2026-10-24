// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Meta.Language;
using Content.Meta.Runtime;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Actors.Domains;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Meta.Demo;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Domains;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;
using Content.Server._NC.Meta.Network;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Chemistry;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Power.Components;
using Content.Shared.Power.EntitySystems;
using Content.Shared.PowerCell.Components;
using Content.Shared.Preferences;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._NC.Meta;

/// <summary>Physical D20 acceptance: native chemistry/medical outcomes, with genuine installed META invocations.</summary>
[TestFixture, NonParallelizable]
[TestOf(typeof(NCMetaMachineSystem))]
[TestOf(typeof(NCMetaClinicalSystem))]
public sealed partial class NCMetaLaboratoryClinicalTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = false };
    [SidedDependency(Side.Server)] private NCMetaDemoSceneSystem _scenes = default!;
    [SidedDependency(Side.Server)] private NCMetaDeviceRegistrySystem _devices = default!;
    [SidedDependency(Side.Server)] private NCMetaRuntimeSystem _runtime = default!;
    [SidedDependency(Side.Server)] private NCMetaMachineSystem _machines = default!;
    [SidedDependency(Side.Server)] private NCMetaClinicalSystem _clinical = default!;
    [SidedDependency(Side.Server)] private SharedSolutionContainerSystem _solutions = default!;
    [SidedDependency(Side.Server)] private ItemSlotsSystem _slots = default!;
    [SidedDependency(Side.Server)] private SharedBatterySystem _batteries = default!;
    [SidedDependency(Side.Server)] private SharedTransformSystem _transforms = default!;
    [SidedDependency(Side.Server)] private IServerDbManager _database = default!;

    private sealed record Scene(NCMetaDemoSceneInstance Physical, string Owner, NCActorId ActorId, EntityUid? ClinicalHost = null)
    {
        public EntityUid Actor => Physical.Operator;
        public EntityUid this[string key] => key == "host" && ClinicalHost is { } clinical ? clinical : Physical.Objects[key];
    }
    private sealed class Invocation { public bool Completed; }

    private async Task Until(Func<bool> condition, string reason, int maximumTicks = 900)
    {
        var elapsed = Stopwatch.StartNew();
        for (var tick = 0; tick < maximumTicks && elapsed.Elapsed < TimeSpan.FromSeconds(30); tick++)
        {
            var complete = false;
            var remaining = TimeSpan.FromSeconds(30) - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await Server.WaitPost(() => complete = condition()).WaitAsync(remaining);
            if (complete) return;
            remaining = TimeSpan.FromSeconds(30) - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await Server.WaitRunTicks(1).WaitAsync(remaining);
        }
        Assert.Fail(reason);
    }

    private Task<T> Durable<T>(Func<Task<T>> operation) => NCMetaNativeTestTasks.Run(Server, operation);

    private async Task AdvancePast(TimeSpan duration)
    {
        var ticks = 0;
        await Server.WaitAssertion(() => ticks = (int) Math.Ceiling(duration.TotalSeconds / Server.ResolveDependency<IGameTiming>().TickPeriod.TotalSeconds) + 1);
        Assert.That(ticks, Is.InRange(1, 1800));
        await Server.WaitRunTicks(ticks).WaitAsync(TimeSpan.FromSeconds(30));
    }

    private void MoveNear(EntityUid actor, EntityUid target) => _transforms.SetCoordinates(actor,
        SComp<TransformComponent>(target).Coordinates.Offset(new Vector2(0, -0.45f)));
    private void MoveBetween(Scene scene, EntityUid target) => _transforms.SetCoordinates(scene.Actor,
        new EntityCoordinates(scene.Physical.Grid, (SComp<TransformComponent>(scene["host"]).Coordinates.Position +
            SComp<TransformComponent>(target).Coordinates.Position) / 2 + new Vector2(0, -0.45f)));

    private void ChargePortable(EntityUid uid)
    {
        if (!STryComp<PowerCellSlotComponent>(uid, out var slot)) return;
        var cell = _slots.GetItemOrNull(uid, slot.CellSlotId);
        if (cell == null)
        {
            cell = SSpawnAtPosition("PowerCellSmall", SComp<TransformComponent>(uid).Coordinates);
            Assert.That(_slots.TryInsert(uid, slot.CellSlotId, cell.Value, null), Is.True);
        }
        _batteries.SetCharge(cell.Value, Math.Min(1000f, SComp<BatteryComponent>(cell.Value).MaxCharge));
    }

    private async Task Claim(Scene scene, EntityUid uid)
    {
        await Until(() => _devices.TryGetEntry(uid, out var entry) && entry.Ready && _devices.IsPowered(uid), "Physical endpoint did not become ready/powered.");
        await Server.WaitAssertion(() => { MoveNear(scene.Actor, uid); Assert.That(_devices.TryClaim(uid, scene.Actor), Is.True); });
        await Until(() => _devices.TryGetEntry(uid, out var entry) && entry.Owner == scene.Owner, "Native ownership claim did not commit.");
    }

    private async Task RestoreActorBinding(Scene scene)
    {
        var actors = Server.System<NCCityActorRegistrySystem>();
        var domains = Server.System<NCCityActorDomainRegistrySystem>();
        Assert.That(await Durable(() => actors.GetAsync(scene.ActorId)), Is.Not.Null);
        var health = await Durable(() => domains.GetHealthAsync(scene.ActorId));
        var inventory = await Durable(() => domains.GetInventoryAsync(scene.ActorId));
        Assert.That(health, Is.Not.Null); Assert.That(inventory, Is.Not.Null);
        var physicalization = Server.System<NCEconomicPhysicalizationSystem>();
        var plan = await Durable(() => physicalization.BuildActorInventoryPlanAsync(scene.ActorId, inventory!.Snapshot));
        Assert.That(plan.IsSuccess, Is.True, plan.Error);
        await Server.WaitAssertion(() =>
        {
            // The fixture keeps its physical body across selected-world tests. Restore the same native inventory
            // authority and economic lease that NCPhysicalActorLifecycleSystem initializes for a spawned resident.
            var adapter = SEntMan.EnsureComponent<NCCityActorInventoryAdapterComponent>(scene.Actor);
            adapter.AuthorityVersion = inventory!.Snapshot.SourceAuthorityVersion;
            adapter.EquippedLoadoutId = inventory.Snapshot.Summary.EquippedLoadoutId;
            Assert.That(physicalization.TryMaterialize(scene.Actor, plan.Plan!, out var error), Is.True, error);
            Assert.That(Server.System<NCCityActorRuntimeIndexSystem>().TryBind(scene.ActorId, scene.Actor), Is.True);
            Assert.That(Server.System<NCActiveWorldTimeSystem>().TryGetCurrentTime(out _, out var now), Is.True);
            Assert.That(domains.TryCaptureMaterialized(scene.ActorId, scene.Actor, health!.AggregateVersion,
                inventory.AggregateVersion, now, out _, out error), Is.True, error);
        });
    }

    private async Task<Scene> CreateScene(bool clinicalHost = false)
    {
        await Server.WaitPost(() => SEntMan.EventBus.RaiseEvent(EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await Until(() => Server.System<NCActiveWorldTimeSystem>().IsReady, "Active-world clock did not start.");
        NCMetaDemoSceneInstance physical = null!;
        EntityUid? clinical = null;
        var actorId = new NCActorId(Guid.NewGuid()); HumanoidCharacterProfile profile = null!;
        await Server.WaitAssertion(() =>
        {
            Assert.That(_scenes.TryCreate("NCMetaBedsideLabScene", out physical), Is.True);
            profile = HumanoidCharacterProfile.Random();
            ChargePortable(physical.Objects["bedside"]);
            if (clinicalHost)
            {
                clinical = SSpawnAtPosition("NCMetaClinicalController", SComp<TransformComponent>(physical.Objects["host"]).Coordinates);
                ChargePortable(clinical.Value);
                MoveNear(physical.Operator, clinical.Value);
                var cable = SSpawnAtPosition("NCMetaNetworkCable", SComp<TransformComponent>(clinical.Value).Coordinates);
                var network = Server.System<NCMetaNetworkSystem>();
                Assert.That(network.TryConnectCable(cable, physical.Operator, clinical.Value), Is.True);
                Assert.That(network.TryConnectCable(cable, physical.Operator, physical.Objects["host"]), Is.True);
            }
        });
        var request = Guid.NewGuid();
        var payload = NCHumanoidProfilePayload.Serialize(Server.ResolveDependency<ISerializationManager>(), profile);
        var created = await Durable(() => Server.System<NCCityActorRegistrySystem>().CreateResidentAsync(new(request, actorId,
            "Native laboratory practitioner", new("NCLivingCityResident"), new("NCDefaultResidentPersona"), payload,
            default, null, request, AuditSource: "integration.meta-laboratory")));
        Assert.That(created.Status, Is.EqualTo(NCCityActorMutationStatus.Applied), created.Error);
        var scene = new Scene(physical, NCPartyRef.ForActor(actorId).ToString(), actorId, clinical);
        await RestoreActorBinding(scene);
        foreach (var uid in physical.Objects.Where(value => value.Key is "host" or "bedside" or "scanner" or "grinder" or "chemMaster").Select(value => value.Value)
                     .Concat(clinical is { } sourceHost ? new[] { sourceHost } : []).Distinct()) await Claim(scene, uid);
        return scene;
    }

    private async Task Finish(Scene scene)
    {
        await Server.WaitAssertion(() =>
        {
            // Test-spawned surgical tools are transient physical props, not economic assets. Use the ordinary
            // drop action before the persistent actor checkpoint; never invent an item identity/adapter for them.
            var hands = Server.System<SharedHandsSystem>();
            foreach (var held in hands.EnumerateHeld(scene.Actor).ToArray())
                if (!SEntMan.HasComponent<NCCityPersistentItemAdapterComponent>(held))
                    Assert.That(hands.TryDrop(scene.Actor, held), Is.True);
        });
        await Server.WaitPost(() => SEntMan.EventBus.RaiseEvent(EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
        await Until(() => !Server.System<NCActiveWorldTimeSystem>().IsReady, "Native world close did not drain its real operations.");
        await Durable(async () => { await Server.System<NCActiveWorldTimeSystem>().WaitForIdleAsync(); return true; });
        Assert.That((await Durable(() => _database.GetNCActiveWorldClockAsync()))!.RoundActive, Is.False,
            "The fixture must durably close the world, rather than treating a failed clock as successful cleanup.");
        await Server.WaitPost(() => _scenes.TryDestroy(scene.Physical.Id));
        await Server.WaitRunTicks(2);
    }

    private async Task<NCMetaNativeNetworkProgramHarness.Installed> Install(Scene scene, string source,
        IReadOnlyDictionary<string, EntityUid> targets, string package)
    {
        await Server.WaitPost(() => MoveNear(scene.Actor, scene["host"]));
        // The shared helper uses ordinary source save/publish/install, exact link consents and durable service approval.
        return await new NCMetaNativeNetworkProgramHarness(Server).InstallAndStartAsync(scene["host"], scene.Actor,
            "native.lab", package, source, targets, retainForInvocations: true).WaitAsync(TimeSpan.FromSeconds(45));
    }

    private async Task<Invocation> Invoke(Scene scene, NCMetaNativeNetworkProgramHarness.Installed program, string function)
    {
        await Until(() => program.Runtime.Process.State == MetaProcessState.Idle, "The real retained VM did not reach invocation idle.");
        var invocation = new Invocation();
        var id = Enumerable.Range(0, program.Artifact.FunctionCount).Single(index => program.Artifact.GetFunction(index).Name == "native.lab." + function);
        await Server.WaitAssertion(() => Assert.That(_runtime.TryInvokeAuthorized(scene["host"], program.Runtime.Generation, scene.Actor, id,
            _ => [], (_, _) => invocation.Completed = true, () => _devices.CanManage(scene["host"], scene.Actor)), Is.True));
        return invocation;
    }

    private Solution Solution(EntityUid entity, string name)
    {
        Assert.That(_solutions.TryGetSolution(entity, name, out _, out var solution), Is.True);
        return solution!;
    }
    private void AddWater(EntityUid entity, string name, int amount)
    {
        Assert.That(_solutions.TryGetSolution(entity, name, out var solution, out _), Is.True);
        if (name == SharedChemMaster.BufferSolutionName && SEntMan.HasComponent<Content.Server.Chemistry.Components.ChemMasterComponent>(entity))
        {
            // ChemMaster's native transfer path fills this unlimited buffer directly;
            // its zero MaxVolume deliberately rejects the general bounded-container API.
            var buffer = solution!.Value.Comp.Solution;
            var before = buffer.Volume;
            buffer.AddReagent("Water", FixedPoint2.New(amount));
            _solutions.UpdateChemicals(solution.Value);
            Assert.That(buffer.Volume, Is.EqualTo(before + FixedPoint2.New(amount)));
            return;
        }
        Assert.That(_solutions.TryAddReagent(solution!.Value, "Water", FixedPoint2.New(amount)), Is.True);
    }

    private async Task<T> Receipt<T>(Scene scene, EntityUid device, string type, string kind, string key, Func<T, bool> terminal) where T : class
    {
        NCMetaDeviceEntry entry = null!;
        await Server.WaitAssertion(() => Assert.That(_devices.TryGetEntry(device, out entry), Is.True));
        var id = NCMetaPersistenceSystem.StableGuid(MetaOperationReceipt.HashFields(type == "meta.machine.operation" ? "meta.machine" : "meta.clinical",
            scene.Owner, entry.Id, kind, key));
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(30))
        {
            var row = await Durable(() => _database.GetNCAggregateSnapshotAsync(type, id.ToString("D")));
            var receipt = row == null ? null : JsonSerializer.Deserialize<T>(row.Payload);
            if (receipt != null && terminal(receipt)) return receipt;
            await Server.WaitRunTicks(1).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Fail("The native physical operation did not produce a terminal durable receipt.");
        throw new InvalidOperationException();
    }
}
