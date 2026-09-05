// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.CityServices;
using Content.Server._NC.LivingCity.Housing;
using Content.Server._NC.LivingCity.Market;
using Content.Server._NC.LivingCity.Social;
using Content.Shared._NC.Identity;
using Content.Shared._NC.Agent.UI;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Market;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.PDA;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Agent;

/// <summary>
/// Read-only Agent projection of player-facing city registries. NPC professions, fixers and faction actors are not
/// reintroduced through this UI while the physical GPT resident foundation is being rebuilt.
/// </summary>
public sealed partial class NCAgentSystem
{
    [Dependency] private NCMarketRegistrySystem _cityMarket = default!;
    [Dependency] private NCPropertyMarketRegistrySystem _propertyMarket = default!;
    [Dependency] private NCSocialObligationRegistrySystem _obligations = default!;
    [Dependency] private NCCityNewsRegistrySystem _cityNews = default!;

    private void InitializeCity()
    {
    }

    private async Task SendCitySnapshotAsync(
        EntityUid agent,
        EntityUid user,
        NCCharacterId owner,
        string notice = "")
    {
        if (Deleted(agent) || Deleted(user) || !_views.ContainsKey((agent, user)))
            return;

        var party = NCPartyRef.ForCharacter(owner);
        var rows = new List<NCAgentCityEntry>();
        var market = await _cityMarket.DiscoverAsync(new NCMarketDiscoveryRequest(
            null,
            24,
            string.Empty,
            null,
            null,
            new NCMarketViewerContext(party, new HashSet<string> { "public" }, 0, 0)));
        foreach (var value in market.Listings)
        {
            // Broker listings belong to the removed fixer layer and are hidden until that domain is rebuilt.
            if (value.Snapshot.Channel == NCMarketChannel.Broker)
                continue;

            NCEconomicServicePrototype? service = null;
            var knownService = value.Snapshot.OfferKind == NCEconomicOfferKind.Service &&
                               _prototypes.TryIndex(
                                   new ProtoId<NCEconomicServicePrototype>(value.Snapshot.OfferId),
                                   out service);
            rows.Add(new NCAgentCityEntry(
                NCAgentCityArea.Market,
                value.Snapshot.ListingId.ToString(),
                knownService ? service!.DisplayName.Id : value.Snapshot.OfferId,
                knownService ? "nc-agent-city-market-service-details" :
                    $"{value.Snapshot.Channel} // {value.Snapshot.OfferKind}",
                value.AggregateVersion,
                NCAgentCityAction.None));
        }

        var properties = await _propertyMarket.DiscoverAsync(null, 24);
        rows.AddRange(properties.Listings.Select(value => new NCAgentCityEntry(
            NCAgentCityArea.Property,
            value.Snapshot.ListingId.ToString(),
            value.Snapshot.OfferKind.ToString(),
            $"PROPERTY {value.Snapshot.PropertyId} // {value.Snapshot.Price}",
            value.AggregateVersion,
            NCAgentCityAction.None)));

        var obligations = await _obligations.GetForPartyAsync(party, 24);
        rows.AddRange(obligations.Select(value => new NCAgentCityEntry(
            NCAgentCityArea.Obligations,
            value.Snapshot.ObligationId.ToString(),
            value.Snapshot.Kind.ToString(),
            value.Snapshot.Status.ToString(),
            value.AggregateVersion,
            NCAgentCityAction.None)));

        var news = await _cityNews.GetPageAsync(null, 32);
        rows.AddRange(news.Where(value => !value.Snapshot.Retracted).Select(value => new NCAgentCityEntry(
            NCAgentCityArea.News,
            value.Snapshot.NewsId.ToString(),
            value.Snapshot.HeadlineKey,
            $"{value.Snapshot.Kind} // SEVERITY {value.Snapshot.Severity}",
            value.AggregateVersion,
            NCAgentCityAction.None)));

        _ui.ServerSendUiMessage(
            agent,
            PdaUiKey.Key,
            new NCAgentCityUpdateMessage(
                rows,
                string.IsNullOrEmpty(notice) ? market.Error ?? properties.Error ?? string.Empty : notice),
            user);
    }
}
