// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.CityServices;
using Content.Server._NC.LivingCity.Fixers;
using Content.Server._NC.LivingCity.Housing;
using Content.Server._NC.LivingCity.Market;
using Content.Server._NC.LivingCity.Social;
using Content.Shared._NC.Agent.Components;
using Content.Shared._NC.Agent.UI;
using Content.Shared._NC.Identity;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Fixers;
using Content.Shared._NC.LivingCity.Market;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.PDA;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Agent;

/// <summary>
/// Permission-filtered Agent city projection and its small server-authoritative action boundary. Hidden stock,
/// beliefs, evidence, prices, and contract plans never cross to the client.
/// </summary>
public sealed partial class NCAgentSystem
{
    private static readonly ProtoId<NCNightMarketPolicyPrototype> AgentNightMarketPolicy = "Default";

    [Dependency] private NCMarketRegistrySystem _cityMarket = default!;
    [Dependency] private NCPropertyMarketRegistrySystem _propertyMarket = default!;
    [Dependency] private NCFixerRegistrySystem _fixers = default!;
    [Dependency] private NCFixerDealBoardSystem _dealBoard = default!;
    [Dependency] private NCFixerEscrowSystem _fixerEscrow = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _actorRuntime = default!;
    [Dependency] private NCSocialObligationRegistrySystem _obligations = default!;
    [Dependency] private NCCityNewsRegistrySystem _cityNews = default!;

    private void InitializeCity()
    {
        SubscribeLocalEvent<NCAgentComponent, NCAgentCityActionMessage>(OnCityAction);
    }

