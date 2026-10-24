// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared.CCVar;
using Content.Shared._NC.Persistence.Time;
using Microsoft.EntityFrameworkCore;
using Moq;
using Npgsql;
using NUnit.Framework;
using Robust.Shared.Configuration;
using Robust.Shared.Log;
using Robust.Shared.Serialization.Manager;

namespace Content.Tests.Server._NC.Meta;

/// <summary>Real PostgreSQL coverage, explicitly pointed at a fresh owned acceptance database.</summary>
[TestFixture, NonParallelizable]
public sealed class NCMetaPostgresPersistenceTest
{
    private ServerDbPostgres _database;
    private DbContextOptions<PostgresServerDbContext> _options;

    [OneTimeSetUp]
    public void Setup()
    {
        var connection = Environment.GetEnvironmentVariable("NC_META_TEST_POSTGRES");
        if (string.IsNullOrEmpty(connection)) Assert.Ignore("An isolated PostgreSQL acceptance connection is required.");
        var parsed = new NpgsqlConnectionStringBuilder(connection);
        Assert.That(parsed.Host, Is.EqualTo("127.0.0.1"));
        Assert.That(parsed.Port, Is.EqualTo(55432));
        Assert.That(parsed.Database, Does.StartWith("nc_meta_"));
        _options = new DbContextOptionsBuilder<PostgresServerDbContext>().UseNpgsql(connection).Options;
        var configuration = new Mock<IConfigurationManager>();
        configuration.Setup(value => value.GetCVar(CCVars.DatabasePgConcurrency)).Returns(4);
        _database = new(_options, connection, configuration.Object, new Mock<ISawmill>().Object,
            new Mock<ISawmill>().Object, new Mock<ISerializationManager>().Object);
    }

    [OneTimeTearDown]
    public void Cleanup() => _database?.Shutdown();

