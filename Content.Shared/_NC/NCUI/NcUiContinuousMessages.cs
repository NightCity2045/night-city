// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.NCUI;

/// <summary>
/// Starts a typed continuous domain operation. Concrete messages add their tool and surface-layer identifiers.
/// </summary>
[Serializable, NetSerializable]
public abstract class NcUiStartOperationMessage : NcUiActionMessage
{
    public NcUiOperationId Operation;

    protected NcUiStartOperationMessage()
    {
    }

    protected NcUiStartOperationMessage(
        NcUiSessionId session,
        NcUiRevision baseRevision,
        NcUiActionSequence sequence,
        NcUiOperationId operation)
        : base(session, baseRevision, sequence)
    {
        Operation = operation;
    }
}

/// <summary>
/// Batches filtered pointer samples. Consequences are calculated by the authoritative domain system.
/// </summary>
[Serializable, NetSerializable]
public abstract class NcUiUpdateOperationMessage : NcUiActionMessage
{
    public NcUiOperationId Operation;
    public uint SegmentSequence;
    public Vector2 Origin;
    public float PointQuantum = NcUiPointCompression.DefaultQuantum;
    public NcUiCompressedPoint[] Points = [];
    public TimeSpan ClientTime;

    protected NcUiUpdateOperationMessage()
    {
    }

    protected NcUiUpdateOperationMessage(
        NcUiSessionId session,
        NcUiRevision baseRevision,
        NcUiActionSequence sequence,
        NcUiOperationId operation,
        uint segmentSequence,
        Vector2[] points,
        TimeSpan clientTime)
        : base(session, baseRevision, sequence)
    {
        Operation = operation;
        SegmentSequence = segmentSequence;
        Origin = points.Length == 0 ? Vector2.Zero : points[0];
        Points = NcUiPointCompression.Compress(points, Origin, PointQuantum);
        ClientTime = clientTime;
    }

    public Vector2[] DecompressPoints() => NcUiPointCompression.Decompress(Points, Origin, PointQuantum);
}

/// <summary>
/// Commits a completed continuous operation after the final server-side validation pass.
/// </summary>
[Serializable, NetSerializable]
public abstract class NcUiCommitOperationMessage : NcUiActionMessage
{
    public NcUiOperationId Operation;

    protected NcUiCommitOperationMessage()
    {
    }

    protected NcUiCommitOperationMessage(
        NcUiSessionId session,
        NcUiRevision baseRevision,
        NcUiActionSequence sequence,
        NcUiOperationId operation)
        : base(session, baseRevision, sequence)
    {
        Operation = operation;
    }
}

/// <summary>
/// Cancels local and authoritative state for an unfinished operation.
/// </summary>
[Serializable, NetSerializable]
public abstract class NcUiCancelOperationMessage : NcUiActionMessage
{
    public NcUiOperationId Operation;

    protected NcUiCancelOperationMessage()
    {
    }

    protected NcUiCancelOperationMessage(
        NcUiSessionId session,
        NcUiRevision baseRevision,
        NcUiActionSequence sequence,
        NcUiOperationId operation)
        : base(session, baseRevision, sequence)
    {
        Operation = operation;
    }
}
