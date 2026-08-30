// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client._NC.NCUI.Controls;
using Content.Client._NC.NCUI.Core;
using Content.Client._NC.NCUI.Virtualization;
using Content.Client.Stylesheets;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using static Content.Client.Stylesheets.StylesheetHelpers;

namespace Content.Client._NC.NCUI.Styles;

[NcUiSheetlet]
public sealed class NcUiBaseWindowSheetlet : Sheetlet<NightCityStylesheet>
{
    public override StyleRule[] GetRules(NightCityStylesheet sheet, object config) =>
    [
        E<PanelContainer>().Class("ncui-panel").Panel(new StyleBoxFlat
        {
            BackgroundColor = NCPanel.Background,
            BorderColor = sheet.PrimaryPalette.Element,
            BorderThickness = new Thickness(NCBorder.Thin),
            ContentMarginLeftOverride = NCSpacing.Md,
            ContentMarginRightOverride = NCSpacing.Md,
            ContentMarginTopOverride = NCSpacing.Md,
            ContentMarginBottomOverride = NCSpacing.Md,
        }),
    ];
}

[NcUiSheetlet]
public sealed class NcUiTypographySheetlet : Sheetlet<NightCityStylesheet>
{
    public override StyleRule[] GetRules(NightCityStylesheet sheet, object config) =>
    [
        E<Label>().Class(NCFont.BodyStyleClass).Prop(Label.StylePropertyFont, sheet.BaseFont.GetFont(NCFontSize.Body)),
        E<Label>().Class(NCFont.HeadingStyleClass).Prop(Label.StylePropertyFont, sheet.BaseFont.GetFont(NCFontSize.Heading)),
        E<Label>().Class(NCFont.DisplayStyleClass).Prop(Label.StylePropertyFont, sheet.BaseFont.GetFont(NCFontSize.Display)),
    ];
}

[NcUiSheetlet]
public sealed class NcUiVirtualListSheetlet : Sheetlet<NightCityStylesheet>
{
    public override StyleRule[] GetRules(NightCityStylesheet sheet, object config) =>
    [
        E<VirtualListControl>().Prop(Control.StylePropertyModulateSelf, Color.White),
        E<VirtualGridControl>().Prop(Control.StylePropertyModulateSelf, Color.White),
        E<VirtualTableControl>().Prop(Control.StylePropertyModulateSelf, Color.White),
        E<VirtualTreeControl>().Prop(Control.StylePropertyModulateSelf, Color.White),
    ];
}

[NcUiSheetlet]
public sealed class NcUiInteractiveSurfaceSheetlet : Sheetlet<NightCityStylesheet>
{
    public override StyleRule[] GetRules(NightCityStylesheet sheet, object config) =>
    [
        E<InteractiveSurfaceControl>().Prop(Control.StylePropertyModulateSelf, Color.White),
        E<PanZoomSurfaceControl>().Prop(Control.StylePropertyModulateSelf, Color.White),
    ];
}

[NcUiSheetlet]
public sealed class NcUiContextMenuSheetlet : Sheetlet<NightCityStylesheet>
{
    public override StyleRule[] GetRules(NightCityStylesheet sheet, object config) =>
    [
        E<ContextActionHost>().Prop(nameof(Control.Margin), new Thickness(NCSpacing.Sm)),
        E<RichTooltipHost>().Prop(Control.StylePropertyModulateSelf, Color.White),
    ];
}

[NcUiSheetlet]
public sealed class NcUiDomainControlSheetlet : Sheetlet<NightCityStylesheet>
{
    public override StyleRule[] GetRules(NightCityStylesheet sheet, object config) =>
    [
        E<SelectionInspector>().Prop(nameof(Control.Margin), new Thickness(NCSpacing.Md)),
        E<SurfaceMinimap>().Prop(Control.StylePropertyModulateSelf, Color.White),
    ];
}