    [Test]
    public async Task ConcurrentIdenticalBlobWritersChargePhysicalAllocationOnlyOnce()
    {
        var bytes = Encoding.UTF8.GetBytes("postgres concurrent blob " + Guid.NewGuid());
        // Complete the bounded legacy allocation scan before comparing durable counters.
        for (var work = 0; work < 16; work++)
            if ((await _database.MaintainNCMetaBlobsAsync()).BackfillComplete) break;
        await using var context = new PostgresServerDbContext(_options);
        var before = await context.NCMetaBlobStorageState.AsNoTracking().SingleAsync();
        var digests = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => RetryAsync(() => _database.StoreNCMetaBlobAsync(bytes))));
        Assert.That(digests.Distinct().Count(), Is.EqualTo(1));
        var after = await context.NCMetaBlobStorageState.AsNoTracking().SingleAsync();
        Assert.That(after.AllocatedCount, Is.EqualTo(before.AllocatedCount + 1));
        Assert.That(after.AllocatedBytes, Is.EqualTo(before.AllocatedBytes + bytes.Length));
        Assert.That(await context.NCMetaBlobs.CountAsync(value => value.Digest == digests[0]), Is.EqualTo(1));
        Assert.That(await _database.ReadNCMetaBlobAsync(digests[0]), Is.EqualTo(bytes));
    }

    [Test]
    public async Task MissingBlobForeignKeyRollsBackSnapshotJournalAndReceipt()
    {
        var epoch = NCSessionEpoch.New();
        var clock = await _database.StartNCActiveWorldSessionAsync(epoch);
        var request = Request(epoch, clock.Time, Guid.NewGuid().ToString("N"), 0, Payload(new string('f', 64)));
        Assert.ThrowsAsync<DbUpdateException>(async () => await _database.ApplyNCPersistentMutationAsync(request));
        Assert.That(await _database.GetNCAggregateSnapshotAsync(request.AggregateType, request.AggregateId), Is.Null);
        Assert.That(await _database.GetNCPersistentCommandResultAsync(request.CommandId), Is.Null);
        Assert.That(await _database.GetNCMetaAggregateRevisionAsync(request.AggregateType, request.AggregateId, 1), Is.Null);
        var digest = await _database.StoreNCMetaBlobAsync(Encoding.UTF8.GetBytes("postgres valid batch " + Guid.NewGuid()));
        var batch = new NCPersistentBatchMutationRequest(Guid.NewGuid(), "test.meta-postgres", "test", "postgres-test",
            [new(Guid.NewGuid(), "meta.volume.written", "meta.volume", Guid.NewGuid().ToString("N"), 0, 1, Payload(digest), "{}"),
                new(Guid.NewGuid(), "meta.volume.written", "meta.volume", Guid.NewGuid().ToString("N"), 0, 1, request.SnapshotPayload, "{}")],
            "{}", Guid.NewGuid(), null, epoch, clock.Time, []);
        Assert.ThrowsAsync<DbUpdateException>(async () => await _database.ApplyNCPersistentBatchMutationAsync(batch));
        Assert.That(await _database.GetNCPersistentCommandResultAsync(batch.CommandId), Is.Null);
        foreach (var write in batch.Writes)
            Assert.That(await _database.GetNCAggregateSnapshotAsync(write.AggregateType, write.AggregateId), Is.Null);
    }

    [Test]
    public async Task HistoricalPolicyBackfillPrecedesPostgresGarbageCollection()
    {
        var epoch = NCSessionEpoch.New();
        var clock = await _database.StartNCActiveWorldSessionAsync(epoch);
        var id = Guid.NewGuid().ToString("N");
        var historical = await _database.StoreNCMetaBlobAsync(Encoding.UTF8.GetBytes("postgres history " + id));
        var orphan = await _database.StoreNCMetaBlobAsync(Encoding.UTF8.GetBytes("postgres orphan " + id));
        Assert.That((await _database.ApplyNCPersistentMutationAsync(Request(epoch, clock.Time, id, 0, Payload(historical)))).Status,
            Is.EqualTo(NCPersistentMutationStatus.Applied));
        await _database.ApplyNCPersistentMutationAsync(Request(epoch, clock.Time, id, 1, "{\"Files\":[]}"));
        await using var context = new PostgresServerDbContext(_options);
        await context.NCMetaAggregateBlobReferences.Where(value => value.AggregateType == "meta.volume" && value.AggregateId == id).ExecuteDeleteAsync();
        await context.NCMetaAggregateRevisions.Where(value => value.AggregateType == "meta.volume" && value.AggregateId == id)
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.ReferencePolicyVersion, 0));
        await context.NCMetaBlobs.Where(value => value.Digest == historical || value.Digest == orphan)
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.CreatedAt, DateTime.UtcNow.AddDays(-2))
                .SetProperty(value => value.AdmittedUntilUtcTicks, 0));
        NCMetaBlobMaintenanceResult result = null;
        for (var work = 0; work < 16; work++)
        {
            result = await _database.MaintainNCMetaBlobsAsync();
            Assert.That(await _database.ReadNCMetaBlobAsync(historical), Is.Not.Null);
            if (result.BackfillComplete) break;
            Assert.That(await _database.ReadNCMetaBlobAsync(orphan), Is.Not.Null);
        }
        Assert.That(result?.BackfillComplete, Is.True);
        Assert.That(await _database.ReadNCMetaBlobAsync(orphan), Is.Null);
        Assert.That(await context.NCMetaAggregateBlobReferences.AnyAsync(value => value.AggregateId == id && value.Digest == historical), Is.True);
        var counters = await context.NCMetaBlobStorageState.AsNoTracking().SingleAsync();
        Assert.That(counters.AllocatedCount, Is.EqualTo(await context.NCMetaBlobs.LongCountAsync()));
        Assert.That(counters.AllocatedBytes, Is.EqualTo(await context.NCMetaBlobs.SumAsync(value => (long) value.ByteCount)));
    }

    private static string Payload(string digest) => JsonSerializer.Serialize(new { Files = new[] { new { Digest = digest } } });

    private static NCPersistentMutationRequest Request(NCSessionEpoch epoch, NCActiveWorldTime time, string id, long version, string payload) =>
        new(Guid.NewGuid(), "test.meta-postgres", "test", "postgres-test", Guid.NewGuid(), "meta.volume.written",
            "meta.volume", id, version, 1, payload, "{}", "{}", Guid.NewGuid(), null, epoch, time, []);

    private static async Task<T> RetryAsync<T>(Func<Task<T>> command)
    {
        // Exercise the same provider classification as the production manager.
        for (var attempt = 0;; attempt++)
        {
            try { return await command(); }
            catch (Exception exception) when (attempt < 8 && ServerDbManager.IsTransient(exception))
            { await Task.Delay(10 * (attempt + 1)); }
        }
    }
}
