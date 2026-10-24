// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
#if TOOLS && NC_DIAGNOSTICS
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Persistence.RoundSnapshot;
using Content.Shared._NC.Meta.Network;
using Content.Shared._NC.Persistence.Time;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;

namespace Content.IntegrationTests.Tests._NC.Meta;

public sealed partial class NCMetaNativeNetworkTest
{
    [TestCase("PolicyPayload")]
    [TestCase("JournalPayload")]
    [TestCase("JournalGap")]
    public async Task ActualSelectedCheckpointAndItsJournalTailRemainAuthoritativeAtTheNativeNetLoadAndActionBoundaries(string fault)
    {
        var scene = await CreateScene(net: true);
        var database = (ServerDbManager)Server.ResolveDependency<IServerDbManager>();
        var snapshots = Server.System<NCMetaSnapshotSystem>();
        var level = _net.Log.Level;
        string accessId = "", type = "", originalPayload = "";
        NCEventJournalEntry removed = null;
        var changed = false;
        async Task Corrupt(Action<SqliteServerDbContext> action)
        {
            Task pending = Task.CompletedTask;
            // This is the actual provider's guarded in-memory integration seam, not another SQLite DB or a fake manager.
            // The callback touches only sync EF and never calls native systems while it owns the provider semaphore.
            await Server.WaitPost(() => pending = database.ApplyMetaIsolatedSqliteFaultAsync(action));
            await pending;
        }
        try
        {
            await Connect(scene);
            await Action(scene, NCMetaNetOperation.Scan);
            NCSessionEpoch ending = default;
            await Server.WaitAssertion(() =>
            {
                Assert.That(_devices.TryGetEntry(scene.Physical.Objects["host"], out var access), Is.True); accessId = access.Id;
                Assert.That(_persistence.TryCaptureTime(out ending, out _), Is.True);
            });
            // The normal closing pipeline drains native NET writes, prepares META sections and commits one real manifest.
            await Server.WaitPost(() => SEntMan.EventBus.RaiseEvent(EventSource.Local,
                new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PreRoundLobby)));
            var coordinator = Server.System<NCRoundSnapshotCoordinatorSystem>();
            Guid selected = default;
            await Server.WaitAssertion(() =>
            {
                Assert.That(coordinator.State, Is.EqualTo(NCRoundSnapshotCoordinatorState.Committed), coordinator.LastError);
                selected = coordinator.LastSnapshotId;
                Assert.That(selected, Is.Not.EqualTo(Guid.Empty));
                SEntMan.EventBus.RaiseEvent(EventSource.Local,
                    new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound));
            });
            await Until(() => _persistence.TryCaptureTime(out var fresh, out _) && fresh != ending);
            await Server.WaitAssertion(() => Assert.That(snapshots.SelectedSnapshotId, Is.EqualTo(selected)));
            Assert.That((await database.GetNCRoundSnapshotSectionsAsync(selected, "meta-aggregates", "world", "city")).Count,
                Is.GreaterThan(0), "The real selected manifest must contain the META policy/journal baseline.");

            if (fault != "PolicyPayload")
            {
                // Reload from that baseline without synchronization/re-authoring, then create a real post-checkpoint journal tail.
                await Until(() =>
                {
                    MoveTo(scene.Actor, scene.Deck);
                    if (_net.TryGetState(scene.Deck, scene.Actor, out var state) && state.SessionId != Guid.Empty) return true;
                    return _net.TryConnect(scene.Deck, scene.Actor, scene.Physical.Objects["host"]) &&
                        _net.TryGetState(scene.Deck, scene.Actor, out state) && state.SessionId != Guid.Empty;
                });
                await Action(scene, NCMetaNetOperation.Scan);
                await Server.WaitRunTicks(200);
            }
            type = fault == "PolicyPayload" ? "meta.net.policy" : "meta.net.journal";
            var current = await database.GetNCAggregateSnapshotAsync(type, accessId);
            Assert.That(current, Is.Not.Null);
            originalPayload = current!.Payload;
            await Server.WaitPost(() => _net.Log.Level = LogLevel.Fatal); // This one deliberately malformed fixture owns the expected NET integrity error.
            await Corrupt(context =>
            {
                if (fault == "JournalGap")
                {
                    removed = context.NCEventJournal.AsNoTracking().Single(value => value.AggregateType == type &&
                        value.AggregateId == accessId && value.AggregateVersion == current.AggregateVersion);
                    context.NCEventJournal.Where(value => value.EventId == removed.EventId).ExecuteDelete();
                }
                else context.NCAggregateSnapshots.Where(value => value.AggregateType == type && value.AggregateId == accessId)
                    .ExecuteUpdate(update => update.SetProperty(value => value.Payload, "{\"injected\":true}"));
            });
            changed = true;
            if (fault == "PolicyPayload")
            {
                // The previous epoch really had a cached ready policy; using it would wrongly attach here.
                await Server.WaitAssertion(() =>
                {
                    MoveTo(scene.Actor, scene.Deck);
                    _net.TryConnect(scene.Deck, scene.Actor, scene.Physical.Objects["host"]);
                });
                await Server.WaitRunTicks(210);
                Assert.That((await ReadState(scene)).SessionId, Is.EqualTo(Guid.Empty));
            }
            else
            {
                var before = await ReadState(scene);
                await Server.WaitAssertion(() => Assert.That(_net.TryExecuteAction(scene.Deck, scene.Actor, NCMetaNetOperation.Probe,
                    before.SessionId, before.Revision, "data"), Is.True));
                await Until(() => _net.TryGetState(scene.Deck, scene.Actor, out var state) && !state.ActionPending);
                var rejected = await ReadState(scene);
                Assert.That(rejected.Nodes.Single(node => node.Id == "data").Probed, Is.False);
                Assert.That(rejected.Trace, Is.EqualTo(before.Trace));
                Assert.That(rejected.StatusKey, Is.EqualTo("nc-meta-net-action-rejected"));
                Assert.That((await database.GetNCAggregateSnapshotAsync(type, accessId))!.AggregateVersion,
                    Is.EqualTo(current.AggregateVersion), "Neither admission nor outcome may append over an unauthenticated journal tail.");
            }
        }
        finally
        {
            if (changed) await Corrupt(context =>
            {
                if (removed != null) { context.NCEventJournal.Add(removed); context.SaveChanges(); }
                else context.NCAggregateSnapshots.Where(value => value.AggregateType == type && value.AggregateId == accessId)
                    .ExecuteUpdate(update => update.SetProperty(value => value.Payload, originalPayload));
            });
            await Server.WaitPost(() => _net.Log.Level = level);
            await Destroy(scene);
        }
    }
}
#endif
