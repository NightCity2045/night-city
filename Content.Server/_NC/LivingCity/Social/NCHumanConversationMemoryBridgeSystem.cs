// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Social;

/// <summary>
/// Commits structured interaction memories without treating raw dialogue as truth. Routine contact is recorded once
/// per session; explicit hostile speech receives its own durable memory.
/// </summary>
public sealed partial class NCHumanConversationMemoryBridgeSystem : EntitySystem
{
    private static readonly ProtoId<NCMemoryEventPrototype> RoutineInteraction = "MemoryRoutineInteraction";
    private static readonly ProtoId<NCMemoryEventPrototype> VerbalThreat = "MemoryVerbalThreat";

    [Dependency] private NCMemoryRegistrySystem _memories = default!;

    private readonly ConcurrentDictionary<ConversationMemoryKey, byte> _recorded = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCConversationTurnRecordedEvent>(OnConversationTurn);
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(_ => _recorded.Clear());
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => _recorded.Clear());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => _recorded.Clear());
    }

    private void OnConversationTurn(NCConversationTurnRecordedEvent args)
    {
        var turn = args.Session.Turns.Count > 0 ? args.Session.Turns[^1] : null;
        foreach (var participant in args.Session.Participants)
        {
            if (!participant.TryGetActor(out var actorId))
                continue;

            var owner = NCPartyRef.ForActor(actorId);
            var others = args.Session.Participants.Where(value => value != owner).ToArray();
            var stableKey = $"{owner}/{args.Session.SessionId}";
            QueueMemory(new ConversationMemoryKey(args.Session.SessionId, actorId, "routine", 0),
                new NCRememberWorldEventRequest(
                NCPersistentIdentity.Command("memory", "conversation-routine", stableKey),
                owner,
                RoutineInteraction,
                args.Session.SessionId.Value,
                [],
                others,
                new NCMemorySource(NCMemorySourceKind.DirectPerception, default),
                [],
                null,
                null,
                NCMemoryPrivacy.Personal,
                owner,
                args.Session.SessionId.Value,
                null,
                "human-conversation"));

            if (turn is not { Act: NCSpeechActKind.Threat } || turn.Speaker == owner)
                continue;
            var threatStableKey = $"{stableKey}/{turn.Sequence}";
            var threatEventId = NCPersistentIdentity.Correlation(
                "memory",
                "conversation-threat",
                threatStableKey);
            QueueMemory(new ConversationMemoryKey(
                    args.Session.SessionId,
                    actorId,
                    "threat",
                    turn.Sequence),
                new NCRememberWorldEventRequest(
                    NCPersistentIdentity.Command("memory", "conversation-threat", threatStableKey),
                    owner,
                    VerbalThreat,
                    threatEventId,
                    [],
                    others,
                    new NCMemorySource(NCMemorySourceKind.DirectPerception, default),
                    [],
                    null,
                    null,
                    null,
                    owner,
                    args.Session.SessionId.Value,
                    null,
                    "human-conversation-threat"));
        }
    }

    private void QueueMemory(ConversationMemoryKey key, NCRememberWorldEventRequest request)
    {
        if (!_recorded.TryAdd(key, 0))
            return;
        _ = RememberAsync(key, request);
    }

    private async Task RememberAsync(ConversationMemoryKey key, NCRememberWorldEventRequest request)
    {
        try
        {
            var result = await _memories.RememberAsync(request);
            if (result.Status is NCMemoryMutationStatus.Applied or NCMemoryMutationStatus.Duplicate)
                return;
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to persist conversation memory {key.SessionId} for {key.ActorId}: {exception}");
        }
        _recorded.TryRemove(key, out _);
    }

    private readonly record struct ConversationMemoryKey(
        NCConversationSessionId SessionId,
        NCActorId ActorId,
        string Kind,
        long Sequence);
}
