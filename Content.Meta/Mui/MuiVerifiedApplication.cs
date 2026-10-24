// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Content.Meta.Bytecode;
using Content.Meta.Language;
using Content.Meta.Verification;

namespace Content.Meta.Mui;

public sealed record MuiVerifiedView(int FunctionId, int StateTypeId, string Source, ValidatedMuiDocument Document);
public sealed record MuiVerifiedAction(int FunctionId, int InputTypeId);

/// <summary>Admission product over independently verified bytes and an independently rebuilt public export certificate.</summary>
public sealed class MuiVerifiedApplication
{
    public VerifiedArtifact Artifact { get; }
    public string PackageDigest { get; }
    public string ContractJson { get; }
    public MuiAppContract Contract { get; }
    public string StyleJson { get; }
    public MuiStyleSheet Styles { get; }
    public int ContextTypeId { get; }
    public int UserRefTypeId { get; }
    public int SessionRefTypeId { get; }
    public ImmutableDictionary<string, MuiVerifiedView> Views { get; }
    public ImmutableDictionary<string, MuiVerifiedAction> Actions { get; }
    public ImmutableDictionary<string, string> Resources { get; }

    private MuiVerifiedApplication(VerifiedArtifact artifact, string packageDigest, string contractJson, MuiAppContract contract,
        string styleJson, MuiStyleSheet styles, int context, int user, int session,
        ImmutableDictionary<string, MuiVerifiedView> views, ImmutableDictionary<string, MuiVerifiedAction> actions,
        ImmutableDictionary<string, string> resources)
    {
        Artifact = artifact;
        PackageDigest = packageDigest;
        ContractJson = contractJson;
        Contract = contract;
        StyleJson = styleJson;
        Styles = styles;
        ContextTypeId = context;
        UserRefTypeId = user;
        SessionRefTypeId = session;
        Views = views;
        Actions = actions;
        Resources = resources;
    }

