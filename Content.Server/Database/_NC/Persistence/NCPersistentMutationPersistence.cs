// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Shared._NC.Persistence.Time;
using Microsoft.EntityFrameworkCore;

namespace Content.Server.Database;

public enum NCPersistentMutationStatus : byte
{
    Applied,
    DuplicateCompleted,
    VersionConflict,
    ClockRejected,
    DuplicateFailed,
    InProgress,
}

public sealed record NCOutboxWrite(
    Guid MessageId,
    string MessageType,
    string Payload,
    DateTime? AvailableAt = null);

public sealed record NCPersistentAggregateWrite(
    Guid EventId,
    string EventType,
    string AggregateType,
    string AggregateId,
    long ExpectedAggregateVersion,
    int SnapshotFormatVersion,
    string SnapshotPayload,
    string EventPayload);

public sealed record NCPersistentBatchOutboxWrite(
    Guid MessageId,
    Guid EventId,
    string MessageType,
    string Payload,
    DateTime? AvailableAt = null);

/// <summary>
/// A bounded set of aggregate writes committed under one command receipt and one serializable transaction.
/// Domain systems must validate gameplay rules before crossing this neutral persistence boundary.
/// </summary>
public sealed record NCPersistentBatchMutationRequest(
    Guid CommandId,
    string CommandType,
    string ScopeType,
    string ScopeId,
    IReadOnlyList<NCPersistentAggregateWrite> Writes,
    string? ResultPayload,
    Guid CorrelationId,
    Guid? CausationId,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime ActiveWorldTime,
    IReadOnlyList<NCPersistentBatchOutboxWrite> OutboxMessages);

public sealed record NCPersistentAggregateMutationData(
    string AggregateType,
    string AggregateId,
    Guid? EventId,
    long AggregateVersion);

public sealed record NCPersistentBatchMutationResult(
    NCPersistentMutationStatus Status,
    Guid CommandId,
    IReadOnlyList<NCPersistentAggregateMutationData> Aggregates,
    string? ResultPayload,
    string? Error);

/// <summary>
/// Read-only persisted command result used to answer retries before a domain re-reads mutable world state.
/// </summary>
public sealed record NCPersistentCommandResultData(
    Guid CommandId,
    string CommandType,
    string ScopeType,
    string ScopeId,
    NCPersistentMutationStatus Status,
    IReadOnlyList<NCPersistentAggregateMutationData> Aggregates,
    string? ResultPayload,
    string? Error);

/// <summary>
/// A domain executor supplies the already validated next snapshot and event. This boundary performs no
/// gameplay formula or skill resolution; it only commits identity, optimistic versioning and delivery metadata.
/// </summary>
public sealed record NCPersistentMutationRequest(
    Guid CommandId,
    string CommandType,
    string ScopeType,
    string ScopeId,
    Guid EventId,
    string EventType,
    string AggregateType,
    string AggregateId,
    long ExpectedAggregateVersion,
    int SnapshotFormatVersion,
    string SnapshotPayload,
    string EventPayload,
    string? ResultPayload,
    Guid CorrelationId,
    Guid? CausationId,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime ActiveWorldTime,
    IReadOnlyList<NCOutboxWrite> OutboxMessages);

public sealed record NCPersistentMutationResult(
    NCPersistentMutationStatus Status,
    Guid CommandId,
    Guid? EventId,
    long AggregateVersion,
    string? ResultPayload,
    string? Error);

public sealed record NCAggregateSnapshotData(
    string AggregateType,
    string AggregateId,
    long AggregateVersion,
    int FormatVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime ActiveWorldTime,
    string Payload,
    DateTime CreatedAt);

/// <summary>
/// Read-only journal projection used by bounded audit/history views. Domain code must still load and validate the
/// referenced aggregate snapshot before presenting authoritative state.
/// </summary>
public sealed record NCPersistentEventData(
    Guid EventId,
    string EventType,
    string AggregateType,
    string AggregateId,
    long AggregateVersion,
    Guid CorrelationId,
    Guid? CausationId,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime ActiveWorldTime,
    string Payload,
    DateTime CreatedAt);

public sealed record NCOutboxMessageData(
    Guid MessageId,
    Guid EventId,
    string MessageType,
    string Payload,
    int Attempts,
    DateTime AvailableAt,
    DateTime? ProcessedAt,
    string? LastError,
    DateTime CreatedAt);

public sealed record NCOutboxStatusData(
    int Pending,
    int Ready,
    int Exhausted,
    DateTime? OldestUnprocessedAt);

