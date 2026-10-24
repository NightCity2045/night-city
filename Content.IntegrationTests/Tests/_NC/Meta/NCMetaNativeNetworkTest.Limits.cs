// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Shared._NC.Meta.Network;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._NC.Meta;

public sealed partial class NCMetaNativeNetworkTest
{
    [TestCase("Utf8")]
    [TestCase("Topic")]
    [TestCase("Peer")]
    [TestCase("Broadcast")]
    public async Task NativeUtf8AndEndpointAclLimitsRejectBeforeAnyPhysicalPacket(string limit)
    {
        var scene = await CreateScene();
        try
        {
            var receiver = await Program(scene, "data", Receiver);
            var destination = "";
            await Server.WaitPost(() =>
            {
                destination = Address(scene.Physical.Objects["data"]);
                Observer.Observe(scene.Physical.Objects["data"]);
                Observer.Observe(scene.Physical.Objects["control"]);
                if (limit == "Topic") SComp<NCMetaMessagePortComponent>(scene.Physical.Objects["data"]).AllowedTopics = ["different"];
                if (limit == "Peer") SComp<NCMetaMessagePortComponent>(scene.Physical.Objects["data"]).AllowedPeers = ["local:someone-else"];
            });
            var sender = await Program(scene, "host", Send(limit == "Broadcast" ? "*" : destination,
                limit == "Utf8" ? new string('я', 1025) : "bounded"));
            await Server.WaitRunTicks(90);
            await Server.WaitAssertion(() =>
            {
                Assert.That(Logs(scene, "host", sender.Installation.Id), Has.None.EqualTo("accepted"));
                Assert.That(Logs(scene, "data", receiver.Installation.Id), Is.Empty);
                Assert.That(Observer.Packets, Is.Empty);
            });
        }
        finally { await Destroy(scene); }
    }

    [Test]
    public async Task OnePrincipalCannotMultiplyTheNativeSendBurstByStartingMorePhysicalHosts()
    {
        var scene = await CreateScene();
        var counted = 0;
        void Sent(EntityUid sender, int bytes) { counted++; }
        try
        {
            EntityUid fourth = default;
            await Server.WaitPost(() => fourth = SEntMan.SpawnEntity("NCMetaNetworkPort",
                new EntityCoordinates(scene.Physical.Grid, new Vector2(1.5f, -1.5f))));
            await Claim(fourth, scene.Actor, scene.Owner);
            await Server.WaitAssertion(() =>
            {
                MoveTo(scene.Actor, scene.Physical.Objects["host"]);
                var cable = SEntMan.SpawnEntity("NCMetaNetworkCable", SComp<TransformComponent>(scene.Actor).Coordinates);
                Assert.That(_network.TryConnectCable(cable, scene.Actor, scene.Physical.Objects["host"]), Is.True);
                Assert.That(_network.TryConnectCable(cable, scene.Actor, fourth), Is.True);
                Assert.That(_network.CanRoute(scene.Physical.Objects["host"], fourth), Is.True);
            });
            var ports = new[] { scene.Physical.Objects["host"], scene.Physical.Objects["data"], scene.Physical.Objects["control"], fourth };
            var prepared = new List<NCMetaNativeNetworkProgramHarness.Prepared>();
            for (var index = 0; index < ports.Length; index++)
            {
                var host = ports[index]; var destination = "";
                await Server.WaitPost(() => { MoveTo(scene.Actor, host); destination = Address(ports[(index + 1) % ports.Length]); });
                var source = $$$"""
                    module native.burst; import meta.net; import meta.system;
                    link port:net.MessagePort="port";
                    async fn main()->void {
                        var count:int=0; while(count<8){
                            match(await port.send("{{{destination}}}","safe","x")){ok(_)=>{system.log("sent");}err(problem)=>{system.log("limited");}}
                            count=count+1;
                        }
                    }
                    """;
                prepared.Add(await Programs.PrepareAsync(host, scene.Actor, "native.burst", "native.burst", source,
                    new Dictionary<string, EntityUid> { ["port"] = host }));
            }
            long start = 0, end = 0;
            await Server.WaitAssertion(() =>
            {
                Assert.That(_persistence.TryCaptureTime(out _, out var now), Is.True); start = now.Ticks;
                _network.TransmissionAdmitted += Sent;
            });
            try
            {
                foreach (var program in prepared)
                {
                    await Server.WaitPost(() => MoveTo(scene.Actor, program.Host));
                    await Programs.StartAsync(program);
                }
                await Server.WaitRunTicks(30);
                await Server.WaitAssertion(() =>
                {
                    Assert.That(_persistence.TryCaptureTime(out _, out var now), Is.True); end = now.Ticks;
                    var upperBound = 20 + (int)Math.Ceiling((end - start) / (double)TimeSpan.TicksPerSecond * 10);
                    Assert.That(counted, Is.GreaterThan(0));
                    Assert.That(counted, Is.LessThan(32), "More VMs/ports cannot bypass the shared principal burst.");
                    Assert.That(counted, Is.LessThanOrEqualTo(upperBound));
                });
            }
            finally { await Server.WaitPost(() => _network.TransmissionAdmitted -= Sent); }
        }
        finally { await Destroy(scene); }
    }

    [Test]
    public async Task ReplyToUsesTheInheritedFiniteContextInsteadOfCreatingAnUnboundedPingPongStorm()
    {
        var scene = await CreateScene();
        try
        {
            var destination = "";
            await Server.WaitPost(() =>
            {
                destination = Address(scene.Physical.Objects["data"]);
                Observer.Observe(scene.Physical.Objects["host"]); Observer.Observe(scene.Physical.Objects["data"]);
            });
            const string echo = """
                module native.transport; import meta.net; import meta.system;
                link port:net.MessagePort="port";
                async watch port.messageReceived(message){
                    system.log("received");
                    match(await port.send(message.replyTo,message.topic,message.payload)){ok(_)=>{}err(problem)=>{system.log("bounded");}}
                }
                """;
            var receiver = await Program(scene, "data", echo);
            var source = echo + $$$"""
                async fn main()->void { match(await port.send("{{{destination}}}","safe","x")){ok(_)=>{}err(problem)=>{}} }
                """;
            var sender = await Program(scene, "host", source);
            await Until(() => Logs(scene, "host", sender.Installation.Id).Contains("bounded") ||
                Logs(scene, "data", receiver.Installation.Id).Contains("bounded"));
            var count = 0;
            await Server.WaitAssertion(() =>
            {
                count = Observer.Packets.Count;
                Assert.That(count, Is.InRange(2, 8));
                Assert.That(Observer.SamplesDropped, Is.Zero);
            });
            await Server.WaitRunTicks(360);
            await Server.WaitAssertion(() =>
            {
                Assert.That(Observer.Packets.Count, Is.EqualTo(count));
                Assert.That(sender.Runtime.Process.Fault, Is.Null);
                Assert.That(receiver.Runtime.Process.Fault, Is.Null);
            });
        }
        finally { await Destroy(scene); }
    }
}
