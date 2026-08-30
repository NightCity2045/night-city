// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.NCUI;
using Content.Client._NC.NCUI.Debug;
using Robust.Shared.GameObjects;
using System.Linq;
using System.Numerics;

namespace Content.Client._NC.NCUI.State;

/// <summary>
/// Client copy of framework-owned collaborative state. Domain presenters own their separate typed model.
/// </summary>
public sealed class NcUiClientSessionState
{
    public NcUiSessionId Session { get; private set; } = NcUiSessionId.Invalid;
    public NcUiRevision Revision { get; private set; }
    public NcUiPermission LocalPermissions { get; private set; }
    public NetEntity LocalParticipant { get; private set; } = NetEntity.Invalid;
    public Dictionary<NetEntity, NcUiParticipantSnapshot> Participants { get; } = [];
    public Dictionary<NcUiObjectId, NcUiLeaseSnapshot> Leases { get; } = [];
    public Dictionary<NetEntity, NcUiObjectId[]> RemoteSelections { get; } = [];
    public Dictionary<NcUiOperationId, NcUiRemoteOperationSnapshot> Operations { get; } = [];

    public event Action? Changed;
    public event Action<NcUiRemoteOperationSnapshot, Vector2[]>? RemoteOperationUpdated;

    public void ApplySnapshot(NcUiSnapshotMessage snapshot)
    {
        Session = snapshot.Session;
        Revision = snapshot.Revision;
        LocalPermissions = snapshot.Permissions;
        LocalParticipant = snapshot.LocalParticipant;
        Participants.Clear();
        Leases.Clear();
        RemoteSelections.Clear();
        Operations.Clear();
        foreach (var participant in snapshot.Participants)
            Participants[participant.Participant] = participant;
        foreach (var lease in snapshot.Leases)
            Leases[lease.Object] = lease;
        foreach (var selection in snapshot.RemoteSelections)
            RemoteSelections[selection.Participant] = selection.Objects;
        foreach (var operation in snapshot.Operations)
            Operations[operation.Operation] = operation;
        Changed?.Invoke();
    }

    public void ApplySessionPatch(NcUiSessionPatchMessage patch)
    {
        foreach (var delta in patch.Deltas)
            ApplyDelta(delta);
        Revision = patch.Revision;
        Changed?.Invoke();
    }

    public void Clear()
    {
        Session = NcUiSessionId.Invalid;
        Revision = default;
        LocalPermissions = NcUiPermission.None;
        LocalParticipant = NetEntity.Invalid;
        Participants.Clear();
        Leases.Clear();
        RemoteSelections.Clear();
        Operations.Clear();
        Changed?.Invoke();
    }

    private void ApplyDelta(NcUiSessionDelta delta)
    {
        switch (delta.Kind)
        {
            case NcUiSessionDeltaKind.ParticipantJoined:
            case NcUiSessionDeltaKind.PermissionsChanged:
                Participants[delta.Participant] = new NcUiParticipantSnapshot(
                    delta.Participant, delta.Permissions, true);
                if (delta.Participant == LocalParticipant)
                    LocalPermissions = delta.Permissions;
                break;
            case NcUiSessionDeltaKind.ParticipantLeft:
                Participants.Remove(delta.Participant);
                RemoteSelections.Remove(delta.Participant);
                var leased = Leases.Where(entry => entry.Value.Owner == delta.Participant)
                    .Select(entry => entry.Key).ToArray();
                foreach (var objectId in leased)
                    Leases.Remove(objectId);
                var operations = Operations.Where(entry => entry.Value.Participant == delta.Participant)
                    .Select(entry => entry.Key).ToArray();
                foreach (var operation in operations)
                    Operations.Remove(operation);
                break;
            case NcUiSessionDeltaKind.LeaseAcquired:
                Leases[delta.Object] = new NcUiLeaseSnapshot(
                    delta.Object, delta.Participant, delta.Operation, delta.LeaseExpiresAt);
                break;
            case NcUiSessionDeltaKind.LeaseReleased:
                Leases.Remove(delta.Object);
                break;
            case NcUiSessionDeltaKind.RemoteSelectionChanged:
                RemoteSelections[delta.Participant] = delta.Selection;
                break;
            case NcUiSessionDeltaKind.OperationStarted:
            case NcUiSessionDeltaKind.OperationUpdated:
                var remoteOperation = new NcUiRemoteOperationSnapshot(
                    delta.Participant, delta.Operation, delta.OperationPoint, delta.OperationTotalLength);
                Operations[delta.Operation] = remoteOperation;
                if (delta.Kind == NcUiSessionDeltaKind.OperationUpdated && delta.OperationPoints.Length != 0)
                {
                    RemoteOperationUpdated?.Invoke(remoteOperation, NcUiPointCompression.Decompress(
                        delta.OperationPoints, delta.OperationOrigin, delta.OperationPointQuantum));
                }
                break;
            case NcUiSessionDeltaKind.OperationCompleted:
            case NcUiSessionDeltaKind.OperationCancelled:
                Operations.Remove(delta.Operation);
                break;
        }
    }
}

