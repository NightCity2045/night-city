// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.AstraGraph;

/// <summary>
/// Studio UI documents live in the game content, next to the other AstraGraph files.
/// </summary>
public static class AstraHtmlDocumentPaths
{
    public static string? File(string documentName)
    {
        if (string.IsNullOrWhiteSpace(documentName) || !AstraConsumerLayout.TryLocate(out var root))
            return null;

        var safe = FileName(documentName);
        return Path.Combine(root, "Resources", "AstraGraph", "Ui", safe + ".json");
    }

    public static string ResourcePath(string documentName) => "/AstraGraph/Ui/" + FileName(documentName) + ".json";

    public static string FileName(string documentName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = documentName.Trim().Select(ch => Array.IndexOf(invalid, ch) >= 0 ? '-' : ch).ToArray();
        var name = new string(chars).Trim().Trim('.');
        return name.Length == 0 ? "ui" : name;
    }
}
