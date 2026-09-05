// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.CityServices;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Navigation;
using Content.Server._NC.LivingCity.Social;
using Content.Server._NC.LivingCity.Time;
using Content.Shared.Roles;
using Content.Shared.Item;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;
using System.Threading;

namespace Content.Server._NC.LivingCity.Neural;

/// <summary>Builds the only neural-safe context from the existing privacy redaction boundary.</summary>
public sealed partial class NCNeuralContextBuilderSystem : EntitySystem
{
    private static readonly ProtoId<NCNeuralPolicyPrototype> DefaultPolicy = "Default";
    private static readonly ProtoId<NCNeuralPromptProfilePrototype> DefaultPrompt = "NCLivingCityDefault";
    private static readonly ProtoId<NCNeuralPersonaProfilePrototype> DefaultResidentPersona =
        "NCDefaultResidentPersona";
    private static readonly ProtoId<NCNeuralAutonomyPolicyPrototype> DefaultResidentAutonomy =
        "NCDefaultNeuralAutonomy";
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCEntityPartyResolverSystem _entityParties = default!;
    [Dependency] private NCCityLocalPerceptionSystem _perception = default!;
    [Dependency] private NCLocationKnowledgeSystem _locationKnowledge = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NCSocialContextRedactionSystem _redaction = default!;
    [Dependency] private NCSocialProfileRegistrySystem _socialProfiles = default!;
    [Dependency] private NCConversationArchiveSystem _conversationArchive = default!;
    [Dependency] private NCNeuralCognitionClientSystem _client = default!;
    [Dependency] private NCNeuralSuggestionSystem _suggestions = default!;
    [Dependency] private NCNeuralConversationSystem _neuralConversations = default!;
    [Dependency] private NCNeuralActionExecutionSystem _execution = default!;
    [Dependency] private NCHumanVanillaActionAdapterSystem _vanilla = default!;
    [Dependency] private NCHumanActionRouterSystem _actionRouter = default!;
    [Dependency] private NCNeuralEmbodimentContextSystem _embodiment = default!;
    [Dependency] private NCWorldOpportunityBusSystem _opportunities = default!;
    [Dependency] private NCStationSemanticDestinationSystem _stationDestinations = default!;
    private readonly Dictionary<NCActorId, IncidentSubmissionGate> _incidentGates = [];
    private readonly Dictionary<NCActorId, RecentActionFailure> _recentActionFailures = [];
    private readonly Dictionary<NCActorId, FailureReplanState> _failureReplanRequests = [];
    private long _wakeRevision;
    private long _ordinaryDialogueSubmissions;
    private long _narrativeDialogueSubmissions;
    private long _incidentSubmissionAttempts;
    private long _incidentSubmissions;
    private long _incidentSubmissionRejects;
    private long _incidentSubmissionSuppressions;
    private long _failureReplanSubmissions;

