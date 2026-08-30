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
    public async Task<NCMemoryMutationResult> RememberAsync(NCRememberWorldEventRequest request)
    {
        if (!TryValidateRememberRequest(request, out var resolved, out var policy, out var error) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(_clock.IsReady ? NCMemoryMutationStatus.Rejected : NCMemoryMutationStatus.ClockUnavailable,
                error ?? "The active-world clock is not ready.");
        }

        var bookId = NCPersistentIdentity.MemoryBook(request.Owner);
        var memoryId = NCPersistentIdentity.Memory(request.Owner, request.WorldEventId);
        var applicationId = NCPersistentIdentity.MemoryApplication(memoryId);
        var scopeId = applicationId.ToString("D");
        var commandType = "living-city.memory.remember";
        var fingerprint = RememberFingerprint(request, resolved);
        InitialCommandState commandState;
        try
        {
            commandState = await ValidateInitialCommandAsync(request.CommandId, commandType, scopeId);
            if (!await ResolvePartiesAsync(request.Owner, request.Initiator, request.Source.SourceParty,
                    request.Participants))
            {
                return Reject(NCMemoryMutationStatus.PartyUnavailable,
                    "Memory owner, initiator, source, or participant is unavailable.");
            }
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to prepare memory event {request.WorldEventId}: {exception}");
            return Reject(NCMemoryMutationStatus.StorageFailure, exception.Message);
        }

        if (commandState == InitialCommandState.Collision)
            return Reject(NCMemoryMutationStatus.Rejected, "The command ID belongs to another operation.");
        if (commandState == InitialCommandState.InProgress)
            return Reject(NCMemoryMutationStatus.StorageFailure, "The memory command is still in progress.");

        var gate = Gate(bookId);
        await gate.WaitAsync();
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var application = await GetApplicationAsync(RememberApplicationAggregateType, applicationId);
                if (application != null)
                {
                    return await ReplayApplicationAsync(application, RememberOperation, bookId, request.Owner,
                        request.WorldEventId, memoryId, fingerprint, request.Initiator);
                }
                if (attempt == 0 && commandState == InitialCommandState.Completed)
                {
                    return Reject(NCMemoryMutationStatus.StorageFailure,
                        "The completed remember command is missing its application marker.");
                }

                var current = await GetAsync(request.Owner, true);
                if (current?.Snapshot.Episodes.Any(memory => memory.MemoryId == memoryId) == true ||
                    current?.Snapshot.Summaries.Any(summary => summary.SourceMemoryIds.Contains(memoryId)) == true)
                {
                    return Reject(NCMemoryMutationStatus.StorageFailure,
                        "The memory exists without its immutable application marker.");
                }

                var bookEventId = Guid.NewGuid();
                var applicationEventId = Guid.NewGuid();
                var nextSequence = checked((current?.Snapshot.DataVersion ?? 0) + 1);
                var episode = new NCEpisodicMemorySnapshot(
                    memoryId,
                    request.Owner,
                    resolved.EventTypeKey,
                    resolved.ConsolidationKey,
                    resolved.SourceWorldEventIds,
                    resolved.Participants,
                    request.Source,
                    resolved.EmotionalTagKeys,
                    resolved.Salience,
                    resolved.Confidence,
                    resolved.Privacy,
                    resolved.RetentionClass,
                    resolved.Critical,
                    resolved.CanConsolidate,
                    now,
                    resolved.Lifetime is { } lifetime ? now.Advance(lifetime) : null,
                    null,
                    0,
                    nextSequence,
                    1,
                    bookEventId);
                var episodes = current?.Snapshot.Episodes.ToList() ?? [];
                var summaries = current?.Snapshot.Summaries.ToList() ?? [];
                episodes.Add(episode);
                if (!TryMaintain(
                        request.Owner,
                        episodes,
                        summaries,
                        now,
                        policy,
                        false,
                        nextSequence,
                        bookEventId,
                        out var maintenance,
                        out error))
                {
                    return Reject(NCMemoryMutationStatus.Rejected, error!);
                }

                var next = new NCMemoryBookSnapshot(
                    bookId,
                    request.Owner,
                    maintenance.Episodes,
                    maintenance.Summaries,
                    checked((current?.Snapshot.TotalRemembered ?? 0) + 1),
                    checked((current?.Snapshot.TotalForgotten ?? 0) + maintenance.ForgottenEpisodes),
                    checked((current?.Snapshot.TotalConsolidated ?? 0) + maintenance.ConsolidatedEpisodes),
                    nextSequence,
                    bookEventId);
                var nextVersion = (current?.AggregateVersion ?? 0) + 1;
                var marker = new MemoryApplicationSnapshot(
                    applicationId,
                    RememberOperation,
                    bookId,
                    request.Owner,
                    request.WorldEventId,
                    memoryId,
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
                                "living-city.memory.remembered",
                                BookAggregateType,
                                BookKey(bookId),
                                current?.AggregateVersion ?? 0,
                                SnapshotFormatVersion,
                                JsonSerializer.Serialize(next, JsonOptions),
                                JsonSerializer.Serialize(new MemoryRememberedEvent(
                                    memoryId,
                                    request.Owner,
                                    request.WorldEventId,
                                    resolved.EventTypeKey,
                                    request.Initiator,
                                    maintenance.RemovedMemoryIds,
                                    maintenance.CreatedSummaryIds,
                                    request.AuditSource.Trim()), JsonOptions)),
                            ApplicationWrite(
                                applicationEventId,
                                "living-city.memory.application-recorded",
                                RememberApplicationAggregateType,
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
                            "The committed memory book could not be reloaded.");
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
                    result.Error ?? "The memory event was not committed.");
            }

            return Reject(NCMemoryMutationStatus.VersionConflict,
                "The remember operation exceeded its optimistic retry limit.");
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to remember world event {request.WorldEventId} for {request.Owner}: {exception}");
            return Reject(NCMemoryMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> ResolvePartiesAsync(
        NCPartyRef owner,
        NCPartyRef initiator,
        NCPartyRef optionalSource,
        IReadOnlyList<NCPartyRef> participants)
    {
        var parties = participants.Append(owner).Append(initiator);
        if (optionalSource.IsValid)
            parties = parties.Append(optionalSource);
        foreach (var party in parties.Distinct())
        {
            if (!(await _parties.ResolveAsync(party, true)).IsResolved)
                return false;
        }
        return true;
    }

    private sealed record MemoryRememberedEvent(
        NCMemoryId MemoryId,
        NCPartyRef Owner,
        Guid WorldEventId,
        string EventTypeKey,
        NCPartyRef Initiator,
        IReadOnlyList<NCMemoryId> RemovedMemoryIds,
        IReadOnlyList<NCMemorySummaryId> CreatedSummaryIds,
        string AuditSource);
}
