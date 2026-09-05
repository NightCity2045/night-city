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
    private readonly Dictionary<NCActorId, AppliedIntent> _intents = [];
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
            if (_intents.TryGetValue(expiry.ActorId, out var intent) && intent.RequestId == expiry.RequestId)
                _intents.Remove(expiry.ActorId);
        }
    }

    public bool TryTrackDialogue(
        Guid requestId,
        NCConversationSessionId sessionId,
        int conversationVersion,
        long triggerTurnSequence,
        NCPartyRef triggerSpeaker,
        bool parallelReplies)
    {
        if (!_active || _policy == null || requestId == Guid.Empty || !sessionId.IsValid || conversationVersion < 1 ||
            triggerTurnSequence < 1 || !triggerSpeaker.IsValid ||
            _dialogues.Count >= _policy.MaximumPendingCorrelations)
            return false;
        return _dialogues.TryAdd(requestId, new DialogueCorrelation(
            sessionId,
            conversationVersion,
            triggerTurnSequence,
            triggerSpeaker,
            parallelReplies));
    }

    public void ForgetDialogue(Guid requestId) => _dialogues.Remove(requestId);

    public bool TryGetIntentSnapshot(NCActorId actorId, out NCNeuralIntentSnapshot snapshot)
    {
        if (!_intents.TryGetValue(actorId, out var value))
        {
            snapshot = default!;
            return false;
        }
        snapshot = new NCNeuralIntentSnapshot(
            value.RequestId,
            value.Key,
            value.TargetHandle,
            value.Priority,
            value.AcceptedAt,
            value.ExpiresAt);
        return true;
    }

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
        var accepted = _active && policy != null && response != null && hasCurrentTime &&
            result.Status == NCNeuralResultStatus.Accepted &&
            response.ContextVersion == result.Request.ContextVersion &&
            epoch == result.Request.SessionEpoch && result.Request.ExpiresAt > now;

        if (result.Request.Kind == NCNeuralRequestKind.DialogueContinuation && dialogue != null)
        {
            if (accepted && policy != null && result.Response is { } dialogueResponse &&
                TryGetCurrentDialogue(dialogue, out var currentSession))
            {
                if (TrySelectPhysicalCommands(result.Request, dialogueResponse, now,
                        out var physicalCommands, out var dialogueCommandFailure))
                {
                    if (physicalCommands.Length == 0 ||
                        ApplySuggestion(result.Request, dialogueResponse, physicalCommands, policy, now))
                    {
                        TrackIntent(result.Request, dialogueResponse, policy, now);
                    }
                }
                else if (dialogueCommandFailure != null)
                {
                    ReportBatchRejection(result.Request, dialogueCommandFailure, now);
                }
                // Dialogue may propose physical commands, but the typed executor remains their authority.
                TryDeliverDialogue(result.Request.ActorId, result.Request.RequestId, currentSession,
                    dialogueResponse);
                RaiseDeliberationDirectives(result.Request, dialogueResponse, currentSession.SessionId);
            }
            return;
        }

        if (!accepted || response == null || policy == null)
            return;

        if (TrySelectPhysicalCommands(result.Request, response, now,
                out var acceptedPhysicalCommands, out var commandFailure))
        {
            if (acceptedPhysicalCommands.Length == 0 ||
                ApplySuggestion(result.Request, response, acceptedPhysicalCommands, policy, now))
            {
                TrackIntent(result.Request, response, policy, now);
            }
        }
        else if (commandFailure != null)
        {
            ReportBatchRejection(result.Request, commandFailure, now);
        }
        if (result.Request.Kind == NCNeuralRequestKind.StrategicDecision &&
            (response.ShouldSpeak || IsDirectAssault(result.Request)))
            TryDeliverAutonomousSpeech(result.Request.ActorId, response);
        RaiseDeliberationDirectives(result.Request, response, null);

    }

    private void RaiseDeliberationDirectives(
        NCNeuralRequestEnvelope request,
        NCNeuralResponseEnvelope response,
        NCConversationSessionId? conversationSessionId)
    {
        if (response.DecisionMode == "sense" && response.SenseRequests.Count > 0 && request.SenseDepth < 2)
        {
            RaiseLocalEvent(new NCNeuralSenseRequestedEvent(
                request,
                response.SenseRequests.Take(2).ToArray(),
                conversationSessionId));
        }

        if (response.Wake is { AfterSeconds: >= 0 and <= 86400 } wake)
            RaiseLocalEvent(new NCNeuralWakeScheduledEvent(request, wake.On, wake.AfterSeconds));
    }

    private bool TryDeliverAutonomousSpeech(NCActorId actorId, NCNeuralResponseEnvelope response)
    {
        if (string.IsNullOrWhiteSpace(response.RenderedText) ||
            !_runtime.TryGetEntity(actorId, out var entityUid) || !Exists(entityUid))
            return false;
        _chat.TrySendInGameICMessage(entityUid, response.RenderedText.Trim(), InGameICChatType.Speak,
            hideChat: false, checkRadioPrefix: false);
        return true;
    }

    private bool ApplySuggestion(
        NCNeuralRequestEnvelope request,
        NCNeuralResponseEnvelope response,
        IReadOnlyList<NCNeuralCommandProposal> physicalCommands,
        NCNeuralPolicyPrototype policy,
        NCActiveWorldTime now)
    {
        if (physicalCommands.Count == 0)
            return false;
        var primary = physicalCommands[0];
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
        if (!_execution.TryAccept(request, response with { Commands = physicalCommands }, now, out var failure))
        {
            ReportBatchRejection(request, failure ?? "neural-command-batch-rejected", now);
            return false;
        }
        _suggestions[request.ActorId] = suggestion;
        _expiries.Enqueue(new SuggestionExpiry(request.ActorId, suggestion.RequestId), expiresAt.Ticks);
        return true;
    }

    private void TrackIntent(
        NCNeuralRequestEnvelope request,
        NCNeuralResponseEnvelope response,
        NCNeuralPolicyPrototype policy,
        NCActiveWorldTime now)
    {
        _intents.Remove(request.ActorId);
        if (response.Intent is not { } intent ||
            !ValidCurrentTarget(request, intent.TargetHandle, now))
        {
            return;
        }

        var expiresAt = now.Advance(policy.SuggestionLifetime);
        _intents[request.ActorId] = new AppliedIntent(
            request.RequestId,
            intent.Key,
            intent.TargetHandle,
            intent.Priority,
            now,
            expiresAt);
        _expiries.Enqueue(new SuggestionExpiry(request.ActorId, request.RequestId), expiresAt.Ticks);
    }

    /// <summary>
    /// Validates the complete physical batch before execution. Speech remains independently deliverable, but one
    /// stale target or unauthorized command rejects every physical command so dependent intent is never truncated.
    /// </summary>
    private bool TrySelectPhysicalCommands(
        NCNeuralRequestEnvelope request,
        NCNeuralResponseEnvelope response,
        NCActiveWorldTime now,
        out NCNeuralCommandProposal[] commands,
        out string? failure)
    {
        commands = [];
        failure = null;
        if (response.Commands.Count == 0)
            return true;

        foreach (var command in response.Commands)
        {
            if (!request.AllowedCapabilityIds.Contains(command.CapabilityId) ||
                !_prototypes.TryIndex(new ProtoId<NCCognitionCapabilityPrototype>(command.CapabilityId),
                    out var capability) || capability == null ||
                capability.StrategicOptions.Length == 0 && command.GoalKey != "execute" ||
                capability.StrategicOptions.Length > 0 &&
                !capability.StrategicOptions.Any(option => option.Id == command.GoalKey) ||
                capability.ProcedureSteps.Length == 0 &&
                !capability.StrategicOptions.Any(option => option.Id == command.GoalKey) ||
                !ValidCurrentTarget(request, command.TargetHandle, now))
            {
                failure = "neural-command-batch-rejected";
                return false;
            }
        }

        commands = response.Commands.ToArray();
        return true;
    }

    private void ReportBatchRejection(
        NCNeuralRequestEnvelope request,
        string failure,
        NCActiveWorldTime now)
    {
        // Reuse the bounded action-failure wake path; this makes rejection observable without exposing prompt data.
        RaiseLocalEvent(new NCNeuralActionExecutionFailedEvent(
            request.ActorId,
            request.RequestId,
            request.Priority,
            failure,
            now));
    }

    private bool ValidCurrentTarget(NCNeuralRequestEnvelope request, string handle, NCActiveWorldTime now) =>
        string.IsNullOrWhiteSpace(handle) ||
        _perception.TryResolveSubjectHandle(request.ActorId, handle, now, out _) ||
        (request.KnownLocations ?? []).Any(location => location.Handle == handle);

    private static bool IsDirectAssault(NCNeuralRequestEnvelope request) =>
        (request.PerceivedEvents ?? []).Any(value =>
            value.Kind == "assault" && value.TargetHandle == "self" && value.ExpiresAtTicks > 0);

    private bool TryDeliverDialogue(
        NCActorId actorId,
        Guid requestId,
        NCConversationSessionSnapshot session,
        NCNeuralResponseEnvelope response)
    {
        if (session.Channel != NCConversationChannel.InPerson ||
            !session.Participants.Contains(NCPartyRef.ForActor(actorId)) ||
            !_runtime.TryGetEntity(actorId, out var entityUid) || !Exists(entityUid))
            return false;
        if (!response.ShouldSpeak)
            return true;
        if (string.IsNullOrWhiteSpace(response.RenderedText) ||
            !TryMapSpeechAct(response.SpeechActKey, out var act))
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

    private bool TryGetCurrentDialogue(DialogueCorrelation dialogue, out NCConversationSessionSnapshot session)
    {
        if (!_conversations.TryGet(dialogue.SessionId, out session!) ||
            session.ConversationVersion < dialogue.ConversationVersion)
            return false;
        if (!dialogue.ParallelReplies)
            return session.ConversationVersion == dialogue.ConversationVersion;
        var trigger = session.Turns.FirstOrDefault(turn => turn.Sequence == dialogue.TriggerTurnSequence);
        if (trigger == null || trigger.Speaker != dialogue.TriggerSpeaker)
            return false;
        // Multiple NPCs may answer one crowd address, but a newer player/NPC-originating group turn supersedes it.
        return session.Turns.All(turn => turn.Sequence <= dialogue.TriggerTurnSequence ||
            turn.Speaker.TryGetActor(out _));
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
        _intents.Clear();
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
    private sealed record AppliedIntent(
        Guid RequestId,
        string Key,
        string TargetHandle,
        int Priority,
        NCActiveWorldTime AcceptedAt,
        NCActiveWorldTime ExpiresAt);
    private sealed record DialogueCorrelation(
        NCConversationSessionId SessionId,
        int ConversationVersion,
        long TriggerTurnSequence,
        NCPartyRef TriggerSpeaker,
        bool ParallelReplies);
}

public sealed record NCNeuralSuggestionSnapshot(
    Guid RequestId,
    string CapabilityId,
    string GoalKey,
    string TargetHandle,
    int Confidence,
    NCActiveWorldTime AcceptedAt,
    NCActiveWorldTime ExpiresAt);

public sealed record NCNeuralIntentSnapshot(
    Guid RequestId,
    string Key,
    string TargetHandle,
    int Priority,
    NCActiveWorldTime AcceptedAt,
    NCActiveWorldTime ExpiresAt);
