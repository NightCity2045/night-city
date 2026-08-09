// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server.Database;
using Content.Shared._NC.City.ZoneTemplates;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Housing;
using Content.Shared._NC.Identity;
using Content.Shared.Preferences;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Configuration;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Serialization.Manager;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.Housing;

[TestFixture]
[TestOf(typeof(ServerDbBase))]
public sealed class NCHousingPersistenceTest : GameTest
{
    [Test]
    public async Task RentAndDesignPersistInOneTransaction()
    {
        var db = GetDb(Pair.Server);
        var renter = await CreateCharacterAsync(db, 500);
        var propertyId = await CreatePropertyAsync(db);
        var dueAt = DateTime.UtcNow.AddDays(7);

        var result = await RentAsync(db, propertyId, renter, 125, dueAt);

        Assert.Multiple(() =>
        {
            Assert.That(result.Result, Is.EqualTo(NCPropertyMutationResult.Success));
            Assert.That(result.Property?.OwnerCharacterId, Is.Null);
            Assert.That(result.Property?.Status, Is.EqualTo(NCPropertyStatus.Rented));
            Assert.That(result.Property?.Tenants.Single().CharacterId, Is.EqualTo(renter));
            Assert.That(result.Property?.CurrentTemplateId, Is.EqualTo("TestInterior"));
            Assert.That(result.Property?.CurrentTemplateRevision, Is.EqualTo(3));
            Assert.That(result.Property?.RentPolicyId, Is.EqualTo("TestRentPolicy"));
            Assert.That(result.Property?.NextRentDueAt, Is.EqualTo(dueAt));
            Assert.That(result.Grant?.ValidUntil, Is.Null);
            Assert.That(result.Balance, Is.EqualTo(375));
        });
    }

    [Test]
    public async Task TwoRentersCannotAcquireSameApartmentOrBothPay()
    {
        var db = GetDb(Pair.Server, synchronous: false);
        var firstRenter = await CreateCharacterAsync(db, 500);
        var secondRenter = await CreateCharacterAsync(db, 500);
        var propertyId = await CreatePropertyAsync(db);
        var dueAt = DateTime.UtcNow.AddDays(7);

        var attempts = await Task.WhenAll(
            RentAsync(db, propertyId, firstRenter, 125, dueAt),
            RentAsync(db, propertyId, secondRenter, 125, dueAt));
        var winner = attempts.Single(value => value.Result == NCPropertyMutationResult.Success);
        var loser = attempts.Single(value => value.Result != NCPropertyMutationResult.Success);
        var firstAccount = await db.GetNCBankAccountAsync(firstRenter);
        var secondAccount = await db.GetNCBankAccountAsync(secondRenter);

        Assert.Multiple(() =>
        {
            Assert.That(winner.Balance, Is.EqualTo(375));
            Assert.That(loser.Result, Is.EqualTo(NCPropertyMutationResult.NotAvailable));
            Assert.That(new[] { firstAccount?.Balance, secondAccount?.Balance },
                Is.EquivalentTo(new int?[] { 375, 500 }));
        });
    }

    [Test]
    public async Task RenewalChargesOnePeriodAndClearsDelinquency()
    {
        var db = GetDb(Pair.Server);
        var renter = await CreateCharacterAsync(db, 500);
        var propertyId = await CreatePropertyAsync(db);
        var rent = await RentAsync(db, propertyId, renter, 100, DateTime.UtcNow.AddMinutes(1));
        Assert.That(rent.Result, Is.EqualTo(NCPropertyMutationResult.Success));
        var delinquent = await db.TryMarkNCPropertyRentDelinquentAsync(propertyId, rent.Property!.Version);
        Assert.That(delinquent.Property?.Status, Is.EqualTo(NCPropertyStatus.Delinquent));

        var nextDueAt = DateTime.UtcNow.AddDays(7);
        var renewed = await db.TryRenewNCPropertyRentAsync(
            propertyId,
            renter,
            100,
            nextDueAt,
            delinquent.Property!.Version);

        Assert.Multiple(() =>
        {
            Assert.That(renewed.Result, Is.EqualTo(NCPropertyMutationResult.Success));
            Assert.That(renewed.Property?.Status, Is.EqualTo(NCPropertyStatus.Rented));
            Assert.That(renewed.Property?.NextRentDueAt, Is.EqualTo(nextDueAt));
            Assert.That(renewed.Balance, Is.EqualTo(300));
        });
    }

