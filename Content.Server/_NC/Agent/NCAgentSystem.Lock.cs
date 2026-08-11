// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Security.Cryptography;
using Content.Shared._NC.Agent.Components;
using Content.Shared._NC.Agent.UI;
using Content.Shared.PDA;

namespace Content.Server._NC.Agent;

/// <summary>
/// Owns the physical Agent PIN and per-user unlock sessions.
/// PIN secrets remain server-only; the client receives only presentation state.
/// </summary>
public sealed partial class NCAgentSystem
{
    private const int PinHashSize = 32;
    private const int PinSaltSize = 16;

    private readonly Dictionary<(EntityUid Agent, EntityUid User), TimeSpan> _unlockSessions = [];
    private TimeSpan _nextUnlockSessionCheck;

    private void InitializeLocks()
    {
        SubscribeLocalEvent<NCAgentComponent, NCAgentUnlockMessage>(OnUnlock);
        SubscribeLocalEvent<NCAgentComponent, NCAgentSetPinMessage>(OnSetPin);
        SubscribeLocalEvent<NCAgentComponent, NCAgentDisablePinMessage>(OnDisablePin);
        SubscribeLocalEvent<NCAgentComponent, NCAgentLockMessage>(OnLock);
        SubscribeLocalEvent<NCAgentComponent, NCAgentDeviceAccessAttemptEvent>(OnDeviceAccessAttempt);
    }

    private async void OnUnlock(EntityUid uid, NCAgentComponent component, NCAgentUnlockMessage args)
    {
        if (!TryGetAnyView(uid, args.Actor, out var user, out var view) ||
            !TryComp<NCAgentLockComponent>(uid, out var agentLock))
            return;

        if (!TryVerifyPin(uid, args.Pin, agentLock, out var notice))
        {
            SendLockState(uid, user, notice);
            return;
        }

        MarkUnlocked(uid, user, view);
        SendLockState(uid, user, "ACCESS GRANTED");
        ResendPendingNotificationsAfterUnlock(view.Owner, user);
        await InitializeAuthenticatedViewAsync(uid, user, view.Owner);
    }

    private void OnSetPin(EntityUid uid, NCAgentComponent component, NCAgentSetPinMessage args)
    {
        if (!TryGetAnyView(uid, args.Actor, out var user, out var view) ||
            !IsValidPin(args.NewPin))
        {
            if (args.Actor is { Valid: true } actor)
                SendLockState(uid, actor, PinRequirementsNotice());
            return;
        }

        if (TryComp<NCAgentLockComponent>(uid, out var existing) &&
            !TryVerifyPin(uid, args.CurrentPin, existing, out var notice))
        {
            SendLockState(uid, user, notice);
            return;
        }

        var agentLock = EnsureComp<NCAgentLockComponent>(uid);
        agentLock.PinSalt = RandomNumberGenerator.GetBytes(PinSaltSize);
        agentLock.PinHash = HashPin(args.NewPin, agentLock.PinSalt);
        agentLock.FailedAttempts = 0;
        agentLock.LockoutEnd = TimeSpan.Zero;
        MarkUnlocked(uid, user, view);
        SendLockState(uid, user, existing == null ? "PIN ENABLED" : "PIN CHANGED");
    }

    private void OnDisablePin(EntityUid uid, NCAgentComponent component, NCAgentDisablePinMessage args)
    {
        if (!TryGetAnyView(uid, args.Actor, out var user, out var view) ||
            !TryComp<NCAgentLockComponent>(uid, out var agentLock))
            return;

        if (!TryVerifyPin(uid, args.CurrentPin, agentLock, out var notice))
        {
            SendLockState(uid, user, notice);
            return;
        }

        RemComp<NCAgentLockComponent>(uid);
        ClearAgentUnlockSessions(uid);
        view.Authenticated = true;
        SendLockState(uid, user, "PIN DISABLED");
        ResendPendingNotificationsAfterUnlock(view.Owner, user);
    }

    private void OnLock(EntityUid uid, NCAgentComponent component, NCAgentLockMessage args)
    {
        if (!TryGetAnyView(uid, args.Actor, out var user, out var view))
            return;

        if (!HasComp<NCAgentLockComponent>(uid))
        {
            SendLockState(uid, user, "CONFIGURE A PIN BEFORE LOCKING");
            return;
        }

        RevokeAgentAccess(uid, endCall: true);
        SendLockStates(uid, "DEVICE LOCKED");
    }

    private void OnDeviceAccessAttempt(Entity<NCAgentComponent> ent, ref NCAgentDeviceAccessAttemptEvent args)
    {
        if (!_views.TryGetValue((ent.Owner, args.User), out var view) ||
            !IsViewAuthenticated(ent.Owner, args.User, view))
            args.Cancelled = true;
    }

