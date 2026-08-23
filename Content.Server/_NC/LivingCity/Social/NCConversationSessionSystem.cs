// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Linq;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.Identity;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.LivingCity.Social;

/// <summary>
/// Server-owned, round-local conversation state. It stores semantic acts and external record IDs, never player text;
/// a sentence cannot execute a gameplay mutation or pretend that a promise, payment, or transfer happened.
/// </summary>
public sealed partial class NCConversationSessionSystem : EntitySystem
{
    private static readonly ProtoId<NCConversationPolicyPrototype> DefaultPolicy = "Default";
    private const int AbsoluteMaximumSessions = 4_096;
    private const int MaximumStableKeyLength = 96;
    private const int MaximumExternalReferenceLength = 160;

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private IGameTiming _timing = default!;

    private readonly object _sync = new();
    private readonly Dictionary<NCConversationSessionId, NCConversationSessionSnapshot> _active = [];
    private readonly Dictionary<NCPartyRef, HashSet<NCConversationSessionId>> _byParticipant = [];
    private readonly Queue<NCConversationSessionSnapshot> _recent = [];
    private TimeSpan _nextSweep;
    private bool _roundActive;

    public int ActiveSessionCount
    {
        get
        {
            lock (_sync)
                return _active.Count;
        }
    }

    public int RecentSessionCount
    {
        get
        {
            lock (_sync)
                return _recent.Count;
        }
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(OnSessionClosed);
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(OnClockFailed);
    }

