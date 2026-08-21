// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Economy;

/// <summary>
/// Reverses an unconsumed purchase as one ledger transaction. Refund callers supply only optimistic versions and
/// identities; exact monetary legs are derived from the immutable original purchase transaction.
/// </summary>
public sealed partial class NCEconomicAssetRegistrySystem
{
    public async Task<NCEconomicTradeMutationResult> RefundSettlementAsync(
        NCRefundEconomicSettlementRequest request)
    {
        if (!Valid(request) || !TryGetPolicy(out var policy) ||
            !_clock.TryGetCurrentTime(out _, out var now))
        {
            return RejectTrade(_clock.IsReady
                ? NCEconomicTradeMutationStatus.Rejected
                : NCEconomicTradeMutationStatus.ClockUnavailable,
                "The refund request, asset policy, or active-world clock is invalid.");
        }

        try
        {
            var initialReservation = await GetReservationAsync(request.ReservationId, true);
            if (initialReservation == null)
                return RejectTrade(NCEconomicTradeMutationStatus.NotFound, "The reservation does not exist.");
            var initialQuote = await GetQuoteAsync(initialReservation.Snapshot.QuoteId, true);
            if (initialQuote?.Snapshot.SettlementTransactionId is not { } purchaseTransactionId)
                return RejectTrade(NCEconomicTradeMutationStatus.Rejected,
                    "The reservation does not reference a settled purchase.");
            if (!await ResolveParties(initialQuote.Snapshot.Seller, initialQuote.Snapshot.Buyer, request.Initiator))
                return RejectTrade(NCEconomicTradeMutationStatus.PartyUnavailable,
                    "The refund parties or initiator are unavailable.");
            if (request.Initiator != initialQuote.Snapshot.Seller)
                return RejectTrade(NCEconomicTradeMutationStatus.Rejected,
                    "Only the seller or service provider can approve a refund.");

            var purchase = await _ledger.GetTransactionAsync(purchaseTransactionId, true);
            if (!ValidPurchaseForRefund(purchase?.Snapshot, initialQuote.Snapshot))
                return RejectTrade(NCEconomicTradeMutationStatus.StorageFailure,
                    "The immutable purchase transaction does not match the settled quote.");
            if (!TryBuildRefundLegs(request, purchase!.Snapshot, out var refundLegs, out var legError))
                return RejectTrade(NCEconomicTradeMutationStatus.Rejected, legError!);

            if (initialReservation.Snapshot.Status == NCEconomicReservationStatus.Refunded)
            {
                var replay = await _ledger.PostTransactionAsync(
                    RefundLedgerRequest(request, initialQuote.Snapshot, refundLegs!, []));
                if (replay.Status != NCEconomyMutationStatus.Duplicate ||
                    !SameRefund(replay.Transaction?.Snapshot, request, initialQuote.Snapshot))
                {
                    return RejectTrade(Map(replay.Status), replay.Error ??
                        "The refunded reservation does not match this command replay.");
                }
                return await LoadRefundResult(request, replay, NCEconomicTradeMutationStatus.Duplicate);
            }

            var initialSale = initialQuote.Snapshot.SettlementTransferId is { } initialSaleId
                ? await GetTransferAsync(initialSaleId, true)
                : null;
            if (initialQuote.Snapshot.SettlementTransferId.HasValue && initialSale == null)
                return RejectTrade(NCEconomicTradeMutationStatus.StorageFailure,
                    "The settled quote is missing its purchase transfer.");
            var gates = await AcquireGates(RefundGateKeys(request, initialQuote.Snapshot, initialSale));
            try
            {
                var reservation = await GetReservationAsync(request.ReservationId, true);
                var quote = reservation == null ? null : await GetQuoteAsync(reservation.Snapshot.QuoteId, true);
                if (reservation == null || quote == null)
                    return RejectTrade(NCEconomicTradeMutationStatus.StorageFailure,
                        "The refund reservation or quote disappeared while acquiring the mutation boundary.");
                if (reservation.AggregateVersion != request.ExpectedReservationVersion ||
                    quote.AggregateVersion != request.ExpectedQuoteVersion)
                {
                    return RejectTrade(NCEconomicTradeMutationStatus.VersionConflict,
                        "The refund reservation or quote version changed.");
                }
                if (reservation.Snapshot.Status != NCEconomicReservationStatus.Settled ||
                    quote.Snapshot.Status != NCEconomicQuoteStatus.Settled ||
                    reservation.Snapshot.SettlementTransactionId != purchaseTransactionId ||
                    quote.Snapshot.SettlementTransactionId != purchaseTransactionId)
                {
                    return RejectTrade(NCEconomicTradeMutationStatus.Rejected,
                        "Only one currently settled purchase can be refunded.");
                }

                var writes = new List<NCPersistentAggregateWrite>();
                var assetResult = await BuildRefundParticipantWrites(request, quote, reservation, policy, now, writes);
                if (assetResult.Error != null)
                    return RejectTrade(assetResult.Status, assetResult.Error);

                var reservationEventId = Guid.NewGuid();
                var nextReservation = reservation.Snapshot with
                {
                    Status = NCEconomicReservationStatus.Refunded,
                    RefundTransactionId = request.RefundTransactionId,
                    DataVersion = checked(reservation.Snapshot.DataVersion + 1),
                    LastAppliedEventId = reservationEventId,
                };
                var quoteEventId = Guid.NewGuid();
                var nextQuote = quote.Snapshot with
                {
                    Status = NCEconomicQuoteStatus.Refunded,
                    RefundTransactionId = request.RefundTransactionId,
                    DataVersion = checked(quote.Snapshot.DataVersion + 1),
                    LastAppliedEventId = quoteEventId,
                };
                writes.Add(ReservationWrite(reservation, nextReservation, reservationEventId,
                    "living-city.economic-reservation.refunded", request.AuditSource));
                writes.Add(QuoteWrite(quote, nextQuote, quoteEventId,
                    "living-city.economic-quote.refunded", request.AuditSource));

                var ledger = await _ledger.PostTransactionAsync(RefundLedgerRequest(
                    request,
                    quote.Snapshot,
                    refundLegs!,
                    writes.Select(ToParticipant).ToArray()));
                if (ledger.Status is not (NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate))
                    return RejectTrade(Map(ledger.Status), ledger.Error ?? "The atomic refund was not committed.", ledger);
                return await LoadRefundResult(request, ledger,
                    ledger.Status == NCEconomyMutationStatus.Applied
                        ? NCEconomicTradeMutationStatus.Applied
                        : NCEconomicTradeMutationStatus.Duplicate);
            }
            finally
            {
                ReleaseGates(gates);
            }
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to refund economic settlement {request.ReservationId}: {exception}");
            return RejectTrade(NCEconomicTradeMutationStatus.StorageFailure, exception.Message);
        }
    }

