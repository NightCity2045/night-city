// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Immutable;
using System.Text;
using Content.Meta.Bytecode;

namespace Content.Meta.Language;

public sealed partial class MetaCompiler
{
    /// <summary>Runs the authoritative compiler binder on an immutable authorized source bundle. Never fetches dependencies.</summary>
    public MetaSemanticAnalysis AnalyzeSemantics(MetaCompilationRequest request, MetaSemanticCompletionPoint? completion = null,
        CancellationToken cancellation = default, IReadOnlyList<MetaSemanticLibraryHeader>? publicDependencies = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request with { Limits = null };
        if (publicDependencies is { Count: > 16 } || publicDependencies != null &&
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(publicDependencies).Length > MetaSemanticHeaders.MaximumBytes)
            throw new ArgumentException("Semantic public dependency quota exceeded.", nameof(publicDependencies));
        var files = request.Sources.Concat((request.Dependencies ?? []).Where(value => value.Authorized).SelectMany(value => value.Sources)).ToArray();
        if (files.Length is < 1 or > 64 || files.Any(value => value.Text.Length > 256 * 1024) ||
            files.Sum(value => (long)Encoding.UTF8.GetByteCount(value.Text)) > 1024 * 1024)
            throw new ArgumentException("Semantic analysis source quota exceeded.", nameof(request));
        var originals = files.Select(value => new SourceText(value.FileId, value.Text)).ToDictionary(value => value.FileId, StringComparer.Ordinal);
        (string File, int Offset, int Length)? inserted = null;
        if (completion is { } point && originals.TryGetValue(point.FileId, out var source) && source.Revision == point.Revision &&
            source.IsScalarBoundary(point.Utf8Offset) && source.Utf8.Length + 17 <= 256 * 1024)
        {
            var lexed = MetaLexer.Lex(source, cancellation: cancellation);
            var previous = lexed.Tokens.LastOrDefault(value => value.Kind != MetaTokenKind.End && value.Span.EndUtf8Byte <= point.Utf8Offset);
            if (previous?.Text == "." && source.Text.AsSpan(source.Utf16Offset(previous.Span.EndUtf8Byte),
                    source.Utf16Offset(point.Utf8Offset) - source.Utf16Offset(previous.Span.EndUtf8Byte)).IsWhiteSpace())
            {
                const string hole = "__meta_completion";
                inserted = (point.FileId, point.Utf8Offset, hole.Length);
                MetaSourceFile Patch(MetaSourceFile file) => file.FileId == point.FileId
                    ? file with { Text = source.Text.Insert(source.Utf16Offset(point.Utf8Offset), hole) } : file;
                request = request with { Sources = request.Sources.Select(Patch).ToArray(),
                    Dependencies = request.Dependencies?.Select(value => value with { Sources = value.Sources.Select(Patch).ToArray() }).ToArray() };
            }
        }
        var session = new Session(request, _catalog, cancellation, semantic: true);
        return session.AnalyzeSemanticResult(originals, inserted, publicDependencies ?? []);
    }

    private sealed partial class Session
    {
        private SemanticRecorder? _semantic;

