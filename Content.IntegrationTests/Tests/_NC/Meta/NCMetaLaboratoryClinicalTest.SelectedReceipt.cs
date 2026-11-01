// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
#if TOOLS && NC_DIAGNOSTICS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Meta.Language;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Persistence.RoundSnapshot;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.Kitchen.Components;
using Content.Shared.Kitchen.EntitySystems;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;

namespace Content.IntegrationTests.Tests._NC.Meta;

public sealed partial class NCMetaLaboratoryClinicalTest
{
    private const string ReceiptProgram = """
        module native.lab; import meta.lab; import meta.system;
        link grinder:lab.Grinder="grinder"; fn main()->void{}
        pub async fn run()->void {
            match(await grinder.start(lab.GrinderProgram.Grind,"$KEY$")){
                ok(receipt)=>{
                    if(receipt.status==system.OperationStatus.Applied){system.log("retry-applied");}
                    else{system.log("start-receipt");}
                }
                err(error)=>{system.log("start-rejected");}
            }
        }
        pub async fn status()->void {
            match(await grinder.operationStatus("$KEY$")){
                ok(receipt)=>{
                    if(receipt.status==system.OperationStatus.Applied){system.log("receipt-applied");}
                    else{system.log("receipt-other");}
                }
                err(error)=>{system.log("receipt-unavailable");}
            }
        }
        """;

