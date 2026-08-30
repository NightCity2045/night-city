// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Server.Preferences.Managers;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Player;

namespace Content.Server._NC.LivingCity.Identity;

/// <summary>
/// Resolves a currently materialized entity to its canonical persistent party. This is the shared identity boundary
/// used by perception, memory and conversation; callers must never derive persistent identity from an entity UID.
/// </summary>
public sealed partial class NCEntityPartyResolverSystem : EntitySystem
{
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private ISharedPlayerManager _players = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;

    public bool TryResolve(EntityUid entityUid, out NCPartyRef party)
    {
        if (_runtime.TryGetActor(entityUid, out var actorId) && actorId.IsValid)
        {
            party = NCPartyRef.ForActor(actorId);
            return true;
        }

        if (_players.TryGetSessionByEntity(entityUid, out var session) &&
            _preferences.TryGetSelectedNCCharacterId(session.UserId, out var characterId))
        {
            party = NCPartyRef.ForCharacter(characterId);
            return true;
        }

        party = default;
        return false;
    }
}
