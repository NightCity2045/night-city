// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Immutable;

namespace Content.Meta.Language;

public enum MetaSemanticKind : byte { Import, Type, Field, Variant, Function, Global, Local, Parameter, Link, Intrinsic, Method, Keyword }

public sealed record MetaSemanticSymbol(string Id, string Name, string QualifiedName, MetaSemanticKind Kind,
    string TypeName, string Signature, string Module, bool Public, SourceSpan? Definition = null,
    SourceSpan? Visibility = null, string? OwnerType = null);
public sealed record MetaSemanticReference(string SymbolId, SourceSpan Span);
public sealed record MetaSemanticExpression(SourceSpan Span, string TypeName);
public sealed record MetaSemanticDocument(string FileId, string Revision, string Module, int Bytes);
public sealed record MetaSemanticCompletionPoint(string FileId, string Revision, int Utf8Offset);
public sealed record MetaSemanticAnalysis(MetaSemanticIndex Index, IReadOnlyList<MetaDiagnostic> Diagnostics);

/// <summary>Immutable, bounded compiler binding facts. It has no device lookup, source fetch, worker or world authority.</summary>
public sealed class MetaSemanticIndex
{
    public const int MaximumSymbols = 8192;
    public const int MaximumReferences = 65536;
    public const int MaximumExpressions = 32768;
    private readonly ImmutableDictionary<string, SourceText> _sources;
    private readonly ImmutableDictionary<string, ImmutableArray<MetaToken>> _tokens;
    private readonly ImmutableDictionary<string, MetaSemanticSymbol> _symbols;
    private readonly ImmutableDictionary<string, ImmutableDictionary<string, string>> _aliases;
    private readonly ImmutableArray<MetaSemanticExpression> _expressions;
    public ImmutableArray<MetaSemanticDocument> Documents { get; }
    public ImmutableArray<MetaSemanticSymbol> Symbols { get; }
    public ImmutableArray<MetaSemanticReference> References { get; }
    public bool Complete { get; }

    internal MetaSemanticIndex(IEnumerable<MetaSemanticSymbol> symbols, IEnumerable<MetaSemanticReference> references,
        IEnumerable<MetaSemanticExpression> expressions, IEnumerable<(SourceText Source, string Module, IReadOnlyList<MetaToken> Tokens,
            IReadOnlyDictionary<string, string> Aliases)> documents, bool complete)
    {
        Symbols = symbols.Take(MaximumSymbols).ToImmutableArray();
        References = references.Take(MaximumReferences).ToImmutableArray();
        _expressions = expressions.Take(MaximumExpressions).ToImmutableArray();
        _symbols = Symbols.ToImmutableDictionary(value => value.Id, StringComparer.Ordinal);
        var files = documents.Take(64).ToArray();
        Documents = files.Select(value => new MetaSemanticDocument(value.Source.FileId, value.Source.Revision,
            value.Module, value.Source.Utf8.Length)).ToImmutableArray();
        _sources = files.ToImmutableDictionary(value => value.Source.FileId, value => value.Source, StringComparer.Ordinal);
        _tokens = files.ToImmutableDictionary(value => value.Source.FileId, value => value.Tokens.ToImmutableArray(), StringComparer.Ordinal);
        _aliases = files.ToImmutableDictionary(value => value.Source.FileId,
            value => value.Aliases.ToImmutableDictionary(StringComparer.Ordinal), StringComparer.Ordinal);
        Complete = complete;
    }

    public bool IsCurrent(string fileId, string revision) => _sources.TryGetValue(fileId, out var source) && source.Revision == revision;

    public MetaSemanticSymbol? SymbolAt(string fileId, string revision, int utf8Offset)
    {
        if (!IsCurrent(fileId, revision)) return null;
        bool Contains(SourceSpan span, int at) => span.FileId == fileId && span.Revision == revision &&
            span.StartUtf8Byte <= at && at < span.EndUtf8Byte;
        for (var shift = 0; shift <= 1 && utf8Offset >= shift; shift++)
        {
            var at = utf8Offset - shift;
            var reference = References.Where(value => Contains(value.Span, at)).MinBy(value => value.Span.Length);
            if (reference != null && _symbols.TryGetValue(reference.SymbolId, out var bound)) return bound;
            var declaration = Symbols.Where(value => value.Definition is { } span && Contains(span, at))
                .MinBy(value => value.Definition!.Value.Length);
            if (declaration != null) return declaration;
        }
        return null;
    }

    public ImmutableArray<SourceSpan> ReferencesTo(string symbolId, int maximum = 128) => !_symbols.ContainsKey(symbolId) ? [] :
        References.Where(value => value.SymbolId == symbolId).Select(value => value.Span).Distinct()
            .OrderBy(value => value.FileId, StringComparer.Ordinal).ThenBy(value => value.StartUtf8Byte)
            .Take(Math.Clamp(maximum, 0, 128)).ToImmutableArray();

