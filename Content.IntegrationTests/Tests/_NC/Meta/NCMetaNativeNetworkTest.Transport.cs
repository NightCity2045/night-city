// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Meta.Runtime;
using Content.Server.DeviceNetwork.Systems;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.Network;
using Content.Shared.DeviceNetwork.Components;
using Content.Shared.Power.EntitySystems;
using Content.Shared.PowerCell;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._NC.Meta;

public sealed partial class NCMetaNativeNetworkTest
{
    [Test]
    public async Task ARealOneShotSendStillDeliversAfterCleanMainCompletionWithAuthenticatedReplyAddress()
    {
        var scene = await CreateScene();
        try
        {
            var receiver = await Program(scene, "data", Receiver);
            string destination = "", senderAddress = "";
            await Server.WaitPost(() =>
            {
                destination = Address(scene.Physical.Objects["data"]); senderAddress = Address(scene.Physical.Objects["host"]);
                SComp<NCMetaMessagePortComponent>(scene.Physical.Objects["host"]).LatencyMilliseconds = 500;
                Observer.Observe(scene.Physical.Objects["data"]);
            });
            var sender = await Program(scene, "host", Send(destination, "claimed-sender:someone-else"));
            await Until(() => sender.Runtime.Process.State == MetaProcessState.Stopped);
            await Server.WaitAssertion(() =>
            {
                Assert.That(sender.Runtime.Process.StopReason, Is.EqualTo(MetaStopReason.Completed));
                Assert.That(Logs(scene, "data", receiver.Installation.Id), Is.Empty, "Native latency must separate completion from physical delivery.");
            });
            await Until(() => Logs(scene, "data", receiver.Installation.Id).Length == 1);
            await Server.WaitAssertion(() =>
            {
                Assert.That(Logs(scene, "data", receiver.Installation.Id), Is.EqualTo(new[] { senderAddress + "|safe|claimed-sender:someone-else" }));
                Assert.That(Observer.Packets, Has.Count.EqualTo(1));
                Assert.That(Observer.Packets[0].Sender, Is.EqualTo(scene.Physical.Objects["host"]));
            });
        }
        finally { await Destroy(scene); }
    }

