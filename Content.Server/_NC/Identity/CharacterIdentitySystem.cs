// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.Server.Preferences.Managers;
using Content.Shared._NC.Identity;
using Content.Shared._NC.Identity.Components;
using Content.Shared.GameTicking;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Robust.Shared.Network;

namespace Content.Server._NC.Identity;

/// <summary>
/// Connects a persistent character profile to its round-local mind entity.
/// The mind owns the identity so body changes do not change the persistent character.
/// </summary>
public sealed partial class CharacterIdentitySystem : EntitySystem
{
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private SharedMindSystem _mind = default!;

    private ISawmill _log = default!;

    public override void Initialize()
    {
        base.Initialize();

        _log = Logger.GetSawmill("nc.identity");
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
    }

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent args)
    {
        if (!_preferences.TryGetSelectedProfileId(args.Player.UserId, out var profileId))
        {
            // Guest sessions have no persistent database profile and intentionally receive no persistent identity.
            _log.Warning($"Unable to resolve a persistent profile for account {args.Player.UserId}.");
            return;
        }

        if (!_mind.TryGetMind(args.Mob, out var mindId, out _))
        {
            _log.Error($"Unable to bind profile {profileId}: spawned entity {ToPrettyString(args.Mob)} has no mind.");
            return;
        }

        if (!TryBindIdentity(mindId, profileId, args.Player.UserId))
            return;

        // Dependent persistence systems should load from this event instead of relying on spawn subscription order.
        var loaded = new CharacterIdentityLoadedEvent(profileId, args.Player.UserId, mindId, args.Mob);
        RaiseLocalEvent(args.Mob, ref loaded, true);
    }

    /// <summary>
    /// Binds an identity once. A conflicting second bind is rejected to prevent one mind from changing characters.
    /// </summary>
    public bool TryBindIdentity(EntityUid mindId, ProfileId profileId, NetUserId accountId)
    {
        if (!profileId.IsValid || !HasComp<MindComponent>(mindId))
            return false;

        if (TryComp<CharacterIdentityComponent>(mindId, out var existing))
        {
            if (existing.ProfileId == profileId && existing.AccountId == accountId)
                return true;

            _log.Warning(
                $"Rejected conflicting identity bind on mind {ToPrettyString(mindId)}: " +
                $"existing profile {existing.ProfileId}, requested profile {profileId}.");
            return false;
        }

        var identity = AddComp<CharacterIdentityComponent>(mindId);
        identity.ProfileId = profileId;
        identity.AccountId = accountId;
        return true;
    }

    /// <summary>
    /// Resolves the persistent identity from either a mind entity or its currently controlled body.
    /// </summary>
    public bool TryGetIdentity(EntityUid entity, out ProfileId profileId, out NetUserId accountId)
    {
        if (TryComp<CharacterIdentityComponent>(entity, out var direct))
        {
            profileId = direct.ProfileId;
            accountId = direct.AccountId;
            return profileId.IsValid;
        }

        if (_mind.TryGetMind(entity, out var mindId, out _) &&
            TryComp<CharacterIdentityComponent>(mindId, out var identity))
        {
            profileId = identity.ProfileId;
            accountId = identity.AccountId;
            return profileId.IsValid;
        }

        profileId = default;
        accountId = default;
        return false;
    }
}

/// <summary>
/// Raised after a spawned entity has been linked to its persistent character.
/// </summary>
[ByRefEvent]
public readonly record struct CharacterIdentityLoadedEvent(
    ProfileId ProfileId,
    NetUserId AccountId,
    EntityUid Mind,
    EntityUid Character);
