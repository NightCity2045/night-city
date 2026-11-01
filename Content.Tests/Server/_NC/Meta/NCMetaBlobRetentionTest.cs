// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Threading.Tasks;
using System.Linq;
using System.Text;
using System.Text.Json;
using Content.Server.Database;
using Content.Shared._NC.Persistence.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Robust.Shared.Configuration;
using Robust.Shared.Log;
using Robust.Shared.Serialization.Manager;

namespace Content.Tests.Server._NC.Meta;

[TestFixture]
public sealed class NCMetaBlobRetentionTest
{
    [Test]
    public async Task MetaPolicyUpgradeRebuildsHistoricalRootsBeforeAnyCollection()
    {
        var db = GetDb(out var connection, out var options);
        await using var databaseConnection = connection;
        var epoch = NCSessionEpoch.New();
        var clock = await db.StartNCActiveWorldSessionAsync(epoch);
        var roots = new System.Collections.Generic.List<string>();
        for (var index = 0; index < 12; index++)
        {
            var digest = await db.StoreNCMetaBlobAsync(Encoding.UTF8.GetBytes("historical root " + index));
            roots.Add(digest);
            await db.ApplyNCPersistentMutationAsync(CreateRequest(epoch, clock.Time, index, BlobPayload(digest)));
        }
        await db.ApplyNCPersistentMutationAsync(CreateRequest(epoch, clock.Time, 12, "{\"Files\":[]}"));
        await using var context = new SqliteServerDbContext(options);
        await context.NCMetaAggregateBlobReferences.ExecuteDeleteAsync();
        await context.NCMetaAggregateRevisions.ExecuteUpdateAsync(update => update.SetProperty(value => value.ReferencePolicyVersion, 0));
        await context.NCMetaBlobs.ExecuteUpdateAsync(update => update.SetProperty(value => value.CreatedAt, DateTime.UtcNow.AddDays(-2))
            .SetProperty(value => value.AdmittedUntilUtcTicks, 0));
        NCMetaBlobMaintenanceResult result = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            result = await db.MaintainNCMetaBlobsAsync();
            foreach (var digest in roots) Assert.That(await db.ReadNCMetaBlobAsync(digest), Is.Not.Null);
            if (result.BackfillComplete) break;
        }
        Assert.That(result?.BackfillComplete, Is.True);
        Assert.That(await context.NCMetaAggregateBlobReferences.CountAsync(), Is.EqualTo(12));
        Assert.That(await context.NCMetaAggregateRevisions.CountAsync(value => value.ReferencePolicyVersion == Content.Meta.Persistence.MetaBlobReferences.PolicyVersion), Is.EqualTo(13));
    }

    [Test]
    public async Task MetaStorageAdmissionIsDurableDeduplicatedAndCollectionReleasesCounters()
    {
        var db = GetDb(out var connection, out var options);
        await using var databaseConnection = connection;
        var bytes = Encoding.UTF8.GetBytes("deduplicated admitted blob");
        var digest = await db.StoreNCMetaBlobAsync(bytes);
        await using var context = new SqliteServerDbContext(options);
        var state = await context.NCMetaBlobStorageState.SingleAsync();
        Assert.That(state.AllocatedCount, Is.EqualTo(1));
        Assert.That(state.AllocatedBytes, Is.EqualTo(bytes.Length));
        state.CountTokens = 0;
        state.RefilledAtUtcTicks = DateTime.UtcNow.AddHours(1).Ticks;
        await context.SaveChangesAsync();
        Assert.That(await db.StoreNCMetaBlobAsync(bytes), Is.EqualTo(digest));
        Assert.ThrowsAsync<NCMetaBlobQuotaException>(async () => await db.StoreNCMetaBlobAsync(Encoding.UTF8.GetBytes("refused new bytes")));
        Assert.That(await context.NCMetaBlobs.CountAsync(), Is.EqualTo(1));
        await context.Entry(state).ReloadAsync();
        Assert.That(state.AllocatedCount, Is.EqualTo(1));
        Assert.That(state.CountTokens, Is.Zero);
        await context.NCMetaBlobs.ExecuteUpdateAsync(update => update.SetProperty(value => value.CreatedAt, DateTime.UtcNow.AddDays(-2))
            .SetProperty(value => value.AdmittedUntilUtcTicks, 0));
        for (var attempt = 0; attempt < 5; attempt++)
            if ((await db.MaintainNCMetaBlobsAsync()).BackfillComplete) break;
        Assert.That(await db.ReadNCMetaBlobAsync(digest), Is.Null);
        await context.Entry(state).ReloadAsync();
        Assert.That(state.AllocatedCount, Is.Zero);
        Assert.That(state.AllocatedBytes, Is.Zero);
    }

    [Test]
    public async Task MetaHighFrequencyStateWritesCannotAccumulateUnboundedRevisionHistory()
    {
        var db = GetDb(out var connection, out var options);
        await using var databaseConnection = connection;
        var epoch = NCSessionEpoch.New();
        var clock = await db.StartNCActiveWorldSessionAsync(epoch);
        var digest = await db.StoreNCMetaBlobAsync(Encoding.UTF8.GetBytes("shared state bytes"));
        var revisions = ServerDbBase.NCMetaMaximumRetainedRevisions + 7;
        for (var index = 0; index < revisions; index++)
            Assert.That((await db.ApplyNCPersistentMutationAsync(CreateRequest(epoch, clock.Time, index,
                BlobPayload(digest)))).Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
        await using var context = new SqliteServerDbContext(options);
        Assert.That(await context.NCMetaAggregateRevisions.CountAsync(), Is.EqualTo(ServerDbBase.NCMetaMaximumRetainedRevisions));
        Assert.That(await context.NCMetaAggregateBlobReferences.CountAsync(), Is.EqualTo(ServerDbBase.NCMetaMaximumRetainedRevisions));
        Assert.That(await db.GetNCMetaAggregateRevisionAsync("meta.volume", "aggregate-1", 1), Is.Null);
        Assert.That(await db.GetNCMetaAggregateRevisionAsync("meta.volume", "aggregate-1", revisions), Is.Not.Null);
        Assert.That(await db.ReadNCMetaBlobAsync(digest), Is.Not.Null);
    }

    [Test]
    public async Task MetaBlobReferencesCommitWithReceiptAndMissingBytesRollBackTheEntireBatch()
    {
        var db = GetDb(out var connection, out var options);
        await using var databaseConnection = connection;
        var epoch = NCSessionEpoch.New();
        var clock = await db.StartNCActiveWorldSessionAsync(epoch);
        var digest = await db.StoreNCMetaBlobAsync(Encoding.UTF8.GetBytes("durable referenced source"));
        var first = CreateRequest(epoch, clock.Time, 0, BlobPayload(digest)) with { AggregateType = "meta.volume" };
        Assert.That((await db.ApplyNCPersistentMutationAsync(first)).Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
        await using var context = new SqliteServerDbContext(options);
        Assert.That(await context.NCMetaAggregateBlobReferences.CountAsync(), Is.EqualTo(1));
        Assert.That((await db.GetNCMetaAggregateRevisionAsync("meta.volume", first.AggregateId, 1))?.Payload, Is.EqualTo(first.SnapshotPayload));

        var missing = new string('e', 64);
        var request = first with { CommandId = Guid.NewGuid(), EventId = Guid.NewGuid(), AggregateId = "missing", SnapshotPayload = BlobPayload(missing) };
        Assert.ThrowsAsync<DbUpdateException>(async () => await db.ApplyNCPersistentMutationAsync(request));
        Assert.That(await db.GetNCPersistentCommandResultAsync(request.CommandId), Is.Null);
        Assert.That(await db.GetNCAggregateSnapshotAsync("meta.volume", "missing"), Is.Null);
        Assert.That(await db.GetNCMetaAggregateRevisionAsync("meta.volume", "missing", 1), Is.Null);

        var batch = new NCPersistentBatchMutationRequest(Guid.NewGuid(), "test.meta-refs", "test", "owner-1",
            [new(Guid.NewGuid(), "meta.volume.changed", "meta.volume", "batch-existing", 0, 1, BlobPayload(digest), "{}"),
                new(Guid.NewGuid(), "meta.volume.changed", "meta.volume", "batch-missing", 0, 1, BlobPayload(missing), "{}")],
            "{}", Guid.NewGuid(), null, epoch, clock.Time, []);
        Assert.ThrowsAsync<DbUpdateException>(async () => await db.ApplyNCPersistentBatchMutationAsync(batch));
        Assert.That(await db.GetNCPersistentCommandResultAsync(batch.CommandId), Is.Null);
        Assert.That(await db.GetNCAggregateSnapshotAsync("meta.volume", "batch-existing"), Is.Null);
        Assert.That(await context.NCMetaAggregateRevisions.CountAsync(), Is.EqualTo(1));
        Assert.That(await db.ReadNCMetaBlobAsync(digest), Is.Not.Null);
    }

    [Test]
    public async Task MetaRetentionKeepsCurrentAndRetainedWorldRootsWhileCollectingExpiredHistoryAndOrphans()
    {
        var db = GetDb(out var connection, out var options);
        await using var databaseConnection = connection;
        var epoch = NCSessionEpoch.New();
        var clock = await db.StartNCActiveWorldSessionAsync(epoch);
        var old = await db.StoreNCMetaBlobAsync(Encoding.UTF8.GetBytes("old source"));
        var current = await db.StoreNCMetaBlobAsync(Encoding.UTF8.GetBytes("current source"));
        var checkpointOnly = await db.StoreNCMetaBlobAsync(Encoding.UTF8.GetBytes("retained checkpoint source"));
        var orphan = await db.StoreNCMetaBlobAsync(Encoding.UTF8.GetBytes("interrupted precommit upload"));
        var young = await db.StoreNCMetaBlobAsync(Encoding.UTF8.GetBytes("pending upload grace"));
        var first = CreateRequest(epoch, clock.Time, 0, BlobPayload(old), aggregateId: "source") with { AggregateType = "meta.volume" };
        await db.ApplyNCPersistentMutationAsync(first);
        await db.ApplyNCPersistentMutationAsync(first with
        {
            CommandId = Guid.NewGuid(), EventId = Guid.NewGuid(), ExpectedAggregateVersion = 1, SnapshotPayload = BlobPayload(current),
        });
        var anchor = first with { CommandId = Guid.NewGuid(), EventId = Guid.NewGuid(), AggregateId = "anchor", SnapshotPayload = BlobPayload(checkpointOnly) };
        await db.ApplyNCPersistentMutationAsync(anchor);
        var anchored = await db.GetNCAggregateSnapshotAsync("meta.volume", "anchor");
        var snapshotId = await CommitMetaBlobCheckpointAsync(db, epoch, clock.Time, JsonSerializer.Serialize(new[] { anchored }));
        await db.ApplyNCPersistentMutationAsync(anchor with
        {
            CommandId = Guid.NewGuid(), EventId = Guid.NewGuid(), ExpectedAggregateVersion = 1, SnapshotPayload = "{\"Files\":[]}",
        });

        await using var context = new SqliteServerDbContext(options);
        var oldDate = DateTime.UtcNow.AddDays(-45);
        foreach (var blob in await context.NCMetaBlobs.Where(value => value.Digest != young).ToListAsync())
        { blob.CreatedAt = oldDate; blob.AdmittedUntilUtcTicks = 0; }
        foreach (var revision in await context.NCMetaAggregateRevisions.ToListAsync()) revision.CreatedAt = oldDate;
        await context.SaveChangesAsync();
        Assert.That((await db.MaintainNCMetaBlobsAsync()).BackfillComplete, Is.False);
        var collected = await db.MaintainNCMetaBlobsAsync();
        Assert.Multiple(() =>
        {
            Assert.That(collected.BackfillComplete, Is.True);
            Assert.That(collected.RevisionsRemoved, Is.EqualTo(2));
            Assert.That(collected.BlobsRemoved, Is.EqualTo(2));
        });
        Assert.That(await db.ReadNCMetaBlobAsync(old), Is.Null);
        Assert.That(await db.ReadNCMetaBlobAsync(orphan), Is.Null);
        Assert.That(await db.ReadNCMetaBlobAsync(current), Is.Not.Null);
        Assert.That(await db.ReadNCMetaBlobAsync(young), Is.Not.Null);
        Assert.That(await db.ReadNCMetaBlobAsync(checkpointOnly), Is.Not.Null);
        Assert.That(await db.GetNCMetaAggregateRevisionAsync("meta.volume", "source", 1), Is.Null);
        Assert.That(await db.GetNCMetaAggregateRevisionAsync("meta.volume", "source", 2), Is.Not.Null);

        var nextEpoch = NCSessionEpoch.New();
        var nextClock = await db.StartNCActiveWorldSessionAsync(nextEpoch);
        await CommitMetaBlobCheckpointAsync(db, nextEpoch, nextClock.Time, "[]", retain: 1);
        Assert.That(await db.GetNCRoundSnapshotInventoryAsync(snapshotId), Is.Null);
        Assert.That((await db.MaintainNCMetaBlobsAsync()).BlobsRemoved, Is.EqualTo(1));
        Assert.That(await db.ReadNCMetaBlobAsync(checkpointOnly), Is.Null);
        Assert.That(await db.ReadNCMetaBlobAsync(current), Is.Not.Null);
    }

    [Test]
    public async Task MetaLegacyBackfillBlocksCollectionUntilEveryRetainedChunkIsVerified()
    {
        var db = GetDb(out var connection, out var options);
        await using var databaseConnection = connection;
        var epoch = NCSessionEpoch.New();
        var clock = await db.StartNCActiveWorldSessionAsync(epoch);
        var retained = await db.StoreNCMetaBlobAsync(Encoding.UTF8.GetBytes("legacy checkpoint only"));
        var orphan = await db.StoreNCMetaBlobAsync(Encoding.UTF8.GetBytes("old orphan"));
        var first = CreateRequest(epoch, clock.Time, 0, BlobPayload(retained)) with { AggregateType = "meta.volume" };
        await db.ApplyNCPersistentMutationAsync(first);
        var record = await db.GetNCAggregateSnapshotAsync("meta.volume", first.AggregateId);
        var payload = JsonSerializer.Serialize(new[] { record });
        var id = await CommitMetaBlobCheckpointAsync(db, epoch, clock.Time, payload);
        await db.ApplyNCPersistentMutationAsync(first with
        {
            CommandId = Guid.NewGuid(), EventId = Guid.NewGuid(), ExpectedAggregateVersion = 1, SnapshotPayload = "{\"Files\":[]}",
        });
        await using var context = new SqliteServerDbContext(options);
        // Model an upgraded database: legacy aggregates/checkpoints precede reference indexes.
        await context.NCMetaAggregateRevisions.ExecuteDeleteAsync();
        await context.NCMetaCheckpointBlobReferences.ExecuteDeleteAsync();
        await context.NCMetaCheckpointBlobBackfills.ExecuteDeleteAsync();
        foreach (var blob in await context.NCMetaBlobs.ToListAsync())
        { blob.CreatedAt = DateTime.UtcNow.AddDays(-45); blob.AdmittedUntilUtcTicks = 0; }
        var chunk = await context.NCRoundSnapshotSections.SingleAsync(value => value.SnapshotId == id);
        chunk.Payload = "[]"; // Preserved descriptor checksum must not hide corrupt bytes.
        await context.SaveChangesAsync();
        Assert.That((await db.MaintainNCMetaBlobsAsync()).BackfillComplete, Is.False);
        Assert.ThrowsAsync<InvalidOperationException>(async () => await db.MaintainNCMetaBlobsAsync());
        Assert.That(await db.ReadNCMetaBlobAsync(orphan), Is.Not.Null);
        chunk.Payload = payload;
        await context.SaveChangesAsync();
        NCMetaBlobMaintenanceResult result = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            result = await db.MaintainNCMetaBlobsAsync();
            if (result.BackfillComplete) break;
            Assert.That(await db.ReadNCMetaBlobAsync(orphan), Is.Not.Null);
        }
        Assert.That(result?.BackfillComplete, Is.True);
        Assert.That(await db.ReadNCMetaBlobAsync(orphan), Is.Null);
        Assert.That(await db.ReadNCMetaBlobAsync(retained), Is.Not.Null);
    }

    [Test]
    public async Task ReusingAnOldUnreferencedBlobRenewsItsPendingReferenceGraceWithoutChargingQuota()
    {
        var db = GetDb(out var connection, out var options);
        await using var databaseConnection = connection;
        var bytes = Encoding.UTF8.GetBytes("old bytes being referenced again");
        var digest = await db.StoreNCMetaBlobAsync(bytes);
        await using var context = new SqliteServerDbContext(options);
        await context.NCMetaBlobs.ExecuteUpdateAsync(update => update.SetProperty(value => value.CreatedAt, DateTime.UtcNow.AddDays(-2))
            .SetProperty(value => value.AdmittedUntilUtcTicks, 0));
        Assert.That(await db.StoreNCMetaBlobAsync(bytes), Is.EqualTo(digest));
        for (var work = 0; work < 8; work++)
            if ((await db.MaintainNCMetaBlobsAsync()).BackfillComplete) break;
        Assert.That(await db.ReadNCMetaBlobAsync(digest), Is.EqualTo(bytes));
        var state = await context.NCMetaBlobStorageState.AsNoTracking().SingleAsync();
        Assert.That(state.AllocatedCount, Is.EqualTo(1));
        var epoch = NCSessionEpoch.New();
        var clock = await db.StartNCActiveWorldSessionAsync(epoch);
        var result = await db.ApplyNCPersistentMutationAsync(CreateRequest(epoch, clock.Time, 0, BlobPayload(digest)));
        Assert.That(result.Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
    }

    private static string BlobPayload(string digest) => JsonSerializer.Serialize(new { Files = new[] { new { Digest = digest } } });

    private static ServerDbSqlite GetDb(out SqliteConnection connection, out DbContextOptions<SqliteServerDbContext> options)
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        options = new DbContextOptionsBuilder<SqliteServerDbContext>().UseSqlite(connection).Options;
        var captured = options;
        return new ServerDbSqlite(() => captured, true, new Mock<IConfigurationManager>().Object, true,
            new Mock<ISawmill>().Object, new Mock<ISerializationManager>().Object);
    }

    private static NCPersistentMutationRequest CreateRequest(NCSessionEpoch epoch, NCActiveWorldTime time,
        long version, string payload, string aggregateId = "aggregate-1") =>
        new(Guid.NewGuid(), "test.meta-storage", "test", "owner-1", Guid.NewGuid(), "meta.volume.changed",
            "meta.volume", aggregateId, version, 1, payload, "{}", "{}", Guid.NewGuid(), null, epoch, time, []);

    private static async Task<Guid> CommitMetaBlobCheckpointAsync(ServerDbSqlite db, NCSessionEpoch epoch,
        NCActiveWorldTime time, string payload, int retain = 3)
    {
        using var document = JsonDocument.Parse(payload);
        var section = new NCRoundSnapshotSectionWrite("meta-aggregates", "world", "city", 0, 1,
            document.RootElement.GetArrayLength(), payload, NCMetaJournalIntegrity.HashPayload(payload));
        var descriptor = new NCRoundSnapshotInventorySection(section.SectionType, section.OwnerType, section.OwnerId,
            0, section.FormatVersion, section.EntityCount, Encoding.UTF8.GetByteCount(payload), section.Checksum);
        var id = Guid.NewGuid();
        Assert.That((await db.CommitNCRoundSnapshotAsync(new(id, epoch.Value, time.Ticks, 1, "test", 0, "",
            NCRoundSnapshotInventoryIntegrity.Checksum([descriptor]), DateTime.UtcNow, [section], [], retain))).Status,
            Is.EqualTo(NCRoundSnapshotCommitStatus.Committed));
        return id;
    }
}