    private async Task<RefundParticipantBuild> BuildRefundParticipantWrites(
        NCRefundEconomicSettlementRequest request,
        NCEconomicQuoteRecord quote,
        NCEconomicReservationRecord reservation,
        NCEconomicAssetPolicyPrototype policy,
        NCActiveWorldTime now,
        List<NCPersistentAggregateWrite> writes)
    {
        if (quote.Snapshot.OfferKind is NCEconomicOfferKind.Service or NCEconomicOfferKind.InformationAccess)
        {
            if (request.RefundTransferId.HasValue || request.ExpectedPurchasedAssetVersion != 0 ||
                request.ExpectedBuyerStockNodeVersion != 0 || request.ExpectedSellerStockNodeVersion != 0 ||
                request.ExpectedFulfillmentVersion <= 0 || quote.Snapshot.FulfillmentId is not { } fulfillmentId)
            {
                return RefundParticipantBuild.Reject(
                    "A service/access refund requires only the current fulfillment version.");
            }
            var fulfillment = await GetFulfillmentAsync(fulfillmentId, true);
            if (fulfillment == null)
                return RefundParticipantBuild.NotFound("The purchased fulfillment does not exist.");
            if (fulfillment.AggregateVersion != request.ExpectedFulfillmentVersion)
                return RefundParticipantBuild.Conflict("The fulfillment version changed.");
            if (fulfillment.Snapshot.QuoteId != quote.Snapshot.QuoteId ||
                fulfillment.Snapshot.Provider != quote.Snapshot.Seller ||
                fulfillment.Snapshot.Recipient != quote.Snapshot.Buyer ||
                fulfillment.Snapshot.RefundTransactionId.HasValue ||
                quote.Snapshot.OfferKind == NCEconomicOfferKind.Service &&
                fulfillment.Snapshot.Status is not (NCEconomicFulfillmentStatus.Pending or
                    NCEconomicFulfillmentStatus.Failed) ||
                quote.Snapshot.OfferKind == NCEconomicOfferKind.InformationAccess &&
                fulfillment.Snapshot.Status != NCEconomicFulfillmentStatus.Granted)
            {
                return RefundParticipantBuild.Reject(
                    "The service was completed or the access/fulfillment is not refundable.");
            }

            var fulfillmentEventId = Guid.NewGuid();
            var nextFulfillment = fulfillment.Snapshot with
            {
                Status = NCEconomicFulfillmentStatus.Revoked,
                RefundTransactionId = request.RefundTransactionId,
                UpdatedAt = now,
                DataVersion = checked(fulfillment.Snapshot.DataVersion + 1),
                LastAppliedEventId = fulfillmentEventId,
            };
            writes.Add(FulfillmentWrite(fulfillment, nextFulfillment, fulfillmentEventId,
                "living-city.economic-fulfillment.refunded", request.AuditSource));
            return RefundParticipantBuild.Success(null, nextFulfillment);
        }

        if (request.RefundTransferId is not { } refundTransferId || request.ExpectedFulfillmentVersion != 0 ||
            request.ExpectedPurchasedAssetVersion <= 0 || request.ExpectedBuyerStockNodeVersion <= 0 ||
            request.ExpectedSellerStockNodeVersion <= 0 || quote.Snapshot.SettlementTransferId is not { } saleTransferId)
        {
            return RefundParticipantBuild.Reject(
                "An asset refund requires current purchased-asset and stock-node versions plus a transfer ID.");
        }
        if (await GetTransferAsync(refundTransferId, true) != null)
            return RefundParticipantBuild.Reject("The refund transfer identity already exists.");
        var sale = await GetTransferAsync(saleTransferId, true);
        if (sale == null)
            return RefundParticipantBuild.NotFound("The purchase transfer does not exist.");
        if (sale.Snapshot.QuoteId != quote.Snapshot.QuoteId ||
            sale.Snapshot.ReservationId != reservation.Snapshot.ReservationId ||
            sale.Snapshot.PreviousOwner != quote.Snapshot.Seller || sale.Snapshot.NewOwner != quote.Snapshot.Buyer ||
            sale.Snapshot.Quantity != quote.Snapshot.Quantity)
        {
            return RefundParticipantBuild.Reject("The purchase transfer does not match the settled quote.");
        }

        var buyerNode = await GetStockNodeAsync(sale.Snapshot.DestinationStockNodeId, true);
        var sellerNode = await GetStockNodeAsync(sale.Snapshot.SourceStockNodeId, true);
        if (buyerNode == null || sellerNode == null)
            return RefundParticipantBuild.NotFound("The buyer or seller stock node does not exist.");
        if (_materializationLeases.IsLeased(buyerNode.Snapshot.StockNodeId) ||
            _materializationLeases.IsLeased(sellerNode.Snapshot.StockNodeId))
        {
            return RefundParticipantBuild.Reject("Materialized stock requires a physical refund interaction.");
        }
        if (buyerNode.AggregateVersion != request.ExpectedBuyerStockNodeVersion ||
            sellerNode.AggregateVersion != request.ExpectedSellerStockNodeVersion)
        {
            return RefundParticipantBuild.Conflict("The buyer or seller stock-node version changed.");
        }
        if (buyerNode.Snapshot.Owner != quote.Snapshot.Buyer || sellerNode.Snapshot.Owner != quote.Snapshot.Seller ||
            !buyerNode.Snapshot.AllowsWithdrawals || !sellerNode.Snapshot.AcceptsDeposits)
        {
            return RefundParticipantBuild.Reject("The refund stock ownership or deposit policy is invalid.");
        }

        return sale.Snapshot.AssetKind == NCEconomicTransferAssetKind.ItemInstance
            ? await BuildItemRefundWrites(request, quote, reservation, policy, now, sale, buyerNode, sellerNode, writes)
            : await BuildCommodityRefundWrites(
                request, quote, reservation, policy, now, sale, buyerNode, sellerNode, writes);
    }