    private bool TryVerifyPin(
        EntityUid agent,
        string pin,
        NCAgentLockComponent agentLock,
        out string notice)
    {
        var now = _gameTiming.CurTime;
        if (agentLock.LockoutEnd > now)
        {
            notice = "SECURITY LOCKOUT ACTIVE";
            return false;
        }

        if (!IsValidPin(pin) ||
            agentLock.PinHash.Length != PinHashSize ||
            agentLock.PinSalt.Length != PinSaltSize ||
            !CryptographicOperations.FixedTimeEquals(HashPin(pin, agentLock.PinSalt), agentLock.PinHash))
        {
            agentLock.FailedAttempts++;
            if (agentLock.FailedAttempts >= Math.Max(1, Configuration.PinMaximumAttempts))
            {
                agentLock.FailedAttempts = 0;
                agentLock.LockoutEnd = now + Configuration.PinLockoutDuration;
                RevokeAgentAccess(agent, endCall: true);
                notice = "TOO MANY ATTEMPTS // DEVICE TEMPORARILY LOCKED";
                return false;
            }

            notice = "INVALID PIN";
            return false;
        }

        agentLock.FailedAttempts = 0;
        agentLock.LockoutEnd = TimeSpan.Zero;
        notice = string.Empty;
        return true;
    }

    private byte[] HashPin(string pin, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            pin,
            salt,
            Math.Max(1, Configuration.PinHashIterations),
            HashAlgorithmName.SHA256,
            PinHashSize);
    }

    private bool IsValidPin(string pin)
    {
        return pin.Length >= Configuration.MinimumPinLength &&
               pin.Length <= Configuration.MaximumPinLength &&
               pin.All(char.IsAsciiDigit);
    }

    private string PinRequirementsNotice()
    {
        return $"PIN MUST CONTAIN {Configuration.MinimumPinLength}-{Configuration.MaximumPinLength} DIGITS";
    }

    private bool TryResumeUnlock(EntityUid agent, EntityUid user)
    {
        if (!HasComp<NCAgentLockComponent>(agent))
            return true;

        return HasActiveUnlockSession(agent, user);
    }

    private void MarkUnlocked(EntityUid agent, EntityUid user, AgentView view)
    {
        // One physical Agent has one holder session; unlocking revokes stale viewers immediately.
        ClearAgentUnlockSessions(agent);
        foreach (var entry in _views.Where(entry => entry.Key.Agent == agent))
            entry.Value.Authenticated = entry.Key.User == user;
        _unlockSessions[(agent, user)] = _gameTiming.CurTime + Configuration.PinUnlockSessionDuration;
        view.Authenticated = true;
    }

    private bool HasActiveUnlockSession(EntityUid agent, EntityUid user)
    {
        if (!HasComp<NCAgentLockComponent>(agent))
            return true;

        return _unlockSessions.TryGetValue((agent, user), out var expires) && expires > _gameTiming.CurTime;
    }

    private bool IsViewAuthenticated(EntityUid agent, EntityUid user, AgentView view)
    {
        if (!HasComp<NCAgentLockComponent>(agent))
        {
            view.Authenticated = true;
            return true;
        }

        if (view.Authenticated &&
            _unlockSessions.TryGetValue((agent, user), out var expires) &&
            expires > _gameTiming.CurTime)
            return true;

        view.Authenticated = false;
        _unlockSessions.Remove((agent, user));
        return false;
    }

    private void UpdateAgentLocks()
    {
        var now = _gameTiming.CurTime;
        if (now < _nextUnlockSessionCheck)
            return;

        _nextUnlockSessionCheck = now + TimeSpan.FromSeconds(1);
        foreach (var (key, view) in _views.ToArray())
        {
            if (!view.Authenticated || IsViewAuthenticated(key.Agent, key.User, view))
                continue;

            EndCallForAgent(key.Agent);
            SendLockState(key.Agent, key.User, "SESSION EXPIRED");
        }

        foreach (var (key, expires) in _unlockSessions.ToArray())
        {
            if (expires <= now)
                _unlockSessions.Remove(key);
        }
    }

    private void SendLockStates(EntityUid agent, string notice)
    {
        foreach (var key in _views.Keys.Where(value => value.Agent == agent).ToArray())
            SendLockState(key.Agent, key.User, notice);
    }

    private void SendLockState(EntityUid agent, EntityUid user, string notice = "")
    {
        if (!_views.TryGetValue((agent, user), out var view))
            return;

        var configured = TryComp<NCAgentLockComponent>(agent, out var agentLock);
        var retryAfter = agentLock is not null && agentLock.LockoutEnd > _gameTiming.CurTime
            ? agentLock.LockoutEnd - _gameTiming.CurTime
            : TimeSpan.Zero;
        var locked = agentLock is not null && !IsViewAuthenticated(agent, user, view);
        _ui.ServerSendUiMessage(agent, PdaUiKey.Key, new NCAgentLockUpdateMessage(
            configured,
            locked,
            agentLock?.FailedAttempts ?? 0,
            Configuration.MinimumPinLength,
            Configuration.MaximumPinLength,
            retryAfter,
            notice), user);
    }

    private void ClearAgentUnlockSessions(EntityUid agent)
    {
        foreach (var key in _unlockSessions.Keys.Where(value => value.Agent == agent).ToArray())
            _unlockSessions.Remove(key);
    }

    private void RevokeAgentAccess(EntityUid agent, bool endCall)
    {
        ClearAgentUnlockSessions(agent);
        foreach (var entry in _views.Where(entry => entry.Key.Agent == agent))
            entry.Value.Authenticated = false;

        if (endCall)
            EndCallForAgent(agent);
    }

    private void ClearAgentLocks()
    {
        _unlockSessions.Clear();
        _nextUnlockSessionCheck = TimeSpan.Zero;
    }
}
