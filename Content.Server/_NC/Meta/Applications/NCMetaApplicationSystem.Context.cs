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
