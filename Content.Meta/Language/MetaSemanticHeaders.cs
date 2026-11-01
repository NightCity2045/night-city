// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Globalization;
using System.Text.Json;
using Content.Meta.Bytecode;
using Content.Meta.Verification;

namespace Content.Meta.Language;

public sealed record MetaSemanticTypeHeader(string Name, MetaTypeKind Kind, MetaApiField[] Fields, string[] Variants);
public sealed record MetaSemanticConstantHeader(string Name, int Value);
public sealed record MetaSemanticLiteralHeader(string Type, MetaConstantKind Kind, string? Value, int[] Items);
public sealed record MetaSemanticLibraryHeader(string Package, string Version, string ArtifactDigest, string SourceTreeHash,
    string RootModule, MetaSemanticTypeHeader[] Types, MetaFunctionExport[] Functions,
    MetaSemanticConstantHeader[] Constants, MetaSemanticLiteralHeader[] Values);
public sealed record MetaSemanticEnvironment(string RootModule, string Package, string Version, bool Library,
    MetaSemanticLibraryHeader[] Dependencies);
public sealed record MetaSemanticRevision(string FileId, string Path, long Revision, string Digest);

public static class MetaSemanticHeaders
{
    public const int MaximumBytes = 1024 * 1024;
    public static string RevisionStamp(IEnumerable<MetaSemanticRevision> files) => MetaApiCatalog.HashText(string.Join('\n',
        files.OrderBy(value => value.Path, StringComparer.Ordinal).ThenBy(value => value.FileId, StringComparer.Ordinal)
            .Select(value => value.FileId + ":" + value.Path + ":" + value.Revision.ToString(CultureInfo.InvariantCulture) + ":" + value.Digest)));

    /// <summary>Public, source-free ABI only. Never copies code, private names, debug spans or a source/blob locator.</summary>
    public static MetaSemanticLibraryHeader Project(VerifiedArtifact verified, MetaExportCertificate certificate) =>
        MetaCompiler.ProjectSemanticHeader(verified, certificate);
}

public sealed partial class MetaCompiler
{
    internal static MetaSemanticLibraryHeader ProjectSemanticHeader(VerifiedArtifact verified, MetaExportCertificate certificate) =>
        Session.ProjectSemanticHeader(verified, certificate);

    private sealed partial class Session
    {
        private IReadOnlyList<MetaSemanticLibraryHeader> _semanticHeaders = [];

        public static MetaSemanticLibraryHeader ProjectSemanticHeader(VerifiedArtifact verified, MetaExportCertificate certificate)
        {
            var module = verified.Module;
            if (certificate.ArtifactDigest != verified.Digest || certificate.SourceTreeHash != module.Manifest.SourceTreeHash)
                throw new MetaFormatException("Semantic public ABI is not bound to this verified release.");
            ValidateBinaryExports(module, certificate);
            var publicTypes = new HashSet<int>();
            var pendingTypes = new Stack<int>(certificate.Types.Select(value => value.TypeId)
                .Concat(certificate.Functions.SelectMany(value => module.Functions[value.FunctionId].ParameterTypes.Append(module.Functions[value.FunctionId].ReturnType)))
                .Concat(certificate.Constants.Select(value => module.Constants[value.ConstantId].TypeId)));
            while (pendingTypes.TryPop(out var typeId))
            {
                if (!publicTypes.Add(typeId)) continue;
                var type = module.Types[typeId];
                foreach (var argument in type.Args) pendingTypes.Push(argument);
                foreach (var field in type.Fields) pendingTypes.Push(field.TypeId);
            }
            var values = new List<MetaSemanticLiteralHeader>();
            var remap = new Dictionary<int, int>();
            int Value(int id)
            {
                if (remap.TryGetValue(id, out var prior)) return prior;
                var constant = module.Constants[id];
                var items = constant.Items.Select(Value).ToArray();
                var next = values.Count;
                values.Add(new(module.Types[constant.TypeId].Name, constant.Kind, constant.Value, items)); remap.Add(id, next); return next;
            }
            var header = new MetaSemanticLibraryHeader(module.Manifest.PackageName, module.Manifest.PackageVersion,
                verified.Digest, module.Manifest.SourceTreeHash, module.Manifest.RootModule,
                publicTypes.Where(value => module.Types[value].Kind is MetaTypeKind.Struct or MetaTypeKind.Enum && module.Types[value].ContractHash == null)
                .Order().Select(value =>
                {
                    var type = module.Types[value];
                    return new MetaSemanticTypeHeader(type.Name, type.Kind,
                        type.Fields.Select(field => new MetaApiField(field.Name, module.Types[field.TypeId].Name)).ToArray(),
                        type.Variants.Select(variant => variant.Name).ToArray());
                }).ToArray(), certificate.Functions.Select(value => value with { ParameterTypes = value.ParameterTypes.ToArray() }).ToArray(),
                certificate.Constants.Select(value => new MetaSemanticConstantHeader(value.Name, Value(value.ConstantId))).ToArray(), values.ToArray());
            if (JsonSerializer.SerializeToUtf8Bytes(header).Length > MetaSemanticHeaders.MaximumBytes)
                throw new MetaFormatException("Semantic public ABI exceeds its byte quota.");
            return header;
        }

