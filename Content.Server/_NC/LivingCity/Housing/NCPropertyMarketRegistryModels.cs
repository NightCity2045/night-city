// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Housing;
using Content.Shared._NC.LivingCity.Housing;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Housing;

public sealed record NCPropertyListingRecord(NCPropertyListingSnapshot Snapshot, long AggregateVersion,
    NCSessionEpoch SessionEpoch, NCActiveWorldTime PersistedAt);
public sealed record NCPropertyQuoteRecord(NCPropertyQuoteSnapshot Snapshot, long AggregateVersion,
    NCSessionEpoch SessionEpoch, NCActiveWorldTime PersistedAt);
public sealed record NCPropertyReservationRecord(NCPropertyReservationSnapshot Snapshot, long AggregateVersion,
    NCSessionEpoch SessionEpoch, NCActiveWorldTime PersistedAt);

public sealed record NCCreatePropertyListingRequest(
    Guid CommandId,
    NCPropertyListingId ListingId,
    string SourceListingId,
    NCApartmentId PropertyId,
    long ExpectedPropertyVersion,
    NCPropertyOfferKind OfferKind,
    long Price,
    long SecurityDeposit,
    string RentPolicyId,
    long BillingIntervalTicks,
    string? TemplateId,
    int? TemplateRevision,
    NCPropertyPermission OccupantPermissions,
    TimeSpan? Lifetime,
    NCPartyRef Seller,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "property-listing-create");

public sealed record NCCreatePropertyQuoteRequest(
    Guid CommandId,
    NCPropertyQuoteId QuoteId,
    NCPropertyListingId ListingId,
    long ExpectedListingVersion,
    NCPartyRef Buyer,
    TimeSpan Lifetime,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "property-quote-create");

public sealed record NCReservePropertyQuoteRequest(
    Guid CommandId,
    NCPropertyReservationId ReservationId,
    NCPropertyQuoteId QuoteId,
    long ExpectedQuoteVersion,
    TimeSpan Lifetime,
    NCPartyRef Buyer,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "property-quote-reserve");

public sealed record NCReopenPropertyListingRequest(
    Guid CommandId,
    NCPropertyListingId ListingId,
    long ExpectedListingVersion,
    long ExpectedPropertyVersion,
    NCPartyRef Seller,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "property-listing-reopen");

public sealed record NCClosePropertyReservationRequest(
    Guid CommandId,
    NCPropertyReservationId ReservationId,
    long ExpectedReservationVersion,
    NCPartyRef Actor,
    bool Expired,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "property-reservation-close");

public sealed record NCClosePropertyListingRequest(
    Guid CommandId,
    NCPropertyListingId ListingId,
    long ExpectedListingVersion,
    NCPartyRef Seller,
    bool Expired,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "property-listing-close");

public sealed record NCPropertyMarketMutationResult(
    NCPropertyAggregateMutationStatus Status,
    NCPropertyListingRecord? Listing,
    NCPropertyQuoteRecord? Quote,
    NCPropertyReservationRecord? Reservation,
    string? Error);

public sealed record NCPropertyListingDiscoveryPage(
    IReadOnlyList<NCPropertyListingRecord> Listings,
    string? NextAfterAggregateId,
    string? Error);
