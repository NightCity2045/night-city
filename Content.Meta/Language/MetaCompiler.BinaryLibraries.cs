// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Bytecode;
using Content.Meta.Verification;

namespace Content.Meta.Language;

/// <summary>A server-authorized, exact artifact and its publisher's digest-bound public ABI receipt.</summary>
public sealed record MetaBinaryLibrary(string PackageIdentity, string Version, string ContentHash, byte[] Artifact,
    MetaExportCertificate Exports, bool Authorized = true);

public sealed partial class MetaCompiler
{
    private sealed partial class Session
    {
        private readonly List<BinaryLibrary> _binaryLibraries = new();
        private readonly HashSet<string> _binaryTypeNames = new(StringComparer.Ordinal);
        private readonly HashSet<string> _binaryPublicTypes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FunctionSymbol> _binaryFunctions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, VariableSymbol> _binaryGlobals = new(StringComparer.Ordinal);
        private readonly Dictionary<FunctionSymbol, (BinaryLibrary Library, MetaFunctionDef Function)> _binaryOrigins = new();
        private readonly HashSet<VariableSymbol> _binaryGlobalSymbols = new();

        private void LoadBinaryLibraries()
        {
            var identities = new Dictionary<string, (string Version, string Hash)>(StringComparer.Ordinal);
            foreach (var source in _request.Dependencies ?? []) identities[source.PackageIdentity] = (source.Version, source.ContentHash);
            var bytes = 0;
            foreach (var dependency in (_request.BinaryDependencies ?? []).OrderBy(d => d.PackageIdentity, StringComparer.Ordinal))
            {
                _budget.Cancellation.ThrowIfCancellationRequested();
                if (!dependency.Authorized) { _budget.Report("E304", EmptySpan, dependency.PackageIdentity); continue; }
                if (dependency.Artifact.Length > 1024 * 1024 || (bytes = checked(bytes + dependency.Artifact.Length)) > 4 * 1024 * 1024 || _binaryLibraries.Count >= 64)
                    throw _budget.Limit("E411", EmptySpan);
                if (identities.TryGetValue(dependency.PackageIdentity, out var existing) && existing != (dependency.Version, dependency.ContentHash))
                { _budget.Report("E305", EmptySpan, dependency.PackageIdentity); continue; }
                identities[dependency.PackageIdentity] = (dependency.Version, dependency.ContentHash);
                MetaModule module;
                try
                {
                    module = MetaBytecodeLoader.Load(dependency.Artifact);
                    var verified = MetaVerifier.Verify(dependency.Artifact, _catalog.CreateVerificationCatalog(module));
                    if (verified.Digest != dependency.ContentHash || dependency.Exports.ArtifactDigest != verified.Digest || dependency.Exports.SourceTreeHash != module.Manifest.SourceTreeHash ||
                        module.Manifest.PackageName != dependency.PackageIdentity || module.Manifest.PackageVersion != dependency.Version)
                        throw new MetaFormatException("Binary library identity or export digest mismatch.");
                    if (module.Links.Length != 0 || module.Watches.Length != 0 || module.Manifest.MainFunctionId != null)
                        throw new MetaFormatException("A library cannot declare main, links or watches.");
                    ValidateBinaryExports(module, dependency.Exports);
                    foreach (var import in module.Imports.Where(i => i.Kind == "Library"))
                    {
                        var identity = (import.Version!, import.ContentHash!);
                        if (identities.TryGetValue(import.Name, out var prior) && prior != identity) throw new MetaFormatException("Conflicting transitive library lock.");
                        identities[import.Name] = identity;
                    }
                }
                catch (MetaFormatException exception) { _budget.Report("E310", EmptySpan, dependency.PackageIdentity, exception.Message); continue; }
                var library = new BinaryLibrary(dependency, module); _binaryLibraries.Add(library);
                var moduleNames = module.Functions.Select(f => Owner(f.Name)).Concat(module.Globals.Select(g => Owner(g.Name))).Append(module.Manifest.RootModule).Concat(module.Types.Where(t => t.Kind is MetaTypeKind.Struct or MetaTypeKind.Enum && t.ContractHash == null).Select(t => Owner(t.Name)))
                    .Concat(dependency.Exports.Constants.Select(c => Owner(c.Name))).Distinct(StringComparer.Ordinal);
                foreach (var moduleName in moduleNames)
                {
                    if (_modules.TryGetValue(moduleName, out var prior) && !prior.Binary) { _budget.Report("E302", EmptySpan, moduleName); continue; }
                    if (!_modules.ContainsKey(moduleName))
                    {
                        if (_modules.Count >= 64) throw _budget.Limit("E411", EmptySpan);
                        var source = new SourceText("binary.meta", "");
                        _modules.Add(moduleName, new(source, new(moduleName, [], [], source.Span)) { Binary = true });
                    }
                }
                foreach (var exported in dependency.Exports.Types) _binaryPublicTypes.Add(exported.Name);
            }
        }

