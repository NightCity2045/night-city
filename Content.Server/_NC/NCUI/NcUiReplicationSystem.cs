// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.NCUI;
using Robust.Server.GameObjects;
using System.Linq;
using System.Numerics;

namespace Content.Server._NC.NCUI;

/// <summary>
/// Advances revisions, broadcasts typed patches and retains a bounded replay window for resync.
/// </summary>
public sealed partial class NcUiReplicationSystem : EntitySystem
{
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private NcUiSessionSystem _sessions = default!;

    private readonly Dictionary<NcUiSessionId, Queue<NcUiPatchMessage>> _history = [];

    public override void Initialize()
    {
        base.Initialize();
        _sessions.SessionClosed += OnSessionClosed;
        _sessions.ParticipantJoined += OnParticipantJoined;
        _sessions.ParticipantLeft += OnParticipantLeft;
        _sessions.PermissionsChanged += OnPermissionsChanged;
        _sessions.LeaseAcquired += OnLeaseAcquired;
        _sessions.LeaseReleased += OnLeaseReleased;
        _sessions.LeaseExpired += OnLeaseReleased;
        _sessions.RemoteSelectionChanged += OnRemoteSelectionChanged;
        _sessions.OperationStarted += OnOperationStarted;
        _sessions.OperationUpdated += OnOperationUpdated;
        _sessions.OperationFinished += OnOperationFinished;
    }

    public override void Shutdown()
    {
        _sessions.SessionClosed -= OnSessionClosed;
        _sessions.ParticipantJoined -= OnParticipantJoined;
        _sessions.ParticipantLeft -= OnParticipantLeft;
        _sessions.PermissionsChanged -= OnPermissionsChanged;
        _sessions.LeaseAcquired -= OnLeaseAcquired;
        _sessions.LeaseReleased -= OnLeaseReleased;
        _sessions.LeaseExpired -= OnLeaseReleased;
        _sessions.RemoteSelectionChanged -= OnRemoteSelectionChanged;
        _sessions.OperationStarted -= OnOperationStarted;
        _sessions.OperationUpdated -= OnOperationUpdated;
        _sessions.OperationFinished -= OnOperationFinished;
        _history.Clear();
        base.Shutdown();
    }

    public NcUiPatchMessage? PublishPatch(
        NcUiSessionId id,
        Func<NcUiSessionId, NcUiRevision, NcUiRevision, NcUiPatchMessage> createPatch)
    {
        if (!_sessions.TryGetSession(id, out var session))
            return null;
        var previous = session.Revision;
        var next = previous.Next();
        var patch = createPatch(id, previous, next);
        if (patch.Session != id || patch.BaseRevision != previous || patch.Revision != next)
            throw new InvalidOperationException("An NCUI patch factory returned an invalid revision envelope.");
        session.Revision = next;
        if (!_history.TryGetValue(id, out var history))
            _history[id] = history = new Queue<NcUiPatchMessage>();
        history.Enqueue(patch);
        while (history.Count > session.Options.PatchHistoryLength)
            history.Dequeue();
        foreach (var actor in session.Participants.Keys)
        {
            if (_ui.IsUiOpen(session.Owner, session.UiKey, actor))
                _ui.ServerSendUiMessage(session.Owner, session.UiKey, patch, actor);
        }
        return patch;
    }

    public void SendSnapshot(EntityUid actor, NcUiSnapshotMessage snapshot)
    {
        if (!_sessions.TryGetSession(snapshot.Session, out var session) ||
            snapshot.Revision != session.Revision || !session.Participants.ContainsKey(actor) ||
            !_ui.IsUiOpen(session.Owner, session.UiKey, actor))
        {
            return;
        }
        _ui.ServerSendUiMessage(session.Owner, session.UiKey, snapshot, actor);
    }

    public void SendResult(EntityUid actor, NcUiActionResultMessage result)
    {
        if (!_sessions.TryGetSession(result.Session, out var session) ||
            !session.Participants.ContainsKey(actor))
        {
            return;
        }
        _ui.ServerSendUiMessage(session.Owner, session.UiKey, result, actor);
    }

    public bool TryReplay(EntityUid actor, NcUiSessionId id, NcUiRevision afterRevision)
    {
        if (!_sessions.TryGetSession(id, out var session) || !session.Participants.ContainsKey(actor) ||
            !_history.TryGetValue(id, out var history))
        {
            return false;
        }
        if (afterRevision == session.Revision)
            return true;
        var expected = afterRevision.Next();
        var foundStart = false;
        foreach (var patch in history)
        {
            if (!foundStart)
            {
                if (patch.Revision != expected)
                    continue;
                foundStart = true;
            }
            if (patch.Revision != expected)
                return false;
            _ui.ServerSendUiMessage(session.Owner, session.UiKey, patch, actor);
            expected = expected.Next();
        }
        return foundStart && expected == session.Revision.Next();
    }

    public NcUiParticipantSnapshot[] CreateParticipantSnapshots(NcUiSession session)
    {
        var snapshots = new NcUiParticipantSnapshot[session.Participants.Count];
        var index = 0;
        foreach (var participant in session.Participants.Values.OrderBy(value => value.Actor.Id))
        {
            snapshots[index++] = new NcUiParticipantSnapshot(
                GetNetEntity(participant.Actor), participant.Permissions, true);
        }
        return snapshots;
    }