    public static bool TryAdmit(VerifiedArtifact artifact, MetaExportCertificate exports, string contractJson,
        IReadOnlyDictionary<string, string> documents, string styleJson, string expectedPackageDigest,
        out MuiVerifiedApplication? application, out string error)
    {
        application = null;
        error = "MUI1301";
        if (exports.ArtifactDigest != artifact.Digest || exports.SourceTreeHash != artifact.Manifest.SourceTreeHash ||
            !MuiContractParser.TryParse(contractJson, out var contract, out _) ||
            !MuiStyleSheet.TryParse(styleJson, contract!, out var styles, out _) || documents.Count > 32 ||
            documents.Values.Sum(x => (long) Encoding.UTF8.GetByteCount(x)) > 1024 * 1024)
            return false;
        if (contract!.Resources.Any(path => !MuiResourceCatalog.IsBuiltin(path) &&
            (!path.EndsWith(".mui", StringComparison.Ordinal) || !documents.ContainsKey(path))) ||
            documents.Where(x => contract.Resources.Contains(x.Key)).Sum(x => (long) Encoding.UTF8.GetByteCount(x.Value)) > 64 * 1024)
            return false;
        var functions = new Dictionary<string, MetaFunctionExport>(StringComparer.Ordinal);
        foreach (var export in exports.Functions)
        {
            if (export.FunctionId < 0 || export.FunctionId >= artifact.FunctionCount || !functions.TryAdd(export.Name, export))
                return false;
            var actual = artifact.GetFunction(export.FunctionId);
            if (actual.Name != export.Name || actual.Initializer || actual.Async != export.Async ||
                artifact.GetType(actual.ReturnType).Name != export.ReturnType ||
                !actual.ParameterTypes.Select(x => artifact.GetType(x).Name).SequenceEqual(export.ParameterTypes))
                return false;
        }
        var publicTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var export in exports.Types)
        {
            if (export.TypeId < 0 || export.TypeId >= artifact.TypeCount || artifact.GetType(export.TypeId).Name != export.Name || !publicTypes.Add(export.Name))
                return false;
        }
        var context = FindType(artifact, "meta.app.AppContext");
        var user = FindType(artifact, "meta.app.UserRef");
        var session = FindType(artifact, "meta.app.SessionRef");
        if (context < 0 || user < 0 || session < 0 || artifact.GetType(user).Kind != MetaTypeKind.Opaque || artifact.GetType(session).Kind != MetaTypeKind.Opaque)
            return false;
        var views = ImmutableDictionary.CreateBuilder<string, MuiVerifiedView>();
        var actions = ImmutableDictionary.CreateBuilder<string, MuiVerifiedAction>();
        foreach (var (id, view) in contract!.Views)
        {
            if (view.StateType is null || !publicTypes.Contains(view.StateType) || !functions.TryGetValue(view.Function, out var export) ||
                !export.ParameterTypes.SequenceEqual(["meta.app.AppContext"]) || export.ReturnType != view.StateType ||
                !documents.TryGetValue(view.Document, out var source) || !MuiParser.TryParse(source, view, contract, documents, out var document, out _) ||
                !styles!.FitsBudget(document!))
                return false;
            var definition = artifact.GetFunction(export.FunctionId);
            if (!MatchesRecord(artifact, definition.ReturnType, view.Fields))
                return false;
            views.Add(id, new(export.FunctionId, definition.ReturnType, source, document!));
        }
        foreach (var (id, action) in contract.Actions)
        {
            if (action.InputType is null || !publicTypes.Contains(action.InputType) || !functions.TryGetValue(action.Function, out var export) ||
                !export.ParameterTypes.SequenceEqual(["meta.app.AppContext", action.InputType]) || export.ReturnType != "result<void>")
                return false;
            var definition = artifact.GetFunction(export.FunctionId);
            var result = artifact.GetType(definition.ReturnType);
            if (result.Kind != MetaTypeKind.Result || result.Args.Length != 1 || result.Args[0] != 0 ||
                !MatchesRecord(artifact, definition.ParameterTypes[1], action.Inputs))
                return false;
            actions.Add(id, new(export.FunctionId, definition.ParameterTypes[1]));
        }
        var digest = ComputePackageDigest(artifact.Digest, contractJson, documents, styleJson);
        if (expectedPackageDigest.Length != 64 || !string.Equals(digest, expectedPackageDigest, StringComparison.Ordinal))
            return false;
        application = new(artifact, digest, contractJson, contract, styleJson, styles!, context, user, session, views.ToImmutable(), actions.ToImmutable(),
            documents.Where(x => contract.Resources.Contains(x.Key)).ToImmutableDictionary());
        error = "";
        return true;
    }

    public static string ComputePackageDigest(string artifactDigest, string contractJson, IReadOnlyDictionary<string, string> documents, string styleJson)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add("meta-app-1.0");
        Add(artifactDigest);
        Add(contractJson);
        Add(styleJson);
        foreach (var document in documents.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            Add(document.Key);
            Add(document.Value);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());

        void Add(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            Span<byte> length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
    }

    private static int FindType(VerifiedArtifact artifact, string name)
    {
        for (var i = 0; i < artifact.TypeCount; i++)
            if (artifact.GetType(i).Name == name)
                return i;
        return -1;
    }

    private static bool MatchesRecord(VerifiedArtifact artifact, int id, IReadOnlyDictionary<string, MuiField> schema)
    {
        var type = artifact.GetType(id);
        if (type.Kind != MetaTypeKind.Struct || type.Fields.Length != schema.Count)
            return false;
        return type.Fields.All(x => schema.TryGetValue(x.Name, out var field) && MatchesType(artifact, x.TypeId, field, 0));
    }

    private static bool MatchesType(VerifiedArtifact artifact, int id, MuiField? field, int depth)
    {
        if (depth >= 16)
            return false;
        var type = artifact.GetType(id);
        if (type.Kind == MetaTypeKind.Option)
            return field is null ? MatchesType(artifact, type.Args[0], null, depth + 1) :
                field is { Required: false } && MatchesType(artifact, type.Args[0], field with { Required = true }, depth + 1);
        if (field is { Required: false })
            return false;
        var expected = type.Kind switch
        {
            MetaTypeKind.Bool => MuiValueKind.Boolean,
            MetaTypeKind.Byte or MetaTypeKind.Int => MuiValueKind.Integer,
            MetaTypeKind.Float => MuiValueKind.Number,
            MetaTypeKind.String or MetaTypeKind.Enum => MuiValueKind.String,
            MetaTypeKind.Struct => MuiValueKind.Object,
            MetaTypeKind.Array or MetaTypeKind.List => MuiValueKind.Array,
            MetaTypeKind.Map => MuiValueKind.Object,
            _ => (MuiValueKind?) null,
        };
        if (!expected.HasValue || field is not null && field.Kind != expected)
            return false;
        if (type.Kind is MetaTypeKind.Array or MetaTypeKind.List)
            return MatchesType(artifact, type.Args[0], null, depth + 1);
        if (type.Kind == MetaTypeKind.Map)
            return artifact.GetType(type.Args[0]).Kind == MetaTypeKind.String && MatchesType(artifact, type.Args[1], null, depth + 1);
        return type.Fields.All(x => MatchesType(artifact, x.TypeId, null, depth + 1));
    }
}
