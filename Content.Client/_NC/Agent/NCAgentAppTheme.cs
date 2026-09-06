// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Utility;

namespace Content.Client._NC.Agent;

/// <summary>Shared presentation for the Agent's contract and fixer screens.</summary>
internal static class NCAgentAppTheme
{
    public static void Apply(Control control)
    {
        // Match Agent actions while preserving the standard hover and disabled feedback.
        if (control is Button button)
            button.Label.FontColorOverride = Color.FromHex("#77FFF2");
        foreach (var child in control.Children)
            Apply(child);
    }

    public static ContainerButton CreateEntry(string title, string status)
    {
        var button = new ContainerButton
        {
            HorizontalExpand = true,
            Margin = new Thickness(0, 3),
            StyleBoxOverride = new StyleBoxFlat
            {
                BackgroundColor = Color.FromHex("#0B1E24"),
                BorderColor = Color.FromHex("#245C59"),
                BorderThickness = new Thickness(1),
            },
        };
        var message = new FormattedMessage();
        message.PushColor(Color.FromHex("#D7FFFB"));
        message.AddText(title);
        message.Pop();
        message.PushNewline();
        message.PushColor(Color.FromHex("#6C918E"));
        message.AddText(status);
        message.Pop();
        var label = new RichTextLabel { Margin = new Thickness(8, 6) };
        label.SetMessage(message);
        button.AddChild(label);
        return button;
    }
}
