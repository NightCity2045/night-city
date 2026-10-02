// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    /// <summary>
    /// Enables automatic translation of incoming player chat for this client.
    /// </summary>
    public static readonly CVarDef<bool> NCChatTranslationPreferenceEnabled =
        CVarDef.Create("nc.chat_translation.preference.enabled", true, CVar.CLIENT | CVar.REPLICATED | CVar.ARCHIVE);

    /// <summary>
    /// Preferred translation language. Empty follows the selected game language.
    /// </summary>
    public static readonly CVarDef<string> NCChatTranslationPreferenceLanguage =
        CVarDef.Create("nc.chat_translation.preference.language", string.Empty, CVar.CLIENT | CVar.REPLICATED | CVar.ARCHIVE);

    /// <summary>
    /// Mirrors the selected client culture to the server for per-recipient presentation.
    /// </summary>
    public static readonly CVarDef<string> NCClientCulture =
        CVarDef.Create("nc.localization.culture", "en-US", CVar.CLIENT | CVar.REPLICATED);

    public static readonly CVarDef<bool> NCChatTranslationEnabled =
        CVarDef.Create("nc.chat_translation.enabled", false, CVar.SERVERONLY);

    public static readonly CVarDef<string> NCChatTranslationServiceUrl =
        CVarDef.Create("nc.chat_translation.service_url", string.Empty, CVar.SERVERONLY);

    public static readonly CVarDef<string> NCChatTranslationApiKey =
        CVarDef.Create("nc.chat_translation.api_key", string.Empty, CVar.SERVERONLY);

    public static readonly CVarDef<int> NCChatTranslationTimeoutMs =
        CVarDef.Create("nc.chat_translation.timeout_ms", 450, CVar.SERVERONLY);

    public static readonly CVarDef<int> NCChatTranslationSoftHoldMs =
        CVarDef.Create("nc.chat_translation.soft_hold_ms", 75, CVar.SERVERONLY);

    public static readonly CVarDef<int> NCChatTranslationFailureBackoffSeconds =
        CVarDef.Create("nc.chat_translation.failure_backoff_seconds", 5, CVar.SERVERONLY);

    public static readonly CVarDef<int> NCChatTranslationMaxMessageLength =
        CVarDef.Create("nc.chat_translation.max_message_length", 240, CVar.SERVERONLY);

    public static readonly CVarDef<int> NCChatTranslationCacheTtlSeconds =
        CVarDef.Create("nc.chat_translation.cache_ttl_seconds", 1800, CVar.SERVERONLY);

    public static readonly CVarDef<int> NCChatTranslationCacheMaxEntries =
        CVarDef.Create("nc.chat_translation.cache_max_entries", 4096, CVar.SERVERONLY);

    /// <summary>
    /// Player-authored channels eligible for translation. AHelp is handled by its own secure route.
    /// </summary>
    public static readonly CVarDef<string> NCChatTranslationChannels =
        CVarDef.Create(
            "nc.chat_translation.channels",
            "local,whisper,radio,looc,dead,ooc,emotes,admin,ahelp",
            CVar.SERVERONLY);
}