    private async Task SendCitySnapshotAsync(EntityUid agent, EntityUid user, NCCharacterId owner,
        string notice = "")
    {
        if (Deleted(agent) || Deleted(user) || !_views.ContainsKey((agent, user)))
            return;

        var party = NCPartyRef.ForCharacter(owner);
        var rows = new List<NCAgentCityEntry>();
        var market = await _cityMarket.DiscoverAsync(new NCMarketDiscoveryRequest(null, 24, string.Empty, null,
            null, new NCMarketViewerContext(party, new HashSet<string> { "public" }, 0, 0)));
        foreach (var value in market.Listings)
        {
            var brokerable = value.Snapshot.Channel == NCMarketChannel.Broker &&
                             value.Snapshot.OfferKind == NCEconomicOfferKind.Service &&
                             await _fixers.HasAccessAsync(value.Snapshot.Seller, party);
            if (value.Snapshot.Channel == NCMarketChannel.Broker &&
                value.Snapshot.OfferKind == NCEconomicOfferKind.Service && !brokerable)
                continue;
            NCEconomicServicePrototype? service = null;
            var knownService = value.Snapshot.OfferKind == NCEconomicOfferKind.Service &&
                               _prototypes.TryIndex(
                                   new ProtoId<NCEconomicServicePrototype>(value.Snapshot.OfferId),
                                   out service);
            rows.Add(new NCAgentCityEntry(NCAgentCityArea.Market, value.Snapshot.ListingId.ToString(),
                knownService ? service!.DisplayName.Id : value.Snapshot.OfferId,
                knownService ? "nc-agent-city-market-service-details" :
                    $"{value.Snapshot.Channel} // {value.Snapshot.OfferKind}",
                value.AggregateVersion,
                brokerable ? NCAgentCityAction.BrokerListing : NCAgentCityAction.None));
        }
        foreach (var value in _fixers.GetCachedNightMarkets().Where(value => value.Snapshot.Status is
                     NCNightMarketStatus.Scheduled or NCNightMarketStatus.Assembling or NCNightMarketStatus.Open))
        {
            if (!await _fixers.HasAccessAsync(value.Snapshot.Organizer, party))
                continue;
            rows.Add(new NCAgentCityEntry(NCAgentCityArea.Market, value.Snapshot.NightMarketId.ToString(),
                "nc-agent-city-night-market-title",
                $"nc-agent-city-night-market-details-{value.Snapshot.Status.ToString().ToLowerInvariant()}",
                value.AggregateVersion, NCAgentCityAction.None,
                [value.Snapshot.VenueZone.ToString(), value.Snapshot.Vendors.Count.ToString()]));
        }

        var hasFixerAccess = false;
        foreach (var network in _fixers.GetCachedNetworks())
        {
            if (!await _fixers.HasAccessAsync(network.Snapshot.Fixer, party))
                continue;
            hasFixerAccess = true;
            break;
        }
        if (hasFixerAccess &&
            _prototypes.TryIndex(AgentNightMarketPolicy, out NCNightMarketPolicyPrototype? nightMarketPolicy))
        {
            foreach (var definitionId in nightMarketPolicy.AutomaticVendorItems)
            {
                if (!_prototypes.TryIndex(definitionId, out NCItemDefinitionPrototype? definition))
                    continue;
                var pending = await _cityMarket.FindOpenRequestAsync(
                    party, NCMarketRequestKind.UniqueItem, definitionId.Id) != null;
                rows.Add(new NCAgentCityEntry(NCAgentCityArea.Market, definitionId.Id,
                    definition.DisplayName.Id,
                    pending
                        ? "nc-agent-city-night-market-demand-pending"
                        : "nc-agent-city-night-market-demand-available",
                    0,
                    pending ? NCAgentCityAction.None : NCAgentCityAction.RequestNightMarketItem));
            }
        }

        foreach (var (actorId, entity) in FindNearbyFixers(user))
        {
            var fixer = NCPartyRef.ForActor(actorId);
            var access = await _fixers.HasAccessAsync(fixer, party);
            rows.Add(new NCAgentCityEntry(NCAgentCityArea.Fixer, actorId.ToString(), MetaData(entity).EntityName,
                access ? "nc-agent-city-fixer-access-active" : "nc-agent-city-fixer-access-nearby",
                0, access ? NCAgentCityAction.None : NCAgentCityAction.RequestFixerAccess));
        }

        var properties = await _propertyMarket.DiscoverAsync(null, 24);
        rows.AddRange(properties.Listings.Select(value => new NCAgentCityEntry(NCAgentCityArea.Property,
            value.Snapshot.ListingId.ToString(), value.Snapshot.OfferKind.ToString(),
            $"PROPERTY {value.Snapshot.PropertyId} // {value.Snapshot.Price}",
            value.AggregateVersion, NCAgentCityAction.None)));
        rows.AddRange(_fixers.GetCachedDealsForParty(party, 24).Select(value => new NCAgentCityEntry(
            NCAgentCityArea.Fixer, value.Snapshot.DealId.ToString(),
            $"nc-agent-city-deal-status-{value.Snapshot.Status.ToString().ToLowerInvariant()}",
            "nc-agent-city-deal-details", value.AggregateVersion, DealAction(value.Snapshot, party),
            [value.Snapshot.Plan.Legs.Count.ToString(), value.Snapshot.Plan.EscrowAmount.ToString()])));
        var obligations = await _obligations.GetForPartyAsync(party, 24);
        rows.AddRange(obligations.Select(value => new NCAgentCityEntry(NCAgentCityArea.Obligations,
            value.Snapshot.ObligationId.ToString(), value.Snapshot.Kind.ToString(),
            value.Snapshot.Status.ToString(), value.AggregateVersion, NCAgentCityAction.None)));
        var news = await _cityNews.GetPageAsync(null, 32);
        rows.AddRange(news.Where(value => !value.Snapshot.Retracted).Select(value => new NCAgentCityEntry(
            NCAgentCityArea.News, value.Snapshot.NewsId.ToString(), value.Snapshot.HeadlineKey,
            $"{value.Snapshot.Kind} // SEVERITY {value.Snapshot.Severity}", value.AggregateVersion,
            NCAgentCityAction.None)));
        _ui.ServerSendUiMessage(agent, PdaUiKey.Key, new NCAgentCityUpdateMessage(rows,
            string.IsNullOrEmpty(notice) ? market.Error ?? properties.Error ?? string.Empty : notice), user);
    }