        public MetaSemanticAnalysis AnalyzeSemanticResult(Dictionary<string, SourceText> originals, (string File, int Offset, int Length)? hole,
            IReadOnlyList<MetaSemanticLibraryHeader> publicDependencies)
        {
            _semantic!.Originals = originals; _semantic.Hole = hole;
            _semanticHeaders = publicDependencies;
            try
            {
                // Analysis binds real source bodies but never emits or returns executable code for header-only modules.
                LoadSources(); LoadBinaryLibraries(); LoadSemanticHeaderModules();
                DeclareTypes(); DeclareSemanticHeaderTypes(); BindImports(); DefineTypes(); DefineSemanticHeaderTypes();
                ValidateTypeGraphs(); DeclareValues(); DeclareSemanticHeaderValues(); ValidateExports();
                _semantic.DeclareAll(this);
                if (!HasErrors)
                {
                    foreach (var module in _modules.Values.Where(value => !value.Binary))
                        foreach (var constant in module.Globals.Values.Where(value => value.Storage == "const"))
                            try { EvaluateGlobalConstant(constant); } catch (SemanticAbortException) { }
                    var initializers = SortGlobalInitializers();
                    if (initializers.Count > 0 && _modules.TryGetValue(_request.RootModule, out var root))
                    {
                        var function = new FunctionSymbol(_functions.Count, root, "$initialize", false, [], 0, new([], root.Syntax.Span), [], root.Syntax.Span);
                        try { new Emitter(this, function, true).Initializers(initializers); } catch (SemanticAbortException) { }
                    }
                    foreach (var function in _functions.Where(value => !value.Module.Binary))
                        try { new Emitter(this, function, false).Body(); } catch (SemanticAbortException) { }
                }
            }
            catch (MetaCompilationAbortException) { }
            catch (Exception exception) when (exception is MetaFormatException or KeyNotFoundException or FormatException or OverflowException)
            { _budget.Report("E310", EmptySpan, "semantic-public-abi"); }
            return new(_semantic.Finish(this, !HasErrors), _budget.Diagnostics.Select(_semantic.MapDiagnostic).ToImmutableArray());
        }

        private sealed class SemanticRecorder
        {
            public Dictionary<string, SourceText> Originals = new(StringComparer.Ordinal);
            public (string File, int Offset, int Length)? Hole;
            private readonly Dictionary<string, MetaSemanticSymbol> _symbols = new(StringComparer.Ordinal);
            private readonly HashSet<MetaSemanticReference> _references = [];
            private readonly HashSet<MetaSemanticExpression> _expressions = [];
            private readonly Dictionary<string, IReadOnlyList<MetaToken>> _tokens = new(StringComparer.Ordinal);
            private bool _overflow;

