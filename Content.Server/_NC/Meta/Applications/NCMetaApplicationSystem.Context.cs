// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Runtime;
using Content.Server._NC.Meta.Runtime;

namespace Content.Server._NC.Meta.Applications;

public sealed partial class NCMetaApplicationSystem
{
    public bool TryResolveAppActor(NCMetaRuntimeInstance runtime, MetaValue userRef, MetaValue sessionRef, out EntityUid actor)
    {
        actor = default;
        if (!runtime.Process.TryGetOpaqueToken(userRef, out var userToken) || !runtime.Process.TryGetOpaqueToken(sessionRef, out var sessionToken) ||
            !_persistence.TryCaptureTime(out var epoch, out _))
            return false;
        foreach (var session in _sessions.Values)
        {
            if (session.SessionToken != sessionToken || session.UserToken != userToken || session.UserRef != userRef || session.SessionRef != sessionRef ||
                session.Registration.Host != runtime.Host || session.Registration.RuntimeGeneration != runtime.Generation ||
                userRef.TypeId != session.Registration.Application.UserRefTypeId || sessionRef.TypeId != session.Registration.Application.SessionRefTypeId ||
                session.WorldEpoch != epoch || runtime.WorldEpoch != epoch || runtime.InvocationActor != session.User || !session.Busy || !CanUse(session) ||
                !_runtime.TryGetGeneration(runtime.Host, runtime.Owner, runtime.Generation, out var live) || !ReferenceEquals(live, runtime))
                continue;
            actor = session.User;
            return true;
        }
        return false;
    }

    /// <summary>
    /// The device a visitor browses from: the computer that holds the session's window. A visitor without a device
    /// (the agent in a pocket terminal) is anonymous and gets an empty code. Only a program that serves the session may ask.
    /// </summary>
    public bool TryGetVisitorDevice(NCMetaRuntimeInstance runtime, MetaValue sessionRef, out string deviceId)
    {
        deviceId = "";
        if (!runtime.Process.TryGetOpaqueToken(sessionRef, out var token)) return false;
        foreach (var session in _sessions.Values)
        {
            if (session.SessionToken != token || session.SessionRef != sessionRef ||
                session.Registration.Host != runtime.Host || session.Registration.RuntimeGeneration != runtime.Generation ||
                sessionRef.TypeId != session.Registration.Application.SessionRefTypeId || !CanUse(session)) continue;
            if (_devices.TryGetEntry(session.UiOwner, out var entry)) deviceId = entry.Id;
            return true;
        }
        return false;
    }

    /// <summary>The one person who has a window of this program open, so an answer that arrives later can still open a page for them.</summary>
    public bool TryGetSessionUser(NCMetaRuntimeInstance runtime, out EntityUid user)
    {
        user = default;
        var found = false;
        foreach (var session in _sessions.Values)
        {
            if (session.Registration.Host != runtime.Host || session.Registration.RuntimeGeneration != runtime.Generation || !CanUse(session)) continue;
            if (found && user != session.User) return false;
            user = session.User; found = true;
        }
        return found;
    }

    /// <summary>Returns only the currently executing, server-admitted action's idempotency identity.</summary>
    public bool TryGetAppActionId(NCMetaRuntimeInstance runtime, MetaValue userRef, MetaValue sessionRef, out Guid actionId)
    {
        actionId = Guid.Empty;
        if (!TryResolveAppActor(runtime, userRef, sessionRef, out var actor))
            return false;
        foreach (var session in _sessions.Values)
        {
            if (session.User != actor || session.UserRef != userRef || session.SessionRef != sessionRef ||
                session.Registration.Host != runtime.Host || session.Registration.RuntimeGeneration != runtime.Generation ||
                session.Action is not { Executed: false } action)
                continue;
            actionId = action.Id;
            return true;
        }
        return false;
    }

}
