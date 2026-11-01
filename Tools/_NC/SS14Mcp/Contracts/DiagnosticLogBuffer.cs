// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text.RegularExpressions;

namespace NightCity.Diagnostics.Contracts;

public sealed record DiagnosticLogEntry(long Sequence, DateTimeOffset Timestamp, string Level, string Category, string Message);
public sealed record DiagnosticLogPage(DiagnosticLogEntry[] Entries, long NextCursor, long Dropped);

/// <summary>Thread-safe, bounded capture. It neither reads ECS nor logs recursively from a log callback.</summary>
public sealed partial class DiagnosticLogBuffer(int capacity = 2048)
{
    private readonly object _lock = new();
    private readonly Queue<DiagnosticLogEntry> _entries = new();
    private long _sequence;

    public DiagnosticLogEntry[] Snapshot()
    {
        lock (_lock) return _entries.ToArray();
    }

    public void Add(string level, string category, string message)
    {
        message = SecretPattern().Replace(message.Length > 4096 ? message[..4096] : message, "$1[redacted]");
        lock (_lock)
        {
            _entries.Enqueue(new DiagnosticLogEntry(++_sequence, DateTimeOffset.UtcNow, level, category, message));
            while (_entries.Count > capacity)
                _entries.Dequeue();
        }
    }

    public DiagnosticLogPage Read(long cursor, int limit = 100, string? level = null, string? category = null)
    {
        if (cursor < 0 || limit is < 1 or > 200)
            throw new DiagnosticException("invalid_arguments", "Invalid log cursor or page size.");
        lock (_lock)
        {
            var first = _entries.TryPeek(out var entry) ? entry.Sequence : _sequence + 1;
            var selected = _entries.Where(x => x.Sequence > cursor &&
                (level == null || string.Equals(x.Level, level, StringComparison.OrdinalIgnoreCase)) &&
                (category == null || x.Category.StartsWith(category, StringComparison.Ordinal)))
                .Take(limit).ToArray();
            // Advance past filtered entries when the page is exhausted, but never skip a matching later page.
            var next = selected.Length == limit ? selected[^1].Sequence : _sequence;
            return new DiagnosticLogPage(selected, next, Math.Max(0, first - cursor - 1));
        }
    }

    [GeneratedRegex("""(?i)((?:["']?)(?:[a-z0-9_-]*(?:token|password|secret|authorization|api[_-]?key|access[_-]?key))["']?\s*[=:]\s*)(?:"[^"\r\n]*"|'[^'\r\n]*'|(?:Bearer\s+)?[^\s,;]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();
}
