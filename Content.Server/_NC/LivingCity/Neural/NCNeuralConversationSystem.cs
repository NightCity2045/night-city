// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Preferences.Managers;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Social;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Chat;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Robust.Shared.Player;
using Robust.Shared.Utility;

namespace Content.Server._NC.LivingCity.Neural;

/// <summary>
/// Owns explicit in-person neural conversations. Only speech from the player who selected Talk is forwarded, and
/// the raw utterance exists solely in the transient request event rather than a persistent conversation snapshot.
/// </summary>
public sealed partial class NCNeuralConversationSystem : EntitySystem
{
    private const string InPersonLocationKey = "in-person";

    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCConversationSessionSystem _conversations = default!;
    [Dependency] private NCNeuralCognitionClientSystem _neural = default!;

    private readonly Dictionary<EntityUid, ActiveConversation> _active = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCNeuralConversationTargetComponent, GetVerbsEvent<AlternativeVerb>>(OnGetVerbs);
        SubscribeLocalEvent<NCNeuralConversationTargetComponent, ComponentShutdown>(OnTargetShutdown);
        SubscribeLocalEvent<ActorComponent, EntitySpokeEvent>(OnPlayerSpoke);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => _active.Clear());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => _active.Clear());
    }

    private void OnGetVerbs(
        Entity<NCNeuralConversationTargetComponent> entity,
        ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || args.User == entity.Owner ||
            !HasComp<ActorComponent>(args.User) ||
            !_runtime.TryGetActor(entity.Owner, out _))
        {
            return;
        }

        var user = args.User;
        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString("nc-neural-conversation-talk-verb"),
            Priority = 3,
            Act = () => TryStartConversation(user, entity),
        });
    }

    private void TryStartConversation(EntityUid user, Entity<NCNeuralConversationTargetComponent> target)
    {
        if (!_neural.IsProviderEnabled)
        {
            Popup(user, "nc-neural-conversation-provider-unavailable");
            return;
        }
        if (!Exists(user) || !Exists(target.Owner) ||
            !_interaction.InRangeUnobstructed(user, target.Owner, target.Comp.InteractionRange) ||
            !TryComp<ActorComponent>(user, out var player) ||
            !_preferences.TryGetSelectedNCCharacterId(player.PlayerSession.UserId, out var characterId) ||
            !_runtime.TryGetActor(target.Owner, out var actorId))
        {
            Popup(user, "nc-neural-conversation-start-failed");
            return;
        }

        EndConversation(user, "replaced");
        var sessionId = NCConversationSessionId.New();
        var request = new NCConversationStartRequest(
            sessionId,
            [NCPartyRef.ForCharacter(characterId), NCPartyRef.ForActor(actorId)],
            NCConversationChannel.InPerson,
            new NCInformationSubject(NCInformationSubjectKind.StableKey, InPersonLocationKey),
            target.Comp.TopicKeys,
            [],
            target.Comp.MaximumDisclosure,
            target.Comp.Language,
            target.Comp.Locale);
        if (!_conversations.TryStart(request, out _, out _))
        {
            Popup(user, "nc-neural-conversation-start-failed");
            return;
        }

        _active[user] = new ActiveConversation(sessionId, target.Owner, actorId, characterId);
        Popup(user, "nc-neural-conversation-started");
    }

    private void OnPlayerSpoke(Entity<ActorComponent> entity, ref EntitySpokeEvent args)
    {
        // Radio and whispers are intentionally excluded: only ordinary local speech enters an explicit session.
        if (args.Channel != null || args.ObfuscatedMessage != null || !_active.TryGetValue(entity.Owner, out var active))
            return;
        if (!Exists(active.Target) ||
            !_runtime.TryGetActor(active.Target, out var boundActor) || boundActor != active.ActorId ||
            !TryComp<NCNeuralConversationTargetComponent>(active.Target, out var target) ||
            !_interaction.InRangeUnobstructed(entity.Owner, active.Target, target.InteractionRange) ||
            !_conversations.TryGet(active.SessionId, out var session) ||
            session.Status is NCConversationStatus.Ended or NCConversationStatus.Expired or
                NCConversationStatus.Interrupted)
        {
            EndConversation(entity.Owner, "invalidated");
            Popup(entity.Owner, "nc-neural-conversation-ended");
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
                NCPartyRef.ForCharacter(active.CharacterId),
                act,
                "player.utterance",
                externalReference,
                null), out _, out _, utterance))
        {
            EndConversation(entity.Owner, "record-failed");
            Popup(entity.Owner, "nc-neural-conversation-ended");
        }
    }

    private void OnTargetShutdown(
        Entity<NCNeuralConversationTargetComponent> entity,
        ref ComponentShutdown args)
    {
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
        _conversations.TryTransition(active.SessionId, NCConversationStatus.Ended, reason, out _);
    }

    private void Popup(EntityUid user, string message)
    {
        if (Exists(user))
            _popup.PopupEntity(Loc.GetString(message), user, user);
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

    private sealed record ActiveConversation(
        NCConversationSessionId SessionId,
        EntityUid Target,
        NCActorId ActorId,
        Content.Shared._NC.Identity.NCCharacterId CharacterId);
}
