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
using Content.Shared._NC.Persistence.Time;
using Content.Shared.Chat;
using Content.Shared.Interaction;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server._NC.LivingCity.Neural;

/// <summary>
/// Gives neural actors ordinary local hearing. The nearest available NPC may focus on one unobstructed speaker;
/// raw utterances exist solely in transient request events rather than persistent conversation snapshots.
/// </summary>
public sealed partial class NCNeuralConversationSystem : EntitySystem
{
    private const string InPersonLocationKey = "in-person";

    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCCityActorDematerializationSystem _dematerialization = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCConversationSessionSystem _conversations = default!;
    [Dependency] private NCNeuralCognitionClientSystem _neural = default!;
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private IGameTiming _timing = default!;

    private readonly Dictionary<EntityUid, ActiveConversation> _active = [];
    private readonly Dictionary<EntityUid, EntityUid> _attention = [];
    private readonly Dictionary<EntityUid, Content.Shared._NC.Persistence.Time.NCActiveWorldTime> _nextNpcSpeech = [];
    private readonly Dictionary<float, int> _listeningRanges = [];
    private float _maximumListeningRange;
    private readonly HashSet<EntityUid> _targets = [];
    private readonly Dictionary<EntityUid, bool> _targetNarrativeTiers = [];
    private readonly PriorityQueue<ScheduledInitiative, (long DueTicks, long Sequence)> _initiativeQueue = new();
    private readonly Dictionary<EntityUid, NCActiveWorldTime> _initiativeDue = [];
    private long _initiativeSequence;
    private TimeSpan _nextInitiativeAttempt;
    private int _ordinaryResidentTargets;
    private int _narrativeActorTargets;

