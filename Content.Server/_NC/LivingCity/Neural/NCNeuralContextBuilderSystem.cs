// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.Social;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Neural;

/// <summary>Builds the only neural-safe context from the existing privacy redaction boundary.</summary>
public sealed partial class NCNeuralContextBuilderSystem : EntitySystem
{
    private static readonly ProtoId<NCNeuralPolicyPrototype> DefaultPolicy = "Default";
    private static readonly ProtoId<NCNeuralPromptProfilePrototype> DefaultPrompt = "NCLivingCityDefault";
    private const string DialogueCapability = "social.dialogue.reply";
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCCognitionSystem _cognition = default!;
    [Dependency] private NCSocialContextRedactionSystem _redaction = default!;
    [Dependency] private NCNeuralCognitionClientSystem _client = default!;
    [Dependency] private NCNeuralSuggestionSystem _suggestions = default!;
    private readonly Dictionary<NCActorId, NCActiveWorldTime> _nextStrategicRequest = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCConversationTurnRecordedEvent>(OnConversationTurn);
        SubscribeLocalEvent<NCCognitionDecisionAppliedEvent>(OnDecisionApplied);
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(_ => _nextStrategicRequest.Clear());
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => _nextStrategicRequest.Clear());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => _nextStrategicRequest.Clear());
    }

    public async Task<bool> SubmitAsync(NCActorId actorId, NCNeuralRequestKind kind, NCNeuralPriority priority,
        IReadOnlyList<string> allowedCapabilities, NCConversationSessionId? conversation = null,
        string? transientUtterance = null)
    {
        if (!_client.IsProviderEnabled || !_clock.TryGetCurrentTime(out var epoch, out var now) ||
            !_prototypes.TryIndex(DefaultPolicy, out NCNeuralPolicyPrototype? policy) ||
            !_prototypes.TryIndex(DefaultPrompt, out NCNeuralPromptProfilePrototype? prompt) ||
            !prompt.AllowedKinds.Contains(kind)) return false;
        var actor = await _actors.GetAsync(actorId, true);
        if (actor == null || actor.Snapshot.CognitionTier != NCCityActorCognitionTier.NeuralEligible)
            return false;
        var purpose = conversation == null ? NCSocialContextPurpose.InternalReasoning :
            NCSocialContextPurpose.ConversationRendering;
        var context = await _redaction.BuildAsync(NCPartyRef.ForActor(actorId), purpose, conversation);
        if (context == null) return false;
        var facts = context.Facts.Where(value => !context.ForbiddenTopics.Contains(value.FactKey))
            .Select(value => new NCNeuralFact(value.Domain, value.FactKey, StableSubject(value.SubjectKey),
                value.Confidence, value.Privacy.ToString())).Take(policy.MaximumFacts).ToArray();
        NCConversationSessionSnapshot? conversationSnapshot = null;
        if (conversation is { } sessionId && !_conversations.TryGet(sessionId, out conversationSnapshot!))
            return false;
        NCNeuralDialogueInput? dialogueInput = null;
        if (kind == NCNeuralRequestKind.DialogueContinuation)
        {
            var utterance = NormalizeDialogueText(transientUtterance, policy.MaximumDialogueInputLength);
            var actorName = NormalizeDialogueText(actor.Snapshot.Identity.FullName, 128);
            if (utterance.Length == 0 || actorName.Length == 0)
                return false;
            dialogueInput = new NCNeuralDialogueInput(actorName, utterance);
        }
        var request = new NCNeuralRequestEnvelope(Guid.NewGuid(), kind, priority, actorId, epoch,
            actor.AggregateVersion, now.Advance(policy.RequestLifetime), DefaultPrompt, facts,
            allowedCapabilities.Distinct().Take(policy.MaximumCapabilities).ToArray(),
            conversationSnapshot?.CurrentTopicKeys ?? [], "ru-RU", dialogueInput);
        var trackedDialogue = kind == NCNeuralRequestKind.DialogueContinuation && conversationSnapshot != null;
        if (trackedDialogue && !_suggestions.TryTrackDialogue(
                request.RequestId,
                conversationSnapshot!.SessionId,
                conversationSnapshot.ConversationVersion))
            return false;
        if (_client.Submit(request))
            return true;
        if (trackedDialogue)
            _suggestions.ForgetDialogue(request.RequestId);
        return false;
    }

    [Dependency] private NCConversationSessionSystem _conversations = default!;
    private void OnConversationTurn(NCConversationTurnRecordedEvent args)
    {
        var session = args.Session;
        if (session.Turns.Count == 0 || session.Turns[session.Turns.Count - 1].Speaker.TryGetActor(out _))
            return;
        var actor = session.Participants.FirstOrDefault(value => value.TryGetActor(out _) &&
            value != session.Turns[session.Turns.Count - 1].Speaker);
        if (!actor.TryGetActor(out var actorId))
            return;

        // Dialogue rendering has one speech-only capability and never inherits gameplay mutation capabilities from
        // the planner. This keeps prompt output outside police, combat, economy, and access authorities.
        _ = SubmitAsync(actorId, NCNeuralRequestKind.DialogueContinuation, NCNeuralPriority.P1,
            [DialogueCapability], session.SessionId, args.TransientUtterance);
    }

    private void OnDecisionApplied(NCCognitionDecisionAppliedEvent args)
    {
        if (args.CognitionTier != NCCityActorCognitionTier.NeuralEligible || !_client.IsProviderEnabled ||
            !_clock.TryGetCurrentTime(out _, out var now) ||
            !_prototypes.TryIndex(DefaultPolicy, out NCNeuralPolicyPrototype? policy) ||
            _nextStrategicRequest.TryGetValue(args.ActorId, out var next) && next > now)
            return;

        var capabilities = args.Desires
            .Where(value => value.TaskKind is NCCognitionTaskKind.KnownProcedure or
                NCCognitionTaskKind.GroupOperation or NCCognitionTaskKind.StrategicChoice)
            .Select(value => value.CapabilityId)
            .Distinct()
            .ToArray();
        if (capabilities.Length == 0)
            return;
        _nextStrategicRequest[args.ActorId] = now.Advance(policy.StrategicRequestCooldown);
        _ = SubmitAsync(args.ActorId, NCNeuralRequestKind.StrategicDecision, NCNeuralPriority.P3, capabilities);
    }

    private static string StableSubject(string value)
    {
        var normalized = new string(value.Select(character => char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_' or '.' or ':' ? character : '_').ToArray());
        return string.IsNullOrWhiteSpace(normalized)
            ? "unknown"
            : normalized.Substring(0, Math.Min(128, normalized.Length));
    }

    private static string NormalizeDialogueText(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || maximumLength < 1)
            return string.Empty;
        var normalized = string.Join(' ', value.Split((char[]?) null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Substring(0, Math.Min(maximumLength, normalized.Length));
    }
}
