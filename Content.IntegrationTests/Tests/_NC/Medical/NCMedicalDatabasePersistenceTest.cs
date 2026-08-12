// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server.Database;
using Content.Shared._NC.Identity;
using Content.Shared.Preferences;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Configuration;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Serialization.Manager;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.Medical;

[TestFixture]
[TestOf(typeof(ServerDbBase))]
public sealed class NCMedicalDatabasePersistenceTest : GameTest
{
    [Test]
    public async Task SnapshotSurvivesReconnectAndStaleRevisionCannotOverwriteIt()
    {
        var database = GetDb(Pair.Server);
        var characterId = await CreateCharacterAsync(database);
        var captured = DateTime.UtcNow;
        var first = await database.TrySaveNCMedicalSnapshotAsync(
            Row(characterId, 0, "version: 2\ncapturedAt: 00:00:05\n", captured),
            0);
        var reconnected = await database.GetNCMedicalSnapshotAsync(characterId);
        var second = await database.TrySaveNCMedicalSnapshotAsync(
            Row(characterId, first.Revision, "version: 2\ncapturedAt: 00:00:10\n", captured),
            first.Revision);
        var stale = await database.TrySaveNCMedicalSnapshotAsync(
            Row(characterId, first.Revision, "version: 2\ncapturedAt: 00:00:15\n", captured),
            first.Revision);
        var afterRestart = await database.GetNCMedicalSnapshotAsync(characterId);

        Assert.Multiple(() =>
        {
            Assert.That(first.Status, Is.EqualTo(NCMedicalSnapshotWriteStatus.Saved));
            Assert.That(first.Revision, Is.EqualTo(1));
            Assert.That(reconnected?.Revision, Is.EqualTo(1));
            Assert.That(second.Status, Is.EqualTo(NCMedicalSnapshotWriteStatus.Saved));
            Assert.That(second.Revision, Is.EqualTo(2));
            Assert.That(stale.Status, Is.EqualTo(NCMedicalSnapshotWriteStatus.Conflict));
            Assert.That(stale.Revision, Is.EqualTo(2));
            Assert.That(afterRestart?.Revision, Is.EqualTo(2));
            Assert.That(afterRestart?.Payload, Does.Contain("00:00:10"));
        });
    }

    [Test]
    public async Task ProfileDeletionCascadesExactlyOneMedicalSnapshot()
    {
        var database = GetDb(Pair.Server);
        var userId = new NetUserId(Guid.NewGuid());
        var preference = await database.InitPrefsAsync(userId, new HumanoidCharacterProfile());
        var characterId = new NCCharacterId(preference.Profiles.Single().Id);
        var saved = await database.TrySaveNCMedicalSnapshotAsync(
            Row(characterId, 0, "version: 2\n", DateTime.UtcNow),
            0);
        Assert.That(saved.Status, Is.EqualTo(NCMedicalSnapshotWriteStatus.Saved));

        await database.SaveCharacterSlotAsync(userId, null, 0);

        Assert.That(await database.GetNCMedicalSnapshotAsync(characterId), Is.Null);
    }

    private static NCMedicalStoredSnapshotData Row(
        NCCharacterId characterId,
        long revision,
        string payload,
        DateTime capturedUtc)
    {
        return new NCMedicalStoredSnapshotData(
            characterId,
            2,
            revision,
            payload,
            DateTime.SpecifyKind(capturedUtc, DateTimeKind.Utc),
            DateTime.SpecifyKind(capturedUtc, DateTimeKind.Utc));
    }

    private static async Task<NCCharacterId> CreateCharacterAsync(ServerDbSqlite database)
    {
        var preference = await database.InitPrefsAsync(
            new NetUserId(Guid.NewGuid()),
            new HumanoidCharacterProfile());
        return new NCCharacterId(preference.Profiles.Single().Id);
    }

    private static ServerDbSqlite GetDb(RobustIntegrationTest.ServerIntegrationInstance server)
    {
        var configuration = server.ResolveDependency<IConfigurationManager>();
        var serialization = server.ResolveDependency<ISerializationManager>();
        var operationsLog = server.ResolveDependency<ILogManager>().GetSawmill("db.ops");
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>();
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        options.UseSqlite(connection);
        return new ServerDbSqlite(() => options.Options, true, configuration, true, operationsLog, serialization);
    }
}
