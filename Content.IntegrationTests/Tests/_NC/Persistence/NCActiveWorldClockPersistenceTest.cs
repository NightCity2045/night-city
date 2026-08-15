// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using Content.IntegrationTests.Fixtures;
using Content.Server.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Configuration;
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
        var first = await db.StartNCActiveWorldSessionAsync();
        var advanced = await db.AdvanceNCActiveWorldTimeAsync(first.SessionEpoch, TimeSpan.FromMinutes(15));
        var frozen = await db.FreezeNCActiveWorldSessionAsync(first.SessionEpoch);

        Assert.Multiple(() =>
        {
            Assert.That(first.RoundActive, Is.True);
            Assert.That(first.Time, Is.EqualTo(Content.Shared._NC.Persistence.Time.NCActiveWorldTime.Zero));
            Assert.That(advanced?.Time.Elapsed, Is.EqualTo(TimeSpan.FromMinutes(15)));
            Assert.That(frozen?.RoundActive, Is.False);
            Assert.That(frozen?.Time.Elapsed, Is.EqualTo(TimeSpan.FromMinutes(15)));
        });

        var resumed = await db.StartNCActiveWorldSessionAsync();

        Assert.Multiple(() =>
        {
            Assert.That(resumed.RoundActive, Is.True);
            Assert.That(resumed.SessionEpoch, Is.Not.EqualTo(first.SessionEpoch));
            Assert.That(resumed.Time.Elapsed, Is.EqualTo(TimeSpan.FromMinutes(15)));
            Assert.That(resumed.Version, Is.GreaterThan(frozen!.Version));
        });
    }

    [Test]
    public async Task FrozenOrStaleEpochCannotAdvanceWorldTime()
    {
        var db = GetDb(Pair.Server);
        var first = await db.StartNCActiveWorldSessionAsync();
        var frozen = await db.FreezeNCActiveWorldSessionAsync(first.SessionEpoch);
        Assert.That(frozen, Is.Not.Null);

        var frozenAdvance = await db.AdvanceNCActiveWorldTimeAsync(first.SessionEpoch, TimeSpan.FromMinutes(1));
        var second = await db.StartNCActiveWorldSessionAsync();
        var staleAdvance = await db.AdvanceNCActiveWorldTimeAsync(first.SessionEpoch, TimeSpan.FromMinutes(1));
        var current = await db.GetNCActiveWorldClockAsync();

        Assert.Multiple(() =>
        {
            Assert.That(frozenAdvance, Is.Null);
            Assert.That(staleAdvance, Is.Null);
            Assert.That(current?.SessionEpoch, Is.EqualTo(second.SessionEpoch));
            Assert.That(current?.Time.Elapsed, Is.EqualTo(TimeSpan.Zero));
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
