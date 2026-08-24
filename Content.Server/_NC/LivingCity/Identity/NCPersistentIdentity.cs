// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Fixers;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.LivingCity.Market;
using Content.Shared._NC.LivingCity.Housing;
using Content.Shared._NC.LivingCity.Police.NCPD;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Identity;

/// <summary>
/// Produces stable identities for canonical Living City participants and commands. A renamed prototype keeps its
/// explicit stable key instead of silently creating a second legal party.
/// </summary>
public static class NCPersistentIdentity
{
    private static readonly Guid NamespaceId = new("34ef622e-f578-50c5-a83a-e3cfe67eed86");

    public static NCPartyRef Organization(string stablePrototypeId) => NCPartyRef.ForOrganization(
        new NCOrganizationId(CreateGuid($"party/organization/{Normalize(stablePrototypeId)}")));

    public static NCPartyRef CityService(string stableServiceId) => NCPartyRef.ForCityService(
        new NCCityServiceId(CreateGuid($"party/city-service/{Normalize(stableServiceId)}")));

    public static NCPartyRef Business(string stableBusinessId) => NCPartyRef.ForBusiness(
        new NCBusinessId(CreateGuid($"party/business/{Normalize(stableBusinessId)}")));

    public static NCAccountId PrimaryAccount(NCPartyRef owner)
    {
        if (!owner.IsValid)
            throw new ArgumentOutOfRangeException(nameof(owner));

        return new NCAccountId(CreateGuid($"account/primary/{owner}"));
    }

    public static NCAccountId SystemAccount(string stableAccountId) =>
        new(CreateGuid($"account/system/{Normalize(stableAccountId)}"));

    /// <summary>
    /// Stable identity for an authored initial employment. Later dismiss/re-hire operations may deliberately use a
    /// new identity, while population bootstrap retries always converge on this one relation.
    /// </summary>
    public static NCEmploymentId Employment(NCPartyRef employee, NCPartyRef employer, string positionId)
    {
        if (!employee.IsValid || !employer.IsValid || string.IsNullOrWhiteSpace(positionId))
            throw new ArgumentOutOfRangeException(nameof(employee));

        return new NCEmploymentId(CreateGuid(
            $"employment/bootstrap/{employee}/{employer}/{Normalize(positionId)}"));
    }

    public static Guid Command(string domain, string operation, string stableKey) =>
        CreateGuid($"command/{Normalize(domain)}/{Normalize(operation)}/{Normalize(stableKey)}");

    public static Guid Correlation(string domain, string operation, string stableKey) =>
        CreateGuid($"correlation/{Normalize(domain)}/{Normalize(operation)}/{Normalize(stableKey)}");

    public static NCTransactionId Transaction(string domain, string operation, string stableKey) =>
        new(CreateGuid($"transaction/{Normalize(domain)}/{Normalize(operation)}/{Normalize(stableKey)}"));

    public static NCStockNodeId StockNode(NCPartyRef owner, string stableKey)
    {
        if (!owner.IsValid)
            throw new ArgumentOutOfRangeException(nameof(owner));
        return new NCStockNodeId(CreateGuid($"stock-node/{owner}/{Normalize(stableKey)}"));
    }

    public static NCMarketRequestId MarketRequest(NCPartyRef requester, string stableKey)
    {
        if (!requester.IsValid)
            throw new ArgumentOutOfRangeException(nameof(requester));
        return new NCMarketRequestId(CreateGuid($"market-request/{requester}/{Normalize(stableKey)}"));
    }

    public static NCPropertyListingId PropertyListing(NCApartmentId propertyId, string stableKey)
    {
        if (!propertyId.IsValid)
            throw new ArgumentOutOfRangeException(nameof(propertyId));
        return new NCPropertyListingId(CreateGuid($"property-listing/{propertyId}/{Normalize(stableKey)}"));
    }

    public static NCPropertyQuoteId PropertyQuote(NCPropertyListingId listingId, NCPartyRef buyer, string stableKey)
    {
        if (!listingId.IsValid || !buyer.IsValid)
            throw new ArgumentOutOfRangeException(nameof(listingId));
        return new NCPropertyQuoteId(CreateGuid($"property-quote/{listingId}/{buyer}/{Normalize(stableKey)}"));
    }

