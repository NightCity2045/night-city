// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Meta.Runtime;
using Content.Server.DeviceNetwork.Systems;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.Network;
using Content.Shared.DeviceNetwork.Components;
using Content.Shared.Power.EntitySystems;

namespace Content.IntegrationTests.Tests._NC.Meta;

public sealed partial class NCMetaNativeNetworkTest
{
    [Test]
    public async Task ARealPendingEnvelopeRejectsAForgedNativeSenderAndConsumesItsGenuineDeliveryOnlyOnce()
    {
        var scene = await CreateScene();
        try
        {
            var receiver = await Program(scene, "data", Receiver);
            string address = "";
            await Server.WaitPost(() =>
            {
                address = Address(scene.Physical.Objects["data"]);
                Observer.Observe(scene.Physical.Objects["data"]);
                Observer.SubstituteSenderOnce = scene.Physical.Objects["control"];
            });
            await Program(scene, "host", Send(address));
            await Until(() => Observer.Packets.Count == 1);
            await Server.WaitAssertion(() =>
            {
                Assert.That(Logs(scene, "data", receiver.Installation.Id), Is.Empty);
                var observed = Observer.Packets[0];
                var target = SComp<DeviceNetworkComponent>(scene.Physical.Objects["data"]);
                Assert.That(Server.System<DeviceNetworkSystem>().QueuePacket(scene.Physical.Objects["control"], target.Address,
                    NCMetaNativePacketObserverSystem.ReplayPayload(observed), target.ReceiveFrequency, target.DeviceNetId), Is.True);
            });
            await Server.WaitRunTicks(3);
            await Server.WaitAssertion(() =>
            {
                Assert.That(Logs(scene, "data", receiver.Installation.Id), Is.Empty);
                var target = SComp<DeviceNetworkComponent>(scene.Physical.Objects["data"]);
                Assert.That(Server.System<DeviceNetworkSystem>().QueuePacket(scene.Physical.Objects["host"], target.Address,
                    NCMetaNativePacketObserverSystem.ReplayPayload(Observer.Packets[0]), target.ReceiveFrequency, target.DeviceNetId), Is.True);
            });
            await Until(() => Logs(scene, "data", receiver.Installation.Id).Length == 1);
            await Server.WaitAssertion(() =>
            {
                var target = SComp<DeviceNetworkComponent>(scene.Physical.Objects["data"]);
                Assert.That(Server.System<DeviceNetworkSystem>().QueuePacket(scene.Physical.Objects["host"], target.Address,
                    NCMetaNativePacketObserverSystem.ReplayPayload(Observer.Packets[0]), target.ReceiveFrequency, target.DeviceNetId), Is.True);
            });
            await Server.WaitRunTicks(10);
            await Server.WaitAssertion(() => Assert.That(Logs(scene, "data", receiver.Installation.Id), Has.Length.EqualTo(1)));
        }
        finally { await Destroy(scene); }
    }

    [Test]
    public async Task NativePacketLossExpiresTheEnvelopeAndASeparateFreshSendCanUseTheReleasedQueueSlot()
    {
        var scene = await CreateScene();
        try
        {
            var receiver = await Program(scene, "data", Receiver);
            string address = "";
            await Server.WaitPost(() =>
            {
                address = Address(scene.Physical.Objects["data"]);
                SComp<NCMetaMessagePortComponent>(scene.Physical.Objects["data"]).QueueMessages = 1;
                Observer.Observe(scene.Physical.Objects["data"]); Observer.SetLoss(scene.Physical.Objects["data"], true);
            });
            await Program(scene, "host", Send(address, "lost"));
            await Server.WaitRunTicks(90);
            await Server.WaitAssertion(() =>
            {
                Assert.That(Observer.Dropped, Is.GreaterThan(0));
                Assert.That(Logs(scene, "data", receiver.Installation.Id), Is.Empty);
                Observer.SetLoss(scene.Physical.Objects["data"], false);
            });
            await Program(scene, "host", Send(address, "fresh"));
            await Until(() => Logs(scene, "data", receiver.Installation.Id).Length == 1);
            await Server.WaitAssertion(() => Assert.That(Logs(scene, "data", receiver.Installation.Id)[0], Does.EndWith("|fresh")));
        }
        finally { await Destroy(scene); }
    }

