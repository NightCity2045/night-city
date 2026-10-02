// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text;

namespace Content.Shared._NC.Chat.Translation;

/// <summary>
/// Shared RU/EN language helpers and the compact rich-text marker used for translated chat.
/// </summary>
public static class NCChatTranslationMarkup
{
    public const string RussianLanguageCode = "RU";
    public const string EnglishLanguageCode = "EN";
    public const string TagName = "ncchatlang";

    public static string? NormalizeLanguageCode(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
            return null;

        var normalized = languageCode.Trim();
        if (normalized.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
            return RussianLanguageCode;
        if (normalized.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return EnglishLanguageCode;

        return null;
    }

    public static string BuildTagMarkup(string sourceLanguage, string originalText)
    {
        var language = NormalizeLanguageCode(sourceLanguage) ?? EnglishLanguageCode;
        return $"[{TagName} lang=\"{language}\" original=\"{EncodeOriginalText(originalText)}\"][/{TagName}]";
    }

    public static string EncodeOriginalText(string text)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryDecodeOriginalText(string? encoded, out string decoded)
    {
        decoded = string.Empty;
        if (string.IsNullOrWhiteSpace(encoded))
            return false;

        try
        {
            var padded = encoded.Replace('-', '+').Replace('_', '/');
            if (padded.Length % 4 != 0)
                padded = padded.PadRight(padded.Length + 4 - padded.Length % 4, '=');

            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