            public void Source(SourceText source, IReadOnlyList<MetaToken> tokens) => _tokens[source.FileId] = tokens;
            private SourceSpan Map(SourceSpan span)
            {
                if (!Originals.TryGetValue(span.FileId, out var original)) return span;
                int Offset(int value) => Hole is { } hole && hole.File == span.FileId && value >= hole.Offset
                    ? Math.Max(hole.Offset, value - hole.Length) : value;
                return new(span.FileId, original.Revision, Offset(span.StartUtf8Byte), Offset(span.EndUtf8Byte));
            }
            public MetaDiagnostic MapDiagnostic(MetaDiagnostic value)
            {
                var span = Map(new(value.FileId, value.SourceRevision, value.StartUtf8Byte, value.EndUtf8Byte));
                return value with { SourceRevision = span.Revision, StartUtf8Byte = span.StartUtf8Byte, EndUtf8Byte = span.EndUtf8Byte,
                    Related = value.Related.Select(location => location with { Span = Map(location.Span) }).ToImmutableArray() };
            }
            private SourceSpan NameSpan(SourceSpan span, string name, bool last = false)
            {
                name = name[(name.LastIndexOf('.') + 1)..];
                if (!_tokens.TryGetValue(span.FileId, out var tokens)) return Map(span);
                _session?._budget.Cancellation.ThrowIfCancellationRequested();
                int LowerBound(int offset)
                {
                    var low = 0; var high = tokens.Count;
                    while (low < high)
                    { var middle = low + (high - low) / 2; if (tokens[middle].Span.StartUtf8Byte < offset) low = middle + 1; else high = middle; }
                    return low;
                }
                var start = LowerBound(span.StartUtf8Byte); var end = LowerBound(span.EndUtf8Byte);
                for (var index = last ? end - 1 : start; index >= start && index < end; index += last ? -1 : 1)
                    if (tokens[index].Span.EndUtf8Byte <= span.EndUtf8Byte && tokens[index].Text == name) return Map(tokens[index].Span);
                return Map(span);
            }
            private string Add(MetaSemanticSymbol symbol)
            {
                if (_symbols.ContainsKey(symbol.Id)) return symbol.Id;
                if (_symbols.Count >= MetaSemanticIndex.MaximumSymbols) { _overflow = true; return symbol.Id; }
                _symbols.Add(symbol.Id, symbol); return symbol.Id;
            }
            private static string Key(string kind, string name, SourceSpan? span = null) => kind + ":" + name + (span is { } value
                ? ":" + MetaApiCatalog.HashText(value.FileId + ":" + value.Revision + ":" + value.StartUtf8Byte) : "");
            private void Use(string id, SourceSpan span)
            {
                if (span.Length == 0) return;
                if (_references.Count >= MetaSemanticIndex.MaximumReferences) { _overflow = true; return; }
                _references.Add(new(id, span));
            }
            public void Expression(SourceSpan span, string type)
            {
                if (_expressions.Count >= MetaSemanticIndex.MaximumExpressions) { _overflow = true; return; }
                _expressions.Add(new(Map(span), type));
            }
            public string Variable(VariableSymbol variable, SourceSpan? scope = null, bool parameter = false, bool startsAtScope = false)
            {
                var definition = NameSpan(variable.Span, variable.Name);
                var kind = variable.Global ? MetaSemanticKind.Global : parameter ? MetaSemanticKind.Parameter : MetaSemanticKind.Local;
                var id = Key(variable.Global ? "global" : "local", variable.Module.Syntax.Module + "." + variable.Name, definition);
                var type = _session!._types[variable.Type].Name;
                var visibility = scope is { } range ? Map(range) with { StartUtf8Byte = parameter || startsAtScope ? Map(range).StartUtf8Byte :
                    Math.Max(Map(range).StartUtf8Byte, Map(variable.Span).EndUtf8Byte) } : (SourceSpan?)null;
                var exported = variable.Global && variable.Module.Values.GetValueOrDefault(variable.Name)?.Public == true;
                Add(new(id, variable.Name, variable.Module.Syntax.Module + "." + variable.Name, kind, type,
                    variable.Storage + " " + variable.Name + ":" + type, variable.Module.Syntax.Module, exported,
                    variable.Module.Binary ? null : definition, visibility));
                return id;
            }
            private Session? _session;
            public SourceSpan ParameterSpan(FunctionSymbol function, int index)
            {
                if (function.Module.Syntax.Declarations.OfType<MetaFunctionSyntax>().FirstOrDefault(value => value.Name == function.Name)
                    ?.Parameters.ElementAtOrDefault(index) is { } parameter) return parameter.Span;
                if (!_tokens.TryGetValue(function.Span.FileId, out var tokens)) return function.Span;
                var header = tokens.SkipWhile(value => value.Span.StartUtf8Byte < function.Span.StartUtf8Byte)
                    .TakeWhile(value => value.Span.StartUtf8Byte < function.Body.Span.StartUtf8Byte)
                    .SkipWhile(value => value.Text != "(").Skip(1).TakeWhile(value => value.Text != ")")
                    .Where(value => value.Kind == MetaTokenKind.Identifier).ElementAtOrDefault(index);
                return header?.Span ?? function.Span;
            }
            public void UseVariable(VariableSymbol variable, SourceSpan span) =>
                Use(Key(variable.Global ? "global" : "local", variable.Module.Syntax.Module + "." + variable.Name,
                    NameSpan(variable.Span, variable.Name)), NameSpan(span, variable.Name, last: true));
            public void UseLink(ModuleSymbol module, string name, SourceSpan span) => Use("link:" + module.Syntax.Module + "." + name, NameSpan(span, name));
            public void UseFunction(FunctionSymbol function, SourceSpan span) => Use("function:" + function.Module.Syntax.Module + "." + function.Name, NameSpan(span, function.Name, true));
            public void UseAlias(ModuleSymbol module, string name, SourceSpan span) => Use("import:" + module.Syntax.Module + "." + name, NameSpan(span, name));
            public void UseType(int type, SourceSpan span)
            {
                if (_session == null) return;
                var definition = _session._types[type];
                if (definition.Kind is MetaTypeKind.Array or MetaTypeKind.List or MetaTypeKind.Map or MetaTypeKind.Option or MetaTypeKind.Result) return;
                Use("type:" + definition.Name, NameSpan(span, definition.Name, true));
            }
            public void UseMember(string owner, string name, SourceSpan span) => Use("member:" + owner + "." + name, NameSpan(span, name, true));
            public void UseIntrinsic(MetaIntrinsicSignature intrinsic, SourceSpan span) =>
                Use("intrinsic:" + intrinsic.Module + "." + intrinsic.Name, NameSpan(span, intrinsic.Name, true));