    [TestCase("SenderEpoch")]
    [TestCase("ReceiverEpoch")]
    [TestCase("ManualStop")]
    [TestCase("GrantRevoked")]
    [TestCase("BlockUnblock")]
    [TestCase("DisableEnable")]
    public async Task AQueuedSendCannotOutliveRevocationOrReviveAfterAnAdministrativePolicyAba(string transition)
    {
        var scene = await CreateScene();
        NCMetaNativeNetworkProgramHarness.Installed? sender = null;
        try
        {
            var receiver = await Program(scene, "data", Receiver);
            string address = "";
            await Server.WaitPost(() =>
            {
                address = Address(scene.Physical.Objects["data"]);
                SComp<NCMetaMessagePortComponent>(scene.Physical.Objects["host"]).LatencyMilliseconds = 500;
                Observer.Observe(scene.Physical.Objects["data"]);
            });
            sender = await Program(scene, "host", Send(address, "old", transition == "ManualStop" ? "while(true){yield;}" : ""));
            await Until(() => Logs(scene, "host", sender.Installation.Id).Contains("accepted"));
            if (transition != "ManualStop")
                await Until(() => sender.Runtime.Process.State == MetaProcessState.Stopped);
            if (transition == "GrantRevoked")
            {
                var policy = await _persistence.ReadServiceGrantPolicyAsync(sender.Installation.Id);
                string hostId = "";
                await Server.WaitPost(() => { MoveTo(scene.Actor, scene.Physical.Objects["host"]); _devices.TryGetEntry(scene.Physical.Objects["host"], out var device); hostId = device.Id; });
                var revoked = await NCMetaNativeTestTasks.Run(Server, () => _authority.TryRevokeGrantAsync(scene.Physical.Objects["host"], scene.Actor,
                    scene.Physical.Objects["host"], Guid.NewGuid(), hostId, sender.Installation.Id, Guid.Empty, policy!.Revision));
                Assert.That(revoked.Success, Is.True, revoked.MessageKey);
            }
            else await Server.WaitAssertion(() =>
            {
                var epoch = _runtime.OperationalPolicyEpoch;
                switch (transition)
                {
                    case "SenderEpoch": Assert.That(_devices.TryInvalidate(scene.Physical.Objects["host"]), Is.True); break;
                    case "ReceiverEpoch": Assert.That(_devices.TryInvalidate(scene.Physical.Objects["data"]), Is.True); break;
                    case "ManualStop": _runtime.StopHost(scene.Physical.Objects["host"]); break;
                    case "BlockUnblock":
                        Assert.That(_runtime.TrySetOperationalArtifactBlock(sender.Artifact.ContentHash, true), Is.True);
                        Assert.That(_runtime.TrySetOperationalArtifactBlock(sender.Artifact.ContentHash, false), Is.True);
                        Assert.That(_runtime.OperationalPolicyEpoch, Is.Not.EqualTo(epoch)); break;
                    case "DisableEnable":
                        Server.CfgMan.SetCVar(NCMetaCVars.Enabled, false); Server.CfgMan.SetCVar(NCMetaCVars.Enabled, true);
                        Assert.That(_runtime.OperationalPolicyEpoch, Is.Not.EqualTo(epoch)); break;
                }
            });
            await Server.WaitRunTicks(90);
            await Server.WaitAssertion(() =>
            {
                Assert.That(Observer.Packets, Is.Empty, "Check physical transmission even when the recipient VM was also stopped.");
                Assert.That(Logs(scene, "data", receiver.Installation.Id), Is.Empty);
            });
        }
        finally
        {
            await Server.WaitPost(() =>
            {
                Server.CfgMan.SetCVar(NCMetaCVars.Enabled, true);
                if (sender != null) _runtime.TrySetOperationalArtifactBlock(sender.Artifact.ContentHash, false);
            });
            await Destroy(scene);
        }
    }

    [TestCase("Cable")]
    [TestCase("Endpoint")]
    [TestCase("Power")]
    public async Task ARealPhysicalBreakBeforeTransmissionNeverBecomesAPhantomMessage(string transition)
    {
        var scene = await CreateScene();
        try
        {
            var receiver = await Program(scene, "data", Receiver);
            string address = "";
            await Server.WaitPost(() =>
            {
                address = Address(scene.Physical.Objects["data"]);
                SComp<NCMetaMessagePortComponent>(scene.Physical.Objects["host"]).LatencyMilliseconds = 500;
                Observer.Observe(scene.Physical.Objects["data"]);
            });
            var sender = await Program(scene, "host", Send(address));
            await Until(() => sender.Runtime.Process.State == MetaProcessState.Stopped);
            await Server.WaitAssertion(() =>
            {
                if (transition == "Power") Server.System<SharedBatterySystem>().SetCharge(scene.Physical.PowerSupply, 0);
                else if (transition == "Endpoint") SEntMan.DeleteEntity(scene.Physical.Objects["data"]);
                else
                {
                    var cables = scene.Physical.Cables.Where(uid => SComp<NCMetaNetworkCableComponent>(uid).PortA == scene.Physical.Objects["data"] ||
                        SComp<NCMetaNetworkCableComponent>(uid).PortB == scene.Physical.Objects["data"]).ToArray();
                    Assert.That(cables, Is.Not.Empty);
                    foreach (var cable in cables)
                    {
                        MoveTo(scene.Actor, cable);
                        Assert.That(_network.TryDisconnectCable(cable, scene.Actor), Is.True);
                    }
                }
            });
            await Server.WaitRunTicks(90);
            await Server.WaitAssertion(() => Assert.That(Observer.Packets, Is.Empty));
        }
        finally { await Destroy(scene); }
    }
}
