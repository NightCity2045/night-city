// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Market;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Admin;

/// <summary>
/// Bounded read-only F7 projections for market, business, production and logistics aggregates.
/// </summary>
public sealed partial class NCLivingCityAdminSystem
{
    private async Task<InspectionResult> InspectMarketListingAsync(string target)
    {
        if (!TryGuid(target, out var raw))
            return Fail("invalid-market-listing-id");
        var record = await _market.GetListingAsync(new NCMarketListingId(raw), true);
        if (record == null)
            return Fail("market-listing-not-found");
        var value = record.Snapshot;
        var fields = new FieldBuilder();
        fields.Add("listing", "id", value.ListingId);
        fields.Add("listing", "status", value.Status);
        fields.Add("listing", "channel", value.Channel);
        fields.Add("listing", "offer-kind", value.OfferKind);
        fields.Add("listing", "offer-id", value.OfferId);
        fields.Add("listing", "maximum-quantity", value.MaximumQuantity);
        fields.Add("listing", "seller", value.Seller);
        fields.Add("listing", "stock-node", value.SourceStockNodeId?.ToString() ?? "none");
        fields.Add("access", "visibility", value.Access.Visibility);
        fields.Add("access", "audience", value.Access.AudienceKey);
        fields.Add("time", "created", value.CreatedAt.Elapsed);
        fields.Add("time", "updated", value.UpdatedAt.Elapsed);
        fields.Add("time", "expires", value.ExpiresAt?.Elapsed.ToString() ?? "none");
        fields.Add("persistent", "aggregate-version", record.AggregateVersion);
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectMarketRequestAsync(string target)
    {
        if (!TryGuid(target, out var raw))
            return Fail("invalid-market-request-id");
        var record = await _market.GetRequestAsync(new NCMarketRequestId(raw), true);
        if (record == null)
            return Fail("market-request-not-found");
        var value = record.Snapshot;
        var fields = new FieldBuilder();
        fields.Add("request", "id", value.RequestId);
        fields.Add("request", "status", value.Status);
        fields.Add("request", "kind", value.Kind);
        fields.Add("request", "definition", value.DefinitionId);
        fields.Add("request", "quantity", value.Quantity);
        fields.Add("request", "maximum-unit-price", value.MaximumUnitPrice);
        fields.Add("request", "minimum-quality", value.MinimumQuality);
        fields.Add("request", "requester", value.Requester);
        fields.Add("request", "channel", value.Channel);
        fields.Add("request", "preferred-zone", value.PreferredZone);
        fields.Add("request", "matched-listing", value.MatchedListingId?.ToString() ?? "none");
        fields.Add("time", "expires", value.ExpiresAt.Elapsed);
        fields.Add("persistent", "aggregate-version", record.AggregateVersion);
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectBusinessAsync(string target)
    {
        if (!TryGuid(target, out var raw))
            return Fail("invalid-business-id");
        var record = await _businesses.GetAsync(new NCBusinessId(raw), true);
        if (record == null)
            return Fail("business-not-found");
        var value = record.Snapshot;
        var fields = new FieldBuilder();
        fields.Add("business", "id", value.BusinessId);
        fields.Add("business", "stable-key", value.StableKey);
        fields.Add("business", "name", value.DisplayName);
        fields.Add("business", "kind", value.Kind);
        fields.Add("business", "status", value.Status);
        fields.Add("business", "zone", value.OperatingZone);
        fields.Add("authority", "party", value.BusinessParty);
        fields.Add("authority", "owner", value.Owner);
        fields.Add("authority", "operators", value.Operators.Count);
        fields.Add("economy", "account", value.AccountId);
        fields.Add("economy", "input-stock", value.InventoryStockNodeId);
        fields.Add("economy", "output-stock", value.OutputStockNodeId);
        fields.Add("runtime", "production-queue", value.ProductionQueue.Count);
        fields.Add("runtime", "active-shipments", value.ActiveShipments.Count);
        fields.Add("policy", "minimum-cash", value.Policy.MinimumCashReserve);
        fields.Add("policy", "restock-basis-points", value.Policy.RestockThresholdBasisPoints);
        fields.Add("persistent", "aggregate-version", record.AggregateVersion);
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectProductionJobAsync(string target)
    {
        if (!TryGuid(target, out var raw))
            return Fail("invalid-production-job-id");
        var record = await _production.GetAsync(new NCProductionJobId(raw), true);
        if (record == null)
            return Fail("production-job-not-found");
        var value = record.Snapshot;
        var fields = new FieldBuilder();
        fields.Add("job", "id", value.JobId);
        fields.Add("job", "business", value.BusinessId);
        fields.Add("job", "recipe", value.RecipeId);
        fields.Add("job", "runs", value.Runs);
        fields.Add("job", "status", value.Status);
        fields.Add("job", "inputs", value.Inputs.Count);
        fields.Add("job", "output-stock", value.OutputStockNodeId);
        fields.Add("job", "failure", value.FailureCode);
        fields.Add("time", "started", value.StartedAt.Elapsed);
        fields.Add("time", "due", value.DueAt.Elapsed);
        fields.Add("persistent", "aggregate-version", record.AggregateVersion);
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectShipmentAsync(string target)
    {
        if (!TryGuid(target, out var raw))
            return Fail("invalid-shipment-id");
        var record = await _shipments.GetAsync(new NCShipmentId(raw), true);
        if (record == null)
            return Fail("shipment-not-found");
        var value = record.Snapshot;
        var fields = new FieldBuilder();
        fields.Add("shipment", "id", value.ShipmentId);
        fields.Add("shipment", "status", value.Status);
        fields.Add("shipment", "shipper", value.Shipper);
        fields.Add("shipment", "recipient", value.Recipient);
        fields.Add("shipment", "carrier", value.Carrier);
        fields.Add("shipment", "handoff", value.HandoffKind);
        fields.Add("shipment", "assets", value.Assets.Count);
        fields.Add("stock", "origin", value.OriginStockNodeId);
        fields.Add("stock", "transit", value.TransitStockNodeId);
        fields.Add("stock", "destination", value.DestinationStockNodeId);
        fields.Add("route", "origin-zone", value.OriginZone);
        fields.Add("route", "destination-zone", value.DestinationZone);
        fields.Add("route", "travel-mode", value.TravelMode);
        fields.Add("route", "legs", value.Route.Count);
        fields.Add("route", "current-leg", value.CurrentLeg);
        fields.Add("route", "risk-basis-points", value.RiskBasisPoints);
        fields.Add("time", "expected-arrival", value.ExpectedArrivalAt?.Elapsed.ToString() ?? "none");
        fields.Add("proof", "delivery-event", value.ProofOfDeliveryEventId?.ToString("D") ?? "none");
        fields.Add("proof", "dispute", value.DisputeId?.ToString() ?? "none");
        fields.Add("persistent", "aggregate-version", record.AggregateVersion);
        return Ok(fields);
    }
}