            public void DeclareAll(Session session)
            {
                _session = session;
                foreach (var module in session._modules.Values.Where(value => !value.Binary))
                {
                    foreach (var import in module.Syntax.Imports)
                        Add(new("import:" + module.Syntax.Module + "." + import.Alias, import.Alias, import.Module,
                            MetaSemanticKind.Import, import.Module, "import " + import.Module + " as " + import.Alias,
                            module.Syntax.Module, false, NameSpan(import.Span, import.Alias, true)));
                    foreach (var link in module.Syntax.Declarations.OfType<MetaLinkSyntax>())
                        if (module.Links.TryGetValue(link.Name, out var index))
                        {
                            var type = session._types[session._links[index].CapabilityTypeId].Name;
                            Add(new("link:" + module.Syntax.Module + "." + link.Name, link.Name, module.Syntax.Module + "." + link.Name,
                                MetaSemanticKind.Link, type, "link " + link.Name + ":" + type, module.Syntax.Module, false, NameSpan(link.Span, link.Name)));
                        }
                }
                foreach (var function in session._functions.Where(value => !value.Name.StartsWith('$')))
                {
                    var visible = function.Module.Values.GetValueOrDefault(function.Name)?.Public == true;
                    if (function.Module.Binary && !visible) continue;
                    var type = session._types[function.ReturnType].Name;
                    Add(new("function:" + function.Module.Syntax.Module + "." + function.Name, function.Name,
                        function.Module.Syntax.Module + "." + function.Name, MetaSemanticKind.Function, type,
                        (function.Async ? "async " : "") + "fn " + function.Name + "(" + string.Join(",", function.Parameters.Select(value => session._types[value].Name)) + ")->" + type,
                        function.Module.Syntax.Module, visible, function.Module.Binary ? null : NameSpan(function.Span, function.Name)));
                }
                foreach (var module in session._modules.Values)
                    foreach (var global in module.Globals.Values)
                        if (!module.Binary || module.Values.GetValueOrDefault(global.Name)?.Public == true) Variable(global);
                Types(session);
                foreach (var module in session._modules.Values.Where(value => !value.Binary))
                    foreach (var declaration in module.Syntax.Declarations)
                    {
                        IEnumerable<MetaTypeSyntax> annotations = declaration switch
                        {
                            MetaFunctionSyntax function => function.Parameters.Select(value => value.Type).Append(function.ReturnType),
                            MetaStructSyntax structure => structure.Fields.Select(value => value.Type),
                            MetaGlobalSyntax global => [global.Type], MetaLinkSyntax link => [link.Type], _ => []
                        };
                        foreach (var annotation in annotations) session.ResolveType(module, annotation);
                    }
            }