    public static NCPropertyReservationId PropertyReservation(NCPropertyQuoteId quoteId, string stableKey)
    {
        if (!quoteId.IsValid)
            throw new ArgumentOutOfRangeException(nameof(quoteId));
        return new NCPropertyReservationId(CreateGuid($"property-reservation/{quoteId}/{Normalize(stableKey)}"));
    }

    public static NCFixerNetworkId FixerNetwork(NCPartyRef fixer)
    {
        if (!fixer.IsValid)
            throw new ArgumentOutOfRangeException(nameof(fixer));
        return new NCFixerNetworkId(CreateGuid($"fixer-network/{fixer}"));
    }

    public static NCFixerDealId FixerDeal(NCFixerNetworkId networkId, string stableKey)
    {
        if (!networkId.IsValid)
            throw new ArgumentOutOfRangeException(nameof(networkId));
        return new NCFixerDealId(CreateGuid($"fixer-deal/{networkId}/{Normalize(stableKey)}"));
    }

    public static NCNightMarketId NightMarket(NCPartyRef organizer, string stableKey)
    {
        if (!organizer.IsValid)
            throw new ArgumentOutOfRangeException(nameof(organizer));
        return new NCNightMarketId(CreateGuid($"night-market/{organizer}/{Normalize(stableKey)}"));
    }

    public static NCSocialObligationId SocialObligation(NCFixerDealId dealId, Guid legId)
    {
        if (!dealId.IsValid || legId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(dealId));
        return new NCSocialObligationId(CreateGuid($"fixer-obligation/{dealId}/{legId:D}"));
    }

    public static NCCityIncidentId CityIncident(Guid sourceWorldEventId, string kind)
    {
        if (sourceWorldEventId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(sourceWorldEventId));
        return new NCCityIncidentId(CreateGuid($"city-incident/{sourceWorldEventId:D}/{Normalize(kind)}"));
    }

    public static NCServiceDispatchId ServiceDispatch(NCCityIncidentId incidentId, NCCityServiceId serviceId)
    {
        if (!incidentId.IsValid || !serviceId.IsValid)
            throw new ArgumentOutOfRangeException(nameof(incidentId));
        return new NCServiceDispatchId(CreateGuid($"service-dispatch/{incidentId}/{serviceId}"));
    }

    public static NCServiceContractId ServiceContract(NCCityServiceId serviceId, NCPartyRef subscriber,
        string stableKey)
    {
        if (!serviceId.IsValid || !subscriber.IsValid)
            throw new ArgumentOutOfRangeException(nameof(serviceId));
        return new NCServiceContractId(CreateGuid(
            $"service-contract/{serviceId}/{subscriber}/{Normalize(stableKey)}"));
    }

    public static NCCityNewsId CityNews(NCCityIncidentId incidentId, NCCityIncidentStage stage)
    {
        if (!incidentId.IsValid)
            throw new ArgumentOutOfRangeException(nameof(incidentId));
        return new NCCityNewsId(CreateGuid($"city-news/{incidentId}/{stage}"));
    }

    public static NCEstateId Estate(NCPartyRef decedent)
    {
        if (!decedent.IsValid)
            throw new ArgumentOutOfRangeException(nameof(decedent));
        return new NCEstateId(CreateGuid($"estate/{decedent}"));
    }

    public static NCCommodityBatchId CommodityBatch(string commodityId, Guid sourceWorldEventId, string lotKey)
    {
        if (sourceWorldEventId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(sourceWorldEventId));
        return new NCCommodityBatchId(CreateGuid(
            $"commodity-batch/{Normalize(commodityId)}/{sourceWorldEventId:D}/{Normalize(lotKey)}"));
    }

    public static NCRelationshipId Relationship(NCPartyRef observer, NCPartyRef subject)
    {
        if (!observer.IsValid || !subject.IsValid || observer == subject)
            throw new ArgumentOutOfRangeException(nameof(observer));

        return new NCRelationshipId(CreateGuid($"relationship/{observer}/{subject}"));
    }