    private async void OnCityAction(EntityUid uid, NCAgentComponent component, NCAgentCityActionMessage args)
    {
        if (!Enum.IsDefined(args.Action) || args.Action == NCAgentCityAction.None ||
            !TryGetView(uid, args.Actor, out var user, out var view) ||
            view.ActiveApp != NCAgentApp.City || !_processing.Add((uid, user)))
            return;

        var notice = "nc-agent-city-result-invalid";
        try
        {
            var party = NCPartyRef.ForCharacter(view.Owner);
            notice = args.Action switch
            {
                NCAgentCityAction.RequestFixerAccess => await RequestFixerAccessAsync(user, party, args.Id),
                NCAgentCityAction.BrokerListing => await CreateBrokerDealAsync(party, args.Id,
                    args.ExpectedVersion),
                NCAgentCityAction.RequestNightMarketItem => await RequestNightMarketItemAsync(party, args.Id),
                NCAgentCityAction.FundDeal => await FundDealAsync(party, args.Id, args.ExpectedVersion),
                NCAgentCityAction.CancelDeal => await CancelDealAsync(party, args.Id, args.ExpectedVersion),
                NCAgentCityAction.OpenDispute => await OpenDisputeAsync(party, args.Id, args.ExpectedVersion),
                _ => notice,
            };
        }
        catch (Exception exception)
        {
            Log.Error($"Agent city action failed: {exception}");
            notice = "nc-agent-city-result-failed";
        }
        finally
        {
            _processing.Remove((uid, user));
            await SendCitySnapshotAsync(uid, user, view.Owner, notice);
        }
    }

    private async Task<string> RequestFixerAccessAsync(EntityUid user, NCPartyRef client, string actorId)
    {
        if (!Guid.TryParse(actorId, out var raw) ||
            !TryFindNearbyFixer(user, new NCActorId(raw), out var fixer))
            return "nc-agent-city-result-fixer-not-nearby";
        if (await _fixers.HasAccessAsync(fixer, client, true))
            return "nc-agent-city-result-access-active";
        var result = await _fixers.GrantAccessAsync(new NCGrantFixerAccessRequest(
            Guid.NewGuid(), fixer, client, fixer, 0, 0, string.Empty, [], null,
            "physical-agent-introduction", Guid.NewGuid(), AuditSource: "agent-fixer-introduction"));
        return result.Network == null
            ? "nc-agent-city-result-access-denied"
            : "nc-agent-city-result-access-granted";
    }

    private async Task<string> CreateBrokerDealAsync(NCPartyRef buyer, string listingId, long version)
    {
        if (!Guid.TryParse(listingId, out var raw))
            return "nc-agent-city-result-invalid";
        var result = await _dealBoard.CreateServiceDealAsync(new NCMarketListingId(raw), version, buyer);
        return result.Deal == null ? result.Error ?? "nc-agent-city-result-deal-failed" : result.Notice;
    }

    private async Task<string> FundDealAsync(NCPartyRef buyer, string dealId, long version)
    {
        if (!Guid.TryParse(dealId, out var raw))
            return "nc-agent-city-result-invalid";
        var deal = await _fixers.GetDealAsync(new NCFixerDealId(raw), true);
        if (deal == null || deal.AggregateVersion != version || deal.Snapshot.Buyer != buyer ||
            deal.Snapshot.Status != NCFixerDealStatus.Reserved)
            return "nc-agent-city-result-deal-changed";
        var result = await _fixerEscrow.FundAsync(deal.Snapshot.DealId, deal.AggregateVersion, buyer);
        return result.Deal == null ? "nc-agent-city-result-funding-failed" : "nc-agent-city-result-funded";
    }

    private async Task<string> RequestNightMarketItemAsync(NCPartyRef buyer, string definitionId)
    {
        var result = await _dealBoard.CreateNightMarketItemDemandAsync(
            new ProtoId<NCItemDefinitionPrototype>(definitionId), buyer);
        return result.Error ?? result.Notice;
    }

