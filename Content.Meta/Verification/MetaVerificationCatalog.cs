// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Bytecode;

namespace Content.Meta.Verification;

/// <summary>Trusted, server-owned ABI allowlist. Artifact declarations never extend this catalog.</summary>
public sealed record MetaVerificationCatalog
{
    public string Hash { get; init; } = new('0', 64);
    public MetaTypeDef[] NativeTypes { get; init; } = [];
    public MetaImportDef[] Imports { get; init; } = [];
    public string[] ConstructibleEnums { get; init; } = [];
    public MetaEventContract[] Events { get; init; } = [];
}

public sealed record MetaEventContract(string Name, string Delivery, int Capacity);

public sealed class VerifiedArtifact
{
    internal MetaModule Module { get; }
    internal MetaInstruction[][] Instructions { get; }
    internal Dictionary<int, int>[] InstructionIndices { get; }
    internal Dictionary<(uint Intrinsic, uint Type), MetaIntrinsicSignature> Intrinsics { get; }
    public string Digest { get; }
    public string ContentHash { get; }
    /// <summary>Conservative managed residency estimate, including decoded instructions, indices and retained metadata.</summary>
    public long ResidentMemoryBytes { get; }
    /// <summary>Guest tables, immutable constants and the largest initial function frame; excludes dynamic allocations.</summary>
    public long MinimumProcessMemoryBytes { get; }
    public int MaximumConstantStringBytes { get; }
    public int VerifierPolicyVersion => MetaVerifier.PolicyVersion;
    public int FunctionCount => Module.Functions.Length;
    public int TypeCount => Module.Types.Length;
    public MetaManifest Manifest => Module.Manifest with { RequiredScopes = Module.Manifest.RequiredScopes.ToArray() };

    internal VerifiedArtifact(MetaModule module, string digest, string contentHash, MetaInstruction[][] instructions)
    {
        Module = module;
        Digest = digest;
        ContentHash = contentHash;
        Instructions = instructions;
        InstructionIndices = instructions.Select(function => function.Select((instruction, index) => (instruction.Offset, index)).ToDictionary(pair => pair.Offset, pair => pair.index)).ToArray();
        Intrinsics = instructions.SelectMany(function => function).Where(instruction => instruction.Opcode == MetaOpcode.CallIntrinsic).Select(instruction => (instruction.A, instruction.B)).Distinct().ToDictionary(key => key, key => MetaIntrinsics.Resolve(module, key.A, key.B));
        ResidentMemoryBytes = EstimateResidency(module, instructions, Intrinsics.Values);
        long minimum = checked(512L + 16L * (module.Globals.Length + module.Constants.Length) + 128L * module.Links.Length);
        foreach (var constant in module.Constants)
        {
            if (constant.Kind == MetaConstantKind.String)
            {
                var utf8 = System.Text.Encoding.UTF8.GetByteCount(constant.Value!);
                MaximumConstantStringBytes = Math.Max(MaximumConstantStringBytes, utf8);
                minimum = checked(minimum + 160 + constant.Value!.Length * 2L + utf8);
            }
            else if (constant.Kind == MetaConstantKind.Struct) minimum = checked(minimum + 160 + constant.Items.Length * 16L);
        }
        MinimumProcessMemoryBytes = checked(minimum + module.Functions.Select(function => 128L + 16L * (function.Locals.Length + function.MaxStack)).DefaultIfEmpty(0).Max());
    }

    private static long EstimateResidency(MetaModule module, MetaInstruction[][] instructions, IEnumerable<MetaIntrinsicSignature> signatures)
    {
        // Count each record and string independently even when the loader happens to share it. The margins cover
        // CLR headers, alignment, spare dictionary capacity and nested array headers; serialized byte size alone does not.
        long bytes = 1024;
        void Text(string? value) { if (value != null) bytes = checked(bytes + 64 + value.Length * 2L); }
        void Table(int count, int recordBytes = 256) => bytes = checked(bytes + 32 + count * (8L + recordBytes));
        var manifest = module.Manifest;
        foreach (var value in new[] { manifest.PackageName, manifest.PackageVersion, manifest.LanguageVersion, manifest.RuntimeAbi, manifest.CompilerBuild, manifest.SourceTreeHash, manifest.LockHash, manifest.ApiCatalogHash, manifest.RootModule }) Text(value);
        Table(manifest.RequiredScopes.Length, 0); foreach (var scope in manifest.RequiredScopes) Text(scope);
        Table(module.Types.Length);
        foreach (var type in module.Types)
        {
            Text(type.Name); Text(type.ContractHash); Table(type.Args.Length, 0); Table(type.Fields.Length); Table(type.Variants.Length);
            foreach (var field in type.Fields) Text(field.Name);
            foreach (var variant in type.Variants) Text(variant.Name);
        }
        Table(module.Constants.Length);
        foreach (var constant in module.Constants) { Text(constant.Value); Table(constant.Items.Length, 0); }
        Table(module.Functions.Length);
        foreach (var function in module.Functions) { Text(function.Name); Table(function.ParameterTypes.Length, 0); Table(function.Locals.Length); }
        Table(module.Imports.Length);
        foreach (var import in module.Imports) { Text(import.Kind); Text(import.Name); Text(import.Version); Text(import.ContentHash); Text(import.ContractHash); Text(import.Effect); Table(import.ParameterTypes.Length, 0); }
        Table(module.Links.Length); foreach (var link in module.Links) { Text(link.SlotName); Text(link.ContractHash); }
        Table(module.Watches.Length); foreach (var watch in module.Watches) Text(watch.Delivery);
        Table(module.Globals.Length); foreach (var global in module.Globals) Text(global.Name);
        if (module.Debug is { } debug)
        {
            Table(debug.Files.Length); foreach (var file in debug.Files) { Text(file.Path); Text(file.SourceHash); }
            Table(debug.Spans.Length); Table(debug.Locals.Length); foreach (var local in debug.Locals) Text(local.Name);
        }
        bytes = checked(bytes + 32 + module.Code.Length);
        Table(instructions.Length, 128);
        foreach (var function in instructions) bytes = checked(bytes + 64 + function.Length * 160L);
        foreach (var signature in signatures) { bytes = checked(bytes + 256 + signature.Parameters.Length * 8L); Text(signature.Effect); }
        Text(new string('0', 64)); Text(new string('0', 64));
        return bytes;
    }

