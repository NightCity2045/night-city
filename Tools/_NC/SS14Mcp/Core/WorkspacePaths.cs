// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using NightCity.Diagnostics.Contracts;

namespace NightCity.Diagnostics.Core;

public sealed class WorkspacePaths
{
    public string Root { get; }
    public string ArtifactRoot { get; }

    public WorkspacePaths(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(Root))
            throw new DiagnosticException("invalid_workspace", "The workspace does not exist.");
        ArtifactRoot = Resolve("artifacts/ss14-mcp");
    }

    public string Resolve(string relativePath)
    {
        var absolute = Path.GetFullPath(relativePath, Root);
        if (!IsWithinRoot(absolute))
            throw new DiagnosticException("invalid_path", "The path must stay inside the selected workspace.");
        var cursor = absolute;
        while (!string.Equals(cursor, Root, PathComparison))
        {
            FileSystemInfo entry = Directory.Exists(cursor) ? new DirectoryInfo(cursor) : new FileInfo(cursor);
            if (entry.Exists && entry.LinkTarget != null)
            {
                var resolved = entry.ResolveLinkTarget(true);
                if (resolved == null || !IsWithinRoot(resolved.FullName))
                    throw new DiagnosticException("invalid_path", "The path contains a link outside the workspace.");
            }
            cursor = Path.GetDirectoryName(cursor) ?? Root;
        }
        return absolute;
    }

    private bool IsWithinRoot(string path) => string.Equals(path, Root, PathComparison) ||
        path.StartsWith(Root + Path.DirectorySeparatorChar, PathComparison);

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
