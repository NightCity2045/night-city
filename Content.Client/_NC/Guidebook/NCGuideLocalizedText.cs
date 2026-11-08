// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Diagnostics.CodeAnalysis;
using Content.Client.Guidebook.Richtext;
using Content.Client.Message;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._NC.Guidebook;

/// <summary>Localized paragraphs for NC guide documents, using the client's selected locale.</summary>
public sealed class NCGuideLocalizedText : RichTextLabel, IDocumentTag
{
    public bool TryParseTag(Dictionary<string, string> args, [NotNullWhen(true)] out Control? control)
    {
        control = null;
        if (args.Count != 1 || !args.TryGetValue("Key", out var key) || !Loc.TryGetString(key, out var text))
            return false;
        HorizontalExpand = true;
        Margin = new Thickness(0, 0, 0, 15);
        this.SetMarkup(text);
        control = this;
        return true;
    }
}
