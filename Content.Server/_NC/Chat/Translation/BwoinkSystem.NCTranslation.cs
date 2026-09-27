// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Chat.Translation;
using Content.Shared.CCVar;
using Content.Shared._NC.Chat.Translation;
using Robust.Shared.Configuration;
using Robust.Shared.Player;

namespace Content.Server.Administration.Systems;

public sealed partial class BwoinkSystem
{
    [Dependency] private INCChatTranslationService _ncAHelpTranslation = default!;
    [Dependency] private INetConfigurationManager _ncAHelpNetConfig = default!;

    private string ResolveNCAHelpVisibleText(
        ICommonSession recipient,
        string originalText,
        NCChatTranslationPayload? translation,
        out string? languageTag)
    {
        languageTag = null;
        if (translation == null ||
            !_ncAHelpNetConfig.GetClientCVar(recipient.Channel, CCVars.NCChatTranslationPreferenceEnabled))
        {
            return originalText;
        }

        var preferred = _ncAHelpNetConfig.GetClientCVar(
            recipient.Channel,
            CCVars.NCChatTranslationPreferenceLanguage);
        var target = NCChatTranslationMarkup.NormalizeLanguageCode(preferred);
        if (target == null)
        {
            var culture = _ncAHelpNetConfig.GetClientCVar(recipient.Channel, CCVars.NCClientCulture);
            target = NCChatTranslationMarkup.NormalizeLanguageCode(culture);
        }

        if (target == null)
            return originalText;

        var visible = translation.GetVisibleText(target);
        if (!string.Equals(visible, originalText, StringComparison.Ordinal))
            languageTag = NCChatTranslationMarkup.BuildTagMarkup(translation.SourceLanguage, originalText);

        return visible;
    }
}
