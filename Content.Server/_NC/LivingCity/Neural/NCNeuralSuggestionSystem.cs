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
    private const string DirectiveCapability = "NCCognitionAcceptDirective";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCognitionSystem _cognition = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCCityLocalPerceptionSystem _perception = default!;
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

    public bool TryTrackDialogue(
        Guid requestId,
        NCConversationSessionId sessionId,
        int conversationVersion,
        string fallbackMessage)
    {
        if (!_active || _policy == null || requestId == Guid.Empty || !sessionId.IsValid || conversationVersion < 1 ||
            string.IsNullOrWhiteSpace(fallbackMessage) ||
            _dialogues.Count >= _policy.MaximumPendingCorrelations)
            return false;
        return _dialogues.TryAdd(requestId, new DialogueCorrelation(sessionId, conversationVersion, fallbackMessage));
    }

    public void ForgetDialogue(Guid requestId) => _dialogues.Remove(requestId);

    public bool TryGetSuggestionSnapshot(NCActorId actorId, out NCNeuralSuggestionSnapshot snapshot)
    {
        if (!_suggestions.TryGetValue(actorId, out var value))
        {
            snapshot = default!;
            return false;
        }
        snapshot = new NCNeuralSuggestionSnapshot(value.RequestId, value.CapabilityId, value.GoalKey,
            value.TargetHandle, value.Confidence, value.AcceptedAt, value.ExpiresAt);
        return true;
    }

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
            (response.Intent?.TargetHandle.Length is not > 0 ||
             _perception.TryResolveSubjectHandle(result.Request.ActorId, response.Intent.TargetHandle, now, out _)) &&
            epoch == result.Request.SessionEpoch && result.Request.ExpiresAt > now;

        if (result.Request.Kind == NCNeuralRequestKind.DialogueContinuation && dialogue != null)
        {
            if (accepted && policy != null && result.Response is { } dialogueResponse)
            {
                if (dialogueResponse.SelectedCapabilityId == DirectiveCapability && dialogueResponse.Intent != null)
                {
                    // Natural orders are deferred: the domain authority must validate and commit them before the NPC
                    // may acknowledge anything. The handler later releases both the BDI hint and the spoken response.
                    var proposed = new NCNeuralDialogueDirectiveProposedEvent(
                        result.Request.ActorId,
                        dialogue.SessionId,
                        dialogue.ConversationVersion,
                        result.Request,
                        dialogueResponse);
                    RaiseLocalEvent(proposed);
                    if (!proposed.Handled)
                        TryDeliverDialogueFallback(result.Request.ActorId, result.Request.RequestId, dialogue);
                    return;
                }
                if (dialogueResponse.Intent != null)
                    ApplySuggestion(result.Request, dialogueResponse, policy, now);
                // Non-directive dialogue intents retain their ordinary immediate cognition path.
                TryDeliverDialogue(result.Request.ActorId, result.Request.RequestId, dialogue, dialogueResponse);
            }
            else
                TryDeliverDialogueFallback(result.Request.ActorId, result.Request.RequestId, dialogue);
            return;
        }

        if (!accepted || response == null || policy == null)
            return;

        ApplySuggestion(result.Request, response, policy, now);

    }

    private void ApplySuggestion(
        NCNeuralRequestEnvelope request,
        NCNeuralResponseEnvelope response,
        NCNeuralPolicyPrototype policy,
        NCActiveWorldTime now)
    {
        if (response.Intent == null ||
            _suggestions.Count >= policy.MaximumAppliedSuggestions && !_suggestions.ContainsKey(request.ActorId))
        {
            return;
        }
        var expiresAt = now.Advance(policy.SuggestionLifetime);
        var suggestion = new AppliedSuggestion(
            request.RequestId,
            response.SelectedCapabilityId,
            request.ContextVersion,
            response.Confidence,
            response.Intent.TargetHandle,
            response.Intent.GoalKey,
            now,
            expiresAt);
        _suggestions[request.ActorId] = suggestion;
        _expiries.Enqueue(new SuggestionExpiry(request.ActorId, suggestion.RequestId), expiresAt.Ticks);
        _cognition.ForceSchedule(request.ActorId, now);
    }

    /// <summary>Releases a directive suggestion only after its server-owned authority committed the order.</summary>
    public bool TryApplyDeferredSuggestion(NCNeuralRequestEnvelope request, NCNeuralResponseEnvelope response)
    {
        if (!_active || _policy == null || !_clock.TryGetCurrentTime(out var epoch, out var now) ||
            epoch != request.SessionEpoch || request.ExpiresAt <= now || response.Intent == null)
            return false;
        ApplySuggestion(request, response, _policy, now);
        return true;
    }

    /// <summary>Speaks a deferred order response after the executor has observed that the order actually started.</summary>
    public bool TryDeliverDeferredDialogue(
        NCActorId actorId,
        Guid requestId,
        NCConversationSessionId sessionId,
        int conversationVersion,
        NCNeuralResponseEnvelope response) =>
        TryDeliverDialogue(actorId, requestId,
            new DialogueCorrelation(sessionId, conversationVersion, "nc-neural-conversation-fallback"), response);

    private void OnCollectFacts(NCCognitionCollectFactsEvent args)
    {
        if (!_active || _policy == null || !_suggestions.TryGetValue(args.ActorId, out var suggestion) ||
            suggestion.ExpiresAt <= args.Now || suggestion.TargetHandle.Length > 0 &&
            !_perception.TryResolveSubjectHandle(args.ActorId, suggestion.TargetHandle, args.Now, out _))
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
        if (suggestion.TargetHandle.Length > 0)
        {
            args.Sink.Add(new NCCognitionFact(
                $"neural.target.{suggestion.CapabilityId}",
                new NCCognitionSubjectRef(NCCognitionReferenceKind.StableKey, suggestion.TargetHandle),
                NCCognitionDomain.Core,
                SuggestionSource,
                suggestion.ContextVersion,
                NCCognitionValue.FromStableKey(suggestion.GoalKey),
                suggestion.AcceptedAt,
                suggestion.ExpiresAt));
        }
    }

    private bool TryDeliverDialogue(
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
            return false;

        var recorded = _conversations.TryRecordTurn(new NCConversationTurnRequest(
            session.SessionId,
            NCPartyRef.ForActor(actorId),
            act,
            $"neural.{response.SpeechActKey.ToLowerInvariant()}",
            $"neural:{requestId:N}",
            requestId,
            response.SummaryKey), out _, out _, response.RenderedText.Trim());
        if (!recorded)
            return false;

        // ChatSystem remains the physical speech authority and applies the normal speech blockers and sanitization.
        _chat.TrySendInGameICMessage(
            entityUid,
            response.RenderedText.Trim(),
            InGameICChatType.Speak,
            hideChat: false,
            checkRadioPrefix: false);
        return true;
    }

    private void TryDeliverDialogueFallback(
        NCActorId actorId,
        Guid requestId,
        DialogueCorrelation dialogue)
    {
        if (!_conversations.TryGet(dialogue.SessionId, out var session) ||
            session.ConversationVersion != dialogue.ConversationVersion ||
            session.Channel != NCConversationChannel.InPerson ||
            !_runtime.TryGetEntity(actorId, out var entityUid) || !Exists(entityUid))
        {
            return;
        }

        var fallbackText = Loc.GetString(dialogue.FallbackMessage);

        if (!_conversations.TryRecordTurn(new NCConversationTurnRequest(
                session.SessionId,
                NCPartyRef.ForActor(actorId),
                NCSpeechActKind.Statement,
                "neural.fallback",
                $"neural:{requestId:N}",
                requestId), out _, out _, fallbackText))
        {
            return;
        }

        _chat.TrySendInGameICMessage(
            entityUid,
            fallbackText,
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
        string TargetHandle,
        string GoalKey,
        NCActiveWorldTime AcceptedAt,
        NCActiveWorldTime ExpiresAt);

    private readonly record struct SuggestionExpiry(NCActorId ActorId, Guid RequestId);
    private sealed record DialogueCorrelation(
        NCConversationSessionId SessionId,
        int ConversationVersion,
        string FallbackMessage);
}

/// <summary>
/// Published before a natural order is spoken. Exactly one domain authority must claim it and later release the
/// response after authorization and observable execution have started.
/// </summary>
public sealed class NCNeuralDialogueDirectiveProposedEvent(
    NCActorId actorId,
    NCConversationSessionId sessionId,
    int conversationVersion,
    NCNeuralRequestEnvelope request,
    NCNeuralResponseEnvelope response) : EntityEventArgs
{
    public NCActorId ActorId { get; } = actorId;
    public NCConversationSessionId SessionId { get; } = sessionId;
    public int ConversationVersion { get; } = conversationVersion;
    public NCNeuralRequestEnvelope Request { get; } = request;
    public NCNeuralResponseEnvelope Response { get; } = response;
    public bool Handled { get; set; }
}

public sealed record NCNeuralSuggestionSnapshot(
    Guid RequestId,
    string CapabilityId,
    string GoalKey,
    string TargetHandle,
    int Confidence,
    NCActiveWorldTime AcceptedAt,
    NCActiveWorldTime ExpiresAt);
