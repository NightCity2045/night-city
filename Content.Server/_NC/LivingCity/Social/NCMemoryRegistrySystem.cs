// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Social;

public enum NCMemoryMutationStatus : byte
{
    Applied,
    Duplicate,
    NoChange,
    PartyUnavailable,
    VersionConflict,
    ClockUnavailable,
    Rejected,
    StorageFailure,
}

public sealed record NCMemoryBookRecord(
    NCMemoryBookSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCMemoryMutationResult(
    NCMemoryMutationStatus Status,
    NCMemoryBookRecord? Book,
    string? Error);

/// <summary>
/// Canonical structured episodic memory. Blackboard, dialogue, neural summaries and materialized entities are bounded
/// consumers; only server-owned world events and cognition recall events can mutate this authority.
/// </summary>
public sealed partial class NCMemoryRegistrySystem : EntitySystem
{
    public const string BookAggregateType = "living-city.memory-book";
    public const string RememberApplicationAggregateType = "living-city.memory-application";
    public const string ConsolidationApplicationAggregateType = "living-city.memory-consolidation-application";
    public const string RecallApplicationAggregateType = "living-city.memory-recall-application";
    public const int SnapshotFormatVersion = 1;

    private const string RememberOperation = "remember";
    private const string ConsolidationOperation = "consolidate";
    private const string RecallOperation = "recall";
    private const string BookScopeType = "memory-book";
    private const int MutationGateStripeCount = 64;
    private const int MaximumAuditSourceLength = 128;
    private const int MaximumStableKeyLength = 128;
    private const int AbsoluteMaximumEpisodes = 4_096;
    private const int AbsoluteMaximumSummaries = 1_024;
    private const int AbsoluteMaximumSourceMemories = 128;
    private const int AbsoluteMaximumSourceEvents = 1_024;
    private const int AbsoluteMaximumParticipants = 256;
    private const int AbsoluteMaximumEmotionalTags = 128;
    private const int AbsoluteMaximumScore = 1_000_000;

    private static readonly ProtoId<NCMemoryPolicyPrototype> DefaultPolicy = "Default";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPartyResolverSystem _parties = default!;

    private readonly ConcurrentDictionary<NCMemoryBookId, NCMemoryBookRecord> _cache = [];
    private readonly SemaphoreSlim[] _gates = CreateGates();

    public int CachedBookCount => _cache.Count;

    public bool Unload(NCPartyRef owner)
    {
        if (!ValidOwner(owner))
            return false;
        return _cache.TryRemove(NCPersistentIdentity.MemoryBook(owner), out _);
    }

    public async Task<NCMemoryBookRecord?> GetAsync(NCPartyRef owner, bool forceReload = false)
    {
        if (!ValidOwner(owner))
            return null;

        var bookId = NCPersistentIdentity.MemoryBook(owner);
        if (!forceReload && _cache.TryGetValue(bookId, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(BookAggregateType, BookKey(bookId));
        if (stored == null)
            return null;
        var record = DeserializeBook(stored, bookId, owner);
        return record == null
            ? throw new InvalidOperationException($"Memory book {bookId} snapshot is invalid.")
            : CacheNewest(record);
    }

    /// <summary>
    /// Builds a bounded structured context. Privacy permission is explicit; no prose or untrusted player text is stored
    /// or returned by this authority.
    /// </summary>
    public async Task<IReadOnlyList<NCMemoryContextItem>> GetRelevantAsync(
        NCPartyRef owner,
        NCMemoryQuery query,
        bool forceReload = false)
    {
        if (!TryGetPolicy(out var policy) || !ValidQuery(query, policy) ||
            !_clock.TryGetCurrentTime(out _, out var now))
        {
            return [];
        }

        var book = await GetAsync(owner, forceReload);
        if (book == null)
            return [];

        var participants = query.Participants.ToHashSet();
        var eventTypes = query.EventTypeKeys.Select(NormalizeStableKey).ToHashSet(StringComparer.Ordinal);
        var emotions = query.EmotionalTagKeys.Select(NormalizeStableKey).ToHashSet(StringComparer.Ordinal);
        var result = new List<NCMemoryContextItem>();
        foreach (var episode in book.Snapshot.Episodes)
        {
            if (Expired(episode, now) || episode.Privacy > query.MaximumPrivacy ||
                !Matches(episode.Participants, [episode.EventTypeKey], episode.EmotionalTagKeys,
                    participants, eventTypes, emotions))
            {
                continue;
            }

            result.Add(new NCMemoryContextItem(
                episode.MemoryId,
                null,
                Relevance(episode.Salience, episode.Confidence, episode.CreatedAt, episode.Participants,
                    [episode.EventTypeKey], episode.EmotionalTagKeys, participants, eventTypes, emotions, now, policy),
                episode,
                null));
        }

        foreach (var summary in book.Snapshot.Summaries)
        {
            if (summary.ExpiresAt <= now || summary.Privacy > query.MaximumPrivacy ||
                !Matches(summary.Participants, summary.EventTypeKeys, summary.EmotionalTagKeys,
                    participants, eventTypes, emotions))
            {
                continue;
            }

            result.Add(new NCMemoryContextItem(
                null,
                summary.SummaryId,
                Relevance(summary.MaximumSalience, summary.MinimumConfidence, summary.LastOccurredAt,
                    summary.Participants, summary.EventTypeKeys, summary.EmotionalTagKeys,
                    participants, eventTypes, emotions, now, policy),
                null,
                summary));
        }

        return result
            .OrderByDescending(item => item.Relevance)
            .ThenByDescending(item => item.Episode?.CreatedAt ?? item.Summary!.LastOccurredAt)
            .ThenBy(item => item.MemoryId?.Value ?? item.SummaryId!.Value.Value)
            .Take(query.Limit)
            .ToArray();
    }

    private async Task<InitialCommandState> ValidateInitialCommandAsync(
        Guid commandId,
        string commandType,
        string scopeId)
    {
        var command = await _database.GetNCPersistentCommandResultAsync(commandId);
        if (command == null)
            return InitialCommandState.Available;
        if (command.CommandType != commandType || command.ScopeType != BookScopeType || command.ScopeId != scopeId)
            return InitialCommandState.Collision;

        return command.Status switch
        {
            NCPersistentMutationStatus.InProgress => InitialCommandState.InProgress,
            NCPersistentMutationStatus.DuplicateCompleted => InitialCommandState.Completed,
            _ => InitialCommandState.Failed,
        };
    }

    private async Task<MemoryApplicationSnapshot?> GetApplicationAsync(
        string aggregateType,
        Guid applicationId)
    {
        var stored = await _database.GetNCAggregateSnapshotAsync(aggregateType, applicationId.ToString("D"));
        if (stored == null)
            return null;
        if (stored.AggregateVersion != 1 || stored.FormatVersion != SnapshotFormatVersion ||
            !stored.SessionEpoch.IsValid || !stored.ActiveWorldTime.IsValid)
        {
            throw new InvalidOperationException("The memory application metadata is invalid.");
        }

        try
        {
            var marker = JsonSerializer.Deserialize<MemoryApplicationSnapshot>(stored.Payload, JsonOptions);
            return Valid(marker, applicationId) && marker!.AppliedAt == stored.ActiveWorldTime
                ? marker
                : throw new InvalidOperationException("The memory application snapshot is invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The memory application payload is invalid.", exception);
        }
    }

    private async Task<NCMemoryMutationResult> ReplayApplicationAsync(
        MemoryApplicationSnapshot marker,
        string operation,
        NCMemoryBookId bookId,
        NCPartyRef owner,
        Guid operationEventId,
        NCMemoryId? memoryId,
        string fingerprint,
        NCPartyRef initiator)
    {
        if (marker.Operation != operation || marker.BookId != bookId || marker.Owner != owner ||
            marker.OperationEventId != operationEventId || marker.MemoryId != memoryId ||
            marker.Initiator != initiator ||
            !string.Equals(marker.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return Reject(NCMemoryMutationStatus.Rejected,
                "The memory application identity is bound to different immutable data.");
        }

        var current = await GetAsync(owner, true);
        return marker.ResultingBookVersion > 0 &&
               (current == null || current.AggregateVersion < marker.ResultingBookVersion)
            ? Reject(NCMemoryMutationStatus.StorageFailure,
                "The application exists without its resulting memory-book version.")
            : new NCMemoryMutationResult(NCMemoryMutationStatus.Duplicate, current, null);
    }

    private static NCPersistentAggregateWrite ApplicationWrite(
        Guid eventId,
        string eventType,
        string aggregateType,
        MemoryApplicationSnapshot marker)
    {
        var payload = JsonSerializer.Serialize(marker, JsonOptions);
        return new NCPersistentAggregateWrite(
            eventId,
            eventType,
            aggregateType,
            marker.ApplicationId.ToString("D"),
            0,
            SnapshotFormatVersion,
            payload,
            payload);
    }

    private NCMemoryBookRecord CacheNewest(NCMemoryBookRecord candidate) =>
        _cache.AddOrUpdate(candidate.Snapshot.BookId, candidate,
            (_, current) => current.AggregateVersion > candidate.AggregateVersion ? current : candidate);

    private SemaphoreSlim Gate(NCMemoryBookId bookId) =>
        _gates[(bookId.Value.GetHashCode() & int.MaxValue) % _gates.Length];

    private static SemaphoreSlim[] CreateGates()
    {
        var gates = new SemaphoreSlim[MutationGateStripeCount];
        for (var i = 0; i < gates.Length; i++)
            gates[i] = new SemaphoreSlim(1, 1);
        return gates;
    }

    private static string BookKey(NCMemoryBookId bookId) => bookId.Value.ToString("D");

    private static NCMemoryMutationResult Reject(NCMemoryMutationStatus status, string error) =>
        new(status, null, error);

    private sealed record MemoryApplicationSnapshot(
        Guid ApplicationId,
        string Operation,
        NCMemoryBookId BookId,
        NCPartyRef Owner,
        Guid OperationEventId,
        NCMemoryId? MemoryId,
        string Fingerprint,
        NCPartyRef Initiator,
        long ResultingBookVersion,
        NCActiveWorldTime AppliedAt,
        Guid LastAppliedEventId);

    private sealed record MemoryBookReceipt(
        NCMemoryBookSnapshot? Snapshot,
        long AggregateVersion,
        NCSessionEpoch SessionEpoch,
        NCActiveWorldTime PersistedAt);

    private enum InitialCommandState : byte
    {
        Available,
        Completed,
        Failed,
        InProgress,
        Collision,
    }
}
