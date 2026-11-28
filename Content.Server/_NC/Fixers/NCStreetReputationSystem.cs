// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Server._NC.Identity;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Shared._NC.Fixers;
using Content.Shared.GameTicking;
using Content.Shared.Mind;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Fixers;

/// <summary>
/// Street reputation shared by all fixers. Loaded when a character spawns and saved after every change.
/// </summary>
public sealed class NCStreetReputationSystem : EntitySystem
{
    public const int MaximumReputation = 1000;

    [Dependency] private IServerDbManager _db = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedMindSystem _minds = default!;

    private readonly Dictionary<int, int> _reputation = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
    }

    private async void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent args)
    {
        if (!_preferences.TryGetSelectedNCCharacterId(args.Player.UserId, out var characterId) || !characterId.IsValid)
            return;

        try
        {
            var value = await _db.GetNCStreetReputationAsync(characterId.Value);
            _reputation.TryAdd(characterId.Value, value);
        }
        catch (Exception exception)
        {
            Log.Error($"Unable to load the street reputation of profile {characterId.Value}: {exception}");
            return;
        }

        if (!TerminatingOrDeleted(args.Mob))
            Mirror(args.Mob, GetReputation(characterId.Value));
    }

    /// <summary>The character profile a player-controlled entity belongs to.</summary>
    public bool TryGetProfile(EntityUid user, out int profileId)
    {
        profileId = 0;
        if (!_minds.TryGetMind(user, out var mindId, out _) ||
            !TryComp<NCCharacterIdentityComponent>(mindId, out var identity) ||
            !identity.CharacterId.IsValid)
        {
            return false;
        }

        profileId = identity.CharacterId.Value;
        return true;
    }

    public int GetReputation(int profileId)
    {
        return _reputation.GetValueOrDefault(profileId);
    }

    public int GetLevel(int reputation)
    {
        return _prototypes.TryIndex<NCStreetReputationPrototype>(NCStreetReputationPrototype.DefaultId, out var levels)
            ? levels.Level(reputation)
            : 0;
    }

    public void AddReputation(int profileId, int delta)
    {
        var value = Math.Clamp(GetReputation(profileId) + delta, 0, MaximumReputation);
        _reputation[profileId] = value;
        Persist(profileId, value);

        var minds = EntityQueryEnumerator<NCCharacterIdentityComponent, MindComponent>();
        while (minds.MoveNext(out var identity, out var mind))
        {
            if (identity.CharacterId.IsValid && identity.CharacterId.Value == profileId &&
                mind.OwnedEntity is { } body && !TerminatingOrDeleted(body))
            {
                Mirror(body, value);
            }
        }
    }

    /// <summary>Shows the reputation in the owner's character window.</summary>
    private void Mirror(EntityUid body, int value)
    {
        var mirror = EnsureComp<NCStreetReputationComponent>(body);
        if (mirror.Reputation == value)
            return;

        mirror.Reputation = value;
        Dirty(body, mirror);
    }

    private async void Persist(int profileId, int value)
    {
        try
        {
            await _db.SetNCStreetReputationAsync(profileId, value);
        }
        catch (Exception exception)
        {
            Log.Error($"Unable to save the street reputation of profile {profileId}: {exception}");
        }
    }
}
