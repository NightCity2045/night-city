// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Market;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Market;

public sealed class NCMarketAggregateChangedEvent(string aggregateType, string aggregateId) : EntityEventArgs
{
    public string AggregateType { get; } = aggregateType;
    public string AggregateId { get; } = aggregateId;
}

public enum NCMarketMutationStatus : byte
{
    Applied,
    Duplicate,
    NotFound,
    VersionConflict,
    PartyUnavailable,
    ClockUnavailable,
    CapacityExceeded,
    Rejected,
    StorageFailure,
}

public sealed record NCMarketListingRecord(
    NCMarketListingSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCMarketRequestRecord(
    NCMarketRequestSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCCreateMarketListingRequest(
    Guid CommandId,
    NCMarketListingId ListingId,
    NCEconomicOfferKind OfferKind,
    string OfferId,
    long MaximumQuantity,
    NCPartyRef Seller,
    NCStockNodeId? SourceStockNodeId,
    NCMarketChannel Channel,
    NCMarketAccessPolicy Access,
    TimeSpan? Lifetime,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "market-listing-create");

public sealed record NCCreateMarketRequestRequest(
    Guid CommandId,
    NCMarketRequestId RequestId,
    NCMarketRequestKind Kind,
    string DefinitionId,
    long Quantity,
    long MaximumUnitPrice,
    int MinimumQuality,
    NCPartyRef Requester,
    NCMarketChannel Channel,
    NCMarketAccessPolicy Access,
    NCZoneId PreferredZone,
    TimeSpan Lifetime,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "market-request-create");

public sealed record NCChangeMarketListingStatusRequest(
    Guid CommandId,
    NCMarketListingId ListingId,
    long ExpectedVersion,
    NCMarketListingStatus Status,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "market-listing-status");

public sealed record NCChangeMarketRequestStatusRequest(
    Guid CommandId,
    NCMarketRequestId RequestId,
    long ExpectedVersion,
    NCMarketRequestStatus Status,
    NCMarketListingId? MatchedListingId,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "market-request-status");

public sealed record NCMarketViewerContext(
    NCPartyRef Viewer,
    IReadOnlySet<string> AudienceKeys,
    int Relationship,
    int Reputation);

public sealed record NCMarketDiscoveryRequest(
    string? AfterAggregateId,
    int Limit,
    string Query,
    NCMarketChannel? Channel,
    NCEconomicOfferKind? OfferKind,
    NCMarketViewerContext Viewer);

public sealed record NCMarketDiscoveryPage(
    IReadOnlyList<NCMarketListingRecord> Listings,
    string? NextAfterAggregateId,
    string? Error);

public sealed record NCMarketListingMutationResult(
    NCMarketMutationStatus Status,
    NCMarketListingRecord? Listing,
    string? Error);

public sealed record NCMarketRequestMutationResult(
    NCMarketMutationStatus Status,
    NCMarketRequestRecord? Request,
    string? Error);

public sealed record NCQuoteMarketListingRequest(
    Guid CommandId,
    NCEconomicQuoteId QuoteId,
    NCMarketListingId ListingId,
    long ExpectedListingVersion,
    long Quantity,
    NCMarketViewerContext Buyer,
    NCEconomicPriceContext PriceContext,
    TimeSpan Lifetime,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "market-listing-quote");
