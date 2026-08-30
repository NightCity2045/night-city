// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;
using System.Numerics;

namespace Content.Shared._NC.NCUI;

/// <summary>
/// Sanitized participant information used by collaborative presenters.
/// </summary>
[Serializable, NetSerializable]
public sealed record NcUiParticipantSnapshot(
    NetEntity Participant,
    NcUiPermission Permissions,
    bool Connected);

/// <summary>
/// A short authoritative ownership lease for an object that cannot be edited concurrently.
/// </summary>
[Serializable, NetSerializable]
public sealed record NcUiLeaseSnapshot(
    NcUiObjectId Object,
    NetEntity Owner,
    NcUiOperationId Operation,
    TimeSpan ExpiresAt);

/// <summary>
/// Remote selection is presentation state shared deliberately by a collaborative domain UI.
/// </summary>
[Serializable, NetSerializable]
public sealed record NcUiRemoteSelectionSnapshot(
    NetEntity Participant,
    NcUiObjectId[] Objects);

[Serializable, NetSerializable]
public sealed record NcUiRemoteOperationSnapshot(
    NetEntity Participant,
    NcUiOperationId Operation,
    Vector2 LastPoint,
    float TotalLength);

/// <summary>
/// Generic collaborative changes. Domain patches remain strongly typed subclasses of <see cref="NcUiPatchMessage"/>.
/// </summary>
[Serializable, NetSerializable]
public enum NcUiSessionDeltaKind : byte
{
    ParticipantJoined,
    ParticipantLeft,
    PermissionsChanged,
    LeaseAcquired,
    LeaseReleased,
    RemoteSelectionChanged,
    OperationStarted,
    OperationUpdated,
    OperationCompleted,
    OperationCancelled,
}

/// <summary>
/// One framework-level session delta. Only fields relevant to <see cref="Kind"/> are populated.
/// </summary>
[Serializable, NetSerializable]
public sealed record NcUiSessionDelta(
    NcUiSessionDeltaKind Kind,
    NetEntity Participant,
    NcUiPermission Permissions,
    NcUiObjectId Object,
    NcUiOperationId Operation,
    TimeSpan LeaseExpiresAt,
    NcUiObjectId[] Selection)
{
    public Vector2 OperationPoint { get; init; }
    public float OperationTotalLength { get; init; }
    public Vector2 OperationOrigin { get; init; }
    public float OperationPointQuantum { get; init; } = NcUiPointCompression.DefaultQuantum;
    public NcUiCompressedPoint[] OperationPoints { get; init; } = [];
}
