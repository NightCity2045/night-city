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
/// Commits one structured interaction memory per Actor and conversation. Raw utterances and provider prose remain
/// round-local, so a long conversation never becomes a database write per turn.
/// </summary>
public sealed partial class NCHumanConversationMemoryBridgeSystem : EntitySystem
{
    private static readonly ProtoId<NCMemoryEventPrototype> RoutineInteraction = "MemoryRoutineInteraction";

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
        foreach (var participant in args.Session.Participants)
        {
            if (!participant.TryGetActor(out var actorId))
                continue;
            var key = new ConversationMemoryKey(args.Session.SessionId, actorId);
            if (!_recorded.TryAdd(key, 0))
                continue;

            var owner = NCPartyRef.ForActor(actorId);
            var others = args.Session.Participants.Where(value => value != owner).ToArray();
            var stableKey = $"{owner}/{args.Session.SessionId}";
            var request = new NCRememberWorldEventRequest(
                NCPersistentIdentity.Command("memory", "conversation", stableKey),
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
                "human-conversation");
            _ = RememberAsync(key, request);
        }
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
        NCActorId ActorId);
}