    public int ActivePhysicalConversations => _active.Count;
    public int ScheduledAmbientInitiatives => _initiativeDue.Count;
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
            if (_clock.TryGetCurrentTime(out _, out var now))
                ScheduleInitiative(target, now);
        }
        else
        {
            _narrativeActorTargets = Math.Max(0, _narrativeActorTargets - 1);
            _ordinaryResidentTargets++;
            _initiativeDue.Remove(target);
        }
    }

    /// <summary>Resolves only the physical speaker already bound to this in-person conversation.</summary>
    public bool TryGetSpeaker(NCConversationSessionId sessionId, NCActorId listener, out EntityUid speaker)
    {
        foreach (var (entityUid, active) in _active)
        {
            if (active.SessionId != sessionId || !Exists(entityUid))
                continue;
            if (_runtime.TryGetActor(active.Target, out var targetActor) && targetActor == listener)
            {
                speaker = entityUid;
                return true;
            }
            if (active.SpeakerParty.TryGetActor(out var speakerActor) && speakerActor == listener &&
                Exists(active.Target))
            {
                speaker = active.Target;
                return true;
            }
        }
        speaker = EntityUid.Invalid;
        return false;
    }

    public bool CanContinueNpcConversation(NCConversationSessionId sessionId, int currentTurns)
    {
        foreach (var active in _active.Values)
        {
            if (active.SessionId != sessionId || !active.SpeakerParty.TryGetActor(out _) ||
                !TryComp<NCNeuralConversationTargetComponent>(active.Target, out var target))
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
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Clear());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Clear());
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(_ => ScheduleAllInitiatives());
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_neural.IsProviderEnabled || _timing.CurTime < _nextInitiativeAttempt ||
            !_clock.TryGetCurrentTime(out _, out var now))
            return;
        _nextInitiativeAttempt = _timing.CurTime + TimeSpan.FromSeconds(1);
        while (_initiativeQueue.TryPeek(out var item, out var priority) && priority.DueTicks <= now.Ticks)
        {
            _initiativeQueue.Dequeue();
            if (!_initiativeDue.TryGetValue(item.Actor, out var due) || due.Ticks != priority.DueTicks)
                continue;
            _initiativeDue.Remove(item.Actor);
            TryInitiateAmbientConversation(item.Actor);
            ScheduleInitiative(item.Actor, now);
            break;
        }
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
        if (_clock.TryGetCurrentTime(out _, out var now))
            ScheduleInitiative(entity.Owner, now);
    }

    private bool TryStartConversation(EntityUid user, Entity<NCNeuralConversationTargetComponent> target)
    {
        if (!target.Comp.ListenToLocalSpeech ||
            !ValidRange(target.Comp.ListeningRange) || !Exists(user) || !Exists(target.Owner) ||
            _attention.TryGetValue(target.Owner, out var currentSpeaker) && currentSpeaker != user ||
            user == target.Owner ||
            !_interaction.InRangeUnobstructed(user, target.Owner, target.Comp.ListeningRange) ||
            !TryResolveSpeakerParty(user, out var speakerParty) ||
            !_runtime.TryGetActor(target.Owner, out var actorId))
        {
            return false;
        }

        EndConversation(user, "replaced");
        var sessionId = NCConversationSessionId.New();
        var request = new NCConversationStartRequest(
            sessionId,
            [speakerParty, NCPartyRef.ForActor(actorId)],
            NCConversationChannel.InPerson,
            new NCInformationSubject(NCInformationSubjectKind.StableKey, InPersonLocationKey),
            target.Comp.TopicKeys,
            [],
            target.Comp.MaximumDisclosure,
            target.Comp.Language,
            target.Comp.Locale);
        if (!_conversations.TryStart(request, out _, out _))
            return false;

        _active[user] = new ActiveConversation(
            sessionId,
            target.Owner,
            actorId,
            speakerParty,
            target.Comp.ListeningRange);
        _attention[target.Owner] = user;
        BeginConversationBlockers(user, target.Owner);
        return true;
    }

    private void OnPlayerSpoke(Entity<ActorComponent> entity, ref EntitySpokeEvent args)
    {
        HandleSpoke(entity.Owner, args);
    }

    private void OnNpcSpoke(Entity<NCNeuralConversationTargetComponent> entity, ref EntitySpokeEvent args)
    {
        if (!entity.Comp.SpeakWithNpcListeners || entity.Comp.NpcConversationCooldown < TimeSpan.Zero ||
            _attention.ContainsKey(entity.Owner) || !_clock.TryGetCurrentTime(out _, out var now) ||
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
        if (!_active.TryGetValue(speaker, out var active))
        {
            if (!TryAcquireAttention(speaker) || !_active.TryGetValue(speaker, out active))
                return;
        }
        if (!Exists(active.Target) ||
            !_runtime.TryGetActor(active.Target, out var boundActor) || boundActor != active.ActorId ||
            !TryComp<NCNeuralConversationTargetComponent>(active.Target, out var target) ||
            !_interaction.InRangeUnobstructed(speaker, active.Target, active.ListeningRange) ||
            !_conversations.TryGet(active.SessionId, out var session) ||
            session.Status is NCConversationStatus.Ended or NCConversationStatus.Expired or
                NCConversationStatus.Interrupted)
        {
            EndConversation(speaker, "invalidated");
            return;
        }

        var utterance = NormalizeUtterance(args.Message, target.MaximumUtteranceLength);
        if (utterance.Length == 0)
            return;
        var act = utterance.EndsWith('?')
            ? NCSpeechActKind.Question
            : NCSpeechActKind.Statement;
        var externalReference = $"chat:{Guid.NewGuid():N}";
        if (!_conversations.TryRecordTurn(new NCConversationTurnRequest(
                active.SessionId,
                active.SpeakerParty,
                act,
                "player.utterance",
                externalReference,
                null), out _, out _, utterance))
        {
            EndConversation(speaker, "record-failed");
        }
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
        _initiativeDue.Remove(entity.Owner);
        if (entity.Comp.ListenToLocalSpeech && ValidRange(entity.Comp.ListeningRange) &&
            _listeningRanges.TryGetValue(entity.Comp.ListeningRange, out var count))
        {
            if (count <= 1)
                _listeningRanges.Remove(entity.Comp.ListeningRange);
            else
                _listeningRanges[entity.Comp.ListeningRange] = count - 1;
            _maximumListeningRange = _listeningRanges.Count == 0 ? 0f : _listeningRanges.Keys.Max();
        }
        foreach (var user in _active.Where(value => value.Value.Target == entity.Owner)
                     .Select(value => value.Key).ToArray())
        {
            EndConversation(user, "target-removed");
        }
    }

    private void EndConversation(EntityUid user, string reason)
    {
        if (!_active.Remove(user, out var active))
            return;
        if (_attention.TryGetValue(active.Target, out var speaker) && speaker == user)
            _attention.Remove(active.Target);
        EndConversationBlockers(user, active.Target);
        _conversations.TryTransition(active.SessionId, NCConversationStatus.Ended, reason, out _);
    }

    private void BeginConversationBlockers(EntityUid speaker, EntityUid target)
    {
        // Conversation is a physical commitment for every persistent participant, not merely for the neural target.
        if (_runtime.TryGetActor(target, out _))
            _dematerialization.BeginBlocker(target, NCCityDematerializationBlockerFlags.Conversation);
        if (speaker != target && _runtime.TryGetActor(speaker, out _))
            _dematerialization.BeginBlocker(speaker, NCCityDematerializationBlockerFlags.Conversation);
    }

    private void EndConversationBlockers(EntityUid speaker, EntityUid target)
    {
        if (_runtime.TryGetActor(target, out _))
            _dematerialization.EndBlocker(target, NCCityDematerializationBlockerFlags.Conversation);
        if (speaker != target && _runtime.TryGetActor(speaker, out _))
            _dematerialization.EndBlocker(speaker, NCCityDematerializationBlockerFlags.Conversation);
    }

    private bool TryAcquireAttention(EntityUid speaker)
    {
        if (_maximumListeningRange <= 0f || !Exists(speaker))
            return false;
        var origin = _transform.GetMapCoordinates(speaker);
        Entity<NCNeuralConversationTargetComponent>? selected = null;
        var selectedDistance = float.MaxValue;
        foreach (var candidate in _lookup.GetEntitiesInRange<NCNeuralConversationTargetComponent>(
                     origin,
                     _maximumListeningRange))
        {
            if (!candidate.Comp.ListenToLocalSpeech || !ValidRange(candidate.Comp.ListeningRange) ||
                candidate.Owner == speaker ||
                _attention.ContainsKey(candidate.Owner) || !_runtime.TryGetActor(candidate.Owner, out _) ||
                !_interaction.InRangeUnobstructed(speaker, candidate.Owner, candidate.Comp.ListeningRange))
            {
                continue;
            }
            var targetCoordinates = _transform.GetMapCoordinates(candidate.Owner);
            var distance = System.Numerics.Vector2.DistanceSquared(origin.Position, targetCoordinates.Position);
            if (distance >= selectedDistance)
                continue;
            selected = candidate;
            selectedDistance = distance;
        }

        return selected is { } target && TryStartConversation(speaker, target);
    }

    private void Clear()
    {
        foreach (var user in _active.Keys.ToArray())
            EndConversation(user, "round-ended");
        _attention.Clear();
        _nextNpcSpeech.Clear();
        _initiativeDue.Clear();
        _initiativeQueue.Clear();
        _initiativeSequence = 0;
    }

    private void ScheduleAllInitiatives()
    {
        _initiativeDue.Clear();
        _initiativeQueue.Clear();
        if (!_clock.TryGetCurrentTime(out _, out var now))
            return;
        // This is a round-boundary rebuild, never a per-frame entity scan.
        foreach (var target in _targets)
            ScheduleInitiative(target, now);
    }

    private void ScheduleInitiative(EntityUid actor, NCActiveWorldTime now)
    {
        if (!TryComp<NCNeuralConversationTargetComponent>(actor, out var component) ||
            !component.InitiateAmbientConversations || component.AmbientOpeners.Count == 0 ||
            component.MinimumAmbientConversationInterval < TimeSpan.Zero ||
            component.MaximumAmbientConversationInterval < component.MinimumAmbientConversationInterval)
            return;
        var range = component.MaximumAmbientConversationInterval - component.MinimumAmbientConversationInterval;
        var seed = unchecked((uint) actor.Id * 2654435761u + (uint) _initiativeSequence);
        var fraction = (seed & 0x00ffffff) / (float) 0x01000000;
        var delay = component.MinimumAmbientConversationInterval + range * fraction;
        var due = now.Advance(delay);
        _initiativeDue[actor] = due;
        var scheduled = new ScheduledInitiative(actor, ++_initiativeSequence);
        _initiativeQueue.Enqueue(scheduled, (due.Ticks, scheduled.Sequence));
    }

    private void TryInitiateAmbientConversation(EntityUid speaker)
    {
        if (!TryComp<NCNeuralConversationTargetComponent>(speaker, out var component) ||
            !Exists(speaker) || _active.ContainsKey(speaker) || _attention.ContainsKey(speaker) ||
            !_runtime.TryGetActor(speaker, out _))
            return;
        var origin = _transform.GetMapCoordinates(speaker);
        Entity<NCNeuralConversationTargetComponent>? selected = null;
        var selectedDistance = float.MaxValue;
        var checkedCandidates = 0;
        foreach (var candidate in _lookup.GetEntitiesInRange<NCNeuralConversationTargetComponent>(
                     origin, component.ListeningRange))
        {
            if (++checkedCandidates > component.MaximumAmbientCandidates)
                break;
            if (candidate.Owner == speaker || _active.ContainsKey(candidate.Owner) ||
                _attention.ContainsKey(candidate.Owner) || !_runtime.TryGetActor(candidate.Owner, out _) ||
                !_interaction.InRangeUnobstructed(speaker, candidate.Owner, component.ListeningRange))
                continue;
            var candidateCoordinates = _transform.GetMapCoordinates(candidate.Owner);
            var distance = System.Numerics.Vector2.DistanceSquared(origin.Position, candidateCoordinates.Position);
            if (distance >= selectedDistance)
                continue;
            selected = candidate;
            selectedDistance = distance;
        }
        if (selected is not { } target || !TryStartConversation(speaker, target))
            return;
        var index = (int) (unchecked((uint) HashCode.Combine(speaker.Id, _initiativeSequence)) %
                           (uint) component.AmbientOpeners.Count);
        var opener = NormalizeUtterance(component.AmbientOpeners[index], component.MaximumUtteranceLength);
        if (opener.Length > 0)
            _chat.TrySendInGameICMessage(speaker, opener, InGameICChatType.Speak, hideChat: false,
                checkRadioPrefix: false);
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
        EntityUid Target,
        NCActorId ActorId,
        NCPartyRef SpeakerParty,
        float ListeningRange);

    private readonly record struct ScheduledInitiative(EntityUid Actor, long Sequence);
}
