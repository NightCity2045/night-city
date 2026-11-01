// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Globalization;
using System.Numerics;
using System.Text;
using Content.Meta.Bytecode;

namespace Content.Meta.Language;

public sealed record MetaSourceFile(string FileId, string Text);
public sealed record MetaDependencyLock(string PackageIdentity, string Version, string ContentHash,
    IReadOnlyList<MetaSourceFile> Sources, bool Authorized = true);
public sealed record MetaCompilationRequest(IReadOnlyList<MetaSourceFile> Sources, string RootModule,
    string PackageName = "user.program", string PackageVersion = "1.0.0",
    IReadOnlyList<MetaDependencyLock>? Dependencies = null, MetaCompilerLimits? Limits = null,
    IReadOnlyList<MetaBinaryLibrary>? BinaryDependencies = null, bool Library = false);
public sealed record MetaIrInstruction(MetaInstruction Instruction, int ResultType, SourceSpan Span, MetaEffectFlags Effects);
public sealed record MetaFunctionAnalysis(int FunctionId, MetaEffectFlags Effects, IReadOnlyList<int> Calls);
public sealed record MetaCompilationResult(byte[]? Artifact, string? ArtifactHash, string CacheIdentity,
    IReadOnlyList<MetaDiagnostic> Diagnostics, MetaModule? Module,
    IReadOnlyList<MetaFunctionAnalysis> Analysis, MetaExportCertificate? Exports = null)
{
    public bool Success => Artifact != null && Diagnostics.All(d => d.Severity != MetaDiagnosticSeverity.Error);
}

/// <summary>Bounded, deterministic compiler. It has no ECS, filesystem, network or CLR evaluation entrypoint.</summary>
public sealed partial class MetaCompiler
{
    public const string BuildId = "nc-meta-1.0.0";
    private readonly MetaApiCatalog _catalog;
    public MetaCompiler(MetaApiCatalog? catalog = null) => _catalog = catalog ?? MetaApiCatalog.Default;

    public static string? InferRootModule(MetaSourceFile source, CancellationToken cancellation = default)
        => MetaParser.Parse(new SourceText(source.FileId, source.Text), cancellation: cancellation).Unit?.Module;

    public MetaCompilationResult Compile(MetaCompilationRequest request, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = new Session(request, _catalog, cancellation);
        return session.Compile();
    }

    private sealed partial class Session
    {
        private readonly MetaCompilationRequest _request;
        private readonly MetaApiCatalog _catalog;
        private readonly CompilationBudget _budget;
        private readonly List<SourceText> _sources = new();
        private readonly Dictionary<string, ModuleSymbol> _modules = new(StringComparer.Ordinal);
        private readonly List<MetaTypeDef> _types = MetaTypeDef.Primitives.ToList();
        private readonly Dictionary<string, int> _typeNames = MetaTypeDef.Primitives.ToDictionary(t => t.Name, t => t.Id, StringComparer.Ordinal);
        private readonly Dictionary<string, (ModuleSymbol Module, MetaDeclaration Syntax)> _userTypes = new(StringComparer.Ordinal);
        private readonly List<FunctionSymbol> _functions = new();
        private readonly List<VariableSymbol> _globals = new();
        private readonly List<MetaConstantDef> _constants = new();
        private readonly Dictionary<string, int> _constantIds = new(StringComparer.Ordinal);
        private readonly List<MetaImportDef> _imports = new();
        private readonly List<MetaLinkDef> _links = new();
        private readonly List<MetaWatchDef> _watches = new();
        private readonly HashSet<string> _scopes = new(StringComparer.Ordinal);
        private string _cacheIdentity = "";
        public Session(MetaCompilationRequest request, MetaApiCatalog catalog, CancellationToken cancellation, bool semantic = false)
        {
            (_request, _catalog, _budget) = (request, catalog, new(request.Limits ?? new(), cancellation));
            if (semantic) _semantic = new SemanticRecorder();
        }

