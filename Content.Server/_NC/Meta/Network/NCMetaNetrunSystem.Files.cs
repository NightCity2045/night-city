// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Content.Meta.Hosting;
using Content.Shared._NC.Meta;
using Content.Server._NC.Meta.Persistence;

namespace Content.Server._NC.Meta.Network;

public sealed partial class NCMetaNetrunSystem
{
    private sealed record TextCopyAdmission(Guid SessionId, Guid ActionId, string Owner, string DeckId,
        ulong DeckEpoch, ulong DeckAuthority, string SourceId, string SourceOwner, ulong SourceEpoch,
        ulong SourceAuthority, string Node, ulong NodeGeneration, string SourcePath);

    private bool BeginRead(Session session, Pending pending, NodeEntry node)
    {
        if (_pendingFileOperations >= 8 || node.Component.Kind != Content.Shared._NC.Meta.Network.NCMetaNetNodeKind.Data || !MetaPaths.IsValid(node.Component.DataPath) || !_devices.TryResolve(node.Component.ControlledDeviceId, out var source) || source.Owner != session.Security.Owner || !TryComp<NCMetaStorageComponent>(source.Entity, out _) || !TryComp<NCMetaStorageComponent>(session.Deck, out _) || !_devices.TryGetEntry(session.Deck, out var deck) || !_network.CanRoute(session.AccessPoint, source.Entity)) return false;
        Interlocked.Increment(ref _pendingFileOperations);
        session.Pending = pending with { IoState = 1, Due = _now + 5000 };
        _actions.Enqueue((session.Id, pending.Id), _now + 5000);
        var sourceVolume = NCMetaPersistenceSystem.StableGuid("volume:" + source.Id);
        var admission = new TextCopyAdmission(session.Id, pending.Id, session.Owner, deck.Id, deck.DeviceEpoch,
            deck.AuthorityEpoch, source.Id, source.Owner, source.DeviceEpoch, source.AuthorityEpoch,
            pending.Node, pending.NodeGeneration, node.Component.DataPath);
        _ = ReadFileAsync(admission, sourceVolume);
        return true;
    }
    private async Task ReadFileAsync(TextCopyAdmission admission, Guid volume)
    {
        string? text;
        try { text = await _persistence.ReadTextAsync(volume, admission.SourceOwner, admission.SourcePath); }
        catch (Exception) { text = null; }
        _completed.Enqueue(() =>
        {
            if (!_sessions.TryGetValue(admission.SessionId, out var session) || session.Pending is not { } pending || pending.Id != admission.ActionId || pending.IoState != 1) { Interlocked.Decrement(ref _pendingFileOperations); return; }
            if (pending.Due <= _now) { Interlocked.Decrement(ref _pendingFileOperations); CompleteError(session, pending, "Timeout"); return; }
            if (!CurrentTextCopy(admission, 1)) { Interlocked.Decrement(ref _pendingFileOperations); CompleteError(session, pending, "PermissionRevoked"); return; }
            if (text == null) { Interlocked.Decrement(ref _pendingFileOperations); CompleteError(session, pending, "NotFound"); return; }
            if (!TryComp<NCMetaStorageComponent>(session.Deck, out var storage) || !_devices.TryGetEntry(session.Deck, out var deck) || !_persistence.TryCaptureTime(out var epoch, out var time)) { Interlocked.Decrement(ref _pendingFileOperations); CompleteError(session, pending, "Disconnected"); return; }
            session.Pending = pending with { IoState = 2 };
            var targetVolume = NCMetaPersistenceSystem.StableGuid("volume:" + deck.Id);
            var filename = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(session.Architecture + ":" + pending.Node)))[..24];
            _ = WriteCopyAsync(admission, targetVolume, "net-copies/" + filename + ".txt", text, storage.CapacityBytes, epoch, time);
        });
    }

    private bool CurrentTextCopy(TextCopyAdmission admission, int ioState) =>
        _sessions.TryGetValue(admission.SessionId, out var session) && session.Owner == admission.Owner && Current(session) &&
        _persistence.TryCaptureTime(out _, out var time) && session.Pending is { } pending &&
        pending.Id == admission.ActionId && pending.Operation == Content.Shared._NC.Meta.Network.NCMetaNetOperation.Read &&
        pending.IoState == ioState && pending.Due > time.Ticks / TimeSpan.TicksPerMillisecond &&
        pending.Node == admission.Node && pending.NodeGeneration == admission.NodeGeneration &&
        TryNode(session, admission.Node, out var node) && node.Generation == admission.NodeGeneration &&
        node.Component.ControlledDeviceId == admission.SourceId && node.Component.DataPath == admission.SourcePath &&
        session.Grants.GetValueOrDefault(admission.Node) > time.Ticks / TimeSpan.TicksPerMillisecond &&
        _devices.TryResolve(admission.SourceId, out var source) && source.Owner == admission.SourceOwner &&
        source.Owner == session.Security.Owner && source.DeviceEpoch == admission.SourceEpoch && source.AuthorityEpoch == admission.SourceAuthority &&
        _devices.TryGetEntry(session.Deck, out var deck) && deck.Id == admission.DeckId && deck.DeviceEpoch == admission.DeckEpoch &&
        deck.AuthorityEpoch == admission.DeckAuthority && _network.CanRoute(session.AccessPoint, source.Entity);

    private Task<bool> CanCommitTextCopy(TextCopyAdmission admission)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Unlike the active-world completion queue, this also resolves during shutdown/recovery so mutation leases drain.
        _configurationTasks.RunOnMainThread(() =>
        {
            try { completion.SetResult(CurrentTextCopy(admission, 2)); }
            catch (Exception exception) { completion.SetException(exception); }
        });
        return completion.Task;
    }

    private async Task WriteCopyAsync(TextCopyAdmission admission, Guid volume, string path,
        string text, int capacity, Content.Shared._NC.Persistence.Time.NCSessionEpoch epoch, Content.Shared._NC.Persistence.Time.NCActiveWorldTime time)
    {
        bool success;
        try { success = (await _persistence.WriteTextAsync(admission.ActionId, volume, admission.Owner, path, text, capacity, epoch, time,
            () => CanCommitTextCopy(admission), new NCMetaFileWriteAuthorization(admission.DeckId, admission.DeckAuthority))).Success; }
        catch (Exception) { success = false; }
        Interlocked.Decrement(ref _pendingFileOperations);
        _completed.Enqueue(() =>
        {
            if (!_sessions.TryGetValue(admission.SessionId, out var session) || session.Pending?.Id != admission.ActionId) return;
            if (success) session.Preview = Truncate(text, 512);
            CompleteAction(session, success ? "nc-meta-net-copy-complete" : "nc-meta-net-copy-unknown");
        });
    }
    private static string Truncate(string text, int bytes)
    {
        var length = 0; var count = 0;
        foreach (var rune in text.EnumerateRunes()) { if (count + rune.Utf8SequenceLength > bytes) break; count += rune.Utf8SequenceLength; length += rune.Utf16SequenceLength; }
        return text[..length];
    }
}
