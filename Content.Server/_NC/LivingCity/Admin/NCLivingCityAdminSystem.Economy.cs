// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Server._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Admin;

/// <summary>
/// Bounded, read-only F7 projections for the canonical economic aggregates. These views never expose JSON payloads
/// and cannot mutate balances, ownership, stock, reservations, or fulfillment state.
/// </summary>
public sealed partial class NCLivingCityAdminSystem
{
    private async Task<InspectionResult> InspectEconomicStockNodeAsync(string target)
    {
        if (!TryGuid(target, out var raw))
            return Fail("invalid-stock-node-id");
        var record = await _economicAssets.GetStockNodeAsync(new NCStockNodeId(raw), true);
        if (record == null)
            return Fail("stock-node-not-found");
        var snapshot = record.Snapshot;
        var fields = new FieldBuilder();
        fields.Add("stock-node", "stock-node-id", snapshot.StockNodeId);
        fields.Add("stock-node", "stable-key", snapshot.StableKey);
        fields.Add("stock-node", "kind", snapshot.Kind);
        fields.Add("stock-node", "owner", snapshot.Owner);
        fields.Add("stock-node", "semantic-location", snapshot.SemanticLocationKey);
        fields.Add("stock-node", "accepts-deposits", snapshot.AcceptsDeposits);
        fields.Add("stock-node", "allows-withdrawals", snapshot.AllowsWithdrawals);
        fields.Add("stock-node", "commodity-batches", snapshot.CommodityBatchIds.Count);
        fields.Add("stock-node", "unique-items", snapshot.ItemIds.Count);
        fields.Add("stock-node", "aggregate-version", record.AggregateVersion);
        fields.Add("stock-node", "created-at", snapshot.CreatedAt.Elapsed);
        fields.Add("stock-node", "updated-at", snapshot.UpdatedAt.Elapsed);
        foreach (var batchId in snapshot.CommodityBatchIds.Take(64))
            fields.Add("commodity-index", batchId.ToString(), "present");
        foreach (var itemId in snapshot.ItemIds.Take(64))
            fields.Add("item-index", itemId.ToString(), "present");
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectEconomicCommodityBatchAsync(string target)
    {
        if (!TryGuid(target, out var raw))
            return Fail("invalid-commodity-batch-id");
        var record = await _economicAssets.GetCommodityBatchAsync(new NCCommodityBatchId(raw), true);
        if (record == null)
            return Fail("commodity-batch-not-found");
        var snapshot = record.Snapshot;
        var fields = new FieldBuilder();
        fields.Add("commodity", "batch-id", snapshot.BatchId);
        fields.Add("commodity", "definition", snapshot.CommodityId);
        fields.Add("commodity", "quantity", snapshot.Quantity);
        fields.Add("commodity", "reserved", snapshot.ReservedQuantity);
        fields.Add("commodity", "available", snapshot.Quantity - snapshot.ReservedQuantity);
        fields.Add("commodity", "quality", snapshot.Quality);
        fields.Add("commodity", "owner", snapshot.Owner);
        fields.Add("commodity", "location-kind", snapshot.Location.Kind);
        fields.Add("commodity", "location", snapshot.Location.StableId);
        fields.Add("commodity", "legal-status", snapshot.LegalStatus);
        fields.Add("commodity", "trade-status", snapshot.TradeStatus);
        fields.Add("commodity", "lifecycle", snapshot.Lifecycle);
        fields.Add("commodity", "expires-at", snapshot.ExpiresAt?.Elapsed);
        fields.Add("commodity", "aggregate-version", record.AggregateVersion);
        fields.Add("provenance", "origin", snapshot.Provenance.OriginKey);
        fields.Add("provenance", "manufacturer", snapshot.Provenance.Manufacturer);
        fields.Add("provenance", "source-events", snapshot.Provenance.SourceWorldEventIds.Count);
        fields.Add("provenance", "produced-at", snapshot.Provenance.ProducedAt.Elapsed);
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectEconomicItemAsync(string target)
    {
        if (!TryGuid(target, out var raw))
            return Fail("invalid-economic-item-id");
        var record = await _economicAssets.GetItemAsync(new NCItemId(raw), true);
        if (record == null)
            return Fail("economic-item-not-found");
        var snapshot = record.Snapshot;
        var fields = new FieldBuilder();
        fields.Add("item", "item-id", snapshot.ItemId);
        fields.Add("item", "definition", snapshot.DefinitionId);
        fields.Add("item", "serial", snapshot.SerialNumber);
        fields.Add("item", "condition", snapshot.Condition);
        fields.Add("item", "owner", snapshot.Owner);
        fields.Add("item", "location-kind", snapshot.Location.Kind);
        fields.Add("item", "location", snapshot.Location.StableId);
        fields.Add("item", "legal-status", snapshot.LegalStatus);
        fields.Add("item", "trade-status", snapshot.TradeStatus);
        fields.Add("item", "lifecycle", snapshot.Lifecycle);
        fields.Add("item", "reservation", snapshot.ReservationId?.ToString() ?? "none");
        fields.Add("item", "parent-item", snapshot.ParentItemId?.ToString() ?? "none");
        fields.Add("item", "installed-parts", snapshot.InstalledPartIds.Count);
        fields.Add("item", "aggregate-version", record.AggregateVersion);
        fields.Add("provenance", "origin", snapshot.Provenance.OriginKey);
        fields.Add("provenance", "manufacturer", snapshot.Provenance.Manufacturer);
        fields.Add("provenance", "source-events", snapshot.Provenance.SourceWorldEventIds.Count);
        foreach (var partId in snapshot.InstalledPartIds.Take(64))
            fields.Add("installed-parts", partId.ToString(), "installed");
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectEconomicQuoteAsync(string target)
    {
        if (!TryGuid(target, out var raw))
            return Fail("invalid-economic-quote-id");
        var record = await _economicAssets.GetQuoteAsync(new NCEconomicQuoteId(raw), true);
        if (record == null)
            return Fail("economic-quote-not-found");
        var snapshot = record.Snapshot;
        var fields = new FieldBuilder();
        fields.Add("quote", "quote-id", snapshot.QuoteId);
        fields.Add("quote", "offer-kind", snapshot.OfferKind);
        fields.Add("quote", "offer-id", snapshot.OfferId);
        fields.Add("quote", "definition", snapshot.DefinitionId);
        fields.Add("quote", "quantity", snapshot.Quantity);
        fields.Add("quote", "seller", snapshot.Seller);
        fields.Add("quote", "buyer", snapshot.Buyer);
        fields.Add("quote", "source-stock-node", snapshot.SourceStockNodeId?.ToString() ?? "none");
        fields.Add("quote", "status", snapshot.Status);
        fields.Add("quote", "created-at", snapshot.CreatedAt.Elapsed);
        fields.Add("quote", "expires-at", snapshot.ExpiresAt.Elapsed);
        fields.Add("quote", "settlement-transaction", snapshot.SettlementTransactionId?.ToString() ?? "none");
        fields.Add("quote", "refund-transaction", snapshot.RefundTransactionId?.ToString() ?? "none");
        fields.Add("quote", "settlement-transfer", snapshot.SettlementTransferId?.ToString() ?? "none");
        fields.Add("quote", "fulfillment", snapshot.FulfillmentId?.ToString() ?? "none");
        fields.Add("quote", "aggregate-version", record.AggregateVersion);
        fields.Add("price", "currency", snapshot.CurrencyId);
        fields.Add("price", "base", snapshot.Price.BaseAmount);
        fields.Add("price", "multiplier-basis-points", snapshot.Price.AppliedMultiplierBasisPoints);
        fields.Add("price", "subtotal", snapshot.Price.Subtotal);
        fields.Add("price", "fee", snapshot.Price.Fee);
        fields.Add("price", "tax", snapshot.Price.Tax);
        fields.Add("price", "total", snapshot.Price.Total);
        fields.Add("inputs", "scarcity", snapshot.PriceContext.Scarcity);
        fields.Add("inputs", "demand", snapshot.PriceContext.Demand);
        fields.Add("inputs", "danger", snapshot.PriceContext.Danger);
        fields.Add("inputs", "relationship", snapshot.PriceContext.Relationship);
        fields.Add("inputs", "legality", snapshot.PriceContext.Legality);
        fields.Add("inputs", "urgency", snapshot.PriceContext.Urgency);
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectEconomicReservationAsync(string target)
    {
        if (!TryGuid(target, out var raw))
            return Fail("invalid-economic-reservation-id");
        var record = await _economicAssets.GetReservationAsync(new NCEconomicReservationId(raw), true);
        if (record == null)
            return Fail("economic-reservation-not-found");
        var snapshot = record.Snapshot;
        var fields = new FieldBuilder();
        fields.Add("reservation", "reservation-id", snapshot.ReservationId);
        fields.Add("reservation", "quote-id", snapshot.QuoteId);
        fields.Add("reservation", "offer-kind", snapshot.OfferKind);
        fields.Add("reservation", "offer-id", snapshot.OfferId);
        fields.Add("reservation", "quantity", snapshot.Quantity);
        fields.Add("reservation", "seller", snapshot.Seller);
        fields.Add("reservation", "buyer", snapshot.Buyer);
        fields.Add("reservation", "source-stock-node", snapshot.SourceStockNodeId?.ToString() ?? "none");
        fields.Add("reservation", "status", snapshot.Status);
        fields.Add("reservation", "created-at", snapshot.CreatedAt.Elapsed);
        fields.Add("reservation", "expires-at", snapshot.ExpiresAt.Elapsed);
        fields.Add("reservation", "settlement-transaction", snapshot.SettlementTransactionId?.ToString() ?? "none");
        fields.Add("reservation", "refund-transaction", snapshot.RefundTransactionId?.ToString() ?? "none");
        fields.Add("reservation", "aggregate-version", record.AggregateVersion);
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectEconomicTransferAsync(string target)
    {
        if (!TryGuid(target, out var raw))
            return Fail("invalid-economic-transfer-id");
        var record = await _economicAssets.GetTransferAsync(new NCEconomicTransferId(raw), true);
        if (record == null)
            return Fail("economic-transfer-not-found");
        var snapshot = record.Snapshot;
        var fields = new FieldBuilder();
        fields.Add("transfer", "transfer-id", snapshot.TransferId);
        fields.Add("transfer", "quote-id", snapshot.QuoteId?.ToString() ?? "none");
        fields.Add("transfer", "reservation-id", snapshot.ReservationId?.ToString() ?? "none");
        fields.Add("transfer", "asset-kind", snapshot.AssetKind);
        fields.Add("transfer", "source-asset", snapshot.SourceAssetId);
        fields.Add("transfer", "destination-asset", snapshot.DestinationAssetId);
        fields.Add("transfer", "quantity", snapshot.Quantity);
        fields.Add("transfer", "source-stock-node", snapshot.SourceStockNodeId);
        fields.Add("transfer", "destination-stock-node", snapshot.DestinationStockNodeId);
        fields.Add("transfer", "previous-owner", snapshot.PreviousOwner);
        fields.Add("transfer", "new-owner", snapshot.NewOwner);
        fields.Add("transfer", "initiator", snapshot.Initiator);
        fields.Add("transfer", "occurred-at", snapshot.OccurredAt.Elapsed);
        fields.Add("transfer", "aggregate-version", record.AggregateVersion);
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectEconomicFulfillmentAsync(string target)
    {
        if (!TryGuid(target, out var raw))
            return Fail("invalid-economic-fulfillment-id");
        var record = await _economicAssets.GetFulfillmentAsync(new NCEconomicFulfillmentId(raw), true);
        if (record == null)
            return Fail("economic-fulfillment-not-found");
        var snapshot = record.Snapshot;
        var fields = new FieldBuilder();
        fields.Add("fulfillment", "fulfillment-id", snapshot.FulfillmentId);
        fields.Add("fulfillment", "quote-id", snapshot.QuoteId);
        fields.Add("fulfillment", "offer-kind", snapshot.OfferKind);
        fields.Add("fulfillment", "definition", snapshot.DefinitionId);
        fields.Add("fulfillment", "provider", snapshot.Provider);
        fields.Add("fulfillment", "recipient", snapshot.Recipient);
        fields.Add("fulfillment", "status", snapshot.Status);
        fields.Add("fulfillment", "refund-transaction", snapshot.RefundTransactionId?.ToString() ?? "none");
        fields.Add("fulfillment", "created-at", snapshot.CreatedAt.Elapsed);
        fields.Add("fulfillment", "updated-at", snapshot.UpdatedAt.Elapsed);
        fields.Add("fulfillment", "aggregate-version", record.AggregateVersion);
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectEconomicReconciliationAsync(string target)
    {
        var parts = target.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length > 3 || parts.Any(value => value.Length > 0 && !TryGuid(value, out _)))
            return Fail("invalid-economic-reconciliation-cursor");
        var report = await _economicReconciliation.AuditAsync(new NCEconomicReconciliationCursor(
            parts.ElementAtOrDefault(0),
            parts.ElementAtOrDefault(1),
            parts.ElementAtOrDefault(2)));
        var fields = new FieldBuilder();
        fields.Add("reconciliation", "stock-nodes-scanned", report.StockNodesScanned);
        fields.Add("reconciliation", "commodity-batches-scanned", report.CommodityBatchesScanned);
        fields.Add("reconciliation", "items-scanned", report.ItemsScanned);
        fields.Add("reconciliation", "reference-checks", report.ReferenceChecks);
        fields.Add("reconciliation", "reference-checks-truncated", report.ReferenceChecksTruncated);
        fields.Add("reconciliation", "issues", report.Issues.Count);
        fields.Add("reconciliation", "has-more", report.HasMore);
        fields.Add("continuation", "stock-node-after", report.Continuation.StockNodeAfter ?? "none");
        fields.Add("continuation", "commodity-batch-after", report.Continuation.CommodityBatchAfter ?? "none");
        fields.Add("continuation", "item-after", report.Continuation.ItemAfter ?? "none");
        for (var i = 0; i < report.Issues.Count; i++)
        {
            var issue = report.Issues[i];
            var section = $"issue-{i:D3}";
            fields.Add(section, "aggregate-type", issue.AggregateType);
            fields.Add(section, "aggregate-id", issue.AggregateId);
            fields.Add(section, "code", issue.Code);
            fields.Add(section, "related-id", issue.RelatedId);
        }
        return Ok(fields);
    }
}
