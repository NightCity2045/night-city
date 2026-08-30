// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Identity;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Social;

public sealed partial class NCMemoryRegistrySystem
{
    public bool ValidatePrototypes(List<string> errors)
    {
        var initialCount = errors.Count;
        if (!TryGetPolicy(out var policy))
        {
            errors.Add("The Default memory policy is missing or invalid.");
            return false;
        }

        foreach (var other in _prototypes.EnumeratePrototypes<NCMemoryPolicyPrototype>())
        {
            if (!ValidPolicy(other))
                errors.Add($"Memory policy {other.ID} has invalid bounds.");
        }

        var emotionKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var emotion in _prototypes.EnumeratePrototypes<NCMemoryEmotionPrototype>())
        {
            if (!ValidEmotion(emotion) || !emotionKeys.Add(NormalizeStableKey(emotion.StableKey)))
                errors.Add($"Memory emotion {emotion.ID} is invalid or duplicates a stable key.");
        }

        var retentionKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var retention in _prototypes.EnumeratePrototypes<NCMemoryRetentionPrototype>())
        {
            if (!ValidRetention(retention) || !retentionKeys.Add(NormalizeStableKey(retention.StableKey)))
                errors.Add($"Memory retention {retention.ID} is invalid or duplicates a stable key.");
        }

        var eventKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var memoryEvent in _prototypes.EnumeratePrototypes<NCMemoryEventPrototype>())
        {
            if (!ValidEvent(memoryEvent, policy) || !eventKeys.Add(NormalizeStableKey(memoryEvent.StableKey)))
                errors.Add($"Memory event {memoryEvent.ID} is invalid or duplicates a stable key.");
        }

        return errors.Count == initialCount;
    }

    private bool TryValidateRememberRequest(
        NCRememberWorldEventRequest request,
        out ResolvedMemoryEvent resolved,
        out NCMemoryPolicyPrototype policy,
        out string? error)
    {
        resolved = default!;
        policy = default!;
        error = null;
        if (!TryGetPolicy(out policy) || request.CommandId == Guid.Empty || request.WorldEventId == Guid.Empty ||
            request.CorrelationId == Guid.Empty || !ValidOwner(request.Owner) || !request.Initiator.IsValid ||
            request.AdditionalSourceEventIds == null || request.Participants == null ||
            request.AdditionalEmotionalTags == null || string.IsNullOrWhiteSpace(request.AuditSource) ||
            request.AuditSource.Trim().Length > MaximumAuditSourceLength || !ValidSource(request.Source) ||
            request.AdditionalSourceEventIds.Count >= policy.MaximumSourceEventsPerEpisode ||
            request.AdditionalSourceEventIds.Any(id => id == Guid.Empty || id == request.WorldEventId) ||
            request.AdditionalSourceEventIds.Distinct().Count() != request.AdditionalSourceEventIds.Count ||
            request.Participants.Count > policy.MaximumParticipantsPerEpisode ||
            request.Participants.Any(party => !party.IsValid) ||
            request.Participants.Distinct().Count() != request.Participants.Count ||
            request.AdditionalEmotionalTags.Count > policy.MaximumEmotionalTagsPerEpisode ||
            request.AdditionalEmotionalTags.Distinct().Count() != request.AdditionalEmotionalTags.Count ||
            request.Salience is < 0 or > 1_000 || request.Confidence is < 0 or > 1_000 ||
            request.Privacy is { } privacy && !Enum.IsDefined(privacy))
        {
            error = "The remember-world-event request is invalid or exceeds the active memory policy.";
            return false;
        }
        if (!_prototypes.TryIndex(request.EventType, out NCMemoryEventPrototype? eventPrototype) ||
            eventPrototype == null || !ValidEvent(eventPrototype, policy) ||
            !_prototypes.TryIndex(eventPrototype.Retention, out NCMemoryRetentionPrototype? retention) ||
            retention == null || !ValidRetention(retention))
        {
            error = "The memory event or retention prototype is invalid.";
            return false;
        }

        var emotions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var emotionId in eventPrototype.EmotionalTags.Concat(request.AdditionalEmotionalTags))
        {
            if (!_prototypes.TryIndex(emotionId, out NCMemoryEmotionPrototype? emotion) || emotion == null ||
                !ValidEmotion(emotion) || !emotions.Add(NormalizeStableKey(emotion.StableKey)))
            {
                error = "The memory event contains an invalid or duplicate emotional tag.";
                return false;
            }
        }
        if (emotions.Count > policy.MaximumEmotionalTagsPerEpisode)
        {
            error = "The resolved memory event exceeds the emotional-tag limit.";
            return false;
        }

        resolved = new ResolvedMemoryEvent(
            NormalizeStableKey(eventPrototype.StableKey),
            NormalizeStableKey(eventPrototype.ConsolidationKey),
            [request.WorldEventId, .. request.AdditionalSourceEventIds.Order()],
            request.Participants.OrderBy(party => party.ToString(), StringComparer.Ordinal).ToArray(),
            emotions.Order(StringComparer.Ordinal).ToArray(),
            request.Salience ?? eventPrototype.DefaultSalience,
            request.Confidence ?? eventPrototype.DefaultConfidence,
            request.Privacy ?? eventPrototype.DefaultPrivacy,
            retention.Class,
            eventPrototype.Critical,
            retention.CanConsolidate,
            retention.Class == NCMemoryRetentionClass.Permanent ? null : retention.Lifetime);
        return true;
    }

    private static bool ValidConsolidationRequest(NCConsolidateMemoryRequest request) =>
        request.CommandId != Guid.Empty && request.ConsolidationEventId != Guid.Empty &&
        request.CorrelationId != Guid.Empty && ValidOwner(request.Owner) && request.Initiator.IsValid &&
        !string.IsNullOrWhiteSpace(request.AuditSource) &&
        request.AuditSource.Trim().Length <= MaximumAuditSourceLength;

    private static bool ValidRecallRequest(NCRecordMemoryRecallRequest request, NCMemoryPolicyPrototype policy) =>
        request.CommandId != Guid.Empty && request.RecallEventId != Guid.Empty &&
        request.CorrelationId != Guid.Empty && ValidOwner(request.Owner) && request.Initiator.IsValid &&
        request.MemoryIds != null && request.SummaryIds != null &&
        request.MemoryIds.Count + request.SummaryIds.Count is > 0 &&
        request.MemoryIds.Count + request.SummaryIds.Count <= policy.MaximumQueryCount &&
        request.MemoryIds.All(id => id.IsValid) && request.SummaryIds.All(id => id.IsValid) &&
        request.MemoryIds.Distinct().Count() == request.MemoryIds.Count &&
        request.SummaryIds.Distinct().Count() == request.SummaryIds.Count &&
        !string.IsNullOrWhiteSpace(request.AuditSource) &&
        request.AuditSource.Trim().Length <= MaximumAuditSourceLength;

    private static bool ValidQuery(NCMemoryQuery query, NCMemoryPolicyPrototype policy)
    {
        return query.Participants != null && query.EventTypeKeys != null && query.EmotionalTagKeys != null &&
               query.Limit is > 0 && query.Limit <= policy.MaximumQueryCount &&
               Enum.IsDefined(query.MaximumPrivacy) &&
               query.Participants.Count <= policy.MaximumParticipantsPerSummary &&
               query.Participants.All(party => party.IsValid) &&
               query.Participants.Distinct().Count() == query.Participants.Count &&
               query.EventTypeKeys.Count <= policy.MaximumSourceMemoriesPerSummary &&
               query.EventTypeKeys.All(IsStableKey) &&
               query.EventTypeKeys.Select(NormalizeStableKey).Distinct().Count() == query.EventTypeKeys.Count &&
               query.EmotionalTagKeys.Count <= policy.MaximumEmotionalTagsPerSummary &&
               query.EmotionalTagKeys.All(IsStableKey) &&
               query.EmotionalTagKeys.Select(NormalizeStableKey).Distinct().Count() == query.EmotionalTagKeys.Count;
    }

    private bool TryGetPolicy(out NCMemoryPolicyPrototype policy)
    {
        policy = default!;
        if (!_prototypes.TryIndex(DefaultPolicy, out NCMemoryPolicyPrototype? resolved) || resolved == null ||
            !ValidPolicy(resolved))
        {
            return false;
        }
        policy = resolved;
        return true;
    }

    private bool ValidEvent(NCMemoryEventPrototype memoryEvent, NCMemoryPolicyPrototype policy)
    {
        if (!IsStableKey(memoryEvent.StableKey) || !IsStableKey(memoryEvent.ConsolidationKey) ||
            memoryEvent.DefaultSalience is < 0 or > 1_000 ||
            memoryEvent.DefaultConfidence is < 0 or > 1_000 || !Enum.IsDefined(memoryEvent.DefaultPrivacy) ||
            memoryEvent.EmotionalTags.Count > policy.MaximumEmotionalTagsPerEpisode ||
            memoryEvent.EmotionalTags.Distinct().Count() != memoryEvent.EmotionalTags.Count ||
            !_prototypes.TryIndex(memoryEvent.Retention, out NCMemoryRetentionPrototype? retention) ||
            retention == null || !ValidRetention(retention) ||
            (memoryEvent.Critical && retention.Class != NCMemoryRetentionClass.Permanent) ||
            (!memoryEvent.Critical && retention.Class == NCMemoryRetentionClass.Permanent))
        {
            return false;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var emotionId in memoryEvent.EmotionalTags)
        {
            if (!_prototypes.TryIndex(emotionId, out NCMemoryEmotionPrototype? emotion) || emotion == null ||
                !ValidEmotion(emotion) || !keys.Add(NormalizeStableKey(emotion.StableKey)))
            {
                return false;
            }
        }
        return true;
    }

    private static bool ValidPolicy(NCMemoryPolicyPrototype policy) =>
        policy.MaximumEpisodesPerOwner is > 1 and <= AbsoluteMaximumEpisodes &&
        policy.TargetEpisodesAfterConsolidation is > 0 &&
        policy.TargetEpisodesAfterConsolidation < policy.MaximumEpisodesPerOwner &&
        policy.MaximumSummariesPerOwner is > 0 and <= AbsoluteMaximumSummaries &&
        policy.MaximumSourceMemoriesPerSummary is > 1 and <= AbsoluteMaximumSourceMemories &&
        policy.MaximumSourceEventsPerEpisode is > 0 and <= AbsoluteMaximumSourceEvents &&
        policy.MaximumSourceEventsPerSummary >= policy.MaximumSourceMemoriesPerSummary &&
        policy.MaximumSourceEventsPerSummary <= AbsoluteMaximumSourceEvents &&
        policy.MaximumParticipantsPerEpisode is > 0 and <= AbsoluteMaximumParticipants &&
        policy.MaximumParticipantsPerSummary >= policy.MaximumParticipantsPerEpisode &&
        policy.MaximumParticipantsPerSummary <= AbsoluteMaximumParticipants &&
        policy.MaximumEmotionalTagsPerEpisode is > 0 and <= AbsoluteMaximumEmotionalTags &&
        policy.MaximumEmotionalTagsPerSummary >= policy.MaximumEmotionalTagsPerEpisode &&
        policy.MaximumEmotionalTagsPerSummary <= AbsoluteMaximumEmotionalTags &&
        policy.MinimumConsolidationGroupSize is > 1 &&
        policy.MinimumConsolidationGroupSize <= policy.MaximumSourceMemoriesPerSummary &&
        policy.SummaryLifetime > TimeSpan.Zero && policy.SummaryLifetime <= TimeSpan.FromDays(3_650) &&
        policy.MaximumQueryCount is > 0 &&
        policy.MaximumQueryCount <= policy.MaximumEpisodesPerOwner + policy.MaximumSummariesPerOwner &&
        ValidWeight(policy.ParticipantMatchBonus) && ValidWeight(policy.EventTypeMatchBonus) &&
        ValidWeight(policy.EmotionalTagMatchBonus) && ValidWeight(policy.SalienceWeight) &&
        ValidWeight(policy.ConfidenceWeight) && ValidWeight(policy.RecentMemoryBonus) &&
        policy.RecentMemoryWindow > TimeSpan.Zero && policy.RecentMemoryWindow <= TimeSpan.FromDays(365);

    private static bool ValidWeight(int value) => value is >= 0 and <= AbsoluteMaximumScore;

    private static bool ValidRetention(NCMemoryRetentionPrototype retention)
    {
        if (!IsStableKey(retention.StableKey) || !Enum.IsDefined(retention.Class))
            return false;
        return retention.Class == NCMemoryRetentionClass.Permanent
            ? retention.Lifetime == TimeSpan.Zero && !retention.CanConsolidate
            : retention.Lifetime > TimeSpan.Zero && retention.Lifetime <= TimeSpan.FromDays(3_650);
    }

    private static bool ValidEmotion(NCMemoryEmotionPrototype emotion) => IsStableKey(emotion.StableKey);

    private static bool ValidOwner(NCPartyRef owner) =>
        owner.IsValid && owner.Kind is NCPartyKind.Character or NCPartyKind.Actor;

    private static bool ValidSource(NCMemorySource source)
    {
        if (!Enum.IsDefined(source.Kind))
            return false;
        return source.Kind is NCMemorySourceKind.DirectPerception or NCMemorySourceKind.Inference
            ? !source.SourceParty.IsValid
            : source.SourceParty.IsValid;
    }

    private static bool Valid(NCMemoryBookSnapshot? book, NCMemoryBookId expected, NCPartyRef owner)
    {
        if (book == null || book.BookId != expected || book.BookId != NCPersistentIdentity.MemoryBook(owner) ||
            book.Owner != owner || !ValidOwner(owner) || book.Episodes.Count > AbsoluteMaximumEpisodes ||
            book.Summaries.Count > AbsoluteMaximumSummaries || book.TotalRemembered < 0 || book.TotalForgotten < 0 ||
            book.TotalConsolidated < 0 || book.TotalRemembered !=
            book.Episodes.Count + book.TotalForgotten + book.TotalConsolidated ||
            book.DataVersion < 1 || book.LastAppliedEventId == Guid.Empty)
        {
            return false;
        }

        var memoryIds = new HashSet<NCMemoryId>();
        foreach (var episode in book.Episodes)
        {
            if (!Valid(episode, owner, book.DataVersion) || !memoryIds.Add(episode.MemoryId))
                return false;
        }

        var summaryIds = new HashSet<NCMemorySummaryId>();
        var consolidatedSources = new HashSet<NCMemoryId>();
        foreach (var summary in book.Summaries)
        {
            if (!Valid(summary, owner, book.DataVersion) || !summaryIds.Add(summary.SummaryId) ||
                summary.SourceMemoryIds.Any(id => memoryIds.Contains(id) || !consolidatedSources.Add(id)))
            {
                return false;
            }
        }

        return OrderedEpisodes(book.Episodes) && OrderedSummaries(book.Summaries);
    }

    private static bool Valid(NCEpisodicMemorySnapshot episode, NCPartyRef owner, int bookVersion) =>
        episode.MemoryId.IsValid && episode.Owner == owner && episode.SourceWorldEventIds.Count is > 0 and
            <= AbsoluteMaximumSourceEvents && episode.SourceWorldEventIds.All(id => id != Guid.Empty) &&
        episode.SourceWorldEventIds.Distinct().Count() == episode.SourceWorldEventIds.Count &&
        episode.MemoryId == NCPersistentIdentity.Memory(owner, episode.SourceWorldEventIds[0]) &&
        IsStableKey(episode.EventTypeKey) && IsStableKey(episode.ConsolidationKey) &&
        ValidOrderedParties(episode.Participants, AbsoluteMaximumParticipants) && ValidSource(episode.Source) &&
        ValidOrderedKeys(episode.EmotionalTagKeys, AbsoluteMaximumEmotionalTags) &&
        episode.Salience is >= 0 and <= 1_000 && episode.Confidence is >= 0 and <= 1_000 &&
        Enum.IsDefined(episode.Privacy) && Enum.IsDefined(episode.RetentionClass) &&
        (!episode.Critical || episode.RetentionClass == NCMemoryRetentionClass.Permanent) &&
        (episode.RetentionClass == NCMemoryRetentionClass.Permanent
            ? episode.ExpiresAt == null && !episode.CanConsolidate
            : episode.ExpiresAt > episode.CreatedAt) &&
        episode.CreatedAt.IsValid && ValidRecall(episode.LastRecalledAt, episode.RecallCount, episode.CreatedAt) &&
        episode.LastChangedSequence is > 0 && episode.LastChangedSequence <= bookVersion &&
        episode.DataVersion > 0 && episode.LastAppliedEventId != Guid.Empty;

    private static bool Valid(NCConsolidatedMemorySnapshot summary, NCPartyRef owner, int bookVersion) =>
        summary.SummaryId.IsValid && summary.Owner == owner && IsStableKey(summary.ConsolidationKey) &&
        summary.SourceMemoryIds.Count is > 1 and <= AbsoluteMaximumSourceMemories &&
        summary.SourceMemoryIds.All(id => id.IsValid) &&
        summary.SourceMemoryIds.SequenceEqual(summary.SourceMemoryIds.OrderBy(id => id.Value)) &&
        summary.SourceMemoryIds.Distinct().Count() == summary.SourceMemoryIds.Count &&
        summary.SummaryId == NCPersistentIdentity.MemorySummary(owner, summary.SourceMemoryIds) &&
        summary.SourceWorldEventIds.Count is > 1 and <= AbsoluteMaximumSourceEvents &&
        summary.SourceWorldEventIds.All(id => id != Guid.Empty) &&
        summary.SourceWorldEventIds.SequenceEqual(summary.SourceWorldEventIds.Order()) &&
        summary.SourceWorldEventIds.Distinct().Count() == summary.SourceWorldEventIds.Count &&
        ValidOrderedKeys(summary.EventTypeKeys, AbsoluteMaximumSourceMemories) &&
        ValidOrderedParties(summary.Participants, AbsoluteMaximumParticipants) &&
        summary.SourceKinds.Count is > 0 and <= 5 && summary.SourceKinds.All(Enum.IsDefined) &&
        summary.SourceKinds.SequenceEqual(summary.SourceKinds.Order()) &&
        summary.SourceKinds.Distinct().Count() == summary.SourceKinds.Count &&
        ValidOrderedKeys(summary.EmotionalTagKeys, AbsoluteMaximumEmotionalTags) &&
        summary.OccurrenceCount == summary.SourceMemoryIds.Count && summary.MaximumSalience is >= 0 and <= 1_000 &&
        summary.MinimumConfidence is >= 0 and <= 1_000 && Enum.IsDefined(summary.Privacy) &&
        summary.FirstOccurredAt.IsValid && summary.LastOccurredAt >= summary.FirstOccurredAt &&
        summary.CreatedAt >= summary.LastOccurredAt && summary.ExpiresAt > summary.CreatedAt &&
        ValidRecall(summary.LastRecalledAt, summary.RecallCount, summary.CreatedAt) &&
        summary.LastChangedSequence is > 0 && summary.LastChangedSequence <= bookVersion &&
        summary.DataVersion > 0 && summary.LastAppliedEventId != Guid.Empty;

    private static bool ValidRecall(NCActiveWorldTime? recalledAt, int recallCount, NCActiveWorldTime createdAt) =>
        recallCount == 0 ? recalledAt == null : recallCount > 0 && recalledAt >= createdAt;

    private static bool OrderedEpisodes(IReadOnlyList<NCEpisodicMemorySnapshot> episodes) =>
        episodes.Zip(episodes.Skip(1), (left, right) =>
            left.LastChangedSequence > right.LastChangedSequence ||
            left.LastChangedSequence == right.LastChangedSequence && left.MemoryId.Value.CompareTo(right.MemoryId.Value) < 0)
            .All(value => value);

    private static bool OrderedSummaries(IReadOnlyList<NCConsolidatedMemorySnapshot> summaries) =>
        summaries.Zip(summaries.Skip(1), (left, right) =>
            left.LastChangedSequence > right.LastChangedSequence ||
            left.LastChangedSequence == right.LastChangedSequence && left.SummaryId.Value.CompareTo(right.SummaryId.Value) < 0)
            .All(value => value);

    private static bool ValidOrderedParties(IReadOnlyList<NCPartyRef> parties, int maximum) =>
        parties.Count <= maximum && parties.All(party => party.IsValid) &&
        parties.SequenceEqual(parties.OrderBy(party => party.ToString(), StringComparer.Ordinal)) &&
        parties.Distinct().Count() == parties.Count;

    private static bool ValidOrderedKeys(IReadOnlyList<string> keys, int maximum) =>
        keys.Count <= maximum && keys.All(IsStableKey) &&
        keys.SequenceEqual(keys.Order(StringComparer.Ordinal)) && keys.Distinct().Count() == keys.Count;

    private static bool Valid(MemoryApplicationSnapshot? marker, Guid expected)
    {
        if (marker == null || marker.ApplicationId != expected || string.IsNullOrWhiteSpace(marker.Operation) ||
            marker.BookId != NCPersistentIdentity.MemoryBook(marker.Owner) || !ValidOwner(marker.Owner) ||
            marker.OperationEventId == Guid.Empty || !Fingerprint(marker.Fingerprint) || !marker.Initiator.IsValid ||
            marker.ResultingBookVersion < 0 || !marker.AppliedAt.IsValid || marker.LastAppliedEventId == Guid.Empty)
        {
            return false;
        }

        return marker.Operation switch
        {
            RememberOperation => marker.MemoryId is { } memoryId && memoryId.IsValid &&
                memoryId == NCPersistentIdentity.Memory(marker.Owner, marker.OperationEventId) &&
                marker.ApplicationId == NCPersistentIdentity.MemoryApplication(memoryId),
            ConsolidationOperation => marker.MemoryId == null && marker.ApplicationId ==
                NCPersistentIdentity.MemoryConsolidationApplication(marker.BookId, marker.OperationEventId),
            RecallOperation => marker.MemoryId == null && marker.ApplicationId ==
                NCPersistentIdentity.MemoryRecallApplication(marker.BookId, marker.OperationEventId),
            _ => false,
        };
    }

    private static NCMemoryBookRecord? DeserializeBook(
        NCAggregateSnapshotData stored,
        NCMemoryBookId expected,
        NCPartyRef owner)
    {
        if (stored.AggregateType != BookAggregateType || stored.AggregateId != BookKey(expected) ||
            stored.FormatVersion != SnapshotFormatVersion || stored.AggregateVersion is < 1 or > int.MaxValue ||
            !stored.SessionEpoch.IsValid || !stored.ActiveWorldTime.IsValid)
        {
            return null;
        }
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCMemoryBookSnapshot>(stored.Payload, JsonOptions);
            return Valid(snapshot, expected, owner) && snapshot!.DataVersion == stored.AggregateVersion
                ? new NCMemoryBookRecord(snapshot, stored.AggregateVersion, stored.SessionEpoch, stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Expired(NCEpisodicMemorySnapshot episode, NCActiveWorldTime now) =>
        episode.ExpiresAt is { } expiresAt && expiresAt <= now;

    private static bool Matches(
        IReadOnlyList<NCPartyRef> recordParticipants,
        IReadOnlyList<string> recordEventTypes,
        IReadOnlyList<string> recordEmotions,
        IReadOnlySet<NCPartyRef> participants,
        IReadOnlySet<string> eventTypes,
        IReadOnlySet<string> emotions) =>
        (participants.Count == 0 || recordParticipants.Any(participants.Contains)) &&
        (eventTypes.Count == 0 || recordEventTypes.Any(eventTypes.Contains)) &&
        (emotions.Count == 0 || recordEmotions.Any(emotions.Contains));

    private static int Relevance(
        int salience,
        int confidence,
        NCActiveWorldTime occurredAt,
        IReadOnlyList<NCPartyRef> recordParticipants,
        IReadOnlyList<string> recordEventTypes,
        IReadOnlyList<string> recordEmotions,
        IReadOnlySet<NCPartyRef> participants,
        IReadOnlySet<string> eventTypes,
        IReadOnlySet<string> emotions,
        NCActiveWorldTime now,
        NCMemoryPolicyPrototype policy)
    {
        long score = (long) salience * policy.SalienceWeight + (long) confidence * policy.ConfidenceWeight;
        score += (long) recordParticipants.Count(participants.Contains) * policy.ParticipantMatchBonus;
        score += (long) recordEventTypes.Count(eventTypes.Contains) * policy.EventTypeMatchBonus;
        score += (long) recordEmotions.Count(emotions.Contains) * policy.EmotionalTagMatchBonus;
        if (now.ElapsedSince(occurredAt) <= policy.RecentMemoryWindow)
            score += policy.RecentMemoryBonus;
        return (int) Math.Min(score, int.MaxValue);
    }

    private static string RememberFingerprint(NCRememberWorldEventRequest request, ResolvedMemoryEvent resolved) =>
        HashFingerprint([
            RememberOperation,
            request.Owner.ToString(),
            request.WorldEventId.ToString("D"),
            resolved.EventTypeKey,
            resolved.ConsolidationKey,
            string.Join(',', resolved.SourceWorldEventIds),
            string.Join(',', resolved.Participants),
            ((byte) request.Source.Kind).ToString(),
            request.Source.SourceParty.ToString(),
            string.Join(',', resolved.EmotionalTagKeys),
            resolved.Salience.ToString(),
            resolved.Confidence.ToString(),
            ((byte) resolved.Privacy).ToString(),
            ((byte) resolved.RetentionClass).ToString(),
            resolved.Critical.ToString(),
            resolved.CanConsolidate.ToString(),
            resolved.Lifetime?.Ticks.ToString() ?? "permanent",
            request.Initiator.ToString(),
        ]);

    private static string ConsolidationFingerprint(NCConsolidateMemoryRequest request) => HashFingerprint([
        ConsolidationOperation,
        request.Owner.ToString(),
        request.ConsolidationEventId.ToString("D"),
        request.Initiator.ToString(),
    ]);

    private static string RecallFingerprint(NCRecordMemoryRecallRequest request) => HashFingerprint([
        RecallOperation,
        request.Owner.ToString(),
        request.RecallEventId.ToString("D"),
        string.Join(',', request.MemoryIds.Select(id => id.Value).Order()),
        string.Join(',', request.SummaryIds.Select(id => id.Value).Order()),
        request.Initiator.ToString(),
    ]);

    private static string HashFingerprint(IEnumerable<string> values)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', values));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static bool Fingerprint(string value) =>
        value.Length == 64 && value.All(char.IsAsciiHexDigitLower);

    private static bool IsStableKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumStableKeyLength ||
            value != value.Trim().ToLowerInvariant())
        {
            return false;
        }
        return value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    private static string NormalizeStableKey(string value) => value.Trim().ToLowerInvariant();

    private sealed record ResolvedMemoryEvent(
        string EventTypeKey,
        string ConsolidationKey,
        IReadOnlyList<Guid> SourceWorldEventIds,
        IReadOnlyList<NCPartyRef> Participants,
        IReadOnlyList<string> EmotionalTagKeys,
        int Salience,
        int Confidence,
        NCMemoryPrivacy Privacy,
        NCMemoryRetentionClass RetentionClass,
        bool Critical,
        bool CanConsolidate,
        TimeSpan? Lifetime);
}
