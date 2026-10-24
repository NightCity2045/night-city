// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Security.Cryptography;
using System.Text.Json;
using NightCity.Diagnostics.Contracts;

namespace NightCity.Diagnostics.Core;

/// <summary>Selects an explicitly requested, verified acceptance bundle without modifying common diagnostic output.</summary>
public sealed record DiagnosticRuntimeBuild(string? BuildId, string ServerAssembly, string ClientAssembly, string Resources)
{
    public static DiagnosticRuntimeBuild Resolve(WorkspacePaths paths, string? buildId)
    {
        try { return ResolveCore(paths, buildId); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException)
        { throw new DiagnosticException("invalid_build", "The acceptance manifest has invalid metadata."); }
    }
    private static DiagnosticRuntimeBuild ResolveCore(WorkspacePaths paths, string? buildId)
    {
        if (string.IsNullOrEmpty(buildId)) return new(null,
            paths.Resolve("bin/NCDiagnostics/Content.Server/Content.Server.dll"),
            paths.Resolve("bin/NCDiagnostics/Content.Client/Content.Client.dll"), paths.Resolve("Resources"));
        if (!Guid.TryParseExact(buildId, "N", out var id))
            throw new DiagnosticException("invalid_build", "SS14_MCP_BUILD_ID must name a canonical acceptance UUID.");
        buildId = id.ToString("N");
        var root = paths.Resolve("artifacts/meta-acceptance/" + buildId);
        var manifestPath = paths.Resolve(Path.Combine(root, "build-manifest.json"));
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 128 * 1024)
            throw new DiagnosticException("invalid_build", "The requested acceptance manifest is missing or oversized.");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath), new JsonDocumentOptions { MaxDepth = 8 });
        var data = manifest.RootElement;
        var version = data.GetProperty("version").GetInt32();
        if (version is not (1 or 2) || data.GetProperty("buildId").GetString() != buildId)
            throw new DiagnosticException("invalid_build", "The acceptance manifest identity does not match.");
        var testDirectory = paths.Resolve(Path.Combine(root, "bin/Content.IntegrationTests"));
        var recordedDirectory = paths.Resolve(data.GetProperty("testDirectory").GetString()!);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(testDirectory, recordedDirectory, comparison))
            throw new DiagnosticException("invalid_build", "The acceptance test path is not the requested bundle.");
        var entries = data.GetProperty("assemblies").EnumerateArray().ToArray();
        if (entries.Length is < 8 or > 32) throw new DiagnosticException("invalid_build", "Invalid acceptance inventory.");
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var name = entry.GetProperty("name").GetString()!;
            if (name is not { Length: >= 1 and <= 128 } || Path.IsPathFullyQualified(name) || name.Contains('\\') || name.Contains(':') ||
                name.Split('/').Any(part => part is "" or "." or "..") || !hashes.TryAdd(name, entry.GetProperty("sha256").GetString()!))
                throw new DiagnosticException("invalid_build", "Invalid acceptance assembly name.");
            var inventoryRoot = version == 2 && name.StartsWith("MetaCompilerWorker/", StringComparison.Ordinal)
                ? Path.Combine(root, "bin") : testDirectory;
            RequireHash(paths.Resolve(Path.Combine(inventoryRoot, name)), hashes[name]);
        }
        string Assembly(string project, string file)
        {
            if (!hashes.TryGetValue(file, out var hash)) throw new DiagnosticException("invalid_build", "Required runtime assembly is absent.");
            var path = paths.Resolve(Path.Combine(root, "bin", project, file));
            RequireHash(path, hash); return path;
        }
        var server = Assembly("Content.Server", "Content.Server.dll");
        var client = Assembly("Content.Client", "Content.Client.dll");
        Assembly("Content.Server", "Content.Shared.dll"); Assembly("Content.Client", "Content.Shared.dll");
        Assembly("Content.Server", "Content.Meta.dll"); Assembly("Content.Client", "Content.Meta.dll");
        Assembly("Content.Server", "Content.Server.Database.dll");
        Assembly("Content.Server", "Content.Shared.Database.dll"); Assembly("Content.Client", "Content.Shared.Database.dll");
        foreach (var workerFile in new[] { "Meta.CompilerWorker.dll", "Meta.CompilerWorker.deps.json", "Meta.CompilerWorker.runtimeconfig.json", "Content.Meta.dll" })
        {
            if (!hashes.TryGetValue("MetaCompilerWorker/" + workerFile, out var hash))
                throw new DiagnosticException("invalid_build", "The verified compiler worker inventory is missing.");
            RequireHash(paths.Resolve(Path.Combine(root, version == 2 ? "bin/MetaCompilerWorker" : "bin/Content.Server/MetaCompilerWorker", workerFile)), hash);
        }
        if (!string.Equals(hashes["Content.Meta.dll"], hashes["MetaCompilerWorker/Content.Meta.dll"], StringComparison.OrdinalIgnoreCase))
            throw new DiagnosticException("invalid_build", "Runtime and compiler worker use different META cores.");
        var resources = paths.Resolve(Path.Combine(root, "Resources"));
        if (!Directory.Exists(resources) || !data.TryGetProperty("resources", out var snapshot) ||
            snapshot.ValueKind != JsonValueKind.Object || !snapshot.GetProperty("capturedBeforeBuild").GetBoolean() ||
            snapshot.GetProperty("buildId").GetString() != buildId ||
            !string.Equals(paths.Resolve(snapshot.GetProperty("directory").GetString()!), resources, comparison))
            throw new DiagnosticException("invalid_build", "The acceptance resources were not captured before compilation.");
        return new(buildId, server, client, resources);
    }
    private static void RequireHash(string path, string expected)
    {
        if (expected is not { Length: 64 } || !expected.All(char.IsAsciiHexDigit) || !File.Exists(path) ||
            !string.Equals(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), expected, StringComparison.OrdinalIgnoreCase))
            throw new DiagnosticException("invalid_build", "An acceptance assembly changed or is missing.");
    }
}
