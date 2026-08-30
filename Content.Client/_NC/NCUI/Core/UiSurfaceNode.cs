// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Maths;

namespace Content.Client._NC.NCUI.Core;

/// <summary>
/// Capabilities used by input and rendering without consulting a full Robust control tree.
/// </summary>
[Flags]
public enum UiNodeFlags : ushort
{
    None = 0,
    Visible = 1 << 0,
    Enabled = 1 << 1,
    HitTest = 1 << 2,
    Draggable = 1 << 3,
    DropTarget = 1 << 4,
    Focusable = 1 << 5,
    Selectable = 1 << 6,
}

/// <summary>
/// Visual state is resolved by a surface palette and never triggers Robust style or layout invalidation.
/// </summary>
public enum UiNodeVisualState : byte
{
    Normal,
    Hovered,
    Pressed,
    Selected,
    Dragging,
    ValidTarget,
    InvalidTarget,
    Disabled,
    Error,
    Warning,
    PendingServer,
}

/// <summary>
/// Dense data record for one logical object. Bounds are an axis-aligned surface-space box.
/// </summary>
public struct UiSurfaceNode
{
    public UiNodeId Id;
    public UiNodeId? Parent;
    public UIBox2 Bounds;
    public int ZIndex;
    public UiNodeFlags Flags;
    public UiNodeVisualState VisualState;
    public UiVisualHandle Visual;
    public UiHitShapeHandle HitShape;
    public UiActionId PrimaryAction;
    public float Opacity;
    public float Rotation;
    public float Scale;
    public Color Tint;
    public float Highlight;
    public float ThicknessScale;
    public float Fill;

    public UiSurfaceNode(
        UIBox2 bounds,
        UiVisualHandle visual,
        UiNodeFlags flags = UiNodeFlags.Visible | UiNodeFlags.Enabled,
        int zIndex = 0,
        UiNodeId? parent = null)
    {
        Bounds = bounds;
        Visual = visual;
        Flags = flags;
        ZIndex = zIndex;
        Parent = parent;
        Id = UiNodeId.Invalid;
        VisualState = UiNodeVisualState.Normal;
        HitShape = UiHitShapeHandle.Invalid;
        PrimaryAction = UiActionId.Invalid;
        Opacity = 1f;
        Rotation = 0f;
        Scale = 1f;
        Tint = Color.White;
        Highlight = 0f;
        ThicknessScale = 1f;
        Fill = 1f;
    }
}
