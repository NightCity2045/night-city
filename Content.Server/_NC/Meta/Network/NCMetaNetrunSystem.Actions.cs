// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Meta.Network;

namespace Content.Server._NC.Meta.Network;

public sealed partial class NCMetaNetrunSystem
{
    /// <summary>Current native operator view. Returns only this physical session's known, permitted nodes.</summary>
    public bool TryGetState(EntityUid deck, EntityUid actor, out NCMetaNetState state)
    {
        state = null!;
        if (!_devices.CanManage(deck, actor) || !HasComp<NCMetaCyberdeckComponent>(deck)) return false;
        state = BuildState(deck, actor, "nc-meta-net-disconnected");
        return true;
    }

    /// <summary>The BUI and trusted native callers use the same session/revision/budget/action admission.</summary>
    public bool TryExecuteAction(EntityUid deck, EntityUid actor, NCMetaNetOperation operation,
        Guid sessionId, long revision, string nodeId = "") => _requests.TryConsume(_now) &&
        _devices.CanManage(deck, actor) && TryExecuteActionCore(deck, actor, operation, sessionId, revision, nodeId);

    private bool TryExecuteActionCore(EntityUid deck, EntityUid actor, NCMetaNetOperation operation,
        Guid sessionId, long revision, string nodeId)
    {
        if (!_deckSessions.TryGetValue(deck, out var id) || !_sessions.TryGetValue(id, out var session) ||
            session.Actor != actor || session.Id != sessionId || session.Revision != revision)
        { Publish(deck, actor); return false; }
        if (operation == NCMetaNetOperation.Disconnect) { Disconnect(session, "nc-meta-net-disconnected"); return true; }
        if (operation == NCMetaNetOperation.Refresh) { Publish(deck, actor); return true; }
        if (operation == NCMetaNetOperation.Defend && !_devices.CanManage(session.AccessPoint, actor, requireRange: false))
        { session.Status = "nc-meta-net-action-rejected"; Publish(deck, actor); return false; }
        if (!Enum.IsDefined(operation) || nodeId == null || nodeId.Length > 128 ||
            !TryBegin(session, operation, nodeId, null, 0, 0, out _))
        { session.Status = "nc-meta-net-action-rejected"; Publish(deck, actor); return false; }
        return true;
    }

    private NCMetaNetState BuildState(EntityUid deck, EntityUid actor, string disconnected)
    {
        if (_nativeAccepting && _deckSessions.TryGetValue(deck, out var id) && _sessions.TryGetValue(id, out var session) &&
            session.Actor == actor && Current(session))
        {
            var nodes = session.Known.Take(64).Select(nodeId => TryNode(session, nodeId, out var node)
                ? new NCMetaNetNodeView(nodeId, Loc.GetString(node.Component.Label), node.Component.Kind,
                    session.Probed.Contains(nodeId), session.Grants.GetValueOrDefault(nodeId) > _now) : null)
                .Where(node => node != null).Cast<NCMetaNetNodeView>().ToArray();
            return new(session.Id, session.Revision, "nc-meta-net-attached", session.Trace,
                Math.Max(0, _prototypes.Index(Comp<NCMetaCyberdeckComponent>(deck).ToolProfile).Budget - session.Spent),
                nodes, session.Status, session.Pending != null, session.Preview);
        }
        if (_connecting.TryGetValue(deck, out var connection) && connection.Actor == actor)
            return new(Guid.Empty, 0, "nc-meta-net-connecting", 0, 0, [], "nc-meta-net-connecting", true);
        return new(Guid.Empty, 0, disconnected, 0, 0, [], disconnected, false);
    }
}
