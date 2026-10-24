// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Diagnostics;
using NightCity.Diagnostics.Contracts;
using NightCity.Diagnostics.Seeds;

namespace NightCity.Diagnostics.Core;

/// <summary>Optional offline prefill of a fresh owned disk database, before the server's ordinary Migrate().</summary>
public sealed record SqliteSeedPrefill(string SeedId, string SeedSha256, string ManifestSha256, string HelperSha256)
{
    public const string IdVariable = "SS14_MCP_SQLITE_SEED_ID";
    public const string SeedVariable = "SS14_MCP_SQLITE_SEED_SHA256";
    public const string ManifestVariable = "SS14_MCP_SQLITE_MANIFEST_SHA256";
    public const string HelperVariable = "SS14_MCP_SQLITE_HELPER_SHA256";
    public static SqliteSeedPrefill? ReadEnvironment() => FromVariables(Environment.GetEnvironmentVariable);
    public static SqliteSeedPrefill? FromVariables(Func<string, string?> read)
    {
        var values = new[] { IdVariable, SeedVariable, ManifestVariable, HelperVariable }.Select(read).ToArray();
        if (values.All(string.IsNullOrEmpty)) return null;
        try
        {
            if (values.Any(string.IsNullOrEmpty) || values.Skip(1).Any(value => !SqliteSeedArtifact.IsHash(value)))
                throw new InvalidDataException("All four SQLite prefill pins must be explicit.");
            SqliteSeedArtifact.RequireId(values[0]!);
            return new(values[0]!, values[1]!, values[2]!, values[3]!);
        }
        catch (InvalidDataException) { throw new DiagnosticException("invalid_sqlite_seed", "SQLite prefill requires a canonical seed ID and all three SHA256 pins."); }
    }

