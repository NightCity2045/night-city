using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server.Preferences.Managers;
using Content.Server._NC.Identity;
using Content.Shared._NC.CCVar;
using Content.Shared._NC.Persistence.Components;
using Content.Shared.GameTicking;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Configuration;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;

namespace Content.Server._NC.Persistence;

/// <summary>
/// Declares death immediately but performs destructive cleanup only at round end or lobby exit.
/// Revival in the same round cancels the pending declaration.
/// </summary>
public sealed partial class PermadeathSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private CharacterIdentitySystem _identity = default!;
    [Dependency] private GameTicker _ticker = default!;

    private readonly Dictionary<Guid, PendingDeath> _pendingByAccount = new();
    private readonly Dictionary<Guid, SemaphoreSlim> _lifecycleLocks = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<RoundEndMessageEvent>(OnRoundEnd);
        SubscribeLocalEvent<PlayerJoinedLobbyEvent>(OnJoinedLobby);
    }

    private void OnMobStateChanged(MobStateChangedEvent args)
    {
        if (!_configuration.GetCVar(NCCVars.PermadeathEnabled) ||
            !_identity.TryGetIdentity(args.Target, out var profileId, out var accountId))
            return;

        if (args.NewMobState == MobState.Dead)
        {
            var pending = new PendingDeath(profileId.Value, accountId.UserId, Guid.NewGuid());
            _pendingByAccount[accountId.UserId] = pending;
            _ = SetPendingAsync(pending, true);
        }
        else if (args.OldMobState == MobState.Dead)
        {
            _pendingByAccount.Remove(accountId.UserId);
            _ = SetPendingAsync(
                new PendingDeath(profileId.Value, accountId.UserId, Guid.NewGuid()),
                false);
        }
    }

    private async Task SetPendingAsync(PendingDeath pending, bool value)
    {
        var lifecycleLock = GetLifecycleLock(pending.AccountId);
        await lifecycleLock.WaitAsync();
        try
        {
            // A newer death/revival event supersedes an older asynchronous database write.
            if (value &&
                (!_pendingByAccount.TryGetValue(pending.AccountId, out var current) ||
                 current.RequestId != pending.RequestId))
                return;
            if (!value && _pendingByAccount.ContainsKey(pending.AccountId))
                return;

            var result = await _database.SetNCPermadeathPendingAsync(
                pending.ProfileId,
                pending.AccountId,
                _ticker.RoundId,
                value,
                value ? "mob-state-dead" : "mob-revived",
                pending.RequestId);
            if (result.Success && TryGetMindState(pending.ProfileId, out var state))
                state.LifecycleStatus = (byte) (value
                    ? NCCharacterLifecycleStatus.PermadeathPending
                    : NCCharacterLifecycleStatus.Alive);
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    private async void OnRoundEnd(RoundEndMessageEvent args)
    {
        if (!_configuration.GetCVar(NCCVars.PermadeathEnabled))
            return;

        var locks = _pendingByAccount.Keys.Select(GetLifecycleLock).ToArray();
        foreach (var lifecycleLock in locks)
            await lifecycleLock.WaitAsync();
        try
        {
            await _database.FinalizeAllNCPermadeathsAsync(_ticker.RoundId);
        }
        finally
        {
            foreach (var lifecycleLock in locks)
                lifecycleLock.Release();
        }
    }

    private async void OnJoinedLobby(PlayerJoinedLobbyEvent args)
    {
        if (!_configuration.GetCVar(NCCVars.PermadeathEnabled) ||
            !_pendingByAccount.Remove(args.PlayerSession.UserId.UserId, out var pending))
            return;

        var lifecycleLock = GetLifecycleLock(pending.AccountId);
        await lifecycleLock.WaitAsync();
        try
        {
            await _database.FinalizeNCPermadeathForAccountAsync(pending.AccountId, _ticker.RoundId);
            await _preferences.RefreshAfterNCPermadeathAsync(args.PlayerSession, pending.ProfileId);
        }
        finally
        {
            lifecycleLock.Release();
            _lifecycleLocks.Remove(pending.AccountId);
        }
    }

    private bool TryGetMindState(int profileId, out CharacterPersistentStateComponent state)
    {
        var query = EntityQueryEnumerator<CharacterPersistentStateComponent>();
        while (query.MoveNext(out _, out var candidate))
        {
            if (candidate.ProfileId.Value != profileId)
                continue;
            state = candidate;
            return true;
        }

        state = default!;
        return false;
    }

    private SemaphoreSlim GetLifecycleLock(Guid accountId)
    {
        if (_lifecycleLocks.TryGetValue(accountId, out var lifecycleLock))
            return lifecycleLock;

        lifecycleLock = new SemaphoreSlim(1, 1);
        _lifecycleLocks[accountId] = lifecycleLock;
        return lifecycleLock;
    }

    private readonly record struct PendingDeath(int ProfileId, Guid AccountId, Guid RequestId);
}