    private async Task<RefundParticipantBuild> BuildItemRefundWrites(
        NCRefundEconomicSettlementRequest request,
        NCEconomicQuoteRecord quote,
        NCEconomicReservationRecord reservation,
        NCEconomicAssetPolicyPrototype policy,
        NCActiveWorldTime now,
        NCEconomicAssetTransferRecord sale,
        NCStockNodeRecord buyerNode,
        NCStockNodeRecord sellerNode,
        List<NCPersistentAggregateWrite> writes)
    {
        if (!Guid.TryParse(sale.Snapshot.DestinationAssetId, out var parsedItemId))
            return RefundParticipantBuild.Reject("The purchased item identity is malformed.");
        var itemId = new NCItemId(parsedItemId);
        var item = await GetItemAsync(itemId, true);
        if (item == null)
            return RefundParticipantBuild.NotFound("The purchased item does not exist.");
        if (item.AggregateVersion != request.ExpectedPurchasedAssetVersion)
            return RefundParticipantBuild.Conflict("The purchased item version changed.");
        if (item.Snapshot.Owner != quote.Snapshot.Buyer || item.Snapshot.Location != StockLocation(
                buyerNode.Snapshot.StockNodeId) || item.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active ||
            item.Snapshot.ReservationId.HasValue || !buyerNode.Snapshot.ItemIds.Contains(itemId) ||
            sellerNode.Snapshot.ItemIds.Contains(itemId))
        {
            return RefundParticipantBuild.Reject("The purchased item was used, moved, reserved, or destroyed.");
        }
        if (sellerNode.Snapshot.ItemIds.Count >= policy.MaximumItemsPerStockNode)
            return RefundParticipantBuild.Reject("The seller item capacity is exhausted.");
        var installedParts = await LoadAssemblyPartsAsync(item);
        if (installedParts == null)
            return RefundParticipantBuild.Reject(
                "The purchased item assembly contains an inconsistent installed part.");

        var itemEventId = Guid.NewGuid();
        var nextItem = item.Snapshot with
        {
            Owner = quote.Snapshot.Seller,
            Location = StockLocation(sellerNode.Snapshot.StockNodeId),
            UpdatedAt = now,
            DataVersion = checked(item.Snapshot.DataVersion + 1),
            LastAppliedEventId = itemEventId,
        };
        var buyerEventId = Guid.NewGuid();
        var nextBuyer = buyerNode.Snapshot with
        {
            ItemIds = buyerNode.Snapshot.ItemIds.Where(id => id != itemId).ToArray(),
            UpdatedAt = now,
            DataVersion = checked(buyerNode.Snapshot.DataVersion + 1),
            LastAppliedEventId = buyerEventId,
        };
        var sellerEventId = Guid.NewGuid();
        var nextSeller = sellerNode.Snapshot with
        {
            ItemIds = sellerNode.Snapshot.ItemIds.Append(itemId).OrderBy(id => id.Value).ToArray(),
            UpdatedAt = now,
            DataVersion = checked(sellerNode.Snapshot.DataVersion + 1),
            LastAppliedEventId = sellerEventId,
        };
        var transferEventId = Guid.NewGuid();
        var transfer = RefundTransfer(request, quote, reservation, sale, itemId.Value, now, transferEventId);
        writes.Add(ItemMutationWrite(item, nextItem, itemEventId,
            "living-city.item-instance.refunded", request.AuditSource));
        foreach (var part in installedParts)
        {
            var partEventId = Guid.NewGuid();
            var nextPart = part.Snapshot with
            {
                Owner = quote.Snapshot.Seller,
                UpdatedAt = now,
                DataVersion = checked(part.Snapshot.DataVersion + 1),
                LastAppliedEventId = partEventId,
            };
            writes.Add(ItemMutationWrite(part, nextPart, partEventId,
                "living-city.item-instance.refunded-with-assembly", request.AuditSource));
        }
        writes.Add(StockNodeWrite(buyerNode, nextBuyer, buyerEventId,
            "living-city.stock-node.refund-withdrawn", request.AuditSource));
        writes.Add(StockNodeWrite(sellerNode, nextSeller, sellerEventId,
            "living-city.stock-node.refund-returned", request.AuditSource));
        writes.Add(TransferWrite(transfer, transferEventId, request.AuditSource));
        return RefundParticipantBuild.Success(transfer, null);
    }

