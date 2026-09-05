// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Social;

/// <summary>
/// Archives completed conversations in the generic Living City persistence store. Full transcripts are immutable;
/// compact actor-relative index rows make recall bounded without rewriting one ever-growing actor snapshot.
/// </summary>
public sealed partial class NCConversationArchiveSystem : EntitySystem
{
    public const string ArchiveAggregateType = "living-city.conversation-archive";
    public const string IndexAggregateType = "living-city.conversation-index";
    public const int SnapshotFormatVersion = 1;

    private const string ArchiveCommandType = "living-city.conversation.archive";
    private const string ArchiveScopeType = "conversation";
    private const string ArchiveEventType = "living-city.conversation.archived";
    private const string IndexEventType = "living-city.conversation.indexed";
    private const int MaximumWritesPerBatch = 24;

    private static readonly ProtoId<NCConversationPolicyPrototype> DefaultPolicy = "Default";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;

    private readonly ConcurrentDictionary<ConversationPairKey,
        IReadOnlyList<NCConversationArchiveIndexSnapshot>> _pairCache = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(_ => _pairCache.Clear());
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => _pairCache.Clear());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => _pairCache.Clear());
    }

    public Task ArchiveAsync(
        NCConversationSessionSnapshot session,
        IReadOnlyList<NCConversationTranscriptTurn> transcript)
    {
        if (!_clock.TryGetCurrentTime(out var epoch, out var now))
            return Task.FromException(new InvalidOperationException("The active-world clock is unavailable."));
        return ArchiveAsync(session, transcript, epoch, now);
    }

    /// <summary>
    /// Writes one immutable transcript plus actor/counterpart search projections. The supplied epoch keeps final round
    /// flushes inside the active-world durability barrier.
    /// </summary>
    public async Task ArchiveAsync(
        NCConversationSessionSnapshot session,
        IReadOnlyList<NCConversationTranscriptTurn> transcript,
        NCSessionEpoch epoch,
        NCActiveWorldTime now)
    {
        if (!TryGetPolicy(out var policy) || !Valid(session, transcript, epoch, now, policy))
            throw new InvalidOperationException($"Conversation {session.SessionId} cannot be archived.");

        var archiveEventId = StableId("archive-event", session.SessionId.ToString());
        var archivedTurns = transcript
            .Select((turn, index) => new NCConversationArchivedTurn(
                index + 1,
                turn.Speaker,
                turn.Act,
                NormalizeText(turn.Utterance, policy.MaximumUtteranceLength),
                turn.At))
            .ToArray();
        var semanticSummaryKeys = session.Turns
            .Where(turn => turn.SummaryKey.Length > 0)
            .Select(turn => turn.SummaryKey)
            .Distinct(StringComparer.Ordinal)
            .Take(policy.MaximumTopics)
            .ToArray();
        var archive = new NCConversationArchiveSnapshot(
            session.SessionId,
            session.Participants,
            session.Channel,
            session.LocationContext,
            session.StartedAt,
            session.LastActivityAt,
            session.MaximumDisclosure,
            session.Language,
            session.Locale,
            session.Status,
            session.EndReason,
            session.CurrentTopicKeys,
            semanticSummaryKeys,
            archivedTurns,
            1,
            archiveEventId);

        var writes = new List<NCPersistentAggregateWrite>
        {
            new(
                archiveEventId,
                ArchiveEventType,
                ArchiveAggregateType,
                ArchiveKey(session.SessionId),
                0,
                SnapshotFormatVersion,
                JsonSerializer.Serialize(archive, JsonOptions),
                JsonSerializer.Serialize(new ArchiveEvent(
                    session.SessionId,
                    session.Participants.Count,
                    archivedTurns.Length), JsonOptions)),
        };
        var indexEntries = new List<(ConversationPairKey Pair, NCConversationArchiveIndexSnapshot Snapshot)>();
        var originSpeaker = transcript.Count > 0 ? transcript[0].Speaker : default;
        foreach (var participant in session.Participants)
        {
            if (!participant.TryGetActor(out var ownerActorId))
                continue;
            // Crowd archives remain actor-relative: listeners index the originating speaker, while an NPC originator
            // indexes only people who actually answered. This avoids quadratic bystander-to-bystander relationships.
            IEnumerable<NCPartyRef> counterparts = participant == originSpeaker
                ? transcript.Select(turn => turn.Speaker).Where(value => value != participant).Distinct()
                : originSpeaker.IsValid
                    ? [originSpeaker]
                    : [];
            foreach (var counterpart in counterparts)
            {
                var pair = new ConversationPairKey(ownerActorId, counterpart);
                var indexEventId = StableId("index-event",
                    $"{ownerActorId}/{counterpart}/{session.SessionId}");
                var index = new NCConversationArchiveIndexSnapshot(
                    ownerActorId,
                    counterpart,
                    session.SessionId,
                    session.LastActivityAt,
                    session.CurrentTopicKeys,
                    semanticSummaryKeys,
                    BuildSummary(ownerActorId, transcript, session.CurrentTopicKeys,
                        policy.MaximumArchivedSummaryTurns, policy.MaximumArchivedSummaryLength),
                    archivedTurns.Length,
                    1,
                    indexEventId);
                indexEntries.Add((pair, index));
                writes.Add(new NCPersistentAggregateWrite(
                    indexEventId,
                    IndexEventType,
                    IndexAggregateType,
                    IndexKey(pair, session.LastActivityAt, session.SessionId),
                    0,
                    SnapshotFormatVersion,
                    JsonSerializer.Serialize(index, JsonOptions),
                    JsonSerializer.Serialize(new IndexEvent(
                        ownerActorId,
                        counterpart,
                        session.SessionId), JsonOptions)));
            }
        }

        for (var offset = 0; offset < writes.Count; offset += MaximumWritesPerBatch)
        {
            var batch = writes.Skip(offset).Take(MaximumWritesPerBatch).ToArray();
            var batchNumber = offset / MaximumWritesPerBatch;
            var commandId = StableId("archive-command", $"{session.SessionId}/{batchNumber}");
            var result = await _database.ApplyNCPersistentBatchMutationAsync(new NCPersistentBatchMutationRequest(
                commandId,
                ArchiveCommandType,
                ArchiveScopeType,
                $"{session.SessionId}/{batchNumber}",
                batch,
                JsonSerializer.Serialize(new ArchiveReceipt(session.SessionId, batchNumber), JsonOptions),
                session.SessionId.Value,
                null,
                epoch,
                now,
                [],
                NCPersistenceWriteClass.DurableOutcome));
            if (result.Status is not (NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted))
            {
                throw new InvalidOperationException(
                    $"Conversation archive batch {batchNumber} failed: {result.Status}: {result.Error}");
            }
        }

        foreach (var entry in indexEntries)
            UpdateCache(entry.Pair, entry.Snapshot, policy.MaximumCachedArchivesPerPair);
    }

    public async Task<NCConversationArchiveSnapshot?> GetAsync(NCConversationSessionId sessionId)
    {
        if (!sessionId.IsValid || !TryGetPolicy(out var policy))
            return null;
        var stored = await _database.GetNCAggregateSnapshotAsync(ArchiveAggregateType, ArchiveKey(sessionId));
        return DeserializeArchive(stored, sessionId, policy);
    }

    /// <summary>
    /// Selects recent actor-relative memories for the current interlocutors. Only compact summaries enter Neural
    /// context; full archived turns stay server-side and can be loaded explicitly through <see cref="GetAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<NCConversationMemoryContext>> GetRelevantAsync(
        NCActorId actorId,
        IReadOnlyList<NCPartyRef> participants,
        IReadOnlyList<string> topicKeys,
        int limit)
    {
        if (!actorId.IsValid || limit < 1 || !TryGetPolicy(out var policy))
            return [];
        var owner = NCPartyRef.ForActor(actorId);
        var counterparts = participants
            .Where(value => value.IsValid && value != owner)
            .Distinct()
            .ToArray();
        if (counterparts.Length == 0)
            return [];

        var loads = counterparts
            .Select(counterpart => LoadPairAsync(
                new ConversationPairKey(actorId, counterpart),
                policy.MaximumCachedArchivesPerPair))
            .ToArray();
        var loaded = await Task.WhenAll(loads);
        var topics = topicKeys.ToHashSet(StringComparer.Ordinal);
        return loaded
            .SelectMany(values => values)
            .GroupBy(value => value.SessionId)
            .Select(group => group.First())
            .OrderByDescending(value => value.TopicKeys.Count(topics.Contains) +
                value.SemanticSummaryKeys.Count(topics.Contains))
            .ThenByDescending(value => value.LastActivityAt.Ticks)
            .Take(limit)
            .Select(value => new NCConversationMemoryContext(
                value.SessionId,
                value.LastActivityAt,
                value.TopicKeys,
                value.SemanticSummaryKeys,
                value.Summary))
            .ToArray();
    }

    internal static string BuildSummary(
        NCActorId ownerActorId,
        IReadOnlyList<NCConversationTranscriptTurn> transcript,
        IReadOnlyList<string> topicKeys,
        int maximumTurns,
        int maximumLength)
    {
        if (maximumTurns < 1 || maximumLength < 1)
            return string.Empty;
        if (transcript.Count == 0)
        {
            var topicSummary = topicKeys.Count == 0
                ? "A previous conversation occurred."
                : $"Previous conversation topics: {string.Join(", ", topicKeys)}.";
            return NormalizeText(topicSummary, maximumLength);
        }

        var selected = new HashSet<int>();
        for (var i = transcript.Count - 1; i >= 0 && selected.Count < maximumTurns; i--)
        {
            if (transcript[i].Act is NCSpeechActKind.Threat or NCSpeechActKind.Offer or
                NCSpeechActKind.Acceptance or NCSpeechActKind.Refusal)
                selected.Add(i);
        }
        for (var i = transcript.Count - 1; i >= 0 && selected.Count < maximumTurns; i--)
            selected.Add(i);

        var owner = NCPartyRef.ForActor(ownerActorId);
        var builder = new StringBuilder(Math.Min(maximumLength, 1024));
        foreach (var index in selected.Order())
        {
            var turn = transcript[index];
            var speaker = turn.Speaker == owner ? "self" : "interlocutor";
            var fragment = $"{speaker}[{turn.Act.ToString().ToLowerInvariant()}]: " +
                           NormalizeText(turn.Utterance, maximumLength);
            if (builder.Length > 0)
                builder.Append(" | ");
            var remaining = maximumLength - builder.Length;
            if (remaining <= 0)
                break;
            builder.Append(fragment.AsSpan(0, Math.Min(fragment.Length, remaining)));
        }
        return builder.ToString();
    }

    private async Task<IReadOnlyList<NCConversationArchiveIndexSnapshot>> LoadPairAsync(
        ConversationPairKey pair,
        int limit)
    {
        if (_pairCache.TryGetValue(pair, out var cached))
            return cached;
        var prefix = IndexPrefix(pair);
        var page = await _database.GetNCAggregateSnapshotPrefixPageDescendingAsync(
            IndexAggregateType,
            prefix,
            null,
            limit);
        var values = page
            .Select(value => DeserializeIndex(value, pair))
            .Where(value => value != null)
            .Select(value => value!)
            .ToArray();
        return _pairCache.GetOrAdd(pair, values);
    }

    private void UpdateCache(ConversationPairKey pair, NCConversationArchiveIndexSnapshot snapshot, int limit)
    {
        _pairCache.AddOrUpdate(
            pair,
            new[] { snapshot },
            (_, current) => current
                .Prepend(snapshot)
                .GroupBy(value => value.SessionId)
                .Select(group => group.First())
                .OrderByDescending(value => value.LastActivityAt.Ticks)
                .Take(limit)
                .ToArray());
    }

    private bool TryGetPolicy(out NCConversationPolicyPrototype policy) =>
        _prototypes.TryIndex(DefaultPolicy, out policy!);

    private static bool Valid(
        NCConversationSessionSnapshot session,
        IReadOnlyList<NCConversationTranscriptTurn> transcript,
        NCSessionEpoch epoch,
        NCActiveWorldTime now,
        NCConversationPolicyPrototype policy) =>
        session.SessionId.IsValid && epoch.IsValid && now.IsValid && session.StartedAt.IsValid &&
        session.LastActivityAt.IsValid && session.StartedAt <= session.LastActivityAt &&
        session.LastActivityAt <= now &&
        session.Participants.Count is >= 2 && session.Participants.Count <= policy.MaximumParticipants &&
        session.Participants.All(value => value.IsValid) &&
        session.Status is (NCConversationStatus.Ended or NCConversationStatus.Expired or
            NCConversationStatus.Interrupted) &&
        transcript.Count <= policy.MaximumTurns &&
        transcript.All(value => value.Speaker.IsValid && session.Participants.Contains(value.Speaker) &&
            Enum.IsDefined(value.Act) && value.At.IsValid && value.At >= session.StartedAt &&
            value.At <= session.LastActivityAt && value.Utterance.Length is > 0 &&
            value.Utterance.Length <= policy.MaximumUtteranceLength &&
            value.Utterance.All(character => !char.IsControl(character)));

    private static NCConversationArchiveSnapshot? DeserializeArchive(
        NCAggregateSnapshotData? stored,
        NCConversationSessionId expected,
        NCConversationPolicyPrototype policy)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion || stored.AggregateVersion != 1 ||
            !stored.SessionEpoch.IsValid || !stored.ActiveWorldTime.IsValid)
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCConversationArchiveSnapshot>(stored.Payload, JsonOptions);
            return snapshot != null && snapshot.SessionId == expected && snapshot.DataVersion == 1 &&
                   snapshot.LastAppliedEventId != Guid.Empty && snapshot.Participants.Count is >= 2 &&
                   snapshot.Participants.Count <= policy.MaximumParticipants && snapshot.Turns.Count <= policy.MaximumTurns
                ? snapshot
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static NCConversationArchiveIndexSnapshot? DeserializeIndex(
        NCAggregateSnapshotData stored,
        ConversationPairKey expected)
    {
        if (stored.FormatVersion != SnapshotFormatVersion || stored.AggregateVersion != 1 ||
            !stored.SessionEpoch.IsValid || !stored.ActiveWorldTime.IsValid)
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCConversationArchiveIndexSnapshot>(stored.Payload, JsonOptions);
            return snapshot != null && snapshot.OwnerActorId == expected.ActorId &&
                   snapshot.Counterpart == expected.Counterpart && snapshot.SessionId.IsValid &&
                   snapshot.LastActivityAt.IsValid && snapshot.DataVersion == 1 &&
                   snapshot.LastAppliedEventId != Guid.Empty
                ? snapshot
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ArchiveKey(NCConversationSessionId sessionId) => sessionId.Value.ToString("N");

    private static string IndexKey(
        ConversationPairKey pair,
        NCActiveWorldTime at,
        NCConversationSessionId sessionId) =>
        $"{PairId(pair):N}/{at.Ticks:D20}/{sessionId.Value:N}";

    private static string IndexPrefix(ConversationPairKey pair) => $"{PairId(pair):N}/";

    private static Guid PairId(ConversationPairKey pair) => NCPersistentIdentity.Correlation(
        "conversation",
        "participant-index",
        $"{pair.ActorId}/{pair.Counterpart}");

    private static Guid StableId(string operation, string stableKey) =>
        NCPersistentIdentity.Command("conversation", operation, stableKey);

    private static string NormalizeText(string value, int maximumLength)
    {
        var normalized = string.Join(' ', value.Split((char[]?) null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized[..Math.Min(normalized.Length, maximumLength)];
    }

    private readonly record struct ConversationPairKey(NCActorId ActorId, NCPartyRef Counterpart);
    private sealed record ArchiveEvent(NCConversationSessionId SessionId, int ParticipantCount, int TurnCount);
    private sealed record IndexEvent(
        NCActorId ActorId,
        NCPartyRef Counterpart,
        NCConversationSessionId SessionId);
    private sealed record ArchiveReceipt(NCConversationSessionId SessionId, int BatchNumber);
}
