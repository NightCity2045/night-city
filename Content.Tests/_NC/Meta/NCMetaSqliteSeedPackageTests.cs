// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
#if TOOLS && NC_DIAGNOSTICS
#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Content.Server.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NUnit.Framework;

namespace Content.Tests._NC.Meta;

[TestFixture, NonParallelizable]
public sealed class NCMetaSqliteSeedPackageTests
{
    private string _workspace = "";
    private string _source = "";
    private static readonly string[] Migrations = ["20261024000000_Seed", "20261025000000_Next"];
    [SetUp]
    public void Setup()
    {
        SQLitePCL.Batteries_V2.Init();
        _workspace = Path.Combine(Path.GetTempPath(), "nc-sqlite-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_workspace, "Content.Server.Database"));
        Directory.CreateDirectory(Path.Combine(_workspace, "artifacts"));
        File.WriteAllText(Path.Combine(_workspace, "global.json"), "{}");
        File.WriteAllText(Path.Combine(_workspace, "Content.Server.Database", "Content.Server.Database.csproj"), "<Project />");
        _source = Path.Combine(_workspace, "artifacts", "meta-sqlite-" + Guid.NewGuid().ToString("N") + ".db");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _source, Pooling = false }.ToString());
        db.Open();
        Execute(db, """
            CREATE TABLE "__EFMigrationsHistory"(MigrationId TEXT PRIMARY KEY, ProductVersion TEXT NOT NULL);
            INSERT INTO "__EFMigrationsHistory" VALUES('20261024000000_Seed','10.0.6');
            CREATE TABLE server(server_id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            INSERT INTO server VALUES(0,'unknown');
            CREATE TABLE application_state(id INTEGER PRIMARY KEY AUTOINCREMENT, payload TEXT);
            CREATE TABLE nc_meta_blob_storage(nc_meta_blob_storage_id INTEGER PRIMARY KEY,allocated_count INTEGER,allocated_bytes INTEGER,
              backfill_cursor TEXT,backfill_complete INTEGER,count_tokens REAL,byte_tokens REAL,refilled_at_utc_ticks INTEGER);
            INSERT INTO nc_meta_blob_storage VALUES(1,0,0,'',0,0.0,0.0,0);
            """);
    }
    [TearDown]
    public void Cleanup()
    {
        if (!Path.GetFullPath(_workspace).StartsWith(Path.Combine(Path.GetTempPath(), "nc-sqlite-seed-"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing cleanup outside this fixture's owned temporary directory.");
        SqliteConnection.ClearAllPools();
        foreach (var path in Directory.EnumerateFiles(_workspace, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(_workspace, recursive: true); // Only the exact GUID directory created by this fixture.
    }
    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Execute(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static long Count(SqliteConnection db) { using var command = db.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM application_state;"; return (long) command.ExecuteScalar()!; }
    private NCMetaSqliteSeedPackage.Request Package()
    {
        var package = NCMetaSqliteSeedPackage.CreatePackage(_workspace, _source, Sha(_source), Migrations);
        return new(_workspace, package.Id, package.SeedSha256, package.ManifestSha256);
    }
    private static SqliteConnection Memory() { var db = new SqliteConnection("Data Source=:memory:"); db.Open(); return db; }
    [Test]
    public void ActualBackupPreservesSourceAndSeparatesMemoryDatabases()
    {
        var before = Sha(_source); var package = Package();
        using var first = Memory(); using var second = Memory();
        NCMetaSqliteSeedPackage.LoadIntoNewMemory(first, package, Migrations);
        NCMetaSqliteSeedPackage.LoadIntoNewMemory(second, package, Migrations);
        Execute(first, "INSERT INTO application_state(payload) VALUES('private');");
        Assert.That(Count(first), Is.EqualTo(1)); Assert.That(Count(second), Is.Zero);
        Assert.That(Sha(_source), Is.EqualTo(before));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(_source)!, "*.db-wal"), Is.Empty);
    }
    [TestCase("application_state", "INSERT INTO application_state(payload) VALUES('persisted actor or money');")]
    [TestCase("storage", "UPDATE nc_meta_blob_storage SET allocated_count=1;")]
    [TestCase("server", "INSERT INTO server VALUES(1,'not pristine');")]
    [TestCase("hidden-default-data", "ALTER TABLE server ADD COLUMN hidden_state TEXT DEFAULT 'private';")]
    [TestCase("sequence", "INSERT INTO application_state(payload) VALUES('old'); DELETE FROM application_state;")]
    public void RejectsUnexpectedPersistentRowsAndDefaultChanges(string reason, string sql)
    {
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _source, Pooling = false }.ToString())) { db.Open(); Execute(db, sql); }
        var hash = Sha(_source);
        Assert.That(() => Package(), Throws.TypeOf<InvalidDataException>(), reason);
        Assert.That(Sha(_source), Is.EqualTo(hash));
    }
    [Test]
    public void RejectsSeedHashManifestHashAndFutureHistoryWithoutChangingSource()
    {
        var package = Package(); var hash = Sha(_source);
        using var first = Memory();
        Assert.That(() => NCMetaSqliteSeedPackage.LoadIntoNewMemory(first, package with { SeedSha256 = new('a', 64) }, Migrations), Throws.TypeOf<InvalidDataException>());
        using var second = Memory();
        Assert.That(() => NCMetaSqliteSeedPackage.LoadIntoNewMemory(second, package with { ManifestSha256 = new('b', 64) }, Migrations), Throws.TypeOf<InvalidDataException>());
        using var third = Memory();
        Assert.That(() => NCMetaSqliteSeedPackage.LoadIntoNewMemory(third, package, ["20261024000000_Different"]), Throws.TypeOf<InvalidDataException>());
        Assert.That(Sha(_source), Is.EqualTo(hash));
    }
    [Test]
    public void RejectsNonfreshMemoryAndAttachedDatabase()
    {
        var package = Package();
        using var first = Memory(); Execute(first, "CREATE TABLE unrelated(x INTEGER);");
        Assert.That(() => NCMetaSqliteSeedPackage.LoadIntoNewMemory(first, package, Migrations), Throws.TypeOf<InvalidDataException>());
        using var second = Memory(); Execute(second, "ATTACH DATABASE ':memory:' AS other;");
        Assert.That(() => NCMetaSqliteSeedPackage.LoadIntoNewMemory(second, package, Migrations), Throws.TypeOf<InvalidDataException>());
    }
    [Test]
    public void RejectsWritablePackageAndSidecar()
    {
        var package = Package();
        var seed = Path.Combine(_workspace, "artifacts", "meta-db-bootstrap", package.Id, "seed.db");
        File.SetAttributes(seed, FileAttributes.Normal);
        using var first = Memory();
        Assert.That(() => NCMetaSqliteSeedPackage.LoadIntoNewMemory(first, package, Migrations), Throws.TypeOf<InvalidDataException>());
        File.SetAttributes(seed, FileAttributes.ReadOnly); File.WriteAllText(seed + "-wal", "unattested");
        using var second = Memory();
        Assert.That(() => NCMetaSqliteSeedPackage.LoadIntoNewMemory(second, package, Migrations), Throws.TypeOf<InvalidDataException>());
    }
    [Test]
    public void PackagesCheckpointedWalHeaderWithoutCreatingSourceSidecars()
    {
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _source, Pooling = false }.ToString()))
        { db.Open(); Execute(db, "PRAGMA journal_mode=WAL; PRAGMA wal_checkpoint(TRUNCATE);"); }
        Assert.That(File.ReadAllBytes(_source)[18], Is.EqualTo(2));
        var before = Sha(_source); var package = Package();
        using var memory = Memory();
        NCMetaSqliteSeedPackage.LoadIntoNewMemory(memory, package, Migrations);
        Assert.That(Count(memory), Is.Zero);
        Assert.That(Sha(_source), Is.EqualTo(before));
        Assert.That(File.Exists(_source + "-wal"), Is.False);
        Assert.That(File.Exists(_source + "-shm"), Is.False);
    }
    [TestCase(NCMetaSqliteSeedPackage.WorkspaceVariable)]
    [TestCase(NCMetaSqliteSeedPackage.IdVariable)]
    [TestCase(NCMetaSqliteSeedPackage.SeedHashVariable)]
    [TestCase(NCMetaSqliteSeedPackage.ManifestHashVariable)]
    public void RejectsAnyPartialEnvironmentSelection(string selected)
    {
        var names = new[] { NCMetaSqliteSeedPackage.WorkspaceVariable, NCMetaSqliteSeedPackage.IdVariable,
            NCMetaSqliteSeedPackage.SeedHashVariable, NCMetaSqliteSeedPackage.ManifestHashVariable, "NC_DIAGNOSTICS_ISOLATED" };
        var previous = names.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, null);
            Assert.That(NCMetaSqliteSeedPackage.ReadEnvironment(), Is.Null, "No opt-in preserves ordinary startup.");
            Environment.SetEnvironmentVariable("NC_DIAGNOSTICS_ISOLATED", "1");
            Environment.SetEnvironmentVariable(selected, selected == NCMetaSqliteSeedPackage.WorkspaceVariable ? _workspace : new string('a', selected == NCMetaSqliteSeedPackage.IdVariable ? 32 : 64));
            Assert.That(() => NCMetaSqliteSeedPackage.ReadEnvironment(), Throws.TypeOf<InvalidDataException>());
        }
        finally { for (var index = 0; index < names.Length; index++) Environment.SetEnvironmentVariable(names[index], previous[index]); }
    }
    [Test]
    public void OrdinaryEfMigrateAppliesPendingRealMigrationAfterBackup()
    {
        var package = Package(); var before = Sha(_source);
        using var memory = Memory();
        NCMetaSqliteSeedPackage.LoadIntoNewMemory(memory, package, Migrations);
        using var context = new NCMetaSeedProofDbContext(new DbContextOptionsBuilder<NCMetaSeedProofDbContext>().UseSqlite(memory).Options);
        Assert.That(context.Database.GetPendingMigrations().ToArray(), Is.EqualTo(new[] { Migrations[1] }));
        context.Database.Migrate(); // The real public EF migrator; no EnsureCreated/history mutation.
        Assert.That(context.Database.GetPendingMigrations(), Is.Empty);
        Execute(memory, "INSERT INTO application_state(payload,proof) VALUES('real migration','applied');");
        Assert.That(Count(memory), Is.EqualTo(1));
        Assert.That(Sha(_source), Is.EqualTo(before));
    }
}

public sealed class NCMetaSeedProofDbContext(DbContextOptions<NCMetaSeedProofDbContext> options) : DbContext(options);
[DbContext(typeof(NCMetaSeedProofDbContext)), Migration("20261024000000_Seed")]
public sealed class NCMetaSeedProofInitial : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("SELECT 1;");
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("SELECT 1;");
}
[DbContext(typeof(NCMetaSeedProofDbContext)), Migration("20261025000000_Next")]
public sealed class NCMetaSeedProofNext : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("ALTER TABLE application_state ADD COLUMN proof TEXT NULL;");
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("ALTER TABLE application_state DROP COLUMN proof;");
}
#endif
