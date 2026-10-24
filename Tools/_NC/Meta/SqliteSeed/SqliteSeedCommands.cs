// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Content.Server.Database;
using Microsoft.Data.Sqlite;

namespace NightCity.Diagnostics.Seeds;

public static class SqliteSeedCommands
{
    public static SqliteSeedArtifact.Validation Validate(NCMetaSqliteSeedPackage.Request request, string databaseAssembly, string databaseSha256)
    {
        var workspace = SqliteSeedArtifact.Workspace(request.Workspace);
        databaseAssembly = Path.GetFullPath(databaseAssembly);
        if (!databaseAssembly.StartsWith(workspace + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            Path.GetFileName(databaseAssembly) != "Content.Server.Database.dll") throw new InvalidDataException("Expected a workspace Server.Database assembly.");
        using var assembly = PinAssembly(databaseAssembly, databaseSha256);
        var known = NCMetaSqliteSeedPackage.ReadSqliteMigrationIds(databaseAssembly);
        using var memory = OpenMemory(); NCMetaSqliteSeedPackage.LoadIntoNewMemory(memory, request, known);
        return new(request.Id, HistoryCount(memory), known.Length, databaseSha256);
    }
    public static SqliteSeedArtifact.Report Materialize(NCMetaSqliteSeedPackage.Request request, string environmentId, string databaseSha256)
    {
        var directory = SqliteSeedArtifact.EnvironmentDirectory(request.Workspace, environmentId);
        var target = SqliteSeedArtifact.Target(request.Workspace, environmentId);
        var data = Path.GetDirectoryName(target)!;
        // A second invocation must never replace even an otherwise empty environment database.
        if (Directory.Exists(data) || File.Exists(data)) throw new InvalidDataException("Owned server-data must be new.");
        using var assembly = PinAssembly(SqliteSeedArtifact.DatabaseAssembly(request.Workspace, environmentId), databaseSha256);
        var known = NCMetaSqliteSeedPackage.ReadSqliteMigrationIds(SqliteSeedArtifact.DatabaseAssembly(request.Workspace, environmentId));
        using var memory = OpenMemory(); NCMetaSqliteSeedPackage.LoadIntoNewMemory(memory, request, known);
        var expected = Fingerprint(memory); var seedMigrations = HistoryCount(memory);
        Directory.CreateDirectory(data); SqliteSeedArtifact.NoLinks(data);
        var temporary = Path.Combine(data, ".sqlite-seed-" + Guid.NewGuid().ToString("N") + ".db");
        var moved = false;
        try
        {
            using (new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            using (var disk = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = temporary, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 2 }.ToString()))
            {
                disk.Open(); disk.EnableExtensions(false); memory.BackupDatabase(disk);
                using var pragma = disk.CreateCommand(); pragma.CommandText = "PRAGMA journal_mode=DELETE;"; pragma.ExecuteNonQuery();
                if (Fingerprint(disk) != expected) throw new InvalidDataException("SQLite disk backup content mismatch.");
            }
            SqliteSeedArtifact.NoSidecars(temporary);
            SqliteSeedArtifact.NoLinks(target);
            if (File.Exists(target) || Directory.Exists(target)) throw new InvalidDataException("Owned database already exists.");
            var bytes = new FileInfo(temporary).Length;
            if (bytes is <= 0 or > SqliteSeedArtifact.MaximumDatabaseBytes) throw new InvalidDataException("SQLite disk backup size limit.");
            var hash = SqliteSeedArtifact.HashFile(temporary, SqliteSeedArtifact.MaximumDatabaseBytes);
            // Reload the pinned immutable source once more, so success also attests source bytes remained unchanged.
            using var recheck = OpenMemory(); NCMetaSqliteSeedPackage.LoadIntoNewMemory(recheck, request, known);
            if (Fingerprint(recheck) != expected || SqliteSeedArtifact.Hash(assembly) != databaseSha256) throw new InvalidDataException("SQLite source changed.");
            File.Move(temporary, target, overwrite: false); moved = true;
            return new(request.Id, environmentId, Path.GetRelativePath(request.Workspace, target).Replace('\\', '/'), bytes, hash, databaseSha256, seedMigrations, known.Length);
        }
        finally
        {
            // Only this invocation's checked temporary names; never remove a preexisting/final database.
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                var owned = temporary + suffix; SqliteSeedArtifact.NoLinks(owned); if (File.Exists(owned)) File.Delete(owned);
            }
            if (!moved && Directory.Exists(data) && !Directory.EnumerateFileSystemEntries(data).Any()) Directory.Delete(data);
        }
    }
    public static SqliteSeedArtifact.Staged StageHelper(string workspace, string seedId)
    {
        var seed = SqliteSeedArtifact.SeedDirectory(workspace, seedId);
        if (!File.Exists(Path.Combine(seed, "seed.db")) || !File.Exists(Path.Combine(seed, "manifest.json"))) throw new InvalidDataException("Select an existing seed package.");
        var bundles = Path.Combine(seed, "materializer"); SqliteSeedArtifact.NoLinks(bundles);
        var source = AppContext.BaseDirectory; SqliteSeedArtifact.NoLinks(source);
        var runtimeId = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        var native = Path.Combine(source, "runtimes", runtimeId, "native");
        if (!Directory.Exists(native)) throw new InvalidDataException("The helper must include this host's native SQLite runtime.");
        var files = Directory.EnumerateFiles(source).Where(file => Path.GetExtension(file) is ".dll" or ".json")
            .Concat(Directory.EnumerateFiles(native)).Order(StringComparer.Ordinal).ToArray();
        if (files.Length is < 4 or > 64) throw new InvalidDataException("SQLite helper file count limit.");
        var staging = Path.Combine(seed, ".materializer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging); SqliteSeedArtifact.NoLinks(staging);
        var entries = new List<SqliteSeedArtifact.Entry>(); long total = 0;
        try
        {
        foreach (var file in files)
        {
            var name = Path.GetRelativePath(source, file).Replace('\\', '/');
            if (!SqliteSeedArtifact.IsEntryName(name)) throw new InvalidDataException("Unexpected helper file name.");
            using var pin = SqliteSeedArtifact.Pin(file, 16 * 1024 * 1024);
            if ((total += pin.Length) > 32 * 1024 * 1024) throw new InvalidDataException("SQLite helper byte limit.");
            var destination = Path.Combine(staging, name); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { pin.CopyTo(output); output.Flush(true); }
            entries.Add(new(name, pin.Length, SqliteSeedArtifact.Hash(pin))); File.SetAttributes(destination, File.GetAttributes(destination) | FileAttributes.ReadOnly);
        }
        var manifest = Path.Combine(staging, "helper-manifest.json");
        using (var output = new FileStream(manifest, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(output, new SqliteSeedArtifact.Bundle(1, seedId, runtimeId, entries.ToArray()), SqliteSeedArtifact.Json); output.Flush(true); }
        File.SetAttributes(manifest, File.GetAttributes(manifest) | FileAttributes.ReadOnly);
        var hash = SqliteSeedArtifact.HashFile(manifest);
        Directory.CreateDirectory(bundles); SqliteSeedArtifact.NoLinks(bundles);
        var target = Path.Combine(bundles, hash); SqliteSeedArtifact.NoLinks(target);
        if (!Directory.Exists(target)) Directory.Move(staging, target); // Append-only: an existing pinned bundle is never overwritten.
        using var verified = SqliteSeedArtifact.PinBundle(workspace, seedId, hash, out _);
        return new(seedId, hash, entries.Count, total);
        }
        finally
        {
            // This invocation's fresh GUID directory only, after checking every descendant for links.
            if (Directory.Exists(staging))
            {
                foreach (var name in SqliteSeedArtifact.Inventory(staging))
                { var file = Path.Combine(staging, name); File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); }
                Directory.Delete(staging, recursive: true);
            }
        }
    }
    private static FileStream PinAssembly(string path, string expected)
    {
        if (!SqliteSeedArtifact.IsHash(expected)) throw new InvalidDataException("Expected current Server.Database SHA256.");
        var stream = SqliteSeedArtifact.Pin(path, 64 * 1024 * 1024);
        if (SqliteSeedArtifact.Hash(stream) == expected) return stream;
        stream.Dispose(); throw new InvalidDataException("Server.Database assembly changed.");
    }
    private static SqliteConnection OpenMemory()
    { var db = new SqliteConnection("Data Source=:memory:;Pooling=False"); db.Open(); db.EnableExtensions(false); return db; }
    private static int HistoryCount(SqliteConnection db)
    { using var command = db.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory;"; return checked((int)(long)command.ExecuteScalar()!); }
    private static string Fingerprint(SqliteConnection db)
    {
        // All rows are already validated as pristine. Compare the actual backup's schema + default/history values.
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var tables = new List<string>();
        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT type,name,tbl_name,sql FROM sqlite_schema ORDER BY type,name;";
            using var reader = command.ExecuteReader(); var count = 0;
            while (reader.Read())
            {
                if (++count > 768) throw new InvalidDataException("Backup schema size limit.");
                if (reader.GetString(0) == "table") tables.Add(reader.GetString(1));
                Append(reader);
            }
        }
        foreach (var table in tables)
        {
            if (!table.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) throw new InvalidDataException("Backup table name invalid.");
            using var command = db.CreateCommand(); command.CommandText = "SELECT * FROM \"" + table + "\" ORDER BY 1;";
            using var reader = command.ExecuteReader(); var count = 0;
            digest.AppendData(Encoding.UTF8.GetBytes(table));
            while (reader.Read()) { if (++count > 1024) throw new InvalidDataException("Backup row limit."); Append(reader); }
        }
        return Convert.ToHexStringLower(digest.GetHashAndReset());
        void Append(SqliteDataReader reader)
        {
            var values = new object[reader.FieldCount]; reader.GetValues(values);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(values.Select(value => value is DBNull ? null : value), SqliteSeedArtifact.Json);
            digest.AppendData(BitConverter.GetBytes(bytes.Length)); digest.AppendData(bytes);
        }
    }
}
