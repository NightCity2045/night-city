// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
#if TOOLS && NC_DIAGNOSTICS
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Persistence.RoundSnapshot;
using Content.Shared._NC.Identity;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.GameObjects;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Content.IntegrationTests.Tests._NC.Meta;

/// <summary>Genuine confirmed writes, a cold selected baseline, and exact provider faults; no cache-field injection.</summary>
[TestFixture, NonParallelizable]
[TestOf(typeof(NCMetaSnapshotSystem))]
public sealed class NCMetaSnapshotLivenessTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = false };

    private async Task Until(Func<bool> condition, string message)
    {
        var elapsed = Stopwatch.StartNew();
        for (var tick = 0; tick < 1800 && elapsed.Elapsed < TimeSpan.FromSeconds(30); tick++)
        {
            var ready = false;
            var remaining = TimeSpan.FromSeconds(30) - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await Server.WaitPost(() => ready = condition()).WaitAsync(remaining);
            if (ready) return;
            remaining = TimeSpan.FromSeconds(30) - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await Server.WaitRunTicks(1).WaitAsync(remaining);
        }
        Assert.Fail(message);
    }

    private async Task<Guid> CloseAndSelectNextEpoch()
    {
        var persistence = Server.System<NCMetaPersistenceSystem>();
        NCSessionEpoch oldEpoch = default;
        await Server.WaitAssertion(() => Assert.That(persistence.TryCaptureTime(out oldEpoch, out _), Is.True));
        await Server.WaitPost(() => SEntMan.EventBus.RaiseEvent(EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PreRoundLobby)));
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
        await Until(() => persistence.TryCaptureTime(out var fresh, out _) && fresh != oldEpoch, "Fresh active-world epoch did not start.");
        await Server.WaitAssertion(() => Assert.That(Server.System<NCMetaSnapshotSystem>().SelectedSnapshotId, Is.EqualTo(selected)));
        return selected;
    }

    private async Task WriteVersions(Guid id, string owner, int first, int last)
    {
        var database = Server.ResolveDependency<IServerDbManager>();
        var persistence = Server.System<NCMetaPersistenceSystem>();
        var elapsed = Stopwatch.StartNew();
        for (var start = first; start <= last; start += 64)
        {
            var batchFirst = start; var batchLast = Math.Min(last, start + 63);
            await NCMetaNativeTestTasks.Run(Server, async () =>
            {
                for (var version = batchFirst; version <= batchLast; version++)
                {
                    Assert.That(persistence.TryCaptureTime(out var epoch, out var time), Is.True);
                    var volume = new NCMetaVolume(id, owner, 262144, [], version);
                    var request = new NCPersistentMutationRequest(Guid.NewGuid(), "test.meta-liveness", "meta.owner", owner,
                        Guid.NewGuid(), "meta.volume.changed", "meta.volume", id.ToString("D"), version - 1, 1,
                        JsonSerializer.Serialize(volume), "{}", "{}", Guid.NewGuid(), null, epoch, time, []);
                    var committed = await database.ApplyNCPersistentMutationAsync(request);
                    Assert.That(committed.Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
                    Assert.That(committed.AggregateVersion, Is.EqualTo(version));
                }
                return true;
            });
            Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromMinutes(3)), "The bounded confirmed-write fixture exceeded its total budget.");
        }
    }

    private async Task ReplaceExactRow(NCAggregateSnapshotData row) => await NCMetaNativeTestTasks.Run(Server, async () =>
    {
        var database = (ServerDbManager)Server.ResolveDependency<IServerDbManager>();
        await database.ApplyMetaIsolatedSqliteFaultAsync(context =>
        {
            var current = context.NCAggregateSnapshots.Single(value => value.AggregateType == row.AggregateType && value.AggregateId == row.AggregateId);
            current.AggregateVersion = row.AggregateVersion;
            current.FormatVersion = row.FormatVersion;
            current.SessionEpoch = row.SessionEpoch.Value;
            current.ActiveWorldTicks = row.ActiveWorldTime.Ticks;
            current.Payload = row.Payload;
            current.CreatedAt = row.CreatedAt;
            context.SaveChanges();
        });
        return true;
    });

    [TestCase(false)]
    [TestCase(true)]
    public async Task ColdSelectedHistoryOver4096RemainsReadableCapturableAndCannotRegressToAnOlderAuthenticatedRow(bool captureFirst)
    {
        var persistence = Server.System<NCMetaPersistenceSystem>();
        var snapshots = Server.System<NCMetaSnapshotSystem>();
        var database = Server.ResolveDependency<IServerDbManager>();
        var clock = Server.System<NCActiveWorldTimeSystem>();
        var id = Guid.NewGuid();
        var owner = NCPartyRef.ForCharacter(new NCCharacterId(750)).ToString();
        NCAggregateSnapshotData latest = null;
        var replaced = false;
        await Server.WaitPost(() => SEntMan.EventBus.RaiseEvent(EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        try
        {
            await Until(() => persistence.TryCaptureTime(out _, out _), "Native epoch did not become ready.");
            await WriteVersions(id, owner, 1, 1);
            var first = await NCMetaNativeTestTasks.Run(Server, () => database.GetNCAggregateSnapshotAsync("meta.volume", id.ToString("D")));
            Assert.That(first, Is.Not.Null);
            var selected = await CloseAndSelectNextEpoch();
            var sections = await NCMetaNativeTestTasks.Run(Server, () => database.GetNCRoundSnapshotSectionsAsync(selected, "meta-aggregates", "world", "city"));
            Assert.That(sections.SelectMany(section => JsonSerializer.Deserialize<NCAggregateSnapshotData[]>(section.Payload)!)
                .Single(row => row.AggregateType == "meta.volume" && row.AggregateId == id.ToString("D")), Is.EqualTo(first));
            // These are actual atomic commits, deliberately without any verifier read of this aggregate in the new epoch.
            await WriteVersions(id, owner, 2, 5004);
            latest = await NCMetaNativeTestTasks.Run(Server, () => database.GetNCAggregateSnapshotAsync("meta.volume", id.ToString("D")));
            Assert.That(latest!.AggregateVersion, Is.EqualTo(5004));
            if (captureFirst) await NCMetaNativeTestTasks.Run(Server, async () => { await snapshots.PrepareCaptureAsync(); return true; });
            Assert.That(await NCMetaNativeTestTasks.Run(Server, () => snapshots.ReadCurrentAsync("meta.volume", id.ToString("D"))), Is.EqualTo(latest));
            await NCMetaNativeTestTasks.Run(Server, async () => { await snapshots.PrepareCaptureAsync(); return true; });

            // This old row is genuinely authenticated by the selected manifest, but the cache already confirmed a newer version.
            await ReplaceExactRow(first!); replaced = true;
            Assert.ThrowsAsync<NCMetaSnapshotIntegrityException>(async () =>
                await NCMetaNativeTestTasks.Run(Server, () => snapshots.ReadCurrentAsync("meta.volume", id.ToString("D"))));
            await ReplaceExactRow(latest); replaced = false;
            Assert.That(await NCMetaNativeTestTasks.Run(Server, () => snapshots.ReadCurrentAsync("meta.volume", id.ToString("D"))), Is.EqualTo(latest));
            await CloseAndSelectNextEpoch();
            Assert.That(await NCMetaNativeTestTasks.Run(Server, () => snapshots.ReadCurrentAsync("meta.volume", id.ToString("D"))), Is.EqualTo(latest));
        }
        finally
        {
            if (replaced && latest != null) await ReplaceExactRow(latest);
            await Server.WaitPost(() => SEntMan.EventBus.RaiseEvent(EventSource.Local,
                new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
            await Until(() => !clock.IsReady, "Native world close did not drain its real durable operations.");
        }
    }
}
#endif