        public MetaCompilationResult Compile()
        {
            MetaModule? module = null;
            byte[]? artifact = null;
            try
            {
                LoadSources();
                LoadBinaryLibraries();
                _cacheIdentity = MetaApiCatalog.HashText(BuildId + "\n" + _request.PackageName + "\n" + _request.PackageVersion + "\n" +
                    _request.RootModule + "\n" + _request.Library + "\n" + _catalog.Hash + "\n" + string.Join('\n', _sources.Select(s => s.FileId + ":" + s.Revision)) + "\n" + LockIdentity());
                DeclareTypes();
                BindImports();
                DefineTypes();
                ValidateTypeGraphs();
                DeclareValues();
                _semantic?.DeclareAll(this);
                ValidateExports();
                if (HasErrors) return Result(null, null);
                module = EmitModule();
                if (!HasErrors)
                {
                    artifact = MetaBytecodeWriter.Write(module);
                    if (artifact.Length > 1024 * 1024) { _budget.Report("E410", _sources.FirstOrDefault()?.Span ?? EmptySpan); artifact = null; }
                }
            }
            catch (MetaCompilationAbortException) { }
            catch (MetaFormatException e)
            {
                _budget.Report("E450", _sources.FirstOrDefault()?.Span ?? EmptySpan, e.Message);
            }
            return Result(artifact, module);
        }

        private bool HasErrors => _budget.Diagnostics.Any(d => d.Severity == MetaDiagnosticSeverity.Error);
        private static SourceSpan EmptySpan => new("project", new string('0', 64), 0, 0);
        private MetaCompilationResult Result(byte[]? bytes, MetaModule? module)
        {
            var digest = bytes == null ? null : Convert.ToHexStringLower(bytes.AsSpan(24, 32));
            var exports = bytes == null || module == null ? null : Certificate(module, digest!);
            return new(bytes, digest, _cacheIdentity, _budget.Diagnostics.ToArray(), module,
                _functions.Select(f => new MetaFunctionAnalysis(f.Id, f.Effects, f.Calls.Order().ToArray())).ToArray(), exports);
        }
        private string LockIdentity() => string.Join('\n', (_request.Dependencies ?? []).Select(d => d.PackageIdentity + ":" + d.Version + ":" + d.ContentHash)
            .Concat((_request.BinaryDependencies ?? []).Select(d => d.PackageIdentity + ":" + d.Version + ":" + d.ContentHash + ":" + MetaApiCatalog.HashText(System.Text.Json.JsonSerializer.Serialize(d.Exports)))).Order(StringComparer.Ordinal));

