// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Meta.Hosting;

public static class MetaPaths
{
    /// <summary>One path policy is shared by admission, lookup, ACL prefixes and deletion.</summary>
    public static bool IsValid(string path)
    {
        if (path.Length is < 1 or > 192 || path[0] == '/' || path[^1] == '/')
            return false;
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
                return false;
            foreach (var c in segment)
            {
                if (c is not (>= 'a' and <= 'z') and not (>= 'A' and <= 'Z') and
                    not (>= '0' and <= '9') and not '_' and not '-' and not '.')
                    return false;
            }
        }
        return true;
    }

    public static bool IsWithin(string path, string prefix) => IsValid(path) &&
        (prefix.Length == 0 || IsValid(prefix) && (path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal)));
}
