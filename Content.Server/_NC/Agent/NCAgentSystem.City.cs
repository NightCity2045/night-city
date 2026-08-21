// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.CityServices;
using Content.Server._NC.LivingCity.Fixers;
using Content.Server._NC.LivingCity.Housing;
using Content.Server._NC.LivingCity.Market;
using Content.Server._NC.LivingCity.Social;
using Content.Shared._NC.Agent.UI;
using Content.Shared._NC.Identity;
using Content.Shared._NC.LivingCity.Market;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.PDA;

namespace Content.Server._NC.Agent;

/// <summary>Permission-filtered city projection for Agent. No hidden stock, beliefs, evidence or ownership is sent.</summary>
public sealed partial class NCAgentSystem
{
    [Dependency] private NCMarketRegistrySystem _cityMarket = default!;
    [Dependency] private NCPropertyMarketRegistrySystem _propertyMarket = default!;
    [Dependency] private NCFixerRegistrySystem _fixers = default!;
    [Dependency] private NCSocialObligationRegistrySystem _obligations = default!;
    [Dependency] private NCCityNewsRegistrySystem _cityNews = default!;

    private async Task SendCitySnapshotAsync(EntityUid agent, EntityUid user, NCCharacterId owner)
    {
        if (Deleted(agent) || Deleted(user) || !_views.ContainsKey((agent, user))) return;
        var party = NCPartyRef.ForCharacter(owner);
        var rows = new List<NCAgentCityEntry>();
        var market = await _cityMarket.DiscoverAsync(new NCMarketDiscoveryRequest(null, 24, string.Empty, null,
            null, new NCMarketViewerContext(party, new HashSet<string> { "public" }, 0, 0)));
        rows.AddRange(market.Listings.Select(value => new NCAgentCityEntry(NCAgentCityArea.Market,
            value.Snapshot.ListingId.ToString(), value.Snapshot.OfferId,
            $"{value.Snapshot.Channel} // {value.Snapshot.OfferKind}", value.AggregateVersion, true)));
        var properties = await _propertyMarket.DiscoverAsync(null, 24);
        rows.AddRange(properties.Listings.Select(value => new NCAgentCityEntry(NCAgentCityArea.Property,
            value.Snapshot.ListingId.ToString(), value.Snapshot.OfferKind.ToString(),
            $"PROPERTY {value.Snapshot.PropertyId} // {value.Snapshot.Price}",
            value.AggregateVersion, true)));
        rows.AddRange(_fixers.GetCachedDealsForParty(party, 24).Select(value => new NCAgentCityEntry(
            NCAgentCityArea.Fixer, value.Snapshot.DealId.ToString(), value.Snapshot.Status.ToString(),
            $"LEGS {value.Snapshot.Plan.Legs.Count}", value.AggregateVersion, true)));
        var obligations = await _obligations.GetForPartyAsync(party, 24);
        rows.AddRange(obligations.Select(value => new NCAgentCityEntry(NCAgentCityArea.Obligations,
            value.Snapshot.ObligationId.ToString(), value.Snapshot.Kind.ToString(),
            value.Snapshot.Status.ToString(), value.AggregateVersion, true)));
        var news = await _cityNews.GetPageAsync(null, 32);
        rows.AddRange(news.Where(value => !value.Snapshot.Retracted).Select(value => new NCAgentCityEntry(
            NCAgentCityArea.News, value.Snapshot.NewsId.ToString(), value.Snapshot.HeadlineKey,
            $"{value.Snapshot.Kind} // SEVERITY {value.Snapshot.Severity}", value.AggregateVersion, false)));
        _ui.ServerSendUiMessage(agent, PdaUiKey.Key, new NCAgentCityUpdateMessage(rows,
            market.Error ?? properties.Error ?? string.Empty), user);
    }
}
