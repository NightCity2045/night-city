// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Identity;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Social;

public sealed partial class NCMemoryRegistrySystem
{
    /// <summary>
    /// Records that cognition actually consumed canonical memories. The caller must pass the exact bounded context it
    /// used; merely querying memory does not make it more persistent.
    /// </summary>
    public async Task<NCMemoryMutationResult> RecordRecallAsync(NCRecordMemoryRecallRequest request)
    {
        if (!TryGetPolicy(out var policy) || !ValidRecallRequest(request, policy) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(_clock.IsReady ? NCMemoryMutationStatus.Rejected : NCMemoryMutationStatus.ClockUnavailable,
                "The recall request, policy, or active-world clock is invalid.");
        }

        var bookId = NCPersistentIdentity.MemoryBook(request.Owner);
        var applicationId = NCPersistentIdentity.MemoryRecallApplication(bookId, request.RecallEventId);
        var scopeId = applicationId.ToString("D");
        var commandType = "living-city.memory.recall";
        var fingerprint = RecallFingerprint(request);
        InitialCommandState commandState;
        try
        {
            commandState = await ValidateInitialCommandAsync(request.CommandId, commandType, scopeId);
            if (!await ResolvePartiesAsync(request.Owner, request.Initiator, default, []))
            {
                return Reject(NCMemoryMutationStatus.PartyUnavailable,
                    "Memory owner or recall initiator is unavailable.");
            }
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to prepare memory recall {request.RecallEventId}: {exception}");
            return Reject(NCMemoryMutationStatus.StorageFailure, exception.Message);
        }

        if (commandState == InitialCommandState.Collision)
            return Reject(NCMemoryMutationStatus.Rejected, "The command ID belongs to another operation.");
        if (commandState == InitialCommandState.InProgress)
            return Reject(NCMemoryMutationStatus.StorageFailure, "The recall command is still in progress.");

        var gate = Gate(bookId);
        await gate.WaitAsync();
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var application = await GetApplicationAsync(RecallApplicationAggregateType, applicationId);
                if (application != null)
                {
                    return await ReplayApplicationAsync(application, RecallOperation, bookId, request.Owner,
                        request.RecallEventId, null, fingerprint, request.Initiator);
                }
                if (attempt == 0 && commandState == InitialCommandState.Completed)
                {
                    return Reject(NCMemoryMutationStatus.StorageFailure,
                        "The completed recall command is missing its application marker.");
                }

                var current = await GetAsync(request.Owner, true);
                if (current == null)
                    return Reject(NCMemoryMutationStatus.Rejected, "The memory book does not exist.");

                var memoryIds = request.MemoryIds.ToHashSet();
                var summaryIds = request.SummaryIds.ToHashSet();
                var selectedEpisodes = current.Snapshot.Episodes
                    .Where(episode => memoryIds.Contains(episode.MemoryId)).ToArray();
                var selectedSummaries = current.Snapshot.Summaries
                    .Where(summary => summaryIds.Contains(summary.SummaryId)).ToArray();
                if (selectedEpisodes.Length != memoryIds.Count || selectedSummaries.Length != summaryIds.Count ||
                    selectedEpisodes.Any(episode => Expired(episode, now)) ||
                    selectedSummaries.Any(summary => summary.ExpiresAt <= now))
                {
                    return Reject(NCMemoryMutationStatus.Rejected,
                        "The recall contains a missing, forgotten, consolidated, or expired memory.");
                }

                var bookEventId = Guid.NewGuid();
                var applicationEventId = Guid.NewGuid();
                var nextSequence = checked(current.Snapshot.DataVersion + 1);
                var episodes = current.Snapshot.Episodes.Select(episode => memoryIds.Contains(episode.MemoryId)
                    ? episode with
                    {
                        LastRecalledAt = now,
                        RecallCount = checked(episode.RecallCount + 1),
                        LastChangedSequence = nextSequence,
                        DataVersion = checked(episode.DataVersion + 1),
                        LastAppliedEventId = bookEventId,
                    }
                    : episode).ToList();
                var summaries = current.Snapshot.Summaries.Select(summary => summaryIds.Contains(summary.SummaryId)
                    ? summary with
                    {
                        LastRecalledAt = now,
                        RecallCount = checked(summary.RecallCount + 1),
                        LastChangedSequence = nextSequence,
                        DataVersion = checked(summary.DataVersion + 1),
                        LastAppliedEventId = bookEventId,
                    }
                    : summary).ToList();
                episodes.Sort(EpisodeOrder);
                summaries.Sort(SummaryOrder);

                var next = current.Snapshot with
                {
                    Episodes = episodes,
                    Summaries = summaries,
                    DataVersion = nextSequence,
                    LastAppliedEventId = bookEventId,
                };
                var nextVersion = checked(current.AggregateVersion + 1);
                var marker = new MemoryApplicationSnapshot(
                    applicationId,
                    RecallOperation,
                    bookId,
                    request.Owner,
                    request.RecallEventId,
                    null,
                    fingerprint,
                    request.Initiator,
                    nextVersion,
                    now,
                    applicationEventId);
                var receipt = new MemoryBookReceipt(next, nextVersion, epoch, now);
                var commandId = attempt == 0 && commandState == InitialCommandState.Available
                    ? request.CommandId
                    : Guid.NewGuid();
                var result = await _database.ApplyNCPersistentBatchMutationAsync(
                    new NCPersistentBatchMutationRequest(
                        commandId,
                        commandType,
                        BookScopeType,
                        scopeId,
                        [
                            new NCPersistentAggregateWrite(
                                bookEventId,
                                "living-city.memory.recalled",
                                BookAggregateType,
                                BookKey(bookId),
                                current.AggregateVersion,
                                SnapshotFormatVersion,
                                JsonSerializer.Serialize(next, JsonOptions),
                                JsonSerializer.Serialize(new MemoryRecalledEvent(
                                    request.Owner,
                                    request.RecallEventId,
                                    request.Initiator,
                                    request.MemoryIds.OrderBy(id => id.Value).ToArray(),
                                    request.SummaryIds.OrderBy(id => id.Value).ToArray(),
                                    request.AuditSource.Trim()), JsonOptions)),
                            ApplicationWrite(
                                applicationEventId,
                                "living-city.memory.recall-application-recorded",
                                RecallApplicationAggregateType,
                                marker),
                        ],
                        JsonSerializer.Serialize(receipt, JsonOptions),
                        request.CorrelationId,
                        request.CausationId,
                        epoch,
                        now,
                        []));

                if (result.Status is NCPersistentMutationStatus.Applied or
                    NCPersistentMutationStatus.DuplicateCompleted)
                {
                    var persisted = await GetAsync(request.Owner, true);
                    if (persisted == null || persisted.AggregateVersion < nextVersion)
                    {
                        return Reject(NCMemoryMutationStatus.StorageFailure,
                            "The recalled memory book could not be reloaded.");
                    }
                    return new NCMemoryMutationResult(
                        result.Status == NCPersistentMutationStatus.Applied
                            ? NCMemoryMutationStatus.Applied
                            : NCMemoryMutationStatus.Duplicate,
                        persisted,
                        null);
                }

                if (result.Status is NCPersistentMutationStatus.VersionConflict or
                    NCPersistentMutationStatus.DuplicateFailed)
                {
                    commandState = InitialCommandState.Failed;
                    continue;
                }

                return Reject(result.Status == NCPersistentMutationStatus.ClockRejected
                        ? NCMemoryMutationStatus.ClockUnavailable
                        : NCMemoryMutationStatus.StorageFailure,
                    result.Error ?? "The memory recall was not committed.");
            }

            return Reject(NCMemoryMutationStatus.VersionConflict,
                "The recall exceeded its optimistic retry limit.");
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to record memory recall {request.RecallEventId} for {request.Owner}: {exception}");
            return Reject(NCMemoryMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed record MemoryRecalledEvent(
        NCPartyRef Owner,
        Guid RecallEventId,
        NCPartyRef Initiator,
        IReadOnlyList<NCMemoryId> MemoryIds,
        IReadOnlyList<NCMemorySummaryId> SummaryIds,
        string AuditSource);
}
