// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Client.Stylesheets;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using static Content.Client.Stylesheets.StylesheetHelpers;

namespace Content.Client._NC.Meta.UI;

/// <summary>
/// The look of the computer's applications and of the server panel: dark glass cards, a cyan accent and a red rule,
/// the same palette as the desktop. Classes: ncMetaWindow, ncMetaBar, ncMetaCard, ncMetaList, ncMetaNav, ncMetaHeading, ncMetaMuted.
/// </summary>
[CommonSheetlet]
public sealed class NCMetaDesktopSheetlet : Sheetlet<PalettedStylesheet>
{
    public const string Window = "ncMetaWindow";
    public const string Bar = "ncMetaBar";
    public const string Card = "ncMetaCard";
    public const string List = "ncMetaList";
    public const string Nav = "ncMetaNav";
    public const string Heading = "ncMetaHeading";
    public const string Muted = "ncMetaMuted";
    public const string Action = "ncMetaAction";
    public const string Frame = "ncMetaFrame";

    private static MutableSelectorElement CButton() => E<ContainerButton>().Class(ContainerButton.StyleClassButton);

    private static StyleBoxFlat Flat(string background, string? border = null, Thickness? thickness = null, float margin = 0)
    {
        var box = new StyleBoxFlat { BackgroundColor = Color.FromHex(background) };
        if (border != null)
        {
            box.BorderColor = Color.FromHex(border);
            box.BorderThickness = thickness ?? new Thickness(1);
        }

        box.ContentMarginLeftOverride = margin;
        box.ContentMarginRightOverride = margin;
        box.ContentMarginTopOverride = margin;
        box.ContentMarginBottomOverride = margin;
        return box;
    }

    private static StyleBoxFlat Tab(string background, string border)
    {
        var box = Flat(background, border, new Thickness(0, 0, 0, 2));
        box.ContentMarginLeftOverride = 14;
        box.ContentMarginRightOverride = 14;
        box.ContentMarginTopOverride = 6;
        box.ContentMarginBottomOverride = 6;
        return box;
    }

    private static StyleBoxFlat Row(string background)
    {
        var box = Flat(background);
        box.ContentMarginLeftOverride = 8;
        box.ContentMarginRightOverride = 8;
        box.ContentMarginTopOverride = 4;
        box.ContentMarginBottomOverride = 4;
        return box;
    }

    public override StyleRule[] GetRules(PalettedStylesheet sheet, object config)
    {
        return
        [
            E<PanelContainer>().Class(Window).Prop(PanelContainer.StylePropertyPanel, Flat("#070B14")),
            E<PanelContainer>().Class(Bar).Prop(PanelContainer.StylePropertyPanel,
                Flat("#0A0F1C", "#A3171E", new Thickness(0, 0, 0, 2), 4)),
            E<PanelContainer>().Class(Card).Prop(PanelContainer.StylePropertyPanel, Flat("#0C1426", "#1F4A6B", new Thickness(1), 10)),

            E<ItemList>().Class(List)
                .Prop(ItemList.StylePropertyBackground, Flat("#080D1A"))
                .Prop(ItemList.StylePropertyItemBackground, Row("#0A1224"))
                .Prop(ItemList.StylePropertyDisabledItemBackground, Row("#0A1224"))
                .Prop(ItemList.StylePropertySelectedItemBackground, Row("#14405A")),

            CButton().Class(Nav).Box(Tab("#0A0F1C", "#1F2A44")),
            CButton().Class(Nav).PseudoHovered().Box(Tab("#11203A", "#3A7FA0")),
            CButton().Class(Nav).PseudoPressed().Box(Tab("#0F2E40", "#31E6FF")),

            E<PanelContainer>().Class(Frame).Prop(PanelContainer.StylePropertyPanel, Flat("#09121C", "#1F4A6B", new Thickness(1), 2)),

            CButton().Class(Action).Box(Flat("#0F1A30", "#1F4A6B", new Thickness(1), 7)),
            CButton().Class(Action).PseudoHovered().Box(Flat("#14284A", "#31E6FF", new Thickness(1), 7)),
            CButton().Class(Action).PseudoPressed().Box(Flat("#14405A", "#FFC233", new Thickness(1), 7)),
            CButton().Class(Action).PseudoDisabled().Box(Flat("#0A1020", "#18283C", new Thickness(1), 7)),

            E<Label>().Class(Heading).FontColor(Color.FromHex("#31E6FF")).Font(sheet.BaseFont.GetFont(15)),
            E<Label>().Class(Muted).FontColor(Color.FromHex("#7FA3BF")),
        ];
    }
}
