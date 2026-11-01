// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Security.Cryptography;
using Content.Server.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NightCity.Diagnostics.Seeds;
using NUnit.Framework;

namespace NightCity.Diagnostics.Seeds.Tests
{
    [TestFixture]
    public sealed class SqliteDiskMaterializerTests
    {
        private string _root = "";
        private string _environment = "";
        private string _databaseHash = "";
        private NCMetaSqliteSeedPackage.Request _seed = null!;
        private static readonly string[] Known = ["20261024000000_DiskSeed", "20261025000000_DiskNext"];
        [SetUp]
        public void Setup()
        {
            SQLitePCL.Batteries_V2.Init();
            _root = Path.Combine(Path.GetTempPath(), "nc-disk-materializer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "Content.Server.Database"));
            File.WriteAllText(Path.Combine(_root, "global.json"), "{}");
            File.WriteAllText(Path.Combine(_root, "Content.Server.Database/Content.Server.Database.csproj"), "<Project />");
            Directory.CreateDirectory(Path.Combine(_root, "artifacts"));
            var source = Path.Combine(_root, "artifacts/meta-sqlite-" + Guid.NewGuid().ToString("N") + ".db");
            using (var db = new SqliteConnection("Data Source=" + source + ";Pooling=False"))
            {
                db.Open(); using var sql = db.CreateCommand(); sql.CommandText = """
                    CREATE TABLE __EFMigrationsHistory(MigrationId TEXT PRIMARY KEY,ProductVersion TEXT NOT NULL);
                    INSERT INTO __EFMigrationsHistory VALUES('20261024000000_DiskSeed','10.0.6');
                    CREATE TABLE application_state(id INTEGER PRIMARY KEY,payload TEXT);
                    CREATE TABLE server(server_id INTEGER PRIMARY KEY,name TEXT NOT NULL);
                    INSERT INTO server VALUES(0,'unknown');
                    """; sql.ExecuteNonQuery();
            }
            var package = NCMetaSqliteSeedPackage.CreatePackage(_root, source, Hash(source), Known);
            _seed = new(_root, package.Id, package.SeedSha256, package.ManifestSha256);
            _environment = Guid.NewGuid().ToString("N");
            var assembly = Path.Combine(_root, "artifacts/ss14-mcp/environments", _environment, "server/bin/Content.Server/Content.Server.Database.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(assembly)!);
            File.Copy(typeof(SqliteDiskMaterializerTests).Assembly.Location, assembly); _databaseHash = Hash(assembly);
        }
        [TearDown]
        public void Cleanup()
        {
            var absolute = Path.GetFullPath(_root);
            Assert.That(Path.GetDirectoryName(absolute), Is.EqualTo(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)));
            Assert.That(Path.GetFileName(absolute), Does.StartWith("nc-disk-materializer-"));
            foreach (var file in Directory.EnumerateFiles(absolute, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(absolute, recursive: true);
        }
        [Test]
        public void RealDiskBackupRemainsPristineAndNormalEfMigrateAppliesPendingMigration()
        {
            var seedPath = Path.Combine(_root, "artifacts/meta-db-bootstrap", _seed.Id, "seed.db");
            var before = Hash(seedPath);
            var report = SqliteSeedCommands.Materialize(_seed, _environment, _databaseHash);
            var target = SqliteSeedArtifact.Target(_root, _environment);
            Assert.That(report.SeedMigrations, Is.EqualTo(1)); Assert.That(report.CurrentMigrations, Is.EqualTo(2));
            Assert.That(report.Sha256, Is.EqualTo(Hash(target))); Assert.That(Hash(seedPath), Is.EqualTo(before));
            Assert.That(File.Exists(seedPath + "-wal") || File.Exists(seedPath + "-shm"), Is.False);
            var options = new DbContextOptionsBuilder<SqliteServerDbContext>().UseSqlite("Data Source=" + target + ";Pooling=False").Options;
            using (var context = new SqliteServerDbContext(options))
            {
                context.Database.Migrate();
                Assert.That(context.Database.GetAppliedMigrations(), Is.EqualTo(Known));
                context.Database.ExecuteSqlRaw("INSERT INTO application_state(id,payload,proof) VALUES(1,'new actor data','pending migration applied');");
            }
            using var disk = new SqliteConnection("Data Source=" + target + ";Pooling=False"); disk.Open();
            using var check = disk.CreateCommand(); check.CommandText = "SELECT proof FROM application_state WHERE id=1;";
            Assert.That(check.ExecuteScalar(), Is.EqualTo("pending migration applied"));
            Assert.That(Hash(seedPath), Is.EqualTo(before));
        }
        [Test]
        public void RepeatCannotOverwriteExistingDatabaseAndWrongAssemblyHashCreatesNoData()
        {
            var target = SqliteSeedArtifact.Target(_root, _environment);
            Assert.Throws<InvalidDataException>(() => SqliteSeedCommands.Materialize(_seed, _environment, new('f', 64)));
            Assert.That(Directory.Exists(Path.GetDirectoryName(target)), Is.False);
            SqliteSeedCommands.Materialize(_seed, _environment, _databaseHash); var original = Hash(target);
            Assert.Throws<InvalidDataException>(() => SqliteSeedCommands.Materialize(_seed, _environment, _databaseHash));
            Assert.That(Hash(target), Is.EqualTo(original));
            Assert.Throws<InvalidDataException>(() => SqliteSeedCommands.Materialize(_seed, "../outside", _databaseHash));
        }
        [Test]
        public void TamperedSeedOrManifestCannotCreateOwnedDiskOutput()
        {
            Assert.Throws<InvalidDataException>(() => SqliteSeedCommands.Materialize(_seed with { SeedSha256 = new('e', 64) }, _environment, _databaseHash));
            Assert.Throws<InvalidDataException>(() => SqliteSeedCommands.Materialize(_seed with { ManifestSha256 = new('d', 64) }, _environment, _databaseHash));
            Assert.That(Directory.Exists(Path.GetDirectoryName(SqliteSeedArtifact.Target(_root, _environment))), Is.False);
        }
        private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    }
}
namespace Content.Server.Database
{
    // Isolated provider fixture; the real tool reads the actual selected server database PE metadata.
    public sealed class SqliteServerDbContext(DbContextOptions<SqliteServerDbContext> options) : DbContext(options);
    [DbContext(typeof(SqliteServerDbContext)), Migration("20261024000000_DiskSeed")]
    public sealed class DiskSeedMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("CREATE TABLE application_state(id INTEGER PRIMARY KEY,payload TEXT);");
        protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException();
    }
    [DbContext(typeof(SqliteServerDbContext)), Migration("20261025000000_DiskNext")]
    public sealed class DiskNextMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("ALTER TABLE application_state ADD COLUMN proof TEXT;");
        protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException();
    }
}