        private void LoadSources()
        {
            var files = _request.Sources.ToList();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dependency in _request.Dependencies ?? [])
            {
                if (!dependency.Authorized) { _budget.Report("E304", EmptySpan, dependency.PackageIdentity); continue; }
                if (!identities.Add(dependency.PackageIdentity)) { _budget.Report("E305", EmptySpan, dependency.PackageIdentity); continue; }
                if (dependency.ContentHash.Length != 64 || !dependency.ContentHash.All(char.IsAsciiHexDigit) || string.IsNullOrWhiteSpace(dependency.Version))
                    _budget.Report("E306", EmptySpan, dependency.PackageIdentity);
                files.AddRange(dependency.Sources);
            }
            if (files.Count == 0 || files.Count > _budget.Limits.MaxFiles) throw _budget.Limit("E400", EmptySpan);
            var bytes = 0;
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in files.OrderBy(f => f.FileId, StringComparer.Ordinal))
            {
                _budget.Cancellation.ThrowIfCancellationRequested();
                if (!ValidVirtualPath(file.FileId) || !paths.Add(file.FileId)) { _budget.Report("E307", EmptySpan, file.FileId); continue; }
                if (Encoding.UTF8.GetByteCount(file.Text) > _budget.Limits.MaxFileBytes) throw _budget.Limit("E400", EmptySpan);
                SourceText source;
                try { source = new SourceText(file.FileId, file.Text); }
                catch (EncoderFallbackException) { _budget.Report("E108", EmptySpan, file.FileId); continue; }
                bytes = checked(bytes + source.Utf8.Length);
                if (bytes > _budget.Limits.MaxProjectBytes) throw _budget.Limit("E400", source.Span);
                _sources.Add(source);
                var parse = MetaParser.Parse(source, _budget);
                _semantic?.Source(source, parse.Tokens);
                if (parse.Unit == null) continue;
                if (parse.Unit.Module == "meta" || parse.Unit.Module.StartsWith("meta.", StringComparison.Ordinal)) _budget.Report("E301", parse.Unit.Span);
                if (!_modules.TryAdd(parse.Unit.Module, new(source, parse.Unit))) _budget.Report("E302", parse.Unit.Span, parse.Unit.Module);
            }
            if (!_modules.ContainsKey(_request.RootModule)) _budget.Report("E303", EmptySpan, _request.RootModule);
        }

        private static bool ValidVirtualPath(string path) => Encoding.UTF8.GetByteCount(path) is > 0 and <= 192 &&
            !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':') && path.Split('/').All(p => p is not ("" or "." or "..") && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'));

        private void DeclareTypes()
        {
            foreach (var native in _catalog.Types) AddType(native.Name, native.Kind, native.ContractHash);
            foreach (var module in _modules.Values)
                foreach (var declaration in module.Syntax.Declarations)
                    if (declaration is MetaStructSyntax or MetaEnumSyntax)
                    {
                        var name = module.Syntax.Module + "." + declaration.Name;
                        if (!_userTypes.TryAdd(name, (module, declaration))) { _budget.Report("E220", declaration.Span, name); continue; }
                        AddType(name, declaration is MetaStructSyntax ? MetaTypeKind.Struct : MetaTypeKind.Enum);
                    }
            DeclareBinaryTypes();
        }

        private int AddType(string name, MetaTypeKind kind, string? hash = null)
        {
            if (Encoding.UTF8.GetByteCount(name) > 128) throw _budget.Limit("E217", EmptySpan);
            if (_typeNames.TryGetValue(name, out var id)) return id;
            id = _types.Count;
            if (id >= 4096) throw _budget.Limit("E406", EmptySpan);
            _types.Add(new() { Id = id, Name = name, Kind = kind, ContractHash = hash });
            _typeNames.Add(name, id); return id;
        }

        private void BindImports()
        {
            foreach (var module in _modules.Values)
                foreach (var import in module.Syntax.Imports)
                {
                    if (!module.Aliases.TryAdd(import.Alias, import.Module)) _budget.Report("E220", import.Span, import.Alias);
                    if (_catalog.Modules.Contains(import.Module, StringComparer.Ordinal))
                    {
                        Import(new() { Kind = "BuiltinModule", Name = import.Module, ContractHash = _catalog.ModuleHash(import.Module) });
                    }
                    else if (!_modules.ContainsKey(import.Module)) _budget.Report("E303", import.Span, import.Module);
                }
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            void Visit(ModuleSymbol module)
            {
                if (visited.Contains(module.Syntax.Module)) return;
                if (!visiting.Add(module.Syntax.Module)) { _budget.Report("E308", module.Syntax.Span, string.Join(" -> ", visiting), module.Syntax.Module); return; }
                foreach (var import in module.Syntax.Imports)
                    if (_modules.TryGetValue(import.Module, out var target)) Visit(target);
                visiting.Remove(module.Syntax.Module); visited.Add(module.Syntax.Module);
            }
            foreach (var module in _modules.Values) Visit(module);
        }

        private void DefineTypes()
        {
            foreach (var native in _catalog.Types)
            {
                var id = _typeNames[native.Name];
                _types[id] = _types[id] with
                {
                    Fields = native.Fields.Select(f => new MetaFieldDef(f.Name, ResolveCanonical(f.Type), true)).ToArray(),
                    Variants = native.Variants.Select((v, i) => new MetaVariantDef(v, i, null)).ToArray()
                };
            }
            DefineBinaryTypes();
            foreach (var (name, entry) in _userTypes)
            {
                var id = _typeNames[name];
                if (entry.Syntax is MetaStructSyntax structure)
                {
                    if (structure.Fields.Count > 64) _budget.Report("E217", structure.Span);
                    DuplicateNames(structure.Fields.Select(f => f.Name), structure.Span);
                    _types[id] = _types[id] with { Fields = structure.Fields.Select(f => new MetaFieldDef(f.Name, ResolveType(entry.Module, f.Type), false)).ToArray() };
                }
                else if (entry.Syntax is MetaEnumSyntax enumeration)
                {
                    DuplicateNames(enumeration.Variants, enumeration.Span);
                    _types[id] = _types[id] with { Variants = enumeration.Variants.Select((v, i) => new MetaVariantDef(v, i, null)).ToArray() };
                }
            }
        }

        private void DuplicateNames(IEnumerable<string> names, SourceSpan span)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in names) if (!seen.Add(name)) _budget.Report("E220", span, name);
        }

        private int ResolveType(ModuleSymbol module, MetaTypeSyntax syntax)
        {
            var type = ResolveTypeCore(module, syntax);
            if (type != 0 || syntax.Name == "void") _semantic?.UseType(type, syntax.Span);
            var dot = syntax.Name.IndexOf('.');
            if (dot > 0 && module.Aliases.ContainsKey(syntax.Name[..dot])) _semantic?.UseAlias(module, syntax.Name[..dot], syntax.Span);
            return type;
        }

        private int ResolveTypeCore(ModuleSymbol module, MetaTypeSyntax syntax)
        {
            if (syntax.Arguments.Count > 0) return Container(syntax.Name, syntax.Arguments.Select(t => ResolveType(module, t)).ToArray(), syntax.Span);
            if (_typeNames.TryGetValue(syntax.Name, out var direct) && direct < 6) return direct;
            if (syntax.Name.Contains('.') && _typeNames.TryGetValue(syntax.Name, out direct) && IsVisibleType(module, syntax.Name) &&
                (syntax.Name.StartsWith(module.Syntax.Module + ".", StringComparison.Ordinal) || module.Syntax.Imports.Any(i => syntax.Name.StartsWith(i.Module + ".", StringComparison.Ordinal)))) return direct;
            if (_typeNames.TryGetValue(module.Syntax.Module + "." + syntax.Name, out var local)) return local;
            var dot = syntax.Name.IndexOf('.');
            if (dot > 0 && module.Aliases.TryGetValue(syntax.Name[..dot], out var target))
            {
                var fullName = target + syntax.Name[dot..];
                if (_typeNames.TryGetValue(fullName, out var qualified) && IsVisibleType(module, fullName)) return qualified;
            }
            var matches = module.Syntax.Imports.Select(i => i.Module + "." + syntax.Name).Where(n => _typeNames.ContainsKey(n) && IsVisibleType(module, n)).ToList();
            if (syntax.Name is "Error" or "ErrorCode" or "SampleStamp") matches.Add("meta.system." + syntax.Name);
            matches = matches.Distinct(StringComparer.Ordinal).ToList();
            if (matches.Count == 1) return _typeNames[matches[0]];
            _budget.Report(matches.Count > 1 ? "E224" : "E221", syntax.Span, syntax.Name);
            return 0;
        }

        private bool IsVisibleType(ModuleSymbol from, string name) => _binaryTypeNames.Contains(name) ? _binaryPublicTypes.Contains(name) : !_userTypes.TryGetValue(name, out var entry) || entry.Module == from || entry.Syntax.Public;
        private int ResolveCanonical(string name)
        {
            var angle = name.IndexOf('<');
            if (angle < 0) return _typeNames[name];
            var content = name[(angle + 1)..^1]; var split = -1; var depth = 0;
            for (var i = 0; i < content.Length; i++) { if (content[i] == '<') depth++; if (content[i] == '>') depth--; if (content[i] == ',' && depth == 0) split = i; }
            var args = split < 0 ? new[] { ResolveCanonical(content) } : [ResolveCanonical(content[..split]), ResolveCanonical(content[(split + 1)..])];
            return Container(name[..angle], args, EmptySpan);
        }

        private int Container(string name, int[] args, SourceSpan span)
        {
            var kind = name switch { "array" => MetaTypeKind.Array, "list" => MetaTypeKind.List, "map" => MetaTypeKind.Map, "option" => MetaTypeKind.Option, "result" => MetaTypeKind.Result, _ => MetaTypeKind.Unit };
            if (kind == MetaTypeKind.Unit || args.Length != (kind == MetaTypeKind.Map ? 2 : 1)) { _budget.Report("E221", span, name); return 0; }
            if (kind == MetaTypeKind.Map && _types[args[0]].Kind is not (MetaTypeKind.Int or MetaTypeKind.Byte or MetaTypeKind.Bool or MetaTypeKind.String or MetaTypeKind.Enum)) _budget.Report("E218", span, "map-key");
            var full = name + "<" + string.Join(',', args.Select(a => _types[a].Name)) + ">";
            var id = AddType(full, kind);
            _types[id] = _types[id] with
            {
                Args = args,
                Variants = kind == MetaTypeKind.Option ? [new("none", 0, null), new("some", 1, args[0])] :
                    kind == MetaTypeKind.Result ? [new("ok", 0, args[0]), new("err", 1, _typeNames["meta.system.Error"])] : []
            };
            return id;
        }

        private void ValidateTypeGraphs()
        {
            var active = new HashSet<int>();
            var done = new Dictionary<int, int>();
            int Visit(int id, int depth)
            {
                if (depth > _budget.Limits.MaxTypeDepth) { _budget.Report("E219", EmptySpan, _types[id].Name); return 0; }
                if (done.TryGetValue(id, out var cached)) return cached;
                if (!active.Add(id)) { _budget.Report("E219", EmptySpan, _types[id].Name); return 0; }
                var type = _types[id];
                foreach (var arg in type.Args) Visit(arg, depth + 1);
                var size = type.Kind is MetaTypeKind.Struct or MetaTypeKind.NativeDto ? type.Fields.Sum(f => Visit(f.TypeId, depth + 1)) : 16;
                if (type.Kind == MetaTypeKind.Struct && size > 4096) _budget.Report("E217", EmptySpan, type.Name);
                active.Remove(id); done[id] = size; return size;
            }
            for (var id = 0; id < _types.Count; id++) Visit(id, 0);
        }

        private void DeclareValues()
        {
            foreach (var module in _modules.Values)
                foreach (var declaration in module.Syntax.Declarations)
                {
                    if (declaration is MetaStructSyntax or MetaEnumSyntax) continue;
                    if (declaration is not MetaWatchSyntax && (module.Aliases.ContainsKey(declaration.Name) || !module.Values.TryAdd(declaration.Name, declaration)))
                        _budget.Report("E220", declaration.Span, declaration.Name);
                    if ((module.Syntax.Module != _request.RootModule || _request.Library) && (declaration is MetaLinkSyntax or MetaWatchSyntax || declaration.Name == "main"))
                        _budget.Report("E309", declaration.Span, declaration.Name);
                    if (declaration is MetaFunctionSyntax function)
                    {
                        var symbol = new FunctionSymbol(_functions.Count, module, function.Name, function.Async, function.Parameters.Select(p => ResolveType(module, p.Type)).ToArray(), ResolveType(module, function.ReturnType), function.Body, function.Parameters.Select(p => p.Name).ToArray(), function.Span);
                        _functions.Add(symbol); module.Functions[function.Name] = symbol;
                        DuplicateNames(function.Parameters.Select(p => p.Name), function.Span);
                        if (function.Name == "main" && (function.Parameters.Count != 0 || symbol.ReturnType != 0)) _budget.Report("E214", function.Span, "main-signature");
                    }
                    else if (declaration is MetaGlobalSyntax global)
                    {
                        var symbol = new VariableSymbol(global.Name, ResolveType(module, global.Type), global.Storage == "var", global.Storage == "const" ? -1 : _globals.Count, true, global.Storage, global.Initializer, module, global.Span);
                        module.Globals[global.Name] = symbol;
                        if (symbol.Storage != "const") _globals.Add(symbol);
                    }
                    else if (declaration is MetaLinkSyntax link)
                    {
                        var type = ResolveType(module, link.Type);
                        if (_types[type].Kind != MetaTypeKind.Capability) _budget.Report("E223", link.Span, link.Type.Name);
                        if (_links.Any(l => l.SlotName == link.Slot)) _budget.Report("E220", link.Span, link.Slot);
                        var id = _links.Count;
                        _links.Add(new(id, link.Slot, type, _types[type].ContractHash ?? new string('0', 64))); module.Links[link.Name] = id;
                    }
                }
            var watchKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var module in _modules.Values)
                foreach (var watch in module.Syntax.Declarations.OfType<MetaWatchSyntax>())
                {
                    if (!watchKeys.Add(watch.Name)) _budget.Report("E220", watch.Span, watch.Name);
                    if (!module.Links.TryGetValue(watch.Link, out var linkId)) { _budget.Report("E222", watch.Span, watch.Link); continue; }
                    var owner = _types[_links[linkId].CapabilityTypeId].Name;
                    var member = _catalog.Members.FirstOrDefault(m => m.Owner == owner && m.Name == watch.Event && m.Effect == "Event");
                    if (member == null) { _budget.Report("E225", watch.Span, watch.Name); continue; }
                    if (watch.Parameters.Count != member.Parameters.Length) _budget.Report("E227", watch.Span, watch.Name);
                    DuplicateNames(watch.Parameters, watch.Span);
                    var symbol = new FunctionSymbol(_functions.Count, module, "$watch." + watch.Name, watch.Async,
                        member.Parameters.Select(ResolveCanonical).ToArray(), 0, watch.Body, watch.Parameters.ToArray(), watch.Span);
                    _functions.Add(symbol);
                    _watches.Add(new(linkId, HostImport(member), symbol.Id, member.Delivery, member.Capacity));
                }
            DeclareBinaryValues();
        }

        private void ValidateExports()
        {
            bool Hidden(int id, HashSet<int> seen)
            {
                if (!seen.Add(id)) return false;
                var type = _types[id];
                return _userTypes.TryGetValue(type.Name, out var entry) && !entry.Syntax.Public || type.Args.Any(a => Hidden(a, seen));
            }
            foreach (var module in _modules.Values)
                foreach (var declaration in module.Syntax.Declarations.Where(d => d.Public))
                {
                    var ids = declaration switch
                    {
                        MetaFunctionSyntax f => module.Functions[f.Name].Parameters.Append(module.Functions[f.Name].ReturnType),
                        MetaStructSyntax s => _types[_typeNames[module.Syntax.Module + "." + s.Name]].Fields.Select(f => f.TypeId),
                        MetaGlobalSyntax g => [module.Globals[g.Name].Type],
                        _ => Array.Empty<int>()
                    };
                    if (ids.Any(id => Hidden(id, new()))) _budget.Report("E230", declaration.Span, declaration.Name);
                }
        }

        private int Import(MetaImportDef import)
        {
            var index = _imports.FindIndex(i => i.Kind == import.Kind && i.Name == import.Name);
            if (index >= 0) return index;
            if (_imports.Count >= 256) throw _budget.Limit("E411", EmptySpan);
            _imports.Add(import); return _imports.Count - 1;
        }
        private int HostImport(MetaApiMember member)
        {
            _scopes.Add(member.Scope);
            return Import(new() { Kind = member.Effect == "Event" ? "HostEvent" : "HostMethod", Name = member.QualifiedName,
                ContractHash = member.ContractHash, CapabilityTypeId = _typeNames[member.Owner], ParameterTypes = member.Parameters.Select(ResolveCanonical).ToArray(),
                ReturnType = ResolveCanonical(member.ReturnType), Effect = member.Effect });
        }

        private sealed class ModuleSymbol(SourceText source, MetaCompilationUnit syntax)
        {
            public SourceText Source = source;
            public bool Binary;
            public MetaCompilationUnit Syntax = syntax;
            public Dictionary<string, string> Aliases = new(StringComparer.Ordinal);
            public Dictionary<string, MetaDeclaration> Values = new(StringComparer.Ordinal);
            public Dictionary<string, FunctionSymbol> Functions = new(StringComparer.Ordinal);
            public Dictionary<string, VariableSymbol> Globals = new(StringComparer.Ordinal);
            public Dictionary<string, int> Links = new(StringComparer.Ordinal);
        }
        private sealed class FunctionSymbol(int id, ModuleSymbol module, string name, bool async, int[] parameters,
            int returnType, MetaBlockSyntax body, string[] parameterNames, SourceSpan span)
        {
            public int Id = id;
            public ModuleSymbol Module = module;
            public string Name = name;
            public bool Async = async;
            public int[] Parameters = parameters;
            public string[] ParameterNames = parameterNames;
            public int ReturnType = returnType;
            public MetaBlockSyntax Body = body;
            public SourceSpan Span = span;
            public MetaEffectFlags Effects;
            public HashSet<int> Calls = new();
        }
        private sealed class VariableSymbol(string name, int type, bool mutable, int slot, bool global, string storage,
            MetaExpression? initializer, ModuleSymbol module, SourceSpan span)
        {
            public string Name = name;
            public int Type = type;
            public bool Mutable = mutable;
            public int Slot = slot;
            public bool Global = global;
            public string Storage = storage;
            public MetaExpression? Initializer = initializer;
            public ModuleSymbol Module = module;
            public SourceSpan Span = span;
            public ConstantValue? Constant;
            public bool Evaluating;
            public bool Read;
        }
        private sealed record ConstantValue(int Type, object? Value);
        private sealed class SemanticAbortException : Exception;
    }
}
