// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server.Database;
using Content.Shared._NC.Persistence.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Configuration;
using Robust.Shared.Log;
using Robust.Shared.Serialization.Manager;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.Persistence;

[TestFixture]
[TestOf(typeof(NCEventJournalEntry))]
public sealed class NCPersistentMutationPersistenceTest : GameTest
{
    [Test]
    public async Task MutationSnapshotReceiptAndOutboxCommitExactlyOnce()
    {
        var db = GetDb(Pair.Server);
        var epoch = NCSessionEpoch.New();
        var clock = await db.StartNCActiveWorldSessionAsync(epoch);
        var outboxId = Guid.NewGuid();
        var request = CreateRequest(
            epoch,
            clock.Time,
            expectedVersion: 0,
            snapshotPayload: "{\"balance\":100}",
            outboxId: outboxId);

        var applied = await db.ApplyNCPersistentMutationAsync(request);
        var duplicate = await db.ApplyNCPersistentMutationAsync(request);
        var snapshot = await db.GetNCAggregateSnapshotAsync(request.AggregateType, request.AggregateId);
        var pending = await db.GetPendingNCOutboxMessagesAsync(DateTime.UtcNow.AddMinutes(1), 10);

        Assert.Multiple(() =>
        {
            Assert.That(applied.Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
            Assert.That(applied.AggregateVersion, Is.EqualTo(1));
            Assert.That(duplicate.Status, Is.EqualTo(NCPersistentMutationStatus.DuplicateCompleted));
            Assert.That(duplicate.EventId, Is.EqualTo(applied.EventId));
            Assert.That(duplicate.AggregateVersion, Is.EqualTo(1));
            Assert.That(snapshot?.AggregateVersion, Is.EqualTo(1));
            Assert.That(snapshot?.Payload, Is.EqualTo("{\"balance\":100}"));
            Assert.That(pending, Has.Count.EqualTo(1));
            Assert.That(pending[0].MessageId, Is.EqualTo(outboxId));
            Assert.That(pending[0].EventId, Is.EqualTo(applied.EventId));
        });

        var retryAt = DateTime.UtcNow.AddMinutes(1);
        var failureRecorded = await db.RecordNCOutboxMessageFailureAsync(outboxId, "temporary transport failure", retryAt);
        var notDue = await db.GetPendingNCOutboxMessagesAsync(retryAt.AddTicks(-1), 10);
        var dueAgain = await db.GetPendingNCOutboxMessagesAsync(retryAt, 10);
        var failedMessage = await db.GetNCOutboxMessageAsync(outboxId);
        var exhaustedStatus = await db.GetNCOutboxStatusAsync(retryAt, 1);
        var excludedAfterExhaustion = await db.GetPendingNCOutboxMessagesAsync(retryAt, 10, 1);
        var requeued = await db.RequeueNCOutboxMessageAsync(outboxId, retryAt, true);
        var requeuedMessage = await db.GetNCOutboxMessageAsync(outboxId);
        var readyStatus = await db.GetNCOutboxStatusAsync(retryAt, 1);
        var processed = await db.MarkNCOutboxMessageProcessedAsync(outboxId, retryAt.AddMinutes(1));
        var duplicateProcessed = await db.MarkNCOutboxMessageProcessedAsync(outboxId, retryAt.AddMinutes(2));
        var processedRequeue = await db.RequeueNCOutboxMessageAsync(outboxId, retryAt, true);
        var pendingAfterSuccess = await db.GetPendingNCOutboxMessagesAsync(retryAt.AddMinutes(3), 10);

        Assert.Multiple(() =>
        {
            Assert.That(failureRecorded, Is.True);
            Assert.That(notDue, Is.Empty);
            Assert.That(dueAgain, Has.Count.EqualTo(1));
            Assert.That(dueAgain[0].Attempts, Is.EqualTo(1));
            Assert.That(dueAgain[0].LastError, Is.EqualTo("temporary transport failure"));
            Assert.That(failedMessage?.Attempts, Is.EqualTo(1));
            Assert.That(exhaustedStatus.Pending, Is.Zero);
            Assert.That(exhaustedStatus.Ready, Is.Zero);
            Assert.That(exhaustedStatus.Exhausted, Is.EqualTo(1));
            Assert.That(exhaustedStatus.OldestUnprocessedAt, Is.Not.Null);
            Assert.That(excludedAfterExhaustion, Is.Empty);
            Assert.That(requeued, Is.True);
            Assert.That(requeuedMessage?.Attempts, Is.Zero);
            Assert.That(requeuedMessage?.LastError, Is.Null);
            Assert.That(readyStatus.Pending, Is.EqualTo(1));
            Assert.That(readyStatus.Ready, Is.EqualTo(1));
            Assert.That(readyStatus.Exhausted, Is.Zero);
            Assert.That(processed, Is.True);
            Assert.That(duplicateProcessed, Is.False);
            Assert.That(processedRequeue, Is.False);
            Assert.That(pendingAfterSuccess, Is.Empty);
        });
    }

    [Test]
    public async Task VersionAndWorldEpochConflictsFailClosedAndRemainIdempotent()
    {
        var db = GetDb(Pair.Server);
        var epoch = NCSessionEpoch.New();
        var clock = await db.StartNCActiveWorldSessionAsync(epoch);
        var first = CreateRequest(epoch, clock.Time, 0, "{\"state\":1}");
        var firstResult = await db.ApplyNCPersistentMutationAsync(first);
        Assert.That(firstResult.Status, Is.EqualTo(NCPersistentMutationStatus.Applied));

        var conflicting = CreateRequest(epoch, clock.Time, 0, "{\"state\":2}");
        var conflict = await db.ApplyNCPersistentMutationAsync(conflicting);
        var duplicateConflict = await db.ApplyNCPersistentMutationAsync(conflicting);

        var closed = await db.CloseNCActiveWorldSessionAsync(
            epoch,
            clock.Time,
            clock.Version,
            TimeSpan.Zero);
        Assert.That(closed, Is.Not.Null);
        var afterClose = CreateRequest(epoch, clock.Time, 1, "{\"state\":3}");
        var clockRejected = await db.ApplyNCPersistentMutationAsync(afterClose);

        var snapshot = await db.GetNCAggregateSnapshotAsync(first.AggregateType, first.AggregateId);
        Assert.Multiple(() =>
        {
            Assert.That(conflict.Status, Is.EqualTo(NCPersistentMutationStatus.VersionConflict));
            Assert.That(conflict.AggregateVersion, Is.EqualTo(1));
            Assert.That(duplicateConflict.Status, Is.EqualTo(NCPersistentMutationStatus.DuplicateFailed));
            Assert.That(clockRejected.Status, Is.EqualTo(NCPersistentMutationStatus.ClockRejected));
            Assert.That(snapshot?.AggregateVersion, Is.EqualTo(1));
            Assert.That(snapshot?.Payload, Is.EqualTo("{\"state\":1}"));
        });
    }

    [Test]
    public async Task AggregateSnapshotsUseStableBoundedKeysetPages()
    {
        var db = GetDb(Pair.Server);
        var epoch = NCSessionEpoch.New();
        var clock = await db.StartNCActiveWorldSessionAsync(epoch);

        foreach (var aggregateId in new[] { "aggregate-3", "aggregate-1", "aggregate-2" })
        {
            var result = await db.ApplyNCPersistentMutationAsync(CreateRequest(
                epoch,
                clock.Time,
                0,
                $"{{\"id\":\"{aggregateId}\"}}",
                aggregateId: aggregateId));
            Assert.That(result.Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
        }

        var otherType = CreateRequest(epoch, clock.Time, 0, "{}", aggregateId: "aggregate-0") with
        {
            AggregateType = "other-aggregate",
        };
        Assert.That(
            (await db.ApplyNCPersistentMutationAsync(otherType)).Status,
            Is.EqualTo(NCPersistentMutationStatus.Applied));

        var firstPage = await db.GetNCAggregateSnapshotPageAsync("test-aggregate", null, 2);
        var secondPage = await db.GetNCAggregateSnapshotPageAsync(
            "test-aggregate",
            firstPage[^1].AggregateId,
            2);

        Assert.Multiple(() =>
        {
            Assert.That(firstPage.Select(value => value.AggregateId),
                Is.EqualTo(new[] { "aggregate-1", "aggregate-2" }));
            Assert.That(secondPage.Select(value => value.AggregateId),
                Is.EqualTo(new[] { "aggregate-3" }));
            Assert.That(firstPage.Concat(secondPage), Has.All.Matches<NCAggregateSnapshotData>(value =>
                value.AggregateType == "test-aggregate"));
        });
    }

    [Test]
    public async Task BatchMutationCommitsAllAggregatesOrNoneAndReplaysExactResult()
    {
        var db = GetDb(Pair.Server);
        var epoch = NCSessionEpoch.New();
        var clock = await db.StartNCActiveWorldSessionAsync(epoch);
        var firstEvent = Guid.NewGuid();
        var secondEvent = Guid.NewGuid();
        var outboxId = Guid.NewGuid();
        var batch = new NCPersistentBatchMutationRequest(
            Guid.NewGuid(),
            "living-city.test.batch",
            "test-batch",
            "membership-1",
            new[]
            {
                new NCPersistentAggregateWrite(
                    firstEvent,
                    "living-city.test.first-changed",
                    "test-batch-aggregate",
                    "first",
                    0,
                    1,
                    "{\"state\":1}",
                    "{\"change\":1}"),
                new NCPersistentAggregateWrite(
                    secondEvent,
                    "living-city.test.second-changed",
                    "test-batch-aggregate",
                    "second",
                    0,
                    1,
                    "{\"state\":2}",
                    "{\"change\":2}"),
            },
            "{\"accepted\":true}",
            Guid.NewGuid(),
            null,
            epoch,
            clock.Time,
            new[]
            {
                new NCPersistentBatchOutboxWrite(
                    outboxId,
                    secondEvent,
                    "living-city.test.batch-notification",
                    "{\"kind\":\"test\"}"),
            });

        var applied = await db.ApplyNCPersistentBatchMutationAsync(batch);
        var duplicate = await db.ApplyNCPersistentBatchMutationAsync(batch);
        var first = await db.GetNCAggregateSnapshotAsync("test-batch-aggregate", "first");
        var second = await db.GetNCAggregateSnapshotAsync("test-batch-aggregate", "second");
        var pending = await db.GetPendingNCOutboxMessagesAsync(DateTime.UtcNow.AddMinutes(1), 10);

        Assert.Multiple(() =>
        {
            Assert.That(applied.Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
            Assert.That(applied.Aggregates.Select(value => value.AggregateVersion), Is.All.EqualTo(1));
            Assert.That(duplicate.Status, Is.EqualTo(NCPersistentMutationStatus.DuplicateCompleted));
            Assert.That(duplicate.Aggregates, Is.EqualTo(applied.Aggregates));
            Assert.That(duplicate.ResultPayload, Is.EqualTo(batch.ResultPayload));
            Assert.That(first?.Payload, Is.EqualTo("{\"state\":1}"));
            Assert.That(second?.Payload, Is.EqualTo("{\"state\":2}"));
            Assert.That(pending.Single(value => value.MessageId == outboxId).EventId, Is.EqualTo(secondEvent));
        });

        var conflictBatch = batch with
        {
            CommandId = Guid.NewGuid(),
            Writes = new[]
            {
                batch.Writes[0] with
                {
                    EventId = Guid.NewGuid(),
                    ExpectedAggregateVersion = 1,
                    SnapshotPayload = "{\"state\":10}",
                },
                batch.Writes[1] with
                {
                    EventId = Guid.NewGuid(),
                    ExpectedAggregateVersion = 0,
                    SnapshotPayload = "{\"state\":20}",
                },
            },
            OutboxMessages = [],
        };
        var conflict = await db.ApplyNCPersistentBatchMutationAsync(conflictBatch);
        var duplicateConflict = await db.ApplyNCPersistentBatchMutationAsync(conflictBatch);
        first = await db.GetNCAggregateSnapshotAsync("test-batch-aggregate", "first");
        second = await db.GetNCAggregateSnapshotAsync("test-batch-aggregate", "second");

        Assert.Multiple(() =>
        {
            Assert.That(conflict.Status, Is.EqualTo(NCPersistentMutationStatus.VersionConflict));
            Assert.That(conflict.Aggregates.Select(value => value.AggregateVersion), Is.EqualTo(new long[] { 1, 1 }));
            Assert.That(duplicateConflict.Status, Is.EqualTo(NCPersistentMutationStatus.DuplicateFailed));
            Assert.That(duplicateConflict.Aggregates, Is.EqualTo(conflict.Aggregates));
            Assert.That(first?.Payload, Is.EqualTo("{\"state\":1}"));
            Assert.That(second?.Payload, Is.EqualTo("{\"state\":2}"));
        });
    }

    private static NCPersistentMutationRequest CreateRequest(
        NCSessionEpoch epoch,
        NCActiveWorldTime time,
        long expectedVersion,
        string snapshotPayload,
        Guid? outboxId = null,
        string aggregateId = "aggregate-1")
    {
        IReadOnlyList<NCOutboxWrite> messages = outboxId == null
            ? []
            : new[]
            {
                new NCOutboxWrite(
                    outboxId.Value,
                    "living-city.test.notification",
                    "{\"kind\":\"test\"}"),
            };
        return new NCPersistentMutationRequest(
            Guid.NewGuid(),
            "living-city.test.command",
            "test",
            "owner-1",
            Guid.NewGuid(),
            "living-city.test.changed",
            "test-aggregate",
            aggregateId,
            expectedVersion,
            1,
            snapshotPayload,
            "{\"change\":1}",
            "{\"accepted\":true}",
            Guid.NewGuid(),
            null,
            epoch,
            time,
            messages);
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
