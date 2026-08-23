// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using System.Threading;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server.Preferences.Managers;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Social;

/// <summary>
/// Converts unique directly witnessed incidents into durable structured memory. Repeated damage refreshes the same
/// sensory event and therefore never creates a database write per hit.
/// </summary>
public sealed partial class NCHumanMemoryBridgeSystem : EntitySystem
{
    private static readonly ProtoId<NCMemoryEventPrototype> Violence = "MemoryViolenceWitnessed";
    private static readonly ProtoId<NCMemoryEventPrototype> Death = "MemoryDeathWitnessed";

    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCMemoryRegistrySystem _memories = default!;
    [Dependency] private ISharedPlayerManager _players = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;

    private int _writesInFlight;
    private long _remembered;
    private long _rejected;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCityLocalIncidentObservedEvent>(OnIncidentObserved);
    }

    public NCHumanMemoryBridgeCounters GetCounters() =>
        new(_writesInFlight, Interlocked.Read(ref _remembered), Interlocked.Read(ref _rejected));

    private void OnIncidentObserved(NCCityLocalIncidentObservedEvent args)
    {
        if (!_runtime.TryGetActor(args.Observer, out var observer) || !observer.IsValid)
            return;
        var owner = NCPartyRef.ForActor(observer);
        var participants = new List<NCPartyRef>(2);
        if (TryResolveParty(args.Actor, out var actor) && actor != owner)
            participants.Add(actor);
        if (TryResolveParty(args.Target, out var target) && target != owner && !participants.Contains(target))
            participants.Add(target);

        var request = new NCRememberWorldEventRequest(
            Guid.NewGuid(),
            owner,
            args.Kind == NCCityLocalPerceivedEventKind.Killing ? Death : Violence,
            args.EventId,
            [],
            participants,
            new NCMemorySource(NCMemorySourceKind.DirectPerception, default),
            [],
            null,
            null,
            null,
            owner,
            args.EventId,
            null,
            "human-direct-perception");
        _writesInFlight++;
        _ = RememberAsync(request);
    }

    private async Task RememberAsync(NCRememberWorldEventRequest request)
    {
        try
        {
            var result = await _memories.RememberAsync(request);
            if (result.Status is NCMemoryMutationStatus.Applied or NCMemoryMutationStatus.Duplicate)
                Interlocked.Increment(ref _remembered);
            else
                Interlocked.Increment(ref _rejected);
        }
        finally
        {
            Interlocked.Decrement(ref _writesInFlight);
        }
    }

    private bool TryResolveParty(EntityUid entity, out NCPartyRef party)
    {
        if (_runtime.TryGetActor(entity, out var actor) && actor.IsValid)
        {
            party = NCPartyRef.ForActor(actor);
            return true;
        }
        if (_players.TryGetSessionByEntity(entity, out var session) &&
            _preferences.TryGetSelectedNCCharacterId(session.UserId, out var character))
        {
            party = NCPartyRef.ForCharacter(character);
            return true;
        }
        party = default;
        return false;
    }
}

public readonly record struct NCHumanMemoryBridgeCounters(int WritesInFlight, long Remembered, long Rejected);
