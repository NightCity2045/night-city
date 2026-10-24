// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Content.Meta.Runtime;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.Meta.Network;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.Power.EntitySystems;

namespace Content.IntegrationTests.Tests._NC.Meta;

public sealed partial class NCMetaNativeNetworkTest
{
    [Test]
    public async Task APhysicalNetSessionDiscoversOnlyItsKnownGraphAndRejectsForgedSessionRevisionAndNodeIds()
    {
        var scene = await CreateScene(net: true);
        try
        {
            await Connect(scene);
            var attached = await ReadState(scene);
            Assert.That(attached.Nodes.Select(node => node.Id), Is.EqualTo(new[] { "entry" }));
            await Server.WaitAssertion(() =>
            {
                Assert.That(_net.TryExecuteAction(scene.Deck, scene.Actor, NCMetaNetOperation.Probe,
                    Guid.NewGuid(), attached.Revision, "entry"), Is.False);
                Assert.That(_net.TryExecuteAction(scene.Deck, scene.Actor, NCMetaNetOperation.Probe,
                    attached.SessionId, attached.Revision - 1, "entry"), Is.False);
                Assert.That(_net.TryExecuteAction(scene.Deck, scene.Actor, NCMetaNetOperation.Probe,
                    attached.SessionId, attached.Revision, "black-ice"), Is.False, "A hidden node name is not a discovery capability.");
            });
            var scanned = await Action(scene, NCMetaNetOperation.Scan);
            Assert.That(scanned.Nodes.Select(node => node.Id), Is.EquivalentTo(new[] { "entry", "data", "control", "firewall", "black-ice" }));
            Assert.That(scanned.Nodes.All(node => !node.Probed && !node.Accessible), Is.True);
            await Server.WaitAssertion(() =>
            {
                _net.ResumeSessions();
                Assert.That(_net.TryGetState(scene.Deck, scene.Actor, out var resumed), Is.True);
                Assert.That(resumed.SessionId, Is.EqualTo(scanned.SessionId), "Same-epoch maintenance resume must preserve authenticated cache/session state.");
                Assert.That(_net.TryExecuteAction(scene.Deck, scene.Actor, NCMetaNetOperation.Scan,
                    attached.SessionId, attached.Revision), Is.False, "The previous valid revision must not replay.");
                Assert.That(_net.TryDisconnect(scene.Deck, scene.Actor), Is.True);
                Assert.That(_net.TryExecuteAction(scene.Deck, scene.Actor, NCMetaNetOperation.Probe,
                    scanned.SessionId, scanned.Revision, "data"), Is.False);
            });
            Assert.That((await ReadState(scene)).SessionId, Is.EqualTo(Guid.Empty));
        }
        finally { await Destroy(scene); }
    }

    [Test]
    public async Task NativePendingActionAndToolCompatibilityRejectBeforeChargingTheNextBudgetUnit()
    {
        var scene = await CreateScene(net: true);
        try
        {
            await Connect(scene);
            await Action(scene, NCMetaNetOperation.Scan);
            await Server.WaitRunTicks(200);
            await Server.WaitAssertion(() =>
            {
                Assert.That(_net.TryGetState(scene.Deck, scene.Actor, out var before), Is.True);
                Assert.That(_net.TryExecuteAction(scene.Deck, scene.Actor, NCMetaNetOperation.Probe,
                    before.SessionId, before.Revision, "data"), Is.True);
                Assert.That(_net.TryGetState(scene.Deck, scene.Actor, out var pending), Is.True);
                Assert.That(pending.ActionPending, Is.True);
                Assert.That(pending.Budget, Is.EqualTo(before.Budget - 1));
                Assert.That(_net.TryExecuteAction(scene.Deck, scene.Actor, NCMetaNetOperation.Probe,
                    pending.SessionId, pending.Revision, "control"), Is.False);
                Assert.That(_net.TryGetState(scene.Deck, scene.Actor, out var rejected), Is.True);
                Assert.That(rejected.Budget, Is.EqualTo(pending.Budget));
            });
            await Until(() => _net.TryGetState(scene.Deck, scene.Actor, out var state) && !state.ActionPending);
            await Server.WaitRunTicks(200);
            await Server.WaitAssertion(() =>
            {
                SComp<NCMetaCyberdeckComponent>(scene.Deck).ToolProfile = "NCMetaNetDefenseTool";
                Assert.That(_net.TryGetState(scene.Deck, scene.Actor, out var before), Is.True);
                Assert.That(_net.TryExecuteAction(scene.Deck, scene.Actor, NCMetaNetOperation.Probe,
                    before.SessionId, before.Revision, "data"), Is.False, "A real defense-only profile cannot probe data.");
                Assert.That(_net.TryGetState(scene.Deck, scene.Actor, out var rejected), Is.True);
                Assert.That(rejected.Budget, Is.EqualTo(before.Budget));
                Assert.That(rejected.ActionPending, Is.False);
            });
        }
        finally { await Destroy(scene); }
    }

