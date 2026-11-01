// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared.CCVar;
using Content.Shared._NC.Persistence.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Npgsql;
using NUnit.Framework;
using Robust.Shared.Configuration;
using Robust.Shared.Log;
using Robust.Shared.Serialization.Manager;

namespace Content.Tests.Server._NC.Meta;

[TestFixture, NonParallelizable]
public sealed class NCMetaJournalReadPersistenceTest
{
    private sealed class BodyGuard : DbCommandInterceptor
    {
        public int BodyQueries;
        public Action<DbConnection> BeforeBody;
        private void Inspect(DbCommand command)
        {
            if (!command.CommandText.Contains("SELECT * FROM nc_event_journal", StringComparison.Ordinal)) return;
            BodyQueries++;
            var callback = BeforeBody; BeforeBody = null;
            callback?.Invoke(command.Connection!);
        }
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result) { Inspect(command); return result; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Inspect(command); return ValueTask.FromResult(result); }
    }

    private sealed class Provider(ServerDbBase database, Func<ServerDbContext> context, SqliteConnection connection = null) : IAsyncDisposable
    {
        public ServerDbBase Database => database;
        public ServerDbContext Context() => context();
        public async ValueTask DisposeAsync() { database.Shutdown(); if (connection != null) await connection.DisposeAsync(); }
    }

    private static Provider Open(bool postgres, BodyGuard guard)
    {
        var config = new Mock<IConfigurationManager>();
        if (!postgres)
        {
            var connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
            var options = new DbContextOptionsBuilder<SqliteServerDbContext>().UseSqlite(connection).AddInterceptors(guard).Options;
            return new(new ServerDbSqlite(() => options, true, config.Object, true, new Mock<ISawmill>().Object,
                new Mock<ISerializationManager>().Object), () => new SqliteServerDbContext(options), connection);
        }
        var target = Environment.GetEnvironmentVariable("NC_META_TEST_POSTGRES");
        if (string.IsNullOrEmpty(target)) Assert.Ignore("An explicit owned PostgreSQL acceptance connection is required.");
        var parsed = new NpgsqlConnectionStringBuilder(target);
        Assert.Multiple(() =>
        {
            Assert.That(parsed.Host, Is.EqualTo("127.0.0.1"));
            Assert.That(parsed.Port, Is.EqualTo(55432));
            Assert.That(parsed.Database, Is.EqualTo("nc_meta_acceptance"));
            Assert.That(parsed.Username, Is.EqualTo("astro"));
        });
        config.Setup(value => value.GetCVar(CCVars.DatabasePgConcurrency)).Returns(4);
        var pg = new DbContextOptionsBuilder<PostgresServerDbContext>().UseNpgsql(target).AddInterceptors(guard).Options;
        return new(new ServerDbPostgres(pg, target, config.Object, new Mock<ISawmill>().Object, new Mock<ISawmill>().Object,
            new Mock<ISerializationManager>().Object), () => new PostgresServerDbContext(pg));
    }

    private static async Task Seed(Provider provider, string id, int count, int characters)
    {
        var epoch = NCSessionEpoch.New();
        var clock = await provider.Database.StartNCActiveWorldSessionAsync(epoch);
        for (var version = 0; version < count; version++)
        {
            var request = new NCPersistentMutationRequest(Guid.NewGuid(), "test.meta-journal", "test-meta-journal", id,
                Guid.NewGuid(), "meta.volume.changed", "meta.volume", id, version, 1, "{\"Files\":[]}",
                JsonSerializer.Serialize(new { Text = new string('x', characters), Marker = "unicode-marker" }), "{}",
                Guid.NewGuid(), null, epoch, clock.Time, []);
            Assert.That((await provider.Database.ApplyNCPersistentMutationAsync(request)).Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
        }
    }

    private static async Task Cleanup(Provider provider, string id)
    {
        await using var context = provider.Context();
        await context.NCAggregateSnapshots.Where(row => row.AggregateType == "meta.volume" && row.AggregateId == id).ExecuteDeleteAsync();
        await context.NCMetaAggregateRevisions.Where(row => row.AggregateType == "meta.volume" && row.AggregateId == id).ExecuteDeleteAsync();
        await context.NCEventJournal.Where(row => row.AggregateType == "meta.volume" && row.AggregateId == id).ExecuteDeleteAsync();
        await context.NCCommandReceipts.Where(row => row.ScopeType == "test-meta-journal" && row.ScopeId == id).ExecuteDeleteAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RealProviderPagesHaveBoundedBodiesAndPreserveUnicodeMetadataAndChain(bool postgres)
    {
        var guard = new BodyGuard(); await using var provider = Open(postgres, guard);
        var id = "journal-page-" + Guid.NewGuid().ToString("N");
        try
        {
            await Seed(provider, id, 5, 100000);
            await using (var context = provider.Context())
            {
                var row = await context.NCEventJournal.SingleAsync(row => row.AggregateType == "meta.volume" && row.AggregateId == id && row.AggregateVersion == 5);
                row.Payload = row.Payload.Replace("unicode-marker", "Привет 😀", StringComparison.Ordinal);
                await context.SaveChangesAsync();
            }
            var current = await provider.Database.GetNCAggregateSnapshotAsync("meta.volume", id);
            var verifier = NCMetaJournalIntegrity.TailVerifier.Create(current!, null)!;
            var pages = 0;
            while (verifier.Remaining > 0)
            {
                var page = await provider.Database.GetNCMetaJournalPageAsync("meta.volume", id, verifier.NextVersion + 1);
                Assert.That(page.Count, Is.InRange(1, 2));
                Assert.That(page.Sum(row => row.Payload.Length), Is.LessThanOrEqualTo(NCMetaJournalReadLimits.PageCharacters * 2));
                Assert.That(verifier.ConsumePage(page), Is.True);
                pages++;
            }
            Assert.That(verifier.Complete, Is.True);
            Assert.That(pages, Is.EqualTo(3));
        }
        finally { await Cleanup(provider, id); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SupportedLargeRowIsAloneAndOversizeRowFailsBeforeBodyMaterialization(bool postgres)
    {
        var guard = new BodyGuard(); await using var provider = Open(postgres, guard);
        var id = "journal-limit-" + Guid.NewGuid().ToString("N");
        try
        {
            await Seed(provider, id, 2, 100);
            await using var context = provider.Context();
            var newest = await context.NCEventJournal.SingleAsync(row => row.AggregateType == "meta.volume" && row.AggregateId == id && row.AggregateVersion == 2);
            newest.Payload = new string('x', NCMetaJournalReadLimits.PageCharacters + 1); await context.SaveChangesAsync();
            Assert.That(await provider.Database.GetNCMetaJournalPageAsync("meta.volume", id, 3), Has.Count.EqualTo(1));
            newest.Payload = new string('x', NCMetaJournalReadLimits.EventCharacters + 1); await context.SaveChangesAsync();
            guard.BodyQueries = 0;
            Assert.ThrowsAsync<InvalidDataException>(async () => await provider.Database.GetNCMetaJournalPageAsync("meta.volume", id, 3));
            Assert.That(guard.BodyQueries, Is.Zero, "Oversize bodies must be rejected from SQL metadata, before the payload SELECT.");
        }
        finally { await Cleanup(provider, id); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ARowGrowingBetweenMetadataAndBodyCannotBypassTheSqlBound(bool postgres)
    {
        var guard = new BodyGuard(); await using var provider = Open(postgres, guard);
        var id = "journal-race-" + Guid.NewGuid().ToString("N");
        try
        {
            await Seed(provider, id, 1, 100);
            await using var context = provider.Context();
            var original = await context.NCEventJournal.AsNoTracking().SingleAsync(row => row.AggregateType == "meta.volume" && row.AggregateId == id);
            guard.BeforeBody = connection =>
            {
                using var mutation = connection.CreateCommand();
                mutation.CommandText = "UPDATE nc_event_journal SET payload = payload || " + (postgres ? "" : "char(0) || ") + "@payload WHERE event_id = @id";
                var payload = mutation.CreateParameter(); payload.ParameterName = "@payload";
                // SQLite length(text) stays at its original count after NUL; the SQL byte predicate must still reject this growth.
                payload.Value = new string('x', 1024 * 1024);
                var key = mutation.CreateParameter(); key.ParameterName = "@id"; key.Value = original.EventId;
                mutation.Parameters.Add(payload); mutation.Parameters.Add(key);
                Assert.That(mutation.ExecuteNonQuery(), Is.EqualTo(1));
            };
            Assert.ThrowsAsync<InvalidDataException>(async () => await provider.Database.GetNCMetaJournalPageAsync("meta.volume", id, 2));
            Assert.That(guard.BodyQueries, Is.EqualTo(1));
        }
        finally { await Cleanup(provider, id); }
    }
}
