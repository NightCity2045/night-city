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
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;
using Content.Shared.Roles;

namespace Content.Server._NC.LivingCity.Neural;

/// <summary>Builds the only neural-safe context from the existing privacy redaction boundary.</summary>
public sealed partial class NCNeuralContextBuilderSystem : EntitySystem
{
    private static readonly ProtoId<NCNeuralPolicyPrototype> DefaultPolicy = "Default";
    private static readonly ProtoId<NCNeuralPromptProfilePrototype> DefaultPrompt = "NCLivingCityDefault";
    private const string DialogueCapability = "social.dialogue.reply";
    private const string ApproachPersonCapability = "NCCognitionApproachPerson";
    private const string AcceptDirectiveCapability = "NCCognitionAcceptDirective";
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCEntityPartyResolverSystem _entityParties = default!;
    [Dependency] private NCCityLocalPerceptionSystem _perception = default!;
    [Dependency] private NCLocationKnowledgeSystem _locationKnowledge = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NCCognitionSystem _cognition = default!;
    [Dependency] private NCSocialContextRedactionSystem _redaction = default!;
    [Dependency] private NCSocialProfileRegistrySystem _socialProfiles = default!;
    [Dependency] private NCOrganizationWorkforceSystem _workforce = default!;
    [Dependency] private NCProfessionProfileSystem _professions = default!;
    [Dependency] private NCFactionMembershipRegistrySystem _factionMemberships = default!;
    [Dependency] private NCNeuralCognitionClientSystem _client = default!;
    [Dependency] private NCNeuralSuggestionSystem _suggestions = default!;
    [Dependency] private NCNeuralConversationSystem _neuralConversations = default!;
    private readonly Dictionary<NCActorId, NCActiveWorldTime> _nextIncidentEscalation = [];

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
                out var archetype) || archetype == null ||
            !TryResolveNarrativeProfile(actor, archetype, out var narrativeProfile) ||
            !_prototypes.TryIndex(narrativeProfile.AutonomyPolicy, out var autonomyPolicy) || autonomyPolicy == null ||
            !autonomyPolicy.AllowedKinds.Contains(kind))
            return false;
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
            foreach (var subject in localSubjects.Take(policy.MaximumPerceivedSubjects))
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
                .Take(policy.MaximumPerceivedSubjects)
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
        var purpose = conversation == null ? NCSocialContextPurpose.InternalReasoning :
            NCSocialContextPurpose.ConversationRendering;
        var context = await _redaction.BuildAsync(
            NCPartyRef.ForActor(actorId), purpose, conversation, participantAliases);
        if (context == null)
            return false;
        var facts = context.Facts.Where(value => !context.ForbiddenTopics.Contains(value.FactKey))
            .Select(value => new NCNeuralFact(value.Domain, value.FactKey, StableSubject(value.SubjectKey),
                value.Confidence, value.Privacy.ToString())).ToList();
        if (_cognition.TryGetSnapshot(actorId, out var cognition))
        {
            var physicalFacts = cognition.Facts
                .Where(value => value.ExpiresAt > now && value.Subject.IsValid &&
                                value.Subject.StableId.StartsWith("perceived-", StringComparison.Ordinal))
                .OrderByDescending(value => value.ObservedAt.Ticks)
                .Take(policy.MaximumPerceivedSubjects)
                .ToArray();
            foreach (var value in physicalFacts.GroupBy(value => value.Subject.StableId, StringComparer.Ordinal)
                         .Select(group => group.First()))
            {
                perceivedByHandle.TryAdd(value.Subject.StableId, new NCNeuralPerceivedSubject(
                    value.Subject.StableId, "Unknown person", value.Key, 1f, value.ExpiresAt.Ticks));
            }
            facts.AddRange(physicalFacts.Select(value => new NCNeuralFact(
                value.Domain.ToString(), value.Key, value.Subject.StableId, 1f, "Observed")));
            if (cognition.Intention is { } intention)
            {
                facts.Add(new NCNeuralFact(
                    "Core", "cognition.current-intention", StableSubject(intention.CapabilityId), 1f, "Observed"));
            }
        }
        var perceived = perceivedByHandle.Values.Take(policy.MaximumPerceivedSubjects).ToArray();
        var knownLocations = BuildKnownLocations(actor, policy.MaximumKnownLocations);
        var socialProfileTask = _socialProfiles.GetAsync(actorId);
        var factionMembershipTask = _factionMemberships.GetAsync(actorId);
        await Task.WhenAll(socialProfileTask, factionMembershipTask);
        var socialProfile = await socialProfileTask;
        var factionMembership = await factionMembershipTask;
        AddRoleFacts(actorId, factionMembership, facts);
        var persona = BuildPersona(actor, socialProfile, factionMembership, narrativeProfile);
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
            var utterance = NormalizeDialogueText(transientUtterance, policy.MaximumDialogueInputLength);
            var actorName = NormalizeDialogueText(actor.Snapshot.Identity.FullName, 128);
            if (utterance.Length == 0 || actorName.Length == 0)
                return false;
            var history = conversation is { } transcriptSession
                ? _conversations.GetTranscript(transcriptSession, 13).SkipLast(1)
                    .Select(value => new NCNeuralDialogueTurn(
                        value.Speaker == NCPartyRef.ForActor(actorId) ? "self" : "interlocutor",
                        value.Act.ToString().ToLowerInvariant(),
                        NormalizeDialogueText(value.Utterance, policy.MaximumDialogueInputLength),
                        value.At.Ticks))
                    .ToArray()
                : [];
            dialogueInput = new NCNeuralDialogueInput(actorName, utterance, speakerHandle, history);
        }
        var effectiveCapabilities = allowedCapabilities
            .Where(value => value != ApproachPersonCapability || speakerHandle.Length > 0)
            .Concat(kind == NCNeuralRequestKind.DialogueContinuation ? [AcceptDirectiveCapability] : [])
            .Distinct()
            .Take(policy.MaximumCapabilities)
            .ToArray();
        if (effectiveCapabilities.Length == 0)
            return false;
        var directiveActions = effectiveCapabilities.Contains(AcceptDirectiveCapability)
            ? _prototypes.EnumeratePrototypes<NCHumanDirectiveActionPrototype>()
                .Select(value => value.ID)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];
        var request = new NCNeuralRequestEnvelope(Guid.NewGuid(), kind, priority, actorId, epoch,
            actor.AggregateVersion, now.Advance(policy.RequestLifetime), DefaultPrompt,
            facts.Take(policy.MaximumFacts).ToArray(),
            effectiveCapabilities, conversationSnapshot?.CurrentTopicKeys ?? [], "ru-RU", persona, autonomy,
            perceived, perceivedEvents, knownLocations, dialogueInput, directiveActions);
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
        _ = SubmitAsync(actorId, NCNeuralRequestKind.DialogueContinuation, NCNeuralPriority.P1,
            [DialogueCapability, ApproachPersonCapability, AcceptDirectiveCapability], session.SessionId,
            args.TransientUtterance);
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
        if (!_client.IsProviderEnabled ||
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
        var actor = await _actors.GetAsync(actorId, true);
        if (actor == null ||
            !_prototypes.TryIndex(new ProtoId<NCCityActorArchetypePrototype>(actor.Snapshot.Identity.ArchetypeId),
                out var archetype) || archetype == null ||
            !TryResolveNarrativeProfile(actor, archetype, out var narrativeProfile) ||
            !narrativeProfile.IncidentEscalationEnabled ||
            !_prototypes.TryIndex(archetype.CognitionCapabilityProfile, out NCCognitionCapabilityProfilePrototype? profile) ||
            profile == null)
            return;

        // Only explicitly authored narrative Actors may escalate an incident to Neural Cognition.
        var capabilities = profile.Capabilities.ToList();
        _professions.AddCapabilityOverlays(actorId, capabilities);
        var allowed = capabilities.Select(value => value.Id).Distinct(StringComparer.Ordinal).ToArray();
        if (allowed.Length == 0)
            return;
        await SubmitCoreAsync(actorId, NCNeuralRequestKind.StrategicDecision,
            kind == NCCityLocalPerceivedEventKind.Killing ? NCNeuralPriority.P1 : NCNeuralPriority.P2,
            allowed, null, null);
    }

    private void ClearCooldowns()
    {
        _nextIncidentEscalation.Clear();
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
        NCFactionMembershipRecord? factionMembership,
        NCNarrativeActorProfilePrototype narrativeProfile)
    {
        if (!_prototypes.TryIndex(narrativeProfile.PersonaProfile, out var persona) || persona == null)
            return null;
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
        else if (TryGetActiveFaction(factionMembership, out var membership, out var faction))
        {
            roleKey = StableOrUnknown(membership.Role);
            organizationKey = faction.StableKey;
            principles.AddRange(faction.IdeologyKeys.Where(StableKey));
            biography = $"{biography} Affiliation: {faction.DisplayName}; role: {membership.Role}.".Trim();
        }
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

    private void AddRoleFacts(NCActorId actorId, NCFactionMembershipRecord? affiliation, List<NCNeuralFact> facts)
    {
        if (_workforce.TryGetAssignment(actorId, out var organization, out var job))
        {
            facts.Add(new NCNeuralFact("Jobs", "employment.organization", StableSubject(organization.Id),
                1f, "Private"));
            facts.Add(new NCNeuralFact("Jobs", "employment.position", StableSubject(job.Id), 1f, "Private"));
            if (_professions.TryGetProfile(actorId, out var profession))
            {
                foreach (var skill in profession.SkillKeys.Where(StableKey).Distinct(StringComparer.Ordinal))
                    facts.Add(new NCNeuralFact("Jobs", "employment.skill", StableSubject(skill), 1f, "Private"));
            }
        }

        if (!TryGetActiveFaction(affiliation, out var membership, out var faction))
            return;
        facts.Add(new NCNeuralFact("Faction", "faction.membership", StableSubject(faction.StableKey),
            1f, "Private"));
        facts.Add(new NCNeuralFact("Faction", "faction.role", StableSubject(membership.Role), 1f, "Private"));
    }

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