    [Test]
    public async Task BroadcastAllocatesEachRecipientDtoAndStoppingOneReceiverCannotClearTheOtherHeap()
    {
        var scene = await CreateScene();
        try
        {
            var first = await Program(scene, "data", Receiver);
            var second = await Program(scene, "control", Receiver);
            var copies = 0;
            void Sent(EntityUid sender, int bytes) { if (sender == scene.Physical.Objects["host"]) copies++; }
            await Server.WaitPost(() =>
            {
                var port = SComp<NCMetaMessagePortComponent>(scene.Physical.Objects["host"]);
                port.AllowBroadcast = true; port.MaximumRecipients = 2;
                _network.TransmissionAdmitted += Sent;
            });
            try
            {
                await Program(scene, "host", Send("*", "independent"));
                await Until(() => Logs(scene, "data", first.Installation.Id).Length == 1 && Logs(scene, "control", second.Installation.Id).Length == 1);
                await Server.WaitAssertion(() => { Assert.That(copies, Is.EqualTo(2)); _runtime.StopHost(scene.Physical.Objects["data"]); });
                var reportId = Enumerable.Range(0, second.Artifact.FunctionCount).Single(id => second.Artifact.GetFunction(id).Name == "native.transport.report");
                string? result = null;
                await Server.WaitAssertion(() =>
                {
                    MoveTo(scene.NetworkActor, scene.Physical.Objects["control"]);
                    Assert.That(_runtime.TryInvoke(scene.Physical.Objects["control"], scene.NetworkActor, reportId,
                        _ => [], (process, value) => result = value is { } actual ? process.GetString(actual) : null), Is.True);
                });
                await Until(() => result != null);
                Assert.That(result, Is.EqualTo("independent"));
                Assert.That(first.Runtime.Process.State, Is.EqualTo(MetaProcessState.Stopped));
                Assert.That(second.Runtime.Process.Fault, Is.Null);
            }
            finally { await Server.WaitPost(() => _network.TransmissionAdmitted -= Sent); }
        }
        finally { await Destroy(scene); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AFullRecipientRejectsTheWholeBroadcastBeforeAnyNativeFanout(bool byteLimit)
    {
        var scene = await CreateScene();
        try
        {
            var first = await Program(scene, "data", Receiver);
            var second = await Program(scene, "control", Receiver);
            string destination = "";
            await Server.WaitPost(() =>
            {
                destination = Address(scene.Physical.Objects["data"]);
                var source = SComp<NCMetaMessagePortComponent>(scene.Physical.Objects["host"]);
                source.AllowBroadcast = true; source.MaximumRecipients = 2; source.LatencyMilliseconds = 500;
                var target = SComp<NCMetaMessagePortComponent>(scene.Physical.Objects["data"]);
                if (byteLimit) target.QueueBytes = 129; else target.QueueMessages = 1;
                Observer.Observe(scene.Physical.Objects["data"]); Observer.Observe(scene.Physical.Objects["control"]);
            });
            var sourceText = Send(destination, "x", "match(await port.send(\"*\",\"safe\",\"x\")){ok(_)=>{system.log(\"wrong-broadcast\");}err(problem)=>{system.log(\"broadcast-rejected\");}}");
            var sender = await Program(scene, "host", sourceText);
            await Until(() => sender.Runtime.Process.State == MetaProcessState.Stopped);
            await Server.WaitRunTicks(90);
            await Server.WaitAssertion(() =>
            {
                Assert.That(Logs(scene, "host", sender.Installation.Id), Contains.Item("broadcast-rejected"));
                Assert.That(Logs(scene, "data", first.Installation.Id), Has.Length.EqualTo(1));
                Assert.That(Logs(scene, "control", second.Installation.Id), Is.Empty);
                Assert.That(Observer.Packets, Has.Count.EqualTo(1));
            });
        }
        finally { await Destroy(scene); }
    }

    [Test]
    public async Task SeventeenActuallyRoutedRecipientsExceedTheFanoutLimitBeforeTheNativeQueue()
    {
        var scene = await CreateScene();
        try
        {
            var previous = scene.Physical.Objects["control"];
            var extra = new List<EntityUid>();
            for (var index = 0; index < 15; index++)
            {
                EntityUid endpoint = default;
                var column = index / 5 % 2 == 0 ? index % 5 : 4 - index % 5;
                await Server.WaitPost(() => endpoint = SEntMan.SpawnEntity("NCMetaNetworkPort",
                    new EntityCoordinates(scene.Physical.Grid, new Vector2(-0.5f + column, -0.5f - index / 5))));
                await Claim(endpoint, scene.Actor, scene.Owner);
                var from = previous;
                await Server.WaitAssertion(() =>
                {
                    var position = (SComp<TransformComponent>(from).Coordinates.Position + SComp<TransformComponent>(endpoint).Coordinates.Position) / 2;
                    Server.System<SharedTransformSystem>().SetCoordinates(scene.Actor, new EntityCoordinates(scene.Physical.Grid, position));
                    var wire = SEntMan.SpawnEntity("NCMetaNetworkCable", new EntityCoordinates(scene.Physical.Grid, position));
                    Assert.That(_network.TryConnectCable(wire, scene.Actor, from), Is.True);
                    Assert.That(_network.TryConnectCable(wire, scene.Actor, endpoint), Is.True);
                    Observer.Observe(endpoint);
                });
                extra.Add(endpoint); previous = endpoint;
            }
            await Server.WaitAssertion(() =>
            {
                foreach (var recipient in extra) Assert.That(_network.CanRoute(scene.Physical.Objects["host"], recipient), Is.True);
                var port = SComp<NCMetaMessagePortComponent>(scene.Physical.Objects["host"]);
                port.AllowBroadcast = true; port.MaximumRecipients = 16;
                Observer.Observe(scene.Physical.Objects["data"]); Observer.Observe(scene.Physical.Objects["control"]);
            });
            var sender = await Program(scene, "host", Send("*"));
            await Until(() => sender.Runtime.Process.State == MetaProcessState.Stopped);
            await Server.WaitRunTicks(90);
            await Server.WaitAssertion(() =>
            {
                Assert.That(Logs(scene, "host", sender.Installation.Id), Has.None.EqualTo("accepted"));
                Assert.That(Observer.Packets, Is.Empty);
            });
        }
        finally { await Destroy(scene); }
    }
}
