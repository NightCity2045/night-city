// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server._NC.Meta.Runtime;

namespace Content.Server._NC.Meta.Applications;

public sealed partial class NCMetaApplicationSystem
{
    // Retains only a singleton and scalar identity; a queued packet never owns Session/Registration/Artifact.
    private sealed class AppDeferredAuthority(NCMetaApplicationSystem system, Guid sessionId,
        string action, Guid requestId) : INCMetaDeferredInvocationAuthority
    {
        public bool IsCurrent() => system._sessions.TryGetValue(sessionId, out var session) &&
            session.Receipts.ContainsKey(requestId) && system.CanAct(session, action);
    }
}