    private async Task<RefundParticipantBuild> BuildCommodityRefundWrites(
        NCRefundEconomicSettlementRequest request,
        NCEconomicQuoteRecord quote,
        NCEconomicReservationRecord reservation,
        NCEconomicAssetPolicyPrototype policy,
        NCActiveWorldTime now,
        NCEconomicAssetTransferRecord sale,
        NCStockNodeRecord buyerNode,
        NCStockNodeRecord sellerNode,
        List<NCPersistentAggregateWrite> writes)
    {
        if (!Guid.TryParse(sale.Snapshot.DestinationAssetId, out var parsedBatchId))
            return RefundParticipantBuild.Reject("The purchased batch identity is malformed.");
        var batchId = new NCCommodityBatchId(parsedBatchId);
        var batch = await GetCommodityBatchAsync(batchId, true);
        if (batch == null)
            return RefundParticipantBuild.NotFound("The purchased commodity batch does not exist.");
        if (batch.AggregateVersion != request.ExpectedPurchasedAssetVersion)
            return RefundParticipantBuild.Conflict("The purchased commodity batch version changed.");
        if (batch.Snapshot.Owner != quote.Snapshot.Buyer || batch.Snapshot.Location != StockLocation(
                buyerNode.Snapshot.StockNodeId) || batch.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active ||
            batch.Snapshot.ReservedQuantity != 0 || batch.Snapshot.Quantity != quote.Snapshot.Quantity ||
            !buyerNode.Snapshot.CommodityBatchIds.Contains(batchId) ||
            sellerNode.Snapshot.CommodityBatchIds.Contains(batchId))
        {
            return RefundParticipantBuild.Reject("The purchased commodity was consumed, split, moved, or reserved.");
        }
        if (sellerNode.Snapshot.CommodityBatchIds.Count >= policy.MaximumBatchesPerStockNode)
            return RefundParticipantBuild.Reject("The seller commodity capacity is exhausted.");

        var batchEventId = Guid.NewGuid();
        var nextBatch = batch.Snapshot with
        {
            Owner = quote.Snapshot.Seller,
            Location = StockLocation(sellerNode.Snapshot.StockNodeId),
            UpdatedAt = now,
            DataVersion = checked(batch.Snapshot.DataVersion + 1),
            LastAppliedEventId = batchEventId,
        };
        var buyerEventId = Guid.NewGuid();
        var nextBuyer = buyerNode.Snapshot with
        {
            CommodityBatchIds = buyerNode.Snapshot.CommodityBatchIds.Where(id => id != batchId).ToArray(),
            UpdatedAt = now,
            DataVersion = checked(buyerNode.Snapshot.DataVersion + 1),
            LastAppliedEventId = buyerEventId,
        };
        var sellerEventId = Guid.NewGuid();
        var nextSeller = sellerNode.Snapshot with
        {
            CommodityBatchIds = sellerNode.Snapshot.CommodityBatchIds.Append(batchId)
                .OrderBy(id => id.Value).ToArray(),
            UpdatedAt = now,
            DataVersion = checked(sellerNode.Snapshot.DataVersion + 1),
            LastAppliedEventId = sellerEventId,
        };
        var transferEventId = Guid.NewGuid();
        var transfer = RefundTransfer(request, quote, reservation, sale, batchId.Value, now, transferEventId);
        writes.Add(CommodityMutationWrite(batch, nextBatch, batchEventId,
            "living-city.commodity-batch.refunded", request.AuditSource));
        writes.Add(StockNodeWrite(buyerNode, nextBuyer, buyerEventId,
            "living-city.stock-node.refund-withdrawn", request.AuditSource));
        writes.Add(StockNodeWrite(sellerNode, nextSeller, sellerEventId,
            "living-city.stock-node.refund-returned", request.AuditSource));
        writes.Add(TransferWrite(transfer, transferEventId, request.AuditSource));
        return RefundParticipantBuild.Success(transfer, null);
    }

