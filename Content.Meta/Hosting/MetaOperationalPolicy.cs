// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
namespace Content.Meta.Hosting;

/// <summary>Bounded operator configuration. Malformed policy suspends execution.</summary>
public static class MetaOperationalPolicy
{
    public const int MaximumRevokedArtifacts = 64;
    public static bool TryParseRevokedArtifacts(string text, out HashSet<string> digests)
    {
        digests = new(StringComparer.OrdinalIgnoreCase);
        if (text.Length == 0) return true;
        if (text.Length > MaximumRevokedArtifacts * 65) return false;
        var entries = text.Split(',', StringSplitOptions.TrimEntries);
        if (entries.Length > MaximumRevokedArtifacts) return false;
        foreach (var digest in entries)
            if (digest.Length != 64 || !digest.All(char.IsAsciiHexDigit) || !digests.Add(digest)) return false;
        return true;
    }
}
