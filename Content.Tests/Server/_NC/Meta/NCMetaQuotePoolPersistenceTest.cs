// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Meta.Commerce;
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
public sealed class NCMetaQuotePoolPersistenceTest
{
    [Test]
    public async Task ReusingOneSlotKeepsCurrentCardinalityFixedAndHistoricalAcknowledgementsImmutable()
    {
        var db = Open(out var connection, out var options); await using var lifetime = connection;
        var epoch = NCSessionEpoch.New(); var clock = await db.StartNCActiveWorldSessionAsync(epoch);
        var pool = MetaQuotePoolPolicy.Empty; long slotVersion = 0;
        var firstRequest = Guid.Empty; string? firstAcknowledgement = null;
        for (var cycle = 0; cycle < 12; cycle++)
        {
            var quote = Guid.NewGuid(); var request = Guid.NewGuid();
            var allocation = MetaQuotePoolPolicy.Reserve(pool, "buyer", quote, 0, TimeSpan.TicksPerMinute)!;
            var acknowledgement = JsonSerializer.Serialize(new { Quote = quote, Price = 100 + cycle, ExpiresAtTicks = TimeSpan.TicksPerMinute });
            var created = await db.ApplyNCPersistentBatchMutationAsync(new(request, "meta.test.quote", "meta.owner", "buyer",
                [Write("meta.quote-pool", "software", pool.Revision, allocation.Next), Write("meta.software.quote", MetaQuotePoolPolicy.SlotKey(allocation.Slot), slotVersion,
                    new { Id = quote, Buyer = "buyer", Price = 100 + cycle, Revision = slotVersion + 1 })], acknowledgement, request, null, epoch, clock.Time, [], RequestFingerprint: new string('a',64)));
            Assert.That(created.Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
            slotVersion++; pool = allocation.Next;
            var released = MetaQuotePoolPolicy.Release(pool, "buyer", quote, 0)!;
            var purchased = await db.ApplyNCPersistentBatchMutationAsync(new(Guid.NewGuid(), "meta.test.consume", "meta.owner", "buyer",
                [Write("meta.quote-pool", "software", pool.Revision, released), Write("meta.software.quote", MetaQuotePoolPolicy.SlotKey(allocation.Slot), slotVersion,
                    new { Id = quote, Buyer = "buyer", Consumed = true, Revision = slotVersion + 1 })], "{}", request, null, epoch, clock.Time, []));
            Assert.That(purchased.Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
            slotVersion++; pool = released;
            if (cycle == 0) { firstRequest = request; firstAcknowledgement = acknowledgement; }
        }
        await using var context = new SqliteServerDbContext(options);
        Assert.That(await context.NCAggregateSnapshots.CountAsync(), Is.EqualTo(2));
        Assert.That((await db.GetNCPersistentCommandResultAsync(firstRequest))!.ResultPayload, Is.EqualTo(firstAcknowledgement));
        Assert.That((await db.GetNCAggregateSnapshotAsync("meta.software.quote", MetaQuotePoolPolicy.SlotKey(0)))!.AggregateVersion, Is.EqualTo(24));
    }

    [Test]
    public async Task CompetingPoolCasRollsBackEveryQuoteAndAllOtherParticipants()
    {
        var db = Open(out var connection, out var options); await using var lifetime = connection;
        var epoch = NCSessionEpoch.New(); var clock = await db.StartNCActiveWorldSessionAsync(epoch);
        var first = MetaQuotePoolPolicy.Reserve(MetaQuotePoolPolicy.Empty, "first", Guid.NewGuid(), 0, TimeSpan.TicksPerMinute)!;
        var stale = MetaQuotePoolPolicy.Reserve(MetaQuotePoolPolicy.Empty, "second", Guid.NewGuid(), 0, TimeSpan.TicksPerMinute)!;
        var admitted = await db.ApplyNCPersistentBatchMutationAsync(new(Guid.NewGuid(), "meta.test.quote", "meta.owner", "first",
            [Write("meta.quote-pool", "software", 0, first.Next), Write("meta.software.quote", MetaQuotePoolPolicy.SlotKey(first.Slot), 0, new { Price = 10 })], "{}", Guid.NewGuid(), null, epoch, clock.Time, []));
        Assert.That(admitted.Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
        var rejected = await db.ApplyNCPersistentBatchMutationAsync(new(Guid.NewGuid(), "meta.test.quote", "meta.owner", "second",
            [Write("meta.quote-pool", "software", 0, stale.Next), Write("meta.software.quote", MetaQuotePoolPolicy.SlotKey(stale.Slot), 0, new { Price = 0 }),
                Write("test.payment", "witness", 0, new { Paid = true })], "{}", Guid.NewGuid(), null, epoch, clock.Time, []));
        Assert.That(rejected.Status, Is.EqualTo(NCPersistentMutationStatus.VersionConflict));
        Assert.That(await db.GetNCAggregateSnapshotAsync("test.payment", "witness"), Is.Null);
        await using var context = new SqliteServerDbContext(options);
        Assert.That(await context.NCAggregateSnapshots.CountAsync(), Is.EqualTo(2));
        Assert.That((await db.GetNCAggregateSnapshotAsync("meta.software.quote", MetaQuotePoolPolicy.SlotKey(first.Slot)))!.Payload, Does.Contain("10"));
    }

    private static NCPersistentAggregateWrite Write<T>(string type, string id, long revision, T value) =>
        new(Guid.NewGuid(), "meta.quote.test", type, id, revision, 1, JsonSerializer.Serialize(value), "{}");
    private static ServerDbSqlite Open(out SqliteConnection connection, out DbContextOptions<SqliteServerDbContext> options)
    {
        connection = new("Data Source=:memory:"); connection.Open();
        options = new DbContextOptionsBuilder<SqliteServerDbContext>().UseSqlite(connection).Options;
        var captured = options;
        return new(() => captured, true, new Mock<IConfigurationManager>().Object, true,
            new Mock<ISawmill>().Object, new Mock<ISerializationManager>().Object);
    }
}
