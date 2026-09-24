// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.IO;

namespace Content.Shared._NC.AstraGraph;

/// <summary>
/// Finds the Night City checkout that owns AstraGraph and RobustToolbox.
/// Runtime data must not follow <see cref="AppContext.BaseDirectory"/>.
/// </summary>
public static class AstraConsumerLayout
{
    public const string ManifestFileName = "Compatibility.json";
    public const string ProjectRelativePath = "Resources/AstraGraph";
    public const string DataRelativePath = "data/AstraGraph";

    public static bool TryLocate(out string consumerRoot)
    {
        return TryLocate([Directory.GetCurrentDirectory(), AppContext.BaseDirectory], out consumerRoot);
    }

    public static bool TryLocate(IEnumerable<string> startPaths, out string consumerRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in startPaths)
        {
            if (string.IsNullOrWhiteSpace(start))
                continue;

            DirectoryInfo? cursor;
            try
            {
                cursor = new DirectoryInfo(Path.GetFullPath(start));
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                continue;
            }

            for (var depth = 0; depth < 8 && cursor != null; depth++)
            {
                if (!seen.Add(cursor.FullName))
                    break;

                var manifest = Path.Combine(cursor.FullName, "AstraGraph", ManifestFileName);
                var robust = Path.Combine(cursor.FullName, "RobustToolbox");
                if (File.Exists(manifest) && Directory.Exists(robust))
                {
                    consumerRoot = cursor.FullName;
                    return true;
                }

                cursor = cursor.Parent;
            }
        }

        consumerRoot = string.Empty;
        return false;
    }

    /// <summary>
    /// Reads a checkout commit. Submodule <c>.git</c> files are followed via <c>gitdir:</c>.
    /// </summary>
    public static bool TryReadGitCommit(string repositoryPath, out string commit)
    {
        commit = string.Empty;
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
            return false;

        if (!TryResolveGitDirectory(repositoryPath, out var gitDirectory))
            return false;

        var headPath = Path.Combine(gitDirectory, "HEAD");
        if (!File.Exists(headPath))
            return false;

        var head = File.ReadAllText(headPath).Trim();
        if (head.StartsWith("ref:", StringComparison.Ordinal))
        {
            var refPath = head["ref:".Length..].Trim().Replace('/', Path.DirectorySeparatorChar);
            var refFile = Path.Combine(gitDirectory, refPath);
            if (!File.Exists(refFile))
                return false;

            head = File.ReadAllText(refFile).Trim();
        }

        if (head.Length < 7)
            return false;

        commit = head;
        return true;
    }

    private static bool TryResolveGitDirectory(string repositoryPath, out string gitDirectory)
    {
        gitDirectory = string.Empty;
        var gitPath = Path.Combine(repositoryPath, ".git");
        if (Directory.Exists(gitPath))
        {
            gitDirectory = gitPath;
            return true;
        }

        if (!File.Exists(gitPath))
            return false;

        var text = File.ReadAllText(gitPath).Trim();
        const string prefix = "gitdir:";
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var relative = text[prefix.Length..].Trim();
        gitDirectory = Path.GetFullPath(Path.IsPathRooted(relative)
            ? relative
            : Path.Combine(repositoryPath, relative));
        return Directory.Exists(gitDirectory);
    }
}