    private static NCEconomicAssetTransferSnapshot RefundTransfer(
        NCRefundEconomicSettlementRequest request,
        NCEconomicQuoteRecord quote,
        NCEconomicReservationRecord reservation,
        NCEconomicAssetTransferRecord sale,
        Guid assetId,
        NCActiveWorldTime now,
        Guid eventId) => new(
        request.RefundTransferId!.Value,
        quote.Snapshot.QuoteId,
        reservation.Snapshot.ReservationId,
        sale.Snapshot.AssetKind,
        assetId.ToString("D"),
        assetId.ToString("D"),
        quote.Snapshot.Quantity,
        sale.Snapshot.DestinationStockNodeId,
        sale.Snapshot.SourceStockNodeId,
        quote.Snapshot.Buyer,
        quote.Snapshot.Seller,
        request.Initiator,
        now,
        1,
        eventId);

    private static bool TryBuildRefundLegs(
        NCRefundEconomicSettlementRequest request,
        NCLedgerTransactionSnapshot purchase,
        out NCLedgerLegRequest[]? legs,
        out string? error)
    {
        legs = null;
        error = null;
        if (request.AccountVersions.Count != purchase.Legs.Length ||
            request.AccountVersions.Any(value => !value.AccountId.IsValid || value.ExpectedVersion <= 0) ||
            request.AccountVersions.Select(value => value.AccountId).Distinct().Count() !=
            request.AccountVersions.Count)
        {
            error = "Refund account versions must name every purchase account exactly once.";
            return false;
        }
        var versions = request.AccountVersions.ToDictionary(value => value.AccountId, value => value.ExpectedVersion);
        if (purchase.Legs.Any(leg => !versions.ContainsKey(leg.AccountId)))
        {
            error = "Refund account membership differs from the immutable purchase transaction.";
            return false;
        }
        legs = purchase.Legs
            .Select(leg => new NCLedgerLegRequest(leg.AccountId, versions[leg.AccountId], checked(-leg.Amount)))
            .OrderBy(leg => leg.AccountId.Value)
            .ToArray();
        return true;
    }

