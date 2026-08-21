// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Fixers;
using Content.Server._NC.LivingCity.Housing;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Market;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Housing;
using Content.Shared._NC.Identity;
using Content.Shared._NC.LivingCity.Fixers;
using Content.Shared._NC.LivingCity.Housing;
using Content.Shared._NC.LivingCity.Market;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Preferences;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Market;

[TestFixture]
[TestOf(typeof(NCMarketRegistrySystem))]
[TestOf(typeof(NCPropertyMarketRegistrySystem))]
[TestOf(typeof(NCFixerRegistrySystem))]
[NonParallelizable]
public sealed class NCMarketPropertyFixerSystemTest : GameTest
{
    [Test]
    public async Task MarketPropertyReservationAndFixerAccessArePersistentAndReplaySafe()
    {
        var server = Pair.Server;
        var database = server.ResolveDependency<IServerDbManager>();
        var clock = server.System<NCActiveWorldTimeSystem>();
        var market = server.System<NCMarketRegistrySystem>();
        var properties = server.System<NCPropertyRegistrySystem>();
        var propertyMarket = server.System<NCPropertyMarketRegistrySystem>();
        var fixers = server.System<NCFixerRegistrySystem>();
        var seller = NCPartyRef.ForCharacter(await CreateCharacterAsync(database));
        var buyer = NCPartyRef.ForCharacter(await CreateCharacterAsync(database));

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady, "The active-world clock did not start.");

        var request = new NCCreateMarketRequestRequest(
            Guid.NewGuid(), NCMarketRequestId.New(), NCMarketRequestKind.Commodity, "NCCommodityFoodKibble",
            20, 25, 500, buyer, NCMarketChannel.Agent,
            new NCMarketAccessPolicy(NCMarketVisibility.Public, [], string.Empty, 0, 0),
            new NCZoneId(Guid.NewGuid()), TimeSpan.FromHours(1), buyer, Guid.NewGuid(),
            AuditSource: "phase-ten-test");
        var requestCreated = await market.CreateRequestAsync(request);
        Assert.Multiple(() =>
        {
            Assert.That(requestCreated.Status, Is.EqualTo(NCMarketMutationStatus.Applied));
            Assert.That(requestCreated.Request?.Snapshot.Quantity, Is.EqualTo(20));
            Assert.That(requestCreated.Request?.Snapshot.Status, Is.EqualTo(NCMarketRequestStatus.Open));
        });
        Assert.That((await market.CreateRequestAsync(request)).Status, Is.EqualTo(NCMarketMutationStatus.Duplicate));
        Assert.That((await market.FindOpenRequestAsync(buyer, NCMarketRequestKind.Commodity,
            "NCCommodityFoodKibble"))?.Snapshot.RequestId, Is.EqualTo(request.RequestId));

        var apartment = new NCApartmentId(new NCZoneId(Guid.NewGuid()));
        var registration = await properties.RegisterAsync(new NCRegisterPropertyRequest(
            Guid.NewGuid(), apartment, new NCBuildingId(new NCZoneId(Guid.NewGuid())), [apartment.ZoneId],
            "phase-eleven-property", seller, 1_000, "StandardApartment",
            new NCPropertyStorageBudget(128, 128, 256, 512), "phase-eleven-blueprint", Guid.NewGuid(),
            AuditSource: "phase-eleven-test"));
        Assert.That(registration.Status, Is.EqualTo(NCPropertyAggregateMutationStatus.Applied));

        var listingId = NCPersistentIdentity.PropertyListing(apartment, "phase-eleven-sale");
        var listingRequest = new NCCreatePropertyListingRequest(
            Guid.NewGuid(), listingId, "phase-eleven-sale", apartment, registration.Property!.AggregateVersion,
            NCPropertyOfferKind.Sale, 2_000, 0, string.Empty, 0, null, null, NCPropertyPermission.All,
            TimeSpan.FromHours(2), seller, Guid.NewGuid(), AuditSource: "phase-eleven-test");
        var listing = await propertyMarket.CreateListingAsync(listingRequest);
        Assert.That(listing.Status, Is.EqualTo(NCPropertyAggregateMutationStatus.Applied));

        var quoteId = NCPersistentIdentity.PropertyQuote(listingId, buyer, "phase-eleven-quote");
        var quote = await propertyMarket.CreateQuoteAsync(new NCCreatePropertyQuoteRequest(
            Guid.NewGuid(), quoteId, listingId, listing.Listing!.AggregateVersion, buyer,
            TimeSpan.FromMinutes(5), Guid.NewGuid(), AuditSource: "phase-eleven-test"));
        Assert.That(quote.Status, Is.EqualTo(NCPropertyAggregateMutationStatus.Applied));
        var reservationId = NCPersistentIdentity.PropertyReservation(quoteId, "phase-eleven-reservation");
        var reservation = await propertyMarket.ReserveAsync(new NCReservePropertyQuoteRequest(
            Guid.NewGuid(), reservationId, quoteId, quote.Quote!.AggregateVersion, TimeSpan.FromMinutes(2), buyer,
            Guid.NewGuid(), AuditSource: "phase-eleven-test"));
        Assert.Multiple(() =>
        {
            Assert.That(reservation.Status, Is.EqualTo(NCPropertyAggregateMutationStatus.Applied));
            Assert.That(reservation.Reservation?.Snapshot.Status, Is.EqualTo(NCPropertyReservationStatus.Active));
            Assert.That(reservation.Listing?.Snapshot.Status, Is.EqualTo(NCPropertyListingStatus.Reserved));
        });

        var grant = new NCGrantFixerAccessRequest(Guid.NewGuid(), seller, buyer, seller, 0, 0, string.Empty, [],
            null, "trusted introduction", Guid.NewGuid(), AuditSource: "phase-twelve-test");
        var access = await fixers.GrantAccessAsync(grant);
        Assert.That(access.Status, Is.EqualTo(NCFixerMutationStatus.Applied));
        Assert.That((await fixers.GrantAccessAsync(grant)).Status, Is.EqualTo(NCFixerMutationStatus.Duplicate));
        Assert.That(await fixers.HasAccessAsync(seller, buyer, true), Is.True);

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
        await WaitUntil(server, () => !clock.IsReady, "The active-world clock did not stop.");
    }

    private static async Task<NCCharacterId> CreateCharacterAsync(IServerDbManager database)
    {
        var preference = await database.InitPrefsAsync(new NetUserId(Guid.NewGuid()),
            new HumanoidCharacterProfile(), CancellationToken.None);
        return new NCCharacterId(preference.Profiles.Single().Id);
    }

    private static async Task WaitUntil(RobustIntegrationTest.ServerIntegrationInstance server,
        Func<bool> predicate, string failure, int attempts = 180)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            await server.WaitRunTicks(1);
            var complete = false;
            await server.WaitPost(() => complete = predicate());
            if (complete) return;
        }
        Assert.Fail(failure);
    }
}
