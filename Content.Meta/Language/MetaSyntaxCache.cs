// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
namespace Content.Meta.Language;

/// <summary>Bounded renderer-independent syntax cache for revisioned IDE analysis. Compiler quotas remain per job.</summary>
public sealed class MetaSyntaxCache
{
    private readonly object _gate = new();
    private readonly Dictionary<(string FileId, string Revision), MetaParseResult> _entries = new();
    private readonly Queue<(string FileId, string Revision)> _order = new();
    public MetaParseResult Analyze(SourceText source, CancellationToken cancellation = default)
    {
        var key = (source.FileId, source.Revision);
        lock (_gate) if (_entries.TryGetValue(key, out var result)) return result;
        var parsed = MetaParser.Parse(source, cancellation: cancellation);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var result)) return result;
            while (_entries.Count >= 64) _entries.Remove(_order.Dequeue());
            _entries.Add(key, parsed); _order.Enqueue(key);
        }
        return parsed;
    }
    public void Invalidate(string fileId)
    {
        lock (_gate)
        {
            foreach (var key in _entries.Keys.Where(k => k.FileId == fileId).ToArray()) _entries.Remove(key);
            var retained = _order.Where(_entries.ContainsKey).ToArray(); _order.Clear();
            foreach (var key in retained) _order.Enqueue(key);
        }
    }
}