    public override void Shutdown()
    {
        Clear();
        base.Shutdown();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_roundActive || _timing.CurTime < _nextSweep || !TryGetPolicy(out var policy) ||
            !_clock.TryGetCurrentTime(out _, out var now))
            return;
        _nextSweep = _timing.CurTime + policy.ExpirySweepInterval;
        lock (_sync)
        {
            foreach (var session in _active.Values.ToArray())
            {
                var idle = now.ElapsedSince(session.LastActivityAt) >= policy.IdleTimeout;
                var old = now.ElapsedSince(session.StartedAt) >= policy.MaximumLifetime;
                if (idle || old)
                    Archive(session with
                    {
                        Status = NCConversationStatus.Expired,
                        EndReason = idle ? "idle-timeout" : "maximum-lifetime",
                        LastActivityAt = now,
                        ConversationVersion = checked(session.ConversationVersion + 1),
                    }, policy);
            }
        }
    }

    public bool TryStart(
        NCConversationStartRequest request,
        out NCConversationSessionSnapshot session,
        out string? error)
    {
        session = default!;
        error = null;
        if (!_roundActive || !_clock.TryGetCurrentTime(out _, out var now) || !TryGetPolicy(out var policy) ||
            !Valid(request, policy))
        {
            error = "The conversation request, policy, or active-world clock is invalid.";
            return false;
        }
        lock (_sync)
        {
            if (_active.TryGetValue(request.SessionId, out var existing))
            {
                session = existing;
                return SameStart(existing, request);
            }
            if (_active.Count >= policy.MaximumActiveSessions || request.Participants.Any(participant =>
                    _byParticipant.TryGetValue(participant, out var ids) &&
                    ids.Count >= policy.MaximumSessionsPerParticipant))
            {
                error = "The bounded active conversation capacity is exhausted.";
                return false;
            }
            session = new NCConversationSessionSnapshot(
                request.SessionId,
                request.Participants.Distinct().OrderBy(value => value.ToString(), StringComparer.Ordinal).ToArray(),
                request.Channel,
                Normalize(request.LocationContext),
                now,
                now,
                1,
                request.TopicKeys.Select(Normalize).Distinct().Order(StringComparer.Ordinal).ToArray(),
                request.RelevantFactIds.Distinct().Order().ToArray(),
                request.MaximumDisclosure,
                request.Language,
                request.Locale,
                [],
                NCConversationStatus.Starting,
                string.Empty);
            _active.Add(session.SessionId, session);
            foreach (var participant in session.Participants)
            {
                if (!_byParticipant.TryGetValue(participant, out var sessions))
                    _byParticipant.Add(participant, sessions = []);
                sessions.Add(session.SessionId);
            }
            return true;
        }
    }

    public bool TryRecordTurn(
        NCConversationTurnRequest request,
        out NCConversationSessionSnapshot session,
        out string? error,
        string? transientUtterance = null)
    {
        session = default!;
        error = null;
        if (!_roundActive || !_clock.TryGetCurrentTime(out _, out var now) || !TryGetPolicy(out var policy) ||
            !request.SessionId.IsValid || !request.Speaker.IsValid || !Enum.IsDefined(request.Act) ||
            !IsStableKey(request.SemanticKey) || request.ExternalReference == null ||
            request.ExternalReference.Length > MaximumExternalReferenceLength ||
            request.WorldEventId == Guid.Empty)
        {
            error = "The conversation turn is invalid.";
            return false;
        }
        lock (_sync)
        {
            if (!_active.TryGetValue(request.SessionId, out var current) ||
                !current.Participants.Contains(request.Speaker) || current.Turns.Count >= policy.MaximumTurns)
            {
                error = "The conversation is unavailable or reached its turn limit.";
                return false;
            }
            var turns = current.Turns.ToList();
            turns.Add(new NCConversationTurn(
                turns.Count + 1,
                request.Speaker,
                request.Act,
                Normalize(request.SemanticKey),
                request.ExternalReference.Trim(),
                request.WorldEventId,
                now));
            session = current with
            {
                Turns = turns,
                Status = NCConversationStatus.Active,
                LastActivityAt = now,
                ConversationVersion = checked(current.ConversationVersion + 1),
            };
            _active[session.SessionId] = session;
        }
        // Raw speech is carried only by this round-local event. The durable conversation snapshot keeps semantic
        // keys and external references, so provider prose never becomes persistence authority.
        RaiseLocalEvent(new NCConversationTurnRecordedEvent(session, transientUtterance));
        return true;
    }

    public bool TryTransition(
        NCConversationSessionId sessionId,
        NCConversationStatus status,
        string reason,
        out NCConversationSessionSnapshot session)
    {
        session = default!;
        if (!_roundActive || !_clock.TryGetCurrentTime(out _, out var now) || !TryGetPolicy(out var policy) ||
            !sessionId.IsValid || !Enum.IsDefined(status) || status == NCConversationStatus.Starting ||
            reason == null || reason.Length > MaximumStableKeyLength)
            return false;
        lock (_sync)
        {
            if (!_active.TryGetValue(sessionId, out var current))
                return false;
            session = current with
            {
                Status = status,
                EndReason = reason.Trim().ToLowerInvariant(),
                LastActivityAt = now,
                ConversationVersion = checked(current.ConversationVersion + 1),
            };
            if (status is NCConversationStatus.Interrupted or NCConversationStatus.Ended or
                NCConversationStatus.Expired)
                Archive(session, policy);
            else
                _active[sessionId] = session;
            return true;
        }
    }

    public bool TryGet(NCConversationSessionId sessionId, out NCConversationSessionSnapshot session)
    {
        lock (_sync)
        {
            if (_active.TryGetValue(sessionId, out session!))
                return true;
            session = _recent.LastOrDefault(value => value.SessionId == sessionId)!;
            return session != null;
        }
    }

    public IReadOnlyList<NCConversationSessionSnapshot> GetForParticipant(NCPartyRef participant, int limit)
    {
        if (!participant.IsValid || limit < 1)
            return [];
        lock (_sync)
        {
            return _active.Values.Concat(_recent)
                .Where(value => value.Participants.Contains(participant))
                .OrderByDescending(value => value.LastActivityAt.Ticks)
                .Take(Math.Min(limit, 50))
                .ToArray();
        }
    }

    public NCConversationSessionId RecordAgentActivity(
        NCConversationChannel channel,
        NCCharacterId sender,
        IReadOnlyList<NCCharacterId> recipients,
        string externalReference,
        NCSpeechActKind act = NCSpeechActKind.ChannelActivity)
    {
        if (!sender.IsValid || recipients.Count == 0)
            return default;
        var participants = new[] { sender }.Concat(recipients).Distinct()
            .Select(NCPartyRef.ForCharacter).ToArray();
        var id = NCConversationSessionId.New();
        var request = new NCConversationStartRequest(
            id,
            participants,
            channel,
            new NCInformationSubject(NCInformationSubjectKind.StableKey, "agent-network"),
            [channel.ToString().ToLowerInvariant()],
            [],
            NCMemoryPrivacy.Restricted,
            NCLanguage.Russian,
            NCLocale.RuRu);
        if (!TryStart(request, out _, out _))
            return default;
        TryRecordTurn(new NCConversationTurnRequest(
            id,
            NCPartyRef.ForCharacter(sender),
            act,
            channel.ToString().ToLowerInvariant(),
            externalReference,
            null), out _, out _);
        TryTransition(id, NCConversationStatus.Ended, "delivered", out _);
        return id;
    }

    public bool ValidatePrototypes(List<string> errors)
    {
        var initial = errors.Count;
        foreach (var value in _prototypes.EnumeratePrototypes<NCConversationPolicyPrototype>())
        {
            if (!Valid(value))
                errors.Add($"Conversation policy {value.ID} is invalid.");
        }
        if (!TryGetPolicy(out _))
            errors.Add("The Default conversation policy is missing or invalid.");
        return errors.Count == initial;
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        Clear();
        _roundActive = TryGetPolicy(out _);
        _nextSweep = _timing.CurTime;
    }

    private void OnSessionClosed(NCActiveWorldSessionClosedEvent args) => Clear();
    private void OnClockFailed(NCActiveWorldClockFailedEvent args) => Clear();

    private void Archive(NCConversationSessionSnapshot session, NCConversationPolicyPrototype policy)
    {
        _active.Remove(session.SessionId);
        foreach (var participant in session.Participants)
        {
            if (!_byParticipant.TryGetValue(participant, out var sessions))
                continue;
            sessions.Remove(session.SessionId);
            if (sessions.Count == 0)
                _byParticipant.Remove(participant);
        }
        _recent.Enqueue(session);
        while (_recent.Count > policy.MaximumRecentSessions)
            _recent.Dequeue();
    }

    private void Clear()
    {
        lock (_sync)
        {
            _roundActive = false;
            _active.Clear();
            _byParticipant.Clear();
            _recent.Clear();
        }
    }

    private bool TryGetPolicy(out NCConversationPolicyPrototype policy) =>
        _prototypes.TryIndex(DefaultPolicy, out policy!) && Valid(policy);

    private static bool Valid(NCConversationPolicyPrototype value) =>
        value.MaximumActiveSessions is > 0 and <= AbsoluteMaximumSessions &&
        value.MaximumRecentSessions is > 0 and <= AbsoluteMaximumSessions &&
        value.MaximumSessionsPerParticipant is > 0 and <= 32 &&
        value.MaximumParticipants is >= 2 and <= 64 && value.MaximumTopics is > 0 and <= 64 &&
        value.MaximumRelevantFacts is > 0 and <= 128 && value.MaximumTurns is > 0 and <= 256 &&
        value.IdleTimeout > TimeSpan.Zero && value.MaximumLifetime >= value.IdleTimeout &&
        value.ExpirySweepInterval > TimeSpan.Zero;

    private static bool Valid(NCConversationStartRequest value, NCConversationPolicyPrototype policy) =>
        value.SessionId.IsValid && value.Participants.Count is >= 2 &&
        value.Participants.Count <= policy.MaximumParticipants && value.Participants.All(item => item.IsValid) &&
        value.Participants.Distinct().Count() == value.Participants.Count && Enum.IsDefined(value.Channel) &&
        value.LocationContext.IsValid && value.LocationContext.StableId.Length <= 128 &&
        value.TopicKeys.Count <= policy.MaximumTopics && value.TopicKeys.All(IsStableKey) &&
        value.RelevantFactIds.Count <= policy.MaximumRelevantFacts &&
        value.RelevantFactIds.All(id => id != Guid.Empty) && Enum.IsDefined(value.MaximumDisclosure) &&
        Enum.IsDefined(value.Language) && Enum.IsDefined(value.Locale);

    private static bool SameStart(NCConversationSessionSnapshot existing, NCConversationStartRequest request) =>
        existing.Participants.SequenceEqual(
            request.Participants.Distinct().OrderBy(value => value.ToString(), StringComparer.Ordinal)) &&
        existing.Channel == request.Channel && existing.LocationContext == Normalize(request.LocationContext) &&
        existing.MaximumDisclosure == request.MaximumDisclosure && existing.Language == request.Language &&
        existing.Locale == request.Locale;

    private static NCInformationSubject Normalize(NCInformationSubject value) =>
        value with { StableId = value.StableId.Trim().ToLowerInvariant() };
    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
    private static bool IsStableKey(string value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumStableKeyLength && value == Normalize(value) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}

public sealed class NCConversationTurnRecordedEvent(
    NCConversationSessionSnapshot session,
    string? transientUtterance = null) : EntityEventArgs
{
    public NCConversationSessionSnapshot Session { get; } = session;
    public string? TransientUtterance { get; } = transientUtterance;
}