/// <summary>
/// Typed BUI message bridge used by domain presenters for snapshot/patch/result/resync handling.
/// </summary>
public sealed class NcUiClientSession<TSnapshot, TPatch>
    where TSnapshot : NcUiSnapshotMessage
    where TPatch : NcUiPatchMessage
{
    private readonly Queue<DelayedInbound> _delayedInbound = [];
    private readonly HashSet<NcUiActionSequence> _pendingSequences = [];
    private readonly NcUiDebugSettings? _debug;
    private readonly NcUiMetrics? _metrics;

    public UiPatchApplier Patches { get; } = new();
    public NcUiClientSessionState SharedState { get; } = new();
    public NcUiActionSequence NextSequence { get; private set; } = NcUiActionSequence.Initial;
    public int PendingActions { get; private set; }

    public event Action<TSnapshot>? SnapshotReceived;
    public event Action<TPatch>? PatchReceived;
    public event Action<NcUiActionResultMessage>? ActionResolved;
    public event Action<NcUiResyncRequestMessage>? ResyncRequested;

    public NcUiClientSession(NcUiDebugSettings? debug = null, NcUiMetrics? metrics = null)
    {
        _debug = debug;
        _metrics = metrics;
        Patches.ResyncRequired += OnResyncRequired;
    }

    public NcUiActionSequence AllocateSequence()
    {
        var sequence = NextSequence;
        NextSequence = NextSequence.Next();
        _pendingSequences.Add(sequence);
        PendingActions = _pendingSequences.Count;
        return sequence;
    }

    public bool Handle(BoundUserInterfaceMessage message, int encodedBytes = 0) =>
        Handle(message, TimeSpan.Zero, encodedBytes);

    public bool Handle(BoundUserInterfaceMessage message, TimeSpan now, int encodedBytes = 0)
    {
        RecordInbound(encodedBytes);
        if (_debug?.DropNextPatch == true && message is NcUiPatchMessage)
        {
            _debug.DropNextPatch = false;
            return true;
        }
        if (_debug?.DelayServerResponse == true)
        {
            _delayedInbound.Enqueue(new DelayedInbound(now + _debug.ServerResponseDelay, message));
            return true;
        }
        return HandleNow(message);
    }

    public void Update(TimeSpan now)
    {
        while (_delayedInbound.TryPeek(out var delayed) && delayed.DeliverAt <= now)
        {
            _delayedInbound.Dequeue();
            HandleNow(delayed.Message);
        }
    }

    public bool TryInjectRejectedAction(NcUiActionMessage action)
    {
        if (_debug?.RejectNextAction != true)
            return false;
        _debug.RejectNextAction = false;
        HandleNow(new NcUiActionResultMessage(
            action.Session,
            action.Sequence,
            Patches.Revision,
            NcUiActionResult.RejectedConflict));
        return true;
    }

    public void RecordOutbound(int encodedBytes)
    {
        if (_debug != null && !_debug.Network)
            return;
        if (_metrics != null && encodedBytes > 0)
            _metrics.NetworkBytesOut += encodedBytes;
    }

    public void RequestResync() => OnResyncRequired(Patches.Revision);

    private bool HandleNow(BoundUserInterfaceMessage message)
    {
        var handled = true;
        switch (message)
        {
            case TSnapshot snapshot:
                Patches.ApplySnapshot(snapshot, incoming =>
                {
                    SharedState.ApplySnapshot(incoming);
                    SnapshotReceived?.Invoke((TSnapshot) incoming);
                });
                break;
            case NcUiSessionPatchMessage sessionPatch:
                handled = Patches.ApplyPatch(sessionPatch, incoming =>
                {
                    SharedState.ApplySessionPatch((NcUiSessionPatchMessage) incoming);
                }) == UiPatchApplyResult.Applied;
                break;
            case TPatch patch:
                handled = Patches.ApplyPatch(patch, incoming => PatchReceived?.Invoke((TPatch) incoming)) ==
                    UiPatchApplyResult.Applied;
                break;
            case NcUiActionResultMessage result:
                if (result.Session != Patches.Session || !_pendingSequences.Remove(result.Sequence))
                    return false;
                PendingActions = _pendingSequences.Count;
                ActionResolved?.Invoke(result);
                break;
            default:
                handled = false;
                break;
        }
        if (_metrics != null)
        {
            _metrics.Session = Patches.Session.Value;
            _metrics.Revision = Patches.Revision.Value;
            _metrics.PatchApplyMilliseconds = Patches.LastApplyMilliseconds;
            _metrics.PendingActions = PendingActions;
        }
        return handled;
    }

    public void Reset()
    {
        Patches.Reset();
        SharedState.Clear();
        NextSequence = NcUiActionSequence.Initial;
        PendingActions = 0;
        _pendingSequences.Clear();
        _delayedInbound.Clear();
        if (_metrics != null)
        {
            _metrics.Session = 0;
            _metrics.Revision = 0;
            _metrics.PendingActions = 0;
        }
    }

    private void OnResyncRequired(NcUiRevision revision)
    {
        var request = new NcUiResyncRequestMessage(Patches.Session, revision, AllocateSequence());
        ResyncRequested?.Invoke(request);
    }

    private void RecordInbound(int encodedBytes)
    {
        if (_debug != null && !_debug.Network)
            return;
        if (_metrics != null && encodedBytes > 0)
            _metrics.NetworkBytesIn += encodedBytes;
    }

    private readonly record struct DelayedInbound(TimeSpan DeliverAt, BoundUserInterfaceMessage Message);
}
