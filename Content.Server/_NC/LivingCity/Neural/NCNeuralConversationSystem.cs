// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Preferences.Managers;
using Content.Server.Chat.Systems;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Social;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Chat;
using Content.Shared.Interaction;
using Content.Shared.Pointing;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server._NC.LivingCity.Neural;

/// <summary>
/// Gives neural actors ordinary local hearing. A pointed or uniquely named NPC receives direct speech; otherwise
/// every unobstructed nearby actor considers the same group address through an isolated ActorId request.
/// </summary>
public sealed partial class NCNeuralConversationSystem : EntitySystem
{
    private const string InPersonLocationKey = "in-person";
    private static readonly ProtoId<NCConversationPolicyPrototype> ConversationPolicy = "Default";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCCityLocalPerceptionSystem _perception = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCConversationSessionSystem _conversations = default!;
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private IGameTiming _timing = default!;

    private readonly Dictionary<EntityUid, ActiveConversation> _active = [];
    private readonly Dictionary<EntityUid, EntityUid> _attention = [];
    private readonly Dictionary<EntityUid, PointedAddress> _pointedAddresses = [];
    private readonly Dictionary<EntityUid, Content.Shared._NC.Persistence.Time.NCActiveWorldTime> _nextNpcSpeech = [];
    private readonly Dictionary<float, int> _listeningRanges = [];
    private float _maximumListeningRange;
    private readonly HashSet<EntityUid> _targets = [];
    private readonly Dictionary<EntityUid, bool> _targetNarrativeTiers = [];
    private int _ordinaryResidentTargets;
    private int _narrativeActorTargets;

    public int ActivePhysicalConversations => _active.Count;
    public int OrdinaryResidentTargets => _ordinaryResidentTargets;
    public int NarrativeActorTargets => _narrativeActorTargets;

    /// <summary>Refreshes the authored tier after a dynamically materialized resident has been fully configured.</summary>
    public void RefreshTarget(EntityUid target)
    {
        if (!_targetNarrativeTiers.TryGetValue(target, out var previous) ||
            !TryComp<NCNeuralConversationTargetComponent>(target, out var component) ||
            previous == component.NarrativeActor)
            return;
        _targetNarrativeTiers[target] = component.NarrativeActor;
        if (component.NarrativeActor)
        {
            _ordinaryResidentTargets = Math.Max(0, _ordinaryResidentTargets - 1);
            _narrativeActorTargets++;
        }
        else
        {
            _narrativeActorTargets = Math.Max(0, _narrativeActorTargets - 1);
            _ordinaryResidentTargets++;
        }
    }

    /// <summary>Resolves the physical speaker and address shape captured by this active local conversation.</summary>
    public bool TryGetDialogueContext(
        NCConversationSessionId sessionId,
        NCActorId listener,
        out EntityUid speaker,
        out string addressScope,
        out int audienceSize)
    {
        foreach (var (source, active) in _active)
        {
            if (active.SessionId != sessionId || !Exists(source))
                continue;
            foreach (var audience in active.Audience)
            {
                if (audience.ActorId != listener)
                    continue;
                speaker = source;
                addressScope = active.GroupAddress ? "group" : "direct";
                audienceSize = active.Audience.Count;
                return true;
            }
            if (active.SpeakerParty.TryGetActor(out var speakerActor) && speakerActor == listener &&
                active.Audience.Count == 1 && Exists(active.Audience[0].Entity))
            {
                speaker = active.Audience[0].Entity;
                addressScope = "direct";
                audienceSize = 1;
                return true;
            }
        }
        speaker = EntityUid.Invalid;
        addressScope = string.Empty;
        audienceSize = 0;
        return false;
    }

    /// <summary>Returns only actors who should independently consider replying to this physical turn.</summary>
    public void GetDialogueListeners(
        NCConversationSessionId sessionId,
        NCPartyRef turnSpeaker,
        ICollection<NCActorId> listeners)
    {
        foreach (var active in _active.Values)
        {
            if (active.SessionId != sessionId)
                continue;
            if (turnSpeaker == active.SpeakerParty)
            {
                foreach (var audience in active.Audience)
                    listeners.Add(audience.ActorId);
            }
            else if (!active.GroupAddress && active.Audience.Count == 1 &&
                     turnSpeaker == NCPartyRef.ForActor(active.Audience[0].ActorId) &&
                     active.SpeakerParty.TryGetActor(out var sourceActor))
            {
                listeners.Add(sourceActor);
            }
            return;
        }
    }

