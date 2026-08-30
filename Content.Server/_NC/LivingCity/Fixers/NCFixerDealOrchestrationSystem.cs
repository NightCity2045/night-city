// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Market;
using Content.Server._NC.LivingCity.Social;
using Content.Shared._NC.LivingCity.Fixers;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Market;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Fixers;

/// <summary>
/// Validates referenced market/shipment aggregates and creates durable social obligations before a funded deal starts.
/// It never duplicates the state machines owned by those domains.
/// </summary>
public sealed partial class NCFixerDealOrchestrationSystem : EntitySystem
{
    [Dependency] private NCFixerRegistrySystem _fixers = default!;
    [Dependency] private NCEconomicAssetRegistrySystem _assets = default!;
    [Dependency] private NCMarketRegistrySystem _market = default!;
    [Dependency] private NCShipmentSystem _shipments = default!;
    [Dependency] private NCSocialObligationRegistrySystem _obligations = default!;
    [Dependency] private NCRumorRegistrySystem _rumors = default!;

    private readonly Dictionary<(NCFixerDealId DealId, Guid LegId), HashSet<EntityUid>> _handoffs = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCFixerHandoffComponent, ComponentStartup>(OnHandoffStartup);
        SubscribeLocalEvent<NCFixerHandoffComponent, ComponentShutdown>(OnHandoffShutdown);
    }

    public async Task<NCFixerMutationResult> ActivateAsync(NCFixerDealId dealId, long expectedVersion,
        NCPartyRef actor)
    {
        var deal = await _fixers.GetDealAsync(dealId, true);
        if (deal == null)
            return Reject(NCFixerMutationStatus.NotFound, "The fixer deal does not exist.");
        if (deal.AggregateVersion != expectedVersion)
            return Reject(NCFixerMutationStatus.VersionConflict, "The fixer deal version changed.", deal);
        if (deal.Snapshot.Status != NCFixerDealStatus.EscrowFunded || actor != deal.Snapshot.Fixer)
            return Reject(NCFixerMutationStatus.NotAuthorized,
                "Only the broker can activate a funded deal.", deal);

        foreach (var leg in deal.Snapshot.Plan.Legs)
        {
            if (leg.MarketRequestId is { } requestId)
            {
                var request = await _market.GetRequestAsync(requestId, true);
                var expectedKind = leg.Kind == NCFixerDealLegKind.Service
                    ? NCMarketRequestKind.Service
                    : leg.Kind == NCFixerDealLegKind.Information
                        ? NCMarketRequestKind.Information
                        : (NCMarketRequestKind?) null;
                if (request == null || expectedKind is { } kind && request.Snapshot.Kind != kind ||
                    request.Snapshot.Status is NCMarketRequestStatus.Cancelled or NCMarketRequestStatus.Expired)
                    return Reject(NCFixerMutationStatus.Rejected,
                        $"Deal leg {leg.LegId} references an unavailable market request.", deal);
            }
            if (leg.ShipmentId is { } shipmentId)
            {
                var shipment = await _shipments.GetAsync(shipmentId, true);
                if (shipment == null || shipment.Snapshot.Status is NCShipmentStatus.Lost or
                        NCShipmentStatus.Cancelled)
                    return Reject(NCFixerMutationStatus.Rejected,
                        $"Deal leg {leg.LegId} references an unavailable shipment.", deal);
            }
            if (leg.FulfillmentId is { } fulfillmentId)
            {
                var fulfillment = await _assets.GetFulfillmentAsync(fulfillmentId, true);
                var expectedKind = leg.Kind == NCFixerDealLegKind.Service
                    ? NCEconomicOfferKind.Service
                    : leg.Kind is NCFixerDealLegKind.Information or NCFixerDealLegKind.Access
                        ? NCEconomicOfferKind.InformationAccess
                        : (NCEconomicOfferKind?) null;
                if (fulfillment == null || expectedKind is not { } kind || fulfillment.Snapshot.OfferKind != kind ||
                    fulfillment.Snapshot.Provider != leg.From || fulfillment.Snapshot.Recipient != leg.To ||
                    fulfillment.Snapshot.Status is NCEconomicFulfillmentStatus.Failed or
                        NCEconomicFulfillmentStatus.Revoked)
                    return Reject(NCFixerMutationStatus.Rejected,
                        $"Deal leg {leg.LegId} references an incompatible fulfillment.", deal);
            }
            else if (leg.Kind == NCFixerDealLegKind.Information)
            {
                if (!Guid.TryParse(leg.DefinitionId, out var informationRaw) ||
                    (await _rumors.GetAsync(leg.From, true))?.Snapshot.Knowledge.Any(value =>
                        value.InformationId == new NCInformationId(informationRaw)) != true)
                    return Reject(NCFixerMutationStatus.Rejected,
                        $"Deal leg {leg.LegId} references information unknown to its provider.", deal);
            }
            else if (leg.Kind == NCFixerDealLegKind.Access)
            {
                var network = await _fixers.GetNetworkAsync(NCPersistentIdentity.FixerNetwork(leg.From), true);
                if (network?.Snapshot.Fixer != leg.From)
                    return Reject(NCFixerMutationStatus.Rejected,
                        $"Deal leg {leg.LegId} references no authoritative access provider.", deal);
            }
            if (leg.Kind == NCFixerDealLegKind.DeadDrop)
            {
                if (!_handoffs.TryGetValue((dealId, leg.LegId), out var handoffs) || handoffs.Count == 0)
                    return Reject(NCFixerMutationStatus.Rejected,
                        $"Deal leg {leg.LegId} has no materialized dead drop.", deal);
            }
            if (leg.Kind == NCFixerDealLegKind.Obligation &&
                !await EnsureObligationAsync(deal.Snapshot, leg))
                return Reject(NCFixerMutationStatus.StorageFailure,
                    $"Deal obligation {leg.LegId} could not be created.", deal);
        }

        var current = deal;
        foreach (var leg in deal.Snapshot.Plan.Legs)
        {
            var stable = $"{dealId}.{leg.LegId:D}.{current.AggregateVersion}";
            var advanced = await _fixers.AdvanceLegAsync(new NCAdvanceFixerDealLegRequest(
                NCPersistentIdentity.Command("fixer", "ready-leg", stable), dealId, current.AggregateVersion,
                leg.LegId, NCFixerDealLegStatus.Ready, null, actor,
                NCPersistentIdentity.Correlation("fixer", "ready-leg", stable),
                current.Snapshot.LastAppliedEventId, "fixer-deal-activation"));
            if (advanced.Deal == null || advanced.Status is not (NCFixerMutationStatus.Applied or
                    NCFixerMutationStatus.Duplicate))
                return advanced;
            current = advanced.Deal;

            // Ready means the referenced authorities were validated; InProgress records that execution actually
            // started. Completion cannot skip this state, which keeps recovery and breach diagnostics meaningful.
            stable = $"{dealId}.{leg.LegId:D}.{current.AggregateVersion}";
            advanced = await _fixers.AdvanceLegAsync(new NCAdvanceFixerDealLegRequest(
                NCPersistentIdentity.Command("fixer", "start-leg", stable), dealId, current.AggregateVersion,
                leg.LegId, NCFixerDealLegStatus.InProgress, null, actor,
                NCPersistentIdentity.Correlation("fixer", "start-leg", stable),
                current.Snapshot.LastAppliedEventId, "fixer-deal-activation"));
            if (advanced.Deal == null || advanced.Status is not (NCFixerMutationStatus.Applied or
                    NCFixerMutationStatus.Duplicate))
                return advanced;
            current = advanced.Deal;
        }
        var transitionStable = $"{dealId}.{current.AggregateVersion}";
        return await _fixers.ChangeDealAsync(new NCChangeFixerDealRequest(
            NCPersistentIdentity.Command("fixer", "start-deal", transitionStable), dealId,
            current.AggregateVersion, NCFixerDealStatus.InProgress, actor,
            NCPersistentIdentity.Correlation("fixer", "start-deal", transitionStable),
            current.Snapshot.LastAppliedEventId, "fixer-deal-activation"));
    }

    public IReadOnlyList<EntityUid> GetHandoffs(NCFixerDealId dealId, Guid legId) =>
        _handoffs.TryGetValue((dealId, legId), out var entities) ? entities.ToArray() : [];

    public async Task<NCFixerMutationResult> CompleteLegAsync(NCFixerDealId dealId, long expectedVersion,
        Guid legId, NCPartyRef actor, Guid proofWorldEventId)
    {
        var deal = await _fixers.GetDealAsync(dealId, true);
        if (deal == null)
            return Reject(NCFixerMutationStatus.NotFound, "The deal does not exist.");
        if (deal.AggregateVersion != expectedVersion)
            return Reject(NCFixerMutationStatus.VersionConflict, "The deal version changed.", deal);
        var leg = deal.Snapshot.Plan.Legs.FirstOrDefault(value => value.LegId == legId);
        if (leg == null || actor != leg.From && actor != deal.Snapshot.Fixer || proofWorldEventId == Guid.Empty)
            return Reject(NCFixerMutationStatus.NotAuthorized, "The deal leg completion is not authorized.", deal);
        if (!await IsDeliveredByAuthorityAsync(deal.Snapshot, leg))
            return Reject(NCFixerMutationStatus.Rejected,
                "The authoritative shipment, asset, request, obligation, or handoff is not complete.", deal);
        var stable = $"{dealId}.{legId:D}.{expectedVersion}";
        return await _fixers.AdvanceLegAsync(new NCAdvanceFixerDealLegRequest(
            NCPersistentIdentity.Command("fixer", "complete-leg", stable), dealId, expectedVersion, legId,
            NCFixerDealLegStatus.Delivered, proofWorldEventId, actor,
            NCPersistentIdentity.Correlation("fixer", "complete-leg", stable),
            deal.Snapshot.LastAppliedEventId, "fixer-authoritative-leg-completion"));
    }

    private async Task<bool> IsDeliveredByAuthorityAsync(NCFixerDealSnapshot deal, NCFixerDealLeg leg)
    {
        if (leg.Kind == NCFixerDealLegKind.Money)
            return deal.FundingTransactionId is { IsValid: true };
        if (leg.Kind == NCFixerDealLegKind.Commodity && Guid.TryParse(leg.DefinitionId, out var commodityRaw))
        {
            var batch = await _assets.GetCommodityBatchAsync(new NCCommodityBatchId(commodityRaw), true);
            return batch?.Snapshot.Owner == leg.To && batch.Snapshot.Quantity >= leg.Quantity;
        }
        if (leg.Kind == NCFixerDealLegKind.UniqueAsset && Guid.TryParse(leg.DefinitionId, out var itemRaw))
        {
            var item = await _assets.GetItemAsync(new NCItemId(itemRaw), true);
            return item?.Snapshot.Owner == leg.To;
        }
        if (leg.ShipmentId is { } shipmentId)
            return (await _shipments.GetAsync(shipmentId, true))?.Snapshot.Status == NCShipmentStatus.Delivered;
        if (leg.MarketRequestId is { } requestId)
            return (await _market.GetRequestAsync(requestId, true))?.Snapshot.Status ==
                NCMarketRequestStatus.Fulfilled;
        if (leg.ObligationId is { } obligationId)
            return (await _obligations.GetAsync(obligationId, true))?.Snapshot.Status ==
                NCSocialObligationStatus.Fulfilled;
        if (leg.FulfillmentId is { } fulfillmentId)
        {
            var fulfillment = await _assets.GetFulfillmentAsync(fulfillmentId, true);
            return leg.Kind == NCFixerDealLegKind.Service
                ? fulfillment?.Snapshot.Status == NCEconomicFulfillmentStatus.Completed
                : leg.Kind is NCFixerDealLegKind.Information or NCFixerDealLegKind.Access &&
                  fulfillment?.Snapshot.Status == NCEconomicFulfillmentStatus.Granted;
        }
        if (leg.Kind == NCFixerDealLegKind.DeadDrop)
        {
            if (!_handoffs.TryGetValue((deal.DealId, leg.LegId), out var entities) || entities.Count == 0 ||
                !leg.HandoffZone.IsValid || !Guid.TryParse(leg.DefinitionId, out var stockRaw))
                return false;
            var stock = await _assets.GetStockNodeAsync(new NCStockNodeId(stockRaw), true);
            // A physical marker alone is not delivery: the canonical stock must be at this drop and owned by the recipient.
            return stock?.Snapshot.Owner == leg.To &&
                   stock.Snapshot.SemanticLocationKey == leg.HandoffZone.ToString() &&
                   stock.Snapshot.CommodityBatchIds.Count + stock.Snapshot.ItemIds.Count > 0;
        }
        if (leg.Kind == NCFixerDealLegKind.Information && Guid.TryParse(leg.DefinitionId, out var informationRaw))
        {
            var book = await _rumors.GetAsync(leg.To, true);
            return book?.Snapshot.Knowledge.Any(value =>
                value.InformationId == new NCInformationId(informationRaw)) == true;
        }
        if (leg.Kind == NCFixerDealLegKind.Access)
            return await _fixers.HasAccessAsync(leg.From, leg.To, true);
        return false;
    }

    private async Task<bool> EnsureObligationAsync(NCFixerDealSnapshot deal, NCFixerDealLeg leg)
    {
        var obligationId = leg.ObligationId ?? NCPersistentIdentity.SocialObligation(deal.DealId, leg.LegId);
        var existing = await _obligations.GetAsync(obligationId, true);
        if (existing != null)
            return existing.Snapshot.Debtor == leg.From && existing.Snapshot.Creditor == leg.To;
        var worldEventId = NCPersistentIdentity.Command("fixer", "obligation-event",
            $"{deal.DealId}.{leg.LegId:D}");
        var result = await _obligations.CreateAsync(new NCCreateSocialObligationRequest(
            NCPersistentIdentity.Command("fixer", "create-obligation", $"{deal.DealId}.{leg.LegId:D}"),
            obligationId, leg.ObligationKind ?? NCSocialObligationKind.Promise, leg.From, leg.To,
            new NCSocialObligationTerms(leg.DefinitionId, Math.Max(1, leg.Quantity), "deal-leg",
                new NCInformationSubject(NCInformationSubjectKind.StableKey, deal.DealId.ToString())),
            NCMemoryPrivacy.Restricted, worldEventId, deal.ReservationExpiresAt, deal.Fixer,
            NCPersistentIdentity.Correlation("fixer", "create-obligation", $"{deal.DealId}.{leg.LegId:D}"),
            deal.LastAppliedEventId, "fixer-deal-obligation"));
        return result.Status is NCSocialObligationMutationStatus.Applied or
            NCSocialObligationMutationStatus.Duplicate;
    }

    private void OnHandoffStartup(Entity<NCFixerHandoffComponent> entity, ref ComponentStartup args)
    {
        if (!entity.Comp.DealId.IsValid || entity.Comp.LegId == Guid.Empty)
            return;
        var key = (entity.Comp.DealId, entity.Comp.LegId);
        if (!_handoffs.TryGetValue(key, out var entities))
            _handoffs[key] = entities = [];
        entities.Add(entity.Owner);
    }

    private void OnHandoffShutdown(Entity<NCFixerHandoffComponent> entity, ref ComponentShutdown args)
    {
        var key = (entity.Comp.DealId, entity.Comp.LegId);
        if (!_handoffs.TryGetValue(key, out var entities))
            return;
        entities.Remove(entity.Owner);
        if (entities.Count == 0)
            _handoffs.Remove(key);
    }

    private static NCFixerMutationResult Reject(NCFixerMutationStatus status, string error,
        NCFixerDealRecord? deal = null) => new(status, null, deal, null, error);
}
