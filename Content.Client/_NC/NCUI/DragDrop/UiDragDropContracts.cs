// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Client._NC.NCUI.Core;

namespace Content.Client._NC.NCUI.DragDrop;

/// <summary>
/// Stable payload category used for fast client previews. The payload remains a concrete typed object.
/// </summary>
public readonly record struct UiDragPayloadType(ushort Value);

public interface IUiDragPayload
{
    UiDragPayloadType Type { get; }
}

public readonly record struct UiDragContext(
    UiNodeId Source,
    UiNodeId Target,
    Vector2 SurfacePosition);

public enum UiDropPreview : byte
{
    Accepted,
    AcceptedWithWarning,
    RejectedWrongType,
    RejectedOccupied,
    RejectedNoAccess,
    RejectedIncompatible,
    RejectedServerCheckRequired,
}

public interface IUiDropTarget
{
    UiDropPreview CanAccept(in UiDragContext context, IUiDragPayload payload);
    void Drop(in UiDragContext context, IUiDragPayload payload);
}
