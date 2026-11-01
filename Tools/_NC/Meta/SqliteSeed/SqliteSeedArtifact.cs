// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightCity.Diagnostics.Seeds;

/// <summary>BCL-only contract shared by the standalone SQLite helper and its process supervisor.</summary>
public static class SqliteSeedArtifact
{
    public const long MaximumDatabaseBytes = 16 * 1024 * 1024;
    public const int MaximumReportBytes = 8192;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8, PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
    };
    public sealed record Entry(string Name, long Bytes, string Sha256);
    public sealed record Bundle(int Version, string SeedId, string RuntimeId, Entry[] Files);
    public sealed record Report(string SeedId, string EnvironmentId, string Target, long Bytes, string Sha256,
        string DatabaseAssemblySha256, int SeedMigrations, int CurrentMigrations);
    public sealed record Validation(string SeedId, int SeedMigrations, int CurrentMigrations, string DatabaseAssemblySha256);
    public sealed record Staged(string SeedId, string HelperSha256, int Files, long Bytes);

    public static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static void RequireId(string value)
    {
        if (!Guid.TryParseExact(value, "N", out var id) || id == Guid.Empty || id.ToString("N") != value)
            throw new InvalidDataException("A canonical nonempty lowercase UUID is required.");
    }
    public static string Workspace(string root)
    {
        if (!Path.IsPathFullyQualified(root) || root.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidDataException("A local absolute workspace is required.");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        NoLinks(root);
        if (!File.Exists(Path.Combine(root, "global.json")) ||
            !File.Exists(Path.Combine(root, "Content.Server.Database", "Content.Server.Database.csproj")))
            throw new InvalidDataException("Workspace markers are missing.");
        return root;
    }
    public static string SeedDirectory(string root, string seedId)
    {
        root = Workspace(root); RequireId(seedId);
        var path = Path.Combine(root, "artifacts", "meta-db-bootstrap", seedId); NoLinks(path); return path;
    }
    public static string EnvironmentDirectory(string root, string environmentId)
    {
        root = Workspace(root); RequireId(environmentId);
        var path = Path.Combine(root, "artifacts", "ss14-mcp", "environments", environmentId); NoLinks(path);
        if (!Directory.Exists(path)) throw new InvalidDataException("The owned environment directory must already exist.");
        return path;
    }
    public static string DatabaseAssembly(string root, string environmentId) => Path.Combine(
        EnvironmentDirectory(root, environmentId), "server", "bin", "Content.Server", "Content.Server.Database.dll");
    public static string Target(string root, string environmentId) => Path.Combine(EnvironmentDirectory(root, environmentId), "server-data", "preferences.db");
    public static void NoLinks(string path)
    {
        for (var cursor = Path.GetFullPath(path); !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
        {
            var item = new FileInfo(cursor);
            if (item.LinkTarget != null || ((File.Exists(cursor) || Directory.Exists(cursor)) &&
                (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0))
                throw new InvalidDataException("SQLite tool paths cannot contain links or reparse points.");
        }
    }
    public static FileStream Pin(string path, long maximumBytes, bool readOnly = false)
    {
        NoLinks(path);
        if (readOnly && (File.GetAttributes(path) & FileAttributes.ReadOnly) == 0)
            throw new InvalidDataException("SQLite tool artifacts must be read-only.");
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 || file.Length > maximumBytes) { file.Dispose(); throw new InvalidDataException("SQLite tool artifact size limit."); }
        return file;
    }
    public static string Hash(Stream stream) { stream.Position = 0; return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    public static string HashFile(string path, long maximum = 32 * 1024 * 1024)
    { using var file = Pin(path, maximum); return Hash(file); }
    public static void NoSidecars(string path)
    {
        if (new[] { "-wal", "-shm", "-journal" }.Any(suffix => File.Exists(path + suffix) || Directory.Exists(path + suffix)))
            throw new InvalidDataException("SQLite tool artifacts cannot have sidecars.");
    }
    public static T ReadJson<T>(Stream stream) where T : class
    {
        stream.Position = 0;
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
        var nodes = 0; Check(document.RootElement);
        return document.RootElement.Deserialize<T>(Json) ?? throw new InvalidDataException("Missing SQLite tool metadata.");
        void Check(JsonElement element)
        {
            if (++nodes > 4096) throw new InvalidDataException("SQLite tool metadata node limit.");
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate SQLite tool metadata property."); Check(property.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) Check(child);
        }
    }

    /// <summary>Pins every executable/dependency byte until the owned helper exits.</summary>
    public static IDisposable PinBundle(string root, string seedId, string manifestSha256, out string helper)
    {
        if (!IsHash(manifestSha256)) throw new InvalidDataException("An explicit helper manifest SHA256 is required.");
        var directory = Path.Combine(SeedDirectory(root, seedId), "materializer", manifestSha256);
        helper = Path.Combine(directory, "SqliteSeed.dll");
        var pins = new List<FileStream>();
        try
        {
            var manifest = Pin(Path.Combine(directory, "helper-manifest.json"), 32768, true); pins.Add(manifest);
            if (Hash(manifest) != manifestSha256) throw new InvalidDataException("SQLite helper manifest hash mismatch.");
            var bundle = ReadJson<Bundle>(manifest);
            if (bundle.Version != 1 || bundle.SeedId != seedId || bundle.RuntimeId != System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier ||
                bundle.Files is not { Length: >= 4 and <= 64 }) throw new InvalidDataException("SQLite helper inventory invalid.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long bytes = 0;
            foreach (var entry in bundle.Files)
            {
                if (entry == null || !IsEntryName(entry.Name) || !names.Add(entry.Name) || !IsHash(entry.Sha256) || entry.Bytes is <= 0 or > 16 * 1024 * 1024 ||
                    (bytes += entry.Bytes) > 32 * 1024 * 1024) throw new InvalidDataException("SQLite helper inventory bounds invalid.");
                var file = Pin(Path.Combine(directory, entry.Name), 16 * 1024 * 1024, true); pins.Add(file);
                if (file.Length != entry.Bytes || Hash(file) != entry.Sha256) throw new InvalidDataException("SQLite helper file hash mismatch.");
            }
            foreach (var required in new[] { "SqliteSeed.dll", "SqliteSeed.deps.json", "SqliteSeed.runtimeconfig.json", "Microsoft.Data.Sqlite.dll" })
                if (!names.Contains(required)) throw new InvalidDataException("SQLite helper runtime is incomplete.");
            var actual = Inventory(directory).Where(name => name != "helper-manifest.json").ToArray();
            if (actual.Length != names.Count || actual.Any(name => !names.Contains(name))) throw new InvalidDataException("Unexpected SQLite helper files.");
            return new BundlePins(pins);
        }
        catch { foreach (var pin in pins) pin.Dispose(); throw; }
    }
    public static string[] Inventory(string directory)
    {
        NoLinks(directory); var names = new List<string>(); var pending = new Stack<string>(); pending.Push(directory); var directories = 0;
        while (pending.TryPop(out var current))
        {
            if (++directories > 16) throw new InvalidDataException("SQLite helper directory limit.");
            foreach (var item in Directory.EnumerateFileSystemEntries(current))
            {
                NoLinks(item);
                if (Directory.Exists(item)) pending.Push(item);
                else if (names.Count < 65) names.Add(Path.GetRelativePath(directory, item).Replace('\\', '/'));
                else throw new InvalidDataException("SQLite helper file limit.");
            }
        }
        return names.Order(StringComparer.Ordinal).ToArray();
    }
    public static bool IsEntryName(string? name) => name is { Length: >= 1 and <= 180 } &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '/') &&
        name.Split('/').All(part => part is not ("" or "." or "..")) &&
        (!name.Contains('/') ? name.EndsWith(".dll", StringComparison.Ordinal) || name.EndsWith(".json", StringComparison.Ordinal) :
            name.StartsWith("runtimes/", StringComparison.Ordinal) && name.Split('/').Length == 4 && name.Split('/')[2] == "native" &&
            (name.EndsWith(".dll", StringComparison.Ordinal) || name.EndsWith(".so", StringComparison.Ordinal) || name.EndsWith(".dylib", StringComparison.Ordinal)));
    private sealed class BundlePins(List<FileStream> pins) : IDisposable
    { public void Dispose() { foreach (var pin in pins) pin.Dispose(); } }
}
