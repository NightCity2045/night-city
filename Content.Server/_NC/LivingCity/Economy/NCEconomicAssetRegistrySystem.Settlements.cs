// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Identity;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Economy;

/// <summary>
/// Joins the common double-entry ledger and reserved stock in one serializable database mutation. It does not define
/// combat or RED resolution rules; the boundary only validates money, ownership, stock, and persistent identities.
/// </summary>
public sealed partial class NCEconomicAssetRegistrySystem
{
    public const string EconomicFulfillmentAggregateType = "living-city.economic-fulfillment";

    [Dependency] private NCEconomyLedgerSystem _ledger = default!;

    private readonly ConcurrentDictionary<NCEconomicFulfillmentId, NCEconomicFulfillmentRecord> _fulfillments = [];

    public int CachedFulfillmentCount => _fulfillments.Count;

    public bool Unload(NCEconomicFulfillmentId id) => _fulfillments.TryRemove(id, out _);

    public async Task<NCEconomicFulfillmentRecord?> GetFulfillmentAsync(
        NCEconomicFulfillmentId id,
        bool forceReload = false)
    {
        if (!id.IsValid)
            return null;
        if (!forceReload && _fulfillments.TryGetValue(id, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(EconomicFulfillmentAggregateType, Key(id));
        var record = DeserializeFulfillment(stored, id);
        return record == null
            ? null
            : _fulfillments.AddOrUpdate(id, record,
                (_, current) => current.AggregateVersion > record.AggregateVersion ? current : record);
    }

    public async Task<NCEconomicTradeMutationResult> SettleReservationAsync(
        NCSettleEconomicReservationRequest request)
    {
        if (!Valid(request) || !TryGetPolicy(out var assetPolicy) || !TryGetPricingPolicy(out var pricingPolicy) ||
            !_clock.TryGetCurrentTime(out _, out var now))
        {
            return RejectTrade(_clock.IsReady
                ? NCEconomicTradeMutationStatus.Rejected
                : NCEconomicTradeMutationStatus.ClockUnavailable,
                "The settlement request, asset policy, or active-world clock is invalid.");
        }

        try
        {
            var initialReservation = await GetReservationAsync(request.ReservationId, true);
            if (initialReservation == null)
                return RejectTrade(NCEconomicTradeMutationStatus.NotFound, "The reservation does not exist.");
            var initialQuote = await GetQuoteAsync(initialReservation.Snapshot.QuoteId, true);
            if (initialQuote == null)
                return RejectTrade(NCEconomicTradeMutationStatus.StorageFailure, "The reservation quote is missing.");
            if (!await ResolveParties(initialQuote.Snapshot.Seller, initialQuote.Snapshot.Buyer, request.Initiator))
                return RejectTrade(NCEconomicTradeMutationStatus.PartyUnavailable,
                    "The seller, buyer, or initiator is unavailable.");
            if (request.Initiator != initialQuote.Snapshot.Buyer)
                return RejectTrade(NCEconomicTradeMutationStatus.Rejected,
                    "Only the quoted buyer can settle this purchase.");

            if (!TryBuildLedgerLegs(request, initialQuote.Snapshot, pricingPolicy, out var legs, out var legError))
                return RejectTrade(NCEconomicTradeMutationStatus.Rejected, legError!);
            var accountError = await ValidateSettlementAccounts(
                request, initialQuote.Snapshot, pricingPolicy, legs!);
            if (accountError != null)
                return accountError;

            // A completed ledger command is replayed before rebuilding participant snapshots. This preserves exact
            // idempotency even though the reservation is no longer Active after the first commit.
            if (initialReservation.Snapshot.Status == NCEconomicReservationStatus.Settled)
            {
                var replay = await _ledger.PostTransactionAsync(LedgerRequest(request, initialQuote.Snapshot, legs!, []));
                if (replay.Status != NCEconomyMutationStatus.Duplicate ||
                    !Same(replay.Transaction?.Snapshot, request, initialQuote.Snapshot))
                {
                    return RejectTrade(Map(replay.Status), replay.Error ??
                        "The settled reservation does not match this ledger command replay.");
                }
                return await LoadTradeResult(request.ReservationId, replay, NCEconomicTradeMutationStatus.Duplicate);
            }

            var gateKeys = SettlementGateKeys(request, initialQuote.Snapshot);
            var gates = await AcquireGates(gateKeys);
            try
            {
                var reservation = await GetReservationAsync(request.ReservationId, true);
                var quote = reservation == null ? null : await GetQuoteAsync(reservation.Snapshot.QuoteId, true);
                if (reservation == null || quote == null)
                    return RejectTrade(NCEconomicTradeMutationStatus.StorageFailure,
                        "The reservation or quote disappeared while acquiring the settlement boundary.");
                if (reservation.AggregateVersion != request.ExpectedReservationVersion)
                    return RejectTrade(NCEconomicTradeMutationStatus.VersionConflict,
                        "The reservation version changed.");
                if (reservation.Snapshot.Status != NCEconomicReservationStatus.Active ||
                    quote.Snapshot.Status != NCEconomicQuoteStatus.Reserved || now >= reservation.Snapshot.ExpiresAt ||
                    now >= quote.Snapshot.ExpiresAt)
                {
                    return RejectTrade(NCEconomicTradeMutationStatus.Rejected,
                        "The quote or reservation is not active in active-world time.");
                }
                if (reservation.Snapshot.Seller != quote.Snapshot.Seller ||
                    reservation.Snapshot.Buyer != quote.Snapshot.Buyer ||
                    reservation.Snapshot.OfferKind != quote.Snapshot.OfferKind ||
                    reservation.Snapshot.OfferId != quote.Snapshot.OfferId ||
                    reservation.Snapshot.Quantity != quote.Snapshot.Quantity)
                {
                    return RejectTrade(NCEconomicTradeMutationStatus.StorageFailure,
                        "The reservation and quote immutable fields disagree.");
                }

                var writes = new List<NCPersistentAggregateWrite>();
                NCEconomicAssetTransferSnapshot? transfer = null;
                NCEconomicFulfillmentSnapshot? fulfillment = null;
                var assetResult = await BuildSettlementAssetWrites(
                    request, quote, reservation, assetPolicy, now, writes);
                if (assetResult.Error != null)
                    return RejectTrade(assetResult.Status, assetResult.Error);
                transfer = assetResult.Transfer;
                fulfillment = assetResult.Fulfillment;

                var reservationEventId = Guid.NewGuid();
                var nextReservation = reservation.Snapshot with
                {
                    Status = NCEconomicReservationStatus.Settled,
                    SettlementTransactionId = request.TransactionId,
                    DataVersion = checked(reservation.Snapshot.DataVersion + 1),
                    LastAppliedEventId = reservationEventId,
                };
                var quoteEventId = Guid.NewGuid();
                var nextQuote = quote.Snapshot with
                {
                    Status = NCEconomicQuoteStatus.Settled,
                    SettlementTransactionId = request.TransactionId,
                    SettlementTransferId = transfer?.TransferId,
                    FulfillmentId = fulfillment?.FulfillmentId,
                    DataVersion = checked(quote.Snapshot.DataVersion + 1),
                    LastAppliedEventId = quoteEventId,
                };
                writes.Add(ReservationWrite(reservation, nextReservation, reservationEventId,
                    "living-city.economic-reservation.settled", request.AuditSource));
                writes.Add(QuoteWrite(quote, nextQuote, quoteEventId,
                    "living-city.economic-quote.settled", request.AuditSource));

                var participants = writes.Select(ToParticipant).ToArray();
                var ledger = await _ledger.PostTransactionAsync(
                    LedgerRequest(request, quote.Snapshot, legs!, participants));
                if (ledger.Status is not (NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate))
                    return RejectTrade(Map(ledger.Status), ledger.Error ?? "The atomic purchase was not committed.", ledger);
                return await LoadTradeResult(request.ReservationId, ledger,
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
            Log.Error($"Failed to settle reservation {request.ReservationId}: {exception}");
            return RejectTrade(NCEconomicTradeMutationStatus.StorageFailure, exception.Message);
        }
    }

    private async Task<SettlementAssetBuild> BuildSettlementAssetWrites(
        NCSettleEconomicReservationRequest request,
        NCEconomicQuoteRecord quote,
        NCEconomicReservationRecord reservation,
        NCEconomicAssetPolicyPrototype policy,
        NCActiveWorldTime now,
        List<NCPersistentAggregateWrite> writes)
    {
        if (quote.Snapshot.OfferKind is NCEconomicOfferKind.CommodityBatch or NCEconomicOfferKind.ItemInstance)
        {
            if (request.DestinationStockNodeId is not { } destinationId ||
                request.TransferId is not { } transferId || request.FulfillmentId.HasValue)
            {
                return SettlementAssetBuild.Reject("An asset purchase requires destination stock and transfer IDs only.");
            }
            if (await GetTransferAsync(transferId, true) != null)
                return SettlementAssetBuild.Reject("The settlement transfer identity already exists.");
            var sourceId = reservation.Snapshot.SourceStockNodeId!.Value;
            var source = await GetStockNodeAsync(sourceId, true);
            var destination = await GetStockNodeAsync(destinationId, true);
            if (source == null || destination == null)
                return SettlementAssetBuild.NotFound("The source or destination stock node does not exist.");
            if (_materializationLeases.IsLeased(sourceId) || _materializationLeases.IsLeased(destinationId))
                return SettlementAssetBuild.Reject("Materialized stock requires a physical settlement interaction.");
            if (source.AggregateVersion != reservation.Snapshot.ReservedStockNodeVersion ||
                destination.AggregateVersion != request.ExpectedDestinationStockNodeVersion)
            {
                return SettlementAssetBuild.Conflict("A settlement stock-node version changed.");
            }
            if (source.Snapshot.Owner != quote.Snapshot.Seller ||
                destination.Snapshot.Owner != quote.Snapshot.Buyer || !source.Snapshot.AllowsWithdrawals ||
                !destination.Snapshot.AcceptsDeposits)
            {
                return SettlementAssetBuild.Reject("The settlement stock ownership or deposit policy is invalid.");
            }

            if (quote.Snapshot.OfferKind == NCEconomicOfferKind.ItemInstance)
            {
                if (request.SplitBatchId.HasValue || destination.Snapshot.ItemIds.Count >= policy.MaximumItemsPerStockNode)
                    return SettlementAssetBuild.Reject("The item settlement shape or destination capacity is invalid.");
                var itemId = new NCItemId(Guid.Parse(quote.Snapshot.OfferId));
                var item = await GetItemAsync(itemId, true);
                if (item == null)
                    return SettlementAssetBuild.NotFound("The reserved item does not exist.");
                if (item.AggregateVersion != reservation.Snapshot.ReservedAssetVersion ||
                    item.Snapshot.ReservationId != reservation.Snapshot.ReservationId ||
                    item.Snapshot.Owner != quote.Snapshot.Seller || item.Snapshot.Location != StockLocation(sourceId) ||
                    !source.Snapshot.ItemIds.Contains(itemId) || destination.Snapshot.ItemIds.Contains(itemId))
                {
                    return SettlementAssetBuild.Conflict("The reserved item or its stock indexes changed.");
                }
                var installedParts = await LoadAssemblyPartsAsync(item);
                if (installedParts == null)
                    return SettlementAssetBuild.Conflict(
                        "The reserved item assembly contains an inconsistent installed part.");

                var itemEventId = Guid.NewGuid();
                var nextItem = item.Snapshot with
                {
                    ReservationId = null,
                    Owner = quote.Snapshot.Buyer,
                    Location = StockLocation(destinationId),
                    UpdatedAt = now,
                    DataVersion = checked(item.Snapshot.DataVersion + 1),
                    LastAppliedEventId = itemEventId,
                };
                var itemSourceEventId = Guid.NewGuid();
                var nextItemSource = source.Snapshot with
                {
                    ItemIds = source.Snapshot.ItemIds.Where(id => id != itemId).ToArray(),
                    UpdatedAt = now,
                    DataVersion = checked(source.Snapshot.DataVersion + 1),
                    LastAppliedEventId = itemSourceEventId,
                };
                var itemDestinationEventId = Guid.NewGuid();
                var nextItemDestination = destination.Snapshot with
                {
                    ItemIds = destination.Snapshot.ItemIds.Append(itemId).OrderBy(id => id.Value).ToArray(),
                    UpdatedAt = now,
                    DataVersion = checked(destination.Snapshot.DataVersion + 1),
                    LastAppliedEventId = itemDestinationEventId,
                };
                var itemTransferEventId = Guid.NewGuid();
                var transfer = new NCEconomicAssetTransferSnapshot(
                    transferId,
                    quote.Snapshot.QuoteId,
                    reservation.Snapshot.ReservationId,
                    NCEconomicTransferAssetKind.ItemInstance,
                    Key(itemId),
                    Key(itemId),
                    1,
                    sourceId,
                    destinationId,
                    quote.Snapshot.Seller,
                    quote.Snapshot.Buyer,
                    request.Initiator,
                    now,
                    1,
                    itemTransferEventId);
                writes.Add(ItemMutationWrite(item, nextItem, itemEventId,
                    "living-city.item-instance.purchased", request.AuditSource));
                foreach (var part in installedParts)
                {
                    var partEventId = Guid.NewGuid();
                    var nextPart = part.Snapshot with
                    {
                        Owner = quote.Snapshot.Buyer,
                        UpdatedAt = now,
                        DataVersion = checked(part.Snapshot.DataVersion + 1),
                        LastAppliedEventId = partEventId,
                    };
                    writes.Add(ItemMutationWrite(part, nextPart, partEventId,
                        "living-city.item-instance.purchased-with-assembly", request.AuditSource));
                }
                writes.Add(StockNodeWrite(source, nextItemSource, itemSourceEventId,
                    "living-city.stock-node.item-sold", request.AuditSource));
                writes.Add(StockNodeWrite(destination, nextItemDestination, itemDestinationEventId,
                    "living-city.stock-node.item-purchased", request.AuditSource));
                writes.Add(TransferWrite(transfer, itemTransferEventId, request.AuditSource));
                return SettlementAssetBuild.Success(transfer);
            }

            if (destination.Snapshot.CommodityBatchIds.Count >= policy.MaximumBatchesPerStockNode)
                return SettlementAssetBuild.Capacity("The destination commodity capacity is exhausted.");
            var sourceBatchId = new NCCommodityBatchId(Guid.Parse(quote.Snapshot.OfferId));
            var batch = await GetCommodityBatchAsync(sourceBatchId, true);
            if (batch == null)
                return SettlementAssetBuild.NotFound("The reserved commodity batch does not exist.");
            if (batch.AggregateVersion != reservation.Snapshot.ReservedAssetVersion ||
                batch.Snapshot.ReservedQuantity < quote.Snapshot.Quantity ||
                batch.Snapshot.ExpiresAt is { } expiresAt && now >= expiresAt ||
                batch.Snapshot.Owner != quote.Snapshot.Seller || batch.Snapshot.Location != StockLocation(sourceId) ||
                !source.Snapshot.CommodityBatchIds.Contains(sourceBatchId))
            {
                return SettlementAssetBuild.Conflict("The reserved commodity or its stock index changed.");
            }
            var split = quote.Snapshot.Quantity < batch.Snapshot.Quantity;
            if (split != request.SplitBatchId.HasValue || request.SplitBatchId == sourceBatchId)
                return SettlementAssetBuild.Reject("A partial purchase requires one unused split-batch identity.");
            var destinationBatchId = split ? request.SplitBatchId!.Value : sourceBatchId;
            if (destination.Snapshot.CommodityBatchIds.Contains(destinationBatchId) ||
                split && await GetCommodityBatchAsync(destinationBatchId, true) != null)
            {
                return SettlementAssetBuild.Reject("The purchased batch identity already exists at the destination.");
            }

            var batchEventId = Guid.NewGuid();
            var nextBatch = batch.Snapshot with
            {
                Quantity = split ? batch.Snapshot.Quantity - quote.Snapshot.Quantity : batch.Snapshot.Quantity,
                ReservedQuantity = split
                    ? batch.Snapshot.ReservedQuantity - quote.Snapshot.Quantity
                    : 0,
                Owner = split ? batch.Snapshot.Owner : quote.Snapshot.Buyer,
                Location = split ? batch.Snapshot.Location : StockLocation(destinationId),
                UpdatedAt = now,
                DataVersion = checked(batch.Snapshot.DataVersion + 1),
                LastAppliedEventId = batchEventId,
            };
            NCCommodityBatchSnapshot? purchasedBatch = null;
            if (split)
            {
                purchasedBatch = batch.Snapshot with
                {
                    BatchId = destinationBatchId,
                    Quantity = quote.Snapshot.Quantity,
                    ReservedQuantity = 0,
                    Owner = quote.Snapshot.Buyer,
                    Location = StockLocation(destinationId),
                    CreatedAt = now,
                    UpdatedAt = now,
                    DataVersion = 1,
                    LastAppliedEventId = Guid.NewGuid(),
                };
            }
            var sourceEventId = Guid.NewGuid();
            var nextSource = source.Snapshot with
            {
                CommodityBatchIds = split
                    ? source.Snapshot.CommodityBatchIds
                    : source.Snapshot.CommodityBatchIds.Where(id => id != sourceBatchId).ToArray(),
                UpdatedAt = now,
                DataVersion = checked(source.Snapshot.DataVersion + 1),
                LastAppliedEventId = sourceEventId,
            };
            var destinationEventId = Guid.NewGuid();
            var nextDestination = destination.Snapshot with
            {
                CommodityBatchIds = destination.Snapshot.CommodityBatchIds.Append(destinationBatchId)
                    .OrderBy(id => id.Value).ToArray(),
                UpdatedAt = now,
                DataVersion = checked(destination.Snapshot.DataVersion + 1),
                LastAppliedEventId = destinationEventId,
            };
            var transferEventId = Guid.NewGuid();
            var commodityTransfer = new NCEconomicAssetTransferSnapshot(
                transferId,
                quote.Snapshot.QuoteId,
                reservation.Snapshot.ReservationId,
                NCEconomicTransferAssetKind.CommodityBatch,
                Key(sourceBatchId),
                Key(destinationBatchId),
                quote.Snapshot.Quantity,
                sourceId,
                destinationId,
                quote.Snapshot.Seller,
                quote.Snapshot.Buyer,
                request.Initiator,
                now,
                1,
                transferEventId);
            writes.Add(CommodityMutationWrite(batch, nextBatch, batchEventId,
                split ? "living-city.commodity-batch.purchase-split" : "living-city.commodity-batch.purchased",
                request.AuditSource));
            if (purchasedBatch != null)
            {
                writes.Add(CommodityMutationWrite(null, purchasedBatch, purchasedBatch.LastAppliedEventId,
                    "living-city.commodity-batch.purchase-created", request.AuditSource));
            }
            writes.Add(StockNodeWrite(source, nextSource, sourceEventId,
                split ? "living-city.stock-node.batch-sale-split" : "living-city.stock-node.batch-sold",
                request.AuditSource));
            writes.Add(StockNodeWrite(destination, nextDestination, destinationEventId,
                "living-city.stock-node.batch-purchased", request.AuditSource));
            writes.Add(TransferWrite(commodityTransfer, transferEventId, request.AuditSource));
            return SettlementAssetBuild.Success(commodityTransfer);
        }

        if (request.DestinationStockNodeId.HasValue || request.TransferId.HasValue || request.SplitBatchId.HasValue ||
            request.FulfillmentId is not { } fulfillmentId)
        {
            return SettlementAssetBuild.Reject("A service/access purchase requires one fulfillment ID and no stock IDs.");
        }
        if (await GetFulfillmentAsync(fulfillmentId, true) != null)
            return SettlementAssetBuild.Reject("The fulfillment identity already exists.");
        var fulfillmentEventId = Guid.NewGuid();
        var fulfillment = new NCEconomicFulfillmentSnapshot(
            fulfillmentId,
            quote.Snapshot.QuoteId,
            quote.Snapshot.OfferKind,
            quote.Snapshot.DefinitionId,
            quote.Snapshot.Seller,
            quote.Snapshot.Buyer,
            quote.Snapshot.OfferKind == NCEconomicOfferKind.InformationAccess
                ? NCEconomicFulfillmentStatus.Granted
                : NCEconomicFulfillmentStatus.Pending,
            null,
            now,
            now,
            1,
            fulfillmentEventId);
        writes.Add(FulfillmentWrite(null, fulfillment, fulfillmentEventId,
            "living-city.economic-fulfillment.purchased", request.AuditSource));
        return SettlementAssetBuild.Success(fulfillment);
    }

    private async Task<NCEconomicTradeMutationResult?> ValidateSettlementAccounts(
        NCSettleEconomicReservationRequest request,
        NCEconomicQuoteSnapshot quote,
        NCEconomicPricingPolicyPrototype policy,
        IReadOnlyList<NCLedgerLegRequest> legs)
    {
        var accounts = new Dictionary<NCAccountId, NCAccountRecord>();
        foreach (var leg in legs)
        {
            var account = await _ledger.GetAccountAsync(leg.AccountId, true);
            if (account == null)
                return RejectTrade(NCEconomicTradeMutationStatus.NotFound, $"Account {leg.AccountId} does not exist.");
            accounts.Add(leg.AccountId, account);
        }
        if (accounts[request.BuyerAccountId].Snapshot.Owner != quote.Buyer ||
            accounts[request.SellerAccountId].Snapshot.Owner != quote.Seller)
        {
            return RejectTrade(NCEconomicTradeMutationStatus.Rejected,
                "The buyer or seller account owner does not match the quote.");
        }
        if (request.FeeAccountId is { } feeAccount &&
            accounts[feeAccount].Snapshot.Owner != NCPersistentIdentity.CityService(policy.FeeReceiverServiceId) ||
            request.TaxAccountId is { } taxAccount &&
            accounts[taxAccount].Snapshot.Owner != NCPersistentIdentity.CityService(policy.TaxReceiverServiceId))
        {
            return RejectTrade(NCEconomicTradeMutationStatus.Rejected,
                "A fee or tax account does not belong to its authored city-service receiver.");
        }
        if (accounts.Values.Any(account => account.Snapshot.Status != NCAccountStatus.Active ||
                account.Snapshot.CurrencyId != quote.CurrencyId))
        {
            return RejectTrade(NCEconomicTradeMutationStatus.Rejected,
                "Settlement accounts must be active and use the quote currency.");
        }
        return null;
    }

    private static bool TryBuildLedgerLegs(
        NCSettleEconomicReservationRequest request,
        NCEconomicQuoteSnapshot quote,
        NCEconomicPricingPolicyPrototype policy,
        out NCLedgerLegRequest[]? legs,
        out string? error)
    {
        legs = null;
        error = null;
        if (quote.Price.Total <= 0 || quote.Price.Subtotal <= 0)
        {
            error = "Zero-value offers do not cross the monetary settlement boundary.";
            return false;
        }
        if (quote.Price.Fee > 0 != request.FeeAccountId.HasValue ||
            quote.Price.Tax > 0 != request.TaxAccountId.HasValue)
        {
            error = "Fee and tax receiver accounts must exactly match the authored quote charges.";
            return false;
        }
        if (request.FeeAccountId is { } feeReceiver &&
            feeReceiver != NCPersistentIdentity.PrimaryAccount(
                NCPersistentIdentity.CityService(policy.FeeReceiverServiceId)) ||
            request.TaxAccountId is { } taxReceiver &&
            taxReceiver != NCPersistentIdentity.PrimaryAccount(
                NCPersistentIdentity.CityService(policy.TaxReceiverServiceId)))
        {
            error = "Fee and tax receivers must be the deterministic accounts authored by server policy.";
            return false;
        }

        var amounts = new Dictionary<NCAccountId, (long Version, long Amount)>();
        if (!AddLeg(amounts, request.BuyerAccountId, request.ExpectedBuyerAccountVersion, -quote.Price.Total) ||
            !AddLeg(amounts, request.SellerAccountId, request.ExpectedSellerAccountVersion, quote.Price.Subtotal) ||
            request.FeeAccountId is { } fee &&
            !AddLeg(amounts, fee, request.ExpectedFeeAccountVersion, quote.Price.Fee) ||
            request.TaxAccountId is { } tax &&
            !AddLeg(amounts, tax, request.ExpectedTaxAccountVersion, quote.Price.Tax))
        {
            error = "Settlement account versions conflict or a consolidated ledger leg is zero.";
            return false;
        }
        legs = amounts.OrderBy(pair => pair.Key.Value).Select(pair =>
            new NCLedgerLegRequest(pair.Key, pair.Value.Version, pair.Value.Amount)).ToArray();
        if (legs.Length >= 2 && legs.Sum(leg => (decimal) leg.Amount) == 0)
            return true;
        error = "The consolidated settlement legs are not a balanced double-entry transaction.";
        legs = null;
        return false;
    }

    private static bool AddLeg(
        IDictionary<NCAccountId, (long Version, long Amount)> legs,
        NCAccountId accountId,
        long expectedVersion,
        long amount)
    {
        if (!accountId.IsValid || expectedVersion <= 0 || amount == 0)
            return false;
        if (!legs.TryGetValue(accountId, out var existing))
        {
            legs.Add(accountId, (expectedVersion, amount));
            return true;
        }
        if (existing.Version != expectedVersion)
            return false;
        var combined = checked(existing.Amount + amount);
        if (combined == 0)
            return false;
        legs[accountId] = (expectedVersion, combined);
        return true;
    }

    private static NCPostLedgerTransactionRequest LedgerRequest(
        NCSettleEconomicReservationRequest request,
        NCEconomicQuoteSnapshot quote,
        NCLedgerLegRequest[] legs,
        IReadOnlyList<NCAtomicLedgerParticipantWrite> participants) => new(
        request.CommandId,
        request.TransactionId,
        NCLedgerTransactionKind.Purchase,
        $"economic-quote:{quote.QuoteId}",
        legs,
        request.Initiator,
        request.CorrelationId,
        request.CausationId,
        request.AuditSource,
        participants);

    private async Task<NCEconomicTradeMutationResult> LoadTradeResult(
        NCEconomicReservationId reservationId,
        NCEconomyMutationResult ledger,
        NCEconomicTradeMutationStatus status)
    {
        var reservation = await GetReservationAsync(reservationId, true);
        var quote = reservation == null ? null : await GetQuoteAsync(reservation.Snapshot.QuoteId, true);
        if (reservation == null || quote == null)
            return RejectTrade(NCEconomicTradeMutationStatus.StorageFailure,
                "The committed trade reservation or quote could not be reloaded.", ledger);

        NCEconomicAssetTransferRecord? transfer = null;
        NCEconomicFulfillmentRecord? fulfillment = null;
        NCStockNodeRecord? source = null;
        NCStockNodeRecord? destination = null;
        NCCommodityBatchRecord? sourceBatch = null;
        NCCommodityBatchRecord? destinationBatch = null;
        NCItemInstanceRecord? item = null;
        if (quote.Snapshot.SettlementTransferId is { } transferId)
        {
            transfer = await GetTransferAsync(transferId, true);
            if (transfer != null)
            {
                source = await GetStockNodeAsync(transfer.Snapshot.SourceStockNodeId, true);
                destination = await GetStockNodeAsync(transfer.Snapshot.DestinationStockNodeId, true);
                if (transfer.Snapshot.AssetKind == NCEconomicTransferAssetKind.ItemInstance)
                    item = await GetItemAsync(new NCItemId(Guid.Parse(transfer.Snapshot.DestinationAssetId)), true);
                else
                {
                    sourceBatch = await GetCommodityBatchAsync(
                        new NCCommodityBatchId(Guid.Parse(transfer.Snapshot.SourceAssetId)), true);
                    destinationBatch = transfer.Snapshot.SourceAssetId == transfer.Snapshot.DestinationAssetId
                        ? sourceBatch
                        : await GetCommodityBatchAsync(
                            new NCCommodityBatchId(Guid.Parse(transfer.Snapshot.DestinationAssetId)), true);
                }
            }
        }
        if (quote.Snapshot.FulfillmentId is { } fulfillmentId)
            fulfillment = await GetFulfillmentAsync(fulfillmentId, true);

        if (quote.Snapshot.SettlementTransactionId != ledger.Transaction?.Snapshot.TransactionId ||
            quote.Snapshot.Status != NCEconomicQuoteStatus.Settled ||
            reservation.Snapshot.Status != NCEconomicReservationStatus.Settled ||
            quote.Snapshot.SettlementTransferId.HasValue && transfer == null ||
            quote.Snapshot.FulfillmentId.HasValue && fulfillment == null)
        {
            return RejectTrade(NCEconomicTradeMutationStatus.StorageFailure,
                "The committed trade projections are incomplete.", ledger);
        }
        return new NCEconomicTradeMutationResult(
            status, ledger, reservation, quote, transfer, fulfillment, source, destination,
            sourceBatch, destinationBatch, item, null);
    }

    private NCPersistentAggregateWrite FulfillmentWrite(
        NCEconomicFulfillmentRecord? current,
        NCEconomicFulfillmentSnapshot next,
        Guid eventId,
        string eventType,
        string auditSource) => new(
        eventId,
        eventType,
        EconomicFulfillmentAggregateType,
        Key(next.FulfillmentId),
        current?.AggregateVersion ?? 0,
        SnapshotFormatVersion,
        JsonSerializer.Serialize(next, JsonOptions),
        JsonSerializer.Serialize(new AssetEvent(Key(next.FulfillmentId), auditSource.Trim()), JsonOptions));

    private static NCAtomicLedgerParticipantWrite ToParticipant(NCPersistentAggregateWrite write) => new(
        write.EventId,
        write.EventType,
        write.AggregateType,
        write.AggregateId,
        write.ExpectedAggregateVersion,
        write.SnapshotFormatVersion,
        write.SnapshotPayload,
        write.EventPayload);

    private static bool Valid(NCSettleEconomicReservationRequest value) => value.CommandId != Guid.Empty &&
        value.TransactionId.IsValid && value.ReservationId.IsValid && value.ExpectedReservationVersion > 0 &&
        value.BuyerAccountId.IsValid && value.ExpectedBuyerAccountVersion > 0 && value.SellerAccountId.IsValid &&
        value.ExpectedSellerAccountVersion > 0 && value.BuyerAccountId != value.SellerAccountId &&
        (!value.FeeAccountId.HasValue && value.ExpectedFeeAccountVersion == 0 ||
            value.FeeAccountId is { IsValid: true } && value.ExpectedFeeAccountVersion > 0) &&
        (!value.TaxAccountId.HasValue && value.ExpectedTaxAccountVersion == 0 ||
            value.TaxAccountId is { IsValid: true } && value.ExpectedTaxAccountVersion > 0) &&
        (!value.DestinationStockNodeId.HasValue && value.ExpectedDestinationStockNodeVersion == 0 ||
            value.DestinationStockNodeId is { IsValid: true } && value.ExpectedDestinationStockNodeVersion > 0) &&
        (!value.TransferId.HasValue || value.TransferId.Value.IsValid) &&
        (!value.SplitBatchId.HasValue || value.SplitBatchId.Value.IsValid) &&
        (!value.FulfillmentId.HasValue || value.FulfillmentId.Value.IsValid) && value.Initiator.IsValid &&
        value.CorrelationId != Guid.Empty && ValidAudit(value.AuditSource);

    private static bool Valid(NCEconomicFulfillmentSnapshot? value, NCEconomicFulfillmentId expected) =>
        value != null && value.FulfillmentId == expected && expected.IsValid && value.QuoteId.IsValid &&
        value.OfferKind is NCEconomicOfferKind.Service or NCEconomicOfferKind.InformationAccess &&
        IsPrototypeKey(value.DefinitionId, 128) && value.Provider.IsValid && value.Recipient.IsValid &&
        value.Provider != value.Recipient && Enum.IsDefined(value.Status) && value.CreatedAt.IsValid &&
        (!value.RefundTransactionId.HasValue || value.RefundTransactionId.Value.IsValid) &&
        value.UpdatedAt >= value.CreatedAt && value.DataVersion > 0 && value.LastAppliedEventId != Guid.Empty;

    private static NCEconomicFulfillmentRecord? DeserializeFulfillment(
        NCAggregateSnapshotData? stored,
        NCEconomicFulfillmentId expected)
    {
        if (!StoredShape(stored))
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCEconomicFulfillmentSnapshot>(stored!.Payload, JsonOptions);
            return Valid(snapshot, expected) && snapshot!.DataVersion == stored.AggregateVersion
                ? new NCEconomicFulfillmentRecord(snapshot, stored.AggregateVersion, stored.SessionEpoch,
                    stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Same(
        NCLedgerTransactionSnapshot? transaction,
        NCSettleEconomicReservationRequest request,
        NCEconomicQuoteSnapshot quote) => transaction != null && transaction.TransactionId == request.TransactionId &&
        transaction.Kind == NCLedgerTransactionKind.Purchase &&
        transaction.Reference == $"economic-quote:{quote.QuoteId}" && transaction.CurrencyId == quote.CurrencyId &&
        transaction.Initiator == request.Initiator;

    private static Guid[] SettlementGateKeys(
        NCSettleEconomicReservationRequest request,
        NCEconomicQuoteSnapshot quote)
    {
        var keys = new List<Guid> { request.ReservationId.Value, quote.QuoteId.Value };
        if (quote.SourceStockNodeId is { } sourceId)
            keys.Add(sourceId.Value);
        if (request.DestinationStockNodeId is { } destinationId)
            keys.Add(destinationId.Value);
        if (Guid.TryParse(quote.OfferId, out var assetId))
            keys.Add(assetId);
        if (request.TransferId is { } transferId)
            keys.Add(transferId.Value);
        if (request.SplitBatchId is { } splitBatchId)
            keys.Add(splitBatchId.Value);
        if (request.FulfillmentId is { } fulfillmentId)
            keys.Add(fulfillmentId.Value);
        return keys.ToArray();
    }

    private static NCEconomicTradeMutationStatus Map(NCEconomyMutationStatus status) => status switch
    {
        NCEconomyMutationStatus.Applied => NCEconomicTradeMutationStatus.Applied,
        NCEconomyMutationStatus.Duplicate => NCEconomicTradeMutationStatus.Duplicate,
        NCEconomyMutationStatus.NotFound => NCEconomicTradeMutationStatus.NotFound,
        NCEconomyMutationStatus.VersionConflict => NCEconomicTradeMutationStatus.VersionConflict,
        NCEconomyMutationStatus.InsufficientFunds => NCEconomicTradeMutationStatus.InsufficientFunds,
        NCEconomyMutationStatus.ClockUnavailable => NCEconomicTradeMutationStatus.ClockUnavailable,
        NCEconomyMutationStatus.Rejected => NCEconomicTradeMutationStatus.Rejected,
        _ => NCEconomicTradeMutationStatus.StorageFailure,
    };

    private static NCEconomicTradeMutationResult RejectTrade(
        NCEconomicTradeMutationStatus status,
        string error,
        NCEconomyMutationResult? ledger = null) =>
        new(status, ledger, null, null, null, null, null, null, null, null, null, error);

    private static string Key(NCEconomicFulfillmentId id) => id.Value.ToString("D");

    private readonly record struct SettlementAssetBuild(
        NCEconomicTradeMutationStatus Status,
        NCEconomicAssetTransferSnapshot? Transfer,
        NCEconomicFulfillmentSnapshot? Fulfillment,
        string? Error)
    {
        public static SettlementAssetBuild Success(NCEconomicAssetTransferSnapshot transfer) =>
            new(NCEconomicTradeMutationStatus.Applied, transfer, null, null);
        public static SettlementAssetBuild Success(NCEconomicFulfillmentSnapshot fulfillment) =>
            new(NCEconomicTradeMutationStatus.Applied, null, fulfillment, null);
        public static SettlementAssetBuild Reject(string error) =>
            new(NCEconomicTradeMutationStatus.Rejected, null, null, error);
        public static SettlementAssetBuild NotFound(string error) =>
            new(NCEconomicTradeMutationStatus.NotFound, null, null, error);
        public static SettlementAssetBuild Conflict(string error) =>
            new(NCEconomicTradeMutationStatus.VersionConflict, null, null, error);
        public static SettlementAssetBuild Capacity(string error) =>
            new(NCEconomicTradeMutationStatus.Rejected, null, null, error);
    }
}
