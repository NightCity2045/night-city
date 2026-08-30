// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.GameObjects;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.NCUI;

/// <summary>
/// Base envelope for typed client requests. Domain messages subclass this type instead of sending dictionaries or JSON.
/// </summary>
[Serializable, NetSerializable]
public abstract class NcUiActionMessage : BoundUserInterfaceMessage
{
    public NcUiSessionId Session;
    public NcUiRevision BaseRevision;
    public NcUiActionSequence Sequence;

    protected NcUiActionMessage()
    {
    }

    protected NcUiActionMessage(
        NcUiSessionId session,
        NcUiRevision baseRevision,
        NcUiActionSequence sequence)
    {
        Session = session;
        BaseRevision = baseRevision;
        Sequence = sequence;
    }
}

/// <summary>
/// Initial typed domain snapshot. A concrete interface adds its own strongly typed payload fields.
/// </summary>
[Serializable, NetSerializable]
public abstract class NcUiSnapshotMessage : BoundUserInterfaceMessage
{
    public NcUiSessionId Session;
    public NcUiRevision Revision;
    public NcUiPermission Permissions;
    public NetEntity LocalParticipant = NetEntity.Invalid;
    public NcUiParticipantSnapshot[] Participants = [];
    public NcUiLeaseSnapshot[] Leases = [];
    public NcUiRemoteSelectionSnapshot[] RemoteSelections = [];
    public NcUiRemoteOperationSnapshot[] Operations = [];

    protected NcUiSnapshotMessage()
    {
    }

    protected NcUiSnapshotMessage(
        NcUiSessionId session,
        NcUiRevision revision,
        NcUiPermission permissions,
        NcUiParticipantSnapshot[] participants,
        NcUiLeaseSnapshot[] leases,
        NcUiRemoteSelectionSnapshot[]? remoteSelections = null,
        NcUiRemoteOperationSnapshot[]? operations = null,
        NetEntity localParticipant = default)
    {
        Session = session;
        Revision = revision;
        Permissions = permissions;
        LocalParticipant = localParticipant;
        Participants = participants;
        Leases = leases;
        RemoteSelections = remoteSelections ?? [];
        Operations = operations ?? [];
    }
}

/// <summary>
/// Incremental typed state update. Revisions must form an uninterrupted chain on the client.
/// </summary>
[Serializable, NetSerializable]
public abstract class NcUiPatchMessage : BoundUserInterfaceMessage
{
    public NcUiSessionId Session;
    public NcUiRevision BaseRevision;
    public NcUiRevision Revision;

    protected NcUiPatchMessage()
    {
    }

    protected NcUiPatchMessage(
        NcUiSessionId session,
        NcUiRevision baseRevision,
        NcUiRevision revision)
    {
        Session = session;
        BaseRevision = baseRevision;
        Revision = revision;
    }
}

/// <summary>
/// Framework-owned patch for participants, permissions, leases and remote collaborative state.
/// </summary>
[Serializable, NetSerializable]
public sealed class NcUiSessionPatchMessage : NcUiPatchMessage
{
    public NcUiSessionDelta[] Deltas = [];

    public NcUiSessionPatchMessage()
    {
    }

    public NcUiSessionPatchMessage(
        NcUiSessionId session,
        NcUiRevision baseRevision,
        NcUiRevision revision,
        NcUiSessionDelta[] deltas)
        : base(session, baseRevision, revision)
    {
        Deltas = deltas;
    }
}

/// <summary>
/// Authoritative acknowledgement or rejection for one client sequence.
/// </summary>
[Serializable, NetSerializable]
public sealed class NcUiActionResultMessage : BoundUserInterfaceMessage
{
    public NcUiSessionId Session;
    public NcUiActionSequence Sequence;
    public NcUiRevision Revision;
    public NcUiActionResult Result;

    public NcUiActionResultMessage()
    {
    }

    public NcUiActionResultMessage(
        NcUiSessionId session,
        NcUiActionSequence sequence,
        NcUiRevision revision,
        NcUiActionResult result)
    {
        Session = session;
        Sequence = sequence;
        Revision = revision;
        Result = result;
    }
}

/// <summary>
/// Requests a snapshot or patch replay after a client detects a revision gap.
/// </summary>
[Serializable, NetSerializable]
public sealed class NcUiResyncRequestMessage : NcUiActionMessage
{
    public NcUiResyncRequestMessage()
    {
    }

    public NcUiResyncRequestMessage(
        NcUiSessionId session,
        NcUiRevision baseRevision,
        NcUiActionSequence sequence)
        : base(session, baseRevision, sequence)
    {
    }
}
