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
    public async Task MetaSingleAndBatchJournalAttestationsRejectTamperedStateMissingEventsAndTruncatedCheckpoint()
    {
        var db = GetDb(Pair.Server, out var connection, out var options);
        await using var databaseConnection = connection;
        var epoch = NCSessionEpoch.New();
        var clock = await db.StartNCActiveWorldSessionAsync(epoch);
        var firstRequest = CreateRequest(epoch, clock.Time, 0, "{\"text\":\"durable 😀\"}") with
        {
            AggregateType = "meta.project", EventPayload = "{\"business\":1,\"_ncMetaSnapshot\":\"caller-forged\"}",
        };
        Assert.That((await db.ApplyNCPersistentMutationAsync(firstRequest)).Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
        var baseline = await db.GetNCAggregateSnapshotAsync(firstRequest.AggregateType, firstRequest.AggregateId);
        Assert.That((await db.ApplyNCPersistentMutationAsync(firstRequest with
        {
            CommandId = Guid.NewGuid(), EventId = Guid.NewGuid(), ExpectedAggregateVersion = 1, SnapshotPayload = "{\"text\":\"second\"}",
        })).Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
        var batch = new NCPersistentBatchMutationRequest(Guid.NewGuid(), "test.meta-batch", "test", "owner-1",
            [new(Guid.NewGuid(), "meta.project.changed", "meta.project", firstRequest.AggregateId, 2, 1, "{\"text\":\"third\"}", "{}"),
                new(Guid.NewGuid(), "meta.device.changed", "meta.device", "other-device", 0, 1, "{}", "{}")],
            "{}", Guid.NewGuid(), null, epoch, clock.Time, []);
        Assert.That((await db.ApplyNCPersistentBatchMutationAsync(batch)).Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
        var current = await db.GetNCAggregateSnapshotAsync("meta.project", firstRequest.AggregateId);
        var tail = await db.GetNCPersistentEventPageAsync("meta.project", firstRequest.AggregateId, null, 4, 2);
        Assert.That(NCMetaJournalIntegrity.CanValidateTail(current, baseline, tail), Is.True,
            "Both single and ledger-style batch paths must bind the same contiguous state chain.");
        var newDevice = await db.GetNCAggregateSnapshotAsync("meta.device", "other-device");
        var deviceEvents = await db.GetNCPersistentEventPageAsync("meta.device", "other-device", null, 2, 1);
        Assert.That(NCMetaJournalIntegrity.CanValidateTail(newDevice, null, deviceEvents), Is.True);

        await using var context = new SqliteServerDbContext(options);
        var row = await context.NCAggregateSnapshots.SingleAsync(value => value.AggregateType == "meta.project" && value.AggregateId == firstRequest.AggregateId);
        row.Payload = "{\"injected\":true}";
        await context.SaveChangesAsync();
        var tampered = await db.GetNCAggregateSnapshotAsync("meta.project", firstRequest.AggregateId);
        Assert.That(NCMetaJournalIntegrity.CanValidateTail(tampered, baseline, tail), Is.False,
            "Changing live state while preserving its version and timestamp must not pass journal verification.");
        row.Payload = current.Payload;
        context.NCEventJournal.Remove(await context.NCEventJournal.SingleAsync(value => value.AggregateType == "meta.project" &&
            value.AggregateId == firstRequest.AggregateId && value.AggregateVersion == 2));
        await context.SaveChangesAsync();
        var missingMiddle = await db.GetNCPersistentEventPageAsync("meta.project", firstRequest.AggregateId, null, 4, 2);
        Assert.That(NCMetaJournalIntegrity.CanValidateTail(current, baseline, missingMiddle), Is.False);

        var snapshotId = Guid.NewGuid();
        var sections = new[]
        {
            new NCRoundSnapshotSectionWrite("meta-aggregates", "world", "city", 0, 1, 0, "[]", NCMetaJournalIntegrity.HashPayload("[]")),
            new NCRoundSnapshotSectionWrite("meta-aggregates", "world", "city", 1, 1, 0, "[]", NCMetaJournalIntegrity.HashPayload("[]")),
        };
        var inventorySections = sections.Select(value => new NCRoundSnapshotInventorySection(value.SectionType, value.OwnerType,
            value.OwnerId, value.ChunkIndex, value.FormatVersion, value.EntityCount, 2, value.Checksum)).ToArray();
        Assert.That((await db.CommitNCRoundSnapshotAsync(new(snapshotId, epoch.Value, clock.Time.Ticks, 1, "test", 0, "",
            NCRoundSnapshotInventoryIntegrity.Checksum(inventorySections), DateTime.UtcNow, sections, []))).Status,
            Is.EqualTo(NCRoundSnapshotCommitStatus.Committed));
        Assert.That(await db.GetNCRoundSnapshotInventoryAsync(snapshotId), Is.Not.Null);
        context.NCRoundSnapshotSections.Remove(await context.NCRoundSnapshotSections.SingleAsync(value =>
            value.SnapshotId == snapshotId && value.ChunkIndex == 1));
        await context.SaveChangesAsync();
        Assert.That(await db.GetNCRoundSnapshotInventoryAsync(snapshotId), Is.Null,
            "A missing final META chunk must invalidate the complete selected manifest inventory.");
    }

    [Test]
    public async Task PinnedSnapshotSectionsDoNotDriftWhenANewerWorldCommits()
    {
        var db = GetDb(Pair.Server);
        static string Hash(string value) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value)));
        async Task<Guid> Commit(string payload)
        {
            var epoch = NCSessionEpoch.New();
            var clock = await db.StartNCActiveWorldSessionAsync(epoch);
            var section = new NCRoundSnapshotSectionWrite("meta-devices", "world", "city", 0, 1, 1, payload, Hash(payload));
            var checksum = Hash($"{section.SectionType}\u001f{section.OwnerType}\u001f{section.OwnerId}\u001f{section.ChunkIndex}\u001f{section.FormatVersion}\u001f{section.EntityCount}\u001f{section.Checksum}");
            var id = Guid.NewGuid();
            var committed = await db.CommitNCRoundSnapshotAsync(new(id, epoch.Value, clock.Time.Ticks, 1,
                "test", 1, "", checksum, DateTime.UtcNow, [section], []));
            Assert.That(committed.Status, Is.EqualTo(NCRoundSnapshotCommitStatus.Committed));
            return id;
        }
        var selected = await Commit("[{\"id\":\"first-world\"}]");
        var latest = await Commit("[{\"id\":\"next-world\"}]");
        var oldSections = await db.GetNCRoundSnapshotSectionsAsync(selected, "meta-devices", "world", "city");
        var freshSections = await db.GetNCRoundSnapshotSectionsAsync(latest, "meta-devices", "world", "city");
        var missing = await db.GetNCRoundSnapshotSectionsAsync(Guid.NewGuid(), "meta-devices", "world", "city");
        Assert.Multiple(() =>
        {
            Assert.That(oldSections, Has.Count.EqualTo(1));
            Assert.That(freshSections, Has.Count.EqualTo(1));
            Assert.That(oldSections.Single().Payload, Does.Contain("first-world"));
            Assert.That(freshSections.Single().Payload, Does.Contain("next-world"));
            Assert.That(missing, Is.Empty);
        });
    }

    [Test]
    public async Task NCMetaContentAddressedBlobsAreDeduplicatedAndVerified()
    {
        var db = GetDb(Pair.Server);
        var bytes = System.Text.Encoding.UTF8.GetBytes("META: durable исходник 😀\n");
        var digest = await db.StoreNCMetaBlobAsync(bytes);
        var repeated = await db.StoreNCMetaBlobAsync(bytes.ToArray());
        var restored = await db.ReadNCMetaBlobAsync(digest);
        Assert.Multiple(() =>
        {
            Assert.That(digest, Has.Length.EqualTo(64));
            Assert.That(repeated, Is.EqualTo(digest));
            Assert.That(restored, Is.EqualTo(bytes));
            Assert.That(restored, Is.Not.SameAs(bytes));
        });
        Assert.That(await db.ReadNCMetaBlobAsync(new string('0', 64)), Is.Null);
        Assert.ThrowsAsync<ArgumentException>(async () => await db.ReadNCMetaBlobAsync("../../outside"));
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await db.StoreNCMetaBlobAsync(new byte[1024 * 1024 + 1]));
    }

    [Test]
    public async Task FingerprintedReceiptCannotBeReplayedWithDifferentPayloadOrScope()
    {
        var db = GetDb(Pair.Server);
        var epoch = NCSessionEpoch.New();
        var clock = await db.StartNCActiveWorldSessionAsync(epoch);
        var request = CreateRequest(epoch, clock.Time, 0, "{\"mode\":1}", Guid.NewGuid()) with
        {
            RequestFingerprint = new string('a', 64),
        };
        var first = await db.ApplyNCPersistentMutationAsync(request);
        var replay = await db.ApplyNCPersistentMutationAsync(request);
        var wrongPayload = await db.ApplyNCPersistentMutationAsync(request with
        {
            RequestFingerprint = new string('b', 64),
            SnapshotPayload = "{\"mode\":2}",
        });
        var wrongScope = await db.ApplyNCPersistentMutationAsync(request with { ScopeId = "another-owner" });
        var legacyReplay = await db.ApplyNCPersistentMutationAsync(request with { RequestFingerprint = null });
        Assert.Multiple(() =>
        {
            Assert.That(first.Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
            Assert.That(replay.Status, Is.EqualTo(NCPersistentMutationStatus.DuplicateCompleted));
            foreach (var conflict in new[] { wrongPayload, wrongScope, legacyReplay })
            {
                Assert.That(conflict.Status, Is.EqualTo(NCPersistentMutationStatus.VersionConflict));
                Assert.That(conflict.ResultPayload, Is.Null);
                Assert.That(conflict.EventId, Is.Null);
            }
        });
    }

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
        var newestPage = await db.GetNCAggregateSnapshotPageDescendingAsync("test-aggregate", null, 2);
        var olderPage = await db.GetNCAggregateSnapshotPageDescendingAsync(
            "test-aggregate",
            newestPage[^1].AggregateId,
            2);

        Assert.Multiple(() =>
        {
            Assert.That(firstPage.Select(value => value.AggregateId),
                Is.EqualTo(new[] { "aggregate-1", "aggregate-2" }));
            Assert.That(secondPage.Select(value => value.AggregateId),
                Is.EqualTo(new[] { "aggregate-3" }));
            Assert.That(newestPage.Select(value => value.AggregateId),
                Is.EqualTo(new[] { "aggregate-3", "aggregate-2" }));
            Assert.That(olderPage.Select(value => value.AggregateId),
                Is.EqualTo(new[] { "aggregate-1" }));
            Assert.That(firstPage.Concat(secondPage), Has.All.Matches<NCAggregateSnapshotData>(value =>
                value.AggregateType == "test-aggregate"));
        });
    }

    [Test]
    public async Task AggregateJournalUsesNewestFirstVersionKeysetAndEventFilter()
    {
        var db = GetDb(Pair.Server);
        var epoch = NCSessionEpoch.New();
        var clock = await db.StartNCActiveWorldSessionAsync(epoch);
        var aggregateId = $"journal-{Guid.NewGuid():N}";

        var first = CreateRequest(epoch, clock.Time, 0, "{\"state\":1}", aggregateId: aggregateId);
        var second = CreateRequest(epoch, clock.Time, 1, "{\"state\":2}", aggregateId: aggregateId) with
        {
            EventType = "living-city.test.other-changed",
        };
        var third = CreateRequest(epoch, clock.Time, 2, "{\"state\":3}", aggregateId: aggregateId);
        Assert.That((await db.ApplyNCPersistentMutationAsync(first)).Status,
            Is.EqualTo(NCPersistentMutationStatus.Applied));
        Assert.That((await db.ApplyNCPersistentMutationAsync(second)).Status,
            Is.EqualTo(NCPersistentMutationStatus.Applied));
        Assert.That((await db.ApplyNCPersistentMutationAsync(third)).Status,
            Is.EqualTo(NCPersistentMutationStatus.Applied));

        var all = await db.GetNCPersistentEventPageAsync("test-aggregate", aggregateId, null, null, 10);
        var changed = await db.GetNCPersistentEventPageAsync(
            "test-aggregate",
            aggregateId,
            "living-city.test.changed",
            null,
            1);
        var changedNext = await db.GetNCPersistentEventPageAsync(
            "test-aggregate",
            aggregateId,
            "living-city.test.changed",
            changed[^1].AggregateVersion,
            1);

        Assert.Multiple(() =>
        {
            Assert.That(all.Select(value => value.AggregateVersion), Is.EqualTo(new long[] { 3, 2, 1 }));
            Assert.That(changed.Select(value => value.AggregateVersion), Is.EqualTo(new long[] { 3 }));
            Assert.That(changedNext.Select(value => value.AggregateVersion), Is.EqualTo(new long[] { 1 }));
            Assert.That(all, Has.All.Matches<NCPersistentEventData>(value =>
                value.AggregateId == aggregateId && value.SessionEpoch == epoch));
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
        => GetDb(server, out _, out _);

    private static ServerDbSqlite GetDb(RobustIntegrationTest.ServerIntegrationInstance server,
        out SqliteConnection connection, out DbContextOptions<SqliteServerDbContext> contextOptions)
    {
        var configuration = server.ResolveDependency<IConfigurationManager>();
        var serialization = server.ResolveDependency<ISerializationManager>();
        var operationsLog = server.ResolveDependency<ILogManager>().GetSawmill("db.ops");
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>();
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        options.UseSqlite(connection);
        contextOptions = options.Options;
        var captured = contextOptions;
        return new ServerDbSqlite(() => captured, true, configuration, true, operationsLog, serialization);
    }
}
