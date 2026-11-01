// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.GameObjects;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._NC.Meta;

[TestFixture, NonParallelizable]
public sealed class NCMetaActiveWorldClockTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = false };

    [Test]
    public async Task ClosingAfterTimingRewindsPersistsEveryAlreadyPublishedTimestamp()
    {
        var clock = Server.System<NCActiveWorldTimeSystem>();
        var database = Server.ResolveDependency<IServerDbManager>();
        await Server.WaitPost(() => SEntMan.EventBus.RaiseEvent(EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        for (var tick = 0; tick < 600 && !clock.IsReady; tick++) await Server.WaitRunTicks(1);
        Assert.That(clock.IsReady, Is.True);

        NCSessionEpoch epoch = default;
        NCActiveWorldTime published = default;
        await NCMetaNativeTestTasks.Run(Server, () =>
        {
            var original = Server.Timing.CurTick;
            try
            {
                Server.Timing.CurTick = new GameTick(checked(original.Value + 120u));
                Assert.That(clock.TryGetCurrentTime(out epoch, out published), Is.True);
            }
            finally { Server.Timing.CurTick = original; }
            SEntMan.EventBus.RaiseEvent(EventSource.Local,
                new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PreRoundLobby));
            return Task.FromResult(true);
        });

        NCActiveWorldClockData? closed = null;
        for (var tick = 0; tick < 600; tick++)
        {
            closed = await NCMetaNativeTestTasks.Run(Server, () => database.GetNCActiveWorldClockAsync());
            if (closed is { RoundActive: false } && closed.SessionEpoch == epoch) break;
            await Server.WaitRunTicks(1);
        }
        Assert.That(closed, Is.Not.Null);
        Assert.That(closed!.RoundActive, Is.False);
        Assert.That(closed.SessionEpoch, Is.EqualTo(epoch));
        Assert.That(closed.Time.Ticks, Is.GreaterThanOrEqualTo(published.Ticks),
            "Closing an epoch cannot discard a timestamp already used by a durable META mutation.");
    }
}