            private void Types(Session session)
            {
                foreach (var keyword in MetaLexer.Keywords.Where(value => value is not
                    ("null" or "class" or "throw" or "catch" or "spawn" or "eval" or "unsafe" or "typeof" or "using" or
                     "void" or "bool" or "byte" or "int" or "float" or "string")))
                    Add(new("keyword:" + keyword, keyword, keyword, MetaSemanticKind.Keyword, "", keyword, "", true));
                foreach (var type in session._types)
                {
                    session._budget.Cancellation.ThrowIfCancellationRequested();
                    if (session._binaryTypeNames.Contains(type.Name) && !session._binaryPublicTypes.Contains(type.Name)) continue;
                    var split = type.Name.LastIndexOf('.');
                    var module = split < 0 ? "" : type.Name[..split];
                    var name = split < 0 ? type.Name : type.Name[(split + 1)..];
                    SourceSpan? definition = null;
                    var visible = true;
                    if (session._userTypes.TryGetValue(type.Name, out var entry))
                    { definition = NameSpan(entry.Syntax.Span, entry.Syntax.Name); visible = entry.Syntax.Public; module = entry.Module.Syntax.Module; }
                    Add(new("type:" + type.Name, name, type.Name, MetaSemanticKind.Type, type.Name,
                        type.Name, module, visible, definition));
                    foreach (var field in type.Fields)
                    {
                        SourceSpan? fieldSpan = null;
                        if (entry.Syntax is MetaStructSyntax structure && structure.Fields.FirstOrDefault(value => value.Name == field.Name) is { } syntax)
                            fieldSpan = NameSpan(syntax.Span, field.Name);
                        var fieldType = session._types[field.TypeId].Name;
                        Add(new("member:" + type.Name + "." + field.Name, field.Name, type.Name + "." + field.Name, MetaSemanticKind.Field,
                            fieldType, field.Name + ":" + fieldType, module, visible, fieldSpan, OwnerType: type.Name));
                    }
                    foreach (var variant in type.Variants)
                        Add(new("member:" + type.Name + "." + variant.Name, variant.Name, type.Name + "." + variant.Name, MetaSemanticKind.Variant,
                            type.Name, type.Name + "." + variant.Name, module, visible,
                            entry.Syntax is MetaEnumSyntax enumeration ? NameSpan(enumeration.Span, variant.Name, true) : null, OwnerType: type.Name));
                    void CollectionMember(string member, string result, params string[] arguments) => Add(new("member:" + type.Name + "." + member,
                        member, type.Name + "." + member, member == "length" ? MetaSemanticKind.Field : MetaSemanticKind.Method,
                        result, member + (member == "length" ? ":" + result : "(" + string.Join(",", arguments) + ")->" + result),
                        module, true, OwnerType: type.Name));
                    if (type.Kind is MetaTypeKind.Array or MetaTypeKind.List or MetaTypeKind.String) CollectionMember("length", "int");
                    if (type.Kind == MetaTypeKind.List)
                    { var item = session._types[type.Args[0]].Name; CollectionMember("push", "void", item); CollectionMember("pop", "option<" + item + ">"); }
                    if (type.Kind == MetaTypeKind.Map)
                    {
                        var key = session._types[type.Args[0]].Name; var value = session._types[type.Args[1]].Name;
                        CollectionMember("get", "option<" + value + ">", key); CollectionMember("set", "void", key, value);
                        CollectionMember("remove", "bool", key); CollectionMember("keys", "array<" + key + ">");
                    }
                }
                foreach (var member in session._catalog.Members)
                    Add(new("member:" + member.QualifiedName, member.Name, member.QualifiedName, MetaSemanticKind.Method,
                        member.ReturnType, member.Name + "(" + string.Join(",", member.Parameters) + ")->" + member.ReturnType + " [" + member.Scope + "]",
                        member.Owner[..member.Owner.LastIndexOf('.')], true, OwnerType: member.Owner));
                foreach (var intrinsic in session._catalog.Intrinsics)
                    Add(new("intrinsic:" + intrinsic.Module + "." + intrinsic.Name, intrinsic.Name, intrinsic.Module + "." + intrinsic.Name,
                        MetaSemanticKind.Intrinsic, intrinsic.ReturnType,
                        intrinsic.Name + "(" + string.Join(",", intrinsic.Parameters) + ")->" + intrinsic.ReturnType, intrinsic.Module, true));
            }

            public MetaSemanticIndex Finish(Session session, bool complete)
            {
                if (_session == null) DeclareAll(session);
                Types(session);
                var documents = session._modules.Values.Where(value => !value.Binary && Originals.ContainsKey(value.Source.FileId)).Select(module =>
                {
                    var original = Originals[module.Source.FileId];
                    var tokens = original.Revision == module.Source.Revision && _tokens.TryGetValue(original.FileId, out var cached)
                        ? cached : MetaLexer.Lex(original, cancellation: session._budget.Cancellation).Tokens;
                    return (original, module.Syntax.Module, tokens, (IReadOnlyDictionary<string, string>)module.Aliases);
                });
                return new(_symbols.Values, _references, _expressions, documents, complete && !_overflow);
            }
        }
    }
}
