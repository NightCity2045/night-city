// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Client._NC.NCUI.Styles;

public static class NCSpacing
{
    public const float Xs = 2f;
    public const float Sm = 4f;
    public const float Md = 8f;
    public const float Lg = 12f;
    public const float Xl = 20f;
}

public static class NCRadius
{
    public const float Small = 2f;
    public const float Medium = 4f;
    public const float Large = 8f;
}

public static class NCBorder
{
    public const float Thin = 1f;
    public const float Regular = 2f;
    public const float Emphasis = 3f;
}

public static class NCFontSize
{
    public const int Small = 10;
    public const int Body = 12;
    public const int Heading = 16;
    public const int Display = 22;
}

public static class NCFont
{
    public const string BodyStyleClass = "ncui-font-body";
    public const string HeadingStyleClass = "ncui-font-heading";
    public const string DisplayStyleClass = "ncui-font-display";
}

public static class NCPanel
{
    public static readonly Color Background = Color.FromHex("#0B1115");
    public static readonly Color Raised = Color.FromHex("#142027");
    public static readonly Color Inset = Color.FromHex("#070B0E");
}

public static class NCText
{
    public static readonly Color Primary = Color.FromHex("#D6E7E8");
    public static readonly Color Muted = Color.FromHex("#789194");
}

public static class NCAccent
{
    public static readonly Color Primary = Color.FromHex("#3BE6D1");
    public static readonly Color Secondary = Color.FromHex("#56A8FF");
}

public static class NCDanger
{
    public static readonly Color Color = Robust.Shared.Maths.Color.FromHex("#FF4258");
}

public static class NCWarning
{
    public static readonly Color Color = Robust.Shared.Maths.Color.FromHex("#FFC857");
}

public static class NCSuccess
{
    public static readonly Color Color = Robust.Shared.Maths.Color.FromHex("#58E58B");
}

public static class NCSelection
{
    public static readonly Color Color = Robust.Shared.Maths.Color.FromHex("#78FFF0");
}

public static class NCMotion
{
    public const float Fast = 0.06f;
    public const float Normal = 0.12f;
    public const float Slow = 0.22f;
}

public enum NcUiDevicePalette : byte
{
    Street,
    Ncpd,
    Militech,
    TraumaTeam,
    Biotechnica,
    Industrial,
    BlackMarket,
}

public readonly record struct NcUiDeviceTheme(
    Color Panel,
    Color Accent,
    Color SecondaryAccent,
    Color Text,
    Color Warning);

public static class NcUiDeviceThemes
{
    private static readonly NcUiDeviceTheme[] Themes =
    [
        new(Color.FromHex("#15120F"), Color.FromHex("#F2A541"), Color.FromHex("#B85C38"), Color.FromHex("#EFE1CB"), Color.FromHex("#FFCA5C")),
        new(Color.FromHex("#101820"), Color.FromHex("#4DA3FF"), Color.FromHex("#D83845"), Color.FromHex("#DCEEFF"), Color.FromHex("#FFD166")),
        new(Color.FromHex("#171B16"), Color.FromHex("#9DBA73"), Color.FromHex("#C4A35A"), Color.FromHex("#E8E7D4"), Color.FromHex("#E5C35A")),
        new(Color.FromHex("#122126"), Color.FromHex("#56D9E8"), Color.FromHex("#F5F7FA"), Color.FromHex("#E8FDFF"), Color.FromHex("#FF5E6C")),
        new(Color.FromHex("#101E19"), Color.FromHex("#68D391"), Color.FromHex("#9AE6B4"), Color.FromHex("#E2F7EA"), Color.FromHex("#F6C85F")),
        new(Color.FromHex("#191A1C"), Color.FromHex("#E2A84B"), Color.FromHex("#78828A"), Color.FromHex("#E5E1D8"), Color.FromHex("#FF704D")),
        new(Color.FromHex("#170D16"), Color.FromHex("#E84A9B"), Color.FromHex("#6E56CF"), Color.FromHex("#F4DDF0"), Color.FromHex("#F4B942")),
    ];

    public static NcUiDeviceTheme Get(NcUiDevicePalette palette) => Themes[(int) palette];
}
