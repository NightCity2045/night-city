// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Tests._NC.Medical;
using Content.Meta.Runtime;
using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Medical.Physiology;
using Content.Server._NC.Medical.Systemic;
using Content.Server._NC.LivingCity.Actors;
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
using Content.Shared.PowerCell;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.LivingCity.Actors;
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
        var lastThreatObservation = "";
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
                fn main()->void { while(true){yield;} }
                """, new Dictionary<string, EntityUid>());
            await Until(() => helper.Runtime.Process.State == MetaProcessState.Runnable);
            await Server.WaitAssertion(() =>
            {
                Assert.That(_persistence.TryCaptureTime(out _, out var now), Is.True);
                Assert.That(_runtime.TryDebug(scene.Deck, scene.Actor, process => process.TryPause(now.Ticks / TimeSpan.TicksPerMillisecond)), Is.True);
                Assert.That(helper.Runtime.Process.DebugPaused, Is.True);
                // Compilation and durable setup advance the real hardware clock. Start this threat
                // test with a normally charged cell, rather than an accidentally exhausted deck.
                Assert.That(Server.System<PowerCellSystem>().TryGetBatteryFromSlot(scene.Deck, out var deckCell), Is.True);
                batteries.SetCharge(deckCell!.Value.AsNullable(), deckCell.Value.Comp.MaxCharge);
            });
            await Connect(scene);
            await Action(scene, NCMetaNetOperation.Scan);
            for (var probe = 0; probe < 3; probe++) await Action(scene, NCMetaNetOperation.Probe, "black-ice");
            var before = await ReadState(scene);
            Assert.That(before.Trace, Is.GreaterThanOrEqualTo(3));
            // Early ICE pulses only raise trace; the native defense profile activates Black ICE at its own threshold.
            await Until(() =>
            {
                if (!_net.TryGetState(scene.Deck, scene.Actor, out var state)) return false;
                var cells = Server.System<PowerCellSystem>();
                Assert.That(state.SessionId, Is.Not.EqualTo(Guid.Empty),
                    $"NET disconnected before Black ICE: deckPower={_devices.IsPowered(scene.Deck)}, " +
                    $"accessPower={_devices.IsPowered(scene.Physical.Objects["host"])}, " +
                    $"route={_network.CanRoute(scene.Deck, scene.Physical.Objects["host"])}, " +
                    $"charge={(cells.TryGetBatteryFromSlot(scene.Deck, out var cell) ? batteries.GetCharge(cell.Value.AsNullable()) : -1)}.");
                return state.Trace > before.Trace && Server.System<DamageableSystem>().GetTotalDamage(scene.Deck) > FixedPoint2.Zero;
            },
                reason: "Native Black ICE did not reach its real damage threshold while the helper VM was paused.");
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
                Assert.That(_net.TryGetState(scene.Deck, scene.Actor, out var beforeStop), Is.True);
                _runtime.StopHost(scene.Deck);
                Assert.That(_net.TryGetState(scene.Deck, scene.Actor, out var immediatelyStopped), Is.True);
                Assert.That(immediatelyStopped.SessionId, Is.EqualTo(beforeStop.SessionId), "Stopping software must leave the physical NET session intact.");
            });
            var stopped = await ReadState(scene);
            var nativeDeathExit = false;
            await Until(() =>
            {
                if (!_net.TryGetState(scene.Deck, scene.Actor, out var state)) return false;
                NCPartyRef.TryParse(scene.Owner, out var owner);
                owner.TryGetActor(out var actorId);
                var knownActor = Server.System<NCCityActorRegistrySystem>().TryGetCached(actorId, out var actor);
                var dead = knownActor && actor!.Snapshot.Lifecycle.Status == NCCityActorStatus.Dead;
                var lifecycle = knownActor ? actor!.Snapshot.Lifecycle.Status.ToString() : "missing";
                var physiology = SComp<NCPhysiologyComponent>(scene.Actor);
                lastThreatObservation = $"After helper stop: session={state.SessionId}, trace={state.Trace}/{stopped.Trace}, " +
                    $"actor={lifecycle}, brain={physiology.BrainFunction}, shock={physiology.Shock}, " +
                    $"deckPower={_devices.IsPowered(scene.Deck)}, route={_network.CanRoute(scene.Deck, scene.Physical.Objects["host"])}, " +
                    $"actorPosition={SComp<TransformComponent>(scene.Actor).Coordinates}, deckPosition={SComp<TransformComponent>(scene.Deck).Coordinates}.";
                nativeDeathExit = connection == "Linked" && dead && state.SessionId == Guid.Empty;
                return state.Trace > stopped.Trace || nativeDeathExit;
            },
                maximumTicks: 150, reason: "Stopping helper software incorrectly stopped the native threat.");
            if (nativeDeathExit) await Server.WaitAssertion(() =>
                Assert.That(_authority.TryGetManagementProof(scene.Actor, scene.Owner, out var proof) && _authority.CanContinueManagement(proof),
                    Is.False, "A fatally injured actor must lose native NET authority even when its deck remains powered."));
            else
            {
                Assert.That((await ReadState(scene)).Trace, Is.GreaterThan(stopped.Trace), "Stopping helper software is not an emergency physical disconnect.");
                await Server.WaitAssertion(() => Assert.That(_net.TryDisconnect(scene.Deck, scene.Actor), Is.True));
            }
            Assert.That((await ReadState(scene)).SessionId, Is.EqualTo(Guid.Empty));
        }
        finally
        {
            TestContext.Out.WriteLine(lastThreatObservation);
            // Remove this test's temporary hardware through surgery before the resident checkpoint.
            // These deliberately spawned modules have no economic inventory admission receipt.
            await Server.WaitAssertion(() =>
            {
                var cyberware = Server.System<NCCyberwareSystem>();
                foreach (var module in cyberware.GetInstalledModuleEntities(scene.Actor, 64).ToArray())
                    Assert.That(cyberware.TryRemove(new(NCActionResolutionId.New(), scene.Actor, module)).Accepted, Is.True);
            });
            await Destroy(scene);
        }
    }
}
