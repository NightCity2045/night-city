// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
#if TOOLS && NC_DIAGNOSTICS
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Content.Server.Database;

/// <summary>Explicit isolated test bootstrap. Copies pristine schema/defaults; never replaces normal EF migration.</summary>
public static class NCMetaSqliteSeedPackage
{
    public const string WorkspaceVariable = "NC_DIAGNOSTICS_WORKSPACE";
    public const string IdVariable = "NC_DIAGNOSTICS_SQLITE_SEED_ID";
    public const string SeedHashVariable = "NC_DIAGNOSTICS_SQLITE_SEED_SHA256";
    public const string ManifestHashVariable = "NC_DIAGNOSTICS_SQLITE_MANIFEST_SHA256";
    private const long MaximumBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { MaxDepth = 8, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public sealed record Request(string Workspace, string Id, string SeedSha256, string ManifestSha256);
    public sealed record History(string Id, string ProductVersion);
    public sealed record Schema(string Type, string Name, string Table, string SqlSha256);
    public sealed record Manifest(int Format, string Kind, string PackageId, long Bytes, string Sha256,
        int DefaultPolicy, History[] History, Schema[] Schema);
    public sealed record Package(string Id, string SeedSha256, string ManifestSha256, long Bytes, int Migrations);

    public static bool Requested => new[] { WorkspaceVariable, IdVariable, SeedHashVariable, ManifestHashVariable }
        .Any(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)));
    public static Request? ReadEnvironment()
    {
        if (!Requested) return null;
        if (Environment.GetEnvironmentVariable("NC_DIAGNOSTICS_ISOLATED") != "1") throw new InvalidOperationException("SQLite seed requires explicit isolated diagnostics.");
        var request = new Request(Environment.GetEnvironmentVariable(WorkspaceVariable) ?? "", Environment.GetEnvironmentVariable(IdVariable) ?? "",
            Environment.GetEnvironmentVariable(SeedHashVariable) ?? "", Environment.GetEnvironmentVariable(ManifestHashVariable) ?? "");
        ValidateRequest(request);
        return request;
    }
    private static void ValidateRequest(Request request)
    {
        if (!Guid.TryParseExact(request.Id, "N", out var id) || id == Guid.Empty || id.ToString("N") != request.Id ||
            !Digest(request.SeedSha256) || !Digest(request.ManifestSha256)) throw new InvalidDataException("Incomplete or malformed SQLite seed selection.");
        ValidateWorkspace(request.Workspace);
    }
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Hash(Stream stream) { stream.Position = 0; return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static void NoReparse(string path)
    {
        for (FileSystemInfo? item = File.Exists(path) ? new FileInfo(path) : new DirectoryInfo(path); item != null;
             item = item is FileInfo file ? file.Directory : ((DirectoryInfo) item).Parent)
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("SQLite seed paths cannot contain reparse points.");
    }
    private static string ValidateWorkspace(string workspace)
    {
        if (!Path.IsPathFullyQualified(workspace) || workspace.StartsWith("\\\\", StringComparison.Ordinal)) throw new InvalidDataException("SQLite seed workspace must be a local absolute path.");
        workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        NoReparse(workspace);
        if (!File.Exists(Path.Combine(workspace, "global.json")) || !File.Exists(Path.Combine(workspace, "Content.Server.Database", "Content.Server.Database.csproj")))
            throw new InvalidDataException("SQLite seed workspace markers are missing.");
        return workspace;
    }
    private static string SeedDirectory(string workspace, string id) => Path.Combine(ValidateWorkspace(workspace), "artifacts", "meta-db-bootstrap", id);
    private static void NoSidecars(string path)
    {
        if (new[] { "-wal", "-shm", "-journal" }.Any(suffix => File.Exists(path + suffix))) throw new InvalidDataException("SQLite seed cannot use sidecar state.");
    }
    private static FileStream Pin(string path, long limit, bool requireReadonly)
    {
        NoReparse(path);
        if (requireReadonly && (File.GetAttributes(path) & FileAttributes.ReadOnly) == 0) throw new InvalidDataException("SQLite seed package must be read-only.");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 || stream.Length > limit) { stream.Dispose(); throw new InvalidDataException("SQLite seed file size limit."); }
        return stream;
    }
    private static SqliteConnection OpenSource(string path)
    {
        var source = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?mode=ro&immutable=1", Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 2 }.ToString());
        try
        {
            source.Open(); source.EnableExtensions(false);
            Execute(source, "PRAGMA query_only=ON; PRAGMA trusted_schema=OFF;");
            return source;
        }
        catch { source.Dispose(); throw; }
    }
    private static void Execute(SqliteConnection connection, string sql)
    { using var command = connection.CreateCommand(); command.CommandText = sql; command.CommandTimeout = 2; command.ExecuteNonQuery(); }
    private static List<object?[]> Rows(SqliteConnection connection, string sql, int maximum = 1024)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql; command.CommandTimeout = 2;
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            if (rows.Count >= maximum) throw new InvalidDataException("SQLite seed row limit.");
            var values = new object[reader.FieldCount]; reader.GetValues(values); rows.Add(values);
        }
        return rows;
    }
    private static string Text(object? value) => value is string text ? text : throw new InvalidDataException("SQLite seed text type.");
    private static long Integer(object? value) => value is long integer ? integer : throw new InvalidDataException("SQLite seed integer type.");
    private static string Identifier(string value)
    {
        if (!Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("Invalid SQLite seed identifier.");
        return "\"" + value + "\"";
    }
    private static (History[] History, Schema[] Schema) Inspect(SqliteConnection source, IReadOnlyList<string> known, bool memory = false)
    {
        var quick = Rows(source, "PRAGMA quick_check;", 2);
        if (quick.Count != 1 || Text(quick[0][0]) != "ok" || Rows(source, "PRAGMA foreign_key_check;", 1).Count != 0 ||
            Text(Rows(source, "PRAGMA journal_mode;", 1).Single()[0]) != (memory ? "memory" : "delete")) throw new InvalidDataException("SQLite seed integrity or journal mode invalid.");
        var raw = Rows(source, "SELECT type,name,tbl_name,sql FROM sqlite_schema ORDER BY type,name;", 768);
        if (raw.Count(row => Text(row[0]) == "table") > 256 || raw.Count(row => Text(row[0]) == "index") > 512) throw new InvalidDataException("SQLite seed schema size limit.");
        var schema = raw.Select(row =>
        {
            var type = Text(row[0]); var name = Text(row[1]); var table = Text(row[2]);
            Identifier(name); Identifier(table);
            var sql = row[3] is DBNull ? "" : Text(row[3]);
            if (type is not ("table" or "index") || sql.TrimStart().StartsWith("CREATE VIRTUAL", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SQLite seed admits only ordinary tables/indexes.");
            return new Schema(type, name, table, Hash(Encoding.UTF8.GetBytes(sql)));
        }).ToArray();
        var history = Rows(source, "SELECT MigrationId,ProductVersion FROM \"__EFMigrationsHistory\" ORDER BY MigrationId;", 1024)
            .Select(row => new History(Text(row[0]), Text(row[1]))).ToArray();
        if (history.Length == 0 || history.Length > known.Count || !history.Select(row => row.Id).SequenceEqual(known.Take(history.Length)))
            throw new InvalidDataException("SQLite seed history is not a prefix of current trusted migrations.");
        if (history.Any(row => row.Id == "20220313151753_ServerNameFts") && !schema.Any(item => item.Type == "table" && item.Name == "server") ||
            history.Any(row => row.Id == "20261024102239_NCMetaStorageAdmission") && !schema.Any(item => item.Type == "table" && item.Name == "nc_meta_blob_storage"))
            throw new InvalidDataException("SQLite migration defaults are missing.");
        foreach (var table in schema.Where(item => item.Type == "table"))
        {
            var name = Identifier(table.Name);
            if (table.Name == "__EFMigrationsHistory") continue;
            if (table.Name == "server")
            {
                ExactColumns(source, table.Name, ["server_id", "name"]);
                var rows = Rows(source, "SELECT server_id,name FROM server;", 2);
                if (rows.Count != 1 || Integer(rows[0][0]) != 0 || Text(rows[0][1]) != "unknown") throw new InvalidDataException("Unexpected server seed data.");
            }
            else if (table.Name == "nc_meta_blob_storage")
            {
                ExactColumns(source, table.Name, ["nc_meta_blob_storage_id", "allocated_count", "allocated_bytes", "backfill_cursor", "backfill_complete", "count_tokens", "byte_tokens", "refilled_at_utc_ticks"]);
                var rows = Rows(source, "SELECT nc_meta_blob_storage_id,allocated_count,allocated_bytes,backfill_cursor,backfill_complete,count_tokens,byte_tokens,refilled_at_utc_ticks FROM nc_meta_blob_storage;", 2);
                if (rows.Count != 1 || Integer(rows[0][0]) != 1 || Integer(rows[0][1]) != 0 || Integer(rows[0][2]) != 0 || Text(rows[0][3]) != "" ||
                    Integer(rows[0][4]) != 0 || Convert.ToDouble(rows[0][5]) != 0 || Convert.ToDouble(rows[0][6]) != 0 || Integer(rows[0][7]) != 0)
                    throw new InvalidDataException("Unexpected blob storage seed data.");
            }
            else if (table.Name == "sqlite_sequence")
            {
                var sequences = Rows(source, "SELECT name,seq FROM sqlite_sequence;", 256);
                if (sequences.Any(row => Integer(row[1]) != 0 || !schema.Any(item => item.Type == "table" && item.Name == Text(row[0]))) ||
                    sequences.Select(row => Text(row[0])).Distinct().Count() != sequences.Count) throw new InvalidDataException("Non-pristine SQLite sequences.");
            }
            else if (Rows(source, "SELECT 1 FROM " + name + " LIMIT 1;", 1).Count != 0) throw new InvalidDataException("SQLite seed contains application data.");
        }
        return (history, schema);
    }
    private static void ExactColumns(SqliteConnection source, string table, string[] expected)
    {
        if (!Rows(source, "PRAGMA table_info(" + Identifier(table) + ");", 32).Select(row => Text(row[1])).SequenceEqual(expected))
            throw new InvalidDataException("SQLite default table contains unreviewed columns.");
    }
    private static Manifest ReadManifest(FileStream stream)
    {
        stream.Position = 0;
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
        var nodes = 0;
        Validate(document.RootElement);
        return document.RootElement.Deserialize<Manifest>(Json) ?? throw new InvalidDataException("Missing seed manifest.");
        void Validate(JsonElement element)
        {
            if (++nodes > 8192) throw new InvalidDataException("Seed manifest node limit.");
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject()) { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate seed manifest property."); Validate(property.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Validate(item);
        }
    }
    public static void LoadIntoNewMemory(SqliteConnection destination, Request request, IReadOnlyList<string> knownMigrations)
    {
        try
        {
            ValidateRequest(request);
            if (destination.State != ConnectionState.Open || new SqliteConnectionStringBuilder(destination.ConnectionString).DataSource != ":memory:" ||
                Rows(destination, "PRAGMA database_list;", 2) is not { Count: 1 } databases || Text(databases[0][1]) != "main" || Text(databases[0][2]) != "" ||
                Rows(destination, "SELECT 1 FROM sqlite_schema LIMIT 1;", 1).Count != 0 || Rows(destination, "SELECT 1 FROM sqlite_temp_schema LIMIT 1;", 1).Count != 0)
                throw new InvalidDataException("Seed destination must be a fresh sole in-memory database.");
            var directory = SeedDirectory(request.Workspace, request.Id);
            var seed = Path.Combine(directory, "seed.db"); var manifestPath = Path.Combine(directory, "manifest.json");
            using var seedGuard = Pin(seed, MaximumBytes, true); using var manifestGuard = Pin(manifestPath, 65536, true);
            NoSidecars(seed);
            if (Hash(seedGuard) != request.SeedSha256 || Hash(manifestGuard) != request.ManifestSha256) throw new InvalidDataException("SQLite seed package hash mismatch.");
            var manifest = ReadManifest(manifestGuard);
            if (manifest.Format != 1 || manifest.Kind != "night-city-sqlite-pristine" || manifest.DefaultPolicy != 1 || manifest.PackageId != request.Id ||
                manifest.Bytes != seedGuard.Length || manifest.Sha256 != request.SeedSha256 || manifest.History == null || manifest.Schema == null)
                throw new InvalidDataException("Unsupported SQLite seed manifest.");
            using var source = OpenSource(seed);
            var before = Inspect(source, knownMigrations);
            if (!before.History.SequenceEqual(manifest.History) || !before.Schema.SequenceEqual(manifest.Schema)) throw new InvalidDataException("SQLite seed inventory mismatch.");
            source.BackupDatabase(destination);
            var after = Inspect(destination, knownMigrations, memory: true);
            if (!before.History.SequenceEqual(after.History) || !before.Schema.SequenceEqual(after.Schema) || Hash(seedGuard) != request.SeedSha256 ||
                Hash(manifestGuard) != request.ManifestSha256) throw new InvalidDataException("SQLite backup or immutable source validation failed.");
            NoSidecars(seed);
        }
        catch { destination.Dispose(); throw; }
    }
    public static Package CreatePackage(string workspace, string sourcePath, string expectedSourceHash, IReadOnlyList<string> knownMigrations)
    {
        workspace = ValidateWorkspace(workspace);
        sourcePath = Path.GetFullPath(sourcePath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!Digest(expectedSourceHash) || !string.Equals(Path.GetDirectoryName(sourcePath), Path.Combine(workspace, "artifacts"), comparison) ||
            !Regex.IsMatch(Path.GetFileName(sourcePath), "^meta-sqlite-[a-f0-9]{32}\\.db$", RegexOptions.CultureInvariant)) throw new InvalidDataException("Only an explicit owned fresh migration artifact may seed tests.");
        using var guard = Pin(sourcePath, MaximumBytes, false); NoSidecars(sourcePath);
        if (Hash(guard) != expectedSourceHash) throw new InvalidDataException("Pristine source hash mismatch.");
        using var source = OpenSource(sourcePath);
        var before = Inspect(source, knownMigrations);
        var id = Guid.NewGuid().ToString("N"); var directory = SeedDirectory(workspace, id);
        var parent = Path.GetDirectoryName(directory)!;
        Directory.CreateDirectory(parent); NoReparse(parent);
        if (Directory.Exists(directory)) throw new InvalidDataException("Seed package already exists.");
        Directory.CreateDirectory(directory);
        var seed = Path.Combine(directory, "seed.db");
        using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = seed, Pooling = false }.ToString()))
        {
            destination.Open(); source.BackupDatabase(destination);
            Execute(destination, "PRAGMA journal_mode=DELETE;"); // Only the NEW package is normalized; source is immutable/read-only.
            var copy = Inspect(destination, knownMigrations);
            if (!before.History.SequenceEqual(copy.History) || !before.Schema.SequenceEqual(copy.Schema)) throw new InvalidDataException("Seed backup differs from source.");
        }
        NoSidecars(seed); NoSidecars(sourcePath);
        if (Hash(guard) != expectedSourceHash) throw new InvalidDataException("Pristine source changed.");
        string sha; long bytes;
        using (var seedGuard = Pin(seed, MaximumBytes, false)) { sha = Hash(seedGuard); bytes = seedGuard.Length; }
        var manifest = new Manifest(1, "night-city-sqlite-pristine", id, bytes, sha, 1, before.History, before.Schema);
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        if (json.Length > 65536) throw new InvalidDataException("Seed manifest size limit.");
        var manifestPath = Path.Combine(directory, "manifest.json");
        using (var output = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(json); output.Flush(true); }
        File.SetAttributes(seed, File.GetAttributes(seed) | FileAttributes.ReadOnly);
        File.SetAttributes(manifestPath, File.GetAttributes(manifestPath) | FileAttributes.ReadOnly);
        return new(id, sha, Hash(json), bytes, before.History.Length);
    }
    /// <summary>Reads trusted migration attributes without loading assemblies or building an extra EF model.</summary>
    public static string[] ReadSqliteMigrationIds(string databaseAssembly)
    {
        using var stream = File.OpenRead(databaseAssembly); using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader(); var result = new List<string>();
        foreach (var handle in metadata.TypeDefinitions)
        {
            var definition = metadata.GetTypeDefinition(handle); string? migration = null; string? context = null;
            foreach (var attributeHandle in definition.GetCustomAttributes())
            {
                var attribute = metadata.GetCustomAttribute(attributeHandle);
                if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
                var member = metadata.GetMemberReference((MemberReferenceHandle) attribute.Constructor);
                if (member.Parent.Kind != HandleKind.TypeReference) continue;
                var type = metadata.GetTypeReference((TypeReferenceHandle) member.Parent);
                var ns = metadata.GetString(type.Namespace); var name = metadata.GetString(type.Name);
                if (ns != "Microsoft.EntityFrameworkCore.Migrations" && ns != "Microsoft.EntityFrameworkCore.Infrastructure") continue;
                var blob = metadata.GetBlobReader(attribute.Value);
                if (blob.ReadUInt16() != 1) throw new InvalidDataException("Invalid migration attribute.");
                if (name == "MigrationAttribute") migration = blob.ReadSerializedString();
                else if (name == "DbContextAttribute") context = blob.ReadSerializedString()?.Split(',')[0];
            }
            if (context == "Content.Server.Database.SqliteServerDbContext" && migration != null) result.Add(migration);
        }
        if (result.Count is < 1 or > 1024 || result.Distinct(StringComparer.Ordinal).Count() != result.Count) throw new InvalidDataException("Trusted SQLite migration inventory invalid.");
        return result.Order(StringComparer.Ordinal).ToArray();
    }
}
#endif