    public NcUiLeaseSnapshot[] CreateLeaseSnapshots(NcUiSession session)
    {
        var snapshots = new NcUiLeaseSnapshot[session.Leases.Count];
        var index = 0;
        foreach (var lease in session.Leases.Values.OrderBy(value => value.Object.Value))
        {
            snapshots[index++] = new NcUiLeaseSnapshot(
                lease.Object, GetNetEntity(lease.Owner), lease.Operation, lease.ExpiresAt);
        }
        return snapshots;
    }

    public NcUiRemoteSelectionSnapshot[] CreateRemoteSelectionSnapshots(NcUiSession session)
    {
        return session.Participants.Values
            .Where(value => value.RemoteSelection.Count != 0)
            .OrderBy(value => value.Actor.Id)
            .Select(value => new NcUiRemoteSelectionSnapshot(
                GetNetEntity(value.Actor),
                value.RemoteSelection.OrderBy(objectId => objectId.Value).ToArray()))
            .ToArray();
    }

    public NcUiRemoteOperationSnapshot[] CreateRemoteOperationSnapshots(NcUiSession session)
    {
        return session.ActiveOperations.Values
            .OrderBy(value => value.Id.Value)
            .Select(value => new NcUiRemoteOperationSnapshot(
                GetNetEntity(value.Actor), value.Id, value.LastPoint, value.TotalLength))
            .ToArray();
    }

    private void OnSessionClosed(NcUiSession session) => _history.Remove(session.Id);

    private void OnParticipantJoined(NcUiSession session, NcUiServerParticipant participant) =>
        PublishSessionDelta(session, new NcUiSessionDelta(
            NcUiSessionDeltaKind.ParticipantJoined,
            GetNetEntity(participant.Actor),
            participant.Permissions,
            NcUiObjectId.Invalid,
            NcUiOperationId.Invalid,
            default,
            []));

    private void OnParticipantLeft(NcUiSession session, EntityUid actor) =>
        PublishSessionDelta(session, new NcUiSessionDelta(
            NcUiSessionDeltaKind.ParticipantLeft,
            GetNetEntity(actor),
            NcUiPermission.None,
            NcUiObjectId.Invalid,
            NcUiOperationId.Invalid,
            default,
            []));

    private void OnPermissionsChanged(NcUiSession session, NcUiServerParticipant participant) =>
        PublishSessionDelta(session, new NcUiSessionDelta(
            NcUiSessionDeltaKind.PermissionsChanged,
            GetNetEntity(participant.Actor),
            participant.Permissions,
            NcUiObjectId.Invalid,
            NcUiOperationId.Invalid,
            default,
            []));

    private void OnLeaseAcquired(NcUiSession session, NcUiServerLease lease) =>
        PublishSessionDelta(session, new NcUiSessionDelta(
            NcUiSessionDeltaKind.LeaseAcquired,
            GetNetEntity(lease.Owner),
            NcUiPermission.None,
            lease.Object,
            lease.Operation,
            lease.ExpiresAt,
            []));

    private void OnLeaseReleased(NcUiSession session, NcUiServerLease lease) =>
        PublishSessionDelta(session, new NcUiSessionDelta(
            NcUiSessionDeltaKind.LeaseReleased,
            GetNetEntity(lease.Owner),
            NcUiPermission.None,
            lease.Object,
            lease.Operation,
            default,
            []));

    private void OnRemoteSelectionChanged(NcUiSession session, NcUiServerParticipant participant) =>
        PublishSessionDelta(session, new NcUiSessionDelta(
            NcUiSessionDeltaKind.RemoteSelectionChanged,
            GetNetEntity(participant.Actor),
            participant.Permissions,
            NcUiObjectId.Invalid,
            NcUiOperationId.Invalid,
            default,
            participant.RemoteSelection.OrderBy(value => value.Value).ToArray()));

    private void OnOperationStarted(NcUiSession session, NcUiServerOperation operation) =>
        PublishSessionDelta(session, new NcUiSessionDelta(
            NcUiSessionDeltaKind.OperationStarted,
            GetNetEntity(operation.Actor),
            NcUiPermission.None,
            NcUiObjectId.Invalid,
            operation.Id,
            default,
            [])
        {
            OperationPoint = operation.LastPoint,
            OperationTotalLength = operation.TotalLength,
        });

    private void OnOperationUpdated(NcUiSession session, NcUiServerOperation operation, Vector2[] points)
    {
        var origin = points.Length == 0 ? operation.LastPoint : points[0];
        PublishSessionDelta(session, new NcUiSessionDelta(
            NcUiSessionDeltaKind.OperationUpdated,
            GetNetEntity(operation.Actor),
            NcUiPermission.None,
            NcUiObjectId.Invalid,
            operation.Id,
            default,
            [])
        {
            OperationPoint = operation.LastPoint,
            OperationTotalLength = operation.TotalLength,
            OperationOrigin = origin,
            OperationPoints = NcUiPointCompression.Compress(
                points, origin, NcUiPointCompression.DefaultQuantum),
        });
    }

    private void OnOperationFinished(NcUiSession session, NcUiServerOperation operation, bool cancelled) =>
        PublishSessionDelta(session, new NcUiSessionDelta(
            cancelled ? NcUiSessionDeltaKind.OperationCancelled : NcUiSessionDeltaKind.OperationCompleted,
            GetNetEntity(operation.Actor),
            NcUiPermission.None,
            NcUiObjectId.Invalid,
            operation.Id,
            default,
            []));

    private void PublishSessionDelta(NcUiSession session, NcUiSessionDelta delta)
    {
        PublishPatch(session.Id, (id, previous, next) =>
            new NcUiSessionPatchMessage(id, previous, next, [delta]));
    }
}
