// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server.Preferences.Managers;
using Content.Server._NC.Identity;
using Content.Shared._NC.CCVar;
using Content.Shared._NC.Identity;
using Content.Shared._NC.Persistence.Components;
using Content.Shared.GameTicking;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Configuration;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;

namespace Content.Server._NC.Persistence;

/// <summary>
/// Records only explicitly confirmed permanent deaths and delays destructive cleanup until
/// round end or lobby exit, leaving the ordinary revival window intact.
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

        // Ordinary death never declares permadeath. A successful revival cancels an explicit
        // declaration made earlier in the same round.
        if (args.OldMobState == MobState.Dead &&
            _pendingByAccount.Remove(accountId.UserId, out var pending))
        {
            _ = SetPendingAsync(pending with
            {
                RequestId = Guid.NewGuid(),
                ActorAccountId = accountId.UserId,
                ActorProfileId = profileId.Value,
                Reason = "mob-revived",
            }, false);
        }
    }

    /// <summary>
    /// Explicit confirmation entry point for an authorized game process or administrator.
    /// It rejects living targets and does not delete the profile immediately.
    /// </summary>
    public async Task<NCLifecycleResult> ConfirmPermadeathAsync(
        EntityUid target,
        Guid actorAccountId,
        ProfileId? actorProfileId,
        string reason)
    {
        if (!_configuration.GetCVar(NCCVars.PermadeathEnabled))
            return new NCLifecycleResult(false, "nc-permadeath-error-disabled");
        if (string.IsNullOrWhiteSpace(reason))
            return new NCLifecycleResult(false, "nc-permadeath-error-reason-required");
        if (!_identity.TryGetIdentity(target, out var targetProfileId, out var targetAccountId))
            return new NCLifecycleResult(false, "nc-permadeath-error-profile-not-found");
        if (!TryComp<MobStateComponent>(target, out var mobState) ||
            mobState.CurrentState != MobState.Dead)
            return new NCLifecycleResult(false, "nc-permadeath-error-target-not-dead");

        var pending = new PendingDeath(
            targetProfileId.Value,
            targetAccountId.UserId,
            actorAccountId,
            actorProfileId?.Value,
            reason.Trim(),
            Guid.NewGuid());
        _pendingByAccount[targetAccountId.UserId] = pending;
        return await SetPendingAsync(pending, true);
    }

    private async Task<NCLifecycleResult> SetPendingAsync(PendingDeath pending, bool value)
    {
        var lifecycleLock = GetLifecycleLock(pending.AccountId);
        await lifecycleLock.WaitAsync();
        try
        {
            // A newer death/revival event supersedes an older asynchronous database write.
            if (value &&
                (!_pendingByAccount.TryGetValue(pending.AccountId, out var current) ||
                 current.RequestId != pending.RequestId))
                return new NCLifecycleResult(false, "nc-permadeath-error-superseded");
            if (!value && _pendingByAccount.ContainsKey(pending.AccountId))
                return new NCLifecycleResult(false, "nc-permadeath-error-superseded");

            var result = await _database.SetNCPermadeathPendingAsync(
                pending.ProfileId,
                pending.AccountId,
                pending.ActorAccountId,
                pending.ActorProfileId,
                _ticker.RoundId,
                value,
                pending.Reason,
                pending.RequestId);
            if (value && !result.Success &&
                _pendingByAccount.TryGetValue(pending.AccountId, out var failedCurrent) &&
                failedCurrent.RequestId == pending.RequestId)
            {
                _pendingByAccount.Remove(pending.AccountId);
            }
            if (result.Success && TryGetMindState(pending.ProfileId, out var state))
                state.LifecycleStatus = (byte) (value
                    ? NCCharacterLifecycleStatus.PermadeathPending
                    : NCCharacterLifecycleStatus.Alive);
            return result;
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
        if (!_configuration.GetCVar(NCCVars.PermadeathEnabled))
            return;

        var accountId = args.PlayerSession.UserId.UserId;
        var hasMemoryEntry = _pendingByAccount.Remove(accountId, out var pending);
        var lifecycleLock = GetLifecycleLock(accountId);
        await lifecycleLock.WaitAsync();
        try
        {
            // The database is the source of truth. This also finalizes pending deaths after
            // a server restart, when the in-memory declaration cache is necessarily empty.
            var result = await _database.FinalizeNCPermadeathForAccountAsync(accountId, _ticker.RoundId);
            if (result.Success && result.FinalizedProfiles > 0)
            {
                await _preferences.RefreshAfterNCPermadeathAsync(
                    args.PlayerSession,
                    hasMemoryEntry ? pending.ProfileId : null);
            }
        }
        finally
        {
            lifecycleLock.Release();
            _lifecycleLocks.Remove(accountId);
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

    private readonly record struct PendingDeath(
        int ProfileId,
        Guid AccountId,
        Guid ActorAccountId,
        int? ActorProfileId,
        string Reason,
        Guid RequestId);
}
