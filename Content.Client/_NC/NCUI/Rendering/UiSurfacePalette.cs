// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client._NC.NCUI.Core;

namespace Content.Client._NC.NCUI.Rendering;

/// <summary>
/// Lightweight state colors and motion timings used without invoking the global stylesheet per node.
/// </summary>
public sealed class UiSurfacePalette
{
    public static UiSurfacePalette NightCity => new();

    public Color Normal { get; set; } = Color.White;
    public Color Hovered { get; set; } = Color.FromHex("#B7FFF7");
    public Color Pressed { get; set; } = Color.FromHex("#6EF2E2");
    public Color Selected { get; set; } = Color.FromHex("#42E8D5");
    public Color Dragging { get; set; } = Color.FromHex("#7FD9FF");
    public Color ValidTarget { get; set; } = Color.FromHex("#68E38D");
    public Color InvalidTarget { get; set; } = Color.FromHex("#FF5C6B");
    public Color Disabled { get; set; } = Color.FromHex("#637275");
    public Color Error { get; set; } = Color.FromHex("#FF4356");
    public Color Warning { get; set; } = Color.FromHex("#FFC857");
    public Color PendingServer { get; set; } = Color.FromHex("#BB9CFF");
    public float HoverTransition { get; set; } = 0.08f;
    public float PressTransition { get; set; } = 0.04f;

    public Color Resolve(UiNodeVisualState state)
    {
        return state switch
        {
            UiNodeVisualState.Hovered => Hovered,
            UiNodeVisualState.Pressed => Pressed,
            UiNodeVisualState.Selected => Selected,
            UiNodeVisualState.Dragging => Dragging,
            UiNodeVisualState.ValidTarget => ValidTarget,
            UiNodeVisualState.InvalidTarget => InvalidTarget,
            UiNodeVisualState.Disabled => Disabled,
            UiNodeVisualState.Error => Error,
            UiNodeVisualState.Warning => Warning,
            UiNodeVisualState.PendingServer => PendingServer,
            _ => Normal,
        };
    }
}
