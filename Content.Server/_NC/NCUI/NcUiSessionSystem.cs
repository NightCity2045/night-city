// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.NCUI;
using Robust.Server.Player;
using Robust.Shared.Enums;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._NC.NCUI;

/// <summary>
/// Owns NCUI session lifecycle, participants, short object leases and continuous operation sequencing.
/// </summary>
public sealed partial class NcUiSessionSystem : EntitySystem
{
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IGameTiming _timing = default!;

    private readonly Dictionary<NcUiSessionId, NcUiSession> _sessions = [];
    private readonly List<NcUiObjectId> _leaseBuffer = [];
    private readonly List<NcUiOperationId> _operationBuffer = [];
    private readonly List<NcUiSessionId> _sessionBuffer = [];
    private uint _nextSession = 1;

    public event Action<NcUiSession>? SessionClosed;
    public event Action<NcUiSession, NcUiServerParticipant>? ParticipantJoined;
    public event Action<NcUiSession, EntityUid>? ParticipantLeft;
    public event Action<NcUiSession, NcUiServerParticipant>? PermissionsChanged;
    public event Action<NcUiSession, NcUiServerLease>? LeaseAcquired;
    public event Action<NcUiSession, NcUiServerLease>? LeaseReleased;
    public event Action<NcUiSession, NcUiServerLease>? LeaseExpired;
    public event Action<NcUiSession, NcUiServerParticipant>? RemoteSelectionChanged;
    public event Action<NcUiSession, NcUiServerOperation>? OperationStarted;
    public event Action<NcUiSession, NcUiServerOperation, Vector2[]>? OperationUpdated;
    public event Action<NcUiSession, NcUiServerOperation, bool>? OperationFinished;

    public override void Initialize()
    {
        base.Initialize();
        _players.PlayerStatusChanged += OnPlayerStatusChanged;
        SubscribeLocalEvent<PlayerDetachedEvent>(OnPlayerDetached);
    }

    public override void Shutdown()
    {
        _players.PlayerStatusChanged -= OnPlayerStatusChanged;
        foreach (var session in _sessions.Values)
            SessionClosed?.Invoke(session);
        _sessions.Clear();
        base.Shutdown();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var now = _timing.CurTime;
        _sessionBuffer.Clear();
        foreach (var session in _sessions.Values)
        {
            if (!Exists(session.Owner))
            {
                _sessionBuffer.Add(session.Id);
                continue;
            }
            if (session.Leases.Count == 0)
                continue;
            _leaseBuffer.Clear();
            foreach (var (id, lease) in session.Leases)
            {
                if (lease.ExpiresAt <= now)
                    _leaseBuffer.Add(id);
            }
            foreach (var id in _leaseBuffer)
            {
                var lease = session.Leases[id];
                session.Leases.Remove(id);
                LeaseExpired?.Invoke(session, lease);
            }
        }
        foreach (var session in _sessionBuffer)
            CloseSession(session);
    }

    public NcUiSession CreateSession(
        EntityUid owner,
        Enum uiKey,
        NcUiSessionOptions? options = null)
    {
        if (!Exists(owner))
            throw new ArgumentException("The NCUI owner entity must exist.", nameof(owner));
        var actualOptions = options ?? NcUiSessionOptions.Default;
        actualOptions.Validate();
        var id = AllocateSessionId();
        var session = new NcUiSession(id, owner, uiKey, actualOptions);
        _sessions.Add(id, session);
        return session;
    }

    public bool CloseSession(NcUiSessionId id)
    {
        if (!_sessions.Remove(id, out var session))
            return false;
        SessionClosed?.Invoke(session);
        return true;
    }

    public bool TryGetSession(NcUiSessionId id, out NcUiSession session) =>
        _sessions.TryGetValue(id, out session!);

    public bool TryJoin(NcUiSessionId id, EntityUid actor, NcUiPermission permissions)
    {
        if (!_sessions.TryGetValue(id, out var session) || !Exists(actor) ||
            session.Participants.Count >= session.Options.MaximumParticipants ||
            session.Participants.ContainsKey(actor))
        {
            return false;
        }
        var participant = new NcUiServerParticipant(actor, permissions);
        session.Participants.Add(actor, participant);
        ParticipantJoined?.Invoke(session, participant);
        return true;
    }

    public bool Leave(NcUiSessionId id, EntityUid actor)
    {
        if (!_sessions.TryGetValue(id, out var session) || !session.Participants.Remove(actor))
            return false;
        ReleaseActorState(session, actor);
        ParticipantLeft?.Invoke(session, actor);
        return true;
    }

