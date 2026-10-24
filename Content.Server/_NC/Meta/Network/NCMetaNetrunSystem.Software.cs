// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Threading;
using Content.Server._NC.Meta.Commerce;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.Network;

namespace Content.Server._NC.Meta.Network;

public sealed partial class NCMetaNetrunSystem
{
    [Dependency] private NCMetaMarketplaceSystem _software = default!;

    public bool TryBindSoftwareNode(EntityUid node, EntityUid source, EntityUid actor, Guid installationId = default)
    {
        if (!_devices.CanManage(node, actor) || !_devices.CanManage(source, actor, requireRange: false) ||
            !TryComp<NCMetaNetNodeComponent>(node, out var component) || component.Kind != NCMetaNetNodeKind.Data ||
            !HasComp<NCMetaHostComponent>(source) || !_network.CanRoute(node, source) || !_devices.TryGetEntry(source, out var device) ||
            !_nodes.TryGetValue((component.ArchitectureId, component.NodeId), out var entry) || entry.Entity != node) return false;
        return TrySaveControlBinding(entry, device, actor, installationId == Guid.Empty ? "software" : "software/" + installationId.ToString("D"));
    }

    private bool BeginCopySoftware(Session session, Pending pending, NodeEntry node)
    {
        if (_pendingFileOperations >= 8 || node.Component.Kind != NCMetaNetNodeKind.Data ||
            node.Component.DataPath != "software" && !node.Component.DataPath.StartsWith("software/", StringComparison.Ordinal) ||
            !_devices.TryResolve(node.Component.ControlledDeviceId, out var source) || source.Owner != session.Security.Owner ||
            !HasComp<NCMetaHostComponent>(source.Entity) || !HasComp<NCMetaStorageComponent>(session.Deck) ||
            !_network.CanRoute(session.AccessPoint, source.Entity)) return false;
        Interlocked.Increment(ref _pendingFileOperations);
        session.Pending = pending with { IoState = 1, Due = _now + 5000 };
        _actions.Enqueue((session.Id, pending.Id), _now + 5000);
        _ = SelectCopyAsync(session.Id, pending.Id, source.Id, source.Owner, source.DeviceEpoch, source.AuthorityEpoch, node.Component.DataPath);
        return true;
    }

    private async Task SelectCopyAsync(Guid sessionId, Guid actionId, string sourceId, string sourceOwner,
        ulong sourceEpoch, ulong sourceAuthority, string path)
    {
        NCMetaInstallation? installation = null;
        try
        {
            var values = await _persistence.ReadInstallationsAsync(sourceId, sourceOwner);
            if (path == "software") { if (values.Length == 1) installation = values[0]; }
            else if (Guid.TryParseExact(path[9..], "D", out var selected)) installation = values.SingleOrDefault(value => value.Id == selected);
        }
        catch (Exception) { }
        var selectedInstallation = installation;
        _completed.Enqueue(() =>
        {
            if (!_sessions.TryGetValue(sessionId, out var session) || session.Pending is not { } pending || pending.Id != actionId)
            { Interlocked.Decrement(ref _pendingFileOperations); return; }
            if (selectedInstallation == null || pending.IoState != 1 || pending.Due <= _now || !Current(session) ||
                !TryNode(session, pending.Node, out var node) || node.Generation != pending.NodeGeneration ||
                !_devices.TryGetEntry(session.Deck, out var deck))
            { Interlocked.Decrement(ref _pendingFileOperations); CompleteError(session, pending, "PermissionRevoked"); return; }
            var admission = new NCMetaSoftwareCopyAdmission(actionId, sessionId, session.Owner, deck.Id, deck.DeviceEpoch,
                deck.AuthorityEpoch, sourceId, sourceOwner, sourceEpoch, sourceAuthority, session.Architecture, pending.Node,
                pending.NodeGeneration, selectedInstallation.Id);
            if (!CurrentCopy(admission, path))
            { Interlocked.Decrement(ref _pendingFileOperations); CompleteError(session, pending, "PermissionRevoked"); return; }
            session.Pending = pending with { IoState = 2 };
            _ = CommitCopyAsync(admission, path);
        });
    }

    private bool CurrentCopy(NCMetaSoftwareCopyAdmission admission, string path) =>
        _sessions.TryGetValue(admission.SessionId, out var session) && session.Owner == admission.Owner && Current(session) &&
        session.Pending is { } pending && pending.Id == admission.ActionId && pending.Due > _now &&
        TryNode(session, admission.Node, out var node) && node.Generation == admission.NodeGeneration &&
        node.Component.ControlledDeviceId == admission.SourceId && node.Component.DataPath == path &&
        session.Grants.GetValueOrDefault(admission.Node) > _now && _devices.TryResolve(admission.SourceId, out var source) &&
        source.Owner == admission.SourceOwner && source.DeviceEpoch == admission.SourceEpoch && source.AuthorityEpoch == admission.SourceAuthority &&
        _devices.TryGetEntry(session.Deck, out var deck) && deck.Id == admission.DeckId && deck.DeviceEpoch == admission.DeckEpoch &&
        deck.AuthorityEpoch == admission.DeckAuthority && _network.CanRoute(session.AccessPoint, source.Entity);

    private async Task CommitCopyAsync(NCMetaSoftwareCopyAdmission admission, string path)
    {
        bool success;
        try
        {
            var authority = await CaptureCopyAuthorityAsync(admission, path);
            success = authority != null && (await _software.TryCopySoftwareAsync(admission, () => CurrentCopy(admission, path), authority)).Success;
        }
        catch (Exception exception) { Log.Error($"NET software copy failed: {exception.GetType().Name}."); success = false; }
        Interlocked.Decrement(ref _pendingFileOperations);
        _completed.Enqueue(() =>
        {
            if (!_sessions.TryGetValue(admission.SessionId, out var session) || session.Pending?.Id != admission.ActionId) return;
            CompleteAction(session, success ? "nc-meta-net-software-copied" : "nc-meta-net-action-rejected");
        });
    }
}
