// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using Content.IntegrationTests.Fixtures;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.Persistence.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Serialization.Manager;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.Persistence;

[TestFixture]
[TestOf(typeof(NCActiveWorldClockRecord))]
public sealed class NCActiveWorldClockPersistenceTest : GameTest
{
    [Test]
    public async Task NewEpochResumesSavedTimeWithoutOfflineCatchUp()
    {
        var db = GetDb(Pair.Server);
        var first = await db.StartNCActiveWorldSessionAsync(NCSessionEpoch.New());
        var advanced = await db.AdvanceNCActiveWorldTimeAsync(
            first.SessionEpoch,
            first.Time,
            first.Version,
            TimeSpan.FromMinutes(10));
        Assert.That(advanced, Is.Not.Null);
        var closed = await db.CloseNCActiveWorldSessionAsync(
            advanced!.SessionEpoch,
            advanced.Time,
            advanced.Version,
            TimeSpan.FromMinutes(5));

        Assert.Multiple(() =>
        {
            Assert.That(first.RoundActive, Is.True);
            Assert.That(first.Time, Is.EqualTo(NCActiveWorldTime.Zero));
            Assert.That(advanced.Time.Elapsed, Is.EqualTo(TimeSpan.FromMinutes(10)));
            Assert.That(closed?.RoundActive, Is.False);
            Assert.That(closed?.Time.Elapsed, Is.EqualTo(TimeSpan.FromMinutes(15)));
        });

        var resumed = await db.StartNCActiveWorldSessionAsync(NCSessionEpoch.New());

        Assert.Multiple(() =>
        {
            Assert.That(resumed.RoundActive, Is.True);
            Assert.That(resumed.SessionEpoch, Is.Not.EqualTo(first.SessionEpoch));
            Assert.That(resumed.Time.Elapsed, Is.EqualTo(TimeSpan.FromMinutes(15)));
            Assert.That(resumed.Version, Is.GreaterThan(closed!.Version));
        });
    }

    [Test]
    public async Task FrozenOrStaleEpochCannotAdvanceWorldTime()
    {
        var db = GetDb(Pair.Server);
        var first = await db.StartNCActiveWorldSessionAsync(NCSessionEpoch.New());
        var closed = await db.CloseNCActiveWorldSessionAsync(
            first.SessionEpoch,
            first.Time,
            first.Version,
            TimeSpan.Zero);
        Assert.That(closed, Is.Not.Null);

        var frozenAdvance = await db.AdvanceNCActiveWorldTimeAsync(
            first.SessionEpoch,
            first.Time,
            first.Version,
            TimeSpan.FromMinutes(1));
        var second = await db.StartNCActiveWorldSessionAsync(NCSessionEpoch.New());
        var staleAdvance = await db.AdvanceNCActiveWorldTimeAsync(
            first.SessionEpoch,
            closed!.Time,
            closed.Version,
            TimeSpan.FromMinutes(1));
        var current = await db.GetNCActiveWorldClockAsync();

        Assert.Multiple(() =>
        {
            Assert.That(frozenAdvance, Is.Null);
            Assert.That(staleAdvance, Is.Null);
            Assert.That(current?.SessionEpoch, Is.EqualTo(second.SessionEpoch));
            Assert.That(current?.Time.Elapsed, Is.EqualTo(TimeSpan.Zero));
        });
    }

    [Test]
    public async Task RetriedClockCommandsReturnTheCommittedResultOnce()
    {
        var db = GetDb(Pair.Server);
        var epoch = NCSessionEpoch.New();
        var firstStart = await db.StartNCActiveWorldSessionAsync(epoch);
        var retriedStart = await db.StartNCActiveWorldSessionAsync(epoch);

        var firstAdvance = await db.AdvanceNCActiveWorldTimeAsync(
            epoch,
            firstStart.Time,
            firstStart.Version,
            TimeSpan.FromMinutes(2));
        var retriedAdvance = await db.AdvanceNCActiveWorldTimeAsync(
            epoch,
            firstStart.Time,
            firstStart.Version,
            TimeSpan.FromMinutes(2));
        Assert.That(firstAdvance, Is.Not.Null);

        var firstClose = await db.CloseNCActiveWorldSessionAsync(
            epoch,
            firstAdvance!.Time,
            firstAdvance.Version,
            TimeSpan.FromMinutes(1));
        var retriedClose = await db.CloseNCActiveWorldSessionAsync(
            epoch,
            firstAdvance.Time,
            firstAdvance.Version,
            TimeSpan.FromMinutes(1));

        Assert.Multiple(() =>
        {
            Assert.That(retriedStart, Is.EqualTo(firstStart));
            Assert.That(retriedAdvance, Is.EqualTo(firstAdvance));
            Assert.That(retriedClose, Is.EqualTo(firstClose));
            Assert.That(firstClose?.Time.Elapsed, Is.EqualTo(TimeSpan.FromMinutes(3)));
            Assert.That(firstClose?.RoundActive, Is.False);
        });
    }

    [Test]
    public async Task SystemAdvancesAndClosesOnlyAcrossLiveRoundTransitions()
    {
        var server = Pair.Server;
        var database = server.ResolveDependency<IServerDbManager>();
        var clock = server.System<NCActiveWorldTimeSystem>();

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));

        var ready = false;
        for (var i = 0; i < 120 && !ready; i++)
        {
            await server.WaitRunTicks(1);
            await server.WaitPost(() => ready = clock.IsReady);
        }

        Assert.That(ready, Is.True, "The active-world epoch did not become ready within 120 ticks.");

        NCSessionEpoch epoch = default;
        NCActiveWorldTime before = default;
        await server.WaitPost(() => Assert.That(clock.TryGetCurrentTime(out epoch, out before), Is.True));
        await server.WaitRunTicks(3);

        NCActiveWorldTime after = default;
        await server.WaitPost(() => Assert.That(clock.TryGetCurrentTime(out _, out after), Is.True));
        Assert.That(after, Is.GreaterThan(before));

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));

        NCActiveWorldClockData? stored = null;
        for (var i = 0; i < 120; i++)
        {
            await server.WaitRunTicks(1);
            stored = await database.GetNCActiveWorldClockAsync();
            if (stored is { RoundActive: false } && stored.SessionEpoch == epoch)
                break;
        }

        Assert.Multiple(() =>
        {
            Assert.That(stored?.SessionEpoch, Is.EqualTo(epoch));
            Assert.That(stored?.RoundActive, Is.False);
            Assert.That(stored?.Time, Is.GreaterThanOrEqualTo(after));
        });
    }

    private static ServerDbSqlite GetDb(RobustIntegrationTest.ServerIntegrationInstance server)
    {
        var configuration = server.ResolveDependency<IConfigurationManager>();
        var serialization = server.ResolveDependency<ISerializationManager>();
        var operationsLog = server.ResolveDependency<ILogManager>().GetSawmill("db.ops");
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>();
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        options.UseSqlite(connection);
        return new ServerDbSqlite(() => options.Options, true, configuration, true, operationsLog, serialization);
    }
}