    [Test]
    public async Task FailedRenewalCanEnterDelinquencyWithoutCharging()
    {
        var db = GetDb(Pair.Server);
        var renter = await CreateCharacterAsync(db, 50);
        var propertyId = await CreatePropertyAsync(db);
        var rent = await RentAsync(db, propertyId, renter, 0, DateTime.UtcNow.AddMinutes(1));
        Assert.That(rent.Result, Is.EqualTo(NCPropertyMutationResult.Success));

        var renewal = await db.TryRenewNCPropertyRentAsync(
            propertyId,
            renter,
            100,
            DateTime.UtcNow.AddDays(7),
            rent.Property!.Version);
        Assert.That(renewal.Result, Is.EqualTo(NCPropertyMutationResult.InsufficientFunds));
        var delinquent = await db.TryMarkNCPropertyRentDelinquentAsync(propertyId, rent.Property.Version);
        var account = await db.GetNCBankAccountAsync(renter);

        Assert.Multiple(() =>
        {
            Assert.That(delinquent.Result, Is.EqualTo(NCPropertyMutationResult.Success));
            Assert.That(delinquent.Property?.Status, Is.EqualTo(NCPropertyStatus.Delinquent));
            Assert.That(account?.Balance, Is.EqualTo(50));
        });
    }

