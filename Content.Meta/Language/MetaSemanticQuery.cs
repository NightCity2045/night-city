// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;

namespace Content.Meta.Language;

public sealed record MetaSemanticQuery(int Kind, string File, string Revision, int Utf8Offset,
    MetaSemanticLibraryHeader[] Dependencies);
public sealed record MetaSemanticItem(string Name, string Signature, SourceSpan? Location);
public sealed record MetaSemanticReply(string Signature, MetaSemanticItem[] Items, MetaDiagnostic[] Diagnostics);

public static class MetaSemanticQueryRunner
{
    /// <summary>Runs only in the bounded compiler worker. Output contains public metadata and submitted-source locations.</summary>
    public static MetaSemanticReply Run(MetaCompilationRequest request, MetaSemanticQuery query, CancellationToken cancellation)
    {
        if (query.Kind is < 0 or > 3 || query.File.Length > 192 || query.Revision.Length != 64 ||
            query.Utf8Offset is < 0 or > 256 * 1024 || query.Dependencies.Length > 16)
            throw new ArgumentException("Invalid semantic query.");
        var selected = request.Sources.SingleOrDefault(file => file.FileId == query.File);
        if (selected == null || new SourceText(selected.FileId, selected.Text).Revision != query.Revision)
            throw new ArgumentException("Semantic source revision changed.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(TimeSpan.FromMilliseconds(750));
        var analysis = new MetaCompiler().AnalyzeSemantics(request, query.Kind == 1
            ? new(query.File, query.Revision, query.Utf8Offset) : null, limit.Token, query.Dependencies);
        var items = new List<MetaSemanticItem>(); var signature = "";
        var sourceFiles = request.Sources.Select(file => file.FileId).ToHashSet(StringComparer.Ordinal);
        if (query.Kind == 0)
        {
            foreach (var symbol in analysis.Index.Symbols.Where(symbol => symbol.Definition?.FileId == query.File &&
                         symbol.Kind is not (MetaSemanticKind.Local or MetaSemanticKind.Parameter or MetaSemanticKind.Field or MetaSemanticKind.Variant)).Take(128))
                items.Add(new(symbol.Name, symbol.Signature, symbol.Definition));
        }
        else if (query.Kind == 1)
        {
            foreach (var symbol in analysis.Index.CompleteAt(query.File, query.Revision, query.Utf8Offset).Take(128))
                items.Add(new(symbol.Name, symbol.Signature, null));
        }
        else if (analysis.Index.SymbolAt(query.File, query.Revision, query.Utf8Offset) is { } bound)
        {
            signature = bound.Signature;
            IEnumerable<SourceSpan> locations = query.Kind == 3 ? analysis.Index.ReferencesTo(bound.Id) :
                bound.Definition is { } definition ? new[] { definition } : Array.Empty<SourceSpan>();
            foreach (var location in locations.Where(location => sourceFiles.Contains(location.FileId)).Take(128))
                items.Add(new(bound.Name, bound.Signature, location));
        }
        var diagnostics = analysis.Diagnostics.Where(diagnostic => sourceFiles.Contains(diagnostic.FileId)).Take(100).ToArray();
        var result = new MetaSemanticReply(signature, items.ToArray(), diagnostics);
        // IPC has a larger cap; semantic UI responses retain their original 64 KiB bound.
        while (JsonSerializer.SerializeToUtf8Bytes(result, MetaCompilerProtocol.JsonOptions).Length > 65536)
        {
            if (result.Items.Length != 0) result = result with { Items = result.Items[..^1] };
            else if (result.Diagnostics.Length != 0) result = result with { Diagnostics = result.Diagnostics[..^1] };
            else throw new ArgumentException("Semantic response exceeds policy.");
            cancellation.ThrowIfCancellationRequested();
        }
        return result;
    }
}
