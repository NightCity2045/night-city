// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Diagnostics.CodeAnalysis;
using Content.Shared._NC.Chat.Translation;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.RichText;
using Robust.Shared.Utility;

namespace Content.Client._NC.Chat.Translation;

/// <summary>
/// Renders the compact source-language badge and exposes the original line as a tooltip.
/// </summary>
[UsedImplicitly]
public sealed class NCChatTranslationTag : IMarkupTagHandler
{
    private static readonly Color RussianColor = Color.FromHex("#C98C8C");
    private static readonly Color EnglishColor = Color.FromHex("#7FAEDC");

    public string Name => NCChatTranslationMarkup.TagName;

    public bool TryCreateControl(MarkupNode node, [NotNullWhen(true)] out Control? control)
    {
        control = null;
        if (!node.Attributes.TryGetValue("lang", out var languageParameter) ||
            !languageParameter.TryGetString(out var rawLanguage))
        {
            return false;
        }

        var language = NCChatTranslationMarkup.NormalizeLanguageCode(rawLanguage);
        if (language == null)
            return false;

        string? tooltip = null;
        if (node.Attributes.TryGetValue("original", out var originalParameter) &&
            originalParameter.TryGetString(out var encodedOriginal) &&
            NCChatTranslationMarkup.TryDecodeOriginalText(encodedOriginal, out var original) &&
            !string.IsNullOrWhiteSpace(original))
        {
            tooltip = original;
        }

        control = new Label
        {
            Text = $"[{language}]",
            ToolTip = tooltip,
            MouseFilter = Control.MouseFilterMode.Stop,
            FontColorOverride = language == NCChatTranslationMarkup.RussianLanguageCode ? RussianColor : EnglishColor,
            Margin = new Thickness(0f, 0f, 6f, 0f),
        };
        return true;
    }
}