        private static string Owner(string name)
        {
            var dot = name.LastIndexOf('.');
            if (dot <= 0) throw new MetaFormatException("Library symbol requires a module-qualified name.");
            return name[..dot];
        }

        private static void ValidateBinaryExports(MetaModule module, MetaExportCertificate exports)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var function in exports.Functions)
            {
                if ((uint) function.FunctionId >= module.Functions.Length || !names.Add(function.Name)) throw new MetaFormatException("Invalid library function export.");
                var actual = module.Functions[function.FunctionId];
                if (actual.Name != function.Name || actual.Async != function.Async || actual.Initializer || module.Types[actual.ReturnType].Name != function.ReturnType || !actual.ParameterTypes.Select(t => module.Types[t].Name).SequenceEqual(function.ParameterTypes, StringComparer.Ordinal))
                    throw new MetaFormatException("Library function export ABI mismatch.");
            }
            foreach (var type in exports.Types)
                if ((uint) type.TypeId >= module.Types.Length || module.Types[type.TypeId].Name != type.Name || module.Types[type.TypeId].Kind is not (MetaTypeKind.Struct or MetaTypeKind.Enum) || module.Types[type.TypeId].ContractHash != null)
                    throw new MetaFormatException("Invalid library type export.");
            foreach (var constant in exports.Constants)
                if ((uint) constant.ConstantId >= module.Constants.Length || !names.Add(constant.Name)) throw new MetaFormatException("Invalid library constant export.");
        }

        private void DeclareBinaryTypes()
        {
            foreach (var library in _binaryLibraries)
            {
                library.Types = new int[library.Module.Types.Length];
                foreach (var type in library.Module.Types)
                {
                    if (_userTypes.ContainsKey(type.Name)) { _budget.Report("E305", EmptySpan, type.Name); continue; }
                    library.Types[type.Id] = AddType(type.Name, type.Kind, type.ContractHash);
                    if (type.Kind is MetaTypeKind.Struct or MetaTypeKind.Enum && type.ContractHash == null) _binaryTypeNames.Add(type.Name);
                }
            }
        }

        private void DefineBinaryTypes()
        {
            var defined = new HashSet<int>();
            foreach (var library in _binaryLibraries)
                foreach (var source in library.Module.Types)
                {
                    var id = library.Types[source.Id];
                    var mapped = source with
                    {
                        Id = id, Args = source.Args.Select(a => library.Types[a]).ToArray(),
                        Fields = source.Fields.Select(f => f with { TypeId = library.Types[f.TypeId] }).ToArray(),
                        Variants = source.Variants.Select(v => v with { PayloadTypeId = v.PayloadTypeId is { } p ? library.Types[p] : null }).ToArray()
                    };
                    if (defined.Contains(id) || source.Id < 6 || source.ContractHash != null)
                    {
                        if (!MetaCanonicalJson.Serialize(_types[id]).AsSpan().SequenceEqual(MetaCanonicalJson.Serialize(mapped))) _budget.Report("E305", EmptySpan, source.Name);
                    }
                    else _types[id] = mapped;
                    defined.Add(id);
                }
        }

        private void DeclareBinaryValues()
        {
            foreach (var library in _binaryLibraries)
            {
                library.Constants = new int[library.Module.Constants.Length];
                for (var id = 0; id < library.Constants.Length; id++)
                {
                    var original = library.Module.Constants[id];
                    library.Constants[id] = InternDefinition(original with { TypeId = library.Types[original.TypeId], Items = original.Items.Select(i => library.Constants[i]).ToArray() });
                }
                library.Functions = Enumerable.Repeat(-1, library.Module.Functions.Length).ToArray();
                foreach (var function in library.Module.Functions.Where(f => !f.Initializer))
                {
                    var module = _modules[Owner(function.Name)]; var shortName = function.Name[(function.Name.LastIndexOf('.') + 1)..];
                    if (!_binaryFunctions.TryGetValue(function.Name, out var symbol))
                    {
                        if (_functions.Count >= 4096) throw _budget.Limit("E408", EmptySpan);
                        symbol = new(_functions.Count, module, shortName, function.Async, function.ParameterTypes.Select(p => library.Types[p]).ToArray(), library.Types[function.ReturnType], new([], module.Syntax.Span), [], module.Syntax.Span);
                        _functions.Add(symbol); _binaryFunctions.Add(function.Name, symbol); _binaryOrigins.Add(symbol, (library, function));
                    }
                    else if (!symbol.Parameters.SequenceEqual(function.ParameterTypes.Select(p => library.Types[p])) || symbol.ReturnType != library.Types[function.ReturnType] || symbol.Async != function.Async)
                        _budget.Report("E305", EmptySpan, function.Name);
                    library.Functions[function.Id] = symbol.Id;
                    var exported = library.Dependency.Exports.Functions.Any(f => f.FunctionId == function.Id);
                    if (!module.Functions.ContainsKey(shortName))
                    {
                        module.Functions.Add(shortName, symbol);
                        module.Values[shortName] = new MetaFunctionSyntax(shortName, exported, function.Async, [], new("void", [], module.Syntax.Span), new([], module.Syntax.Span), module.Syntax.Span);
                    }
                    else if (exported && module.Values[shortName] is MetaFunctionSyntax declaration) module.Values[shortName] = declaration with { Public = true };
                }
                library.Globals = new int[library.Module.Globals.Length];
                foreach (var global in library.Module.Globals)
                {
                    var module = _modules.GetValueOrDefault(Owner(global.Name)) ?? _modules[library.Module.Manifest.RootModule];
                    if (!_binaryGlobals.TryGetValue(global.Name, out var symbol))
                    {
                        symbol = new(global.Name[(global.Name.LastIndexOf('.') + 1)..], library.Types[global.TypeId], global.Mutable, _globals.Count, true, global.Mutable ? "var" : "let", null, module, module.Syntax.Span);
                        _globals.Add(symbol); _binaryGlobals.Add(global.Name, symbol); _binaryGlobalSymbols.Add(symbol);
                    }
                    else if (symbol.Type != library.Types[global.TypeId] || symbol.Mutable != global.Mutable) _budget.Report("E305", EmptySpan, global.Name);
                    library.Globals[global.Id] = symbol.Slot;
                }
                foreach (var constant in library.Dependency.Exports.Constants)
                {
                    var module = _modules[Owner(constant.Name)]; var name = constant.Name[(constant.Name.LastIndexOf('.') + 1)..];
                    var value = DecodeConstant(library, constant.ConstantId);
                    if (module.Values.ContainsKey(name))
                    {
                        if (!module.Globals.TryGetValue(name, out var prior) || prior.Constant == null || !ConstantEquals(prior.Constant, value)) _budget.Report("E305", EmptySpan, constant.Name);
                        continue;
                    }
                    var typeSyntax = new MetaTypeSyntax(_types[value.Type].Name, [], module.Syntax.Span);
                    module.Values.Add(name, new MetaGlobalSyntax(name, true, "const", typeSyntax, new MetaLiteralSyntax("Unit", null, module.Syntax.Span), module.Syntax.Span));
                    module.Globals.Add(name, new(name, value.Type, false, -1, true, "const", null, module, module.Syntax.Span) { Constant = value });
                }
                library.Imports = new int[library.Module.Imports.Length];
                for (var id = 0; id < library.Imports.Length; id++)
                {
                    var import = library.Module.Imports[id];
                    // The closed module was independently checked against its immutable trusted catalog above.
                    // Builtin module grouping hashes may grow additively; native types/member hashes still require
                    // exact compatibility after remapping into this compilation's current catalog.
                    if (import.Kind == "BuiltinModule") import = import with { ContractHash = _catalog.ModuleHash(import.Name) };
                    library.Imports[id] = Import(import with { CapabilityTypeId = import.CapabilityTypeId is { } c ? library.Types[c] : null, ParameterTypes = import.ParameterTypes.Select(p => library.Types[p]).ToArray(), ReturnType = import.ReturnType is { } r ? library.Types[r] : null });
                }
                foreach (var scope in library.Module.Manifest.RequiredScopes) _scopes.Add(scope);
                Import(new() { Kind = "Library", Name = library.Dependency.PackageIdentity, Version = library.Dependency.Version, ContentHash = library.Dependency.ContentHash, ContractHash = MetaApiCatalog.HashText(JsonSerializer.Serialize(library.Dependency.Exports)) });
            }
            // Closed bodies have no source AST. Recover their direct effects and call edges before the shared fixed point.
            foreach (var (symbol, origin) in _binaryOrigins)
            {
                _budget.Cancellation.ThrowIfCancellationRequested();
                foreach (var instruction in MetaInstructionCodec.Decode(origin.Library.Module.Code.AsSpan(origin.Function.CodeOffset, origin.Function.CodeLength)))
                {
                    symbol.Effects |= instruction.Opcode switch
                    {
                        MetaOpcode.HostRead => MetaEffectFlags.CallsHost,
                        MetaOpcode.HostAwait => MetaEffectFlags.CallsHost | MetaEffectFlags.MaySuspend,
                        MetaOpcode.Yield or MetaOpcode.CallAsync => MetaEffectFlags.MaySuspend,
                        MetaOpcode.StoreGlobal => MetaEffectFlags.UsesMutableGlobal,
                        MetaOpcode.LoadGlobal when origin.Library.Module.Globals[(int) instruction.A].Mutable => MetaEffectFlags.UsesMutableGlobal,
                        MetaOpcode.ArrayNew or MetaOpcode.ArrayLiteral or MetaOpcode.RecordNew or MetaOpcode.VariantNew => MetaEffectFlags.Allocates,
                        MetaOpcode.CallIntrinsic => _catalog.Intrinsics.FirstOrDefault(i => i.Id == instruction.A)?.Effects ?? MetaEffectFlags.Allocates,
                        _ => MetaEffectFlags.None
                    };
                    if (instruction.Opcode is MetaOpcode.Call or MetaOpcode.CallAsync)
                        symbol.Calls.Add(origin.Library.Functions[(int) instruction.A]);
                }
            }
        }

        private ConstantValue DecodeConstant(BinaryLibrary library, int id)
        {
            var constant = library.Module.Constants[id]; object? value = constant.Kind switch
            {
                MetaConstantKind.Unit => null, MetaConstantKind.Bool => constant.Value == "true", MetaConstantKind.Byte => byte.Parse(constant.Value!, System.Globalization.CultureInfo.InvariantCulture),
                MetaConstantKind.Int => long.Parse(constant.Value!, System.Globalization.CultureInfo.InvariantCulture), MetaConstantKind.Float => BitConverter.UInt64BitsToDouble(Convert.ToUInt64(constant.Value, 16)),
                MetaConstantKind.String => constant.Value, MetaConstantKind.Enum => int.Parse(constant.Value!, System.Globalization.CultureInfo.InvariantCulture),
                MetaConstantKind.Struct => constant.Items.Select(i => DecodeConstant(library, i)).ToArray(), _ => throw new MetaFormatException("Unknown constant kind.")
            };
            return new(library.Types[constant.TypeId], value);
        }

        private int InternDefinition(MetaConstantDef definition)
        {
            var key = definition.TypeId + ":" + definition.Kind + ":" + definition.Value + ":" + string.Join(',', definition.Items);
            if (_constantIds.TryGetValue(key, out var existing)) return existing;
            if (_constants.Count >= 16384) throw _budget.Limit("E407", EmptySpan);
            var id = _constants.Count; _constants.Add(definition); _constantIds.Add(key, id); return id;
        }

        private MetaModule LinkBinaryCode(MetaModule source)
        {
            if (_binaryLibraries.Count == 0 || HasErrors) return source;
            var definitions = source.Functions.ToArray(); var code = source.Code.ToList();
            var duplicates = _binaryLibraries.SelectMany(l => l.Module.Functions.Where(f => !f.Initializer).Select(f => (Library: l, Function: f)))
                .GroupBy(p => p.Library.Functions[p.Function.Id]).ToDictionary(g => g.Key, g => g.ToArray());
            foreach (var (symbol, origin) in _binaryOrigins)
            {
                var instructions = MetaInstructionCodec.Decode(origin.Library.Module.Code.AsSpan(origin.Function.CodeOffset, origin.Function.CodeLength));
                var mapped = instructions.Select(i => Relocate(origin.Library, i)).ToArray();
                var bytes = MetaInstructionCodec.Encode(mapped);
                definitions[symbol.Id] = origin.Function with
                {
                    Id = symbol.Id, ParameterTypes = symbol.Parameters, ReturnType = symbol.ReturnType,
                    Locals = origin.Function.Locals.Select(l => l with { TypeId = origin.Library.Types[l.TypeId] }).ToArray(), CodeOffset = code.Count
                };
                code.AddRange(bytes);
                // Identical transitive modules are linked once. Conflicting same-name code is rejected.
                foreach (var duplicate in duplicates[symbol.Id])
                    {
                        var library = duplicate.Library; var candidate = duplicate.Function;
                        var other = MetaInstructionCodec.Encode(MetaInstructionCodec.Decode(library.Module.Code.AsSpan(candidate.CodeOffset, candidate.CodeLength)).Select(i => Relocate(library, i)));
                        if (!other.AsSpan().SequenceEqual(bytes)) _budget.Report("E305", EmptySpan, candidate.Name);
                    }
            }
            if (source.Manifest.InitializerFunctionId is { } initializerId)
            {
                var initializer = definitions[initializerId]; var locals = initializer.Locals.ToList(); var prefix = new List<MetaInstruction>();
                var initialized = new HashSet<int>(); var prefixLength = 0; var maximum = initializer.MaxStack;
                foreach (var library in _binaryLibraries)
                {
                    if (library.Module.Manifest.InitializerFunctionId is not { } oldId || library.Globals.All(initialized.Contains)) continue;
                    var old = library.Module.Functions[oldId]; var localBase = locals.Count;
                    locals.AddRange(old.Locals.Select(l => l with { TypeId = library.Types[l.TypeId] })); maximum = Math.Max(maximum, old.MaxStack);
                    var instructions = MetaInstructionCodec.Decode(library.Module.Code.AsSpan(old.CodeOffset, old.CodeLength));
                    var offsetMap = new Dictionary<int, int>(); var relocated = new List<MetaInstruction>(); var relocatedLength = 0;
                    foreach (var instruction in instructions)
                    {
                        offsetMap[instruction.Offset] = prefixLength + relocatedLength;
                        if (instruction.Opcode == MetaOpcode.Return) continue;
                        var mapped = Relocate(library, instruction);
                        if (mapped.Opcode is MetaOpcode.LoadLocal or MetaOpcode.StoreLocal or MetaOpcode.ResetLocal) mapped = mapped with { A = mapped.A + (uint) localBase };
                        if (mapped.Opcode == MetaOpcode.StoreGlobal && !initialized.Add((int) mapped.A)) mapped = new(0, MetaOpcode.Pop);
                        relocated.Add(mapped);
                        relocatedLength += mapped.Size;
                    }
                    for (var i = 0; i < relocated.Count; i++)
                    {
                        var instruction = relocated[i];
                        if (instruction.Opcode is MetaOpcode.Br or MetaOpcode.BrFalse) relocated[i] = instruction with { A = (uint) offsetMap[(int) instruction.A] };
                        else if (instruction.Opcode == MetaOpcode.BrVariant) relocated[i] = instruction with { C = (uint) offsetMap[(int) instruction.C] };
                    }
                    prefix.AddRange(relocated); prefixLength += relocatedLength;
                }
                var own = MetaInstructionCodec.Decode(source.Code.AsSpan(initializer.CodeOffset, initializer.CodeLength));
                var shifted = own.Select(i => i.Opcode is MetaOpcode.Br or MetaOpcode.BrFalse ? i with { A = i.A + (uint) prefixLength } : i.Opcode == MetaOpcode.BrVariant ? i with { C = i.C + (uint) prefixLength } : i);
                var bytes = MetaInstructionCodec.Encode(prefix.Concat(shifted));
                definitions[initializerId] = initializer with { CodeOffset = code.Count, CodeLength = bytes.Length, Locals = locals.ToArray(), MaxStack = maximum };
                // The old initializer is removed from the executable stream and all later ranges shift back.
                code.RemoveRange(initializer.CodeOffset, initializer.CodeLength);
                for (var id = 0; id < definitions.Length; id++)
                    if (id != initializerId && definitions[id].CodeOffset > initializer.CodeOffset) definitions[id] = definitions[id] with { CodeOffset = definitions[id].CodeOffset - initializer.CodeLength };
                definitions[initializerId] = definitions[initializerId] with { CodeOffset = code.Count };
                code.AddRange(bytes);
                if (source.Debug != null)
                    source = source with { Debug = source.Debug with { Spans = source.Debug.Spans.Select(s => s.FunctionId == initializerId ? s with { PcOffset = s.PcOffset + prefixLength } : s).ToArray() } };
            }
            return source with { Functions = definitions, Code = code.ToArray() };
        }

        private static MetaInstruction Relocate(BinaryLibrary library, MetaInstruction instruction)
        {
            var a = (int) instruction.A;
            return instruction.Opcode switch
            {
                MetaOpcode.Const => instruction with { A = (uint) library.Constants[a] },
                MetaOpcode.LoadGlobal or MetaOpcode.StoreGlobal => instruction with { A = (uint) library.Globals[a] },
                MetaOpcode.Call or MetaOpcode.CallAsync => instruction with { A = (uint) library.Functions[a] },
                MetaOpcode.Eq or MetaOpcode.Ne or MetaOpcode.Lt or MetaOpcode.Le or MetaOpcode.Gt or MetaOpcode.Ge or MetaOpcode.RecordNew or MetaOpcode.RecordGet or MetaOpcode.RecordSet or
                    MetaOpcode.VariantNew or MetaOpcode.VariantTag or MetaOpcode.VariantPayload or MetaOpcode.ArrayNew or MetaOpcode.ArrayLen or MetaOpcode.ArrayGet or MetaOpcode.ArraySet or MetaOpcode.ArrayLiteral or MetaOpcode.BrVariant => instruction with { A = (uint) library.Types[a] },
                MetaOpcode.CallIntrinsic when instruction.B != uint.MaxValue => instruction with { B = (uint) library.Types[(int) instruction.B] },
                MetaOpcode.HostRead or MetaOpcode.HostAwait => instruction with { A = (uint) library.Imports[a] },
                _ => instruction
            };
        }
        private sealed class BinaryLibrary(MetaBinaryLibrary dependency, MetaModule module)
        {
            public MetaBinaryLibrary Dependency = dependency;
            public MetaModule Module = module;
            public int[] Types = []; public int[] Functions = []; public int[] Constants = []; public int[] Globals = []; public int[] Imports = [];
        }
    }
}
