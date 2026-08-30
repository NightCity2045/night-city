// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Market;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Fixers;
using Content.Shared._NC.LivingCity.Market;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Fixers;

/// <summary>
/// Publishes real, prototype-backed Fixer services and turns an Agent selection into the existing market request,
/// quote, deal, escrow, and fulfillment aggregates. It never accepts a client-authored price or contract plan.
/// </summary>
public sealed partial class NCFixerDealBoardSystem : EntitySystem
{
    private static readonly ProtoId<NCFixerPolicyPrototype> DefaultPolicy = "Default";
    private static readonly ProtoId<NCNightMarketPolicyPrototype> DefaultNightMarketPolicy = "Default";
    private static readonly ProtoId<NCEconomicPricingPolicyPrototype> DefaultPricingPolicy = "Default";
    private static readonly NCEconomicPriceContext NeutralPriceContext = new(0, 0, 0, 0, 0, 0);

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCMarketRegistrySystem _market = default!;
    [Dependency] private NCFixerRegistrySystem _fixers = default!;
    [Dependency] private NCFixerDealOrchestrationSystem _orchestration = default!;
    [Dependency] private NCFixerEscrowSystem _escrow = default!;

    private readonly HashSet<Task> _inFlight = [];
    private readonly ConcurrentDictionary<NCFixerDealId, byte> _reconciling = [];