    public static Guid RelationshipIndex(NCPartyRef observer)
    {
        if (!observer.IsValid)
            throw new ArgumentOutOfRangeException(nameof(observer));

        return CreateGuid($"relationship-index/{observer}");
    }

    public static Guid RelationshipApplication(NCRelationshipId relationshipId, Guid worldEventId)
    {
        if (!relationshipId.IsValid || worldEventId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(relationshipId));

        return CreateGuid($"relationship-application/{relationshipId}/{worldEventId:D}");
    }

    public static NCReputationId Reputation(NCPartyRef subject)
    {
        if (!subject.IsValid)
            throw new ArgumentOutOfRangeException(nameof(subject));

        return new NCReputationId(CreateGuid($"reputation/{subject}"));
    }

    public static NCReputationEntryId ReputationEntry(NCReputationId reputationId, string audienceKey)
    {
        if (!reputationId.IsValid)
            throw new ArgumentOutOfRangeException(nameof(reputationId));

        return new NCReputationEntryId(CreateGuid($"reputation-entry/{reputationId}/{Normalize(audienceKey)}"));
    }

    public static Guid ReputationApplication(NCReputationEntryId entryId, Guid worldEventId)
    {
        if (!entryId.IsValid || worldEventId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(entryId));

        return CreateGuid($"reputation-application/{entryId}/{worldEventId:D}");
    }

    public static NCMemoryBookId MemoryBook(NCPartyRef owner)
    {
        if (!owner.IsValid)
            throw new ArgumentOutOfRangeException(nameof(owner));

        return new NCMemoryBookId(CreateGuid($"memory-book/{owner}"));
    }

    public static NCMemoryId Memory(NCPartyRef owner, Guid worldEventId)
    {
        if (!owner.IsValid || worldEventId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(owner));

        return new NCMemoryId(CreateGuid($"memory/{owner}/{worldEventId:D}"));
    }

    public static NCMemorySummaryId MemorySummary(NCPartyRef owner, IReadOnlyList<NCMemoryId> sourceMemoryIds)
    {
        if (!owner.IsValid || sourceMemoryIds.Count == 0 || sourceMemoryIds.Any(id => !id.IsValid))
            throw new ArgumentOutOfRangeException(nameof(owner));

        var sources = string.Join('/', sourceMemoryIds.Select(id => id.Value).Order());
        return new NCMemorySummaryId(CreateGuid($"memory-summary/{owner}/{sources}"));
    }

    public static Guid MemoryApplication(NCMemoryId memoryId)
    {
        if (!memoryId.IsValid)
            throw new ArgumentOutOfRangeException(nameof(memoryId));

        return CreateGuid($"memory-application/{memoryId}");
    }

    public static Guid MemoryConsolidationApplication(NCMemoryBookId bookId, Guid consolidationEventId)
    {
        if (!bookId.IsValid || consolidationEventId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(bookId));

        return CreateGuid($"memory-consolidation-application/{bookId}/{consolidationEventId:D}");
    }

    public static Guid MemoryRecallApplication(NCMemoryBookId bookId, Guid recallEventId)
    {
        if (!bookId.IsValid || recallEventId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(bookId));

        return CreateGuid($"memory-recall-application/{bookId}/{recallEventId:D}");
    }

    public static Guid RumorBook(NCPartyRef owner)
    {
        if (!owner.IsValid)
            throw new ArgumentOutOfRangeException(nameof(owner));
        return CreateGuid($"rumor-book/{owner}");
    }

    public static NCInformationId Information(string topicKey, NCInformationSubject subject, Guid sourceWorldEventId)
    {
        if (sourceWorldEventId == Guid.Empty || string.IsNullOrWhiteSpace(topicKey) || !subject.IsValid)
            throw new ArgumentOutOfRangeException(nameof(sourceWorldEventId));
        return new NCInformationId(CreateGuid(
            $"information/{Normalize(topicKey)}/{subject.Kind}/{Normalize(subject.StableId)}/{sourceWorldEventId:D}"));
    }

