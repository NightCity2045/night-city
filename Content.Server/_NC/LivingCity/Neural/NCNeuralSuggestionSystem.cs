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
/// Validates neural results, retains short diagnostics and forwards accepted choices to the typed action authority.
/// </summary>
public sealed partial class NCNeuralSuggestionSystem : EntitySystem
{
    private static readonly ProtoId<NCNeuralPolicyPrototype> DefaultPolicy = "Default";
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCNeuralActionExecutionSystem _execution = default!;
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
        int conversationVersion)
    {
        if (!_active || _policy == null || requestId == Guid.Empty || !sessionId.IsValid || conversationVersion < 1 ||
            _dialogues.Count >= _policy.MaximumPendingCorrelations)
            return false;
        return _dialogues.TryAdd(requestId, new DialogueCorrelation(sessionId, conversationVersion));
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
        var commands = response?.Commands ?? [];
        var accepted = _active && policy != null && response != null && hasCurrentTime &&
            result.Status == NCNeuralResultStatus.Accepted &&
            response.ContextVersion == result.Request.ContextVersion &&
            meetsConfidence &&
            commands.Count is >= 1 and <= 8 && commands.All(command =>
                result.Request.AllowedCapabilityIds.Contains(command.CapabilityId) &&
                (command.TargetHandle.Length == 0 ||
                 _perception.TryResolveSubjectHandle(result.Request.ActorId, command.TargetHandle, now, out _))) &&
            epoch == result.Request.SessionEpoch && result.Request.ExpiresAt > now;

        if (result.Request.Kind == NCNeuralRequestKind.DialogueContinuation && dialogue != null)
        {
            if (accepted && policy != null && result.Response is { } dialogueResponse)
            {
                if (dialogueResponse.Commands.Any(command => command.CapabilityId != "social.dialogue.reply"))
                    ApplySuggestion(result.Request, dialogueResponse, policy, now);
                // Dialogue may propose physical commands, but the typed executor remains their authority.
                TryDeliverDialogue(result.Request.ActorId, result.Request.RequestId, dialogue, dialogueResponse);
            }
            return;
        }

        if (!accepted || response == null || policy == null)
            return;

        ApplySuggestion(result.Request, response, policy, now);

    }

    private bool ApplySuggestion(
        NCNeuralRequestEnvelope request,
        NCNeuralResponseEnvelope response,
        NCNeuralPolicyPrototype policy,
        NCActiveWorldTime now)
    {
        if (response.Commands.Count == 0 ||
            _suggestions.Count >= policy.MaximumAppliedSuggestions && !_suggestions.ContainsKey(request.ActorId))
        {
            return false;
        }
        var primary = response.Commands[0];
        var expiresAt = now.Advance(policy.SuggestionLifetime);
        var suggestion = new AppliedSuggestion(
            request.RequestId,
            primary.CapabilityId,
            request.ContextVersion,
            response.Confidence,
            primary.TargetHandle,
            primary.GoalKey,
            now,
            expiresAt);
        _suggestions[request.ActorId] = suggestion;
        _expiries.Enqueue(new SuggestionExpiry(request.ActorId, suggestion.RequestId), expiresAt.Ticks);
        return _execution.TryAccept(request, response, now, out _);
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
        int ConversationVersion);
}

public sealed record NCNeuralSuggestionSnapshot(
    Guid RequestId,
    string CapabilityId,
    string GoalKey,
    string TargetHandle,
    int Confidence,
    NCActiveWorldTime AcceptedAt,
    NCActiveWorldTime ExpiresAt);