        private void LoadSemanticHeaderModules()
        {
            foreach (var header in _semanticHeaders)
            {
                if (header.Types.Length > 4096 || header.Functions.Length > 4096 || header.Constants.Length > 16384 || header.Values.Length > 16384 ||
                    header.ArtifactDigest.Length != 64 || header.SourceTreeHash.Length != 64) throw _budget.Limit("E411", EmptySpan);
                foreach (var name in header.Types.Select(value => Owner(value.Name)).Concat(header.Functions.Select(value => Owner(value.Name)))
                             .Concat(header.Constants.Select(value => Owner(value.Name))).Append(header.RootModule).Distinct(StringComparer.Ordinal))
                {
                    if (name.Length is < 1 or > 128 || name == "meta" || name.StartsWith("meta.", StringComparison.Ordinal) ||
                        _modules.TryGetValue(name, out var existing) && !existing.Binary) { _budget.Report("E302", EmptySpan, name); continue; }
                    if (_modules.ContainsKey(name)) continue;
                    if (_modules.Count >= 64) throw _budget.Limit("E411", EmptySpan);
                    var source = new SourceText("semantic-header.meta", "");
                    _modules.Add(name, new(source, new(name, [], [], source.Span)) { Binary = true });
                }
            }
        }

        private void DeclareSemanticHeaderTypes()
        {
            foreach (var header in _semanticHeaders)
                foreach (var type in header.Types)
                {
                    if (type.Kind is not (MetaTypeKind.Struct or MetaTypeKind.Enum) || type.Name.StartsWith("meta.", StringComparison.Ordinal) ||
                        type.Name.Length > 128 || type.Fields.Length > 64 || type.Variants.Length > 64)
                    { _budget.Report("E305", EmptySpan, type.Name); continue; }
                    if (_typeNames.ContainsKey(type.Name))
                    {
                        var prior = _semanticHeaders.SelectMany(value => value.Types).First(value => value.Name == type.Name);
                        if (!_binaryPublicTypes.Contains(type.Name) || prior.Kind != type.Kind || !prior.Fields.SequenceEqual(type.Fields) || !prior.Variants.SequenceEqual(type.Variants))
                            _budget.Report("E305", EmptySpan, type.Name);
                        continue;
                    }
                    AddType(type.Name, type.Kind); _binaryTypeNames.Add(type.Name); _binaryPublicTypes.Add(type.Name);
                }
        }
        private void DefineSemanticHeaderTypes()
        {
            foreach (var header in _semanticHeaders)
                foreach (var type in header.Types)
                {
                    if (!_binaryPublicTypes.Contains(type.Name)) continue;
                    if (type.Fields.Any(value => value.Name.Length > 128 || value.Type.Length > 128)) throw _budget.Limit("E217", EmptySpan);
                    DuplicateNames(type.Fields.Select(value => value.Name), EmptySpan); DuplicateNames(type.Variants, EmptySpan);
                    var id = _typeNames[type.Name];
                    _types[id] = _types[id] with { Fields = type.Fields.Select(value => new MetaFieldDef(value.Name, ResolveCanonical(value.Type), false)).ToArray(),
                        Variants = type.Variants.Select((value, index) => new MetaVariantDef(value, index, null)).ToArray() };
                }
        }

