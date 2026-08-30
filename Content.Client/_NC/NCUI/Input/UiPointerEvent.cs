// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Client._NC.NCUI.Core;
using Robust.Shared.Input;

namespace Content.Client._NC.NCUI.Input;

/// <summary>
/// Logical interaction types routed through lightweight nodes rather than the Robust control tree.
/// </summary>
public enum UiPointerEventType : byte
{
    PointerEnter,
    PointerLeave,
    PointerDown,
    PointerUp,
    PointerMove,
    Click,
    DoubleClick,
    LongPress,
    Wheel,
    Pan,
    Zoom,
    DragBegin,
    DragMove,
    DragEnter,
    DragLeave,
    Drop,
    DragCancel,
    SelectionBegin,
    SelectionChanged,
    SelectionEnd,
    StrokeBegin,
    StrokePoint,
    StrokeEnd,
    StrokeCancel,
    ConnectBegin,
    ConnectPreview,
    ConnectCommit,
    ConnectCancel,
    ContextAction,
    KeyboardAction,
    TextCommit,
}

/// <summary>
/// Mutable routed event. A handler may stop bubbling or request logical pointer capture.
/// </summary>
public struct UiPointerEvent
{
    public UiPointerEventType Type;
    public UiNodeId Target;
    public UiNodeId CurrentTarget;
    public Vector2 PixelPosition;
    public Vector2 SurfacePosition;
    public Vector2 PixelDelta;
    public float WheelDelta;
    public BoundKeyFunction Function;
    public string? Text;
    public bool Handled;
    public bool CapturePointer;
    public bool ReleasePointer;

    public void Handle() => Handled = true;
}

/// <summary>
/// Node behavior receives routed events without requiring one Control or ECS entity per object.
/// </summary>
public interface IUiNodeInteractionHandler
{
    void Handle(UiSurfaceDocument document, UiNodeId node, ref UiPointerEvent args);
}
