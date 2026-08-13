// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client._NC.Combat.Targeting;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._NC.Medical.Diagnostics;

/// <summary>
/// Compact pass-through owner-health display positioned inside the gameplay viewport, never inside chat.
/// </summary>
public sealed class NCBodyStatusHudOverlay : LayoutContainer
{
    public NCTargetingDollControl Doll { get; }

    public NCBodyStatusHudOverlay()
    {
        MouseFilter = MouseFilterMode.Ignore;
        InheritChildMeasure = false;

        Doll = new NCTargetingDollControl
        {
            DisplayScale = 2f,
            InteractionEnabled = false,
            ShowAutomaticSelection = false,
            ShowCanvasBackground = false,
            MouseFilter = MouseFilterMode.Ignore,
        };

        AddChild(Doll);

        LayoutContainer.SetAnchorAndMarginPreset(Doll, LayoutPreset.CenterRight, margin: 18);
        // Keep clear of the vanilla center-right alert stack while matching the compact slot above it.
        LayoutContainer.SetAnchorTop(Doll, 0.38f);
        LayoutContainer.SetAnchorBottom(Doll, 0.38f);
    }
}
