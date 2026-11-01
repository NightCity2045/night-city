// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Meta.Authority;
using Content.Server._NC.Meta.Demo;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Network;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Meta.Network;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Preferences;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Manager;

namespace Content.IntegrationTests.Tests._NC.Meta;

[TestFixture, NonParallelizable]
[TestOf(typeof(NCMetaNetworkSystem))]
[TestOf(typeof(NCMetaNetrunSystem))]
public sealed partial class NCMetaNativeNetworkTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = false };
    [SidedDependency(Side.Server)] private NCMetaDemoSceneSystem _scenes = default!;
    [SidedDependency(Side.Server)] private NCMetaDeviceRegistrySystem _devices = default!;
    [SidedDependency(Side.Server)] private NCMetaNetworkSystem _network = default!;
    [SidedDependency(Side.Server)] private NCMetaNetrunSystem _net = default!;
    [SidedDependency(Side.Server)] private NCMetaRuntimeSystem _runtime = default!;
    [SidedDependency(Side.Server)] private NCMetaPersistenceSystem _persistence = default!;
    [SidedDependency(Side.Server)] private NCMetaAuthoritySystem _authority = default!;

    private sealed record Scene(NCMetaDemoSceneInstance Physical, EntityUid Actor, string Owner,
        EntityUid NetworkActor, string NetworkOwner, EntityUid Deck);
    private NCMetaNativeNetworkProgramHarness Programs => new(Server);
    private NCMetaNativePacketObserverSystem Observer => Server.System<NCMetaNativePacketObserverSystem>();

    private async Task Until(Func<bool> condition, int maximumTicks = 600, string reason = "Native condition did not complete.")
    {
        var ready = false;
        for (var tick = 0; tick < maximumTicks; tick++)
        {
            await Server.WaitPost(() => ready = condition());
            if (ready) return;
            await Server.WaitRunTicks(1);
        }
        Assert.Fail(reason);
    }

    private void MoveTo(EntityUid actor, EntityUid target) => Server.System<SharedTransformSystem>().SetCoordinates(actor,
        SComp<TransformComponent>(target).Coordinates.Offset(new Vector2(0, -0.35f)));

    private async Task<string> CreateOwner(EntityUid body, string name)
    {
        var actor = new NCActorId(Guid.NewGuid());
        HumanoidCharacterProfile profile = null!;
        await Server.WaitAssertion(() =>
        {
            profile = HumanoidCharacterProfile.Random();
        });
        var payload = NCHumanoidProfilePayload.Serialize(Server.ResolveDependency<ISerializationManager>(), profile);
        var request = Guid.NewGuid();
        var created = await Server.System<NCCityActorRegistrySystem>().CreateResidentAsync(new(request, actor, name,
            new("NCLivingCityResident"), new("NCDefaultResidentPersona"), payload, default, null, request,
            AuditSource: "integration.meta-native-network"));
        Assert.That(created.Status, Is.EqualTo(NCCityActorMutationStatus.Applied), created.Error);
        await NCMetaNativeActorHarness.RestoreBinding(Server, actor, body);
        return NCPartyRef.ForActor(actor).ToString();
    }

    private async Task Claim(EntityUid endpoint, EntityUid actor, string owner)
    {
        await Until(() => _devices.TryGetEntry(endpoint, out var device) && device.Ready && _devices.IsPowered(endpoint),
            reason: "Real endpoint did not finish persistence restore/power-up.");
        await Server.WaitAssertion(() => { MoveTo(actor, endpoint); Assert.That(_devices.TryClaim(endpoint, actor), Is.True); });
        await Until(() => _devices.TryGetEntry(endpoint, out var device) && device.Owner == owner && device.AuthorityEpoch >= 2,
            reason: "Native durable device claim did not commit.");
    }

    private async Task<Scene> CreateScene(bool net = false, bool foreignNetwork = false, bool neural = false)
    {
        await Server.WaitPost(() => SEntMan.EventBus.RaiseEvent(EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await Until(() => Server.System<NCActiveWorldTimeSystem>().IsReady);
        NCMetaDemoSceneInstance physical = null!;
        await Server.WaitAssertion(() => Assert.That(_scenes.TryCreate("NCMetaNetScene", out physical), Is.True));
        var actor = physical.Operator;
        var deck = physical.Objects["deck"];
        EntityUid networkActor = actor;
        if (!net) await Server.WaitPost(() =>
        {
            foreach (var key in new[] { "deck", "firewall", "blackIce", "door" }) SEntMan.DeleteEntity(physical.Objects[key]);
        });
        if (foreignNetwork) await Server.WaitPost(() =>
            networkActor = SEntMan.SpawnEntity("MobHuman", SComp<TransformComponent>(actor).Coordinates));
        if (neural) await Server.WaitPost(() =>
        {
            var coordinates = SComp<TransformComponent>(deck).Coordinates;
            SEntMan.DeleteEntity(deck); deck = SEntMan.SpawnEntity("NCMetaNeuralCyberdeck", coordinates);
        });
        var owner = await CreateOwner(actor, "Native network operator");
        var networkOwner = foreignNetwork ? await CreateOwner(networkActor, "Native network owner") : owner;
        foreach (var (key, endpoint) in physical.Objects)
        {
            if ((!net && key is "deck" or "firewall" or "blackIce" or "door") || (neural && key == "deck")) continue;
            if (key == "deck") await Claim(endpoint, actor, owner);
            else await Claim(endpoint, networkActor, networkOwner);
        }
        if (neural) await Claim(deck, actor, owner);
        await Server.WaitPost(() => Observer.Clear());
        return new(physical, actor, owner, networkActor, networkOwner, deck);
    }

    private async Task Destroy(Scene scene)
    {
        await NCMetaNativeTestTasks.CloseWorld(Server);
        // Capture before map deletion tears down the actor's authoritative medical graph.
        await NCMetaNativeActorHarness.UnbindBeforeDelete(Server, scene.Actor, scene.NetworkActor);
        await Server.WaitPost(() => { _scenes.TryDestroy(scene.Physical.Id); Observer.Clear(); });
        await Server.WaitRunTicks(2);
    }

    private async Task RestoreSceneActorBindings(Scene scene)
    {
        Assert.That(NCPartyRef.TryParse(scene.Owner, out var owner) && owner.TryGetActor(out _), Is.True);
        owner.TryGetActor(out var actor);
        await NCMetaNativeActorHarness.RestoreBinding(Server, actor, scene.Actor);
        if (scene.NetworkActor == scene.Actor) return;
        Assert.That(NCPartyRef.TryParse(scene.NetworkOwner, out var networkOwner) && networkOwner.TryGetActor(out _), Is.True);
        networkOwner.TryGetActor(out var networkActor);
        await NCMetaNativeActorHarness.RestoreBinding(Server, networkActor, scene.NetworkActor);
    }

    private async Task<NCMetaNativeNetworkProgramHarness.Installed> Program(Scene scene, string key, string source,
        string module = "native.transport", string? ownerPackage = null)
    {
        var host = scene.Physical.Objects[key];
        var actor = scene.NetworkActor;
        await Server.WaitPost(() => MoveTo(actor, host));
        return await Programs.InstallAndStartAsync(host, actor, module, ownerPackage ?? module, source,
            new Dictionary<string, EntityUid> { ["port"] = host });
    }

    private string Address(EntityUid endpoint) => SComp<NCMetaMessagePortComponent>(endpoint).Address;
    private string[] Logs(Scene scene, string key, Guid installation)
    { var host = scene.Physical.Objects[key]; MoveTo(scene.NetworkActor, host); return _runtime.GetLogs(host, scene.NetworkActor, installation); }
    private const string Receiver = """
        module native.transport;
        import meta.system; import meta.net;
        link port:net.MessagePort="port";
        var last:option<net.Message> = none;
        pub fn report()->string { match(last){some(message)=>{return message.payload;} none=>{return "";}} }
        watch port.messageReceived(message){last=some(message);system.log(message.replyTo+"|"+message.topic+"|"+message.payload);}
        """;
    private static string Send(string address, string body = "payload", string tail = "") => $$$"""
        module native.transport;
        import meta.system; import meta.net;
        link port:net.MessagePort="port";
        async fn main()->void {
            match(await port.send("{{{address}}}","safe","{{{body}}}")){ok(_)=>{system.log("accepted");} err(error)=>{system.log(error.message);}}
            {{{tail}}}
        }
        """;

    private async Task Connect(Scene scene)
    {
        await Server.WaitPost(() => MoveTo(scene.NetworkActor, scene.Physical.Objects["host"]));
        await Server.WaitAssertion(() => Assert.That(_net.TrySynchronizeSecurityPolicy(scene.Physical.Objects["host"], scene.NetworkActor), Is.True));
        await Until(() =>
        {
            MoveTo(scene.Actor, scene.Deck);
            if (_net.TryGetState(scene.Deck, scene.Actor, out var current) && current.SessionId != Guid.Empty) return true;
            return _net.TryConnect(scene.Deck, scene.Actor, scene.Physical.Objects["host"]) &&
                _net.TryGetState(scene.Deck, scene.Actor, out var state) && state.SessionId != Guid.Empty;
        }, maximumTicks: 900, reason: "Native NET did not authenticate and attach to its persisted physical architecture.");
    }

    private async Task<NCMetaNetState> ReadState(Scene scene)
    {
        NCMetaNetState state = null!;
        await Server.WaitAssertion(() => { MoveTo(scene.Actor, scene.Deck); Assert.That(_net.TryGetState(scene.Deck, scene.Actor, out state), Is.True); });
        return state;
    }

    private async Task<NCMetaNetState> Action(Scene scene, NCMetaNetOperation operation, string node = "")
    {
        var admitted = false;
        for (var attempt = 0; attempt < 60 && !admitted; attempt++)
        {
            await Server.WaitPost(() =>
            {
                MoveTo(scene.Actor, scene.Deck);
                if (_net.TryGetState(scene.Deck, scene.Actor, out var state) && !state.ActionPending)
                    admitted = _net.TryExecuteAction(scene.Deck, scene.Actor, operation, state.SessionId, state.Revision, node);
            });
            if (!admitted) await Server.WaitRunTicks(12);
        }
        Assert.That(admitted, Is.True, "Native NET action never passed its real cooldown/budget/policy gate.");
        await Until(() => _net.TryGetState(scene.Deck, scene.Actor, out var state) && !state.ActionPending,
            maximumTicks: 900, reason: "Admitted native NET action never completed its audit/action boundary.");
        return await ReadState(scene);
    }
}
