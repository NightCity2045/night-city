// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Preferences.Managers;
using Content.Shared.GameTicking;
using Content.Shared.Players;

namespace Content.Server._NC.Identity;

/// <summary>
/// Binds the authenticated selected profile to the player's mind after spawning.
/// </summary>
public sealed partial class NCCharacterIdentitySystem : EntitySystem
{
    [Dependency] private IServerPreferencesManager _preferences = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
    }

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent args)
    {
        if (!_preferences.TryGetSelectedNCCharacterId(args.Player.UserId, out var characterId) ||
            args.Player.GetMind() is not { } mind)
        {
            return;
        }

        var identity = EnsureComp<NCCharacterIdentityComponent>(mind);
        identity.CharacterId = characterId;
    }
}