    private static NCPostLedgerTransactionRequest RefundLedgerRequest(
        NCRefundEconomicSettlementRequest request,
        NCEconomicQuoteSnapshot quote,
        NCLedgerLegRequest[] legs,
        IReadOnlyList<NCAtomicLedgerParticipantWrite> participants) => new(
        request.CommandId,
        request.RefundTransactionId,
        NCLedgerTransactionKind.Refund,
        RefundReference(quote),
        legs,
        request.Initiator,
        request.CorrelationId,
        request.CausationId,
        request.AuditSource,
        participants);

    private async Task<NCEconomicTradeMutationResult> LoadRefundResult(
        NCRefundEconomicSettlementRequest request,
        NCEconomyMutationResult ledger,
        NCEconomicTradeMutationStatus status)
    {
        var reservation = await GetReservationAsync(request.ReservationId, true);
        var quote = reservation == null ? null : await GetQuoteAsync(reservation.Snapshot.QuoteId, true);
        if (reservation == null || quote == null || reservation.Snapshot.Status != NCEconomicReservationStatus.Refunded ||
            quote.Snapshot.Status != NCEconomicQuoteStatus.Refunded ||
            reservation.Snapshot.RefundTransactionId != request.RefundTransactionId ||
            quote.Snapshot.RefundTransactionId != request.RefundTransactionId)
        {
            return RejectTrade(NCEconomicTradeMutationStatus.StorageFailure,
                "The committed refund projections are incomplete.", ledger);
        }

        NCEconomicAssetTransferRecord? transfer = null;
        NCEconomicFulfillmentRecord? fulfillment = null;
        NCStockNodeRecord? source = null;
        NCStockNodeRecord? destination = null;
        NCCommodityBatchRecord? batch = null;
        NCItemInstanceRecord? item = null;
        if (request.RefundTransferId is { } transferId)
        {
            transfer = await GetTransferAsync(transferId, true);
            if (transfer != null)
            {
                source = await GetStockNodeAsync(transfer.Snapshot.SourceStockNodeId, true);
                destination = await GetStockNodeAsync(transfer.Snapshot.DestinationStockNodeId, true);
                if (transfer.Snapshot.AssetKind == NCEconomicTransferAssetKind.ItemInstance)
                    item = await GetItemAsync(new NCItemId(Guid.Parse(transfer.Snapshot.DestinationAssetId)), true);
                else
                    batch = await GetCommodityBatchAsync(
                        new NCCommodityBatchId(Guid.Parse(transfer.Snapshot.DestinationAssetId)), true);
            }
            if (transfer == null || source == null || destination == null || item == null && batch == null)
                return RejectTrade(NCEconomicTradeMutationStatus.StorageFailure,
                    "The committed refund transfer is incomplete.", ledger);
        }
        else if (quote.Snapshot.FulfillmentId is { } fulfillmentId)
        {
            fulfillment = await GetFulfillmentAsync(fulfillmentId, true);
            if (fulfillment?.Snapshot.RefundTransactionId != request.RefundTransactionId ||
                fulfillment.Snapshot.Status != NCEconomicFulfillmentStatus.Revoked)
            {
                return RejectTrade(NCEconomicTradeMutationStatus.StorageFailure,
                    "The committed refunded fulfillment is incomplete.", ledger);
            }
        }
        if (!SameRefund(ledger.Transaction?.Snapshot, request, quote.Snapshot))
            return RejectTrade(NCEconomicTradeMutationStatus.StorageFailure,
                "The committed refund ledger transaction is inconsistent.", ledger);
        return new NCEconomicTradeMutationResult(status, ledger, reservation, quote, transfer, fulfillment,
            source, destination, batch, batch, item, null);
    }