public partial interface IServerDbManager
{
    Task<NCPersistentMutationResult> ApplyNCPersistentMutationAsync(NCPersistentMutationRequest request);
    Task<NCPersistentBatchMutationResult> ApplyNCPersistentBatchMutationAsync(NCPersistentBatchMutationRequest request);
    Task<NCPersistentCommandResultData?> GetNCPersistentCommandResultAsync(Guid commandId);
    Task<NCAggregateSnapshotData?> GetNCAggregateSnapshotAsync(string aggregateType, string aggregateId);
    Task<IReadOnlyList<NCAggregateSnapshotData>> GetNCAggregateSnapshotPageAsync(
        string aggregateType,
        string? afterAggregateId,
        int limit);
    Task<IReadOnlyList<NCAggregateSnapshotData>> GetNCAggregateSnapshotPageDescendingAsync(
        string aggregateType,
        string? beforeAggregateId,
        int limit);
    Task<IReadOnlyList<NCPersistentEventData>> GetNCPersistentEventPageAsync(
        string aggregateType,
        string aggregateId,
        string? eventType,
        long? beforeAggregateVersion,
        int limit);
    Task<IReadOnlyList<NCOutboxMessageData>> GetPendingNCOutboxMessagesAsync(
        DateTime availableAt,
        int limit,
        int maximumAttempts = int.MaxValue);
    Task<NCOutboxMessageData?> GetNCOutboxMessageAsync(Guid messageId);
    Task<NCOutboxStatusData> GetNCOutboxStatusAsync(DateTime readyAt, int maximumAttempts);
    Task<bool> MarkNCOutboxMessageProcessedAsync(Guid messageId, DateTime processedAt);
    Task<bool> RecordNCOutboxMessageFailureAsync(Guid messageId, string error, DateTime retryAt);
    Task<bool> RequeueNCOutboxMessageAsync(Guid messageId, DateTime availableAt, bool resetAttempts);
}

public sealed partial class ServerDbManager
{
    public Task<NCPersistentMutationResult> ApplyNCPersistentMutationAsync(NCPersistentMutationRequest request)
    {
        DbWriteOpsMetric.Inc();
        return RunNCPersistentDbCommand(true, () => _db.ApplyNCPersistentMutationAsync(request));
    }

    public Task<NCPersistentBatchMutationResult> ApplyNCPersistentBatchMutationAsync(
        NCPersistentBatchMutationRequest request)
    {
        DbWriteOpsMetric.Inc();
        return RunNCPersistentDbCommand(true, () => _db.ApplyNCPersistentBatchMutationAsync(request));
    }

    public Task<NCAggregateSnapshotData?> GetNCAggregateSnapshotAsync(string aggregateType, string aggregateId)
    {
        DbReadOpsMetric.Inc();
        return RunNCPersistentDbCommand(false, () => _db.GetNCAggregateSnapshotAsync(aggregateType, aggregateId));
    }

    public Task<NCPersistentCommandResultData?> GetNCPersistentCommandResultAsync(Guid commandId)
    {
        DbReadOpsMetric.Inc();
        return RunNCPersistentDbCommand(false, () => _db.GetNCPersistentCommandResultAsync(commandId));
    }

    public Task<IReadOnlyList<NCAggregateSnapshotData>> GetNCAggregateSnapshotPageAsync(
        string aggregateType,
        string? afterAggregateId,
        int limit)
    {
        DbReadOpsMetric.Inc();
        return RunNCPersistentDbCommand(false,
            () => _db.GetNCAggregateSnapshotPageAsync(aggregateType, afterAggregateId, limit));
    }

    public Task<IReadOnlyList<NCAggregateSnapshotData>> GetNCAggregateSnapshotPageDescendingAsync(
        string aggregateType,
        string? beforeAggregateId,
        int limit)
    {
        DbReadOpsMetric.Inc();
        return RunNCPersistentDbCommand(false, () =>
            _db.GetNCAggregateSnapshotPageDescendingAsync(aggregateType, beforeAggregateId, limit));
    }

    public Task<IReadOnlyList<NCPersistentEventData>> GetNCPersistentEventPageAsync(
        string aggregateType,
        string aggregateId,
        string? eventType,
        long? beforeAggregateVersion,
        int limit)
    {
        DbReadOpsMetric.Inc();
        return RunNCPersistentDbCommand(false, () => _db.GetNCPersistentEventPageAsync(
            aggregateType,
            aggregateId,
            eventType,
            beforeAggregateVersion,
            limit));
    }

    public Task<IReadOnlyList<NCOutboxMessageData>> GetPendingNCOutboxMessagesAsync(
        DateTime availableAt,
        int limit,
        int maximumAttempts = int.MaxValue)
    {
        DbReadOpsMetric.Inc();
        return RunNCPersistentDbCommand(false,
            () => _db.GetPendingNCOutboxMessagesAsync(availableAt, limit, maximumAttempts));
    }

    public Task<NCOutboxMessageData?> GetNCOutboxMessageAsync(Guid messageId)
    {
        DbReadOpsMetric.Inc();
        return RunNCPersistentDbCommand(false, () => _db.GetNCOutboxMessageAsync(messageId));
    }

    public Task<NCOutboxStatusData> GetNCOutboxStatusAsync(DateTime readyAt, int maximumAttempts)
    {
        DbReadOpsMetric.Inc();
        return RunNCPersistentDbCommand(false, () => _db.GetNCOutboxStatusAsync(readyAt, maximumAttempts));
    }

    public Task<bool> MarkNCOutboxMessageProcessedAsync(Guid messageId, DateTime processedAt)
    {
        DbWriteOpsMetric.Inc();
        return RunNCPersistentDbCommand(true, () => _db.MarkNCOutboxMessageProcessedAsync(messageId, processedAt));
    }

    public Task<bool> RecordNCOutboxMessageFailureAsync(Guid messageId, string error, DateTime retryAt)
    {
        DbWriteOpsMetric.Inc();
        return RunNCPersistentDbCommand(true,
            () => _db.RecordNCOutboxMessageFailureAsync(messageId, error, retryAt));
    }

