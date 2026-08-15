// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._NC.Combat.Targeting;

/// <summary>
/// Pass-through gameplay layer that keeps the targeting doll available at the lower-right edge of the HUD.
/// Only the compact panel consumes pointer input.
/// </summary>
public sealed class NCTargetingHudOverlay : LayoutContainer
{
    public NCTargetingDollControl Doll { get; }

    public NCTargetingHudOverlay()
    {
        MouseFilter = MouseFilterMode.Ignore;
        InheritChildMeasure = false;

        Doll = new NCTargetingDollControl();

        var content = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 4,
        };
        content.AddChild(new Label
        {
            Text = Loc.GetString("nc-targeting-doll-title"),
            FontColorOverride = Color.FromHex("#68FFF0"),
            HorizontalAlignment = HAlignment.Center,
        });
        content.AddChild(Doll);

        var panel = new PanelContainer
        {
            MouseFilter = MouseFilterMode.Stop,
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = Color.FromHex("#061016E8"),
                BorderColor = Color.FromHex("#3DD9D0C0"),
                BorderThickness = new Thickness(1),
                ContentMarginLeftOverride = 8,
                ContentMarginRightOverride = 8,
                ContentMarginTopOverride = 6,
                ContentMarginBottomOverride = 7,
            },
        };
        panel.AddChild(content);
        AddChild(panel);

        LayoutContainer.SetAnchorAndMarginPreset(
            panel,
            LayoutPreset.BottomRight,
            margin: 18);
    }
}
