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
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Social;

public sealed partial class NCMemoryRegistrySystem
{
    public async Task<NCMemoryMutationResult> ConsolidateAsync(NCConsolidateMemoryRequest request)
    {
        if (!ValidConsolidationRequest(request) || !TryGetPolicy(out var policy) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(_clock.IsReady ? NCMemoryMutationStatus.Rejected : NCMemoryMutationStatus.ClockUnavailable,
                "The consolidation request, policy, or active-world clock is invalid.");
        }

        var bookId = NCPersistentIdentity.MemoryBook(request.Owner);
        var applicationId = NCPersistentIdentity.MemoryConsolidationApplication(
            bookId, request.ConsolidationEventId);
        var scopeId = applicationId.ToString("D");
        var commandType = "living-city.memory.consolidate";
        var fingerprint = ConsolidationFingerprint(request);
        InitialCommandState commandState;
        try
        {
            commandState = await ValidateInitialCommandAsync(request.CommandId, commandType, scopeId);
            if (!await ResolvePartiesAsync(request.Owner, request.Initiator, default, []))
            {
                return Reject(NCMemoryMutationStatus.PartyUnavailable,
                    "Memory owner or consolidation initiator is unavailable.");
            }
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to prepare memory consolidation {request.ConsolidationEventId}: {exception}");
            return Reject(NCMemoryMutationStatus.StorageFailure, exception.Message);
        }

        if (commandState == InitialCommandState.Collision)
            return Reject(NCMemoryMutationStatus.Rejected, "The command ID belongs to another operation.");
        if (commandState == InitialCommandState.InProgress)
            return Reject(NCMemoryMutationStatus.StorageFailure, "The consolidation command is still in progress.");

        var gate = Gate(bookId);
        await gate.WaitAsync();
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var application = await GetApplicationAsync(
                    ConsolidationApplicationAggregateType, applicationId);
                if (application != null)
                {
                    return await ReplayApplicationAsync(application, ConsolidationOperation, bookId, request.Owner,
                        request.ConsolidationEventId, null, fingerprint, request.Initiator);
                }
                if (attempt == 0 && commandState == InitialCommandState.Completed)
                {
                    return Reject(NCMemoryMutationStatus.StorageFailure,
                        "The completed consolidation command is missing its application marker.");
                }

                var current = await GetAsync(request.Owner, true);
                var bookEventId = Guid.NewGuid();
                var applicationEventId = Guid.NewGuid();
                var nextSequence = checked((current?.Snapshot.DataVersion ?? 0) + 1);
                var maintenance = EmptyMaintenance(current);
                if (current != null && !TryMaintain(
                        request.Owner,
                        current.Snapshot.Episodes.ToList(),
                        current.Snapshot.Summaries.ToList(),
                        now,
                        policy,
                        true,
                        nextSequence,
                        bookEventId,
                        out maintenance,
                        out var error))
                {
                    return Reject(NCMemoryMutationStatus.Rejected, error!);
                }

                NCMemoryBookSnapshot? next = null;
                var nextVersion = current?.AggregateVersion ?? 0;
                if (current != null && maintenance.Changed)
                {
                    next = new NCMemoryBookSnapshot(
                        bookId,
                        request.Owner,
                        maintenance.Episodes,
                        maintenance.Summaries,
                        current.Snapshot.TotalRemembered,
                        checked(current.Snapshot.TotalForgotten + maintenance.ForgottenEpisodes),
                        checked(current.Snapshot.TotalConsolidated + maintenance.ConsolidatedEpisodes),
                        nextSequence,
                        bookEventId);
                    nextVersion = checked(current.AggregateVersion + 1);
                }

                var marker = new MemoryApplicationSnapshot(
                    applicationId,
                    ConsolidationOperation,
                    bookId,
                    request.Owner,
                    request.ConsolidationEventId,
                    null,
                    fingerprint,
                    request.Initiator,
                    nextVersion,
                    now,
                    applicationEventId);
                var receipt = new MemoryBookReceipt(next ?? current?.Snapshot, nextVersion, epoch, now);
                var writes = new List<NCPersistentAggregateWrite>(2);
                if (next != null)
                {
                    writes.Add(new NCPersistentAggregateWrite(
                        bookEventId,
                        "living-city.memory.consolidated",
                        BookAggregateType,
                        BookKey(bookId),
                        current!.AggregateVersion,
                        SnapshotFormatVersion,
                        JsonSerializer.Serialize(next, JsonOptions),
                        JsonSerializer.Serialize(new MemoryConsolidatedEvent(
                            request.Owner,
                            request.ConsolidationEventId,
                            request.Initiator,
                            maintenance.RemovedMemoryIds,
                            maintenance.CreatedSummaryIds,
                            maintenance.RemovedSummaryIds,
                            maintenance.ForgottenEpisodes,
                            maintenance.ConsolidatedEpisodes,
                            request.AuditSource.Trim()), JsonOptions)));
                }
                writes.Add(ApplicationWrite(
                    applicationEventId,
                    "living-city.memory.consolidation-application-recorded",
                    ConsolidationApplicationAggregateType,
                    marker));

                var commandId = attempt == 0 && commandState == InitialCommandState.Available
                    ? request.CommandId
                    : Guid.NewGuid();
                var result = await _database.ApplyNCPersistentBatchMutationAsync(
                    new NCPersistentBatchMutationRequest(
                        commandId,
                        commandType,
                        BookScopeType,
                        scopeId,
                        writes,
                        JsonSerializer.Serialize(receipt, JsonOptions),
                        request.CorrelationId,
                        request.CausationId,
                        epoch,
                        now,
                        []));

                if (result.Status is NCPersistentMutationStatus.Applied or
                    NCPersistentMutationStatus.DuplicateCompleted)
                {
                    var persisted = next == null ? current : await GetAsync(request.Owner, true);
                    if (next != null && (persisted == null || persisted.AggregateVersion < nextVersion))
                    {
                        return Reject(NCMemoryMutationStatus.StorageFailure,
                            "The consolidated memory book could not be reloaded.");
                    }
                    return new NCMemoryMutationResult(
                        result.Status == NCPersistentMutationStatus.DuplicateCompleted
                            ? NCMemoryMutationStatus.Duplicate
                            : next == null ? NCMemoryMutationStatus.NoChange : NCMemoryMutationStatus.Applied,
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
                    result.Error ?? "The consolidation was not committed.");
            }

            return Reject(NCMemoryMutationStatus.VersionConflict,
                "The consolidation exceeded its optimistic retry limit.");
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to consolidate memory for {request.Owner}: {exception}");
            return Reject(NCMemoryMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool TryMaintain(
        NCPartyRef owner,
        List<NCEpisodicMemorySnapshot> episodes,
        List<NCConsolidatedMemorySnapshot> summaries,
        NCActiveWorldTime now,
        NCMemoryPolicyPrototype policy,
        bool forceTarget,
        int nextSequence,
        Guid eventId,
        out MemoryMaintenanceResult result,
        out string? error)
    {
        error = null;
        var removedSummaryIds = summaries
            .Where(summary => summary.ExpiresAt <= now)
            .Select(summary => summary.SummaryId)
            .ToHashSet();
        summaries.RemoveAll(summary => removedSummaryIds.Contains(summary.SummaryId));

        var selected = episodes.Where(episode => Expired(episode, now)).ToList();
        var selectedIds = selected.Select(episode => episode.MemoryId).ToHashSet();
        var requiredRemovalIds = selectedIds.ToHashSet();

        // An explicit maintenance pass consolidates every complete eligible group. This keeps the command useful for
        // scheduled maintenance even when the book has not reached its hard capacity.
        if (forceTarget)
        {
            foreach (var group in episodes
                         .Where(episode => !selectedIds.Contains(episode.MemoryId) && !episode.Critical &&
                             episode.RetentionClass != NCMemoryRetentionClass.Permanent && episode.CanConsolidate)
                         .GroupBy(episode => episode.ConsolidationKey, StringComparer.Ordinal)
                         .Where(group => group.Count() >= policy.MinimumConsolidationGroupSize))
            {
                selected.AddRange(group);
                selectedIds.UnionWith(group.Select(episode => episode.MemoryId));
            }
        }

        // Crossing the hard limit triggers a complete bounded maintenance pass down to the configured target.
        var target = forceTarget || episodes.Count > policy.MaximumEpisodesPerOwner
            ? policy.TargetEpisodesAfterConsolidation
            : policy.MaximumEpisodesPerOwner;
        var remainingAfterExpiry = episodes.Count - selected.Count;
        if (remainingAfterExpiry > target)
        {
            var needed = remainingAfterExpiry - target;
            var capacityCandidates = episodes
                .Where(episode => !selectedIds.Contains(episode.MemoryId) && !episode.Critical &&
                    episode.RetentionClass != NCMemoryRetentionClass.Permanent)
                .OrderBy(episode => episode.Salience)
                .ThenBy(episode => episode.Confidence)
                .ThenBy(episode => episode.LastRecalledAt ?? episode.CreatedAt)
                .ThenBy(episode => episode.MemoryId.Value)
                .Take(needed)
                .ToArray();
            selected.AddRange(capacityCandidates);
            selectedIds.UnionWith(capacityCandidates.Select(episode => episode.MemoryId));
            requiredRemovalIds.UnionWith(capacityCandidates.Select(episode => episode.MemoryId));
        }

        if (!forceTarget && episodes.Count - selectedIds.Count > policy.MaximumEpisodesPerOwner)
        {
            result = default!;
            error = "The memory book is full of permanent or critical episodes; no record may be silently removed.";
            return false;
        }

        var proposed = BuildSummaries(owner, selected.Where(episode => episode.CanConsolidate).ToArray(),
            now, policy, nextSequence, eventId);
        summaries.AddRange(proposed);
        var summaryOverflow = Math.Max(0, summaries.Count - policy.MaximumSummariesPerOwner);
        if (summaryOverflow > 0)
        {
            foreach (var summary in summaries
                         .OrderBy(value => value.MaximumSalience)
                         .ThenBy(value => value.MinimumConfidence)
                         .ThenBy(value => value.LastOccurredAt)
                         .ThenBy(value => value.SummaryId.Value)
                         .Take(summaryOverflow)
                         .ToArray())
            {
                removedSummaryIds.Add(summary.SummaryId);
            }
            summaries.RemoveAll(summary => removedSummaryIds.Contains(summary.SummaryId));
        }

        var retainedSummaryIds = summaries.Select(summary => summary.SummaryId).ToHashSet();
        var retainedProposed = proposed.Where(summary => retainedSummaryIds.Contains(summary.SummaryId)).ToArray();
        var consolidatedIds = retainedProposed.SelectMany(summary => summary.SourceMemoryIds).ToHashSet();
        var removedMemoryIds = requiredRemovalIds.Concat(consolidatedIds).ToHashSet();
        episodes.RemoveAll(episode => removedMemoryIds.Contains(episode.MemoryId));
        episodes.Sort(EpisodeOrder);
        summaries.Sort(SummaryOrder);
        result = new MemoryMaintenanceResult(
            episodes,
            summaries,
            requiredRemovalIds.Count(id => !consolidatedIds.Contains(id)),
            consolidatedIds.Count,
            removedMemoryIds.OrderBy(id => id.Value).ToArray(),
            retainedProposed.Select(summary => summary.SummaryId).OrderBy(id => id.Value).ToArray(),
            removedSummaryIds.OrderBy(id => id.Value).ToArray(),
            removedMemoryIds.Count > 0 || removedSummaryIds.Count > 0 || retainedProposed.Length > 0);
        return true;
    }

    private static IReadOnlyList<NCConsolidatedMemorySnapshot> BuildSummaries(
        NCPartyRef owner,
        IReadOnlyList<NCEpisodicMemorySnapshot> candidates,
        NCActiveWorldTime now,
        NCMemoryPolicyPrototype policy,
        int nextSequence,
        Guid eventId)
    {
        var result = new List<NCConsolidatedMemorySnapshot>();
        foreach (var group in candidates
                     .OrderBy(memory => memory.ConsolidationKey, StringComparer.Ordinal)
                     .ThenBy(memory => memory.CreatedAt)
                     .ThenBy(memory => memory.MemoryId.Value)
                     .GroupBy(memory => memory.ConsolidationKey, StringComparer.Ordinal))
        {
            var batch = new List<NCEpisodicMemorySnapshot>();
            foreach (var memory in group)
            {
                if (batch.Count > 0 && !CanAppend(batch, memory, policy))
                {
                    AddSummary(result, owner, group.Key, batch, now, policy, nextSequence, eventId);
                    batch.Clear();
                }
                batch.Add(memory);
            }
            AddSummary(result, owner, group.Key, batch, now, policy, nextSequence, eventId);
        }
        return result;
    }

    private static bool CanAppend(
        IReadOnlyList<NCEpisodicMemorySnapshot> batch,
        NCEpisodicMemorySnapshot memory,
        NCMemoryPolicyPrototype policy)
    {
        if (batch.Count >= policy.MaximumSourceMemoriesPerSummary)
            return false;
        return batch.SelectMany(value => value.SourceWorldEventIds).Concat(memory.SourceWorldEventIds)
                   .Distinct().Count() <= policy.MaximumSourceEventsPerSummary &&
               batch.SelectMany(value => value.Participants).Concat(memory.Participants).Distinct().Count() <=
               policy.MaximumParticipantsPerSummary &&
               batch.SelectMany(value => value.EmotionalTagKeys).Concat(memory.EmotionalTagKeys).Distinct().Count() <=
               policy.MaximumEmotionalTagsPerSummary;
    }

    private static void AddSummary(
        ICollection<NCConsolidatedMemorySnapshot> result,
        NCPartyRef owner,
        string consolidationKey,
        IReadOnlyList<NCEpisodicMemorySnapshot> batch,
        NCActiveWorldTime now,
        NCMemoryPolicyPrototype policy,
        int nextSequence,
        Guid eventId)
    {
        if (batch.Count < policy.MinimumConsolidationGroupSize)
            return;
        var memoryIds = batch.Select(memory => memory.MemoryId).OrderBy(id => id.Value).ToArray();
        result.Add(new NCConsolidatedMemorySnapshot(
            NCPersistentIdentity.MemorySummary(owner, memoryIds),
            owner,
            consolidationKey,
            memoryIds,
            batch.SelectMany(memory => memory.SourceWorldEventIds).Distinct().Order().ToArray(),
            batch.Select(memory => memory.EventTypeKey).Distinct().Order(StringComparer.Ordinal).ToArray(),
            batch.SelectMany(memory => memory.Participants).Distinct()
                .OrderBy(party => party.ToString(), StringComparer.Ordinal).ToArray(),
            batch.Select(memory => memory.Source.Kind).Distinct().Order().ToArray(),
            batch.SelectMany(memory => memory.EmotionalTagKeys).Distinct().Order(StringComparer.Ordinal).ToArray(),
            batch.Count,
            batch.Max(memory => memory.Salience),
            batch.Min(memory => memory.Confidence),
            batch.Max(memory => memory.Privacy),
            batch.Min(memory => memory.CreatedAt),
            batch.Max(memory => memory.CreatedAt),
            now,
            now.Advance(policy.SummaryLifetime),
            null,
            0,
            nextSequence,
            1,
            eventId));
    }

    private static int EpisodeOrder(NCEpisodicMemorySnapshot left, NCEpisodicMemorySnapshot right)
    {
        var sequence = right.LastChangedSequence.CompareTo(left.LastChangedSequence);
        return sequence != 0 ? sequence : left.MemoryId.Value.CompareTo(right.MemoryId.Value);
    }

    private static int SummaryOrder(NCConsolidatedMemorySnapshot left, NCConsolidatedMemorySnapshot right)
    {
        var sequence = right.LastChangedSequence.CompareTo(left.LastChangedSequence);
        return sequence != 0 ? sequence : left.SummaryId.Value.CompareTo(right.SummaryId.Value);
    }

    private static MemoryMaintenanceResult EmptyMaintenance(NCMemoryBookRecord? current) => new(
        current?.Snapshot.Episodes ?? [],
        current?.Snapshot.Summaries ?? [],
        0,
        0,
        [],
        [],
        [],
        false);

    private sealed record MemoryMaintenanceResult(
        IReadOnlyList<NCEpisodicMemorySnapshot> Episodes,
        IReadOnlyList<NCConsolidatedMemorySnapshot> Summaries,
        int ForgottenEpisodes,
        int ConsolidatedEpisodes,
        IReadOnlyList<NCMemoryId> RemovedMemoryIds,
        IReadOnlyList<NCMemorySummaryId> CreatedSummaryIds,
        IReadOnlyList<NCMemorySummaryId> RemovedSummaryIds,
        bool Changed);

    private sealed record MemoryConsolidatedEvent(
        NCPartyRef Owner,
        Guid ConsolidationEventId,
        NCPartyRef Initiator,
        IReadOnlyList<NCMemoryId> RemovedMemoryIds,
        IReadOnlyList<NCMemorySummaryId> CreatedSummaryIds,
        IReadOnlyList<NCMemorySummaryId> RemovedSummaryIds,
        int ForgottenEpisodes,
        int ConsolidatedEpisodes,
        string AuditSource);
}
