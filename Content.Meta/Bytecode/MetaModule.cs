// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json.Serialization;

namespace Content.Meta.Bytecode;

public enum MetaTypeKind { Unit, Bool, Byte, Int, Float, String, Array, List, Map, Option, Result, Struct, Enum, Capability, Opaque, NativeDto }
public enum MetaConstantKind { Unit, Bool, Byte, Int, Float, String, Enum, Struct }
public enum MetaSectionKind : ushort { Manifest = 1, Types, Constants, Functions, Code, Imports, Links, Watches, Debug, Globals }

public sealed record MetaManifest
{
    public int FormatVersion { get; init; } = 1;
    public string PackageName { get; init; } = "";
    public string PackageVersion { get; init; } = "1.0.0";
    public string LanguageVersion { get; init; } = "1.0";
    public string RuntimeAbi { get; init; } = "1.0";
    public string CompilerBuild { get; init; } = "";
    public int GasPolicyVersion { get; init; } = 1;
    public string SourceTreeHash { get; init; } = new('0', 64);
    public string LockHash { get; init; } = new('0', 64);
    public string ApiCatalogHash { get; init; } = new('0', 64);
    public string RootModule { get; init; } = "";
    public int? MainFunctionId { get; init; }
    public int? InitializerFunctionId { get; init; }
    public string[] RequiredScopes { get; init; } = [];
}

public sealed record MetaTypeDef
{
    public int Id { get; init; }
    public MetaTypeKind Kind { get; init; }
    public string Name { get; init; } = "";
    public int[] Args { get; init; } = [];
    public MetaFieldDef[] Fields { get; init; } = [];
    public MetaVariantDef[] Variants { get; init; } = [];
    public string? ContractHash { get; init; }

    public static MetaTypeDef[] Primitives => [
        new() { Id = 0, Kind = MetaTypeKind.Unit, Name = "void" },
        new() { Id = 1, Kind = MetaTypeKind.Bool, Name = "bool" },
        new() { Id = 2, Kind = MetaTypeKind.Byte, Name = "byte" },
        new() { Id = 3, Kind = MetaTypeKind.Int, Name = "int" },
        new() { Id = 4, Kind = MetaTypeKind.Float, Name = "float" },
        new() { Id = 5, Kind = MetaTypeKind.String, Name = "string" }];
}

public sealed record MetaFieldDef(string Name, int TypeId, bool Readonly);
public sealed record MetaVariantDef(string Name, int Tag, int? PayloadTypeId);
public sealed record MetaConstantDef
{
    public int TypeId { get; init; }
    public MetaConstantKind Kind { get; init; }
    public string? Value { get; init; }
    public int[] Items { get; init; } = [];
}
public sealed record MetaLocalDef(int TypeId, bool Mutable, bool Parameter);
public sealed record MetaFunctionDef
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public int[] ParameterTypes { get; init; } = [];
    public int ReturnType { get; init; }
    [JsonPropertyName("async")] public bool Async { get; init; }
    public bool Initializer { get; init; }
    public MetaLocalDef[] Locals { get; init; } = [];
    public int CodeOffset { get; init; }
    public int CodeLength { get; init; }
    public int MaxStack { get; init; }
}
public sealed record MetaImportDef
{
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Version { get; init; }
    public string? ContentHash { get; init; }
    public string ContractHash { get; init; } = "";
    public int? CapabilityTypeId { get; init; }
    public int[] ParameterTypes { get; init; } = [];
    public int? ReturnType { get; init; }
    public string Effect { get; init; } = "None";
}
public sealed record MetaLinkDef(int Id, string SlotName, int CapabilityTypeId, string ContractHash);
public sealed record MetaWatchDef(int LinkId, int EventImportId, int FunctionId, string Delivery, int Capacity);
public sealed record MetaGlobalDef(int Id, int TypeId, bool Mutable, string Name);
public sealed record MetaDebugFile(int Id, string Path, string SourceHash);
public sealed record MetaDebugSpan(int FunctionId, int PcOffset, int FileId, int Utf8Start, int Utf8Length);
public sealed record MetaLocalName(int FunctionId, int LocalId, string Name);
public sealed record MetaDebugInfo(MetaDebugFile[] Files, MetaDebugSpan[] Spans, MetaLocalName[] Locals);

/// <summary>Untrusted compiler/loader DTO. Only MetaVerifier can create an executable VerifiedArtifact.</summary>
public sealed record MetaModule
{
    public MetaManifest Manifest { get; init; } = new();
    public MetaTypeDef[] Types { get; init; } = MetaTypeDef.Primitives;
    public MetaConstantDef[] Constants { get; init; } = [];
    public MetaFunctionDef[] Functions { get; init; } = [];
    public byte[] Code { get; init; } = [];
    public MetaImportDef[] Imports { get; init; } = [];
    public MetaLinkDef[] Links { get; init; } = [];
    public MetaWatchDef[] Watches { get; init; } = [];
    public MetaDebugInfo? Debug { get; init; }
    public MetaGlobalDef[] Globals { get; init; } = [];
}

public sealed class MetaFormatException(string message) : Exception(message);

public sealed record MetaLimits
{
    public int ArtifactBytes { get; init; } = 1024 * 1024;
    public int Functions { get; init; } = 4096;
    public int Imports { get; init; } = 256;
    public int Types { get; init; } = 4096;
    public int Constants { get; init; } = 16384;
    public int LocalsPerFunction { get; init; } = 4096;
    public int OperandSlots { get; init; } = 4096;
    public int Globals { get; init; } = 4096;
    public int StringBytes { get; init; } = 65536;
    public int EventCapacity { get; init; } = 64;
    public int VerificationSteps { get; init; } = 1_000_000;
    public long VerificationMemoryBytes { get; init; } = 32 * 1024 * 1024;
}