    public bool SetPermissions(NcUiSessionId id, EntityUid actor, NcUiPermission permissions)
    {
        if (!_sessions.TryGetValue(id, out var session) ||
            !session.Participants.TryGetValue(actor, out var participant))
        {
            return false;
        }
        participant.Permissions = permissions;
        PermissionsChanged?.Invoke(session, participant);
        return true;
    }

    public bool SetRemoteSelection(NcUiSessionId id, EntityUid actor, ReadOnlySpan<NcUiObjectId> selection)
    {
        if (!_sessions.TryGetValue(id, out var session) ||
            !session.Participants.TryGetValue(actor, out var participant) || selection.Length > 256)
        {
            return false;
        }
        participant.RemoteSelection.Clear();
        foreach (var objectId in selection)
        {
            if (objectId.IsValid)
                participant.RemoteSelection.Add(objectId);
        }
        RemoteSelectionChanged?.Invoke(session, participant);
        return true;
    }

    public bool TryAcquireLease(
        NcUiSessionId id,
        EntityUid actor,
        NcUiObjectId @object,
        NcUiOperationId operation,
        TimeSpan? duration = null,
        bool overrideExisting = false)
    {
        if (!_sessions.TryGetValue(id, out var session) ||
            !session.Participants.TryGetValue(actor, out var participant) ||
            !@object.IsValid || !operation.IsValid)
        {
            return false;
        }
        var now = _timing.CurTime;
        if (session.Leases.TryGetValue(@object, out var existing) && existing.ExpiresAt > now &&
            existing.Owner != actor && (!overrideExisting ||
                (participant.Permissions & NcUiPermission.OverrideLeases) == 0))
        {
            return false;
        }
        var leaseDuration = duration ?? session.Options.DefaultLeaseDuration;
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > session.Options.MaximumLeaseDuration)
            return false;
        var lease = new NcUiServerLease(@object, actor, operation, now + leaseDuration);
        session.Leases[@object] = lease;
        LeaseAcquired?.Invoke(session, lease);
        return true;
    }

    /// <summary>
    /// Applies the framework arbitration mode before a domain system performs its own semantic validation.
    /// </summary>
    public NcUiActionResult TryBeginObjectMutation(
        NcUiSessionId id,
        EntityUid actor,
        NcUiObjectId @object,
        NcUiOperationId operation,
        NcUiConcurrencyMode mode,
        NcUiRevision baseRevision)
    {
        if (!_sessions.TryGetValue(id, out var session) || !session.Participants.ContainsKey(actor))
            return NcUiActionResult.RejectedInvalidSession;
        if (!@object.IsValid || !operation.IsValid)
            return NcUiActionResult.RejectedInvalidOperation;
        if (mode == NcUiConcurrencyMode.Optimistic && baseRevision != session.Revision)
            return NcUiActionResult.RejectedOutdatedState;
        if (mode == NcUiConcurrencyMode.Exclusive &&
            !TryAcquireLease(id, actor, @object, operation))
        {
            return NcUiActionResult.RejectedConflict;
        }
        return NcUiActionResult.Accepted;
    }

    public bool RenewLease(NcUiSessionId id, EntityUid actor, NcUiObjectId @object, NcUiOperationId operation)
    {
        if (!_sessions.TryGetValue(id, out var session) ||
            !session.Leases.TryGetValue(@object, out var lease) || lease.Owner != actor ||
            lease.Operation != operation)
        {
            return false;
        }
        lease.ExpiresAt = _timing.CurTime + session.Options.DefaultLeaseDuration;
        return true;
    }

    public bool ReleaseLease(NcUiSessionId id, EntityUid actor, NcUiObjectId @object, bool force = false)
    {
        if (!_sessions.TryGetValue(id, out var session) ||
            !session.Leases.TryGetValue(@object, out var lease))
        {
            return false;
        }
        if (lease.Owner != actor && (!force || !session.Participants.TryGetValue(actor, out var participant) ||
            (participant.Permissions & NcUiPermission.OverrideLeases) == 0))
        {
            return false;
        }
        session.Leases.Remove(@object);
        LeaseReleased?.Invoke(session, lease);
        return true;
    }

    public bool TryStartOperation(
        NcUiSessionId id,
        EntityUid actor,
        NcUiOperationId operation,
        Vector2 firstPoint,
        TimeSpan clientTime)
    {
        if (!_sessions.TryGetValue(id, out var session) ||
            !session.Participants.ContainsKey(actor) || !operation.IsValid ||
            session.ActiveOperations.ContainsKey(operation) ||
            !float.IsFinite(firstPoint.X) || !float.IsFinite(firstPoint.Y))
        {
            return false;
        }
        var active = new NcUiServerOperation(operation, actor, firstPoint, clientTime);
        session.ActiveOperations.Add(operation, active);
        OperationStarted?.Invoke(session, active);
        return true;
    }

    public bool TryAppendOperation(
        NcUiSessionId id,
        EntityUid actor,
        NcUiOperationId operation,
        uint segmentSequence,
        ReadOnlySpan<Vector2> points,
        TimeSpan clientTime,
        int maximumPoints,
        float maximumSegmentLength,
        float maximumTotalLength,
        float maximumSpeed,
        TimeSpan? maximumClientInterval = null)
    {
        if (maximumPoints <= 0 || !float.IsFinite(maximumSegmentLength) || maximumSegmentLength <= 0f ||
            !float.IsFinite(maximumTotalLength) || maximumTotalLength <= 0f ||
            !float.IsFinite(maximumSpeed) || maximumSpeed <= 0f ||
            maximumClientInterval is { } interval && interval <= TimeSpan.Zero)
        {
            return false;
        }
        if (!_sessions.TryGetValue(id, out var session) ||
            !session.ActiveOperations.TryGetValue(operation, out var active) || active.Actor != actor ||
            segmentSequence != active.LastSegmentSequence + 1 || points.Length == 0 ||
            points.Length > maximumPoints || clientTime < active.LastClientTime)
        {
            return false;
        }
        var length = 0f;
        var previous = active.LastPoint;
        foreach (var point in points)
        {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
                return false;
            var segment = Vector2.Distance(previous, point);
            if (segment > maximumSegmentLength)
                return false;
            length += segment;
            previous = point;
        }
        var elapsed = Math.Max(0.001, (clientTime - active.LastClientTime).TotalSeconds);
        if (maximumClientInterval is { } maximumInterval && clientTime - active.LastClientTime > maximumInterval ||
            active.TotalLength + length > maximumTotalLength || length / elapsed > maximumSpeed)
            return false;
        active.LastSegmentSequence = segmentSequence;
        active.LastPoint = previous;
        active.TotalLength += length;
        active.LastClientTime = clientTime;
        OperationUpdated?.Invoke(session, active, points.ToArray());
        return true;
    }

    public bool FinishOperation(NcUiSessionId id, EntityUid actor, NcUiOperationId operation, bool cancelled = false)
    {
        if (!_sessions.TryGetValue(id, out var session) ||
            !session.ActiveOperations.TryGetValue(operation, out var active) || active.Actor != actor)
        {
            return false;
        }
        session.ActiveOperations.Remove(operation);
        _leaseBuffer.Clear();
        foreach (var (objectId, lease) in session.Leases)
        {
            if (lease.Operation == operation)
                _leaseBuffer.Add(objectId);
        }
        foreach (var objectId in _leaseBuffer)
        {
            var lease = session.Leases[objectId];
            session.Leases.Remove(objectId);
            LeaseReleased?.Invoke(session, lease);
        }
        OperationFinished?.Invoke(session, active, cancelled);
        return true;
    }

    private NcUiSessionId AllocateSessionId()
    {
        while (true)
        {
            var id = new NcUiSessionId(_nextSession++);
            if (id.IsValid && !_sessions.ContainsKey(id))
                return id;
        }
    }

    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs args)
    {
        if (args.NewStatus is not (SessionStatus.Disconnected or SessionStatus.Zombie) ||
            args.Session.AttachedEntity is not { } actor)
        {
            return;
        }
        RemoveActorFromAllSessions(actor);
    }

    private void OnPlayerDetached(PlayerDetachedEvent args) => RemoveActorFromAllSessions(args.Entity);

    private void RemoveActorFromAllSessions(EntityUid actor)
    {
        foreach (var session in _sessions.Values)
        {
            if (!session.Participants.Remove(actor))
                continue;
            ReleaseActorState(session, actor);
            ParticipantLeft?.Invoke(session, actor);
        }
    }

    private void ReleaseActorState(NcUiSession session, EntityUid actor)
    {
        _leaseBuffer.Clear();
        foreach (var (objectId, lease) in session.Leases)
        {
            if (lease.Owner == actor)
                _leaseBuffer.Add(objectId);
        }
        foreach (var objectId in _leaseBuffer)
        {
            var lease = session.Leases[objectId];
            session.Leases.Remove(objectId);
            LeaseReleased?.Invoke(session, lease);
        }

        _operationBuffer.Clear();
        foreach (var (operationId, operation) in session.ActiveOperations)
        {
            if (operation.Actor == actor)
                _operationBuffer.Add(operationId);
        }
        foreach (var operationId in _operationBuffer)
        {
            var operation = session.ActiveOperations[operationId];
            session.ActiveOperations.Remove(operationId);
            OperationFinished?.Invoke(session, operation, true);
        }
    }
}