    [Test]
    public async Task ControlRequiresItsSeparateNativeLeaseAndPreservesTheRealDoorBoltInterlock()
    {
        var scene = await CreateScene(net: true);
        var door = scene.Physical.Objects["door"];
        try
        {
            await Connect(scene); // Native control configuration extends an existing authenticated architecture policy.
            await Server.WaitAssertion(() =>
            {
                MoveTo(scene.Actor, scene.Physical.Objects["control"]);
                Assert.That(_net.TryBindControlNode(scene.Physical.Objects["control"], door, scene.Actor), Is.True);
            });
            await Until(() => _devices.TryGetEntry(door, out var target) &&
                SComp<NCMetaNetNodeComponent>(scene.Physical.Objects["control"]).ControlledDeviceId == target.Id);
            await Connect(scene);
            await Action(scene, NCMetaNetOperation.Scan);
            var probed = await Action(scene, NCMetaNetOperation.Probe, "control");
            Assert.That(probed.Nodes.Single(node => node.Id == "control").Accessible, Is.False);
            await Server.WaitAssertion(() =>
            {
                Assert.That(_net.TryExecuteAction(scene.Deck, scene.Actor, NCMetaNetOperation.Activate,
                    probed.SessionId, probed.Revision, "control"), Is.False);
                Assert.That(SComp<DoorComponent>(door).State, Is.EqualTo(DoorState.Closed));
            });
            var granted = await Action(scene, NCMetaNetOperation.AcquireControl, "control");
            Assert.That(granted.Nodes.Single(node => node.Id == "control").Accessible, Is.True);
            await Server.WaitPost(() => Server.System<SharedDoorSystem>().SetBoltsDown((door, SComp<DoorBoltComponent>(door)), true));
            var blocked = await Action(scene, NCMetaNetOperation.Activate, "control");
            Assert.That(blocked.StatusKey, Is.EqualTo("nc-meta-net-control-rejected"));
            await Server.WaitAssertion(() =>
            {
                Assert.That(SComp<DoorComponent>(door).State, Is.EqualTo(DoorState.Closed));
                Assert.That(SComp<DoorBoltComponent>(door).BoltsDown, Is.True);
                Server.System<SharedDoorSystem>().SetBoltsDown((door, SComp<DoorBoltComponent>(door)), false);
            });
            var applied = await Action(scene, NCMetaNetOperation.Activate, "control");
            Assert.That(applied.StatusKey, Is.EqualTo("nc-meta-net-control-applied"));
            await Until(() => SComp<DoorComponent>(door).State == DoorState.Open);
            await Server.WaitAssertion(() => Assert.That(_devices.TryInvalidate(door), Is.True));
            var revoked = await ReadState(scene);
            Assert.That(revoked.Nodes.Single(node => node.Id == "control").Accessible, Is.False);
        }
        finally { await Destroy(scene); }
    }

    [TestCase("Detach")]
    [TestCase("Generation")]
    [TestCase("Power")]
    public async Task ADetachedOrReplacedNativeReadCannotCommitCopiedPrivateBytes(string transition)
    {
        var scene = await CreateScene(net: true);
        try
        {
            string sourceId = "", deckId = "";
            Content.Shared._NC.Persistence.Time.NCSessionEpoch epoch = default;
            Content.Shared._NC.Persistence.Time.NCActiveWorldTime time = default;
            await Server.WaitAssertion(() =>
            {
                Assert.That(_devices.TryGetEntry(scene.Physical.Objects["data"], out var source), Is.True); sourceId = source.Id;
                Assert.That(_devices.TryGetEntry(scene.Deck, out var deck), Is.True); deckId = deck.Id;
                Assert.That(_persistence.TryCaptureTime(out epoch, out time), Is.True);
            });
            Assert.That((await _persistence.WriteTextAsync(Guid.NewGuid(), NCMetaPersistenceSystem.StableGuid("volume:" + sourceId),
                scene.Owner, "public/status.txt", "bounded confidential source", 4 * 1024 * 1024, epoch, time)).Success, Is.True);
            await Connect(scene);
            await Action(scene, NCMetaNetOperation.Scan);
            await Action(scene, NCMetaNetOperation.Probe, "data");
            await Server.WaitRunTicks(200);
            await Server.WaitAssertion(() =>
            {
                Assert.That(_net.TryGetState(scene.Deck, scene.Actor, out var state), Is.True);
                Assert.That(_net.TryExecuteAction(scene.Deck, scene.Actor, NCMetaNetOperation.Read,
                    state.SessionId, state.Revision, "data"), Is.True);
                if (transition == "Detach") Assert.That(_net.TryDisconnect(scene.Deck, scene.Actor), Is.True);
                else if (transition == "Generation") Assert.That(_devices.TryInvalidate(scene.Physical.Objects["data"]), Is.True);
                else Server.System<SharedBatterySystem>().SetCharge(scene.Physical.PowerSupply, 0);
            });
            await Server.WaitRunTicks(360);
            var suffix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("meta-demo:data")))[..24];
            Assert.That(await _persistence.ReadTextAsync(NCMetaPersistenceSystem.StableGuid("volume:" + deckId), scene.Owner,
                "net-copies/" + suffix + ".txt"), Is.Null);
            Assert.That((await ReadState(scene)).Preview, Is.Empty);
        }
        finally { await Destroy(scene); }
    }
}
