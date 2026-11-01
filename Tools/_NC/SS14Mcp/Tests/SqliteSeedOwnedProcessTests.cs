// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Security.Cryptography;
using NightCity.Diagnostics.Contracts;
using NightCity.Diagnostics.Core;
using NightCity.Diagnostics.Seeds;
using NUnit.Framework;

namespace NightCity.Diagnostics.Tests;

/// <summary>Explicit tool-only proof: an owned helper process creates a disk DB; no game process is started.</summary>
[TestFixture, NonParallelizable]
public sealed class SqliteSeedOwnedProcessTests
{
    [Test, Category("SeedProof")]
    public async Task PinnedHelperMaterializesActualPristinePackageWithoutStartingGameAndCannotOverwrite()
    {
        if (Environment.GetEnvironmentVariable("SS14_MCP_SEED_PROOF") != "1")
            Assert.Ignore("Set explicit seed pins, workspace and database assembly to run this tool-only disk proof.");
        var paths = new WorkspacePaths(Environment.GetEnvironmentVariable("SS14_MCP_WORKSPACE") ?? throw new InvalidOperationException("Workspace required."));
        var selection = SqliteSeedPrefill.ReadEnvironment() ?? throw new InvalidOperationException("All four pins are required.");
        var original = paths.Resolve(Environment.GetEnvironmentVariable("SS14_MCP_SEED_PROOF_DATABASE") ?? throw new InvalidOperationException("Database assembly required."));
        Assert.That(Path.GetFileName(original), Is.EqualTo("Content.Server.Database.dll"));
        var id = Guid.NewGuid().ToString("N");
        var environment = paths.Resolve("artifacts/ss14-mcp/environments/" + id);
        var seed = Path.Combine(SqliteSeedArtifact.SeedDirectory(paths.Root, selection.SeedId), "seed.db");
        var manifest = Path.Combine(Path.GetDirectoryName(seed)!, "manifest.json");
        var sourceHash = Hash(seed); var manifestHash = Hash(manifest);
        Directory.CreateDirectory(environment);
        try
        {
            var assembly = SqliteSeedArtifact.DatabaseAssembly(paths.Root, id);
            Directory.CreateDirectory(Path.GetDirectoryName(assembly)!); File.Copy(original, assembly);
            var rejected = selection with { HelperSha256 = new('f', 64) };
            await Assert.ThatAsync(async () => await rejected.MaterializeAsync(paths, id, CancellationToken.None), Throws.TypeOf<DiagnosticException>());
            Assert.That(Directory.Exists(Path.Combine(environment, "server-data")), Is.False);
            var report = await selection.MaterializeAsync(paths, id, CancellationToken.None);
            var target = SqliteSeedArtifact.Target(paths.Root, id);
            Assert.That(report.DatabaseAssemblySha256, Is.EqualTo(Hash(original)));
            Assert.That(report.SeedMigrations, Is.GreaterThan(0));
            Assert.That(report.CurrentMigrations, Is.GreaterThanOrEqualTo(report.SeedMigrations));
            Assert.That(report.Sha256, Is.EqualTo(Hash(target)));
            Assert.That(File.GetAttributes(target) & FileAttributes.ReadOnly, Is.EqualTo((FileAttributes)0), "Only the new database is writable for normal Migrate.");
            await Assert.ThatAsync(async () => await selection.MaterializeAsync(paths, id, CancellationToken.None), Throws.TypeOf<DiagnosticException>());
            Assert.That(Hash(target), Is.EqualTo(report.Sha256));
            Assert.That(Hash(seed), Is.EqualTo(sourceHash)); Assert.That(Hash(manifest), Is.EqualTo(manifestHash));
            Assert.That(File.Exists(seed + "-wal") || File.Exists(seed + "-shm"), Is.False);
            TestContext.Progress.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, SqliteSeedArtifact.Json));
        }
        finally
        {
            Assert.That(Path.GetFullPath(environment), Is.EqualTo(Path.Combine(paths.ArtifactRoot, "environments", id)));
            SqliteSeedArtifact.NoLinks(environment);
            foreach (var file in Directory.EnumerateFiles(environment, "*", SearchOption.AllDirectories)) SqliteSeedArtifact.NoLinks(file);
            Directory.Delete(environment, recursive: true); // Only this test's freshly allocated GUID, never an active environment.
        }
    }
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
