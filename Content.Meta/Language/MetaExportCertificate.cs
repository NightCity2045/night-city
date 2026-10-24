// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Bytecode;
using Content.Meta.Verification;

namespace Content.Meta.Language;

public sealed record MetaFunctionExport(int FunctionId, string Name, string[] ParameterTypes, string ReturnType, bool Async);
public sealed record MetaTypeExport(int TypeId, string Name);
public sealed record MetaConstantExport(int ConstantId, string Name);
public sealed record MetaExportCertificate(string SourceTreeHash, string ArtifactDigest, MetaFunctionExport[] Functions,
    MetaTypeExport[] Types, MetaConstantExport[] Constants);

public sealed partial class MetaCompiler
{
    /// <summary>Rebuilds the public export proof from bounded authorized source declarations and verified ABI tables.
    /// This runs independently of any export claims returned by a worker or supplied by a client.</summary>
    public MetaExportCertificate VerifyExports(MetaCompilationRequest authorizedSources, VerifiedArtifact verified, CancellationToken cancellation = default)
    {
        var session = new Session(authorizedSources, _catalog, cancellation);
        return session.VerifyExports(verified.Module, verified.Digest);
    }

    private sealed partial class Session
    {
        public MetaExportCertificate VerifyExports(MetaModule artifact, string digest)
        {
            LoadSources(); LoadBinaryLibraries(); DeclareTypes(); BindImports(); DefineTypes(); ValidateTypeGraphs(); DeclareValues(); ValidateExports();
            if (HasErrors) throw new MetaFormatException("Invalid authorized source headers for export certificate.");
            return Certificate(artifact, digest);
        }

        private MetaExportCertificate Certificate(MetaModule artifact, string digest)
        {
            var sourceHash = MetaApiCatalog.HashText(string.Join('\n', _sources.Select(s => s.FileId + ":" + s.Revision)));
            if (sourceHash != artifact.Manifest.SourceTreeHash) throw new MetaFormatException("Export source tree does not match artifact.");
            var functions = new List<MetaFunctionExport>(); var types = new List<MetaTypeExport>(); var constants = new List<MetaConstantExport>();
            foreach (var module in _modules.Values)
                foreach (var declaration in module.Syntax.Declarations.Where(d => d.Public))
                {
                    var name = module.Syntax.Module + "." + declaration.Name;
                    if (declaration is MetaFunctionSyntax)
                    {
                        var sourceFunction = module.Functions[declaration.Name];
                        var function = artifact.Functions.SingleOrDefault(f => f.Name == name) ?? throw new MetaFormatException("Exported source function missing from artifact.");
                        var parameterNames = sourceFunction.Parameters.Select(p => _types[p].Name).ToArray(); var returnName = _types[sourceFunction.ReturnType].Name;
                        if (!parameterNames.SequenceEqual(function.ParameterTypes.Select(p => artifact.Types[p].Name), StringComparer.Ordinal) || returnName != artifact.Types[function.ReturnType].Name || sourceFunction.Async != function.Async)
                            throw new MetaFormatException("Exported source signature differs from artifact.");
                        functions.Add(new(function.Id, name, parameterNames, returnName, function.Async));
                    }
                    else if (declaration is MetaStructSyntax or MetaEnumSyntax)
                    {
                        var type = artifact.Types.SingleOrDefault(t => t.Name == name) ?? throw new MetaFormatException("Exported source type missing from artifact.");
                        types.Add(new(type.Id, name));
                    }
                    else if (declaration is MetaGlobalSyntax { Storage: "const" })
                    {
                        var constant = EvaluateGlobalConstant(module.Globals[declaration.Name]);
                        var definition = Constant(constant);
                        // Constants are interned deterministically. Header re-verification can find them by canonical value.
                        var serialized = MetaCanonicalJson.Serialize(_constants[definition]);
                        var id = Array.FindIndex(artifact.Constants, c => MetaCanonicalJson.Serialize(c).AsSpan().SequenceEqual(serialized));
                        if (id < 0) throw new MetaFormatException("Exported source constant missing from artifact.");
                        constants.Add(new(id, name));
                    }
                }
            return new(sourceHash, digest, functions.OrderBy(f => f.Name, StringComparer.Ordinal).ToArray(),
                types.OrderBy(t => t.Name, StringComparer.Ordinal).ToArray(), constants.OrderBy(c => c.Name, StringComparer.Ordinal).ToArray());
        }
    }
}
