// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._NC.Persistence.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Robust.Shared.Configuration;
using Robust.Shared.Log;
using Robust.Shared.Serialization.Manager;

namespace Content.Tests.Server._NC.Meta;

[TestFixture]
public sealed class NCMetaProjectRetentionTest
{
    [Test]
    public async Task DeletionAttestsTheProjectAndBlocksBothSingleAndMixedBatchResurrection()
    {
        var db = GetDb(out var connection, out var options);
        await using var databaseConnection = connection;
        var epoch = NCSessionEpoch.New();
        var clock = await db.StartNCActiveWorldSessionAsync(epoch);
        var id = Guid.NewGuid();
        var digest = await db.StoreNCMetaBlobAsync(Encoding.UTF8.GetBytes("private source"));
        await CreateProjectAsync(db, epoch, clock.Time, id, [digest]);
        var withoutProject = new NCPersistentBatchMutationRequest(Guid.NewGuid(), "meta.project.manage", "meta.owner", "owner-1",
            [MarkerWrite(id)], "{}", Guid.NewGuid(), null, epoch, clock.Time, []);
        Assert.That((await db.ApplyNCPersistentBatchMutationAsync(withoutProject)).Status, Is.EqualTo(NCPersistentMutationStatus.VersionConflict));
        Assert.That(await db.GetNCAggregateSnapshotAsync(ServerDbBase.NCMetaProjectTombstoneType, id.ToString("D")), Is.Null);

        var deletion = DeleteRequest(epoch, clock.Time, id, [digest]);
        Assert.That((await db.ApplyNCPersistentBatchMutationAsync(deletion)).Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
        Assert.That((await db.ApplyNCPersistentBatchMutationAsync(deletion)).Status, Is.EqualTo(NCPersistentMutationStatus.DuplicateCompleted));
        // Even a caller that has learned the new version cannot revive the deleted authoring project.
        var edit = ProjectRequest(epoch, clock.Time, id, 2, ProjectPayload(id, 3, [digest]));
        Assert.That((await db.ApplyNCPersistentMutationAsync(edit)).Status, Is.EqualTo(NCPersistentMutationStatus.VersionConflict));
        var mixed = new NCPersistentBatchMutationRequest(Guid.NewGuid(), "meta.project.manage", "meta.owner", "owner-1",
            [new(Guid.NewGuid(), "meta.project.changed", "meta.project", id.ToString("D"), 2, 1, edit.SnapshotPayload, "{}"),
                new(Guid.NewGuid(), "meta.volume.changed", "meta.volume", "other-volume", 0, 1, "{\"Files\":[]}", "{}")],
            "{}", Guid.NewGuid(), null, epoch, clock.Time, []);
        Assert.That((await db.ApplyNCPersistentBatchMutationAsync(mixed)).Status, Is.EqualTo(NCPersistentMutationStatus.VersionConflict));
        Assert.That(await db.GetNCAggregateSnapshotAsync("meta.volume", "other-volume"), Is.Null);
        Assert.That((await db.GetNCAggregateSnapshotAsync("meta.project", id.ToString("D")))?.AggregateVersion, Is.EqualTo(2));
        Assert.That((await db.GetNCAggregateSnapshotAsync(ServerDbBase.NCMetaProjectTombstoneType, id.ToString("D")))?.AggregateVersion, Is.EqualTo(1));
        Assert.That(await db.RetireNCMetaProjectsAsync(epoch, clock.Time), Is.Zero);
        await using var context = new SqliteServerDbContext(options);
        Assert.That(await context.NCCommandReceipts.CountAsync(value => value.CommandType == "meta.project.retire"), Is.Zero);
    }

    [Test]
    public async Task ExpiredDeletedAuthoringRootsAreCollectedWhileInstalledPublishedAndCheckpointImagesRemain()
    {
        var db = GetDb(out var connection, out var options);
        await using var databaseConnection = connection;
        var epoch = NCSessionEpoch.New();
        var clock = await db.StartNCActiveWorldSessionAsync(epoch);
        var id = Guid.NewGuid();
        var source = await db.StoreNCMetaBlobAsync(Encoding.UTF8.GetBytes("deleted private source"));
        var installed = await db.StoreNCMetaBlobAsync(Encoding.UTF8.GetBytes("lawfully installed image"));
        var published = await db.StoreNCMetaBlobAsync(Encoding.UTF8.GetBytes("immutable published image"));
        var checkpointOnly = await db.StoreNCMetaBlobAsync(Encoding.UTF8.GetBytes("older retained checkpoint source"));
        var roots = new[] { source, installed, published, checkpointOnly };
        await CreateProjectAsync(db, epoch, clock.Time, id, roots);
        await AddRootAsync(db, epoch, clock.Time, "meta.installation", "installed", installed);
        await AddRootAsync(db, epoch, clock.Time, "meta.software.publication", "published", published);
        await AddRootAsync(db, epoch, clock.Time, "meta.volume", "checkpoint-only", checkpointOnly);
        var anchor = await db.GetNCAggregateSnapshotAsync("meta.volume", "checkpoint-only");
        await CommitCheckpointAsync(db, epoch, clock.Time, JsonSerializer.Serialize(new[] { anchor }));
        await db.ApplyNCPersistentMutationAsync(ProjectRequest(epoch, clock.Time, id, 1, ProjectPayload(id, 2, roots)) with
        {
            AggregateType = "meta.volume", AggregateId = "checkpoint-only", SnapshotPayload = "{\"Files\":[]}",
        });
        // The project remains at revision one; the separate volume update releases its current root.
        Assert.That((await db.ApplyNCPersistentBatchMutationAsync(DeleteRequest(epoch, clock.Time, id, roots))).Status,
            Is.EqualTo(NCPersistentMutationStatus.Applied));
        await using var context = new SqliteServerDbContext(options);
        var old = DateTime.UtcNow.AddDays(-45);
        foreach (var blob in await context.NCMetaBlobs.ToListAsync()) { blob.CreatedAt = old; blob.AdmittedUntilUtcTicks = 0; }
        foreach (var revision in await context.NCMetaAggregateRevisions.ToListAsync()) revision.CreatedAt = old;
        var project = await context.NCAggregateSnapshots.SingleAsync(value => value.AggregateType == "meta.project" && value.AggregateId == id.ToString("D"));
        var marker = await context.NCAggregateSnapshots.SingleAsync(value => value.AggregateType == ServerDbBase.NCMetaProjectTombstoneType && value.AggregateId == id.ToString("D"));
        project.CreatedAt = old; marker.CreatedAt = old;
        await context.SaveChangesAsync();
        Assert.That(await db.RetireNCMetaProjectsAsync(epoch, clock.Time), Is.EqualTo(1));
        Assert.That(await db.RetireNCMetaProjectsAsync(epoch, clock.Time), Is.Zero);
        var retiredProject = await db.GetNCAggregateSnapshotAsync("meta.project", id.ToString("D"));
        var retiredMarker = await db.GetNCAggregateSnapshotAsync(ServerDbBase.NCMetaProjectTombstoneType, id.ToString("D"));
        using var retired = JsonDocument.Parse(retiredProject.Payload);
        Assert.Multiple(() =>
        {
            Assert.That(retiredProject.AggregateVersion, Is.EqualTo(3));
            Assert.That(retired.RootElement.GetProperty("Files").GetArrayLength(), Is.Zero);
            Assert.That(retired.RootElement.GetProperty("Releases").GetArrayLength(), Is.Zero);
            Assert.That(retiredMarker.AggregateVersion, Is.EqualTo(2));
            Assert.That(JsonSerializer.Deserialize<NCMetaProjectTombstoneData>(retiredMarker.Payload).Retired, Is.True);
        });
        var retirementEvent = (await db.GetNCPersistentEventPageAsync("meta.project", id.ToString("D"), null, null, 1)).Single();
        Assert.Multiple(() =>
        {
            Assert.That(retirementEvent.EventType, Is.EqualTo("meta.project.retired"));
            Assert.That(retirementEvent.SessionEpoch, Is.EqualTo(epoch));
            Assert.That(retirementEvent.ActiveWorldTime, Is.EqualTo(clock.Time));
        });
        for (var pass = 0; pass < 16; pass++)
        {
            var result = await db.MaintainNCMetaBlobsAsync();
            if (result.BackfillComplete && result.RevisionsRemoved < 32 && result.BlobsRemoved < 32) break;
        }
        Assert.That(await db.ReadNCMetaBlobAsync(source), Is.Null);
        Assert.That(await db.ReadNCMetaBlobAsync(installed), Is.Not.Null);
        Assert.That(await db.ReadNCMetaBlobAsync(published), Is.Not.Null);
        Assert.That(await db.ReadNCMetaBlobAsync(checkpointOnly), Is.Not.Null);
        Assert.That((await db.ApplyNCPersistentMutationAsync(ProjectRequest(epoch, clock.Time, id, 3,
            ProjectPayload(id, 4, [installed])))).Status, Is.EqualTo(NCPersistentMutationStatus.VersionConflict));
        Assert.That(await context.NCCommandReceipts.AsNoTracking().CountAsync(value => value.CommandType == "meta.project.retire" &&
            value.State == NCCommandReceiptState.Completed), Is.EqualTo(1));
    }

    [Test]
    public async Task RetirementUsesTheCapturedEpochAndProcessesOnlyOneBoundedPage()
    {
        var db = GetDb(out var connection, out var options);
        await using var databaseConnection = connection;
        var epoch = NCSessionEpoch.New();
        var clock = await db.StartNCActiveWorldSessionAsync(epoch);
        var digest = await db.StoreNCMetaBlobAsync(Encoding.UTF8.GetBytes("shared source"));
        for (var i = 0; i < ServerDbBase.NCMetaMaximumProjectRetirements + 1; i++)
        {
            var id = Guid.NewGuid();
            await CreateProjectAsync(db, epoch, clock.Time, id, [digest]);
            await db.ApplyNCPersistentBatchMutationAsync(DeleteRequest(epoch, clock.Time, id, [digest]));
        }
        await using var context = new SqliteServerDbContext(options);
        foreach (var marker in await context.NCAggregateSnapshots.Where(value => value.AggregateType == ServerDbBase.NCMetaProjectTombstoneType).ToListAsync())
            marker.CreatedAt = DateTime.UtcNow.AddDays(-45);
        await context.SaveChangesAsync();
        Assert.That(await db.RetireNCMetaProjectsAsync(NCSessionEpoch.New(), clock.Time), Is.Zero);
        Assert.That(await context.NCAggregateSnapshots.AsNoTracking().CountAsync(value => value.AggregateType == ServerDbBase.NCMetaProjectTombstoneType &&
            value.AggregateVersion == 2), Is.Zero);
        Assert.That(await db.RetireNCMetaProjectsAsync(epoch, clock.Time), Is.EqualTo(ServerDbBase.NCMetaMaximumProjectRetirements));
        Assert.That(await db.RetireNCMetaProjectsAsync(epoch, clock.Time), Is.EqualTo(1));
    }

    private static ServerDbSqlite GetDb(out SqliteConnection connection, out DbContextOptions<SqliteServerDbContext> options)
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        options = new DbContextOptionsBuilder<SqliteServerDbContext>().UseSqlite(connection).Options;
        var captured = options;
        return new ServerDbSqlite(() => captured, true, new Mock<IConfigurationManager>().Object, true,
            new Mock<ISawmill>().Object, new Mock<ISerializationManager>().Object);
    }

