// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.AstraGraph;

/// <summary>
/// Maps a Night City admin title onto the rank tokens AstraGraph already understands.
/// <see cref="Developer"/> cannot publish. The AstraGraph flag alone does not grant publish.
/// </summary>
public static class AstraAdminRankMap
{
    public const string Developer = "Developer";
    public const string Publisher = "Publisher";
    public const string Maintainer = "Maintainer";

    public static string Map(string? title, bool isHost)
    {
        if (isHost)
            return Maintainer;

        if (string.IsNullOrWhiteSpace(title))
            return Developer;

        var rank = title.ToLowerInvariant();
        if (rank.Contains("head") || rank.Contains("lead") || rank.Contains("maintainer"))
            return Maintainer;

        if (rank.Contains("publisher"))
            return Publisher;

        return Developer;
    }
}
