// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.Social;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Neural;

/// <summary>Builds the only neural-safe context from the existing privacy redaction boundary.</summary>
public sealed partial class NCNeuralContextBuilderSystem : EntitySystem
{
    private static readonly ProtoId<NCNeuralPolicyPrototype> DefaultPolicy = "Default";
    private static readonly ProtoId<NCNeuralPromptProfilePrototype> DefaultPrompt = "NCLivingCityDefault";
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCCognitionSystem _cognition = default!;
    [Dependency] private NCSocialContextRedactionSystem _redaction = default!;
    [Dependency] private NCNeuralCognitionClientSystem _client = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCConversationTurnRecordedEvent>(OnConversationTurn);
    }

    public async Task<bool> SubmitAsync(NCActorId actorId, NCNeuralRequestKind kind, NCNeuralPriority priority,
        IReadOnlyList<string> allowedCapabilities, NCConversationSessionId? conversation = null)
    {
        if (!_clock.TryGetCurrentTime(out var epoch, out var now) ||
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
        return _client.Submit(new NCNeuralRequestEnvelope(Guid.NewGuid(), kind, priority, actorId, epoch,
            actor.AggregateVersion, now.Advance(policy.RequestLifetime), DefaultPrompt, facts,
            allowedCapabilities.Distinct().Take(policy.MaximumCapabilities).ToArray(),
            conversation is { } session && _conversations.TryGet(session, out var snapshot)
                ? snapshot.CurrentTopicKeys : [], "ru-RU"));
    }

    [Dependency] private NCConversationSessionSystem _conversations = default!;
    private void OnConversationTurn(NCConversationTurnRecordedEvent args)
    {
        var session = args.Session;
        var actor = session.Participants.FirstOrDefault(value => value.TryGetActor(out _) &&
            value != session.Turns[^1].Speaker);
        if (!actor.TryGetActor(out var actorId) || !_cognition.TryGetSnapshot(actorId, out var cognition)) return;
        var capabilities = cognition.Desires.Select(value => value.CapabilityId).Distinct().ToArray();
        if (capabilities.Length > 0)
            _ = SubmitAsync(actorId, NCNeuralRequestKind.DialogueContinuation, NCNeuralPriority.P1,
                capabilities, session.SessionId);
    }

    private static string StableSubject(string value)
    {
        var normalized = new string(value.Select(character => char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_' or '.' or ':' ? character : '_').ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized[..Math.Min(128, normalized.Length)];
    }
}