    public bool CanContinueNpcConversation(NCConversationSessionId sessionId, int currentTurns)
    {
        foreach (var active in _active.Values)
        {
            if (active.SessionId != sessionId || !active.SpeakerParty.TryGetActor(out _) ||
                active.Audience.Count != 1 ||
                !TryComp<NCNeuralConversationTargetComponent>(active.Audience[0].Entity, out var target))
                continue;
            return target.MaximumNpcConversationTurns > 0 && currentTurns < target.MaximumNpcConversationTurns;
        }
        return false;
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCNeuralConversationTargetComponent, ComponentStartup>(OnTargetStartup);
        SubscribeLocalEvent<NCNeuralConversationTargetComponent, ComponentShutdown>(OnTargetShutdown);
        SubscribeLocalEvent<ActorComponent, EntitySpokeEvent>(OnPlayerSpoke);
        SubscribeLocalEvent<NCNeuralConversationTargetComponent, EntitySpokeEvent>(OnNpcSpoke);
        SubscribeLocalEvent<ActorComponent, NCNeuralLocalEmoteEvent>(OnPlayerEmoted);
        SubscribeLocalEvent<NCNeuralConversationTargetComponent, NCNeuralLocalEmoteEvent>(OnNpcEmoted);
        SubscribeLocalEvent<ActorComponent, AfterPointedAtEvent>(OnPlayerPointed);
        SubscribeLocalEvent<NCConversationSessionTerminatedEvent>(OnConversationTerminated);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Clear());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Clear());
    }

    private void OnTargetStartup(
        Entity<NCNeuralConversationTargetComponent> entity,
        ref ComponentStartup args)
    {
        if (!_targets.Add(entity.Owner))
            return;
        _targetNarrativeTiers[entity.Owner] = entity.Comp.NarrativeActor;
        if (entity.Comp.NarrativeActor)
            _narrativeActorTargets++;
        else
            _ordinaryResidentTargets++;
        if (!entity.Comp.ListenToLocalSpeech || !ValidRange(entity.Comp.ListeningRange))
            return;
        _listeningRanges[entity.Comp.ListeningRange] =
            _listeningRanges.GetValueOrDefault(entity.Comp.ListeningRange) + 1;
        _maximumListeningRange = Math.Max(_maximumListeningRange, entity.Comp.ListeningRange);
    }

    private bool TryStartConversation(
        EntityUid user,
        IReadOnlyList<Entity<NCNeuralConversationTargetComponent>> targets,
        bool groupAddress)
    {
        if (targets.Count == 0 || !Exists(user) || !TryResolveSpeakerParty(user, out var speakerParty) ||
            !_prototypes.TryIndex(ConversationPolicy, out NCConversationPolicyPrototype? policy) || policy == null)
            return false;

        var audience = new List<AudienceMember>(Math.Min(targets.Count, policy.MaximumParticipants - 1));
        foreach (var target in targets.Take(policy.MaximumParticipants - 1))
        {
            if (!target.Comp.ListenToLocalSpeech || !ValidRange(target.Comp.ListeningRange) ||
                !Exists(target.Owner) || target.Owner == user ||
                _attention.TryGetValue(target.Owner, out var currentSpeaker) && currentSpeaker != user ||
                !_interaction.InRangeUnobstructed(user, target.Owner, target.Comp.ListeningRange) ||
                !_runtime.TryGetActor(target.Owner, out var actorId))
                continue;
            audience.Add(new AudienceMember(target.Owner, actorId, target.Comp.ListeningRange));
        }
        if (audience.Count == 0)
            return false;

        var configured = targets.Where(target => audience.Any(value => value.Entity == target.Owner)).ToArray();
        var first = configured[0].Comp;
        var topics = configured.SelectMany(value => value.Comp.TopicKeys)
            .Distinct(StringComparer.Ordinal)
            .Take(policy.MaximumTopics)
            .ToArray();
        var maximumDisclosure = (NCMemoryPrivacy) configured.Min(value => (int) value.Comp.MaximumDisclosure);

        EndConversation(user, "replaced");
        var sessionId = NCConversationSessionId.New();
        var request = new NCConversationStartRequest(
            sessionId,
            [speakerParty, .. audience.Select(value => NCPartyRef.ForActor(value.ActorId))],
            NCConversationChannel.InPerson,
            new NCInformationSubject(NCInformationSubjectKind.StableKey, InPersonLocationKey),
            topics,
            [],
            maximumDisclosure,
            first.Language,
            first.Locale);
        if (!_conversations.TryStart(request, out _, out _))
            return false;

        _active[user] = new ActiveConversation(sessionId, speakerParty, audience,
            groupAddress && audience.Count > 1);
        foreach (var member in audience)
            _attention[member.Entity] = user;
        return true;
    }

    private void OnPlayerSpoke(Entity<ActorComponent> entity, ref EntitySpokeEvent args)
    {
        HandleSpoke(entity.Owner, args);
    }

    private void OnPlayerPointed(Entity<ActorComponent> entity, ref AfterPointedAtEvent args)
    {
        if (!HasComp<NCNeuralConversationTargetComponent>(args.Pointed) ||
            !_prototypes.TryIndex(ConversationPolicy, out NCConversationPolicyPrototype? policy) || policy == null)
            return;
        _pointedAddresses[entity.Owner] = new PointedAddress(
            args.Pointed,
            _timing.CurTime + policy.PointedAddressLifetime);
    }

    private void OnNpcSpoke(Entity<NCNeuralConversationTargetComponent> entity, ref EntitySpokeEvent args)
    {
        if (!entity.Comp.SpeakWithNpcListeners || entity.Comp.NpcConversationCooldown < TimeSpan.Zero ||
            _active.ContainsKey(entity.Owner) || _attention.ContainsKey(entity.Owner) ||
            !_clock.TryGetCurrentTime(out _, out var now) ||
            _nextNpcSpeech.TryGetValue(entity.Owner, out var next) && next > now)
            return;
        _nextNpcSpeech[entity.Owner] = now.Advance(entity.Comp.NpcConversationCooldown);
        HandleSpoke(entity.Owner, args);
    }

    private void HandleSpoke(EntityUid speaker, EntitySpokeEvent args)
    {
        // Radio and obfuscated speech are excluded: neural actors only hear ordinary local speech through the map.
        if (args.Channel != null || args.ObfuscatedMessage != null)
            return;
        HandleLocalExpression(speaker, args.Message, false);
    }

    private void OnPlayerEmoted(Entity<ActorComponent> entity, ref NCNeuralLocalEmoteEvent args) =>
        HandleLocalExpression(entity.Owner, args.Message, true);

    private void OnNpcEmoted(Entity<NCNeuralConversationTargetComponent> entity, ref NCNeuralLocalEmoteEvent args)
    {
        if (!entity.Comp.SpeakWithNpcListeners || entity.Comp.NpcConversationCooldown < TimeSpan.Zero ||
            _active.ContainsKey(entity.Owner) || _attention.ContainsKey(entity.Owner) ||
            !_clock.TryGetCurrentTime(out _, out var now) ||
            _nextNpcSpeech.TryGetValue(entity.Owner, out var next) && next > now)
            return;
        _nextNpcSpeech[entity.Owner] = now.Advance(entity.Comp.NpcConversationCooldown);
        HandleLocalExpression(entity.Owner, args.Message, true);
    }

    private void HandleLocalExpression(EntityUid speaker, string message, bool emote)
    {
        // A fresh point or an unambiguous spoken name is an explicit change of addressee. Direct conversations
        // must not pin the speaker to the first NPC forever; reusing the same addressee still preserves the session.
        if (_active.TryGetValue(speaker, out var active) &&
            TryAcquireAttention(speaker, message, out var direct, true))
        {
            active = direct;
        }
        else if (!_active.TryGetValue(speaker, out active))
        {
            if (!TryAcquireAttention(speaker, message, out active))
                return;
        }
        TryRecordLocalExpression(speaker, active, message, emote);
    }

    private bool TryRecordLocalExpression(
        EntityUid speaker,
        ActiveConversation active,
        string message,
        bool emote)
    {
        if (!_conversations.TryGet(active.SessionId, out var session) ||
            session.Status is NCConversationStatus.Ended or NCConversationStatus.Expired or
                NCConversationStatus.Interrupted)
        {
            EndConversation(speaker, "invalidated");
            return false;
        }

        var validAudience = new List<AudienceMember>(active.Audience.Count);
        var maximumUtteranceLength = int.MaxValue;
        foreach (var member in active.Audience)
        {
            if (!Exists(member.Entity) ||
                !_runtime.TryGetActor(member.Entity, out var boundActor) || boundActor != member.ActorId ||
                !TryComp<NCNeuralConversationTargetComponent>(member.Entity, out var target) ||
                !_interaction.InRangeUnobstructed(speaker, member.Entity, member.ListeningRange))
            {
                if (_attention.TryGetValue(member.Entity, out var current) && current == speaker)
                    _attention.Remove(member.Entity);
                continue;
            }
            validAudience.Add(member);
            maximumUtteranceLength = Math.Min(maximumUtteranceLength, target.MaximumUtteranceLength);
        }
        if (validAudience.Count == 0)
        {
            EndConversation(speaker, "out-of-range");
            return false;
        }
        if (validAudience.Count != active.Audience.Count)
        {
            active = active with
            {
                Audience = validAudience,
                GroupAddress = active.GroupAddress && validAudience.Count > 1,
            };
            _active[speaker] = active;
        }

        var utterance = NormalizeUtterance(message, maximumUtteranceLength);
        if (utterance.Length == 0)
            return false;
        if (emote)
            utterance = $"*{utterance}*";
        NCConversationPolicyPrototype? policy = null;
        var verbalThreat = !emote && _prototypes.TryIndex(ConversationPolicy, out policy) &&
                           policy != null && ContainsVerbalThreat(utterance, policy.VerbalThreatMarkers);
        var act = verbalThreat
            ? NCSpeechActKind.Threat
            : utterance.EndsWith('?')
                ? NCSpeechActKind.Question
                : NCSpeechActKind.Statement;
        var externalReference = $"{(emote ? "emote" : "chat")}:{Guid.NewGuid():N}";
        if (!_conversations.TryRecordTurn(new NCConversationTurnRequest(
                active.SessionId,
                active.SpeakerParty,
                act,
                emote ? "local.emote" : "local.utterance",
                externalReference,
                null), out _, out _, utterance))
        {
            EndConversation(speaker, "record-failed");
            return false;
        }
        if (_clock.TryGetCurrentTime(out _, out var now))
        {
            foreach (var member in validAudience)
            {
                _perception.ObserveAddressedSpeech(member.Entity, speaker, now);
                if (verbalThreat && policy != null)
                    _perception.ObserveIncident(member.Entity, NCCityLocalPerceivedEventKind.VerbalThreat,
                        speaker, member.Entity, now, policy.VerbalThreatLifetime);
            }
        }
        return true;
    }

    /// <summary>Matches authored threat stems only; dialogue meaning remains data-driven and multilingual.</summary>
    internal static bool ContainsVerbalThreat(string utterance, IReadOnlyList<string> markers)
    {
        if (string.IsNullOrWhiteSpace(utterance))
            return false;
        return markers.Any(marker => !string.IsNullOrWhiteSpace(marker) &&
            utterance.Contains(marker.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private void OnTargetShutdown(
        Entity<NCNeuralConversationTargetComponent> entity,
        ref ComponentShutdown args)
    {
        if (_targets.Remove(entity.Owner))
        {
            var narrative = _targetNarrativeTiers.Remove(entity.Owner, out var configuredTier)
                ? configuredTier
                : entity.Comp.NarrativeActor;
            if (narrative)
                _narrativeActorTargets = Math.Max(0, _narrativeActorTargets - 1);
            else
                _ordinaryResidentTargets = Math.Max(0, _ordinaryResidentTargets - 1);
        }
        if (entity.Comp.ListenToLocalSpeech && ValidRange(entity.Comp.ListeningRange) &&
            _listeningRanges.TryGetValue(entity.Comp.ListeningRange, out var count))
        {
            if (count <= 1)
                _listeningRanges.Remove(entity.Comp.ListeningRange);
            else
                _listeningRanges[entity.Comp.ListeningRange] = count - 1;
            _maximumListeningRange = _listeningRanges.Count == 0 ? 0f : _listeningRanges.Keys.Max();
        }
        if (_active.ContainsKey(entity.Owner))
            EndConversation(entity.Owner, "speaker-removed");
        foreach (var user in _active.Where(value => value.Value.Audience.Any(member => member.Entity == entity.Owner))
                     .Select(value => value.Key).ToArray())
        {
            var active = _active[user];
            var audience = active.Audience.Where(member => member.Entity != entity.Owner).ToArray();
            _attention.Remove(entity.Owner);
            if (audience.Length == 0)
                EndConversation(user, "target-removed");
            else
                _active[user] = active with
                {
                    Audience = audience,
                    GroupAddress = active.GroupAddress && audience.Length > 1,
                };
        }
        foreach (var pointer in _pointedAddresses.Where(value => value.Value.Target == entity.Owner)
                     .Select(value => value.Key).ToArray())
            _pointedAddresses.Remove(pointer);
    }

    private void EndConversation(EntityUid user, string reason)
    {
        if (!_active.Remove(user, out var active))
            return;
        foreach (var member in active.Audience)
        {
            if (_attention.TryGetValue(member.Entity, out var speaker) && speaker == user)
                _attention.Remove(member.Entity);
        }
        _conversations.TryTransition(active.SessionId, NCConversationStatus.Ended, reason, out _);
    }

    /// <summary>Ends the physical conversation that owns this durable session, if it is still active.</summary>
    public bool TryEndConversation(NCConversationSessionId sessionId, string reason)
    {
        foreach (var (speaker, active) in _active)
        {
            if (active.SessionId != sessionId)
                continue;
            EndConversation(speaker, reason);
            return true;
        }
        return false;
    }

    private void OnConversationTerminated(NCConversationSessionTerminatedEvent args)
    {
        foreach (var speaker in _active
                     .Where(pair => pair.Value.SessionId == args.SessionId)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            // The durable owner already archived the session, so only release physical attention here.
            if (!_active.Remove(speaker, out var active))
                continue;
            foreach (var member in active.Audience)
            {
                if (_attention.TryGetValue(member.Entity, out var currentSpeaker) && currentSpeaker == speaker)
                    _attention.Remove(member.Entity);
            }
        }
    }

    private bool TryAcquireAttention(
        EntityUid speaker,
        string message,
        out ActiveConversation active,
        bool explicitOnly = false)
    {
        active = default!;
        if (_maximumListeningRange <= 0f || !Exists(speaker))
            return false;
        var origin = _transform.GetMapCoordinates(speaker);
        var candidates = new List<Entity<NCNeuralConversationTargetComponent>>();
        foreach (var candidate in _lookup.GetEntitiesInRange<NCNeuralConversationTargetComponent>(
                     origin,
                     _maximumListeningRange))
        {
            if (!candidate.Comp.ListenToLocalSpeech || !ValidRange(candidate.Comp.ListeningRange) ||
                candidate.Owner == speaker ||
                !_runtime.TryGetActor(candidate.Owner, out _) ||
                !_interaction.InRangeUnobstructed(speaker, candidate.Owner, candidate.Comp.ListeningRange))
            {
                continue;
            }
            candidates.Add(candidate);
        }
        if (candidates.Count == 0)
            return false;
        candidates.Sort((left, right) => DistanceSquared(origin, left.Owner).CompareTo(
            DistanceSquared(origin, right.Owner)));

        var direct = FindPointedListener(speaker, candidates) ?? FindNamedListener(message, candidates);
        if (direct == null && explicitOnly)
            return false;
        if (direct is { } explicitlyAddressed)
        {
            // A name or a fresh point is stronger than passive conversation attention. Release the old ordinary
            // session first so a player can deliberately address another NPC in a crowd or interrupt NPC chatter.
            if (_attention.TryGetValue(explicitlyAddressed.Owner, out var previousSpeaker) &&
                previousSpeaker != speaker)
            {
                EndConversation(previousSpeaker, "attention-redirected");
            }
            if (explicitlyAddressed.Owner != speaker && _active.ContainsKey(explicitlyAddressed.Owner))
                EndConversation(explicitlyAddressed.Owner, "attention-redirected");
        }
        if (direct is { } addressed && _active.TryGetValue(speaker, out var existing) &&
            existing.Audience.Count == 1 && existing.Audience[0].Entity == addressed.Owner)
        {
            active = existing;
            return true;
        }
        IReadOnlyList<Entity<NCNeuralConversationTargetComponent>> selected = direct is { } target
            ? [target]
            : candidates.Where(candidate =>
                !_attention.TryGetValue(candidate.Owner, out var currentSpeaker) || currentSpeaker == speaker)
                .ToArray();
        if (!TryStartConversation(speaker, selected, direct == null) ||
            !_active.TryGetValue(speaker, out var startedConversation))
            return false;
        active = startedConversation;
        return true;
    }

    private Entity<NCNeuralConversationTargetComponent>? FindPointedListener(
        EntityUid speaker,
        IReadOnlyList<Entity<NCNeuralConversationTargetComponent>> candidates)
    {
        if (!_pointedAddresses.TryGetValue(speaker, out var pointed))
            return null;
        if (pointed.ExpiresAt <= _timing.CurTime)
        {
            _pointedAddresses.Remove(speaker);
            return null;
        }
        foreach (var candidate in candidates)
        {
            if (candidate.Owner == pointed.Target)
                return candidate;
        }
        return null;
    }

    private Entity<NCNeuralConversationTargetComponent>? FindNamedListener(
        string message,
        IReadOnlyList<Entity<NCNeuralConversationTargetComponent>> candidates)
    {
        Entity<NCNeuralConversationTargetComponent>? match = null;
        foreach (var candidate in candidates)
        {
            if (!NameMentioned(message, Name(candidate.Owner)))
                continue;
            if (match != null)
                return null;
            match = candidate;
        }
        return match;
    }

    private static bool NameMentioned(string utterance, string displayName)
    {
        var spoken = NormalizeNameSearch(utterance);
        var name = NormalizeNameSearch(displayName);
        if (spoken.Length == 0 || name.Length == 0)
            return false;
        if ($" {spoken} ".Contains($" {name} ", StringComparison.Ordinal))
            return true;
        var spokenWords = spoken.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(word => word.Length >= 3 && spokenWords.Contains(word));
    }

    private static string NormalizeNameSearch(string value)
    {
        var cleaned = new string(FormattedMessage.RemoveMarkupPermissive(value)
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
            .ToArray());
        return string.Join(' ', cleaned.Split((char[]?) null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private float DistanceSquared(MapCoordinates origin, EntityUid target)
    {
        var coordinates = _transform.GetMapCoordinates(target);
        return origin.MapId == coordinates.MapId
            ? System.Numerics.Vector2.DistanceSquared(origin.Position, coordinates.Position)
            : float.MaxValue;
    }

    private void Clear()
    {
        foreach (var user in _active.Keys.ToArray())
            EndConversation(user, "round-ended");
        _attention.Clear();
        _pointedAddresses.Clear();
        _nextNpcSpeech.Clear();
    }

    private bool TryResolveSpeakerParty(EntityUid speaker, out NCPartyRef party)
    {
        if (_runtime.TryGetActor(speaker, out var actorId))
        {
            party = NCPartyRef.ForActor(actorId);
            return true;
        }
        if (TryComp<ActorComponent>(speaker, out var player) &&
            _preferences.TryGetSelectedNCCharacterId(player.PlayerSession.UserId, out var characterId))
        {
            party = NCPartyRef.ForCharacter(characterId);
            return true;
        }
        party = default;
        return false;
    }

    private static string NormalizeUtterance(string value, int maximumLength)
    {
        if (maximumLength < 1)
            return string.Empty;
        var withoutMarkup = FormattedMessage.RemoveMarkupPermissive(value);
        var normalized = string.Join(' ', withoutMarkup.Split((char[]?) null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Substring(0, Math.Min(maximumLength, normalized.Length));
    }

    private static bool ValidRange(float value) => float.IsFinite(value) && value > 0f;

    private sealed record ActiveConversation(
        NCConversationSessionId SessionId,
        NCPartyRef SpeakerParty,
        IReadOnlyList<AudienceMember> Audience,
        bool GroupAddress);

    private readonly record struct AudienceMember(EntityUid Entity, NCActorId ActorId, float ListeningRange);
    private readonly record struct PointedAddress(EntityUid Target, TimeSpan ExpiresAt);

}
