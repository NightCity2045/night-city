// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Meta.Hosting;
using Content.Server.Database;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.Identity;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Configuration;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.Meta;

[TestFixture]
[NonParallelizable]
public sealed class NCMetaAuthorityPersistenceTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = false };

    [Test]
    public async Task GrantCommitRejectsDemotionRoleAbaAndChangedDeviceAuthorityWithoutPartialWrites()
    {
        var db = GetDb(Server, out var connection);
        await using var connectionLease = connection;
        var user = new NetUserId(Guid.NewGuid());
        var preference = await db.InitPrefsAsync(user, new HumanoidCharacterProfile { Name = "Authority test" });
        var character = new NCCharacterId(preference.Profiles.Single().Id);
        Assert.That(await db.SetNCCharacterEmploymentAsync(user, character.Value, new ProtoId<JobPrototype>("Captain")), Is.True);
        var initial = await db.GetNCPlayerEmploymentProofAsync(character);
        Assert.That(initial, Is.Not.Null);
        Assert.That(initial!.LastEventId, Is.GreaterThan(0));
        var epoch = NCSessionEpoch.New();
        var clock = await db.StartNCActiveWorldSessionAsync(epoch);
        var deviceId = Guid.NewGuid().ToString("D");
        var owner = NCPartyRef.ForOrganization(NCOrganizationId.New()).ToString();
        var device = new NCMetaDeviceRecord(deviceId, owner, [], Guid.Empty, 1, 8);
        var seed = new NCPersistentMutationRequest(Guid.NewGuid(), "test.authority.seed", "meta.owner", owner,
            Guid.NewGuid(), "meta.device.seeded", "meta.device", deviceId, 0, 1, JsonSerializer.Serialize(device), "{}", "{}",
            Guid.NewGuid(), null, epoch, clock.Time, []);
        Assert.That((await db.ApplyNCPersistentMutationAsync(seed)).Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
        var originalDevice = await db.GetNCAggregateSnapshotAsync("meta.device", deviceId);
        var constraints = new NCMetaAuthorityConstraints(initial, [new(deviceId, owner, 8)]);

        async Task<NCPersistentBatchMutationResult> Commit(NCMetaAuthorityConstraints proof)
        {
            var installation = Guid.NewGuid();
            var policy = new MetaServiceGrantPolicy(installation, deviceId, owner, new('a', 64), 8,
                true, [], NCPartyRef.ForCharacter(character).ToString(), clock.Time.Ticks, 1);
            return await db.ApplyNCPersistentBatchMutationAsync(new(Guid.NewGuid(), "test.authority.commit", "meta.owner", owner,
                [new(Guid.NewGuid(), "meta.grants.approved", "meta.service-grants", installation.ToString("D"), 0, 1,
                    JsonSerializer.Serialize(policy), "{}")], "{}", Guid.NewGuid(), null, epoch, clock.Time, [], Authority: proof));
        }

        var accepted = await Commit(constraints);
        Assert.That(accepted.Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
        Assert.That(await db.SetNCCharacterEmploymentAsync(user, character.Value, new ProtoId<JobPrototype>("Engineer")), Is.True);
        var denied = await Commit(constraints);
        Assert.That(denied.Status, Is.EqualTo(NCPersistentMutationStatus.VersionConflict));
        foreach (var aggregate in denied.Aggregates)
            Assert.That(await db.GetNCAggregateSnapshotAsync(aggregate.AggregateType, aggregate.AggregateId), Is.Null);
        Assert.That(await db.SetNCCharacterEmploymentAsync(user, character.Value, new ProtoId<JobPrototype>("Captain")), Is.True);
        var restored = await db.GetNCPlayerEmploymentProofAsync(character);
        Assert.That(restored!.LastEventId, Is.GreaterThan(initial.LastEventId));
        Assert.That((await Commit(constraints)).Status, Is.EqualTo(NCPersistentMutationStatus.VersionConflict),
            "Returning to the same job cannot revive the old proof.");
        Assert.That((await Commit(new(restored, [new(deviceId, owner, 9)]))).Status,
            Is.EqualTo(NCPersistentMutationStatus.VersionConflict));
        Assert.That((await Commit(new(restored, [new(deviceId, owner, 8)]))).Status,
            Is.EqualTo(NCPersistentMutationStatus.Applied));
        Assert.That(await db.GetNCAggregateSnapshotAsync("meta.device", deviceId), Is.EqualTo(originalDevice),
            "Proof row locks must not change device state, epoch, revision or journal identity.");
    }

    [Test]
    public async Task EveryNativePlayerPersonnelPathPublishesOnlyAfterSuccessfulDurableChange()
    {
        var database = Server.ResolveDependency<IServerDbManager>();
        var user = new NetUserId(Guid.NewGuid());
        var preference = await database.InitPrefsAsync(user, new HumanoidCharacterProfile { Name = "Employee" }, CancellationToken.None);
        var employee = new NCCharacterId(preference.Profiles.Single().Id);
        var managerUser = new NetUserId(Guid.NewGuid());
        var managerPreference = await database.InitPrefsAsync(managerUser, new HumanoidCharacterProfile { Name = "Manager" }, CancellationToken.None);
        var manager = new NCCharacterId(managerPreference.Profiles.Single().Id);
        var notifications = 0;
        void Changed() => Interlocked.Increment(ref notifications);
        database.NCPlayerEmploymentChanged += Changed;
        try
        {
            async Task ExpectChange(Func<Task> action)
            {
                var generation = database.NCPlayerEmploymentGeneration;
                var count = Volatile.Read(ref notifications);
                await action();
                Assert.That(database.NCPlayerEmploymentGeneration, Is.GreaterThan(generation));
                Assert.That(Volatile.Read(ref notifications), Is.GreaterThan(count));
            }
            await ExpectChange(async () => Assert.That(await database.SetNCCharacterEmploymentAsync(user, employee.Value,
                new ProtoId<JobPrototype>("Captain")), Is.True));
            await ExpectChange(async () => Assert.That(await database.ResignNCCharacterEmploymentAsync(user, employee.Value), Is.True));
            await ExpectChange(async () => Assert.That((await database.SelfHireNCCharacterAsync(employee, "Engineer", 10000,
                "Employee", "Test self hire")).Result, Is.EqualTo(NCPersonnelChangeResult.Success)));
            await ExpectChange(async () => Assert.That((await database.ChangeNCCharacterPositionAsync(employee, "Captain", 10000,
                ["Engineer", "Captain"], NCEmploymentEventType.Promoted, manager, "Manager", "Test promotion")).Result,
                Is.EqualTo(NCPersonnelChangeResult.Success)));
            await ExpectChange(async () => Assert.That((await database.TerminateNCCharacterAsync(employee, ["Captain"], manager,
                "Manager", "Test dismissal")).Result, Is.EqualTo(NCPersonnelChangeResult.Success)));
            await ExpectChange(async () => Assert.That((await database.HireNCCharacterAsync(employee, "Captain", 10000, manager,
                "Manager", "Test hire")).Result, Is.EqualTo(NCPersonnelChangeResult.Success)));
            var beforeFailure = database.NCPlayerEmploymentGeneration;
            Assert.That((await database.HireNCCharacterAsync(employee, "Captain", 10000, manager, "Manager", "Already employed")).Result,
                Is.Not.EqualTo(NCPersonnelChangeResult.Success));
            Assert.That(database.NCPlayerEmploymentGeneration, Is.EqualTo(beforeFailure));
            Assert.That((await database.GetNCPlayerEmploymentProofAsync(employee))!.JobPrototypeId, Is.EqualTo("Captain"));
        }
        finally { database.NCPlayerEmploymentChanged -= Changed; }
    }

    private static ServerDbSqlite GetDb(RobustIntegrationTest.ServerIntegrationInstance server, out SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>();
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open(); options.UseSqlite(connection);
        return new ServerDbSqlite(() => options.Options, true, server.ResolveDependency<IConfigurationManager>(), true,
            server.ResolveDependency<ILogManager>().GetSawmill("db.ops"), server.ResolveDependency<ISerializationManager>());
    }
}