    private static bool ValidPurchaseForRefund(
        NCLedgerTransactionSnapshot? transaction,
        NCEconomicQuoteSnapshot quote) => transaction != null &&
        transaction.TransactionId == quote.SettlementTransactionId &&
        transaction.Kind == NCLedgerTransactionKind.Purchase &&
        transaction.Reference == $"economic-quote:{quote.QuoteId}" &&
        transaction.CurrencyId == quote.CurrencyId && transaction.Legs.Length >= 2 &&
        transaction.Legs.Sum(leg => (decimal) leg.Amount) == 0;

    private static bool SameRefund(
        NCLedgerTransactionSnapshot? transaction,
        NCRefundEconomicSettlementRequest request,
        NCEconomicQuoteSnapshot quote) => transaction != null &&
        transaction.TransactionId == request.RefundTransactionId && transaction.Kind == NCLedgerTransactionKind.Refund &&
        transaction.Reference == RefundReference(quote) && transaction.CurrencyId == quote.CurrencyId &&
        transaction.Initiator == request.Initiator;

    private static string RefundReference(NCEconomicQuoteSnapshot quote) =>
        $"economic-refund:{quote.QuoteId}:{quote.SettlementTransactionId}";

    private static Guid[] RefundGateKeys(
        NCRefundEconomicSettlementRequest request,
        NCEconomicQuoteSnapshot quote,
        NCEconomicAssetTransferRecord? sale)
    {
        var keys = new List<Guid> { request.ReservationId.Value, quote.QuoteId.Value };
        if (quote.SettlementTransferId is { } saleTransferId)
            keys.Add(saleTransferId.Value);
        if (quote.FulfillmentId is { } fulfillmentId)
            keys.Add(fulfillmentId.Value);
        if (request.RefundTransferId is { } refundTransferId)
            keys.Add(refundTransferId.Value);
        if (sale != null)
        {
            keys.Add(sale.Snapshot.SourceStockNodeId.Value);
            keys.Add(sale.Snapshot.DestinationStockNodeId.Value);
            if (Guid.TryParse(sale.Snapshot.SourceAssetId, out var sourceAssetId))
                keys.Add(sourceAssetId);
            if (Guid.TryParse(sale.Snapshot.DestinationAssetId, out var destinationAssetId))
                keys.Add(destinationAssetId);
        }
        return keys.ToArray();
    }

