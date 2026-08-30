// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Client.Stylesheets;
using Content.Client.Stylesheets.Fonts;
using Content.Client.Stylesheets.Palette;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Utility;
using static Content.Client.Stylesheets.StylesheetHelpers;

namespace Content.Client._NC.NCUI.Styles;

public sealed class NightCityStylesheet : CommonStylesheet
{
    public override string StylesheetName => "NightCity";
    public override NotoFontFamilyStack BaseFont { get; }
    public override ColorPalette PrimaryPalette => ColorPalette.FromHexBase("#318F88", text: NCText.Primary);
    public override ColorPalette SecondaryPalette => ColorPalette.FromHexBase("#263D45", text: NCText.Primary);
    public override ColorPalette PositivePalette => ColorPalette.FromHexBase("#3E9B61", text: NCText.Primary);
    public override ColorPalette NegativePalette => ColorPalette.FromHexBase("#B52E42", text: NCText.Primary);
    public override ColorPalette HighlightPalette => ColorPalette.FromHexBase("#B58A32", text: NCText.Primary);

    public static readonly ResPath TextureRoot = new("/Textures/Interface/Nano");

    public override Dictionary<Type, ResPath[]> Roots => new()
    {
        { typeof(TextureResource), [TextureRoot] },
    };

    public NightCityStylesheet(object config, StylesheetManager manager) : base(config)
    {
        BaseFont = new NotoFontFamilyStack(ResCache);
        var fontSizes = new List<(string?, int)>
        {
            (null, NCFontSize.Body),
            (StyleClass.FontSmall, NCFontSize.Small),
            (StyleClass.FontLarge, NCFontSize.Heading),
        };
        var rules = new[]
        {
            GetRulesForFont(null, BaseFont, fontSizes),
            [
                E().Prop(Label.StylePropertyFont, BaseFont.GetFont(NCFontSize.Body)),
                E().Prop(Label.StylePropertyFontColor, NCText.Primary),
            ],
            GetAllSheetletRules<PalettedStylesheet, CommonSheetletAttribute>(manager),
            GetAllSheetletRules<NightCityStylesheet, NcUiSheetletAttribute>(manager),
        };
        Stylesheet = new Stylesheet(rules.SelectMany(rule => rule).ToArray());
    }
}