    private async Task<string> CancelDealAsync(NCPartyRef buyer, string dealId, long version)
    {
        if (!Guid.TryParse(dealId, out var raw))
            return "nc-agent-city-result-invalid";
        var deal = await _fixers.GetDealAsync(new NCFixerDealId(raw), true);
        if (deal == null || deal.AggregateVersion != version || deal.Snapshot.Buyer != buyer ||
            deal.Snapshot.Status is not (NCFixerDealStatus.Offered or NCFixerDealStatus.Quoted or
                NCFixerDealStatus.Reserved))
            return "nc-agent-city-result-deal-changed";
        var result = await _dealBoard.CancelServiceDealAsync(deal, buyer);
        return result.Deal == null ? result.Error ?? "nc-agent-city-result-cancel-failed" : result.Notice;
    }

    private async Task<string> OpenDisputeAsync(NCPartyRef buyer, string dealId, long version)
    {
        if (!Guid.TryParse(dealId, out var raw))
            return "nc-agent-city-result-invalid";
        var deal = await _fixers.GetDealAsync(new NCFixerDealId(raw), true);
        if (deal == null || deal.AggregateVersion != version || deal.Snapshot.Buyer != buyer ||
            deal.Snapshot.Status is not (NCFixerDealStatus.EscrowFunded or NCFixerDealStatus.InProgress or
                NCFixerDealStatus.AwaitingHandoff or NCFixerDealStatus.Breached))
            return "nc-agent-city-result-deal-changed";
        var result = await _fixers.OpenDisputeAsync(new NCOpenFixerDisputeRequest(Guid.NewGuid(),
            deal.Snapshot.DealId, version, Guid.NewGuid(), buyer, "nc-fixer-dispute-client-request",
            Guid.NewGuid(), deal.Snapshot.LastAppliedEventId, "agent-fixer-dispute"));
        return result.Deal == null ? "nc-agent-city-result-dispute-failed" : "nc-agent-city-result-dispute-opened";
    }

    private IReadOnlyList<(NCActorId ActorId, EntityUid Entity)> FindNearbyFixers(EntityUid user)
    {
        var result = new List<(NCActorId, EntityUid)>();
        var networks = _fixers.GetCachedNetworks();
        foreach (var candidate in _lookup.GetEntitiesInRange(user, Configuration.ContactShareRange,
                     LookupFlags.Dynamic | LookupFlags.Uncontained))
        {
            if (!_actorRuntime.TryGetActor(candidate, out var actorId))
                continue;
            var party = NCPartyRef.ForActor(actorId);
            if (networks.Any(value => value.Snapshot.Fixer == party))
                result.Add((actorId, candidate));
        }
        return result.OrderBy(value => value.Item1.Value).ToArray();
    }

    private bool TryFindNearbyFixer(EntityUid user, NCActorId target, out NCPartyRef fixer)
    {
        foreach (var (actorId, _) in FindNearbyFixers(user))
        {
            if (actorId != target)
                continue;
            fixer = NCPartyRef.ForActor(actorId);
            return true;
        }
        fixer = default;
        return false;
    }

    private static NCAgentCityAction DealAction(NCFixerDealSnapshot deal, NCPartyRef viewer)
    {
        if (deal.Buyer != viewer)
            return NCAgentCityAction.None;
        return deal.Status switch
        {
            NCFixerDealStatus.Reserved => NCAgentCityAction.FundDeal,
            NCFixerDealStatus.Offered or NCFixerDealStatus.Quoted => NCAgentCityAction.CancelDeal,
            NCFixerDealStatus.EscrowFunded or NCFixerDealStatus.InProgress or
                NCFixerDealStatus.AwaitingHandoff or NCFixerDealStatus.Breached =>
                NCAgentCityAction.OpenDispute,
            _ => NCAgentCityAction.None,
        };
    }
}