    public int InFlightCount => _inFlight.Count;
    public long PublishedListings { get; private set; }
    public long CreatedDeals { get; private set; }
    public long FailedOperations { get; private set; }
    public string? LastError { get; private set; }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCFixerAggregateChangedEvent>(OnFixerChanged);
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnSessionClosing);
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        foreach (var network in _fixers.GetCachedNetworks())
            Track(PublishNetworkOffersAsync(network));
    }

    private void OnSessionClosing(NCActiveWorldSessionClosingEvent args)
    {
        if (_inFlight.Count > 0)
            args.AddPendingWrite(Task.WhenAll(_inFlight.ToArray()));
    }

    private void OnFixerChanged(NCFixerAggregateChangedEvent args)
    {
        if (!Guid.TryParse(args.AggregateId, out var raw))
            return;
        if (args.AggregateType == NCFixerRegistrySystem.NetworkAggregateType)
            Track(PublishNetworkAsync(new NCFixerNetworkId(raw)));
        else if (args.AggregateType == NCFixerRegistrySystem.DealAggregateType)
            Track(ReconcileDealResourcesAsync(new NCFixerDealId(raw)));
    }

    private async Task PublishNetworkAsync(NCFixerNetworkId networkId)
    {
        var network = await _fixers.GetNetworkAsync(networkId, true);
        if (network != null)
            await PublishNetworkOffersAsync(network);
    }

    private async Task PublishNetworkOffersAsync(NCFixerNetworkRecord network)
    {
        if (!_clock.TryGetCurrentTime(out var epoch, out _) ||
            !_prototypes.TryIndex(DefaultPolicy, out NCFixerPolicyPrototype? policy))
            return;

        foreach (var offerId in policy.DealBoardServiceOffers)
        {
            var stable = $"{epoch}.{network.Snapshot.NetworkId}.{offerId.Id}";
            var listingId = new NCMarketListingId(
                NCPersistentIdentity.Command("fixer-board", "listing-id", stable));
            var result = await _market.CreateListingAsync(new NCCreateMarketListingRequest(
                NCPersistentIdentity.Command("fixer-board", "publish", stable),
                listingId,
                NCEconomicOfferKind.Service,
                offerId.Id,
                1,
                network.Snapshot.Fixer,
                null,
                NCMarketChannel.Broker,
                new NCMarketAccessPolicy(NCMarketVisibility.Public, [], string.Empty, 0, 0),
                policy.DealBoardListingLifetime,
                network.Snapshot.Fixer,
                NCPersistentIdentity.Correlation("fixer-board", "publish", stable),
                network.Snapshot.LastAppliedEventId,
                "fixer-deal-board"));
            if (result.Status is NCMarketMutationStatus.Applied or NCMarketMutationStatus.Duplicate)
                PublishedListings++;
            else
                Fail(result.Error ?? "A Fixer service listing could not be published.");
        }
    }

    public async Task<NCFixerDealBoardResult> CreateServiceDealAsync(
        NCMarketListingId listingId,
        long expectedVersion,
        NCPartyRef buyer)
    {
        if (!buyer.IsValid || expectedVersion <= 0 ||
            !_prototypes.TryIndex(DefaultPolicy, out NCFixerPolicyPrototype? policy))
            return Reject("nc-agent-city-result-invalid");

        var listing = await _market.GetListingAsync(listingId, true);
        if (listing == null || listing.AggregateVersion != expectedVersion ||
            listing.Snapshot.Status != NCMarketListingStatus.Open ||
            listing.Snapshot.Channel != NCMarketChannel.Broker ||
            listing.Snapshot.OfferKind != NCEconomicOfferKind.Service ||
            !_prototypes.TryIndex(new ProtoId<NCEconomicServicePrototype>(listing.Snapshot.OfferId),
                out var service))
            return Reject("nc-agent-city-result-listing-changed");

        var fixer = listing.Snapshot.Seller;
        var networkId = NCPersistentIdentity.FixerNetwork(fixer);
        if (!await _fixers.HasAccessAsync(fixer, buyer, true))
            return Reject("nc-agent-city-result-no-fixer-access");

        var stable = $"{listingId}.{expectedVersion}.{buyer}";
        var correlation = NCPersistentIdentity.Correlation("fixer-board", "deal", stable);
        var quote = await _market.CreateQuoteAsync(new NCQuoteMarketListingRequest(
            NCPersistentIdentity.Command("fixer-board", "quote", stable),
            new NCEconomicQuoteId(NCPersistentIdentity.Command("fixer-board", "quote-id", stable)),
            listingId,
            expectedVersion,
            1,
            new NCMarketViewerContext(buyer, new HashSet<string> { "public" }, 0, 0),
            NeutralPriceContext,
            policy.QuoteLifetime,
            correlation,
            listing.Snapshot.LastAppliedEventId,
            "fixer-deal-board"));
        if (quote.Quote == null)
            return Reject(quote.Error ?? "nc-agent-city-result-quote-failed");

        var requestId = new NCMarketRequestId(
            NCPersistentIdentity.Command("fixer-board", "request-id", stable));
        var allowed = new[] { fixer, buyer }.Distinct().ToArray();
        var request = await _market.CreateRequestAsync(new NCCreateMarketRequestRequest(
            NCPersistentIdentity.Command("fixer-board", "request", stable),
            requestId,
            NCMarketRequestKind.Service,
            listing.Snapshot.OfferId,
            1,
            quote.Quote.Snapshot.Price.Total,
            0,
            buyer,
            NCMarketChannel.Broker,
            new NCMarketAccessPolicy(NCMarketVisibility.Private, allowed, string.Empty, 0, 0),
            default,
            policy.QuoteLifetime,
            buyer,
            correlation,
            quote.Quote.Snapshot.LastAppliedEventId,
            "fixer-deal-board"));
        if (request.Request == null)
            return Reject(request.Error ?? "nc-agent-city-result-request-failed");

        request = await _market.ChangeRequestStatusAsync(new NCChangeMarketRequestStatusRequest(
            NCPersistentIdentity.Command("fixer-board", "match-request", stable),
            requestId,
            request.Request.AggregateVersion,
            NCMarketRequestStatus.Matched,
            listingId,
            buyer,
            correlation,
            request.Request.Snapshot.LastAppliedEventId,
            "fixer-deal-board"));
        if (request.Request == null)
            return Reject(request.Error ?? "nc-agent-city-result-request-failed");

        var reserved = await _market.ChangeListingStatusAsync(new NCChangeMarketListingStatusRequest(
            NCPersistentIdentity.Command("fixer-board", "reserve-listing", stable),
            listingId,
            expectedVersion,
            NCMarketListingStatus.Reserved,
            fixer,
            correlation,
            listing.Snapshot.LastAppliedEventId,
            "fixer-deal-board"));
        if (reserved.Listing == null)
            return Reject(reserved.Error ?? "nc-agent-city-result-listing-changed");

        try
        {
            var quotedAmount = quote.Quote.Snapshot.Price.Total;
            var brokerFee = checked((long) Math.Ceiling((decimal) quotedAmount *
                policy.DealBoardBrokerFeeBasisPoints / 10_000m));
            var escrowAmount = checked(quotedAmount + brokerFee);
            var moneyLeg = new NCFixerDealLeg(Guid.NewGuid(), NCFixerDealLegKind.Money, buyer, fixer,
                "brokered-service-payment", 1, escrowAmount, default, null, null, null, null, null, null,
                NCFixerDealLegStatus.Planned, null);
            var serviceLeg = new NCFixerDealLeg(Guid.NewGuid(), NCFixerDealLegKind.Service, fixer, buyer,
                listing.Snapshot.OfferId, 1, 0, default, null, requestId, null, null, null, null,
                NCFixerDealLegStatus.Planned, null);
            var dealId = new NCFixerDealId(
                NCPersistentIdentity.Command("fixer-board", "deal-id", stable));
            var created = await _fixers.CreateDealAsync(new NCCreateFixerDealRequest(
                NCPersistentIdentity.Command("fixer-board", "create-deal", stable),
                dealId,
                networkId,
                fixer,
                buyer,
                fixer,
                new NCFixerDealPlan([moneyLeg, serviceLeg], escrowAmount,
                    policy.DealBoardBrokerFeeBasisPoints, 0, false,
                    service.ExpectedDuration > TimeSpan.Zero ? service.ExpectedDuration : policy.DefaultExecutionDuration,
                    null),
                quotedAmount,
                correlation,
                reserved.Listing.Snapshot.LastAppliedEventId,
                "fixer-deal-board"));
            if (created.Deal == null)
                return await CompensateAsync(listingId, reserved.Listing, request.Request,
                    fixer, buyer, correlation, created.Error ?? "nc-agent-city-result-deal-failed");

            var quoted = await ChangeAsync(created.Deal, NCFixerDealStatus.Quoted, fixer, correlation,
                "quote-deal");
            if (quoted.Deal == null)
                return Reject(quoted.Error ?? "nc-agent-city-result-deal-failed");
            var final = await ChangeAsync(quoted.Deal, NCFixerDealStatus.Reserved, buyer, correlation,
                "reserve-deal");
            if (final.Deal == null)
                return Reject(final.Error ?? "nc-agent-city-result-deal-failed");

            CreatedDeals++;
            LastError = null;
            return new NCFixerDealBoardResult(final.Deal, "nc-agent-city-result-deal-reserved", null);
        }
        catch (OverflowException)
        {
            return await CompensateAsync(listingId, reserved.Listing, request.Request,
                fixer, buyer, correlation, "nc-agent-city-result-price-overflow");
        }
    }

    public async Task<NCFixerDealBoardResult> ProgressServiceDealAsync(NCFixerDealRecord deal, NCPartyRef fixer)
    {
        if (deal.Snapshot.Status != NCFixerDealStatus.InProgress || deal.Snapshot.Fixer != fixer ||
            deal.Snapshot.Seller != fixer)
            return Reject("fixer-deal-not-actionable");

        var current = deal;
        var serviceLeg = current.Snapshot.Plan.Legs.FirstOrDefault(value =>
            value.Kind == NCFixerDealLegKind.Service && value.MarketRequestId.HasValue);
        if (serviceLeg?.MarketRequestId is not { } requestId)
            return Reject("fixer-service-request-missing");
        var request = await _market.GetRequestAsync(requestId, true);
        if (request == null || request.Snapshot.MatchedListingId is not { } listingId)
            return Reject("fixer-service-request-missing");
        if (request.Snapshot.Status == NCMarketRequestStatus.Matched)
        {
            var fulfilled = await _market.ChangeRequestStatusAsync(new NCChangeMarketRequestStatusRequest(
                Guid.NewGuid(), requestId, request.AggregateVersion, NCMarketRequestStatus.Fulfilled, listingId,
                fixer, Guid.NewGuid(), request.Snapshot.LastAppliedEventId, "fixer-service-fulfillment"));
            if (fulfilled.Request == null)
                return Reject(fulfilled.Error ?? "fixer-service-request-failed");
        }

        foreach (var leg in current.Snapshot.Plan.Legs.Where(value =>
                     value.Status == NCFixerDealLegStatus.InProgress))
        {
            var proof = NCPersistentIdentity.Command("fixer-board", "leg-proof",
                $"{current.Snapshot.DealId}.{leg.LegId:D}");
            var completed = await _orchestration.CompleteLegAsync(current.Snapshot.DealId,
                current.AggregateVersion, leg.LegId, fixer, proof);
            if (completed.Deal == null)
                return Reject(completed.Error ?? "fixer-deal-leg-failed");
            current = completed.Deal;
        }
        if (current.Snapshot.Status != NCFixerDealStatus.AwaitingHandoff)
            return Reject("fixer-deal-handoff-not-ready");

        var settled = await _escrow.SettleAsync(current.Snapshot.DealId, current.AggregateVersion, fixer);
        if (settled.Deal == null)
            return Reject(settled.Error ?? "fixer-deal-settlement-failed");
        await ReconcileDealResourcesAsync(settled.Deal);
        return new NCFixerDealBoardResult(settled.Deal, "nc-agent-city-result-deal-complete", null);
    }

    public async Task<NCFixerDealBoardResult> CancelServiceDealAsync(NCFixerDealRecord deal, NCPartyRef buyer)
    {
        if (deal.Snapshot.Buyer != buyer || deal.Snapshot.Status is not (NCFixerDealStatus.Offered or
                NCFixerDealStatus.Quoted or NCFixerDealStatus.Reserved))
            return Reject("nc-agent-city-result-deal-changed");
        var cancelled = await ChangeAsync(deal, NCFixerDealStatus.Cancelled, buyer, Guid.NewGuid(), "cancel-deal");
        if (cancelled.Deal == null)
            return Reject(cancelled.Error ?? "nc-agent-city-result-cancel-failed");
        await ReconcileDealResourcesAsync(cancelled.Deal);
        return new NCFixerDealBoardResult(cancelled.Deal, "nc-agent-city-result-cancelled", null);
    }

    public async Task<bool> ReconcileDealResourcesAsync(NCFixerDealRecord deal)
    {
        if (!_reconciling.TryAdd(deal.Snapshot.DealId, 0))
            return true;
        try
        {
            return await ReconcileDealResourcesCoreAsync(deal);
        }
        finally
        {
            _reconciling.TryRemove(deal.Snapshot.DealId, out _);
        }
    }

    private async Task<bool> ReconcileDealResourcesCoreAsync(NCFixerDealRecord deal)
    {
        var serviceLeg = deal.Snapshot.Plan.Legs.FirstOrDefault(value =>
            value.Kind == NCFixerDealLegKind.Service && value.MarketRequestId.HasValue);
        if (serviceLeg?.MarketRequestId is not { } requestId || deal.Snapshot.Status is not
                (NCFixerDealStatus.Fulfilled or NCFixerDealStatus.Cancelled or NCFixerDealStatus.Expired or
                 NCFixerDealStatus.Breached))
            return true;

        var request = await _market.GetRequestAsync(requestId, true);
        if (request == null)
            return false;
        var listingId = request.Snapshot.MatchedListingId;
        var successful = deal.Snapshot.Status == NCFixerDealStatus.Fulfilled;
        var requestStatus = successful ? NCMarketRequestStatus.Fulfilled : NCMarketRequestStatus.Cancelled;
        if (request.Snapshot.Status is NCMarketRequestStatus.Open or NCMarketRequestStatus.Matched)
        {
            var stable = $"{deal.Snapshot.DealId}.{requestStatus}";
            var changed = await _market.ChangeRequestStatusAsync(new NCChangeMarketRequestStatusRequest(
                NCPersistentIdentity.Command("fixer-board", "reconcile-request", stable),
                requestId,
                request.AggregateVersion,
                requestStatus,
                successful ? listingId : null,
                successful ? deal.Snapshot.Seller : request.Snapshot.Requester,
                NCPersistentIdentity.Correlation("fixer-board", "reconcile-request", stable),
                deal.Snapshot.LastAppliedEventId,
                "fixer-board-recovery"));
            if (changed.Request == null)
            {
                Fail(changed.Error ?? "A terminal Fixer deal could not reconcile its market request.");
                return false;
            }
            request = changed.Request;
        }

        if (listingId is not { } matchedListingId)
            return true;
        var listing = await _market.GetListingAsync(matchedListingId, true);
        if (listing?.Snapshot.Status != NCMarketListingStatus.Reserved)
            return true;
        var listingStatus = successful ? NCMarketListingStatus.Fulfilled : NCMarketListingStatus.Open;
        var listingStable = $"{deal.Snapshot.DealId}.{listingStatus}";
        var listingResult = await _market.ChangeListingStatusAsync(new NCChangeMarketListingStatusRequest(
            NCPersistentIdentity.Command("fixer-board", "reconcile-listing", listingStable),
            matchedListingId,
            listing.AggregateVersion,
            listingStatus,
            listing.Snapshot.Seller,
            NCPersistentIdentity.Correlation("fixer-board", "reconcile-listing", listingStable),
            request.Snapshot.LastAppliedEventId,
            "fixer-board-recovery"));
        if (listingResult.Listing != null)
            return true;
        Fail(listingResult.Error ?? "A terminal Fixer deal could not reconcile its service listing.");
        return false;
    }

    private async Task ReconcileDealResourcesAsync(NCFixerDealId dealId)
    {
        var deal = await _fixers.GetDealAsync(dealId, true);
        if (deal != null)
            await ReconcileDealResourcesAsync(deal);
    }

    /// <summary>
    /// Publishes one server-priced unique-item demand signal for a known Fixer contact. The request does not create,
    /// reserve, or promise an item; it only gives the Night Market director authoritative demand to react to.
    /// </summary>
    public async Task<NCFixerDealBoardRequestResult> CreateNightMarketItemDemandAsync(
        ProtoId<NCItemDefinitionPrototype> definitionId,
        NCPartyRef buyer)
    {
        if (!buyer.IsValid || !_clock.TryGetCurrentTime(out var epoch, out _) ||
            !_prototypes.TryIndex(DefaultNightMarketPolicy, out NCNightMarketPolicyPrototype? marketPolicy) ||
            !_prototypes.TryIndex(DefaultPricingPolicy, out NCEconomicPricingPolicyPrototype? pricingPolicy) ||
            !marketPolicy.AutomaticVendorItems.Contains(definitionId) ||
            !_prototypes.TryIndex(definitionId, out NCItemDefinitionPrototype? definition) ||
            definition.BasePrice is not { } basePrice || basePrice < 0)
            return new(string.Empty, "nc-agent-city-result-invalid");

        NCFixerNetworkRecord? network = null;
        foreach (var candidate in _fixers.GetCachedNetworks().OrderBy(value => value.Snapshot.NetworkId.Value))
        {
            if (!await _fixers.HasAccessAsync(candidate.Snapshot.Fixer, buyer, true))
                continue;
            network = candidate;
            break;
        }
        if (network == null)
            return new(string.Empty, "nc-agent-city-result-no-fixer-access");

        var existing = await _market.FindOpenRequestAsync(buyer, NCMarketRequestKind.UniqueItem, definitionId.Id);
        if (existing != null)
            return new("nc-agent-city-result-item-demand-pending", null);

        try
        {
            var maximumPrice = checked((long) Math.Ceiling((decimal) basePrice *
                pricingPolicy.MaximumMultiplierBasisPoints / 10_000m));
            var stable = $"{epoch}.{buyer}.{definitionId.Id}";
            var requestId = NCPersistentIdentity.MarketRequest(buyer, $"night-market.{definition.StableKey}.{epoch}");
            var lifetime = marketPolicy.AutomaticOpenDelay + marketPolicy.AutomaticDuration +
                           marketPolicy.MinimumAssemblyLead;
            var result = await _market.CreateRequestAsync(new NCCreateMarketRequestRequest(
                NCPersistentIdentity.Command("fixer-board", "item-demand", stable),
                requestId,
                NCMarketRequestKind.UniqueItem,
                definitionId.Id,
                1,
                maximumPrice,
                0,
                buyer,
                NCMarketChannel.Broker,
                new NCMarketAccessPolicy(NCMarketVisibility.Private,
                    [buyer, network.Snapshot.Fixer], string.Empty, 0, 0),
                default,
                lifetime,
                buyer,
                NCPersistentIdentity.Correlation("fixer-board", "item-demand", stable),
                network.Snapshot.LastAppliedEventId,
                "fixer-deal-board-item-demand"));
            return result.Request == null
                ? new(string.Empty, "nc-agent-city-result-item-demand-failed")
                : new("nc-agent-city-result-item-demand-created", null);
        }
        catch (OverflowException)
        {
            return new(string.Empty, "nc-agent-city-result-price-overflow");
        }
    }

    private Task<NCFixerMutationResult> ChangeAsync(NCFixerDealRecord deal, NCFixerDealStatus status,
        NCPartyRef actor, Guid correlation, string operation) => _fixers.ChangeDealAsync(new NCChangeFixerDealRequest(
        Guid.NewGuid(), deal.Snapshot.DealId, deal.AggregateVersion, status, actor, correlation,
        deal.Snapshot.LastAppliedEventId, $"fixer-board-{operation}"));

    private async Task<NCFixerDealBoardResult> CompensateAsync(NCMarketListingId listingId,
        NCMarketListingRecord listing, NCMarketRequestRecord request, NCPartyRef fixer, NCPartyRef buyer,
        Guid correlation, string error)
    {
        await _market.ChangeListingStatusAsync(new NCChangeMarketListingStatusRequest(Guid.NewGuid(), listingId,
            listing.AggregateVersion, NCMarketListingStatus.Open, fixer, correlation,
            listing.Snapshot.LastAppliedEventId, "fixer-board-compensation"));
        await _market.ChangeRequestStatusAsync(new NCChangeMarketRequestStatusRequest(Guid.NewGuid(),
            request.Snapshot.RequestId, request.AggregateVersion, NCMarketRequestStatus.Cancelled, null, buyer,
            correlation, request.Snapshot.LastAppliedEventId, "fixer-board-compensation"));
        return Reject(error);
    }

    private void Track(Task task)
    {
        _inFlight.Add(task);
        _ = CompleteTrackedAsync(task);
    }

    private async Task CompleteTrackedAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception exception)
        {
            Fail(exception.Message);
        }
        finally
        {
            _inFlight.Remove(task);
        }
    }

    private void Fail(string error)
    {
        FailedOperations++;
        LastError = error;
        Log.Error($"Fixer Deal Board: {error}");
    }

    private NCFixerDealBoardResult Reject(string error)
    {
        return new NCFixerDealBoardResult(null, string.Empty, error);
    }
}

public sealed record NCFixerDealBoardResult(NCFixerDealRecord? Deal, string Notice, string? Error);

public sealed record NCFixerDealBoardRequestResult(string Notice, string? Error);