    private static async Task CreateProjectAsync(ServerDbSqlite db, NCSessionEpoch epoch, NCActiveWorldTime time, Guid id, string[] roots) =>
        Assert.That((await db.ApplyNCPersistentMutationAsync(ProjectRequest(epoch, time, id, 0, ProjectPayload(id, 1, roots)))).Status,
            Is.EqualTo(NCPersistentMutationStatus.Applied));

    private static string ProjectPayload(Guid id, long revision, string[] roots) => JsonSerializer.Serialize(new
    {
        Id = id, Owner = "owner-1", Name = "private project", Revision = revision,
        Files = roots.Select((digest, index) => new { Id = Guid.Parse($"00000000-0000-0000-0000-{index + 1:D12}"), Path = "src/file" + index + ".meta", Revision = 1, Digest = digest, Bytes = 1 }).ToArray(),
        Releases = Array.Empty<object>(),
    });

    private static NCPersistentMutationRequest ProjectRequest(NCSessionEpoch epoch, NCActiveWorldTime time, Guid id, long expected, string payload) =>
        new(Guid.NewGuid(), "meta.project.write", "meta.owner", "owner-1", Guid.NewGuid(), "meta.project.changed",
            "meta.project", id.ToString("D"), expected, 1, payload, "{}", "{}", Guid.NewGuid(), null, epoch, time, []);

