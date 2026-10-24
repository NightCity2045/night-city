// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Tests._NC.Medical;
using Content.Meta.Runtime;
using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Medical.Physiology;
using Content.Server._NC.Medical.Systemic;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Systemic;
using Content.Shared._NC.Meta.Network;
using Content.Shared.Body;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Power.EntitySystems;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._NC.Meta;

public sealed partial class NCMetaNativeNetworkTest
{
    [TestCase("Linked")]
    [TestCase("OrdinaryDeck")]
    [TestCase("Disconnected")]
    [TestCase("PowerLost")]
    public async Task BlackIceKeepsRunningDuringGuestPauseAndOnlyAHardwareLinkedBodyReceivesNativeElectricalInjury(string connection)
    {
        var scene = await CreateScene(net: true, neural: connection != "OrdinaryDeck");
        try
        {
            EntityUid implant = default, bodyCell = default;
            var cyberware = Server.System<NCCyberwareSystem>();
            var batteries = Server.System<SharedBatterySystem>();
            await Server.WaitAssertion(() =>
            {
                bodyCell = NCCyberwareTestPower.Install(SEntMan, cyberware, Server.System<ItemSlotsSystem>(), batteries, scene.Actor, 1000);
                implant = SEntMan.SpawnEntity("NCSandevistanImplant", SComp<TransformComponent>(scene.Actor).Coordinates);
                var head = SComp<BodyComponent>(scene.Actor).Organs!.ContainedEntities.Single(uid => SComp<OrganComponent>(uid).Category?.Id == "Head");
                Assert.That(cyberware.TryInstall(new(NCActionResolutionId.New(), scene.Actor, head, implant, "NCSlotMotorBurst", true, true)).Accepted, Is.True);
                Assert.That(cyberware.TryCalibrateCyberware(implant, NCActionResolutionId.New(), out _), Is.True);
                Assert.That(cyberware.TrySetNeuralInterface(implant, NCActionResolutionId.New(), true, true).Accepted, Is.True);
            });
            await Until(() => SComp<NCCyberwareComponent>(implant).Operational && SComp<NCCyberwareComponent>(implant).PowerGranted &&
                SComp<NCCyberwareComponent>(implant).NeuralState is NCCyberwareNeuralState.Linked or NCCyberwareNeuralState.Degraded,
                reason: "An actually installed, calibrated and powered neural implant never linked.");
            if (connection == "Disconnected") await Server.WaitAssertion(() =>
                Assert.That(cyberware.TrySetNeuralInterface(implant, NCActionResolutionId.New(), false, false).Accepted, Is.True));
            if (connection == "PowerLost")
            {
                await Server.WaitPost(() => batteries.SetCharge(bodyCell, 0));
                await Until(() => !SComp<NCCyberwareComponent>(implant).PowerGranted);
            }
            // Prepare the real helper before enabling a destructive native threat. Compiler/DB wall time
            // must not decide whether the physical deck survives to the assertions.
            await Server.WaitPost(() => MoveTo(scene.Actor, scene.Deck));
            var helper = await Programs.InstallAndStartAsync(scene.Deck, scene.Actor, "native.icehelper", "native.icehelper", """
                module native.icehelper; import meta.system;
                async fn main()->void { await system.sleepMs(60000); }
                """, new Dictionary<string, EntityUid>());
            await Until(() => helper.Runtime.Process.State == MetaProcessState.Waiting);
            await Server.WaitAssertion(() =>
            {
                Assert.That(_persistence.TryCaptureTime(out _, out var now), Is.True);
                Assert.That(_runtime.TryDebug(scene.Deck, scene.Actor, process => process.TryPause(now.Ticks / TimeSpan.TicksPerMillisecond)), Is.True);
                Assert.That(helper.Runtime.Process.DebugPaused, Is.True);
            });
            await Connect(scene);
            await Action(scene, NCMetaNetOperation.Scan);
            for (var probe = 0; probe < 3; probe++) await Action(scene, NCMetaNetOperation.Probe, "black-ice");
            var before = await ReadState(scene);
            Assert.That(before.Trace, Is.GreaterThanOrEqualTo(3));
            await Until(() => _net.TryGetState(scene.Deck, scene.Actor, out var state) && state.Trace > before.Trace,
                maximumTicks: 150, reason: "Native Black ICE did not advance while the helper VM was paused.");
            var after = await ReadState(scene);
            await Server.WaitAssertion(() =>
            {
                Assert.That(helper.Runtime.Process.DebugPaused, Is.True);
                Assert.That(after.Trace, Is.GreaterThan(before.Trace));
                Assert.That(Server.System<DamageableSystem>().GetTotalDamage(scene.Deck), Is.GreaterThan(FixedPoint2.Zero), "Physical deck damage must come from native Black ICE.");
                var systemic = Server.System<NCSystemicSystem>();
                var electrical = systemic.GetBurden(scene.Actor, NCSystemicChannel.Electrical);
                Assert.That(Server.System<DamageableSystem>().GetTotalDamage(scene.Actor), Is.EqualTo(FixedPoint2.Zero), "NC electrical injury is not legacy global HP damage.");
                if (connection == "Linked")
                {
                    Assert.That(electrical, Is.GreaterThan(0));
                    Assert.That(Server.System<NCPhysiologySchedulerSystem>().ProcessNow(scene.Actor, TimeSpan.Zero), Is.True);
                    var physiology = SComp<NCPhysiologyComponent>(scene.Actor);
                    Assert.That(physiology.BrainFunction < 1f || physiology.Shock > 0f, Is.True,
                        "A native electrical payload must feed the real systemic/brain/shock medical pipeline.");
                }
                else Assert.That(electrical, Is.Zero, "A deck interface, live neural link and actual body power are all required.");
                _runtime.StopHost(scene.Deck);
            });
            var stopped = await ReadState(scene);
            await Until(() => _net.TryGetState(scene.Deck, scene.Actor, out var state) && state.Trace > stopped.Trace,
                maximumTicks: 150, reason: "Stopping helper software incorrectly stopped the native threat.");
            Assert.That((await ReadState(scene)).Trace, Is.GreaterThan(stopped.Trace), "Stopping helper software is not an emergency physical disconnect.");
            await Server.WaitAssertion(() => Assert.That(_net.TryDisconnect(scene.Deck, scene.Actor), Is.True));
            Assert.That((await ReadState(scene)).SessionId, Is.EqualTo(Guid.Empty));
        }
        finally { await Destroy(scene); }
    }
}