        private void DeclareSemanticHeaderValues()
        {
            var maximumStringBytes = new MetaLimits().StringBytes;
            foreach (var header in _semanticHeaders)
            {
                foreach (var exported in header.Functions)
                {
                    if (!_modules.TryGetValue(Owner(exported.Name), out var module) || !module.Binary) continue;
                    var name = exported.Name[(exported.Name.LastIndexOf('.') + 1)..];
                    if (exported.Name.Length > 256 || exported.ParameterTypes.Length > 64 ||
                        exported.ParameterTypes.Any(value => value.Length > 128) || exported.ReturnType.Length > 128) throw _budget.Limit("E217", EmptySpan);
                    var parameters = exported.ParameterTypes.Select(ResolveCanonical).ToArray();
                    var result = ResolveCanonical(exported.ReturnType);
                    var symbol = new FunctionSymbol(_functions.Count, module, name, exported.Async, parameters, result,
                        new([], module.Syntax.Span), [], module.Syntax.Span);
                    if (module.Functions.TryGetValue(name, out var prior))
                    {
                        if (!prior.Parameters.SequenceEqual(parameters) || prior.ReturnType != result || prior.Async != exported.Async) _budget.Report("E305", EmptySpan, exported.Name);
                        continue;
                    }
                    if (_functions.Count >= 4096) throw _budget.Limit("E408", EmptySpan);
                    module.Functions.Add(name, symbol);
                    _functions.Add(symbol);
                    module.Values[name] = new MetaFunctionSyntax(name, true, exported.Async, [], new("void", [], EmptySpan), new([], EmptySpan), EmptySpan);
                }
                var constants = new ConstantValue[header.Values.Length];
                for (var id = 0; id < header.Values.Length; id++)
                {
                    var value = header.Values[id];
                    if (value.Items.Length > 64 || value.Items.Any(item => item < 0 || item >= id)) throw _budget.Limit("E411", EmptySpan);
                    var type = ResolveCanonical(value.Type);
                    object? scalar = value.Kind switch
                    {
                        MetaConstantKind.Unit => null, MetaConstantKind.Bool => bool.Parse(value.Value!),
                        MetaConstantKind.Byte => byte.Parse(value.Value!, CultureInfo.InvariantCulture),
                        MetaConstantKind.Int => long.Parse(value.Value!, CultureInfo.InvariantCulture),
                        MetaConstantKind.Enum => int.Parse(value.Value!, CultureInfo.InvariantCulture),
                        MetaConstantKind.Float => BitConverter.Int64BitsToDouble(unchecked((long)ulong.Parse(value.Value!, NumberStyles.HexNumber, CultureInfo.InvariantCulture))),
                        MetaConstantKind.String => value.Value, MetaConstantKind.Struct => value.Items.Select(item => constants[item]).ToArray(),
                        _ => throw new MetaFormatException("Invalid semantic constant header.")
                    };
                    if (!Enum.TryParse<MetaConstantKind>(_types[type].Kind.ToString(), out var expectedKind) || expectedKind != value.Kind ||
                        value.Kind == MetaConstantKind.Struct && (value.Items.Length != _types[type].Fields.Length ||
                            value.Items.Where((item, position) => constants[item].Type != _types[type].Fields[position].TypeId).Any()) ||
                        value.Kind == MetaConstantKind.Float && !double.IsFinite((double)scalar!) ||
                        value.Kind == MetaConstantKind.Enum && (uint)(int)scalar! >= _types[type].Variants.Length ||
                        value.Kind == MetaConstantKind.String && (value.Value == null || System.Text.Encoding.UTF8.GetByteCount(value.Value) > maximumStringBytes))
                        throw new MetaFormatException("Semantic constant ABI differs from its value.");
                    constants[id] = new(type, scalar);
                }
                foreach (var exported in header.Constants)
                {
                    if ((uint)exported.Value >= constants.Length || !_modules.TryGetValue(Owner(exported.Name), out var module) || !module.Binary)
                        throw new MetaFormatException("Invalid semantic constant export.");
                    var name = exported.Name[(exported.Name.LastIndexOf('.') + 1)..]; var value = constants[exported.Value];
                    if (module.Values.ContainsKey(name))
                    {
                        if (!module.Globals.TryGetValue(name, out var prior) || prior.Constant == null || !ConstantEquals(prior.Constant, value)) _budget.Report("E305", EmptySpan, exported.Name);
                        continue;
                    }
                    module.Values.Add(name, new MetaGlobalSyntax(name, true, "const", new(_types[value.Type].Name, [], EmptySpan), new MetaLiteralSyntax("Unit", null, EmptySpan), EmptySpan));
                    module.Globals.Add(name, new(name, value.Type, false, -1, true, "const", null, module, EmptySpan) { Constant = value });
                }
            }
        }
    }
}