    public static NCRumorKnowledgeId RumorKnowledge(NCPartyRef owner, NCInformationId informationId)
    {
        if (!owner.IsValid || !informationId.IsValid)
            throw new ArgumentOutOfRangeException(nameof(owner));
        return new NCRumorKnowledgeId(CreateGuid($"rumor-knowledge/{owner}/{informationId}"));
    }

    public static Guid RumorPropagationApplication(
        NCRumorKnowledgeId targetKnowledgeId,
        Guid propagationEventId)
    {
        if (!targetKnowledgeId.IsValid || propagationEventId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(targetKnowledgeId));
        return CreateGuid($"rumor-propagation/{targetKnowledgeId}/{propagationEventId:D}");
    }

    public static Guid SocialObligationIndex(NCPartyRef owner)
    {
        if (!owner.IsValid)
            throw new ArgumentOutOfRangeException(nameof(owner));
        return CreateGuid($"social-obligation-index/{owner}");
    }

    public static Guid SocialObligationConsequence(NCSocialObligationId obligationId, int version)
    {
        if (!obligationId.IsValid || version < 1)
            throw new ArgumentOutOfRangeException(nameof(obligationId));
        return CreateGuid($"social-obligation-consequence/{obligationId}/{version}");
    }

    public static Guid CognitionBelief(NCActorId actorId, string key, string subject, string source)
    {
        if (!actorId.IsValid)
            throw new ArgumentOutOfRangeException(nameof(actorId));
        return CreateGuid($"cognition/belief/{actorId}/{Normalize(key)}/{NormalizeOptional(subject)}/{Normalize(source)}");
    }

    public static Guid CognitionDesire(NCActorId actorId, string capabilityId)
    {
        if (!actorId.IsValid)
            throw new ArgumentOutOfRangeException(nameof(actorId));
        return CreateGuid($"cognition/desire/{actorId}/{Normalize(capabilityId)}");
    }

    public static Guid CognitionIntention(NCActorId actorId, string capabilityId, long revision)
    {
        if (!actorId.IsValid || revision < 1)
            throw new ArgumentOutOfRangeException(nameof(actorId));
        return CreateGuid($"cognition/intention/{actorId}/{Normalize(capabilityId)}/{revision}");
    }

    public static Guid CognitionPlan(Guid intentionId, string planner, long dependencyVersion)
    {
        if (intentionId == Guid.Empty || dependencyVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(intentionId));
        return CreateGuid($"cognition/plan/{intentionId:D}/{Normalize(planner)}/{dependencyVersion}");
    }

    public static Guid CognitionAction(Guid planId, string stepId)
    {
        if (planId == Guid.Empty || string.IsNullOrWhiteSpace(stepId))
            throw new ArgumentOutOfRangeException(nameof(planId));
        return CreateGuid($"cognition/action/{planId:D}/{Normalize(stepId)}");
    }

    public static NCNcpdUnitId NcpdUnit(NCZoneId stationZone, IEnumerable<NCActorId> members)
    {
        if (!stationZone.IsValid)
            throw new ArgumentOutOfRangeException(nameof(stationZone));

        var ordered = members
            .Where(static actor => actor.IsValid)
            .Select(static actor => actor.Value)
            .Distinct()
            .Order()
            .ToArray();
        if (ordered.Length == 0)
            throw new ArgumentOutOfRangeException(nameof(members));

        return new NCNcpdUnitId(CreateGuid(
            $"ncpd/unit/{stationZone}/{string.Join('/', ordered.Select(static value => value.ToString("D")))}"));
    }

    private static Guid CreateGuid(string value)
    {
        Span<byte> scope = stackalloc byte[16];
        NamespaceId.TryWriteBytes(scope);
        var text = Encoding.UTF8.GetBytes(value);
        var input = new byte[scope.Length + text.Length];
        scope.CopyTo(input);
        text.CopyTo(input.AsSpan(scope.Length));

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        // Stable UUIDv5-shaped value; SHA-256 is used because the identity is local and not an RFC UUID namespace.
        hash[6] = (byte) ((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte) ((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16]);
    }

    private static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToLowerInvariant();
    }

    private static string NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().ToLowerInvariant();
}