    public MetaFunctionDef GetFunction(int id) => Module.Functions[id] with { ParameterTypes = Module.Functions[id].ParameterTypes.ToArray(), Locals = Module.Functions[id].Locals.ToArray() };
    public MetaTypeDef GetType(int id) => Module.Types[id] with { Args = Module.Types[id].Args.ToArray(), Fields = Module.Types[id].Fields.ToArray(), Variants = Module.Types[id].Variants.ToArray() };
    public MetaWatchDef[] GetWatches() => Module.Watches.ToArray();
    public MetaLinkDef[] GetLinks() => Module.Links.ToArray();
    public MetaImportDef GetImport(int id) => Module.Imports[id] with { ParameterTypes = Module.Imports[id].ParameterTypes.ToArray() };
    public MetaDebugInfo? GetDebugInfo() => Module.Debug is { } debug ? new MetaDebugInfo(debug.Files.ToArray(), debug.Spans.ToArray(), debug.Locals.ToArray()) : null;
}

public readonly record struct MetaIntrinsicSignature(int[] Parameters, int ReturnType, string Effect);

public static class MetaIntrinsics
{
    public const uint Monomorphic = uint.MaxValue;

    public static MetaIntrinsicSignature Resolve(MetaModule module, uint intrinsicId, uint typeId)
    {
        int Named(string name)
        {
            var matches = module.Types.Where(type => type.Name == name).ToArray();
            if (matches.Length != 1) throw new MetaFormatException("Missing intrinsic ABI type: " + name);
            return matches[0].Id;
        }
        int Container(MetaTypeKind kind, int item)
        {
            var match = Array.FindIndex(module.Types, type => type.Kind == kind && type.Args.Length == 1 && type.Args[0] == item);
            if (match < 0) throw new MetaFormatException("Missing intrinsic result container.");
            return match;
        }
        MetaIntrinsicSignature Sig(int[] parameters, int result, string effect = "Pure") => new(parameters, result, effect);
        if (intrinsicId < 0x0100 && typeId != Monomorphic) throw new MetaFormatException("Monomorphic intrinsic type operand.");
        switch (intrinsicId)
        {
            case 0x0001: return Sig([5], 0, "Log");
            case 0x0002: return Sig([Named("meta.system.SampleStamp"), 3], 1, "ReadClock");
            case 0x0003: return Sig([], 3, "ReadClock");
            case 0x0004: return Sig([Named("meta.system.ErrorCode"), 5], Named("meta.system.Error"));
            case 0x0010: return Sig([3], 5);
            case 0x0011: return Sig([4], 5);
            case 0x0012: return Sig([5], Container(MetaTypeKind.Result, 3));
            case 0x0013: return Sig([5], Container(MetaTypeKind.Result, 4));
            case 0x0014: return Sig([5, 3, 3], Container(MetaTypeKind.Result, 5));
            case 0x0015 or 0x0016: return Sig([5], 3);
            case 0x0017: return Sig([5, 5], 5);
            case 0x0020: return Sig([3], 4);
            case 0x0021: return Sig([4], Container(MetaTypeKind.Result, 3));
            case 0x0022: return Sig([3], Container(MetaTypeKind.Result, 2));
            case 0x0023: return Sig([3], 3);
            case 0x0024: return Sig([4], 4);
            case 0x0025: return Sig([2], 3);
            case 0x0030: return Sig([Named("meta.app.SessionRef"), Named("meta.app.SessionRef")], 1, "ReadLocal");
        }
        if (typeId >= module.Types.Length) throw new MetaFormatException("Intrinsic collection type out of range.");
        var type = module.Types[typeId];
        var id = checked((int) typeId);
        if (intrinsicId is >= 0x0100 and <= 0x0107 && type.Kind == MetaTypeKind.List)
        {
            var item = type.Args[0];
            return intrinsicId switch
            {
                0x0100 => Sig([], id, "Allocate"),
                0x0101 or 0x0106 => Sig([id], 3, "ReadLocal"),
                0x0102 => Sig([id, 3], item, "ReadLocal"),
                0x0103 => Sig([id, 3, item], 0, "MutateLocal"),
                0x0104 => Sig([id, item], 0, "MutateLocal"),
                0x0105 => Sig([id], Container(MetaTypeKind.Option, item), "MutateLocal"),
                _ => Sig([id, 3], 0, "ReadLocal")
            };
        }
        if (intrinsicId is >= 0x0110 and <= 0x0114 && type.Kind == MetaTypeKind.Map)
        {
            var key = type.Args[0];
            var value = type.Args[1];
            return intrinsicId switch
            {
                0x0110 => Sig([], id, "Allocate"),
                0x0111 => Sig([id, key], Container(MetaTypeKind.Option, value), "ReadLocal"),
                0x0112 => Sig([id, key, value], 0, "MutateLocal"),
                0x0113 => Sig([id, key], 1, "MutateLocal"),
                _ => Sig([id], Container(MetaTypeKind.Array, key), "ReadLocal")
            };
        }
        throw new MetaFormatException("Unknown intrinsic or invalid collection type.");
    }
}