    public ImmutableArray<MetaSemanticSymbol> CompleteAt(string fileId, string revision, int utf8Offset, int maximum = 64)
    {
        if (!IsCurrent(fileId, revision) || !_sources.TryGetValue(fileId, out var source) ||
            !source.IsScalarBoundary(utf8Offset)) return [];
        var document = Documents.First(value => value.FileId == fileId);
        var tokens = _tokens[fileId];
        var index = -1;
        for (var i = 0; i < tokens.Length && tokens[i].Span.StartUtf8Byte < utf8Offset; i++)
            if (tokens[i].Kind != MetaTokenKind.End) index = i;
        var prefix = "";
        if (index >= 0 && tokens[index].Kind == MetaTokenKind.Identifier && tokens[index].Span.EndUtf8Byte >= utf8Offset)
        {
            var token = tokens[index];
            prefix = source.Text[source.Utf16Offset(token.Span.StartUtf8Byte)..source.Utf16Offset(utf8Offset)];
            index--;
        }
        IEnumerable<MetaSemanticSymbol> candidates;
        if (index >= 0 && tokens[index].Text == ".")
        {
            var dot = tokens[index];
            var expression = _expressions.Where(value => value.Span.FileId == fileId && value.Span.Revision == revision &&
                    value.Span.EndUtf8Byte <= dot.Span.StartUtf8Byte)
                .OrderByDescending(value => value.Span.EndUtf8Byte).ThenByDescending(value => value.Span.Length).FirstOrDefault();
            if (expression != null && OnlyWhitespace(source, expression.Span.EndUtf8Byte, dot.Span.StartUtf8Byte)) candidates = Members(expression.TypeName);
            else
            {
                var names = new List<string>();
                for (var i = index - 1; i >= 0 && names.Count < 16; i -= 2)
                {
                    if (tokens[i].Kind != MetaTokenKind.Identifier) break;
                    names.Insert(0, tokens[i].Text);
                    if (i == 0 || tokens[i - 1].Text != ".") break;
                }
                candidates = CompleteReceiver(names, fileId, document.Module, utf8Offset);
            }
        }
        else candidates = Visible(fileId, document.Module, utf8Offset);
        return candidates.Where(value => value.Name.StartsWith(prefix, StringComparison.Ordinal) && value.Name != prefix)
            .DistinctBy(value => value.Id).OrderBy(value => value.Name, StringComparer.Ordinal).ThenBy(value => value.Id, StringComparer.Ordinal)
            .Take(Math.Clamp(maximum, 0, 64)).ToImmutableArray();
    }

    private IEnumerable<MetaSemanticSymbol> CompleteReceiver(List<string> names, string file, string module, int offset)
    {
        if (names.Count == 0) return [];
        if (_aliases[file].TryGetValue(names[0], out var imported))
        {
            if (names.Count == 1) return Symbols.Where(value => value.Module == imported && value.Public && value.OwnerType == null &&
                value.Kind is MetaSemanticKind.Type or MetaSemanticKind.Function or MetaSemanticKind.Global or MetaSemanticKind.Intrinsic);
            var nominal = imported + "." + names[1];
            if (!Symbols.Any(value => value.Kind == MetaSemanticKind.Type && value.QualifiedName == nominal && value.Public)) return [];
            return names.Count == 2 ? Members(nominal) : [];
        }
        var first = Visible(file, module, offset).Where(value => value.Name == names[0]).ToArray();
        if (first.Length != 1) return [];
        var type = first[0].TypeName;
        foreach (var field in names.Skip(1))
        {
            var match = Members(type).Where(value => value.Name == field && value.Kind == MetaSemanticKind.Field).ToArray();
            if (match.Length != 1) return [];
            type = match[0].TypeName;
        }
        return Members(type);
    }

    private IEnumerable<MetaSemanticSymbol> Members(string type) => Symbols.Where(value => value.OwnerType == type);
    private IEnumerable<MetaSemanticSymbol> Visible(string file, string module, int offset)
    {
        var imports = _aliases[file].Values.ToHashSet(StringComparer.Ordinal);
        return Symbols.Where(value => value.OwnerType == null && (value.Visibility is { } scope
            ? scope.FileId == file && scope.StartUtf8Byte <= offset && offset < scope.EndUtf8Byte
            : value.Module == module || value.Module.Length == 0 && value.Kind is MetaSemanticKind.Type or MetaSemanticKind.Keyword ||
                value.Public && imports.Contains(value.Module) && value.Kind == MetaSemanticKind.Type));
    }

    private static bool OnlyWhitespace(SourceText source, int start, int end) => start <= end &&
        source.Text.AsSpan(source.Utf16Offset(start), source.Utf16Offset(end) - source.Utf16Offset(start)).IsWhiteSpace();
}