    private static bool Valid(NCRefundEconomicSettlementRequest value) =>
        value.CommandId != Guid.Empty && value.RefundTransactionId.IsValid && value.ReservationId.IsValid &&
        value.ExpectedReservationVersion > 0 && value.ExpectedQuoteVersion > 0 &&
        value.AccountVersions is { Count: >= 2 and <= 16 } &&
        value.ExpectedPurchasedAssetVersion >= 0 && value.ExpectedBuyerStockNodeVersion >= 0 &&
        value.ExpectedSellerStockNodeVersion >= 0 &&
        (!value.RefundTransferId.HasValue || value.RefundTransferId.Value.IsValid) &&
        value.ExpectedFulfillmentVersion >= 0 && value.Initiator.IsValid && value.CorrelationId != Guid.Empty &&
        ValidAudit(value.AuditSource);

    private readonly record struct RefundParticipantBuild(
        NCEconomicTradeMutationStatus Status,
        NCEconomicAssetTransferSnapshot? Transfer,
        NCEconomicFulfillmentSnapshot? Fulfillment,
        string? Error)
    {
        public static RefundParticipantBuild Success(
            NCEconomicAssetTransferSnapshot? transfer,
            NCEconomicFulfillmentSnapshot? fulfillment) =>
            new(NCEconomicTradeMutationStatus.Applied, transfer, fulfillment, null);
        public static RefundParticipantBuild Reject(string error) =>
            new(NCEconomicTradeMutationStatus.Rejected, null, null, error);
        public static RefundParticipantBuild NotFound(string error) =>
            new(NCEconomicTradeMutationStatus.NotFound, null, null, error);
        public static RefundParticipantBuild Conflict(string error) =>
            new(NCEconomicTradeMutationStatus.VersionConflict, null, null, error);
    }
}