    public async Task<SqliteSeedArtifact.Report> MaterializeAsync(WorkspacePaths paths, string environmentId, CancellationToken cancellationToken)
    {
        var target = SqliteSeedArtifact.Target(paths.Root, environmentId);
        if (Directory.Exists(Path.GetDirectoryName(target)) || File.Exists(Path.GetDirectoryName(target)))
            throw new DiagnosticException("invalid_sqlite_seed", "SQLite prefill requires new owned server data.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        Process? process = null;
        var started = false;
        var succeeded = false;
        try
        {
            using var bundle = SqliteSeedArtifact.PinBundle(paths.Root, SeedId, HelperSha256, out var helper);
            using var database = SqliteSeedArtifact.Pin(SqliteSeedArtifact.DatabaseAssembly(paths.Root, environmentId), 64 * 1024 * 1024);
            var databaseHash = SqliteSeedArtifact.Hash(database);
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Path.GetDirectoryName(helper)!, UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var argument in new[] { helper, "materialize", paths.Root, SeedId, SeedSha256, ManifestSha256, environmentId, databaseHash })
                start.ArgumentList.Add(argument);
            ClearBootstrapEnvironment(start);
            // Prevent inherited runtime injection from changing the pinned managed/native helper bundle.
            foreach (var name in new[] { "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE", "DOTNET_DiagnosticPorts",
                         "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER", "CORECLR_PROFILER_PATH", "LD_PRELOAD", "DYLD_INSERT_LIBRARIES" })
                start.Environment.Remove(name);
            process = new Process { StartInfo = start };
            timeout.Token.ThrowIfCancellationRequested();
            if (!process.Start()) throw new IOException("Could not start the owned SQLite helper.");
            started = true;
            var budget = new OutputBudget();
            var output = ReadBoundedAsync(process.StandardOutput.BaseStream, timeout, budget);
            var error = ReadBoundedAsync(process.StandardError.BaseStream, timeout, budget);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, error);
            if (process.ExitCode != 0 || (await error).Length != 0)
                throw new InvalidDataException("SQLite helper rejected the prefill.");
            using var reportStream = new MemoryStream(await output);
            var report = SqliteSeedArtifact.ReadJson<SqliteSeedArtifact.Report>(reportStream);
            var expectedTarget = Path.GetRelativePath(paths.Root, target).Replace('\\', '/');
            if (report.SeedId != SeedId || report.EnvironmentId != environmentId || report.Target != expectedTarget ||
                report.DatabaseAssemblySha256 != databaseHash || report.SeedMigrations < 1 || report.CurrentMigrations < report.SeedMigrations ||
                report.CurrentMigrations > 1024 || report.Bytes is <= 0 or > SqliteSeedArtifact.MaximumDatabaseBytes || !SqliteSeedArtifact.IsHash(report.Sha256))
                throw new InvalidDataException("SQLite helper report does not match the owned prefill.");
            SqliteSeedArtifact.NoSidecars(target);
            using var file = SqliteSeedArtifact.Pin(target, SqliteSeedArtifact.MaximumDatabaseBytes);
            if (file.Length != report.Bytes || SqliteSeedArtifact.Hash(file) != report.Sha256 || SqliteSeedArtifact.Hash(database) != databaseHash)
                throw new InvalidDataException("SQLite prefill bytes changed.");
            succeeded = true;
            return report;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new DiagnosticException("sqlite_seed_timeout", "The owned SQLite helper exceeded its 20-second deadline."); }
        catch (Exception failure) when (failure is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or
            System.ComponentModel.Win32Exception or System.Text.Json.JsonException or ArgumentException)
        { throw new DiagnosticException("invalid_sqlite_seed", "SQLite prefill was rejected; no game process was started."); }
        finally
        {
            var processStopped = !started;
            if (process != null)
            {
                try
                {
                    if (started && !process.HasExited) process.Kill(entireProcessTree: true);
                    if (started) await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
                    processStopped = true;
                }
                catch (Exception failure) when (failure is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
                process.Dispose();
            }
            if (!succeeded && processStopped)
                CleanupFailedData(paths.Root, environmentId);
        }
    }
    private sealed class OutputBudget { public int Bytes; }
    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationTokenSource deadline, OutputBudget budget)
    {
        using var output = new MemoryStream(); var buffer = new byte[1024];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, deadline.Token);
            if (count == 0) return output.ToArray();
            if (Interlocked.Add(ref budget.Bytes, count) > SqliteSeedArtifact.MaximumReportBytes)
            { deadline.Cancel(); throw new InvalidDataException("SQLite helper output limit."); }
            output.Write(buffer, 0, count);
        }
    }
    private static void CleanupFailedData(string root, string environmentId)
    {
        // server-data was absent at entry. Only this helper may have created these exact files;
        // never recurse or remove unexpected entries, links, or a different environment's files.
        try
        {
            var directory = Path.GetDirectoryName(SqliteSeedArtifact.Target(root, environmentId))!;
            if (!Directory.Exists(directory)) return;
            SqliteSeedArtifact.NoLinks(directory);
            var entries = Directory.EnumerateFileSystemEntries(directory).Take(17).ToArray();
            if (entries.Length > 16) return;
            foreach (var file in entries)
            {
                SqliteSeedArtifact.NoLinks(file);
                var name = Path.GetFileName(file);
                var baseName = name;
                foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                    if (baseName.EndsWith(suffix, StringComparison.Ordinal)) { baseName = baseName[..^suffix.Length]; break; }
                if (Directory.Exists(file) || baseName != "preferences.db" &&
                    !(baseName.StartsWith(".sqlite-seed-", StringComparison.Ordinal) && baseName.EndsWith(".db", StringComparison.Ordinal) &&
                      Guid.TryParseExact(baseName[13..^3], "N", out _))) return;
            }
            foreach (var file in entries) File.Delete(file);
            Directory.Delete(directory, recursive: false);
        }
        catch (Exception failure) when (failure is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }
    public static void ClearBootstrapEnvironment(ProcessStartInfo start)
    {
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("SS14_MCP_", StringComparison.Ordinal) ||
                     key is "NC_DIAGNOSTICS_WORKSPACE" or "NC_DIAGNOSTICS_SQLITE_SEED_ID" or "NC_DIAGNOSTICS_SQLITE_SEED_SHA256" or
                         "NC_DIAGNOSTICS_SQLITE_MANIFEST_SHA256" || key.StartsWith("ROBUST_AUTH_", StringComparison.Ordinal) ||
                     key is "OPENAI_API_KEY" or "ANTHROPIC_API_KEY").ToArray()) start.Environment.Remove(key);
    }
}
