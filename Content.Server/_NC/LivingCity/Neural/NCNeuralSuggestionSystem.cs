// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.Social;
using Content.Server._NC.LivingCity.Time;
using Content.Server.Chat.Systems;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.Chat;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Neural;

/// <summary>
/// Converts a validated neural answer into a short-lived BDI utility hint. The hint remains ordinary Blackboard
/// input; safety priorities, planner routing and every gameplay mutation authority remain deterministic.
/// </summary>
public sealed partial class NCNeuralSuggestionSystem : EntitySystem
{
    private static readonly ProtoId<NCNeuralPolicyPrototype> DefaultPolicy = "Default";
    private const string SuggestionSource = "neural-suggestion";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCognitionSystem _cognition = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCConversationSessionSystem _conversations = default!;
    [Dependency] private ChatSystem _chat = default!;

    private readonly Dictionary<NCActorId, AppliedSuggestion> _suggestions = [];
    private readonly PriorityQueue<SuggestionExpiry, long> _expiries = new();
    private readonly Dictionary<Guid, DialogueCorrelation> _dialogues = [];
    private NCNeuralPolicyPrototype? _policy;
    private bool _active;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(_ => _active = false);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Clear());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Clear());
        SubscribeLocalEvent<NCNeuralResultEvent>(OnResult);
        SubscribeLocalEvent<NCCognitionCollectFactsEvent>(OnCollectFacts);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_active || !_clock.TryGetCurrentTime(out _, out var now))
            return;

        // The expiry heap avoids a per-tick scan across all neural-eligible actors.
        while (_expiries.TryPeek(out var expiry, out var ticks) && ticks <= now.Ticks)
        {
            _expiries.Dequeue();
            if (_suggestions.TryGetValue(expiry.ActorId, out var current) && current.RequestId == expiry.RequestId)
                _suggestions.Remove(expiry.ActorId);
        }
    }

    public bool TryTrackDialogue(Guid requestId, NCConversationSessionId sessionId, int conversationVersion)
    {
        if (!_active || _policy == null || requestId == Guid.Empty || !sessionId.IsValid || conversationVersion < 1 ||
            _dialogues.Count >= _policy.MaximumPendingCorrelations)
            return false;
        return _dialogues.TryAdd(requestId, new DialogueCorrelation(sessionId, conversationVersion));
    }

    public void ForgetDialogue(Guid requestId) => _dialogues.Remove(requestId);

    private void OnStarted(NCActiveWorldSessionStartedEvent args)
    {
        Clear();
        if (_prototypes.TryIndex(DefaultPolicy, out NCNeuralPolicyPrototype? policy))
        {
            _policy = policy;
            _active = true;
        }
    }

    private void OnResult(NCNeuralResultEvent args)
    {
        var result = args.Result;
        _dialogues.Remove(result.Request.RequestId, out var dialogue);
        var policy = _policy;
        var response = result.Response;
        var hasCurrentTime = _clock.TryGetCurrentTime(out var epoch, out var now);
        // Confidence gates planning influence. A dialogue line has no mutation authority and is instead guarded by
        // its conversation correlation, speech-act allowlist and ordinary ChatSystem checks.
        var meetsConfidence = result.Request.Kind == NCNeuralRequestKind.DialogueContinuation ||
            response?.Confidence >= policy?.MinimumSuggestionConfidence;
        var accepted = _active && policy != null && response != null && hasCurrentTime &&
            result.Status == NCNeuralResultStatus.Accepted &&
            response.ContextVersion == result.Request.ContextVersion &&
            meetsConfidence &&
            result.Request.AllowedCapabilityIds.Contains(response.SelectedCapabilityId) &&
            epoch == result.Request.SessionEpoch && result.Request.ExpiresAt > now;

        if (result.Request.Kind == NCNeuralRequestKind.DialogueContinuation && dialogue != null)
        {
            if (accepted && result.Response is { } dialogueResponse)
                TryDeliverDialogue(result.Request.ActorId, result.Request.RequestId, dialogue, dialogueResponse);
            else
                TryDeliverDialogueFallback(result.Request.ActorId, result.Request.RequestId, dialogue);
            return;
        }

        if (!accepted || response == null || policy == null)
            return;

        if (_suggestions.Count < policy.MaximumAppliedSuggestions || _suggestions.ContainsKey(result.Request.ActorId))
        {
            var expiresAt = now.Advance(policy.SuggestionLifetime);
            var suggestion = new AppliedSuggestion(
                result.Request.RequestId,
                response.SelectedCapabilityId,
                result.Request.ContextVersion,
                response.Confidence,
                now,
                expiresAt);
            _suggestions[result.Request.ActorId] = suggestion;
            _expiries.Enqueue(new SuggestionExpiry(result.Request.ActorId, suggestion.RequestId), expiresAt.Ticks);
            _cognition.ForceSchedule(result.Request.ActorId, now);
        }

    }

    private void OnCollectFacts(NCCognitionCollectFactsEvent args)
    {
        if (!_active || _policy == null || !_suggestions.TryGetValue(args.ActorId, out var suggestion) ||
            suggestion.ExpiresAt <= args.Now)
            return;

        var confidence = suggestion.Confidence / 1000f;
        var subject = new NCCognitionSubjectRef(NCCognitionReferenceKind.Actor, args.ActorId.ToString());
        args.Sink.Add(new NCCognitionFact(
            $"social.utility-modifier.{suggestion.CapabilityId}",
            subject,
            NCCognitionDomain.Social,
            SuggestionSource,
            suggestion.ContextVersion,
            NCCognitionValue.FromNumber(_policy.MaximumUtilityBoost * confidence),
            suggestion.AcceptedAt,
            suggestion.ExpiresAt));
    }

    private void TryDeliverDialogue(
        NCActorId actorId,
        Guid requestId,
        DialogueCorrelation dialogue,
        NCNeuralResponseEnvelope response)
    {
        if (string.IsNullOrWhiteSpace(response.RenderedText) || !TryMapSpeechAct(response.SpeechActKey, out var act) ||
            !_conversations.TryGet(dialogue.SessionId, out var session) ||
            session.ConversationVersion != dialogue.ConversationVersion ||
            session.Channel != NCConversationChannel.InPerson ||
            !session.Participants.Contains(NCPartyRef.ForActor(actorId)) ||
            !_runtime.TryGetEntity(actorId, out var entityUid) || !Exists(entityUid))
            return;

        var recorded = _conversations.TryRecordTurn(new NCConversationTurnRequest(
            session.SessionId,
            NCPartyRef.ForActor(actorId),
            act,
            $"neural.{response.SpeechActKey.ToLowerInvariant()}",
            $"neural:{requestId:N}",
            requestId), out _, out _);
        if (!recorded)
            return;

        // ChatSystem remains the physical speech authority and applies the normal speech blockers and sanitization.
        _chat.TrySendInGameICMessage(
            entityUid,
            response.RenderedText.Trim(),
            InGameICChatType.Speak,
            hideChat: false,
            checkRadioPrefix: false);
    }

    private void TryDeliverDialogueFallback(
        NCActorId actorId,
        Guid requestId,
        DialogueCorrelation dialogue)
    {
        if (!_conversations.TryGet(dialogue.SessionId, out var session) ||
            session.ConversationVersion != dialogue.ConversationVersion ||
            session.Channel != NCConversationChannel.InPerson ||
            !_runtime.TryGetEntity(actorId, out var entityUid) || !Exists(entityUid) ||
            !TryComp<NCNeuralConversationTargetComponent>(entityUid, out var target))
        {
            return;
        }

        if (!_conversations.TryRecordTurn(new NCConversationTurnRequest(
                session.SessionId,
                NCPartyRef.ForActor(actorId),
                NCSpeechActKind.Statement,
                "neural.fallback",
                $"neural:{requestId:N}",
                requestId), out _, out _))
        {
            return;
        }

        _chat.TrySendInGameICMessage(
            entityUid,
            Loc.GetString(target.FallbackMessage),
            InGameICChatType.Speak,
            hideChat: false,
            checkRadioPrefix: false);
    }

    private static bool TryMapSpeechAct(string value, out NCSpeechActKind act)
    {
        act = value.ToLowerInvariant() switch
        {
            "greeting" => NCSpeechActKind.Greeting,
            "statement" => NCSpeechActKind.Statement,
            "question" => NCSpeechActKind.Question,
            "answer" => NCSpeechActKind.Answer,
            "offer" => NCSpeechActKind.Offer,
            "acceptance" => NCSpeechActKind.Acceptance,
            "refusal" => NCSpeechActKind.Refusal,
            "threat" => NCSpeechActKind.Threat,
            "farewell" => NCSpeechActKind.Farewell,
            _ => default,
        };
        return value.Equals(act.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private void Clear()
    {
        _active = false;
        _policy = null;
        _suggestions.Clear();
        _expiries.Clear();
        _dialogues.Clear();
    }

    private sealed record AppliedSuggestion(
        Guid RequestId,
        string CapabilityId,
        long ContextVersion,
        int Confidence,
        NCActiveWorldTime AcceptedAt,
        NCActiveWorldTime ExpiresAt);

    private readonly record struct SuggestionExpiry(NCActorId ActorId, Guid RequestId);
    private sealed record DialogueCorrelation(NCConversationSessionId SessionId, int ConversationVersion);
}