    public Task<bool> RequeueNCOutboxMessageAsync(Guid messageId, DateTime availableAt, bool resetAttempts)
    {
        DbWriteOpsMetric.Inc();
        return RunNCPersistentDbCommand(true,
            () => _db.RequeueNCOutboxMessageAsync(messageId, availableAt, resetAttempts));
    }
}

public abstract partial class ServerDbBase
{
    private const int NCPersistenceReadLimit = 500;
    private const int NCPersistenceBatchWriteLimit = 32;

    public async Task<NCPersistentCommandResultData?> GetNCPersistentCommandResultAsync(Guid commandId)
    {
        if (commandId == Guid.Empty)
            throw new ArgumentException("Command ID must be non-empty.", nameof(commandId));

        await using var db = await GetDb();
        var receipt = await db.DbContext.NCCommandReceipts
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.CommandId == commandId);
        if (receipt == null)
            return null;

        var envelope = DeserializeReceiptEnvelope(receipt.ResultPayload);
        var status = receipt.State switch
        {
            NCCommandReceiptState.Completed => NCPersistentMutationStatus.DuplicateCompleted,
            NCCommandReceiptState.Failed => NCPersistentMutationStatus.DuplicateFailed,
            _ => NCPersistentMutationStatus.InProgress,
        };
        return new NCPersistentCommandResultData(
            receipt.CommandId,
            receipt.CommandType,
            receipt.ScopeType,
            receipt.ScopeId,
            status,
            envelope?.AggregateVersions ?? [],
            envelope?.DomainResultPayload,
            receipt.Error);
    }

    public async Task<NCPersistentMutationResult> ApplyNCPersistentMutationAsync(
        NCPersistentMutationRequest request)
    {
        ValidateMutationRequest(request);

        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var existingReceipt = await db.DbContext.NCCommandReceipts
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.CommandId == request.CommandId);
        if (existingReceipt != null)
            return ReceiptToResult(existingReceipt);

        var snapshot = await db.DbContext.NCAggregateSnapshots
            .SingleOrDefaultAsync(value =>
                value.AggregateType == request.AggregateType &&
                value.AggregateId == request.AggregateId);
        var currentVersion = snapshot?.AggregateVersion ?? 0;
        var now = DateTime.UtcNow;
        var clock = await db.DbContext.NCActiveWorldClocks
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == NCActiveWorldClockId);
        if (clock == null ||
            !clock.RoundActive ||
            clock.SessionEpoch != request.SessionEpoch.Value ||
            (snapshot != null && request.ActiveWorldTime.Ticks < snapshot.ActiveWorldTicks))
        {
            const string error = "The mutation timestamp does not belong to the active world epoch.";
            db.DbContext.NCCommandReceipts.Add(CreateReceipt(
                request,
                NCCommandReceiptState.Failed,
                SerializeReceiptEnvelope(null, currentVersion, null),
                error,
                now));
            await db.DbContext.SaveChangesAsync();
            await transaction.CommitAsync();
            return new NCPersistentMutationResult(
                NCPersistentMutationStatus.ClockRejected,
                request.CommandId,
                null,
                currentVersion,
                null,
                error);
        }

        if (currentVersion != request.ExpectedAggregateVersion)
        {
            var error = $"Expected aggregate version {request.ExpectedAggregateVersion}, found {currentVersion}.";
            db.DbContext.NCCommandReceipts.Add(CreateReceipt(
                request,
                NCCommandReceiptState.Failed,
                SerializeReceiptEnvelope(null, currentVersion, null),
                error,
                now));
            await db.DbContext.SaveChangesAsync();
            await transaction.CommitAsync();
            return new NCPersistentMutationResult(
                NCPersistentMutationStatus.VersionConflict,
                request.CommandId,
                null,
                currentVersion,
                null,
                error);
        }

        if (currentVersion == long.MaxValue)
            throw new InvalidOperationException("The aggregate version space has been exhausted.");

        var nextVersion = currentVersion + 1;
        var receipt = CreateReceipt(
            request,
            NCCommandReceiptState.Started,
            null,
            null,
            now);
        db.DbContext.NCCommandReceipts.Add(receipt);
        db.DbContext.NCEventJournal.Add(new NCEventJournalEntry
        {
            EventId = request.EventId,
            EventType = request.EventType,
            AggregateType = request.AggregateType,
            AggregateId = request.AggregateId,
            ExpectedVersion = request.ExpectedAggregateVersion,
            AggregateVersion = nextVersion,
            CorrelationId = request.CorrelationId,
            CausationId = request.CausationId,
            SessionEpoch = request.SessionEpoch.Value,
            ActiveWorldTicks = request.ActiveWorldTime.Ticks,
            Payload = request.EventPayload,
            CreatedAt = now,
            AppliedAt = now,
        });

        if (snapshot == null)
        {
            snapshot = new NCAggregateSnapshot
            {
                AggregateType = request.AggregateType,
                AggregateId = request.AggregateId,
            };
            db.DbContext.NCAggregateSnapshots.Add(snapshot);
        }

        snapshot.AggregateVersion = nextVersion;
        snapshot.FormatVersion = request.SnapshotFormatVersion;
        snapshot.SessionEpoch = request.SessionEpoch.Value;
        snapshot.ActiveWorldTicks = request.ActiveWorldTime.Ticks;
        snapshot.Payload = request.SnapshotPayload;
        snapshot.CreatedAt = now;

        foreach (var message in request.OutboxMessages)
        {
            db.DbContext.NCOutboxMessages.Add(new NCOutboxMessage
            {
                MessageId = message.MessageId,
                EventId = request.EventId,
                MessageType = message.MessageType,
                Payload = message.Payload,
                Attempts = 0,
                AvailableAt = message.AvailableAt ?? now,
                CreatedAt = now,
            });
        }

        receipt.State = NCCommandReceiptState.Completed;
        receipt.CompletedAtWorldTicks = request.ActiveWorldTime.Ticks;
        receipt.ResultPayload = SerializeReceiptEnvelope(request.EventId, nextVersion, request.ResultPayload);
        receipt.UpdatedAt = now;

        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return new NCPersistentMutationResult(
            NCPersistentMutationStatus.Applied,
            request.CommandId,
            request.EventId,
            nextVersion,
            request.ResultPayload,
            null);
    }

    /// <summary>
    /// Atomically advances several aggregates. This is reserved for invariants such as single group membership,
    /// where eventual outbox projection would permit contradictory authoritative snapshots.
    /// </summary>
    public async Task<NCPersistentBatchMutationResult> ApplyNCPersistentBatchMutationAsync(
        NCPersistentBatchMutationRequest request)
    {
        ValidateBatchMutationRequest(request);

        await using var db = await GetDb();
        await using var transaction = await db.DbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var existingReceipt = await db.DbContext.NCCommandReceipts
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.CommandId == request.CommandId);
        if (existingReceipt != null)
            return ReceiptToBatchResult(existingReceipt);

        var aggregateTypes = request.Writes.Select(value => value.AggregateType).Distinct().ToArray();
        var aggregateIds = request.Writes.Select(value => value.AggregateId).Distinct().ToArray();
        var storedSnapshots = await db.DbContext.NCAggregateSnapshots
            .Where(value =>
                aggregateTypes.Contains(value.AggregateType) &&
                aggregateIds.Contains(value.AggregateId))
            .ToListAsync();
        var snapshots = storedSnapshots.ToDictionary(value => (value.AggregateType, value.AggregateId));
        var current = request.Writes
            .Select(write => new NCPersistentAggregateMutationData(
                write.AggregateType,
                write.AggregateId,
                null,
                snapshots.TryGetValue((write.AggregateType, write.AggregateId), out var snapshot)
                    ? snapshot.AggregateVersion
                    : 0))
            .ToArray();
        var now = DateTime.UtcNow;
        var clock = await db.DbContext.NCActiveWorldClocks
            .AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == NCActiveWorldClockId);
        if (clock == null ||
            !clock.RoundActive ||
            clock.SessionEpoch != request.SessionEpoch.Value ||
            storedSnapshots.Any(value => request.ActiveWorldTime.Ticks < value.ActiveWorldTicks))
        {
            const string error = "The batch timestamp does not belong to the active world epoch.";
            db.DbContext.NCCommandReceipts.Add(CreateBatchReceipt(
                request,
                NCCommandReceiptState.Failed,
                current,
                error,
                now));
            await db.DbContext.SaveChangesAsync();
            await transaction.CommitAsync();
            return new NCPersistentBatchMutationResult(
                NCPersistentMutationStatus.ClockRejected,
                request.CommandId,
                current,
                null,
                error);
        }

        var conflict = false;
        for (var i = 0; i < request.Writes.Count; i++)
        {
            if (request.Writes[i].ExpectedAggregateVersion != current[i].AggregateVersion)
            {
                conflict = true;
                break;
            }
        }

        if (conflict)
        {
            const string error = "One or more aggregate versions do not match the batch expectations.";
            db.DbContext.NCCommandReceipts.Add(CreateBatchReceipt(
                request,
                NCCommandReceiptState.Failed,
                current,
                error,
                now));
            await db.DbContext.SaveChangesAsync();
            await transaction.CommitAsync();
            return new NCPersistentBatchMutationResult(
                NCPersistentMutationStatus.VersionConflict,
                request.CommandId,
                current,
                null,
                error);
        }

        var receipt = CreateBatchReceipt(request, NCCommandReceiptState.Started, null, null, now);
        db.DbContext.NCCommandReceipts.Add(receipt);
        var applied = new NCPersistentAggregateMutationData[request.Writes.Count];
        for (var i = 0; i < request.Writes.Count; i++)
        {
            var write = request.Writes[i];
            if (write.ExpectedAggregateVersion == long.MaxValue)
                throw new InvalidOperationException("The aggregate version space has been exhausted.");

            var nextVersion = write.ExpectedAggregateVersion + 1;
            db.DbContext.NCEventJournal.Add(new NCEventJournalEntry
            {
                EventId = write.EventId,
                EventType = write.EventType,
                AggregateType = write.AggregateType,
                AggregateId = write.AggregateId,
                ExpectedVersion = write.ExpectedAggregateVersion,
                AggregateVersion = nextVersion,
                CorrelationId = request.CorrelationId,
                CausationId = request.CausationId,
                SessionEpoch = request.SessionEpoch.Value,
                ActiveWorldTicks = request.ActiveWorldTime.Ticks,
                Payload = write.EventPayload,
                CreatedAt = now,
                AppliedAt = now,
            });

            if (!snapshots.TryGetValue((write.AggregateType, write.AggregateId), out var snapshot))
            {
                snapshot = new NCAggregateSnapshot
                {
                    AggregateType = write.AggregateType,
                    AggregateId = write.AggregateId,
                };
                snapshots.Add((write.AggregateType, write.AggregateId), snapshot);
                db.DbContext.NCAggregateSnapshots.Add(snapshot);
            }

            snapshot.AggregateVersion = nextVersion;
            snapshot.FormatVersion = write.SnapshotFormatVersion;
            snapshot.SessionEpoch = request.SessionEpoch.Value;
            snapshot.ActiveWorldTicks = request.ActiveWorldTime.Ticks;
            snapshot.Payload = write.SnapshotPayload;
            snapshot.CreatedAt = now;
            applied[i] = new NCPersistentAggregateMutationData(
                write.AggregateType,
                write.AggregateId,
                write.EventId,
                nextVersion);
        }

        foreach (var message in request.OutboxMessages)
        {
            db.DbContext.NCOutboxMessages.Add(new NCOutboxMessage
            {
                MessageId = message.MessageId,
                EventId = message.EventId,
                MessageType = message.MessageType,
                Payload = message.Payload,
                Attempts = 0,
                AvailableAt = message.AvailableAt ?? now,
                CreatedAt = now,
            });
        }

        receipt.State = NCCommandReceiptState.Completed;
        receipt.CompletedAtWorldTicks = request.ActiveWorldTime.Ticks;
        receipt.ResultPayload = SerializeReceiptEnvelope(
            applied[0].EventId,
            applied[0].AggregateVersion,
            request.ResultPayload,
            applied);
        receipt.UpdatedAt = now;

        await db.DbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return new NCPersistentBatchMutationResult(
            NCPersistentMutationStatus.Applied,
            request.CommandId,
            applied,
            request.ResultPayload,
            null);
    }

    public async Task<NCAggregateSnapshotData?> GetNCAggregateSnapshotAsync(
        string aggregateType,
        string aggregateId)
    {
        ValidateIdentityPart(aggregateType, nameof(aggregateType), 64);
        ValidateIdentityPart(aggregateId, nameof(aggregateId), 128);

        await using var db = await GetDb();
        var snapshot = await db.DbContext.NCAggregateSnapshots
            .AsNoTracking()
            .SingleOrDefaultAsync(value =>
                value.AggregateType == aggregateType &&
                value.AggregateId == aggregateId);
        return snapshot == null
            ? null
            : new NCAggregateSnapshotData(
                snapshot.AggregateType,
                snapshot.AggregateId,
                snapshot.AggregateVersion,
                snapshot.FormatVersion,
                new NCSessionEpoch(snapshot.SessionEpoch),
                new NCActiveWorldTime(snapshot.ActiveWorldTicks),
                snapshot.Payload,
                snapshot.CreatedAt);
    }

    /// <summary>
    /// Reads a stable keyset page without materializing ECS entities or offset-scanning prior rows.
    /// </summary>
    public async Task<IReadOnlyList<NCAggregateSnapshotData>> GetNCAggregateSnapshotPageAsync(
        string aggregateType,
        string? afterAggregateId,
        int limit)
    {
        ValidateIdentityPart(aggregateType, nameof(aggregateType), 64);
        if (afterAggregateId != null)
            ValidateIdentityPart(afterAggregateId, nameof(afterAggregateId), 128);
        if (limit is < 1 or > NCPersistenceReadLimit)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, $"Limit must be 1..{NCPersistenceReadLimit}.");

        await using var db = await GetDb();
        var query = db.DbContext.NCAggregateSnapshots
            .AsNoTracking()
            .Where(value => value.AggregateType == aggregateType);
        if (afterAggregateId != null)
            query = query.Where(value => value.AggregateId.CompareTo(afterAggregateId) > 0);

        return await query
            .OrderBy(value => value.AggregateId)
            .Take(limit)
            .Select(value => new NCAggregateSnapshotData(
                value.AggregateType,
                value.AggregateId,
                value.AggregateVersion,
                value.FormatVersion,
                new NCSessionEpoch(value.SessionEpoch),
                new NCActiveWorldTime(value.ActiveWorldTicks),
                value.Payload,
                value.CreatedAt))
            .ToListAsync();
    }

    /// <summary>
    /// Reads a newest-key-first page. Time-ordered aggregate identities can use this without an unbounded scan.
    /// </summary>
    public async Task<IReadOnlyList<NCAggregateSnapshotData>> GetNCAggregateSnapshotPageDescendingAsync(
        string aggregateType,
        string? beforeAggregateId,
        int limit)
    {
        ValidateIdentityPart(aggregateType, nameof(aggregateType), 64);
        if (beforeAggregateId != null)
            ValidateIdentityPart(beforeAggregateId, nameof(beforeAggregateId), 128);
        if (limit is < 1 or > NCPersistenceReadLimit)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, $"Limit must be 1..{NCPersistenceReadLimit}.");

        await using var db = await GetDb();
        var query = db.DbContext.NCAggregateSnapshots
            .AsNoTracking()
            .Where(value => value.AggregateType == aggregateType);
        if (beforeAggregateId != null)
            query = query.Where(value => value.AggregateId.CompareTo(beforeAggregateId) < 0);

        return await query
            .OrderByDescending(value => value.AggregateId)
            .Take(limit)
            .Select(value => new NCAggregateSnapshotData(
                value.AggregateType,
                value.AggregateId,
                value.AggregateVersion,
                value.FormatVersion,
                new NCSessionEpoch(value.SessionEpoch),
                new NCActiveWorldTime(value.ActiveWorldTicks),
                value.Payload,
                value.CreatedAt))
            .ToListAsync();
    }

    /// <summary>
    /// Reads newest-first journal rows for exactly one aggregate. Version keyset pagination keeps the query bounded
    /// and uses the unique aggregate journal index instead of offset-scanning the global event table.
    /// </summary>
    public async Task<IReadOnlyList<NCPersistentEventData>> GetNCPersistentEventPageAsync(
        string aggregateType,
        string aggregateId,
        string? eventType,
        long? beforeAggregateVersion,
        int limit)
    {
        ValidateIdentityPart(aggregateType, nameof(aggregateType), 64);
        ValidateIdentityPart(aggregateId, nameof(aggregateId), 128);
        if (eventType != null)
            ValidateIdentityPart(eventType, nameof(eventType), 96);
        if (beforeAggregateVersion is < 1)
            throw new ArgumentOutOfRangeException(nameof(beforeAggregateVersion));
        if (limit is < 1 or > NCPersistenceReadLimit)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, $"Limit must be 1..{NCPersistenceReadLimit}.");

        await using var db = await GetDb();
        var query = db.DbContext.NCEventJournal
            .AsNoTracking()
            .Where(value => value.AggregateType == aggregateType && value.AggregateId == aggregateId);
        if (eventType != null)
            query = query.Where(value => value.EventType == eventType);
        if (beforeAggregateVersion != null)
            query = query.Where(value => value.AggregateVersion < beforeAggregateVersion.Value);

        return await query
            .OrderByDescending(value => value.AggregateVersion)
            .Take(limit)
            .Select(value => new NCPersistentEventData(
                value.EventId,
                value.EventType,
                value.AggregateType,
                value.AggregateId,
                value.AggregateVersion,
                value.CorrelationId,
                value.CausationId,
                new NCSessionEpoch(value.SessionEpoch),
                new NCActiveWorldTime(value.ActiveWorldTicks),
                value.Payload,
                value.CreatedAt))
            .ToListAsync();
    }

    public async Task<IReadOnlyList<NCOutboxMessageData>> GetPendingNCOutboxMessagesAsync(
        DateTime availableAt,
        int limit,
        int maximumAttempts = int.MaxValue)
    {
        if (limit is < 1 or > NCPersistenceReadLimit)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, $"Limit must be 1..{NCPersistenceReadLimit}.");
        if (maximumAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));

        await using var db = await GetDb();
        return await db.DbContext.NCOutboxMessages
            .AsNoTracking()
            .Where(value =>
                value.ProcessedAt == null &&
                value.Attempts < maximumAttempts &&
                value.AvailableAt <= availableAt)
            .OrderBy(value => value.AvailableAt)
            .ThenBy(value => value.MessageId)
            .Take(limit)
            .Select(value => new NCOutboxMessageData(
                value.MessageId,
                value.EventId,
                value.MessageType,
                value.Payload,
                value.Attempts,
                value.AvailableAt,
                value.ProcessedAt,
                value.LastError,
                value.CreatedAt))
            .ToListAsync();
    }

    public async Task<NCOutboxMessageData?> GetNCOutboxMessageAsync(Guid messageId)
    {
        if (messageId == Guid.Empty)
            throw new ArgumentException("Message ID must be non-empty.", nameof(messageId));

        await using var db = await GetDb();
        return await db.DbContext.NCOutboxMessages
            .AsNoTracking()
            .Where(value => value.MessageId == messageId)
            .Select(value => new NCOutboxMessageData(
                value.MessageId,
                value.EventId,
                value.MessageType,
                value.Payload,
                value.Attempts,
                value.AvailableAt,
                value.ProcessedAt,
                value.LastError,
                value.CreatedAt))
            .SingleOrDefaultAsync();
    }

    public async Task<NCOutboxStatusData> GetNCOutboxStatusAsync(DateTime readyAt, int maximumAttempts)
    {
        if (maximumAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));

        await using var db = await GetDb();
        var unprocessed = db.DbContext.NCOutboxMessages
            .AsNoTracking()
            .Where(value => value.ProcessedAt == null);
        var pending = await unprocessed.CountAsync(value => value.Attempts < maximumAttempts);
        var ready = await unprocessed.CountAsync(value =>
            value.Attempts < maximumAttempts && value.AvailableAt <= readyAt);
        var exhausted = await unprocessed.CountAsync(value => value.Attempts >= maximumAttempts);
        var oldest = await unprocessed
            .OrderBy(value => value.CreatedAt)
            .Select(value => (DateTime?) value.CreatedAt)
            .FirstOrDefaultAsync();
        return new NCOutboxStatusData(pending, ready, exhausted, oldest);
    }

    public async Task<bool> MarkNCOutboxMessageProcessedAsync(Guid messageId, DateTime processedAt)
    {
        if (messageId == Guid.Empty)
            throw new ArgumentException("Message ID must be non-empty.", nameof(messageId));

        await using var db = await GetDb();
        var updated = await db.DbContext.NCOutboxMessages
            .Where(value => value.MessageId == messageId && value.ProcessedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.ProcessedAt, processedAt)
                .SetProperty(value => value.LastError, (string?) null));
        return updated == 1;
    }

    public async Task<bool> RecordNCOutboxMessageFailureAsync(
        Guid messageId,
        string error,
        DateTime retryAt)
    {
        if (messageId == Guid.Empty)
            throw new ArgumentException("Message ID must be non-empty.", nameof(messageId));
        if (string.IsNullOrWhiteSpace(error) || error.Length > 2048)
            throw new ArgumentException("Error must contain 1..2048 characters.", nameof(error));

        await using var db = await GetDb();
        var updated = await db.DbContext.NCOutboxMessages
            .Where(value =>
                value.MessageId == messageId &&
                value.ProcessedAt == null &&
                value.Attempts < int.MaxValue)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.Attempts, value => value.Attempts + 1)
                .SetProperty(value => value.AvailableAt, retryAt)
                .SetProperty(value => value.LastError, error));
        return updated == 1;
    }

    /// <summary>
    /// Requeues an unprocessed message for operator reconciliation. Processed messages cannot be reopened because
    /// their external side effect may already have occurred.
    /// </summary>
    public async Task<bool> RequeueNCOutboxMessageAsync(
        Guid messageId,
        DateTime availableAt,
        bool resetAttempts)
    {
        if (messageId == Guid.Empty)
            throw new ArgumentException("Message ID must be non-empty.", nameof(messageId));

        await using var db = await GetDb();
        var query = db.DbContext.NCOutboxMessages
            .Where(value => value.MessageId == messageId && value.ProcessedAt == null);
        var updated = resetAttempts
            ? await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.Attempts, 0)
                .SetProperty(value => value.AvailableAt, availableAt)
                .SetProperty(value => value.LastError, (string?) null))
            : await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.AvailableAt, availableAt));
        return updated == 1;
    }

    private static NCCommandReceipt CreateReceipt(
        NCPersistentMutationRequest request,
        NCCommandReceiptState state,
        string? resultPayload,
        string? error,
        DateTime now)
    {
        return new NCCommandReceipt
        {
            CommandId = request.CommandId,
            CommandType = request.CommandType,
            ScopeType = request.ScopeType,
            ScopeId = request.ScopeId,
            State = state,
            SessionEpoch = request.SessionEpoch.Value,
            StartedAtWorldTicks = request.ActiveWorldTime.Ticks,
            CompletedAtWorldTicks = state == NCCommandReceiptState.Started
                ? null
                : request.ActiveWorldTime.Ticks,
            ResultPayload = resultPayload,
            Error = error,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static NCCommandReceipt CreateBatchReceipt(
        NCPersistentBatchMutationRequest request,
        NCCommandReceiptState state,
        IReadOnlyList<NCPersistentAggregateMutationData>? aggregates,
        string? error,
        DateTime now)
    {
        return new NCCommandReceipt
        {
            CommandId = request.CommandId,
            CommandType = request.CommandType,
            ScopeType = request.ScopeType,
            ScopeId = request.ScopeId,
            State = state,
            SessionEpoch = request.SessionEpoch.Value,
            StartedAtWorldTicks = request.ActiveWorldTime.Ticks,
            CompletedAtWorldTicks = state == NCCommandReceiptState.Started
                ? null
                : request.ActiveWorldTime.Ticks,
            ResultPayload = aggregates == null
                ? null
                : SerializeReceiptEnvelope(null, aggregates[0].AggregateVersion, null, aggregates),
            Error = error,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static NCPersistentMutationResult ReceiptToResult(NCCommandReceipt receipt)
    {
        var envelope = DeserializeReceiptEnvelope(receipt.ResultPayload);
        var status = receipt.State switch
        {
            NCCommandReceiptState.Completed => NCPersistentMutationStatus.DuplicateCompleted,
            NCCommandReceiptState.Failed => NCPersistentMutationStatus.DuplicateFailed,
            _ => NCPersistentMutationStatus.InProgress,
        };
        return new NCPersistentMutationResult(
            status,
            receipt.CommandId,
            envelope?.EventId,
            envelope?.AggregateVersion ?? 0,
            envelope?.DomainResultPayload,
            receipt.Error);
    }

    private static NCPersistentBatchMutationResult ReceiptToBatchResult(NCCommandReceipt receipt)
    {
        var envelope = DeserializeReceiptEnvelope(receipt.ResultPayload);
        var status = receipt.State switch
        {
            NCCommandReceiptState.Completed => NCPersistentMutationStatus.DuplicateCompleted,
            NCCommandReceiptState.Failed => NCPersistentMutationStatus.DuplicateFailed,
            _ => NCPersistentMutationStatus.InProgress,
        };
        return new NCPersistentBatchMutationResult(
            status,
            receipt.CommandId,
            envelope?.AggregateVersions ?? [],
            envelope?.DomainResultPayload,
            receipt.Error);
    }

    private static string SerializeReceiptEnvelope(
        Guid? eventId,
        long aggregateVersion,
        string? resultPayload,
        IReadOnlyList<NCPersistentAggregateMutationData>? aggregateVersions = null)
    {
        return JsonSerializer.Serialize(new NCReceiptEnvelope(
            eventId,
            aggregateVersion,
            resultPayload,
            aggregateVersions?.ToArray()));
    }

    private static NCReceiptEnvelope? DeserializeReceiptEnvelope(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
            return null;

        try
        {
            return JsonSerializer.Deserialize<NCReceiptEnvelope>(payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ValidateMutationRequest(NCPersistentMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CommandId == Guid.Empty)
            throw new ArgumentException("Command ID must be non-empty.", nameof(request));
        if (request.EventId == Guid.Empty)
            throw new ArgumentException("Event ID must be non-empty.", nameof(request));
        if (request.CorrelationId == Guid.Empty)
            throw new ArgumentException("Correlation ID must be non-empty.", nameof(request));
        if (!request.SessionEpoch.IsValid || !request.ActiveWorldTime.IsValid)
            throw new ArgumentException("A valid active-world timestamp is required.", nameof(request));
        if (request.ExpectedAggregateVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Expected aggregate version cannot be negative.");
        if (request.SnapshotFormatVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(request), "Snapshot format version must be positive.");

        ValidateIdentityPart(request.CommandType, nameof(request.CommandType), 96);
        ValidateIdentityPart(request.ScopeType, nameof(request.ScopeType), 64);
        ValidateIdentityPart(request.ScopeId, nameof(request.ScopeId), 128);
        ValidateIdentityPart(request.EventType, nameof(request.EventType), 96);
        ValidateIdentityPart(request.AggregateType, nameof(request.AggregateType), 64);
        ValidateIdentityPart(request.AggregateId, nameof(request.AggregateId), 128);
        ArgumentNullException.ThrowIfNull(request.SnapshotPayload);
        ArgumentNullException.ThrowIfNull(request.EventPayload);
        ArgumentNullException.ThrowIfNull(request.OutboxMessages);

        var messageIds = new HashSet<Guid>();
        foreach (var message in request.OutboxMessages)
        {
            if (message.MessageId == Guid.Empty || !messageIds.Add(message.MessageId))
                throw new ArgumentException("Outbox message IDs must be non-empty and unique.", nameof(request));
            ValidateIdentityPart(message.MessageType, nameof(message.MessageType), 96);
            ArgumentNullException.ThrowIfNull(message.Payload);
        }
    }

    private static void ValidateBatchMutationRequest(NCPersistentBatchMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CommandId == Guid.Empty)
            throw new ArgumentException("Command ID must be non-empty.", nameof(request));
        if (request.CorrelationId == Guid.Empty)
            throw new ArgumentException("Correlation ID must be non-empty.", nameof(request));
        if (!request.SessionEpoch.IsValid || !request.ActiveWorldTime.IsValid)
            throw new ArgumentException("A valid active-world timestamp is required.", nameof(request));

        ValidateIdentityPart(request.CommandType, nameof(request.CommandType), 96);
        ValidateIdentityPart(request.ScopeType, nameof(request.ScopeType), 64);
        ValidateIdentityPart(request.ScopeId, nameof(request.ScopeId), 128);
        ArgumentNullException.ThrowIfNull(request.Writes);
        ArgumentNullException.ThrowIfNull(request.OutboxMessages);
        if (request.Writes.Count is < 1 or > NCPersistenceBatchWriteLimit)
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"A persistence batch must contain 1..{NCPersistenceBatchWriteLimit} aggregate writes.");

        var aggregateKeys = new HashSet<(string Type, string Id)>();
        var eventIds = new HashSet<Guid>();
        foreach (var write in request.Writes)
        {
            if (write.EventId == Guid.Empty || !eventIds.Add(write.EventId))
                throw new ArgumentException("Batch event IDs must be non-empty and unique.", nameof(request));
            if (write.ExpectedAggregateVersion < 0)
                throw new ArgumentOutOfRangeException(nameof(request), "Expected aggregate versions cannot be negative.");
            if (write.SnapshotFormatVersion < 1)
                throw new ArgumentOutOfRangeException(nameof(request), "Snapshot format versions must be positive.");
            ValidateIdentityPart(write.EventType, nameof(write.EventType), 96);
            ValidateIdentityPart(write.AggregateType, nameof(write.AggregateType), 64);
            ValidateIdentityPart(write.AggregateId, nameof(write.AggregateId), 128);
            ArgumentNullException.ThrowIfNull(write.SnapshotPayload);
            ArgumentNullException.ThrowIfNull(write.EventPayload);
            if (!aggregateKeys.Add((write.AggregateType, write.AggregateId)))
                throw new ArgumentException("A batch may advance each aggregate at most once.", nameof(request));
        }

        var messageIds = new HashSet<Guid>();
        foreach (var message in request.OutboxMessages)
        {
            if (message.MessageId == Guid.Empty || !messageIds.Add(message.MessageId))
                throw new ArgumentException("Outbox message IDs must be non-empty and unique.", nameof(request));
            if (!eventIds.Contains(message.EventId))
                throw new ArgumentException("Every batch outbox message must reference an event in the same batch.", nameof(request));
            ValidateIdentityPart(message.MessageType, nameof(message.MessageType), 96);
            ArgumentNullException.ThrowIfNull(message.Payload);
        }
    }

    private static void ValidateIdentityPart(string value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            throw new ArgumentException($"Value must contain 1..{maxLength} characters.", parameterName);
    }

    private sealed record NCReceiptEnvelope(
        Guid? EventId,
        long AggregateVersion,
        string? DomainResultPayload,
        NCPersistentAggregateMutationData[]? AggregateVersions = null);
}
