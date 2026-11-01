// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
#if TOOLS && NC_DIAGNOSTICS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Meta.Runtime;
using Content.Server._NC.Meta.Network;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.Doors;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._NC.Meta;

public sealed partial class NCMetaLaboratoryClinicalTest
{
    private const string WorldEpochProgram = """
        module native.lab; import meta.doors; import meta.system;
        link gate:doors.Door="gate";
        fn main()->void{}
        pub fn clock()->int {
            match(gate.snapshot()){
                ok(state)=>{
                    if(system.isFresh(state.stamp,1000)){return state.stamp.epoch;}
                    return -1;
                }
                err(error)=>{return -2;}
            }
        }
        pub async fn open()->void {
            // Keep the invocation pending. Closing may legitimately drain an already-committing native write.
            var ticks:int=0;
            while(ticks<10000){ticks=ticks+1;yield;}
            match(await gate.setOpen(true)){ok(state)=>{} err(error)=>{}}
        }
        """;

    [Test]
    public async Task DecreasingOpaqueRoundEpochsKeepOneSchedulerGenerationAndRevokeQueuedWork()
    {
        var scene = await CreateScene();
        EntityUid door = default;
        try
        {
            await Server.WaitAssertion(() =>
            {
                var coordinates = SComp<TransformComponent>(scene["host"]).Coordinates;
                door = SSpawnAtPosition("NCMetaDoor", coordinates.Offset(new Vector2(1, 0)));
                var cable = SSpawnAtPosition("NCMetaNetworkCable", coordinates);
                MoveNear(scene.Actor, scene["host"]);
                var network = Server.System<NCMetaNetworkSystem>();
                Assert.That(network.TryConnectCable(cable, scene.Actor, scene["host"]), Is.True);
                Assert.That(network.TryConnectCable(cable, scene.Actor, door), Is.True);
            });
            await Claim(scene, door);
            var targets = new Dictionary<string, EntityUid> { ["gate"] = door };
            var program = await Install(scene, WorldEpochProgram, targets, "native.lab.world-epoch");
            // Claiming the physical door can bump it open. Establish the native closed baseline before queuing work.
            await Server.WaitPost(() => Server.System<SharedDoorSystem>().TryClose(door));
            await Until(() => SComp<DoorComponent>(door).State == DoorState.Closed, "The native door did not close before the epoch test.");

            async Task<long> ReadClock()
            {
                var invocation = await Invoke(scene, program, "clock");
                await Until(() => invocation.Completed, "Native clock stamp invocation did not finish.");
                long value = 0;
                await Server.WaitAssertion(() =>
                {
                    Assert.That(program.Runtime.Process.LastResult, Is.Not.Null);
                    value = program.Runtime.Process.LastResult!.Value.AsInt();
                    Assert.That(value, Is.GreaterThan(0), "The native stamp and the actual VM clock must share their generation.");
                    Assert.That(value, Is.EqualTo(_runtime.SchedulerEpoch));
                });
                return value;
            }

            NCSessionEpoch epoch = default;
            await Server.WaitAssertion(() => Assert.That(Server.System<Content.Server._NC.Meta.Persistence.NCMetaPersistenceSystem>()
                .TryCaptureTime(out epoch, out _), Is.True));
            var startedGeneration = _runtime.SchedulerEpoch;
            var generation = await ReadClock();
            Assert.That(generation, Is.EqualTo(startedGeneration), "Process start and its following Update must share one clock generation.");
            var decreasingUuidObserved = false;
            for (var round = 0; round < 16; round++)
            {
                var old = program;
                var oldEpoch = epoch;
                var staleCompleted = false;
                var open = Enumerable.Range(0, old.Artifact.FunctionCount)
                    .Single(index => old.Artifact.GetFunction(index).Name == "native.lab.open");
                await SelectRealLabCheckpoint(scene, () =>
                {
                    Assert.That(_runtime.TryInvokeAuthorized(scene["host"], old.Runtime.Generation, scene.Actor, open,
                        _ => [], (_, _) => staleCompleted = true, () => _devices.CanManage(scene["host"], scene.Actor)), Is.True);
                });
                await Server.WaitAssertion(() =>
                {
                    Assert.That(Server.System<Content.Server._NC.Meta.Persistence.NCMetaPersistenceSystem>()
                        .TryCaptureTime(out epoch, out _), Is.True);
                    Assert.That(epoch, Is.Not.EqualTo(oldEpoch));
                    Assert.That(old.Runtime.Process.State, Is.EqualTo(MetaProcessState.Stopped));
                    Assert.That(_runtime.TryInvokeAuthorized(scene["host"], old.Runtime.Generation, scene.Actor, open,
                        _ => [], (_, _) => staleCompleted = true, () => true), Is.False);
                });
                program = await Install(scene, WorldEpochProgram, targets, "native.lab.world-epoch");
                var next = await ReadClock();
                Assert.That(next, Is.EqualTo(generation + 1), "Start and Update must enter one common generation per native world.");
                generation = next;
                await Server.WaitRunTicks(2);
                Assert.That(await ReadClock(), Is.EqualTo(generation), "Further updates must retain the same active generation.");
                await Server.WaitAssertion(() =>
                {
                    Assert.That(staleCompleted, Is.False, "Old queued work must not complete in the replacement world.");
                    Assert.That(SComp<DoorComponent>(door).State, Is.EqualTo(DoorState.Closed));
                });
                // Exercise the former bug with real native UUIDs; production never assigns them numeric order.
                if ((BitConverter.ToInt64(epoch.Value.ToByteArray(), 0) & long.MaxValue) >=
                    (BitConverter.ToInt64(oldEpoch.Value.ToByteArray(), 0) & long.MaxValue)) continue;
                decreasingUuidObserved = true;
                break;
            }
            Assert.That(decreasingUuidObserved, Is.True, "The bounded native round sequence must exercise a decreasing former UUID projection.");
        }
        finally { await Finish(scene); }
    }
}
#endif
