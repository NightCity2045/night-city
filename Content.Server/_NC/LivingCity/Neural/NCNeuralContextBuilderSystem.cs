// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.CityServices;
using Content.Server._NC.LivingCity.Employment;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Social;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.LivingCity.Employment;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;
using Content.Shared.Roles;
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
    private const string DialogueCapability = "social.dialogue.reply";
    private const string ApproachPersonCapability = "NCCognitionApproachPerson";
    private const string AcceptDirectiveCapability = "NCCognitionAcceptDirective";
    private const string RespondToThreatCapability = "NCCognitionRespondToThreat";
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
    [Dependency] private NCOrganizationWorkforceSystem _workforce = default!;
    [Dependency] private NCLivelihoodRegistrySystem _livelihoods = default!;
    [Dependency] private NCProfessionProfileSystem _professions = default!;
    [Dependency] private NCFactionMembershipRegistrySystem _factionMemberships = default!;
    [Dependency] private NCNeuralCognitionClientSystem _client = default!;
    [Dependency] private NCNeuralSuggestionSystem _suggestions = default!;
    [Dependency] private NCNeuralConversationSystem _neuralConversations = default!;
    private readonly Dictionary<NCActorId, NCActiveWorldTime> _nextIncidentEscalation = [];
    private long _ordinaryDialogueSubmissions;
    private long _narrativeDialogueSubmissions;

    public long OrdinaryDialogueSubmissions => Interlocked.Read(ref _ordinaryDialogueSubmissions);
    public long NarrativeDialogueSubmissions => Interlocked.Read(ref _narrativeDialogueSubmissions);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCConversationTurnRecordedEvent>(OnConversationTurn);
        SubscribeLocalEvent<NCCityLocalIncidentObservedEvent>(OnIncidentObserved);
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(_ => ClearCooldowns());
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => ClearCooldowns());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => ClearCooldowns());
    }

    public async Task<bool> SubmitAsync(NCActorId actorId, NCNeuralRequestKind kind, NCNeuralPriority priority,
        IReadOnlyList<string> allowedCapabilities, NCConversationSessionId? conversation = null,
        string? transientUtterance = null)
    {
        return await SubmitCoreAsync(actorId, kind, priority, allowedCapabilities, conversation,
            transientUtterance);
    }

    /// <summary>Submits one independent physical actor decision using its authored capability allowlist.</summary>
    public async Task<bool> SubmitAutonomousAsync(NCActorId actorId, NCNeuralPriority priority)
    {
        var actor = await _actors.GetAsync(actorId, true);
        if (actor == null ||
            !_prototypes.TryIndex(new ProtoId<NCCityActorArchetypePrototype>(actor.Snapshot.Identity.ArchetypeId),
                out var archetype) || archetype == null ||
            !_prototypes.TryIndex(archetype.CognitionCapabilityProfile,
                out NCCognitionCapabilityProfilePrototype? profile) || profile == null)
            return false;

        var capabilities = profile.Capabilities.ToList();
        _professions.AddCapabilityOverlays(actorId, capabilities);
        var allowed = capabilities.Select(value => value.Id).Distinct(StringComparer.Ordinal).ToArray();
        return allowed.Length > 0 && await SubmitCoreAsync(
            actorId,
            NCNeuralRequestKind.StrategicDecision,
            priority,
            allowed,
            null,
            null);
    }

    private async Task<bool> SubmitCoreAsync(NCActorId actorId, NCNeuralRequestKind kind,
        NCNeuralPriority priority, IReadOnlyList<string> allowedCapabilities,
        NCConversationSessionId? conversation, string? transientUtterance)
    {
        if (!_client.IsProviderEnabled || !_clock.TryGetCurrentTime(out var epoch, out var now) ||
            !_prototypes.TryIndex(DefaultPolicy, out NCNeuralPolicyPrototype? policy) ||
            !_prototypes.TryIndex(DefaultPrompt, out NCNeuralPromptProfilePrototype? prompt) ||
            !prompt.AllowedKinds.Contains(kind)) return false;
        var actor = await _actors.GetAsync(actorId, true);
        if (actor == null)
            return false;
        if (!_prototypes.TryIndex(new ProtoId<NCCityActorArchetypePrototype>(actor.Snapshot.Identity.ArchetypeId),
                out var archetype) || archetype == null)
            return false;
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
            if (!_prototypes.TryIndex(DefaultResidentPersona, out personaProfile) ||
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
        EntityUid speakerEntity = EntityUid.Invalid;
        var speakerHandle = string.Empty;
        if (conversation is { } liveConversation &&
            _neuralConversations.TryGetSpeaker(liveConversation, actorId, out speakerEntity) &&
            _runtime.TryGetEntity(actorId, out var actorEntity))
        {
            // A spoken turn receives a fresh bounded scan rather than waiting for the background BDI cadence.
            _perception.RefreshNow(actorId, now);
            _perception.ObserveSubject(actorEntity, speakerEntity, NCCityLocalObservationKind.Neutral, now,
                policy.RequestLifetime);
        }

        var perceivedByHandle = new Dictionary<string, NCNeuralPerceivedSubject>(StringComparer.Ordinal);
        var participantAliases = new Dictionary<NCPartyRef, string>();
        var perceivedEvents = Array.Empty<NCNeuralPerceivedEvent>();
        if (_perception.TryGetSnapshot(actorId, now, out var localSubjects, out var localEvents))
        {
            foreach (var subject in localSubjects.Take(maximumPerceivedSubjects))
            {
                perceivedByHandle[subject.SubjectHandle] = new NCNeuralPerceivedSubject(
                    subject.SubjectHandle,
                    NormalizeDialogueText(Name(subject.EntityUid), 128),
                    subject.Kind.ToString().ToLowerInvariant(),
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
            foreach (var perceivedEvent in perceivedEvents)
            {
                if (!perceivedByHandle.ContainsKey(perceivedEvent.ActorHandle))
                    perceivedByHandle[perceivedEvent.ActorHandle] = new NCNeuralPerceivedSubject(
                        perceivedEvent.ActorHandle, "Unknown person", "event-actor", perceivedEvent.Confidence,
                        perceivedEvent.ExpiresAtTicks);
                if (!perceivedByHandle.ContainsKey(perceivedEvent.TargetHandle))
                    perceivedByHandle[perceivedEvent.TargetHandle] = new NCNeuralPerceivedSubject(
                        perceivedEvent.TargetHandle, "Unknown person", "event-target", perceivedEvent.Confidence,
                        perceivedEvent.ExpiresAtTicks);
            }
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
        var facts = context.Facts.Where(value => !context.ForbiddenTopics.Contains(value.FactKey))
            .Select(value => new NCNeuralFact(value.Domain, value.FactKey, StableSubject(value.SubjectKey),
                value.Confidence, value.Privacy.ToString())).ToList();
        var perceived = perceivedByHandle.Values.Take(maximumPerceivedSubjects).ToArray();
        var knownLocations = BuildKnownLocations(actor, maximumKnownLocations);
        var socialProfileTask = _socialProfiles.GetAsync(actorId);
        var factionMembershipTask = _factionMemberships.GetAsync(actorId);
        var livelihoodTask = _livelihoods.GetAsync(actorId);
        await Task.WhenAll(socialProfileTask, factionMembershipTask, livelihoodTask);
        var socialProfile = await socialProfileTask;
        var factionMembership = await factionMembershipTask;
        var livelihood = await livelihoodTask;
        AddRoleFacts(actorId, livelihood, factionMembership, context.EffectiveMaximumPrivacy,
            context.ForbiddenTopics, facts);
        var persona = BuildPersona(actor, socialProfile, livelihood, personaProfile);
        if (persona == null)
            return false;
        var autonomy = new NCNeuralAutonomyContext(
            autonomyPolicy.StableKey,
            autonomyPolicy.MinimumIntentConfidence,
            autonomyPolicy.MaximumIntentPriority,
            autonomyPolicy.TargetedIntentsAllowed);
        NCConversationSessionSnapshot? conversationSnapshot = null;
        if (conversation is { } sessionId && !_conversations.TryGet(sessionId, out conversationSnapshot!))
            return false;
        NCNeuralDialogueInput? dialogueInput = null;
        if (kind == NCNeuralRequestKind.DialogueContinuation)
        {
            var utterance = NormalizeDialogueText(transientUtterance, maximumDialogueInputLength);
            var actorName = NormalizeDialogueText(actor.Snapshot.Identity.FullName, 128);
            if (utterance.Length == 0 || actorName.Length == 0)
                return false;
            var history = conversation is { } transcriptSession
                ? _conversations.GetTranscript(transcriptSession, maximumDialogueHistoryTurns + 1).SkipLast(1)
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
            var archivedMemories = conversationSnapshot == null
                ? Array.Empty<NCConversationMemoryContext>()
                : await _conversationArchive.GetRelevantAsync(
                    actorId,
                    conversationSnapshot.Participants,
                    conversationSnapshot.CurrentTopicKeys,
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
            dialogueInput = new NCNeuralDialogueInput(actorName, utterance, speakerHandle, history, earlierSummary);
        }
        var effectiveCapabilities = allowedCapabilities
            .Where(value => value != ApproachPersonCapability || speakerHandle.Length > 0)
            .Concat(narrativeAccess && kind == NCNeuralRequestKind.DialogueContinuation
                ? [AcceptDirectiveCapability]
                : [])
            .Distinct()
            .Take(policy.MaximumCapabilities)
            .ToArray();
        if (effectiveCapabilities.Length == 0)
            return false;
        var hasOrganization = _workforce.TryGetAssignment(actorId, out var actorOrganization, out _);
        var directiveActions = effectiveCapabilities.Contains(AcceptDirectiveCapability)
            ? _prototypes.EnumeratePrototypes<NCHumanDirectiveActionPrototype>()
                .Where(value => value.AllowedOrganizations.Count == 0 ||
                    hasOrganization && value.AllowedOrganizations.Contains(actorOrganization))
                .Select(value => value.ID)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];
        var packetFacts = BuildFactPacket(facts, maximumFacts);
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
            packetFacts,
            effectiveCapabilities, conversationSnapshot?.CurrentTopicKeys ?? [], "ru-RU", persona, autonomy,
            perceived, perceivedEvents, knownLocations, dialogueInput, directiveActions, disclosure)
        {
            MaximumOutputTokens = narrativeAccess
                ? policy.MaximumRequestOutputTokens
                : policy.OrdinaryDialogueMaximumOutputTokens,
            AccessTier = narrativeAccess
                ? NCNeuralAccessTier.NarrativeActor
                : NCNeuralAccessTier.OrdinaryResident,
        };
        var trackedDialogue = kind == NCNeuralRequestKind.DialogueContinuation && conversationSnapshot != null;
        if (trackedDialogue && !_suggestions.TryTrackDialogue(
                request.RequestId,
                conversationSnapshot!.SessionId,
                conversationSnapshot.ConversationVersion))
            return false;
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
        return false;
    }

    [Dependency] private NCConversationSessionSystem _conversations = default!;
    private void OnConversationTurn(NCConversationTurnRecordedEvent args)
    {
        var session = args.Session;
        if (session.Turns.Count == 0)
            return;
        var speaker = session.Turns[^1].Speaker;
        if (speaker.TryGetActor(out _) &&
            !_neuralConversations.CanContinueNpcConversation(session.SessionId, session.Turns.Count))
            return;
        var actor = session.Participants.FirstOrDefault(value => value.TryGetActor(out _) && value != speaker);
        if (!actor.TryGetActor(out var actorId))
            return;

        // Dialogue may propose only the generic social approach capability. The proposal still passes through
        // perception validation, BDI selection and ordinary NPC steering before any movement occurs.
        _ = ContinueDialogueAsync(actorId, session.SessionId, args.TransientUtterance ?? string.Empty);
    }

    private async Task ContinueDialogueAsync(
        NCActorId actorId,
        NCConversationSessionId sessionId,
        string transientUtterance)
    {
        await SubmitAsync(actorId, NCNeuralRequestKind.DialogueContinuation, NCNeuralPriority.P1,
            [DialogueCapability, ApproachPersonCapability, AcceptDirectiveCapability, RespondToThreatCapability], sessionId,
            transientUtterance);
    }

    private NCNeuralKnownLocation[] BuildKnownLocations(NCCityActorRecord actor, int maximum)
    {
        _locationKnowledge.Seed(actor);
        var knownZones = new HashSet<NCZoneId>();
        _locationKnowledge.GetKnown(actor.Snapshot.Identity.ActorId, knownZones);
        // Requests may complete concurrently; a per-request buffer avoids sharing mutable query state.
        var zoneBuffer = new List<NCZoneInfo>();
        _zones.GetZones(zoneBuffer);
        return zoneBuffer
            .Where(zone => zone.ContextSlot == NCZoneContextSlot.District || knownZones.Contains(zone.Id))
            .OrderBy(zone => zone.ContextSlot)
            .ThenBy(zone => zone.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, maximum))
            .Select(zone => new NCNeuralKnownLocation(
                $"zone:{zone.Id.Value:N}",
                zone.Name,
                zone.ContextSlot.ToString().ToLowerInvariant()))
            .ToArray();
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
            !_client.IsProviderEnabled ||
            !TryComp<NCCityActorBindingComponent>(args.Observer, out var binding) ||
            !_clock.TryGetCurrentTime(out _, out var now) ||
            !_prototypes.TryIndex(DefaultPolicy, out NCNeuralPolicyPrototype? policy) ||
            _nextIncidentEscalation.TryGetValue(binding.ActorId, out var next) && next > now)
            return;

        _nextIncidentEscalation[binding.ActorId] = now.Advance(policy.IncidentEscalationCooldown);
        _ = SubmitIncidentEscalationAsync(binding.ActorId, args.Kind);
    }

    private async Task SubmitIncidentEscalationAsync(NCActorId actorId, NCCityLocalPerceivedEventKind kind)
    {
        var priority = kind is NCCityLocalPerceivedEventKind.Killing or
            NCCityLocalPerceivedEventKind.Assault or
            NCCityLocalPerceivedEventKind.Fire
            ? NCNeuralPriority.P1
            : NCNeuralPriority.P2;
        await SubmitAutonomousAsync(actorId, priority);
    }

    private void ClearCooldowns()
    {
        _nextIncidentEscalation.Clear();
        Interlocked.Exchange(ref _ordinaryDialogueSubmissions, 0);
        Interlocked.Exchange(ref _narrativeDialogueSubmissions, 0);
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
        NCSocialProfileRecord? socialProfile,
        NCLivelihoodRecord? livelihood,
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
        if (_workforce.TryGetAssignment(actor.Snapshot.Identity.ActorId, out var organizationId, out var jobId))
        {
            roleKey = jobId.Id;
            organizationKey = organizationId.Id;
            if (_prototypes.TryIndex(jobId, out JobPrototype? job) && job != null)
                biography = $"{biography} Current occupation: {job.LocalizedName}.".Trim();
        }
        else if (livelihood != null &&
                 _prototypes.TryIndex(new ProtoId<NCLivelihoodProfilePrototype>(livelihood.Snapshot.ProfileId),
                     out var livelihoodProfile) && livelihoodProfile != null)
        {
            roleKey = livelihood.Snapshot.ProfileId;
            organizationKey = "independent";
            biography = $"{biography} Current livelihood: {livelihoodProfile.DisplayName}.".Trim();
        }
        // Hidden faction membership is never inferred into the provider-facing persona. Authored persona data is the
        // public identity; private affiliations travel only through the purpose-bounded fact packet below.
        return new NCNeuralPersonaContext(
            persona.StableKey,
            roleKey,
            organizationKey,
            NormalizeDialogueText(biography, 1024),
            principles.Distinct(StringComparer.Ordinal).Take(32).ToArray(),
            persona.HabitKeys.Where(StableKey).Take(32).ToArray(),
            StableOrUnknown(social?.Personality.Temperament),
            social?.Personality.RiskTolerance ?? 500,
            social?.Personality.Aggression ?? 500,
            social?.Personality.Empathy ?? 500,
            social?.Personality.AuthorityRespect ?? 500,
            social?.Personality.ViolenceTolerance ?? 500,
            social?.Values.Justice ?? 500,
            social?.Values.Professionalism ?? 500,
            StableOrUnknown(social?.Speech.Register),
            StableOrUnknown(social?.Speech.Vocabulary));
    }

    private void AddRoleFacts(
        NCActorId actorId,
        NCLivelihoodRecord? livelihood,
        NCFactionMembershipRecord? affiliation,
        NCMemoryPrivacy maximumPrivacy,
        IReadOnlyList<string> forbiddenTopics,
        List<NCNeuralFact> facts)
    {
        if (_workforce.TryGetAssignment(actorId, out var organization, out var job))
        {
            TryAddFact(facts, forbiddenTopics, maximumPrivacy, "Jobs", "employment.organization",
                organization.Id, NCMemoryPrivacy.Public);
            TryAddFact(facts, forbiddenTopics, maximumPrivacy, "Jobs", "employment.position",
                job.Id, NCMemoryPrivacy.Public);
            if (_professions.TryGetProfile(actorId, out var profession))
            {
                foreach (var skill in profession.SkillKeys.Where(StableKey).Distinct(StringComparer.Ordinal))
                    TryAddFact(facts, forbiddenTopics, maximumPrivacy, "Jobs", "employment.skill",
                        skill, NCMemoryPrivacy.Personal);
            }
        }
        else if (livelihood != null &&
                 _prototypes.TryIndex(new ProtoId<NCLivelihoodProfilePrototype>(livelihood.Snapshot.ProfileId),
                     out var livelihoodProfile) && livelihoodProfile != null)
        {
            TryAddFact(facts, forbiddenTopics, maximumPrivacy, "Jobs", "livelihood.profile",
                livelihood.Snapshot.ProfileId, NCMemoryPrivacy.Public);
            foreach (var capability in livelihoodProfile.Capabilities.Where(StableKey).Distinct(StringComparer.Ordinal))
                TryAddFact(facts, forbiddenTopics, maximumPrivacy, "Jobs", "livelihood.capability",
                    capability, NCMemoryPrivacy.Personal);
        }

        if (!TryGetActiveFaction(affiliation, out var membership, out var faction))
            return;
        TryAddFact(facts, forbiddenTopics, maximumPrivacy, "Faction", "faction.membership",
            faction.StableKey, NCMemoryPrivacy.Secret);
        TryAddFact(facts, forbiddenTopics, maximumPrivacy, "Faction", "faction.role",
            membership.Role, NCMemoryPrivacy.Secret);
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

    private static NCNeuralFact[] BuildFactPacket(IEnumerable<NCNeuralFact> facts, int maximum)
    {
        return facts
            .Where(value => StableKey(value.Domain) && StableKey(value.Key) && StableKey(value.Subject) &&
                Enum.TryParse<NCMemoryPrivacy>(value.Privacy, true, out _))
            .GroupBy(value => (value.Domain, value.Key, value.Subject, value.Privacy))
            .Select(group => group.OrderByDescending(value => value.Confidence).First())
            .OrderBy(value => value.Domain, StringComparer.Ordinal)
            .ThenBy(value => value.Key, StringComparer.Ordinal)
            .ThenBy(value => value.Subject, StringComparer.Ordinal)
            .Take(Math.Max(0, maximum))
            .Select((value, index) => value with { Handle = $"fact-{index + 1:D3}" })
            .ToArray();
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
}
