// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.Identity;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Preferences;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Economy;

[TestFixture]
[TestOf(typeof(NCEconomicAssetRegistrySystem))]
[NonParallelizable]
public sealed class NCEconomicAssetRegistrySystemTest : GameTest
{
    [Test]
    public async Task StockBatchAndItemCreationAreAtomicBoundedAndReplaySafe()
    {
        var server = Pair.Server;
        var database = server.ResolveDependency<IServerDbManager>();
        var clock = server.System<NCActiveWorldTimeSystem>();
        var assets = server.System<NCEconomicAssetRegistrySystem>();
        var reconciliation = server.System<NCEconomicReconciliationSystem>();
        var finance = server.System<NCPartyFinanceAdapterSystem>();
        var owner = NCPartyRef.ForCharacter(await CreateCharacterAsync(database));
        var errors = new List<string>();
        Assert.That(assets.ValidatePrototypes(errors), Is.True, string.Join(Environment.NewLine, errors));

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await WaitUntil(server, () => clock.IsReady, "The active-world clock did not start.");

        var stockNodeId = NCPersistentIdentity.StockNode(owner, "test-medical-locker");
        var createNode = new NCCreateStockNodeRequest(
            Guid.NewGuid(),
            stockNodeId,
            "test-medical-locker",
            NCStockNodeKind.CharacterInventory,
            owner,
            "test-zone.medical-locker",
            true,
            true,
            owner,
            Guid.NewGuid(),
            AuditSource: "phase-nine-test");
        var nodeCreated = await assets.CreateStockNodeAsync(createNode);
        Assert.Multiple(() =>
        {
            Assert.That(nodeCreated.Status, Is.EqualTo(NCEconomicAssetMutationStatus.Applied));
            Assert.That(nodeCreated.StockNode?.AggregateVersion, Is.EqualTo(1));
            Assert.That(nodeCreated.StockNode?.Snapshot.Owner, Is.EqualTo(owner));
        });
        Assert.That((await assets.CreateStockNodeAsync(createNode)).Status,
            Is.EqualTo(NCEconomicAssetMutationStatus.Duplicate));

        var sourceEvent = Guid.NewGuid();
        var batchId = NCPersistentIdentity.CommodityBatch(
            "NCCommodityTraumaAnalgesic",
            sourceEvent,
            "lot-2045-08-21-a");
        var createBatch = new NCCreateCommodityBatchRequest(
            Guid.NewGuid(),
            batchId,
            new ProtoId<NCCommodityPrototype>("NCCommodityTraumaAnalgesic"),
            250,
            875,
            owner,
            stockNodeId,
            1,
            "clinic-delivery",
            default,
            [sourceEvent],
            owner,
            Guid.NewGuid(),
            AuditSource: "phase-nine-test");
        var batchCreated = await assets.CreateCommodityBatchAsync(createBatch);
        Assert.Multiple(() =>
        {
            Assert.That(batchCreated.Status, Is.EqualTo(NCEconomicAssetMutationStatus.Applied));
            Assert.That(batchCreated.StockNode?.AggregateVersion, Is.EqualTo(2));
            Assert.That(batchCreated.CommodityBatch?.Snapshot.Quantity, Is.EqualTo(250));
            Assert.That(batchCreated.CommodityBatch?.Snapshot.Quality, Is.EqualTo(875));
            Assert.That(batchCreated.CommodityBatch?.Snapshot.Location.Kind,
                Is.EqualTo(NCEconomicAssetLocationKind.StockNode));
            Assert.That(batchCreated.StockNode?.Snapshot.CommodityBatchIds, Is.EqualTo(new[] { batchId }));
        });
        Assert.That((await assets.CreateCommodityBatchAsync(createBatch)).Status,
            Is.EqualTo(NCEconomicAssetMutationStatus.Duplicate));

        var itemId = NCItemId.New();
        var itemEvent = Guid.NewGuid();
        var staleItem = new NCCreateItemInstanceRequest(
            Guid.NewGuid(),
            itemId,
            new ProtoId<NCItemDefinitionPrototype>("NCItemCyberwareNeuralLinkMk1"),
            "ZTC-NL1-000001",
            920,
            owner,
            stockNodeId,
            1,
            "authorized-clinic-stock",
            default,
            [itemEvent],
            owner,
            Guid.NewGuid(),
            AuditSource: "phase-nine-test");
        Assert.That((await assets.CreateItemInstanceAsync(staleItem)).Status,
            Is.EqualTo(NCEconomicAssetMutationStatus.VersionConflict));
        Assert.That(await assets.GetItemAsync(itemId, true), Is.Null,
            "A rejected stale stock version must not leave a partial item aggregate.");

        var createItem = staleItem with
        {
            CommandId = Guid.NewGuid(),
            ExpectedStockNodeVersion = 2,
        };
        var itemCreated = await assets.CreateItemInstanceAsync(createItem);
        Assert.Multiple(() =>
        {
            Assert.That(itemCreated.Status, Is.EqualTo(NCEconomicAssetMutationStatus.Applied));
            Assert.That(itemCreated.StockNode?.AggregateVersion, Is.EqualTo(3));
            Assert.That(itemCreated.Item?.Snapshot.ItemId, Is.EqualTo(itemId));
            Assert.That(itemCreated.Item?.Snapshot.SerialNumber, Is.EqualTo("ZTC-NL1-000001"));
            Assert.That(itemCreated.Item?.Snapshot.InstalledPartIds, Is.Empty);
            Assert.That(itemCreated.StockNode?.Snapshot.ItemIds, Is.EqualTo(new[] { itemId }));
        });
        Assert.That((await assets.CreateItemInstanceAsync(createItem)).Status,
            Is.EqualTo(NCEconomicAssetMutationStatus.Duplicate));

        var buyer = NCPartyRef.ForCharacter(await CreateCharacterAsync(database));
        var quoteRequest = new NCCreateEconomicQuoteRequest(
            Guid.NewGuid(),
            NCEconomicQuoteId.New(),
            NCEconomicOfferKind.CommodityBatch,
            batchId.ToString(),
            50,
            owner,
            buyer,
            stockNodeId,
            3,
            1,
            new NCEconomicPriceContext(0, 0, 0, 0, -1_000, 0),
            TimeSpan.FromMinutes(4),
            buyer,
            Guid.NewGuid(),
            AuditSource: "phase-nine-test");
        var quoteCreated = await assets.CreateQuoteAsync(quoteRequest);
        Assert.Multiple(() =>
        {
            Assert.That(quoteCreated.Status, Is.EqualTo(NCEconomicAssetMutationStatus.Applied));
            Assert.That(quoteCreated.Quote?.Snapshot.PriceContext.Legality, Is.EqualTo(500),
                "Legal risk must be derived from the authoritative commodity, not accepted from the caller.");
            Assert.That(quoteCreated.Quote?.Snapshot.Price.BaseAmount, Is.EqualTo(88));
            Assert.That(quoteCreated.Quote?.Snapshot.Price.AppliedMultiplierBasisPoints, Is.EqualTo(11_000));
            Assert.That(quoteCreated.Quote?.Snapshot.Price.Total, Is.EqualTo(97));
        });
        Assert.That((await assets.CreateQuoteAsync(quoteRequest)).Status,
            Is.EqualTo(NCEconomicAssetMutationStatus.Duplicate));

        var reservationRequest = new NCCreateEconomicReservationRequest(
            Guid.NewGuid(),
            NCEconomicReservationId.New(),
            quoteRequest.QuoteId,
            1,
            TimeSpan.FromMinutes(2),
            buyer,
            Guid.NewGuid(),
            AuditSource: "phase-nine-test");
        var reservationCreated = await assets.CreateReservationAsync(reservationRequest);
        Assert.Multiple(() =>
        {
            Assert.That(reservationCreated.Status, Is.EqualTo(NCEconomicAssetMutationStatus.Applied));
            Assert.That(reservationCreated.Reservation?.Snapshot.Status,
                Is.EqualTo(NCEconomicReservationStatus.Active));
            Assert.That(reservationCreated.Quote?.Snapshot.Status, Is.EqualTo(NCEconomicQuoteStatus.Reserved));
            Assert.That(reservationCreated.CommodityBatch?.Snapshot.ReservedQuantity, Is.EqualTo(50));
            Assert.That(reservationCreated.CommodityBatch?.AggregateVersion, Is.EqualTo(2));
            Assert.That(reservationCreated.StockNode?.AggregateVersion, Is.EqualTo(4));
        });

        var releaseRequest = new NCReleaseEconomicReservationRequest(
            Guid.NewGuid(),
            reservationRequest.ReservationId,
            1,
            NCEconomicReservationStatus.Released,
            buyer,
            Guid.NewGuid(),
            AuditSource: "phase-nine-test");
        var released = await assets.ReleaseReservationAsync(releaseRequest);
        Assert.Multiple(() =>
        {
            Assert.That(released.Status, Is.EqualTo(NCEconomicAssetMutationStatus.Applied));
            Assert.That(released.Reservation?.Snapshot.Status, Is.EqualTo(NCEconomicReservationStatus.Released));
            Assert.That(released.Quote?.Snapshot.Status, Is.EqualTo(NCEconomicQuoteStatus.Cancelled));
            Assert.That(released.CommodityBatch?.Snapshot.ReservedQuantity, Is.Zero);
            Assert.That(released.CommodityBatch?.AggregateVersion, Is.EqualTo(3));
            Assert.That(released.StockNode?.AggregateVersion, Is.EqualTo(5));
        });
        Assert.That((await assets.ReleaseReservationAsync(releaseRequest)).Status,
            Is.EqualTo(NCEconomicAssetMutationStatus.Duplicate));

        var collidingItemId = NCItemId.New();
        var collision = await assets.CreateItemInstanceAsync(createItem with
        {
            CommandId = createBatch.CommandId,
            ItemId = collidingItemId,
            SerialNumber = "ZTC-NL1-COLLISION",
            ExpectedStockNodeVersion = 3,
        });
        var collidedItem = await assets.GetItemAsync(collidingItemId, true);
        Assert.Multiple(() =>
        {
            Assert.That(collision.Status, Is.EqualTo(NCEconomicAssetMutationStatus.Rejected));
            Assert.That(collidedItem, Is.Null);
        });

        var destinationNodeId = NCPersistentIdentity.StockNode(buyer, "test-buyer-inventory");
        var destinationCreated = await assets.CreateStockNodeAsync(new NCCreateStockNodeRequest(
            Guid.NewGuid(),
            destinationNodeId,
            "test-buyer-inventory",
            NCStockNodeKind.CharacterInventory,
            buyer,
            "test-zone.buyer-inventory",
            true,
            true,
            buyer,
            Guid.NewGuid(),
            AuditSource: "phase-nine-test"));
        Assert.That(destinationCreated.Status, Is.EqualTo(NCEconomicAssetMutationStatus.Applied));

        var buyerEmptyAccount = await finance.EnsurePrimaryAccountAsync(buyer, "phase-nine-test");
        var sellerAccount = await finance.EnsurePrimaryAccountAsync(owner, "phase-nine-test");
        Assert.Multiple(() =>
        {
            Assert.That(buyerEmptyAccount.IsResolved, Is.True, buyerEmptyAccount.Error);
            Assert.That(sellerAccount.IsResolved, Is.True, sellerAccount.Error);
        });

        var itemQuoteRequest = new NCCreateEconomicQuoteRequest(
            Guid.NewGuid(),
            NCEconomicQuoteId.New(),
            NCEconomicOfferKind.ItemInstance,
            itemId.ToString(),
            1,
            owner,
            buyer,
            stockNodeId,
            5,
            1,
            new NCEconomicPriceContext(0, 0, 0, 0, 0, 0),
            TimeSpan.FromMinutes(4),
            buyer,
            Guid.NewGuid(),
            AuditSource: "phase-nine-test");
        var itemQuote = await assets.CreateQuoteAsync(itemQuoteRequest);
        Assert.That(itemQuote.Status, Is.EqualTo(NCEconomicAssetMutationStatus.Applied));
        Assert.That(itemQuote.Quote?.Snapshot.Price.Total, Is.EqualTo(506));

        var itemReservationRequest = new NCCreateEconomicReservationRequest(
            Guid.NewGuid(),
            NCEconomicReservationId.New(),
            itemQuoteRequest.QuoteId,
            1,
            TimeSpan.FromMinutes(2),
            buyer,
            Guid.NewGuid(),
            AuditSource: "phase-nine-test");
        var itemReserved = await assets.CreateReservationAsync(itemReservationRequest);
        Assert.Multiple(() =>
        {
            Assert.That(itemReserved.Status, Is.EqualTo(NCEconomicAssetMutationStatus.Applied));
            Assert.That(itemReserved.Item?.Snapshot.ReservationId,
                Is.EqualTo(itemReservationRequest.ReservationId));
            Assert.That(itemReserved.Item?.AggregateVersion, Is.EqualTo(2));
            Assert.That(itemReserved.StockNode?.AggregateVersion, Is.EqualTo(6));
        });

        var purchaseTransferId = NCEconomicTransferId.New();
        var unfundedSettlementRequest = new NCSettleEconomicReservationRequest(
            Guid.NewGuid(),
            NCTransactionId.New(),
            itemReservationRequest.ReservationId,
            1,
            buyerEmptyAccount.AccountId,
            buyerEmptyAccount.Account!.AggregateVersion,
            sellerAccount.AccountId,
            sellerAccount.Account!.AggregateVersion,
            null,
            0,
            null,
            0,
            destinationNodeId,
            1,
            purchaseTransferId,
            null,
            null,
            buyer,
            Guid.NewGuid(),
            AuditSource: "phase-nine-test");
        var unfunded = await assets.SettleReservationAsync(unfundedSettlementRequest);
        var reservationAfterFailure = await assets.GetReservationAsync(itemReservationRequest.ReservationId, true);
        var itemAfterFailure = await assets.GetItemAsync(itemId, true);
        var transferAfterFailure = await assets.GetTransferAsync(purchaseTransferId, true);
        Assert.Multiple(() =>
        {
            Assert.That(unfunded.Status, Is.EqualTo(NCEconomicTradeMutationStatus.InsufficientFunds));
            Assert.That(reservationAfterFailure?.Snapshot.Status, Is.EqualTo(NCEconomicReservationStatus.Active));
            Assert.That(itemAfterFailure?.Snapshot.ReservationId, Is.EqualTo(itemReservationRequest.ReservationId));
            Assert.That(transferAfterFailure, Is.Null,
                "A failed monetary leg must roll back every participant write.");
        });

        var buyerAccount = await finance.EnsurePrimaryAccountWithOpeningBalanceAsync(
            buyer, 1_000, "phase-nine-test");
        Assert.That(buyerAccount.IsResolved, Is.True, buyerAccount.Error);
        var settlementRequest = unfundedSettlementRequest with
        {
            CommandId = Guid.NewGuid(),
            TransactionId = NCTransactionId.New(),
            ExpectedBuyerAccountVersion = buyerAccount.Account!.AggregateVersion,
            CorrelationId = Guid.NewGuid(),
        };
        var settled = await assets.SettleReservationAsync(settlementRequest);
        Assert.Multiple(() =>
        {
            Assert.That(settled.Status, Is.EqualTo(NCEconomicTradeMutationStatus.Applied));
            Assert.That(settled.Ledger?.Transaction?.Snapshot.Legs.Sum(leg => leg.Amount), Is.Zero);
            Assert.That(settled.Quote?.Snapshot.Status, Is.EqualTo(NCEconomicQuoteStatus.Settled));
            Assert.That(settled.Reservation?.Snapshot.Status, Is.EqualTo(NCEconomicReservationStatus.Settled));
            Assert.That(settled.Transfer?.Snapshot.QuoteId, Is.EqualTo(itemQuoteRequest.QuoteId));
            Assert.That(settled.Item?.Snapshot.Owner, Is.EqualTo(buyer));
            Assert.That(settled.Item?.Snapshot.ReservationId, Is.Null);
            Assert.That(settled.SourceStockNode?.AggregateVersion, Is.EqualTo(7));
            Assert.That(settled.DestinationStockNode?.AggregateVersion, Is.EqualTo(2));
            Assert.That(settled.Ledger?.Accounts.Single(account => account.Snapshot.Owner == buyer).Snapshot.Balance,
                Is.EqualTo(494));
            Assert.That(settled.Ledger?.Accounts.Single(account => account.Snapshot.Owner == owner).Snapshot.Balance,
                Is.EqualTo(506));
        });
        Assert.That((await assets.SettleReservationAsync(settlementRequest)).Status,
            Is.EqualTo(NCEconomicTradeMutationStatus.Duplicate));

        var serviceQuoteRequest = new NCCreateEconomicQuoteRequest(
            Guid.NewGuid(),
            NCEconomicQuoteId.New(),
            NCEconomicOfferKind.Service,
            "NCEconomicServiceCyberwareStreetMaintenance",
            1,
            owner,
            buyer,
            null,
            0,
            0,
            new NCEconomicPriceContext(0, 0, 0, 0, -1_000, 0),
            TimeSpan.FromMinutes(4),
            buyer,
            Guid.NewGuid(),
            AuditSource: "phase-nine-test");
        var serviceQuote = await assets.CreateQuoteAsync(serviceQuoteRequest);
        Assert.Multiple(() =>
        {
            Assert.That(serviceQuote.Status, Is.EqualTo(NCEconomicAssetMutationStatus.Applied));
            Assert.That(serviceQuote.Quote?.Snapshot.PriceContext.Legality, Is.EqualTo(500));
            Assert.That(serviceQuote.Quote?.Snapshot.Price.Total, Is.EqualTo(110));
        });

        var serviceReservationRequest = new NCCreateEconomicReservationRequest(
            Guid.NewGuid(),
            NCEconomicReservationId.New(),
            serviceQuoteRequest.QuoteId,
            1,
            TimeSpan.FromMinutes(2),
            buyer,
            Guid.NewGuid(),
            AuditSource: "phase-nine-test");
        var serviceReserved = await assets.CreateReservationAsync(serviceReservationRequest);
        Assert.That(serviceReserved.Status, Is.EqualTo(NCEconomicAssetMutationStatus.Applied));

        var serviceFulfillmentId = NCEconomicFulfillmentId.New();
        var buyerAfterItem = settled.Ledger!.Accounts.Single(account => account.Snapshot.Owner == buyer);
        var sellerAfterItem = settled.Ledger.Accounts.Single(account => account.Snapshot.Owner == owner);
        var serviceSettlementRequest = new NCSettleEconomicReservationRequest(
            Guid.NewGuid(),
            NCTransactionId.New(),
            serviceReservationRequest.ReservationId,
            1,
            buyerAfterItem.Snapshot.AccountId,
            buyerAfterItem.AggregateVersion,
            sellerAfterItem.Snapshot.AccountId,
            sellerAfterItem.AggregateVersion,
            null,
            0,
            null,
            0,
            null,
            0,
            null,
            null,
            serviceFulfillmentId,
            buyer,
            Guid.NewGuid(),
            AuditSource: "phase-nine-test");
        var serviceSettled = await assets.SettleReservationAsync(serviceSettlementRequest);
        Assert.Multiple(() =>
        {
            Assert.That(serviceSettled.Status, Is.EqualTo(NCEconomicTradeMutationStatus.Applied));
            Assert.That(serviceSettled.Fulfillment?.Snapshot.Status,
                Is.EqualTo(NCEconomicFulfillmentStatus.Pending));
            Assert.That(serviceSettled.Fulfillment?.Snapshot.DefinitionId,
                Is.EqualTo("NCEconomicServiceCyberwareStreetMaintenance"));
            Assert.That(serviceSettled.Ledger?.Accounts.Single(account => account.Snapshot.Owner == buyer)
                .Snapshot.Balance, Is.EqualTo(384));
            Assert.That(serviceSettled.Ledger?.Accounts.Single(account => account.Snapshot.Owner == owner)
                .Snapshot.Balance, Is.EqualTo(616));
        });
        Assert.That((await assets.SettleReservationAsync(serviceSettlementRequest)).Status,
            Is.EqualTo(NCEconomicTradeMutationStatus.Duplicate));

        var informationQuoteRequest = serviceQuoteRequest with
        {
            CommandId = Guid.NewGuid(),
            QuoteId = NCEconomicQuoteId.New(),
            OfferKind = NCEconomicOfferKind.InformationAccess,
            OfferId = "NCInformationPharmaceuticalStockAvailability",
            CorrelationId = Guid.NewGuid(),
        };
        var informationQuote = await assets.CreateQuoteAsync(informationQuoteRequest);
        Assert.Multiple(() =>
        {
            Assert.That(informationQuote.Status, Is.EqualTo(NCEconomicAssetMutationStatus.Applied));
            Assert.That(informationQuote.Quote?.Snapshot.PriceContext.Legality, Is.Zero);
            Assert.That(informationQuote.Quote?.Snapshot.Price.Total, Is.EqualTo(25));
        });

        var informationReservationRequest = serviceReservationRequest with
        {
            CommandId = Guid.NewGuid(),
            ReservationId = NCEconomicReservationId.New(),
            QuoteId = informationQuoteRequest.QuoteId,
            CorrelationId = Guid.NewGuid(),
        };
        var informationReserved = await assets.CreateReservationAsync(informationReservationRequest);
        Assert.That(informationReserved.Status, Is.EqualTo(NCEconomicAssetMutationStatus.Applied));

        var informationFulfillmentId = NCEconomicFulfillmentId.New();
        var buyerAfterService = serviceSettled.Ledger!.Accounts.Single(account => account.Snapshot.Owner == buyer);
        var sellerAfterService = serviceSettled.Ledger.Accounts.Single(account => account.Snapshot.Owner == owner);
        var informationSettlementRequest = serviceSettlementRequest with
        {
            CommandId = Guid.NewGuid(),
            TransactionId = NCTransactionId.New(),
            ReservationId = informationReservationRequest.ReservationId,
            ExpectedBuyerAccountVersion = buyerAfterService.AggregateVersion,
            ExpectedSellerAccountVersion = sellerAfterService.AggregateVersion,
            FulfillmentId = informationFulfillmentId,
            CorrelationId = Guid.NewGuid(),
        };
        var informationSettled = await assets.SettleReservationAsync(informationSettlementRequest);
        Assert.Multiple(() =>
        {
            Assert.That(informationSettled.Status, Is.EqualTo(NCEconomicTradeMutationStatus.Applied));
            Assert.That(informationSettled.Fulfillment?.Snapshot.Status,
                Is.EqualTo(NCEconomicFulfillmentStatus.Granted));
            Assert.That(informationSettled.Fulfillment?.Snapshot.DefinitionId,
                Is.EqualTo("NCInformationPharmaceuticalStockAvailability"));
            Assert.That(informationSettled.Ledger?.Accounts.Single(account => account.Snapshot.Owner == buyer)
                .Snapshot.Balance, Is.EqualTo(359));
            Assert.That(informationSettled.Ledger?.Accounts.Single(account => account.Snapshot.Owner == owner)
                .Snapshot.Balance, Is.EqualTo(641));
        });
        Assert.That((await assets.SettleReservationAsync(informationSettlementRequest)).Status,
            Is.EqualTo(NCEconomicTradeMutationStatus.Duplicate));

        var completeServiceRequest = new NCCompleteEconomicFulfillmentRequest(
            Guid.NewGuid(),
            serviceFulfillmentId,
            1,
            NCEconomicFulfillmentStatus.Completed,
            owner,
            Guid.NewGuid(),
            AuditSource: "phase-nine-test");
        var serviceCompleted = await assets.CompleteFulfillmentAsync(completeServiceRequest);
        Assert.Multiple(() =>
        {
            Assert.That(serviceCompleted.Status, Is.EqualTo(NCEconomicAssetMutationStatus.Applied));
            Assert.That(serviceCompleted.Fulfillment?.Snapshot.Status,
                Is.EqualTo(NCEconomicFulfillmentStatus.Completed));
            Assert.That(serviceCompleted.Fulfillment?.AggregateVersion, Is.EqualTo(2));
        });
        Assert.That((await assets.CompleteFulfillmentAsync(completeServiceRequest)).Status,
            Is.EqualTo(NCEconomicAssetMutationStatus.Duplicate));

        var forbiddenAccessMutation = await assets.CompleteFulfillmentAsync(completeServiceRequest with
        {
            CommandId = Guid.NewGuid(),
            FulfillmentId = informationFulfillmentId,
            FinalStatus = NCEconomicFulfillmentStatus.Failed,
            CorrelationId = Guid.NewGuid(),
        });
        Assert.That(forbiddenAccessMutation.Status, Is.EqualTo(NCEconomicAssetMutationStatus.Rejected),
            "The service completion boundary must not mutate an information-access grant.");

        var itemRefundTransferId = NCEconomicTransferId.New();
        var itemRefundRequest = new NCRefundEconomicSettlementRequest(
            Guid.NewGuid(),
            NCTransactionId.New(),
            itemReservationRequest.ReservationId,
            2,
            3,
            informationSettled.Ledger!.Accounts.Select(account =>
                new NCEconomicRefundAccountVersion(account.Snapshot.AccountId, account.AggregateVersion)).ToArray(),
            3,
            2,
            7,
            itemRefundTransferId,
            0,
            owner,
            Guid.NewGuid(),
            AuditSource: "phase-nine-test");
        var itemRefunded = await assets.RefundSettlementAsync(itemRefundRequest);
        Assert.Multiple(() =>
        {
            Assert.That(itemRefunded.Status, Is.EqualTo(NCEconomicTradeMutationStatus.Applied));
            Assert.That(itemRefunded.Quote?.Snapshot.Status, Is.EqualTo(NCEconomicQuoteStatus.Refunded));
            Assert.That(itemRefunded.Reservation?.Snapshot.Status,
                Is.EqualTo(NCEconomicReservationStatus.Refunded));
            Assert.That(itemRefunded.Transfer?.Snapshot.AssetKind,
                Is.EqualTo(NCEconomicTransferAssetKind.ItemInstance));
            Assert.That(itemRefunded.Transfer?.Snapshot.PreviousOwner, Is.EqualTo(buyer));
            Assert.That(itemRefunded.Transfer?.Snapshot.NewOwner, Is.EqualTo(owner));
            Assert.That(itemRefunded.Item?.AggregateVersion, Is.EqualTo(4));
            Assert.That(itemRefunded.Item?.Snapshot.Owner, Is.EqualTo(owner));
            Assert.That(itemRefunded.SourceStockNode?.AggregateVersion, Is.EqualTo(3));
            Assert.That(itemRefunded.SourceStockNode?.Snapshot.ItemIds, Is.Empty);
            Assert.That(itemRefunded.DestinationStockNode?.AggregateVersion, Is.EqualTo(8));
            Assert.That(itemRefunded.DestinationStockNode?.Snapshot.ItemIds, Is.EqualTo(new[] { itemId }));
            Assert.That(itemRefunded.Ledger?.Accounts.Single(account => account.Snapshot.Owner == buyer)
                .Snapshot.Balance, Is.EqualTo(865));
            Assert.That(itemRefunded.Ledger?.Accounts.Single(account => account.Snapshot.Owner == owner)
                .Snapshot.Balance, Is.EqualTo(135));
        });
        Assert.That((await assets.RefundSettlementAsync(itemRefundRequest)).Status,
            Is.EqualTo(NCEconomicTradeMutationStatus.Duplicate));

        var completedServiceRefund = await assets.RefundSettlementAsync(new NCRefundEconomicSettlementRequest(
            Guid.NewGuid(),
            NCTransactionId.New(),
            serviceReservationRequest.ReservationId,
            2,
            3,
            itemRefunded.Ledger!.Accounts.Select(account =>
                new NCEconomicRefundAccountVersion(account.Snapshot.AccountId, account.AggregateVersion)).ToArray(),
            0,
            0,
            0,
            null,
            2,
            owner,
            Guid.NewGuid(),
            AuditSource: "phase-nine-test"));
        Assert.That(completedServiceRefund.Status, Is.EqualTo(NCEconomicTradeMutationStatus.Rejected),
            "A completed service must not be refundable through the unconsumed-offer boundary.");

        var informationRefundRequest = new NCRefundEconomicSettlementRequest(
            Guid.NewGuid(),
            NCTransactionId.New(),
            informationReservationRequest.ReservationId,
            2,
            3,
            itemRefunded.Ledger.Accounts.Select(account =>
                new NCEconomicRefundAccountVersion(account.Snapshot.AccountId, account.AggregateVersion)).ToArray(),
            0,
            0,
            0,
            null,
            1,
            owner,
            Guid.NewGuid(),
            AuditSource: "phase-nine-test");
        var informationRefunded = await assets.RefundSettlementAsync(informationRefundRequest);
        Assert.Multiple(() =>
        {
            Assert.That(informationRefunded.Status, Is.EqualTo(NCEconomicTradeMutationStatus.Applied));
            Assert.That(informationRefunded.Fulfillment?.Snapshot.Status,
                Is.EqualTo(NCEconomicFulfillmentStatus.Revoked));
            Assert.That(informationRefunded.Fulfillment?.Snapshot.RefundTransactionId,
                Is.EqualTo(informationRefundRequest.RefundTransactionId));
            Assert.That(informationRefunded.Ledger?.Accounts.Single(account => account.Snapshot.Owner == buyer)
                .Snapshot.Balance, Is.EqualTo(890));
            Assert.That(informationRefunded.Ledger?.Accounts.Single(account => account.Snapshot.Owner == owner)
                .Snapshot.Balance, Is.EqualTo(110));
        });
        Assert.That((await assets.RefundSettlementAsync(informationRefundRequest)).Status,
            Is.EqualTo(NCEconomicTradeMutationStatus.Duplicate));

        var splitBatchId = NCCommodityBatchId.New();
        var partialTransfer = new NCTransferCommodityRequest(
            Guid.NewGuid(),
            NCEconomicTransferId.New(),
            batchId,
            splitBatchId,
            75,
            3,
            stockNodeId,
            8,
            destinationNodeId,
            3,
            owner,
            buyer,
            owner,
            Guid.NewGuid(),
            AuditSource: "phase-nine-test");
        var partiallyTransferred = await assets.TransferCommodityAsync(partialTransfer);
        Assert.Multiple(() =>
        {
            Assert.That(partiallyTransferred.Status, Is.EqualTo(NCEconomicAssetMutationStatus.Applied));
            Assert.That(partiallyTransferred.SourceCommodityBatch?.Snapshot.Quantity, Is.EqualTo(175));
            Assert.That(partiallyTransferred.SourceCommodityBatch?.Snapshot.Owner, Is.EqualTo(owner));
            Assert.That(partiallyTransferred.DestinationCommodityBatch?.Snapshot.BatchId, Is.EqualTo(splitBatchId));
            Assert.That(partiallyTransferred.DestinationCommodityBatch?.Snapshot.Quantity, Is.EqualTo(75));
            Assert.That(partiallyTransferred.DestinationCommodityBatch?.Snapshot.Owner, Is.EqualTo(buyer));
            Assert.That(partiallyTransferred.SourceStockNode?.AggregateVersion, Is.EqualTo(9));
            Assert.That(partiallyTransferred.DestinationStockNode?.AggregateVersion, Is.EqualTo(4));
        });
        Assert.That((await assets.TransferCommodityAsync(partialTransfer)).Status,
            Is.EqualTo(NCEconomicAssetMutationStatus.Duplicate));

        var rejectedTransferId = NCEconomicTransferId.New();
        var staleTransfer = partialTransfer with
        {
            CommandId = Guid.NewGuid(),
            TransferId = rejectedTransferId,
            SplitBatchId = NCCommodityBatchId.New(),
            Quantity = 25,
            ExpectedSourceStockNodeVersion = 9,
            ExpectedDestinationStockNodeVersion = 4,
        };
        Assert.That((await assets.TransferCommodityAsync(staleTransfer)).Status,
            Is.EqualTo(NCEconomicAssetMutationStatus.VersionConflict));
        Assert.That(await assets.GetTransferAsync(rejectedTransferId, true), Is.Null,
            "A stale transfer must not leave a transfer or split-batch aggregate.");

        var wholeTransfer = partialTransfer with
        {
            CommandId = Guid.NewGuid(),
            TransferId = NCEconomicTransferId.New(),
            SplitBatchId = null,
            Quantity = 175,
            ExpectedSourceBatchVersion = 4,
            ExpectedSourceStockNodeVersion = 9,
            ExpectedDestinationStockNodeVersion = 4,
        };
        var whollyTransferred = await assets.TransferCommodityAsync(wholeTransfer);
        Assert.Multiple(() =>
        {
            Assert.That(whollyTransferred.Status, Is.EqualTo(NCEconomicAssetMutationStatus.Applied));
            Assert.That(whollyTransferred.SourceCommodityBatch?.AggregateVersion, Is.EqualTo(5));
            Assert.That(whollyTransferred.SourceCommodityBatch?.Snapshot.Owner, Is.EqualTo(buyer));
            Assert.That(whollyTransferred.SourceStockNode?.Snapshot.CommodityBatchIds, Is.Empty);
            Assert.That(whollyTransferred.DestinationStockNode?.AggregateVersion, Is.EqualTo(5));
            Assert.That(whollyTransferred.DestinationStockNode?.Snapshot.CommodityBatchIds,
                Is.EqualTo(new[] { batchId, splitBatchId }.OrderBy(id => id.Value).ToArray()));
        });

        Assert.That(assets.Unload(stockNodeId), Is.True);
        Assert.That(assets.Unload(destinationNodeId), Is.True);
        Assert.That(assets.Unload(batchId), Is.True);
        Assert.That(assets.Unload(splitBatchId), Is.True);
        Assert.That(assets.Unload(itemId), Is.True);
        Assert.That(assets.Unload(itemRefundTransferId), Is.True);
        Assert.That(assets.Unload(purchaseTransferId), Is.True);
        Assert.That(assets.Unload(partialTransfer.TransferId), Is.True);
        Assert.That(assets.Unload(wholeTransfer.TransferId), Is.True);
        Assert.That(assets.Unload(quoteRequest.QuoteId), Is.True);
        Assert.That(assets.Unload(reservationRequest.ReservationId), Is.True);
        Assert.That(assets.Unload(itemQuoteRequest.QuoteId), Is.True);
        Assert.That(assets.Unload(itemReservationRequest.ReservationId), Is.True);
        Assert.That(assets.Unload(serviceQuoteRequest.QuoteId), Is.True);
        Assert.That(assets.Unload(serviceReservationRequest.ReservationId), Is.True);
        Assert.That(assets.Unload(serviceFulfillmentId), Is.True);
        Assert.That(assets.Unload(informationQuoteRequest.QuoteId), Is.True);
        Assert.That(assets.Unload(informationReservationRequest.ReservationId), Is.True);
        Assert.That(assets.Unload(informationFulfillmentId), Is.True);
        var restoredNode = await assets.GetStockNodeAsync(stockNodeId, true);
        var restoredDestination = await assets.GetStockNodeAsync(destinationNodeId, true);
        var restoredBatch = await assets.GetCommodityBatchAsync(batchId, true);
        var restoredSplitBatch = await assets.GetCommodityBatchAsync(splitBatchId, true);
        var restoredItem = await assets.GetItemAsync(itemId, true);
        var restoredQuote = await assets.GetQuoteAsync(quoteRequest.QuoteId, true);
        var restoredReservation = await assets.GetReservationAsync(reservationRequest.ReservationId, true);
        var restoredServiceFulfillment = await assets.GetFulfillmentAsync(serviceFulfillmentId, true);
        var restoredInformationFulfillment = await assets.GetFulfillmentAsync(informationFulfillmentId, true);
        var restoredItemQuote = await assets.GetQuoteAsync(itemQuoteRequest.QuoteId, true);
        var restoredItemReservation = await assets.GetReservationAsync(itemReservationRequest.ReservationId, true);
        Assert.Multiple(() =>
        {
            Assert.That(restoredNode?.AggregateVersion, Is.EqualTo(10));
            Assert.That(restoredNode?.Snapshot.CommodityBatchIds, Is.Empty);
            Assert.That(restoredNode?.Snapshot.ItemIds, Is.EqualTo(new[] { itemId }));
            Assert.That(restoredDestination?.AggregateVersion, Is.EqualTo(5));
            Assert.That(restoredBatch?.Snapshot.Provenance.SourceWorldEventIds, Is.EqualTo(new[] { sourceEvent }));
            Assert.That(restoredBatch?.Snapshot.Owner, Is.EqualTo(buyer));
            Assert.That(restoredSplitBatch?.Snapshot.Quantity, Is.EqualTo(75));
            Assert.That(restoredItem?.Snapshot.Provenance.SourceWorldEventIds, Is.EqualTo(new[] { itemEvent }));
            Assert.That(restoredItem?.Snapshot.Owner, Is.EqualTo(owner));
            Assert.That(restoredQuote?.Snapshot.Status, Is.EqualTo(NCEconomicQuoteStatus.Cancelled));
            Assert.That(restoredReservation?.Snapshot.Status, Is.EqualTo(NCEconomicReservationStatus.Released));
            Assert.That(restoredServiceFulfillment?.Snapshot.Status,
                Is.EqualTo(NCEconomicFulfillmentStatus.Completed));
            Assert.That(restoredInformationFulfillment?.Snapshot.Status,
                Is.EqualTo(NCEconomicFulfillmentStatus.Revoked));
            Assert.That(restoredItemQuote?.Snapshot.Status, Is.EqualTo(NCEconomicQuoteStatus.Refunded));
            Assert.That(restoredItemReservation?.Snapshot.Status,
                Is.EqualTo(NCEconomicReservationStatus.Refunded));
        });

        var audit = await reconciliation.AuditAsync(new NCEconomicReconciliationCursor(null, null, null));
        Assert.Multiple(() =>
        {
            Assert.That(audit.StockNodesScanned, Is.GreaterThanOrEqualTo(2));
            Assert.That(audit.CommodityBatchesScanned, Is.GreaterThanOrEqualTo(2));
            Assert.That(audit.ItemsScanned, Is.GreaterThanOrEqualTo(1));
            Assert.That(audit.ReferenceChecksTruncated, Is.False);
            Assert.That(audit.Issues, Is.Empty,
                string.Join(Environment.NewLine, audit.Issues.Select(issue =>
                    $"{issue.Code}: {issue.AggregateType}/{issue.AggregateId} -> {issue.RelatedId}")));
        });

        await server.WaitPost(() => server.EntMan.EventBus.RaiseEvent(
            EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
        await WaitUntil(server, () => !clock.IsReady, "The active-world clock did not stop.");
        var frozen = await assets.CreateStockNodeAsync(createNode with
        {
            CommandId = Guid.NewGuid(),
            StockNodeId = NCStockNodeId.New(),
            StableKey = "frozen-node",
        });
        Assert.That(frozen.Status, Is.EqualTo(NCEconomicAssetMutationStatus.ClockUnavailable));
        Assert.That((await assets.RefundSettlementAsync(itemRefundRequest with
        {
            CommandId = Guid.NewGuid(),
            RefundTransactionId = NCTransactionId.New(),
            RefundTransferId = NCEconomicTransferId.New(),
        })).Status, Is.EqualTo(NCEconomicTradeMutationStatus.ClockUnavailable));
    }

    private static async Task<NCCharacterId> CreateCharacterAsync(IServerDbManager database)
    {
        var preference = await database.InitPrefsAsync(
            new NetUserId(Guid.NewGuid()),
            new HumanoidCharacterProfile(),
            CancellationToken.None);
        return new NCCharacterId(preference.Profiles.Single().Id);
    }

    private static async Task WaitUntil(
        RobustIntegrationTest.ServerIntegrationInstance server,
        Func<bool> predicate,
        string failure,
        int attempts = 180)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            await server.WaitRunTicks(1);
            var complete = false;
            await server.WaitPost(() => complete = predicate());
            if (complete)
                return;
        }
        Assert.Fail(failure);
    }
}