    private async Task<Guid> SelectRealLabCheckpoint(Scene scene, Action beforeClose = null)
    {
        var persistence = Server.System<NCMetaPersistenceSystem>();
        NCSessionEpoch ending = default;
        await Server.WaitAssertion(() => Assert.That(persistence.TryCaptureTime(out ending, out _), Is.True));
        await Server.WaitPost(() =>
        {
            beforeClose?.Invoke();
            SEntMan.EventBus.RaiseEvent(EventSource.Local,
                new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PreRoundLobby));
        });
        Guid selected = default;
        await Server.WaitAssertion(() =>
        {
            var coordinator = Server.System<NCRoundSnapshotCoordinatorSystem>();
            Assert.That(coordinator.State, Is.EqualTo(NCRoundSnapshotCoordinatorState.Committed), coordinator.LastError);
            selected = coordinator.LastSnapshotId;
            Assert.That(selected, Is.Not.EqualTo(Guid.Empty));
            SEntMan.EventBus.RaiseEvent(EventSource.Local,
                new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound));
        });
        await Until(() => persistence.TryCaptureTime(out var fresh, out _) && fresh != ending, "Fresh native world epoch did not select a checkpoint.");
        await RestoreActorBinding(scene);
        await Server.WaitAssertion(() => Assert.That(Server.System<NCMetaSnapshotSystem>().SelectedSnapshotId, Is.EqualTo(selected)));
        return selected;
    }

    private async Task CorruptRealLabDatabase(Action<SqliteServerDbContext> fault) =>
        await NCMetaNativeTestTasks.Run(Server, async () =>
        {
            await ((ServerDbManager)_database).ApplyMetaIsolatedSqliteFaultAsync(fault);
            return true;
        });

    [TestCase("none")]
    [TestCase("baseline-payload")]
    [TestCase("tail-gap")]
    [TestCase("tail-attestation")]
    public async Task NativeLabReceiptCannotReplayAcrossUnauthenticatedSelectedSnapshotOrJournalTail(string fault)
    {
        var scene = await CreateScene(); var grinder = scene["grinder"]; var beaker = scene["beaker"];
        var key = "before-checkpoint";
        var originalLog = _machines.Log.Level;
        NCEventJournalEntry removed = null;
        string originalPayload = null; string eventPayload = null;
        Guid aggregateId = default; Guid eventId = default;
        var changed = false;
        async Task RestoreFault()
        {
            if (!changed) return;
            await CorruptRealLabDatabase(context =>
            {
                if (fault == "baseline-payload") context.NCAggregateSnapshots.Where(row => row.AggregateType == "meta.machine.operation" && row.AggregateId == aggregateId.ToString("D"))
                    .ExecuteUpdate(update => update.SetProperty(row => row.Payload, originalPayload));
                else if (removed != null) { context.NCEventJournal.Add(removed); context.SaveChanges(); }
                else context.NCEventJournal.Where(row => row.EventId == eventId).ExecuteUpdate(update => update.SetProperty(row => row.Payload, eventPayload));
            });
            changed = false;
        }
        try
        {
            await Server.WaitAssertion(() =>
            {
                MoveNear(scene.Actor, grinder);
                Assert.That(_slots.TryInsert(grinder, ReagentGrinderComponent.BeakerSlotId, beaker, scene.Actor), Is.True);
                Assert.That(Server.System<SharedContainerSystem>().Insert(scene["ingredient"], SComp<ReagentGrinderComponent>(grinder).InputContainer), Is.True);
                Assert.That(_machines.TrySetAutomation(grinder, scene.Actor, true), Is.True);
            });
            async Task<NCMetaNativeNetworkProgramHarness.Installed> Prepare(string operationKey)
            {
                var installed = await Install(scene, ReceiptProgram.Replace("$KEY$", operationKey, StringComparison.Ordinal),
                    new Dictionary<string, EntityUid> { ["grinder"] = grinder }, "native.lab.selected-receipt");
                await Server.WaitPost(() => MoveBetween(scene, grinder));
                return installed;
            }
            var program = await Prepare(key);
            var started = await Invoke(scene, program, "run");
            await Until(() => started.Completed && !Server.System<SharedReagentGrinderSystem>().IsActive(grinder), "Initial native grinder operation did not finish.");
            var applied = await Receipt<MetaMachineReceipt>(scene, grinder, "meta.machine.operation", "grinder", key, value => value.Operation.Terminal);
            Assert.That(applied.Operation.Status, Is.EqualTo(MetaOperationStatus.Applied));
            var selected = await SelectRealLabCheckpoint(scene);
            var sections = await NCMetaNativeTestTasks.Run(Server, () => _database.GetNCRoundSnapshotSectionsAsync(selected, "meta-aggregates", "world", "city"));
            aggregateId = Guid.ParseExact(applied.Operation.Id, "N");
            Assert.That(sections.SelectMany(section => JsonSerializer.Deserialize<NCAggregateSnapshotData[]>(section.Payload)!)
                .Any(row => row.AggregateType == "meta.machine.operation" && row.AggregateId == aggregateId.ToString("D")), Is.True,
                "The real immutable checkpoint must contain the actual machine receipt, not a synthetic substitute type.");

            EntityUid replenished = default;
            await Server.WaitAssertion(() =>
            {
                replenished = SSpawnAtPosition("FoodCarrot", SComp<TransformComponent>(grinder).Coordinates);
                Assert.That(Server.System<SharedContainerSystem>().Insert(replenished, SComp<ReagentGrinderComponent>(grinder).InputContainer), Is.True);
            });
            if (fault is "tail-gap" or "tail-attestation") key = "after-checkpoint";
            program = await Prepare(key); // Normal install/consent/start in the new epoch, no stale VM resurrection.
            if (fault is "tail-gap" or "tail-attestation")
            {
                // A real new native job creates v1..v4 after the selected manifest; no hand-authored aggregate/event.
                var next = await Invoke(scene, program, "run");
                await Until(() => next.Completed && !Server.System<SharedReagentGrinderSystem>().IsActive(grinder), "Post-checkpoint native job did not finish.");
                applied = await Receipt<MetaMachineReceipt>(scene, grinder, "meta.machine.operation", "grinder", key, value => value.Operation.Terminal);
                Assert.That(applied.Operation.Status, Is.EqualTo(MetaOperationStatus.Applied));
                aggregateId = Guid.ParseExact(applied.Operation.Id, "N");
                await Server.WaitAssertion(() =>
                {
                    replenished = SSpawnAtPosition("FoodCarrot", SComp<TransformComponent>(grinder).Coordinates);
                    Assert.That(Server.System<SharedContainerSystem>().Insert(replenished, SComp<ReagentGrinderComponent>(grinder).InputContainer), Is.True);
                });
            }
            var current = await NCMetaNativeTestTasks.Run(Server, () => _database.GetNCAggregateSnapshotAsync("meta.machine.operation", aggregateId.ToString("D")));
            Assert.That(current, Is.Not.Null);
            originalPayload = current!.Payload;
            if (fault != "none")
            {
                await Server.WaitPost(() => _machines.Log.Level = LogLevel.Fatal); // Only the deliberately malformed receipt owns this expected integrity error.
                await CorruptRealLabDatabase(context =>
                {
                    if (fault == "baseline-payload")
                    {
                        var receipt = JsonSerializer.Deserialize<MetaMachineReceipt>(originalPayload)!;
                        // Still structurally valid for the native loader. Only selected-state attestation rejects this forged fingerprint.
                        var tampered = JsonSerializer.Serialize(receipt with { Operation = receipt.Operation with { Fingerprint = new string('0', 64) } });
                        context.NCAggregateSnapshots.Where(row => row.AggregateType == "meta.machine.operation" && row.AggregateId == aggregateId.ToString("D"))
                            .ExecuteUpdate(update => update.SetProperty(row => row.Payload, tampered));
                    }
                    else
                    {
                        var middle = context.NCEventJournal.AsNoTracking().Single(row => row.AggregateType == "meta.machine.operation" &&
                            row.AggregateId == aggregateId.ToString("D") && row.AggregateVersion == 2);
                        eventId = middle.EventId;
                        if (fault == "tail-gap") { removed = middle; context.NCEventJournal.Where(row => row.EventId == eventId).ExecuteDelete(); }
                        else { eventPayload = middle.Payload; context.NCEventJournal.Where(row => row.EventId == eventId).ExecuteUpdate(update => update.SetProperty(row => row.Payload, "{}")); }
                    }
                });
                changed = true;
            }
            // Capture real physical output now. The status read must never authorize another native grinder start.
            var volume = default(Content.Shared.FixedPoint.FixedPoint2);
            await Server.WaitPost(() => { Assert.That(_solutions.TryGetFitsInDispenser(beaker, out _, out var solution), Is.True); volume = solution!.Volume; });
            var query = await Invoke(scene, program, "status");
            await Until(() => query.Completed, "Native receipt query did not finish.");
            await Server.WaitAssertion(() =>
            {
                var logs = _runtime.GetLogs(scene["host"], scene.Actor, program.Installation.Id);
                Assert.That(logs, Does.Contain(fault == "none" ? "receipt-applied" : "receipt-unavailable"));
                Assert.That(_solutions.TryGetFitsInDispenser(beaker, out _, out var solution), Is.True);
                Assert.That(solution!.Volume, Is.EqualTo(volume));
                Assert.That(SEntMan.EntityExists(replenished), Is.True);
            });
            Assert.That((await NCMetaNativeTestTasks.Run(Server, () => _database.GetNCAggregateSnapshotAsync("meta.machine.operation", aggregateId.ToString("D"))))!.AggregateVersion,
                Is.EqualTo(current.AggregateVersion), "Rejected status access cannot append over a corrupted receipt/tail.");
            await RestoreFault();
            await Server.WaitPost(() => _machines.Log.Level = originalLog);
            var restoredQuery = await Invoke(scene, program, "status");
            await Until(() => restoredQuery.Completed, "Exact repaired receipt was not readable through native selected-state validation.");
            await AdvancePast(TimeSpan.FromSeconds(1)); // Actual command bucket, no artificial reset.
            var retry = await Invoke(scene, program, "run");
            await Until(() => retry.Completed, "Restored genuine Applied receipt was not returned by the real start path.");
            await Server.WaitAssertion(() =>
            {
                var logs = _runtime.GetLogs(scene["host"], scene.Actor, program.Installation.Id);
                Assert.That(logs, Does.Contain("receipt-applied").And.Contain("retry-applied"));
                Assert.That(SEntMan.EntityExists(replenished), Is.True);
                Assert.That(SComp<ReagentGrinderComponent>(grinder).InputContainer.ContainedEntities.Contains(replenished), Is.True);
                Assert.That(_solutions.TryGetFitsInDispenser(beaker, out _, out var solution), Is.True);
                Assert.That(solution!.Volume, Is.EqualTo(volume));
            });
            Assert.That((await NCMetaNativeTestTasks.Run(Server, () => _database.GetNCAggregateSnapshotAsync("meta.machine.operation", aggregateId.ToString("D"))))!.AggregateVersion,
                Is.EqualTo(current.AggregateVersion));
        }
        finally
        {
            await RestoreFault();
            await Server.WaitPost(() => _machines.Log.Level = originalLog);
            await Finish(scene);
        }
    }
}
#endif