    private static NCPersistentAggregateWrite MarkerWrite(Guid id) => new(Guid.NewGuid(), "meta.project.deleted",
        ServerDbBase.NCMetaProjectTombstoneType, id.ToString("D"), 0, 1,
        JsonSerializer.Serialize(new NCMetaProjectTombstoneData(id, "owner-1")), "{}");

    private static NCPersistentBatchMutationRequest DeleteRequest(NCSessionEpoch epoch, NCActiveWorldTime time, Guid id, string[] roots) =>
        new(Guid.NewGuid(), "meta.project.manage", "meta.owner", "owner-1",
            [new(Guid.NewGuid(), "meta.project.changed", "meta.project", id.ToString("D"), 1, 1, ProjectPayload(id, 2, roots), "{}"), MarkerWrite(id)],
            "{}", Guid.NewGuid(), null, epoch, time, []);

    private static async Task AddRootAsync(ServerDbSqlite db, NCSessionEpoch epoch, NCActiveWorldTime time, string type, string id, string digest) =>
        Assert.That((await db.ApplyNCPersistentMutationAsync(ProjectRequest(epoch, time, Guid.NewGuid(), 0,
            JsonSerializer.Serialize(new { Digest = digest })) with { AggregateType = type, AggregateId = id })).Status,
            Is.EqualTo(NCPersistentMutationStatus.Applied));

    private static async Task CommitCheckpointAsync(ServerDbSqlite db, NCSessionEpoch epoch, NCActiveWorldTime time, string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var section = new NCRoundSnapshotSectionWrite("meta-aggregates", "world", "city", 0, 1,
            document.RootElement.GetArrayLength(), payload, NCMetaJournalIntegrity.HashPayload(payload));
        var descriptor = new NCRoundSnapshotInventorySection(section.SectionType, section.OwnerType, section.OwnerId, 0,
            section.FormatVersion, section.EntityCount, Encoding.UTF8.GetByteCount(payload), section.Checksum);
        Assert.That((await db.CommitNCRoundSnapshotAsync(new(Guid.NewGuid(), epoch.Value, time.Ticks, 1, "test", 0, "",
            NCRoundSnapshotInventoryIntegrity.Checksum([descriptor]), DateTime.UtcNow, [section], [], 3))).Status,
            Is.EqualTo(NCRoundSnapshotCommitStatus.Committed));
    }
}