    [Test]
    public async Task NonPaymentEvictionClearsLeaseDesignAccessAndSnapshot()
    {
        var db = GetDb(Pair.Server);
        var renter = await CreateCharacterAsync(db, 500);
        var propertyId = await CreatePropertyAsync(db);
        var rent = await RentAsync(db, propertyId, renter, 0, DateTime.UtcNow.AddMinutes(1));
        Assert.That(rent.Result, Is.EqualTo(NCPropertyMutationResult.Success));
        Assert.That(await db.UpsertNCPropertySnapshotAsync(new NCPropertySnapshotData(
            propertyId,
            "TestInterior",
            3,
            1,
            "{}",
            3,
            1,
            DateTime.UtcNow)), Is.True);

        var eviction = await db.TryEvictNCPropertyForNonPaymentAsync(propertyId, rent.Property!.Version);
        var snapshots = await db.GetNCPropertySnapshotsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(eviction.Result, Is.EqualTo(NCPropertyMutationResult.Success));
            Assert.That(eviction.Property?.Status, Is.EqualTo(NCPropertyStatus.Available));
            Assert.That(eviction.Property?.Tenants.Single().RevokedAt, Is.Not.Null);
            Assert.That(eviction.Property?.CurrentTemplateId, Is.Null);
            Assert.That(eviction.Property?.NextRentDueAt, Is.Null);
            Assert.That(eviction.Property?.RentPolicyId, Is.Empty);
            Assert.That(snapshots, Is.Empty);
        });
    }

    [Test]
    public async Task LegacyOwnerConvertsToRenterWithoutLosingDesign()
    {
        var db = GetDb(Pair.Server);
        var character = await CreateCharacterAsync(db, 500);
        var propertyId = await CreatePropertyAsync(db);
        var purchase = await db.TryPurchaseNCPropertyAsync(
            propertyId,
            character,
            0,
            NCPropertyPermission.All);
        Assert.That(purchase.Result, Is.EqualTo(NCPropertyMutationResult.Success));

        var dueAt = DateTime.UtcNow.AddDays(7);
        var conversion = await db.TryConvertNCPropertyOwnerToRentalAsync(
            propertyId,
            character,
            "TestInterior",
            3,
            "TestRentPolicy",
            dueAt,
            purchase.Property!.Version);

        Assert.Multiple(() =>
        {
            Assert.That(conversion.Result, Is.EqualTo(NCPropertyMutationResult.Success));
            Assert.That(conversion.Property?.OwnerCharacterId, Is.Null);
            Assert.That(conversion.Property?.Status, Is.EqualTo(NCPropertyStatus.Rented));
            Assert.That(conversion.Property?.Tenants.Single().CharacterId, Is.EqualTo(character));
            Assert.That(conversion.Property?.CurrentTemplateId, Is.EqualTo("TestInterior"));
            Assert.That(conversion.Property?.NextRentDueAt, Is.EqualTo(dueAt));
        });
    }

    [Test]
    public async Task SnapshotUpsertIsAtomicAndRejectsInvalidReplacement()
    {
        var db = GetDb(Pair.Server);
        var propertyId = await CreatePropertyAsync(db);
        var first = new NCPropertySnapshotData(
            propertyId,
            "TestInterior",
            3,
            1,
            "{\"version\":1}",
            4,
            2,
            DateTime.UtcNow);

        Assert.That(await db.UpsertNCPropertySnapshotAsync(first), Is.True);
        Assert.That(await db.UpsertNCPropertySnapshotAsync(first with
        {
            Payload = string.Empty,
            EntityCount = 0,
            ResidentEntityCount = 0,
        }), Is.False);

        var stored = (await db.GetNCPropertySnapshotsAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(stored.PropertyId, Is.EqualTo(propertyId));
            Assert.That(stored.Payload, Is.EqualTo(first.Payload));
            Assert.That(stored.EntityCount, Is.EqualTo(4));
            Assert.That(stored.ResidentEntityCount, Is.EqualTo(2));
        });
    }

    private static Task<NCPropertyMutationData> RentAsync(
        ServerDbSqlite db,
        NCApartmentId propertyId,
        NCCharacterId characterId,
        int rent,
        DateTime nextRentDueAt)
    {
        return db.TryRentNCPropertyAsync(
            propertyId,
            characterId,
            "TestInterior",
            3,
            "TestRentPolicy",
            rent,
            nextRentDueAt,
            NCPropertyPermission.All);
    }

    private static async Task<NCCharacterId> CreateCharacterAsync(ServerDbSqlite db, int balance)
    {
        var preference = await db.InitPrefsAsync(new NetUserId(Guid.NewGuid()), new HumanoidCharacterProfile());
        var characterId = new NCCharacterId(preference.Profiles.Single().Id);
        var account = await db.GetOrCreateNCBankAccountAsync(characterId, balance, "NC", 4);
        Assert.That(account, Is.Not.Null);
        return characterId;
    }

    private static async Task<NCApartmentId> CreatePropertyAsync(ServerDbSqlite db)
    {
        var propertyId = new NCApartmentId(new NCZoneId(Guid.NewGuid()));
        var property = await db.EnsureNCPropertyAsync(new NCPropertySeedData(
            propertyId,
            new NCBuildingId(new NCZoneId(Guid.NewGuid())),
            [],
            0,
            string.Empty,
            "TestAccessPolicy"));
        Assert.That(property, Is.Not.Null);
        return propertyId;
    }

    private static ServerDbSqlite GetDb(
        RobustIntegrationTest.ServerIntegrationInstance server,
        bool synchronous = true)
    {
        var configuration = server.ResolveDependency<IConfigurationManager>();
        var serialization = server.ResolveDependency<ISerializationManager>();
        var operationsLog = server.ResolveDependency<ILogManager>().GetSawmill("db.ops");
        var options = new DbContextOptionsBuilder<SqliteServerDbContext>();
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        options.UseSqlite(connection);
        return new ServerDbSqlite(() => options.Options, true, configuration, synchronous, operationsLog, serialization);
    }
}
