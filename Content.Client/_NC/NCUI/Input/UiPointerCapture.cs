// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Client._NC.NCUI.Core;

namespace Content.Client._NC.NCUI.Input;

/// <summary>
/// Current logical pointer state held entirely inside one focusable InteractiveSurfaceControl.
/// </summary>
public sealed class UiPointerCapture
{
    public UiNodeId? CapturedNode { get; internal set; }
    public UiNodeId? PressedNode { get; internal set; }
    public UiNodeId? HoveredNode { get; internal set; }
    public UiNodeId? FocusedNode { get; internal set; }
    public UiNodeId? DragSourceNode { get; internal set; }
    public Vector2 ActivePixelPosition { get; internal set; }
    public Vector2 PressPixelPosition { get; internal set; }
    public bool Dragging { get; internal set; }

    public void Clear()
    {
        CapturedNode = null;
        PressedNode = null;
        HoveredNode = null;
        FocusedNode = null;
        DragSourceNode = null;
        ActivePixelPosition = default;
        PressPixelPosition = default;
        Dragging = false;
    }
}