    public long OrdinaryDialogueSubmissions => Interlocked.Read(ref _ordinaryDialogueSubmissions);
    public long NarrativeDialogueSubmissions => Interlocked.Read(ref _narrativeDialogueSubmissions);
    public long IncidentSubmissionAttempts => Interlocked.Read(ref _incidentSubmissionAttempts);
    public long IncidentSubmissions => Interlocked.Read(ref _incidentSubmissions);
    public long IncidentSubmissionRejects => Interlocked.Read(ref _incidentSubmissionRejects);
    public long IncidentSubmissionSuppressions => Interlocked.Read(ref _incidentSubmissionSuppressions);
    public long FailureReplanSubmissions => Interlocked.Read(ref _failureReplanSubmissions);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCConversationTurnRecordedEvent>(OnConversationTurn);
        SubscribeLocalEvent<NCCityLocalIncidentObservedEvent>(OnIncidentObserved);
        SubscribeLocalEvent<NCCityLocalPerceptionDeltaObservedEvent>(OnPerceptionDeltaObserved);
        SubscribeLocalEvent<NCNeuralActionExecutionFailedEvent>(OnActionExecutionFailed);
        SubscribeLocalEvent<NCNeuralActionExecutionCompletedEvent>(OnActionExecutionCompleted);
        SubscribeLocalEvent<NCNeuralCommandResultEvent>(OnCommandResult);
        SubscribeLocalEvent<NCNeuralResultEvent>(OnNeuralResult);
        SubscribeLocalEvent<NCNeuralSenseRequestedEvent>(OnSenseRequested);
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(_ => ClearCooldowns());
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => ClearCooldowns());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => ClearCooldowns());
    }

    public async Task<bool> SubmitAsync(NCActorId actorId, NCNeuralRequestKind kind, NCNeuralPriority priority,
        IReadOnlyList<string> allowedCapabilities, NCConversationSessionId? conversation = null,
        string? transientUtterance = null)
    {
        return await SubmitCoreAsync(actorId, kind, priority, allowedCapabilities, conversation,
            transientUtterance, null, null, 0, null);
    }

    /// <summary>Submits one independent physical actor decision using its authored capability allowlist.</summary>
    public async Task<bool> SubmitAutonomousAsync(NCActorId actorId, NCNeuralPriority priority) =>
        await SubmitAutonomousAsync(actorId, priority, null);

    private async Task<bool> SubmitAutonomousAsync(
        NCActorId actorId,
        NCNeuralPriority priority,
        Guid? failureOfPlanId)
    {
        var actor = await _actors.GetAsync(actorId, true);
        if (actor == null ||
            !_prototypes.TryIndex(new ProtoId<NCCityActorArchetypePrototype>(actor.Snapshot.Identity.ArchetypeId),
                out var archetype) || archetype == null ||
            !_prototypes.TryIndex(archetype.CognitionCapabilityProfile,
                out NCCognitionCapabilityProfilePrototype? profile) || profile == null)
            return false;

        var allowed = profile.Capabilities.Select(value => value.Id).Distinct(StringComparer.Ordinal).ToArray();
        return allowed.Length > 0 && await SubmitCoreAsync(
            actorId,
            NCNeuralRequestKind.StrategicDecision,
            priority,
            allowed,
            null,
            null,
            null,
            failureOfPlanId,
            0,
            null);
    }

    private async Task<bool> SubmitCoreAsync(NCActorId actorId, NCNeuralRequestKind kind,
        NCNeuralPriority priority, IReadOnlyList<string> allowedCapabilities,
        NCConversationSessionId? conversation, string? transientUtterance, DialogueTrigger? dialogueTrigger,
        Guid? failureOfPlanId, int senseDepth, IReadOnlyList<NCNeuralSenseRequest>? senseRequests)
    {
        if (senseDepth is < 0 or > 2 || !_client.IsProviderEnabled ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) ||
            !_prototypes.TryIndex(DefaultPolicy, out NCNeuralPolicyPrototype? policy) ||
            !_prototypes.TryIndex(DefaultPrompt, out NCNeuralPromptProfilePrototype? prompt) ||
            !prompt.AllowedKinds.Contains(kind) || prompt.Model != policy.ProviderModel ||
            !StableKey(prompt.StableKey) || prompt.Version <= 0 || !StableKey(prompt.EvaluationProfile)) return false;
        // Reserve before the first await. Completion order is not wake order when database reads overlap.
        var wakeRevision = Interlocked.Increment(ref _wakeRevision);
        var decisionRevision = _client.GetActorDecisionRevision(actorId);
        var actor = await _actors.GetAsync(actorId, true);
        if (actor == null || !_client.CanUseGpt(actorId))
            return false;
        if (!_prototypes.TryIndex(new ProtoId<NCCityActorArchetypePrototype>(actor.Snapshot.Identity.ArchetypeId),
                out var archetype) || archetype == null)
            return false;
        if (!_prototypes.TryIndex(archetype.CognitionCapabilityProfile,
                out NCCognitionCapabilityProfilePrototype? capabilityProfile) || capabilityProfile == null)
            return false;

        // Dialogue is another neural wake for the same physical person, not a reduced social-only brain. Give GPT
        // every capability authored for this actor while keeping the action router as the mutation authority.
        var requestedCapabilities = kind == NCNeuralRequestKind.DialogueContinuation
            ? capabilityProfile.Capabilities.Select(value => value.Id).Distinct(StringComparer.Ordinal)
            : allowedCapabilities;
        NCNeuralPersonaProfilePrototype? personaProfile;
        NCNeuralAutonomyPolicyPrototype? autonomyPolicy;
        var narrativeAccess = false;
        if (TryResolveNarrativeProfile(actor, archetype, out var narrativeProfile))
        {
            narrativeAccess = true;
            if (!_prototypes.TryIndex(narrativeProfile.PersonaProfile, out personaProfile) ||
                !_prototypes.TryIndex(narrativeProfile.AutonomyPolicy, out autonomyPolicy))
                return false;
        }
        else
        {
            var residentPersona = string.IsNullOrWhiteSpace(actor.Snapshot.Identity.NeuralPersonaProfileId)
                ? DefaultResidentPersona
                : new ProtoId<NCNeuralPersonaProfilePrototype>(actor.Snapshot.Identity.NeuralPersonaProfileId);
            if (!_prototypes.TryIndex(residentPersona, out personaProfile) ||
                !_prototypes.TryIndex(DefaultResidentAutonomy, out autonomyPolicy))
                return false;
        }
        if (personaProfile == null || autonomyPolicy == null || !autonomyPolicy.AllowedKinds.Contains(kind))
            return false;
        var maximumPerceivedSubjects = narrativeAccess
            ? policy.MaximumPerceivedSubjects
            : policy.OrdinaryDialogueMaximumPerceivedSubjects;
        var maximumKnownLocations = narrativeAccess
            ? policy.MaximumKnownLocations
            : policy.OrdinaryDialogueMaximumKnownLocations;
        var maximumFacts = narrativeAccess
            ? policy.MaximumFacts
            : policy.OrdinaryDialogueMaximumFacts;
        var maximumDialogueInputLength = narrativeAccess
            ? policy.MaximumDialogueInputLength
            : policy.OrdinaryDialogueMaximumInputLength;
        var maximumDialogueHistoryTurns = narrativeAccess
            ? policy.MaximumDialogueHistoryTurns
            : policy.OrdinaryDialogueMaximumHistoryTurns;
        var maximumDialogueSummaryKeys = narrativeAccess
            ? policy.MaximumDialogueSummaryKeys
            : policy.OrdinaryDialogueMaximumSummaryKeys;
        var maximumDialogueMemorySessions = narrativeAccess
            ? policy.MaximumDialogueMemorySessions
            : policy.OrdinaryDialogueMaximumMemorySessions;
        var actorDisplayName = NormalizeDialogueText(actor.Snapshot.Identity.FullName, 128);
        EntityUid? actorEntity = _runtime.TryGetEntity(actorId, out var resolvedActorEntity) &&
                                 Exists(resolvedActorEntity)
            ? resolvedActorEntity
            : null;
        EntityUid? speakerEntity = dialogueTrigger?.SpeakerEntity;
        var addressScope = dialogueTrigger?.AddressScope ?? "direct";
        var audienceSize = dialogueTrigger?.AudienceSize ?? 1;
        var speakerHandle = string.Empty;
        if (conversation is { } liveConversation && speakerEntity == null &&
            _neuralConversations.TryGetDialogueContext(liveConversation, actorId, out var resolvedSpeaker,
                out addressScope, out audienceSize))
            speakerEntity = resolvedSpeaker;
        if (speakerEntity is { } speaker && Exists(speaker) && actorEntity is { } observerEntity)
        {
            // A spoken turn receives a fresh bounded scan rather than waiting for the background BDI cadence.
            _perception.RefreshNow(actorId, now);
            _perception.ObserveSubject(observerEntity, speaker, NCCityLocalObservationKind.Neutral, now,
                policy.RequestLifetime);
        }

        var perceivedByHandle = new Dictionary<string, NCNeuralPerceivedSubject>(StringComparer.Ordinal)
        {
            // "self" is an explicit subjective handle, so assault.targetHandle=self cannot be mistaken for a rumor
            // about an unknown third party.
            ["self"] = new NCNeuralPerceivedSubject(
                "self",
                actorDisplayName,
                "self",
                1f,
                now.Advance(policy.RequestLifetime).Ticks)
            {
                IdentityKnown = true,
            },
        };
        var participantAliases = new Dictionary<NCPartyRef, string>();
        var perceivedEvents = Array.Empty<NCNeuralPerceivedEvent>();
        var localDeltas = Array.Empty<NCCityLocalPerceptionDelta>();
        var localSubjects = Array.Empty<NCCityLocalObservation>();
        if (_perception.TryGetSnapshot(actorId, now, out var snapshotSubjects, out var localEvents,
                out var snapshotDeltas))
        {
            localSubjects = snapshotSubjects;
            localDeltas = snapshotDeltas;
            foreach (var subject in localSubjects
                         .OrderByDescending(value => value.EntityUid == speakerEntity)
                         .Take(Math.Max(0, maximumPerceivedSubjects - 1)))
            {
                var observationKind = ResolveObservationKind(subject.EntityUid, subject.Kind);
                perceivedByHandle[subject.SubjectHandle] = new NCNeuralPerceivedSubject(
                    subject.SubjectHandle,
                    ObservableDescription(observationKind,
                        IsObjectObservation(observationKind) ? Name(subject.EntityUid) : null),
                    observationKind.ToString().ToLowerInvariant(),
                    subject.Confidence,
                    subject.ExpiresAt.Ticks);
                if (_entityParties.TryResolve(subject.EntityUid, out var subjectParty))
                    participantAliases[subjectParty] = subject.SubjectHandle;
                if (subject.EntityUid == speakerEntity)
                    speakerHandle = subject.SubjectHandle;
            }
            perceivedEvents = localEvents
                .Take(maximumPerceivedSubjects)
                .Select(value => new NCNeuralPerceivedEvent(
                    value.Kind.ToString().ToLowerInvariant(),
                    value.ActorHandle,
                    value.TargetHandle,
                    value.Confidence,
                    value.ObservedAt.Ticks,
                    value.ExpiresAt.Ticks))
                .ToArray();
        }
        var owner = NCPartyRef.ForActor(actorId);
        var disclosurePurpose = conversation == null
            ? NCSocialContextPurpose.InternalReasoning
            : NCSocialContextPurpose.ConversationRendering;
        if (!_redaction.TryResolveMaximumPrivacy(owner, disclosurePurpose, conversation, out var disclosureMaximum))
            return false;

        // The Actor may reason about everything it legitimately knows, including its own secrets. The separate
        // disclosure allowlist below limits which of those facts may appear in the actual spoken response.
        var context = await _redaction.BuildAsync(
            owner, NCSocialContextPurpose.InternalReasoning, null, participantAliases);
        if (context == null)
            return false;
        ApplyKnownPersonIdentities(perceivedByHandle, localSubjects, context.Facts);
        var factCandidates = context.Facts.Where(value => !context.ForbiddenTopics.Contains(value.FactKey))
            .Select(value => new NCNeuralFactCandidate(
                new NCNeuralFact(value.Domain, value.FactKey, StableSubject(value.SubjectKey),
                    value.Confidence, value.Privacy.ToString()),
                value.ObservedAtTicks))
            .ToList();
        var urgentSubjects = new HashSet<string>(StringComparer.Ordinal);
        if (speakerHandle.Length > 0)
            urgentSubjects.Add(speakerHandle);
        foreach (var perceivedEvent in perceivedEvents)
        {
            var key = StableSubject(perceivedEvent.Kind);
            var subject = StableSubject(perceivedEvent.ActorHandle);
            factCandidates.Add(new NCNeuralFactCandidate(
                new NCNeuralFact("perception", key, subject, perceivedEvent.Confidence,
                    NCMemoryPrivacy.Personal.ToString()),
                perceivedEvent.ObservedAtTicks));
            if (perceivedEvent.TargetHandle == "self" ||
                perceivedEvent.Kind is "assault" or "killing" or "verbalthreat" or "fire")
                urgentSubjects.Add(subject);
        }
        AddOperationalFactContext(actorId, now, factCandidates, urgentSubjects);
        var perceived = perceivedByHandle.Values.Take(maximumPerceivedSubjects).ToArray();
        var perceivedHandles = perceived.Select(value => value.Handle).ToHashSet(StringComparer.Ordinal);
        var perceptionDeltas = localDeltas
            .Take(maximumPerceivedSubjects)
            .Select(value => new NCNeuralPerceptionDelta(
                NCCityLocalPerceptionSystem.PerceptionDeltaKindKey(value.Kind),
                value.SubjectHandle,
                value.RelatedHandle,
                value.PreviousKind,
                value.CurrentKind,
                perceivedHandles.Contains(value.SubjectHandle),
                value.RelatedHandle.Length > 0 && perceivedHandles.Contains(value.RelatedHandle),
                value.ObservedAt.Ticks,
                value.ExpiresAt.Ticks))
            .ToArray();
        var knownLocations = BuildKnownLocations(actor, actorEntity, maximumKnownLocations);
        var socialProfile = await _socialProfiles.GetAsync(actorId);
        var persona = BuildPersona(actor, actorEntity, socialProfile, personaProfile);
        if (persona == null)
            return false;
        var autonomy = new NCNeuralAutonomyContext(
            autonomyPolicy.StableKey,
            autonomyPolicy.MinimumIntentConfidence,
            autonomyPolicy.MaximumIntentPriority,
            autonomyPolicy.TargetedIntentsAllowed);
        NCConversationSessionSnapshot? conversationSnapshot = dialogueTrigger?.Session;
        if (conversationSnapshot == null && conversation is { } sessionId &&
            !_conversations.TryGet(sessionId, out conversationSnapshot!))
            return false;
        NCNeuralDialogueInput? dialogueInput = null;
        if (kind == NCNeuralRequestKind.DialogueContinuation)
        {
            var utterance = NormalizeDialogueText(transientUtterance, maximumDialogueInputLength);
            if (utterance.Length == 0 || actorDisplayName.Length == 0)
                return false;
            var transcript = dialogueTrigger?.Transcript ?? (conversation is { } transcriptSession
                ? _conversations.GetTranscript(transcriptSession, maximumDialogueHistoryTurns + 1)
                : []);
            var history = transcript.Count > 0
                ? transcript.TakeLast(maximumDialogueHistoryTurns + 1).SkipLast(1)
                    .Select(value => new NCNeuralDialogueTurn(
                        value.Speaker == NCPartyRef.ForActor(actorId) ? "self" : "interlocutor",
                        value.Act.ToString().ToLowerInvariant(),
                        NormalizeDialogueText(value.Utterance, maximumDialogueInputLength),
                        value.At.Ticks))
                    .ToArray()
                : [];
            var priorTurns = conversationSnapshot?.Turns.SkipLast(1).ToArray() ?? [];
            var earlierTurnCount = Math.Max(0, priorTurns.Length - history.Length);
            var summaryKeys = priorTurns
                .Where(value => value.SummaryKey.Length > 0)
                .Select(value => value.SummaryKey)
                .Distinct(StringComparer.Ordinal)
                .Take(maximumDialogueSummaryKeys)
                .ToArray();
            var memoryParticipants = conversationSnapshot != null && addressScope == "group" &&
                                     conversationSnapshot.Turns.Count > 0
                ? new[] { owner, conversationSnapshot.Turns[^1].Speaker }
                : conversationSnapshot?.Participants;
            var archivedMemories = memoryParticipants == null
                ? Array.Empty<NCConversationMemoryContext>()
                : await _conversationArchive.GetRelevantAsync(
                    actorId,
                    memoryParticipants,
                    conversationSnapshot!.CurrentTopicKeys,
                    maximumDialogueMemorySessions);
            var persistentMemories = archivedMemories
                .Select(value => new NCNeuralConversationMemory(
                    value.LastActivityAt.Ticks,
                    value.TopicKeys.Take(maximumDialogueSummaryKeys).ToArray(),
                    value.SemanticSummaryKeys.Take(maximumDialogueSummaryKeys).ToArray(),
                    NormalizeDialogueText(value.Summary, policy.MaximumDialogueMemoryTextLength)))
                .Where(value => value.Summary.Length > 0)
                .ToArray();
            var earlierSummary = earlierTurnCount > 0 || summaryKeys.Length > 0 ||
                conversationSnapshot?.CurrentTopicKeys.Count > 0 || persistentMemories.Length > 0
                ? new NCNeuralDialogueSummary(
                    earlierTurnCount,
                    conversationSnapshot?.CurrentTopicKeys.Take(maximumDialogueSummaryKeys).ToArray() ?? [],
                    summaryKeys,
                    persistentMemories)
                : null;
            dialogueInput = new NCNeuralDialogueInput(actorDisplayName, utterance, speakerHandle, history,
                earlierSummary, addressScope, audienceSize);
        }
        var effectiveCapabilities = requestedCapabilities
            .Distinct()
            .Take(policy.MaximumCapabilities)
            .ToArray();
        if (effectiveCapabilities.Length == 0)
            return false;
        var packetFacts = BuildFactPacket(factCandidates, maximumFacts, urgentSubjects);
        var body = actorEntity is { } embodiedActor ? _embodiment.BuildBody(embodiedActor) : null;
        var assignmentKeys = BuildAssignmentKeys(actorEntity);
        var actorJob = actorEntity is { } jobEntity && TryComp<NCNpcJobComponent>(jobEntity, out var job)
            ? job.Job
            : (ProtoId<JobPrototype>?) null;
        _opportunities.RefreshSubjectiveSources(
            actorId,
            localSubjects,
            localEvents,
            knownLocations,
            actorJob,
            assignmentKeys.FirstOrDefault() ?? string.Empty,
            now);
        var worldOpportunities = _opportunities.Query(
            actorId,
            perceived.Select(value => value.Handle).ToHashSet(StringComparer.Ordinal),
            knownLocations.Select(value => value.Handle).ToHashSet(StringComparer.Ordinal),
            effectiveCapabilities.ToHashSet(StringComparer.Ordinal),
            assignmentKeys,
            now,
            maximumPerceivedSubjects);
        var resolvableRouteHandles = BuildResolvableRouteHandles(actorEntity, knownLocations);
        var senseResults = BuildSenseResults(senseRequests, perceived, perceivedEvents, body, packetFacts,
            knownLocations, worldOpportunities, resolvableRouteHandles);
        var disclosure = new NCNeuralDisclosureContext(
            disclosureMaximum.ToString(),
            packetFacts.Select(value => value.Handle).ToArray(),
            packetFacts.Where(value => CanDisclose(value, disclosureMaximum))
                .Select(value => value.Handle)
                .ToArray(),
            context.ForbiddenAssertionKeys,
            context.ForbiddenTopics);
        // Context assembly spans multiple asynchronous reads. Revalidate the round after the final await so an old
        // packet cannot enter a newly started Neural queue.
        if (!_clock.TryGetCurrentTime(out var currentEpoch, out var currentNow) || currentEpoch != epoch)
            return false;

        var request = new NCNeuralRequestEnvelope(Guid.NewGuid(), kind, priority, actorId, currentEpoch,
            actor.AggregateVersion, currentNow.Advance(policy.RequestLifetime), DefaultPrompt,
            prompt.StableKey, prompt.Version, prompt.EvaluationProfile,
            packetFacts,
            effectiveCapabilities, conversationSnapshot?.CurrentTopicKeys ?? [], "ru-RU", persona, autonomy,
            perceived, perceivedEvents, knownLocations, dialogueInput, [], disclosure)
        {
            WakeRevision = wakeRevision,
            DecisionRevision = decisionRevision,
            // Keep ordinary spoken replies cheap, but do not truncate a resident's autonomous multi-command plan
            // to the dialogue-only ceiling. This remains a per-request response bound, never a quota that disables GPT.
            MaximumOutputTokens = ResolveMaximumOutputTokens(
                narrativeAccess,
                kind,
                policy.MaximumRequestOutputTokens,
                policy.OrdinaryDialogueMaximumOutputTokens),
            AccessTier = narrativeAccess
                ? NCNeuralAccessTier.NarrativeActor
                : NCNeuralAccessTier.OrdinaryResident,
            Body = body,
            CurrentIntent = _embodiment.BuildCurrentIntent(actorId),
            RecentCommandResults = _embodiment.BuildRecentCommandResults(actorId),
            WorldOpportunities = worldOpportunities,
            PerceptionDeltas = perceptionDeltas,
            AvailableActions = BuildAvailableActions(effectiveCapabilities),
            SenseDepth = senseDepth,
            SenseResults = senseResults,
        };
        var trackedDialogue = kind == NCNeuralRequestKind.DialogueContinuation && conversationSnapshot != null;
        if (trackedDialogue && !IsCurrentDialogueTrigger(conversationSnapshot!, addressScope == "group"))
            return false;
        if (trackedDialogue && !_suggestions.TryTrackDialogue(
                request.RequestId,
                conversationSnapshot!.SessionId,
                conversationSnapshot.ConversationVersion,
                conversationSnapshot.Turns[^1].Sequence,
                conversationSnapshot.Turns[^1].Speaker,
                addressScope == "group"))
            return false;
        if (failureOfPlanId is not null)
            _failureReplanRequests[actorId] = new FailureReplanState(request.RequestId, request.ExpiresAt);
        if (_client.Submit(request))
        {
            if (kind == NCNeuralRequestKind.DialogueContinuation)
            {
                if (narrativeAccess)
                    Interlocked.Increment(ref _narrativeDialogueSubmissions);
                else
                    Interlocked.Increment(ref _ordinaryDialogueSubmissions);
            }
            return true;
        }
        if (trackedDialogue)
            _suggestions.ForgetDialogue(request.RequestId);
        if (failureOfPlanId != null && _failureReplanRequests.TryGetValue(actorId, out var replanRequest) &&
            replanRequest.RequestId == request.RequestId)
            _failureReplanRequests.Remove(actorId);
        return false;
    }

    private bool IsCurrentDialogueTrigger(NCConversationSessionSnapshot built, bool parallelReplies)
    {
        if (!_conversations.TryGet(built.SessionId, out var current) || built.Turns.Count == 0 ||
            current.ConversationVersion < built.ConversationVersion)
            return false;
        if (!parallelReplies)
            return current.ConversationVersion == built.ConversationVersion;

        var trigger = built.Turns[^1];
        var currentTrigger = current.Turns.FirstOrDefault(turn => turn.Sequence == trigger.Sequence);
        if (currentTrigger == null || currentTrigger.Speaker != trigger.Speaker)
            return false;
        return current.Turns.All(turn => turn.Sequence <= trigger.Sequence || turn.Speaker.TryGetActor(out _));
    }

    [Dependency] private NCConversationSessionSystem _conversations = default!;
    private void OnConversationTurn(NCConversationTurnRecordedEvent args)
    {
        var session = args.Session;
        if (session.Turns.Count == 0)
            return;
        var speaker = session.Turns[^1].Speaker;
        if (speaker.TryGetActor(out _) && session.Participants.Count == 2 &&
            !_neuralConversations.CanContinueNpcConversation(session.SessionId, session.Turns.Count))
        {
            _neuralConversations.TryEndConversation(session.SessionId, "npc-turn-limit");
            return;
        }
        var listeners = new List<NCActorId>();
        _neuralConversations.GetDialogueListeners(session.SessionId, speaker, listeners);
        if (listeners.Count == 0)
            return;
        var transcript = _conversations.GetTranscript(session.SessionId, session.Turns.Count);
        foreach (var actorId in listeners.Distinct())
        {
            if (!_neuralConversations.TryGetDialogueContext(session.SessionId, actorId, out var speakerEntity,
                    out var addressScope, out var audienceSize))
                continue;
            var trigger = new DialogueTrigger(session, transcript, args.TransientUtterance ?? string.Empty,
                speakerEntity, addressScope, audienceSize);
            _ = ContinueDialogueAsync(actorId, trigger);
        }
    }

    private async Task ContinueDialogueAsync(NCActorId actorId, DialogueTrigger trigger)
    {
        await SubmitCoreAsync(actorId, NCNeuralRequestKind.DialogueContinuation, NCNeuralPriority.P1,
            [], trigger.Session.SessionId, trigger.Utterance, trigger, null, 0, null);
    }

    private void OnSenseRequested(NCNeuralSenseRequestedEvent args)
    {
        if (args.Request.SenseDepth >= 2 || args.Request.SessionEpoch == default || args.Request.ExpiresAt.Ticks <= 0)
        {
            ClearFailureReplan(args.Request.ActorId, args.Request.RequestId);
            return;
        }
        if (!ValidSenseFollowup(args.Request, args.Requests))
        {
            ClearFailureReplan(args.Request.ActorId, args.Request.RequestId);
            return;
        }

        _ = SubmitSenseFollowupAsync(args);
    }

    /// <summary>
    /// The compact ceiling is exclusively a dialogue cost control. Autonomous residents need enough room for the
    /// same structured multi-command response as narrative Actors; this bound never disables later requests.
    /// </summary>
    internal static int ResolveMaximumOutputTokens(
        bool narrativeAccess,
        NCNeuralRequestKind kind,
        int maximumRequestOutputTokens,
        int ordinaryDialogueMaximumOutputTokens) =>
        !narrativeAccess && kind == NCNeuralRequestKind.DialogueContinuation
            ? ordinaryDialogueMaximumOutputTokens
            : maximumRequestOutputTokens;

    internal static bool ValidSenseFollowup(
        NCNeuralRequestEnvelope request,
        IReadOnlyList<NCNeuralSenseRequest> requests)
    {
        if (requests.Count is < 1 or > 2)
            return false;
        var perceivedHandles = (request.PerceivedSubjects ?? [])
            .Select(value => value.Handle)
            .ToHashSet(StringComparer.Ordinal);
        var knownLocationHandles = (request.KnownLocations ?? [])
            .Select(value => value.Handle)
            .ToHashSet(StringComparer.Ordinal);
        var factSubjects = request.Facts.Select(value => value.Subject).ToHashSet(StringComparer.Ordinal);
        foreach (var senseRequest in requests)
        {
            var targeted = senseRequest.Kind is "inspect-subject" or "inspect-object" or "recall-memories" or
                "examine-route";
            if (senseRequest.Kind is not ("look-around" or "inspect-subject" or "inspect-object" or
                    "check-inventory" or "check-body" or "query-nearby-affordances" or
                    "query-known-locations" or "recall-memories" or "examine-route") ||
                targeted != !string.IsNullOrWhiteSpace(senseRequest.TargetHandle))
            {
                return false;
            }

            if ((senseRequest.Kind is "inspect-subject" or "inspect-object" &&
                 !perceivedHandles.Contains(senseRequest.TargetHandle)) ||
                (senseRequest.Kind == "recall-memories" &&
                 !perceivedHandles.Contains(senseRequest.TargetHandle) &&
                 !knownLocationHandles.Contains(senseRequest.TargetHandle) &&
                 !factSubjects.Contains(senseRequest.TargetHandle)) ||
                (senseRequest.Kind == "examine-route" &&
                 !knownLocationHandles.Contains(senseRequest.TargetHandle)))
            {
                return false;
            }
        }
        return true;
    }

    private async Task SubmitSenseFollowupAsync(NCNeuralSenseRequestedEvent args)
    {
        var previous = args.Request;
        var continuesFailureReplan = IsFailureReplanRequest(previous.ActorId, previous.RequestId);
        if (!_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != previous.SessionEpoch ||
            previous.ExpiresAt <= now || !_runtime.TryGetEntity(previous.ActorId, out var actorEntity) ||
            !Exists(actorEntity))
        {
            if (continuesFailureReplan)
                ClearFailureReplan(previous.ActorId, previous.RequestId);
            return;
        }

        // Sensing is a read-only, bounded rescan. Targeted handles remain subject to the same perception authority;
        // this never teleports knowledge from a stale handle into the next request.
        _perception.RefreshNow(previous.ActorId, now);

        DialogueTrigger? trigger = null;
        NCConversationSessionId? conversation = null;
        string? utterance = null;
        if (previous.Kind == NCNeuralRequestKind.DialogueContinuation)
        {
            if (args.ConversationSessionId is not { } sessionId ||
                !_conversations.TryGet(sessionId, out var session) || session.Turns.Count == 0 ||
                !_neuralConversations.TryGetDialogueContext(sessionId, previous.ActorId, out var speakerEntity,
                    out var addressScope, out var audienceSize))
            {
                if (continuesFailureReplan)
                    ClearFailureReplan(previous.ActorId, previous.RequestId);
                return;
            }

            conversation = sessionId;
            utterance = previous.DialogueInput?.Utterance;
            if (string.IsNullOrWhiteSpace(utterance))
            {
                if (continuesFailureReplan)
                    ClearFailureReplan(previous.ActorId, previous.RequestId);
                return;
            }
            trigger = new DialogueTrigger(
                session,
                _conversations.GetTranscript(sessionId, session.Turns.Count),
                utterance,
                speakerEntity,
                addressScope,
                audienceSize);
        }

        var submitted = await SubmitCoreAsync(
            previous.ActorId,
            previous.Kind,
            previous.Priority,
            previous.AllowedCapabilityIds,
            conversation,
            utterance,
            trigger,
            continuesFailureReplan ? previous.RequestId : null,
            previous.SenseDepth + 1,
            args.Requests.Take(2).ToArray());
        if (!submitted && continuesFailureReplan)
            ClearFailureReplan(previous.ActorId, previous.RequestId);
    }

    private void OnNeuralResult(NCNeuralResultEvent args)
    {
        var result = args.Result;
        if (!IsFailureReplanRequest(result.Request.ActorId, result.Request.RequestId) ||
            !ShouldClearFailureReplan(result.Status, result.Response?.DecisionMode))
        {
            return;
        }

        ClearFailureReplan(result.Request.ActorId, result.Request.RequestId);
    }

    private void OnActionExecutionCompleted(NCNeuralActionExecutionCompletedEvent args)
    {
        ClearFailureReplan(args.ActorId, args.PlanId);
    }

    private void OnCommandResult(NCNeuralCommandResultEvent args)
    {
        if (args.Result.State == "interrupted")
            ClearFailureReplan(args.ActorId, args.Result.RequestId);
    }

    internal static bool ShouldClearFailureReplan(NCNeuralResultStatus status, string? decisionMode) =>
        status != NCNeuralResultStatus.Accepted || decisionMode is not ("act" or "sense");

    private bool IsFailureReplanRequest(NCActorId actorId, Guid requestId) =>
        _failureReplanRequests.TryGetValue(actorId, out var state) && state.RequestId == requestId;

    private void ClearFailureReplan(NCActorId actorId, Guid requestId)
    {
        if (IsFailureReplanRequest(actorId, requestId))
            _failureReplanRequests.Remove(actorId);
    }

    private NCNeuralKnownLocation[] BuildKnownLocations(
        NCCityActorRecord actor,
        EntityUid? actorEntity,
        int maximum)
    {
        if (maximum <= 0)
            return [];

        var result = new List<NCNeuralKnownLocation>(maximum);
        var handles = new HashSet<string>(StringComparer.Ordinal);

        // A physical station's authored work and department anchors are useful even when no CityGraph exists.
        if (actorEntity is { } entity && Exists(entity))
        {
            var stationBuffer = new List<NCStationSemanticDestination>();
            _stationDestinations.BuildFor(entity, stationBuffer);
            foreach (var destination in stationBuffer)
            {
                if (result.Count >= maximum || !handles.Add(destination.Handle))
                    break;
                result.Add(new NCNeuralKnownLocation(
                    destination.Handle,
                    destination.Label,
                    destination.ContextKind));
            }
        }

        // City zones remain an additional semantic source where the map actually has CityGraph geography.
        if (!actor.Snapshot.Location.CurrentZone.IsValid || result.Count >= maximum)
            return result.ToArray();

        _locationKnowledge.Seed(actor);
        var knownZones = new HashSet<NCZoneId>();
        _locationKnowledge.GetKnown(actor.Snapshot.Identity.ActorId, knownZones);
        // Requests may complete concurrently; a per-request buffer avoids sharing mutable query state.
        var zoneBuffer = new List<NCZoneInfo>();
        _zones.GetZones(zoneBuffer);
        foreach (var zone in zoneBuffer
                     .Where(zone => zone.ContextSlot == NCZoneContextSlot.District || knownZones.Contains(zone.Id))
                     .OrderBy(zone => zone.ContextSlot)
                     .ThenBy(zone => zone.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (result.Count >= maximum)
                break;
            var handle = $"zone:{zone.Id.Value:N}";
            if (!handles.Add(handle))
                continue;
            result.Add(new NCNeuralKnownLocation(
                handle,
                zone.Name,
                zone.ContextSlot.ToString().ToLowerInvariant()));
        }

        return result.ToArray();
    }

    private HashSet<string> BuildAssignmentKeys(EntityUid? actorEntity)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (actorEntity is { } entity && TryComp<NCNpcJobComponent>(entity, out var job) &&
            !string.IsNullOrWhiteSpace(job.Job.Id))
        {
            result.Add($"job:{job.Job.Id}");
        }
        return result;
    }

    private HashSet<string> BuildResolvableRouteHandles(
        EntityUid? actorEntity,
        IReadOnlyList<NCNeuralKnownLocation> knownLocations)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (actorEntity is not { } actor || !Exists(actor))
            return result;

        // Station handles are request-scoped and can be checked without exposing coordinates. CityGraph locations
        // stay known destinations, but are reported as unavailable here until a concrete route target is supplied.
        foreach (var location in knownLocations)
        {
            if (location.Handle.StartsWith("station-destination:", StringComparison.Ordinal) &&
                _stationDestinations.TryResolve(actor, location.Handle, out _))
            {
                result.Add(location.Handle);
            }
        }
        return result;
    }

    private void OnPerceptionDeltaObserved(NCCityLocalPerceptionDeltaObservedEvent args)
    {
        if (args.DeltaCount <= 0 || !_client.IsProviderEnabled ||
            !TryComp<NCCityActorBindingComponent>(args.Observer, out var binding))
            return;
        _ = SubmitPerceptionDeltaWakeAsync(binding.ActorId);
    }

    private async Task SubmitPerceptionDeltaWakeAsync(NCActorId actorId)
    {
        try
        {
            await SubmitAutonomousAsync(actorId, NCNeuralPriority.P3);
        }
        catch (Exception exception)
        {
            Log.Warning($"Perception-delta neural wake for {actorId} failed before submission: {exception.Message}");
        }
    }

    private void OnIncidentObserved(NCCityLocalIncidentObservedEvent args)
    {
        if (args.Kind is not (
                NCCityLocalPerceivedEventKind.Assault or
                NCCityLocalPerceivedEventKind.Killing or
                NCCityLocalPerceivedEventKind.Gunshot or
                NCCityLocalPerceivedEventKind.WeaponDisplayed or
                NCCityLocalPerceivedEventKind.VerbalThreat or
                NCCityLocalPerceivedEventKind.BodyDiscovered or
                NCCityLocalPerceivedEventKind.SevereInjury or
                NCCityLocalPerceivedEventKind.Fire) ||
            !TryComp<NCCityActorBindingComponent>(args.Observer, out var binding) ||
            !_clock.TryGetCurrentTime(out _, out var now) ||
            !_prototypes.TryIndex(DefaultPolicy, out NCNeuralPolicyPrototype? policy) || policy == null)
            return;

        var directlyAttacked = args.Kind == NCCityLocalPerceivedEventKind.Assault && args.Observer == args.Target;
        if (directlyAttacked)
        {
            // Damage is already authoritative. Stop stale idle/work immediately, then let a bounded vanilla reflex
            // protect the body while the provider chooses fight, flight, speech, or another allowed action.
            _execution.TryInterrupt(binding.ActorId, NCHumanActionInterruptKind.Combat);
            _vanilla.TryStartDefensiveReflex(binding.ActorId, args.Observer, args.Actor, now,
                policy.RequestLifetime, policy.DefensiveReflexRetreatDistance);
        }

        if (!_client.IsProviderEnabled)
            return;
        var priority = IncidentPriority(args.Kind, directlyAttacked);
        Interlocked.Increment(ref _incidentSubmissionAttempts);
        if (!TryReserveIncidentSubmission(binding.ActorId, priority, directlyAttacked, now, out var token))
        {
            Interlocked.Increment(ref _incidentSubmissionSuppressions);
            return;
        }
        _ = SubmitIncidentEscalationAsync(
            binding.ActorId,
            priority,
            token,
            policy.IncidentEscalationCooldown);
    }

    private async Task SubmitIncidentEscalationAsync(
        NCActorId actorId,
        NCNeuralPriority priority,
        Guid token,
        TimeSpan cooldown)
    {
        var submitted = false;
        try
        {
            submitted = await SubmitAutonomousAsync(actorId, priority);
        }
        catch (Exception exception)
        {
            Log.Warning($"Incident neural wake for {actorId} failed before submission: {exception.Message}");
        }

        if (!_incidentGates.TryGetValue(actorId, out var gate) || gate.PendingToken != token)
            return;
        gate.HasPending = false;
        if (!submitted)
        {
            Interlocked.Increment(ref _incidentSubmissionRejects);
            if (!gate.HasAccepted)
                _incidentGates.Remove(actorId);
            return;
        }

        Interlocked.Increment(ref _incidentSubmissions);
        gate.HasAccepted = true;
        gate.AcceptedPriority = priority;
        var acceptedAt = _clock.TryGetCurrentTime(out _, out var current) ? current : gate.ReservedAt;
        gate.NextAcceptedAt = acceptedAt.Advance(cooldown);
    }

    private bool TryReserveIncidentSubmission(
        NCActorId actorId,
        NCNeuralPriority priority,
        bool directlyAttacked,
        NCActiveWorldTime now,
        out Guid token)
    {
        token = Guid.Empty;
        if (!_incidentGates.TryGetValue(actorId, out var gate))
        {
            gate = new IncidentSubmissionGate();
            _incidentGates.Add(actorId, gate);
        }
        if (gate.HasPending && priority >= gate.PendingPriority)
            return false;
        // A direct P0 assault always bypasses a completed cooldown. Less severe events may replace only an older,
        // lower-severity cooldown, which prevents ambient incidents from hiding new danger.
        if (gate.HasAccepted && ShouldSuppressIncidentCooldown(
                directlyAttacked,
                priority,
                gate.AcceptedPriority,
                gate.NextAcceptedAt,
                now))
            return false;

        token = Guid.NewGuid();
        gate.HasPending = true;
        gate.PendingToken = token;
        gate.PendingPriority = priority;
        gate.ReservedAt = now;
        return true;
    }

    internal static bool ShouldSuppressIncidentCooldown(
        bool directlyAttacked,
        NCNeuralPriority incoming,
        NCNeuralPriority accepted,
        NCActiveWorldTime nextAcceptedAt,
        NCActiveWorldTime now) =>
        !directlyAttacked && nextAcceptedAt > now && incoming >= accepted;

    private static NCNeuralPriority IncidentPriority(
        NCCityLocalPerceivedEventKind kind,
        bool directlyAttacked) => directlyAttacked
        ? NCNeuralPriority.P0
        : kind is NCCityLocalPerceivedEventKind.Killing or
            NCCityLocalPerceivedEventKind.Assault or
            NCCityLocalPerceivedEventKind.Fire
            ? NCNeuralPriority.P1
            : NCNeuralPriority.P2;

    private void OnActionExecutionFailed(NCNeuralActionExecutionFailedEvent args)
    {
        if (!_clock.TryGetCurrentTime(out _, out var now) ||
            !_prototypes.TryIndex(DefaultPolicy, out NCNeuralPolicyPrototype? policy) || policy == null)
            return;
        _recentActionFailures[args.ActorId] = new RecentActionFailure(
            args.Failure,
            args.FailedAt,
            now.Advance(policy.RequestLifetime));

        if (_failureReplanRequests.TryGetValue(args.ActorId, out var existing))
        {
            if (existing.RequestId == args.PlanId)
            {
                // Failure of the one retry is surfaced to the next ordinary wake; it never recursively retries.
                _failureReplanRequests.Remove(args.ActorId);
                return;
            }
            if (existing.ExpiresAt > now)
                return;
            _failureReplanRequests.Remove(args.ActorId);
        }
        if (_client.IsProviderEnabled)
            _ = SubmitActionFailureReplanAsync(args.ActorId, args.PlanId, args.Priority);
    }

    private async Task SubmitActionFailureReplanAsync(
        NCActorId actorId,
        Guid failedPlanId,
        NCNeuralPriority priority)
    {
        if (await SubmitAutonomousAsync(actorId, priority, failedPlanId))
            Interlocked.Increment(ref _failureReplanSubmissions);
    }

    private void ClearCooldowns()
    {
        _incidentGates.Clear();
        _recentActionFailures.Clear();
        _failureReplanRequests.Clear();
        Interlocked.Exchange(ref _ordinaryDialogueSubmissions, 0);
        Interlocked.Exchange(ref _narrativeDialogueSubmissions, 0);
        Interlocked.Exchange(ref _incidentSubmissionAttempts, 0);
        Interlocked.Exchange(ref _incidentSubmissions, 0);
        Interlocked.Exchange(ref _incidentSubmissionRejects, 0);
        Interlocked.Exchange(ref _incidentSubmissionSuppressions, 0);
        Interlocked.Exchange(ref _failureReplanSubmissions, 0);
    }

    private bool TryResolveNarrativeProfile(
        NCCityActorRecord actor,
        NCCityActorArchetypePrototype archetype,
        out NCNarrativeActorProfilePrototype narrativeProfile)
    {
        narrativeProfile = default!;
        var profileId = !string.IsNullOrWhiteSpace(actor.Snapshot.Identity.NarrativeActorProfileId)
            ? new ProtoId<NCNarrativeActorProfilePrototype>(actor.Snapshot.Identity.NarrativeActorProfileId)
            : archetype.NarrativeActorProfile;
        if (profileId is not { } selected ||
            !_prototypes.TryIndex(selected, out NCNarrativeActorProfilePrototype? resolved) || resolved == null)
        {
            return false;
        }

        narrativeProfile = resolved;
        return true;
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

    private NCNeuralPersonaContext? BuildPersona(
        NCCityActorRecord actor,
        EntityUid? actorEntity,
        NCSocialProfileRecord? socialProfile,
        NCNeuralPersonaProfilePrototype persona)
    {
        if (!StableKey(persona.StableKey) || !StableKey(persona.RoleKey) ||
            !StableKey(persona.OrganizationKey))
            return null;
        var social = socialProfile?.Snapshot;
        var roleKey = persona.RoleKey;
        var organizationKey = persona.OrganizationKey;
        var biography = persona.Biography;
        var principles = persona.PrincipleKeys.Where(StableKey).Take(32).ToList();
        var habits = persona.HabitKeys.Where(StableKey).Take(32).ToList();
        if (actor.Snapshot.Identity.GeneratedPersona is { } generated)
        {
            var generatedPrinciples = new[]
            {
                generated.BackgroundKey,
                generated.PressureKey,
                generated.FearKey,
                generated.GoalKey,
            }.Where(StableKey).ToArray();
            principles.AddRange(generatedPrinciples);
            habits.AddRange(new[] { generated.SocialHabitKey, generated.SpeechQuirkKey }.Where(StableKey));
            if (generatedPrinciples.Length > 0)
            {
                biography = $"{biography} Life profile: {string.Join(", ", generatedPrinciples)}.";
            }
        }
        if (actorEntity is { } entity && TryComp<NCNpcJobComponent>(entity, out var npcJob) &&
            _prototypes.TryIndex(npcJob.Job, out JobPrototype? job) && job != null)
        {
            roleKey = $"job:{job.ID}";
            biography = $"{biography} Current station job: {job.LocalizedName}.";
        }
        // Residents have no automatic profession or faction overlays. Their authored persona is the complete role.
        return new NCNeuralPersonaContext(
            persona.StableKey,
            roleKey,
            organizationKey,
            NormalizeDialogueText(biography, 1024),
            principles.Distinct(StringComparer.Ordinal).Take(32).ToArray(),
            habits.Distinct(StringComparer.Ordinal).Take(32).ToArray(),
            StableOrUnknown(persona.TemperamentKey ?? social?.Personality.Temperament),
            persona.RiskTolerance ?? social?.Personality.RiskTolerance ?? 500,
            persona.Aggression ?? social?.Personality.Aggression ?? 500,
            persona.Empathy ?? social?.Personality.Empathy ?? 500,
            persona.AuthorityRespect ?? social?.Personality.AuthorityRespect ?? 500,
            persona.ViolenceTolerance ?? social?.Personality.ViolenceTolerance ?? 500,
            persona.Justice ?? social?.Values.Justice ?? 500,
            persona.Professionalism ?? social?.Values.Professionalism ?? 500,
            StableOrUnknown(persona.SpeechRegisterKey ?? social?.Speech.Register),
            StableOrUnknown(persona.VocabularyKey ?? social?.Speech.Vocabulary));
    }

    private static void TryAddFact(
        ICollection<NCNeuralFact> facts,
        IReadOnlyList<string> forbiddenTopics,
        NCMemoryPrivacy maximumPrivacy,
        string domain,
        string key,
        string subject,
        NCMemoryPrivacy privacy)
    {
        if (privacy > maximumPrivacy || forbiddenTopics.Contains(key))
            return;
        facts.Add(new NCNeuralFact(domain, key, StableSubject(subject), 1f, privacy.ToString()));
    }

    private void AddOperationalFactContext(
        NCActorId actorId,
        NCActiveWorldTime now,
        ICollection<NCNeuralFactCandidate> facts,
        ISet<string> urgentSubjects)
    {
        if (_actionRouter.TryGetSnapshot(actorId, out var action))
        {
            var current = action.State is NCHumanActionRuntimeState.Pending or NCHumanActionRuntimeState.Running;
            if (current && action.Target.Kind == NCCognitionReferenceKind.StableKey &&
                StableKey(action.Target.StableId))
                urgentSubjects.Add(action.Target.StableId);
        }

        if (_suggestions.TryGetIntentSnapshot(actorId, out var intent) && intent.ExpiresAt > now)
        {
            var subject = string.IsNullOrWhiteSpace(intent.TargetHandle)
                ? "self"
                : StableSubject(intent.TargetHandle);
            facts.Add(new NCNeuralFactCandidate(
                new NCNeuralFact("current-intent", StableSubject(intent.Key), subject, 1f,
                    NCMemoryPrivacy.Personal.ToString()),
                intent.AcceptedAt.Ticks));
            urgentSubjects.Add(subject);
        }

        if (!_recentActionFailures.TryGetValue(actorId, out var failure))
            return;
        if (failure.ExpiresAt <= now)
        {
            _recentActionFailures.Remove(actorId);
            return;
        }
        facts.Add(new NCNeuralFactCandidate(
            new NCNeuralFact("command-result", "failed", StableSubject(failure.Failure), 1f,
                NCMemoryPrivacy.Personal.ToString()),
            failure.FailedAt.Ticks));
    }

    internal static NCNeuralFact[] BuildFactPacket(
        IEnumerable<NCNeuralFactCandidate> facts,
        int maximum,
        IReadOnlySet<string> urgentSubjects)
    {
        return facts
            .Where(value => StableKey(value.Fact.Domain) && StableKey(value.Fact.Key) &&
                StableKey(value.Fact.Subject) &&
                Enum.TryParse<NCMemoryPrivacy>(value.Fact.Privacy, true, out _))
            .GroupBy(value => (value.Fact.Domain, value.Fact.Key, value.Fact.Subject, value.Fact.Privacy))
            .Select(group => group.OrderByDescending(value => value.ObservedAtTicks)
                .ThenByDescending(value => value.Fact.Confidence)
                .First())
            .OrderBy(value => RelevanceTier(value.Fact, urgentSubjects))
            .ThenByDescending(value => value.ObservedAtTicks)
            .ThenByDescending(value => value.Fact.Confidence)
            .ThenBy(value => value.Fact.Domain, StringComparer.Ordinal)
            .ThenBy(value => value.Fact.Key, StringComparer.Ordinal)
            .ThenBy(value => value.Fact.Subject, StringComparer.Ordinal)
            .Take(Math.Max(0, maximum))
            .Select((value, index) => value.Fact with { Handle = $"fact-{index + 1:D3}" })
            .ToArray();
    }

    /// <summary>
    /// Converts each accepted active-perception request into a bounded reference list over the newly rebuilt
    /// subjective packet. No result can introduce an entity or handle absent from that actor's current perception.
    /// </summary>
    internal static NCNeuralSenseResult[] BuildSenseResults(
        IReadOnlyList<NCNeuralSenseRequest>? requests,
        IReadOnlyList<NCNeuralPerceivedSubject> perceivedSubjects,
        IReadOnlyList<NCNeuralPerceivedEvent> perceivedEvents,
        NCNeuralBodyContext? body,
        IReadOnlyList<NCNeuralFact>? facts = null,
        IReadOnlyList<NCNeuralKnownLocation>? knownLocations = null,
        IReadOnlyList<NCNeuralWorldOpportunity>? worldOpportunities = null,
        IReadOnlySet<string>? resolvableRouteHandles = null)
    {
        if (requests == null || requests.Count == 0)
            return [];

        facts ??= [];
        knownLocations ??= [];
        worldOpportunities ??= [];
        resolvableRouteHandles ??= new HashSet<string>(StringComparer.Ordinal);
        var subjects = perceivedSubjects.ToDictionary(value => value.Handle, StringComparer.Ordinal);
        var knownLocationsByHandle = knownLocations.ToDictionary(value => value.Handle, StringComparer.Ordinal);
        var visibleWorldHandles = perceivedSubjects
            .Where(value => value.Handle != "self")
            .Select(value => value.Handle)
            .Take(32)
            .ToArray();
        var results = new List<NCNeuralSenseResult>(Math.Min(2, requests.Count));
        foreach (var request in requests.Take(2))
        {
            switch (request.Kind)
            {
                case "look-around":
                    results.Add(new NCNeuralSenseResult(request.Kind, string.Empty, "completed",
                        visibleWorldHandles, null)
                    {
                        OpportunityTargetHandles = worldOpportunities
                            .Where(value => subjects.ContainsKey(value.TargetHandle))
                            .Select(value => value.TargetHandle)
                            .Distinct(StringComparer.Ordinal)
                            .Take(32)
                            .ToArray(),
                    });
                    break;
                case "inspect-subject":
                case "inspect-object":
                {
                    if (!subjects.TryGetValue(request.TargetHandle, out var target))
                    {
                        results.Add(new NCNeuralSenseResult(request.Kind, request.TargetHandle, "not-visible", [],
                            null));
                        break;
                    }

                    var expectsPerson = request.Kind == "inspect-subject";
                    if (IsPersonSubject(target.Kind) != expectsPerson)
                    {
                        results.Add(new NCNeuralSenseResult(request.Kind, request.TargetHandle,
                            "wrong-target-kind", [target.Handle], null));
                        break;
                    }

                    var observedHandles = new List<string>(8) { target.Handle };
                    if (expectsPerson)
                    {
                        // A closer look may associate already visible held objects with their visible holder. It does
                        // not inspect containers, hidden equipment, or any entity outside this request's sensorium.
                        foreach (var perceivedEvent in perceivedEvents)
                        {
                            if (observedHandles.Count >= 8 || perceivedEvent.ActorHandle != target.Handle ||
                                perceivedEvent.Kind is not ("weapondisplayed" or "helditemdisplayed") ||
                                !subjects.ContainsKey(perceivedEvent.TargetHandle) ||
                                observedHandles.Contains(perceivedEvent.TargetHandle, StringComparer.Ordinal))
                            {
                                continue;
                            }
                            observedHandles.Add(perceivedEvent.TargetHandle);
                        }
                    }
                    var detailKeys = perceivedEvents
                        .Where(value => value.ActorHandle == target.Handle || value.TargetHandle == target.Handle)
                        .Select(value => $"event.{value.Kind}")
                        .Concat(worldOpportunities
                            .Where(value => value.TargetHandle == target.Handle)
                            .SelectMany(value => value.AffordanceKeys.Select(key => $"affordance.{key}")))
                        .Where(StableKey)
                        .Distinct(StringComparer.Ordinal)
                        .Take(16)
                        .ToArray();
                    results.Add(new NCNeuralSenseResult(request.Kind, request.TargetHandle, "completed",
                        observedHandles, null)
                    {
                        DetailKeys = detailKeys,
                    });
                    break;
                }
                case "check-inventory":
                    results.Add(new NCNeuralSenseResult(request.Kind, string.Empty, "completed",
                        perceivedSubjects
                            .Where(value => value.Kind is "helditem" or "equippeditem")
                            .Select(value => value.Handle)
                            .Take(32)
                            .ToArray(), null));
                    break;
                case "check-body":
                    results.Add(new NCNeuralSenseResult(request.Kind, string.Empty,
                        body == null ? "unavailable" : "completed", [], body));
                    break;
                case "query-nearby-affordances":
                    results.Add(new NCNeuralSenseResult(request.Kind, string.Empty, "completed", [], null)
                    {
                        OpportunityTargetHandles = worldOpportunities
                            .Where(value => subjects.ContainsKey(value.TargetHandle))
                            .Select(value => value.TargetHandle)
                            .Distinct(StringComparer.Ordinal)
                            .Take(32)
                            .ToArray(),
                    });
                    break;
                case "query-known-locations":
                    results.Add(new NCNeuralSenseResult(request.Kind, string.Empty, "completed", [], null)
                    {
                        LocationHandles = knownLocations.Select(value => value.Handle).Take(32).ToArray(),
                    });
                    break;
                case "recall-memories":
                    results.Add(new NCNeuralSenseResult(request.Kind, request.TargetHandle, "completed", [], null)
                    {
                        FactHandles = facts
                            .Where(value => value.Subject == request.TargetHandle)
                            .Select(value => value.Handle)
                            .Take(32)
                            .ToArray(),
                    });
                    break;
                case "examine-route":
                {
                    var known = knownLocationsByHandle.ContainsKey(request.TargetHandle);
                    var resolvable = known && resolvableRouteHandles.Contains(request.TargetHandle);
                    results.Add(new NCNeuralSenseResult(request.Kind, request.TargetHandle,
                        resolvable ? "completed" : "unavailable", [], null)
                    {
                        // An unavailable route means the live resolver no longer owns a usable location handle.
                        LocationHandles = resolvable ? [request.TargetHandle] : [],
                        DetailKeys = [resolvable ? "route.resolvable" : "route.unavailable"],
                    });
                    break;
                }
                default:
                    results.Add(new NCNeuralSenseResult(request.Kind, request.TargetHandle, "unsupported", [], null));
                    break;
            }
        }
        return results.ToArray();
    }

    private NCNeuralAvailableAction[] BuildAvailableActions(IReadOnlyList<string> capabilityIds)
    {
        var result = new List<NCNeuralAvailableAction>(capabilityIds.Count);
        foreach (var capabilityId in capabilityIds)
        {
            if (!_prototypes.TryIndex(new ProtoId<NCCognitionCapabilityPrototype>(capabilityId),
                    out var capability) || capability == null)
                continue;

            var procedureSteps = capability.ProcedureSteps
                .Concat(capability.StrategicOptions.SelectMany(option => option.ProcedureSteps))
                .ToArray();
            var actions = procedureSteps
                .Select(step => step.Action)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var actionPrototypes = actions
                .Select(actionId => _prototypes.TryIndex(new ProtoId<NCHumanActionPrototype>(actionId),
                    out var action) ? action : null)
                .Where(action => action != null)
                .Cast<NCHumanActionPrototype>()
                .ToArray();
            var goals = capability.StrategicOptions.Length > 0
                ? capability.StrategicOptions.Select(option => option.Id).Distinct(StringComparer.Ordinal).ToArray()
                : ["execute"];
            result.Add(new NCNeuralAvailableAction(
                capability.ID,
                capability.Domain.ToString().ToLowerInvariant(),
                capability.TaskKind.ToString().ToLowerInvariant(),
                goals,
                actions,
                actionPrototypes.Select(action => action.Operation.ToString().ToLowerInvariant())
                    .Distinct(StringComparer.Ordinal).ToArray(),
                actionPrototypes.Select(action => action.TargetKind.ToString().ToLowerInvariant())
                    .Distinct(StringComparer.Ordinal).ToArray()));
        }
        return result.ToArray();
    }

    private static int RelevanceTier(NCNeuralFact fact, IReadOnlySet<string> urgentSubjects)
    {
        if (urgentSubjects.Contains(fact.Subject))
            return 0;
        return fact.Domain switch
        {
            "command-result" => 1,
            "obligation" => 2,
            "relationship" => 3,
            "current-intent" => 4,
            "body" => 5,
            _ => 6,
        };
    }

    private NCCityLocalObservationKind ResolveObservationKind(
        EntityUid entityUid,
        NCCityLocalObservationKind kind) =>
        kind is NCCityLocalObservationKind.HeldItem or NCCityLocalObservationKind.EquippedItem ||
        !HasComp<ItemComponent>(entityUid)
            ? kind
            : NCCityLocalObservationKind.Item;

    private static bool IsObjectObservation(NCCityLocalObservationKind kind) => kind is
        NCCityLocalObservationKind.Item or
        NCCityLocalObservationKind.HeldItem or
        NCCityLocalObservationKind.EquippedItem or
        NCCityLocalObservationKind.Affordance or
        NCCityLocalObservationKind.Seat or
        NCCityLocalObservationKind.VendingMachine or
        NCCityLocalObservationKind.MedicalDevice;

    private static bool IsPersonSubject(string kind) => kind is
        "self" or "neutral" or "friendly" or "hostile" or "critical" or "dead" or "suspect" or
        "event-actor" or "event-target";

    private void ApplyKnownPersonIdentities(
        IDictionary<string, NCNeuralPerceivedSubject> perceivedByHandle,
        IReadOnlyList<NCCityLocalObservation> localSubjects,
        IReadOnlyList<NCRedactedSocialFact> socialFacts)
    {
        foreach (var observation in localSubjects)
        {
            if (!perceivedByHandle.TryGetValue(observation.SubjectHandle, out var perceived) ||
                !Exists(observation.EntityUid))
                continue;
            perceivedByHandle[observation.SubjectHandle] = RecognizeKnownPerson(
                perceived,
                Name(observation.EntityUid),
                socialFacts);
        }
    }

    /// <summary>
    /// Reveals a person's current physical name only when a server-owned durable social fact was joined to the
    /// request-scoped perception handle. Rumors and a visible entity name alone are never recognition evidence.
    /// </summary>
    internal static NCNeuralPerceivedSubject RecognizeKnownPerson(
        NCNeuralPerceivedSubject perceived,
        string currentEntityName,
        IReadOnlyList<NCRedactedSocialFact> socialFacts)
    {
        if (!IsPersonSubject(perceived.Kind) || perceived.Kind == "self" ||
            !socialFacts.Any(value => value.SubjectKey == perceived.Handle && value.Domain is
                "memory" or "memory-summary" or "relationship" or "obligation"))
            return perceived;
        var knownName = NormalizeDialogueText(currentEntityName, 128);
        return knownName.Length == 0
            ? perceived
            : perceived with { DisplayName = knownName, IdentityKnown = true };
    }

    internal static string ObservableDescription(
        NCCityLocalObservationKind kind,
        string? visibleObjectName = null)
    {
        if (IsObjectObservation(kind))
        {
            var normalizedName = NormalizeDialogueText(visibleObjectName, 128);
            if (normalizedName.Length > 0)
                return normalizedName;
        }
        return kind switch
        {
            NCCityLocalObservationKind.Hostile => "observable hostile person",
            NCCityLocalObservationKind.Critical => "observable critically injured person",
            NCCityLocalObservationKind.Dead => "observable body",
            NCCityLocalObservationKind.Item => "observable item",
            NCCityLocalObservationKind.HeldItem => "observable held item",
            NCCityLocalObservationKind.EquippedItem => "observable equipped item",
            NCCityLocalObservationKind.Suspect => "observable suspected person",
            NCCityLocalObservationKind.Seat => "observable seat",
            NCCityLocalObservationKind.VendingMachine => "observable vending machine",
            NCCityLocalObservationKind.MedicalDevice => "observable medical device",
            NCCityLocalObservationKind.Affordance => "observable interactive object",
            _ => "observable person",
        };
    }

    private static bool CanDisclose(NCNeuralFact fact, NCMemoryPrivacy maximumPrivacy) =>
        Enum.TryParse<NCMemoryPrivacy>(fact.Privacy, true, out var privacy) && privacy <= maximumPrivacy;

    private bool TryGetActiveFaction(
        NCFactionMembershipRecord? record,
        out NCFactionMembership membership,
        out NCFactionProfilePrototype faction)
    {
        membership = default!;
        faction = default!;
        var active = record?.Snapshot.Memberships.FirstOrDefault(value =>
            value.Status == NCFactionMembershipStatus.Active);
        if (active == null)
            return false;

        foreach (var candidate in _prototypes.EnumeratePrototypes<NCFactionProfilePrototype>())
        {
            var identity = NCPersistentIdentity.Organization(candidate.StableKey);
            if (identity.PersistentId != active.FactionId.Value)
                continue;
            membership = active;
            faction = candidate;
            return true;
        }
        return false;
    }

    private static bool StableKey(string value) => value.Length is > 0 and <= 128 && value.All(character =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    private static string StableOrUnknown(string? value) => value != null && StableKey(value) ? value : "unknown";

    private sealed record DialogueTrigger(
        NCConversationSessionSnapshot Session,
        IReadOnlyList<NCConversationTranscriptTurn> Transcript,
        string Utterance,
        EntityUid SpeakerEntity,
        string AddressScope,
        int AudienceSize);

    private sealed class IncidentSubmissionGate
    {
        public bool HasPending;
        public Guid PendingToken;
        public NCNeuralPriority PendingPriority;
        public NCActiveWorldTime ReservedAt;
        public bool HasAccepted;
        public NCNeuralPriority AcceptedPriority;
        public NCActiveWorldTime NextAcceptedAt;
    }

    private sealed record RecentActionFailure(
        string Failure,
        NCActiveWorldTime FailedAt,
        NCActiveWorldTime ExpiresAt);

    private readonly record struct FailureReplanState(Guid RequestId, NCActiveWorldTime ExpiresAt);
}

internal readonly record struct NCNeuralFactCandidate(NCNeuralFact Fact, long ObservedAtTicks);
