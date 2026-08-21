// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Text.Json;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.Housing;
using Content.Shared._NC.Housing.Prototypes;
using Content.Shared._NC.LivingCity.Housing;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Housing;

/// <summary>
/// Persistent property discovery, quote and reservation authority. Mapper prototypes seed listings, but are never the
/// mutable listing state and can neither reserve nor transfer a property by themselves.
/// </summary>
public sealed partial class NCPropertyMarketRegistrySystem : EntitySystem
{
    public const string ListingAggregateType = "living-city.property-listing";
    public const string QuoteAggregateType = "living-city.property-quote";
    public const string ReservationAggregateType = "living-city.property-reservation";
    public const int SnapshotFormatVersion = 1;
    private static readonly ProtoId<NCPropertyMarketPolicyPrototype> DefaultPolicy = "Default";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPartyResolverSystem _parties = default!;
    [Dependency] private NCPropertyRegistrySystem _properties = default!;

    private readonly ConcurrentDictionary<NCPropertyListingId, NCPropertyListingRecord> _listings = [];
    private readonly ConcurrentDictionary<NCPropertyQuoteId, NCPropertyQuoteRecord> _quotes = [];
    private readonly ConcurrentDictionary<NCPropertyReservationId, NCPropertyReservationRecord> _reservations = [];
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public int CachedListingCount => _listings.Count;
    public int CachedQuoteCount => _quotes.Count;
    public int CachedReservationCount => _reservations.Count;

    public Task<NCPropertyListingRecord?> GetListingAsync(NCPropertyListingId id, bool forceReload = false) =>
        GetAsync(id, ListingAggregateType, _listings, DeserializeListing, forceReload);
    public Task<NCPropertyQuoteRecord?> GetQuoteAsync(NCPropertyQuoteId id, bool forceReload = false) =>
        GetAsync(id, QuoteAggregateType, _quotes, DeserializeQuote, forceReload);
    public Task<NCPropertyReservationRecord?> GetReservationAsync(
        NCPropertyReservationId id,
        bool forceReload = false) => GetAsync(id, ReservationAggregateType, _reservations,
        DeserializeReservation, forceReload);

    public async Task<NCPropertyMarketMutationResult> CreateListingAsync(NCCreatePropertyListingRequest request)
    {
        if (!TryPolicy(out var policy) || !Valid(request, policy) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject(_clock.IsReady ? NCPropertyAggregateMutationStatus.Rejected :
                NCPropertyAggregateMutationStatus.ClockUnavailable, "The property listing request is invalid.");
        if (!(await _parties.ResolveAsync(request.Seller, true)).IsResolved)
            return Reject(NCPropertyAggregateMutationStatus.Rejected, "The listing seller is unavailable.");
        var gate = Gate(request.ListingId.Value);
        await gate.WaitAsync();
        try
        {
            var existing = await GetListingAsync(request.ListingId, true);
            if (existing != null)
                return Same(existing.Snapshot, request)
                    ? new(NCPropertyAggregateMutationStatus.Duplicate, existing, null, null, null)
                    : Reject(NCPropertyAggregateMutationStatus.Rejected,
                        "The property listing identity belongs to different immutable terms.");
            var property = await _properties.GetAsync(request.PropertyId, true);
            if (property == null)
                return Reject(NCPropertyAggregateMutationStatus.NotFound, "The listed property does not exist.");
            if (property.AggregateVersion != request.ExpectedPropertyVersion)
                return Reject(NCPropertyAggregateMutationStatus.VersionConflict, "The property version changed.");
            if (property.Snapshot.Owner != request.Seller || property.Snapshot.LegalState != NCPropertyLegalState.Clear ||
                property.Snapshot.Status is NCPropertyStatus.Destroyed or NCPropertyStatus.Condemned or
                    NCPropertyStatus.LegalHold || request.OfferKind == NCPropertyOfferKind.Rent &&
                property.Snapshot.Status != NCPropertyStatus.Available)
                return Reject(NCPropertyAggregateMutationStatus.Rejected,
                    "The seller, legal state, or tenure does not permit this listing.");
            var eventId = Guid.NewGuid();
            var snapshot = new NCPropertyListingSnapshot(
                request.ListingId, request.SourceListingId.Trim(), request.PropertyId, request.OfferKind,
                request.Seller, request.Price, request.SecurityDeposit, request.RentPolicyId.Trim(),
                request.BillingIntervalTicks, request.TemplateId?.Trim(), request.TemplateRevision,
                request.OccupantPermissions, NCPropertyListingStatus.Open,
                request.Lifetime is { } lifetime ? now.Advance(lifetime) : null,
                now, now, 1, eventId);
            var persisted = await Persist(request.CommandId, "living-city.property-listing.create", "property-listing",
                request.ListingId.ToString(), [Write(null, snapshot, eventId,
                    "living-city.property-listing.created", request.AuditSource)], request.CorrelationId,
                request.CausationId, epoch, now);
            if (persisted.Status is not (NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted))
                return Reject(Map(persisted.Status), persisted.Error ?? "The property listing was not committed.");
            var loaded = await GetListingAsync(request.ListingId, true);
            return loaded == null
                ? Reject(NCPropertyAggregateMutationStatus.StorageFailure,
                    "The property listing could not be reloaded.")
                : new(persisted.Status == NCPersistentMutationStatus.Applied
                    ? NCPropertyAggregateMutationStatus.Applied
                    : NCPropertyAggregateMutationStatus.Duplicate, loaded, null, null, null);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NCPropertyMarketMutationResult> CreateQuoteAsync(NCCreatePropertyQuoteRequest request)
    {
        if (!TryPolicy(out var policy) || !Valid(request, policy) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) ||
            !(await _parties.ResolveAsync(request.Buyer, true)).IsResolved)
            return Reject(_clock.IsReady ? NCPropertyAggregateMutationStatus.Rejected :
                NCPropertyAggregateMutationStatus.ClockUnavailable, "The property quote request is invalid.");
        var gate = Gate(request.QuoteId.Value);
        await gate.WaitAsync();
        try
        {
            var listing = await GetListingAsync(request.ListingId, true);
            if (listing == null)
                return Reject(NCPropertyAggregateMutationStatus.NotFound, "The property listing does not exist.");
            var existing = await GetQuoteAsync(request.QuoteId, true);
            if (existing != null)
                return existing.Snapshot.ListingId == request.ListingId && existing.Snapshot.Buyer == request.Buyer
                    ? new(NCPropertyAggregateMutationStatus.Duplicate, listing, existing, null, null)
                    : Reject(NCPropertyAggregateMutationStatus.Rejected,
                        "The property quote identity belongs to a different request.");
            if (listing.AggregateVersion != request.ExpectedListingVersion ||
                listing.Snapshot.Status != NCPropertyListingStatus.Open ||
                listing.Snapshot.ExpiresAt is { } listingExpiry && now >= listingExpiry ||
                listing.Snapshot.Seller == request.Buyer)
                return Reject(NCPropertyAggregateMutationStatus.Rejected,
                    "The property listing is unavailable to this buyer.");
            var property = await _properties.GetAsync(listing.Snapshot.PropertyId, true);
            if (property == null || property.Snapshot.Owner != listing.Snapshot.Seller ||
                property.Snapshot.LegalState != NCPropertyLegalState.Clear ||
                !Available(property.Snapshot, listing.Snapshot.OfferKind))
                return Reject(NCPropertyAggregateMutationStatus.Rejected,
                    "The property authority no longer matches the listing.");
            var eventId = Guid.NewGuid();
            var snapshot = new NCPropertyQuoteSnapshot(
                request.QuoteId, listing.Snapshot.ListingId, listing.AggregateVersion,
                listing.Snapshot.PropertyId, property.AggregateVersion, listing.Snapshot.OfferKind,
                listing.Snapshot.Seller, request.Buyer, listing.Snapshot.Price, listing.Snapshot.SecurityDeposit,
                listing.Snapshot.RentPolicyId, listing.Snapshot.BillingIntervalTicks, listing.Snapshot.TemplateId,
                listing.Snapshot.TemplateRevision, listing.Snapshot.OccupantPermissions,
                NCPropertyQuoteStatus.Open, now.Advance(request.Lifetime), now, now, 1, eventId);
            var persisted = await Persist(request.CommandId, "living-city.property-quote.create", "property-quote",
                request.QuoteId.ToString(), [Write(null, snapshot, eventId, "living-city.property-quote.created",
                    request.AuditSource)], request.CorrelationId, request.CausationId, epoch, now);
            if (persisted.Status is not (NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted))
                return Reject(Map(persisted.Status), persisted.Error ?? "The property quote was not committed.");
            var loaded = await GetQuoteAsync(request.QuoteId, true);
            return loaded == null
                ? Reject(NCPropertyAggregateMutationStatus.StorageFailure, "The property quote could not be reloaded.")
                : new(persisted.Status == NCPersistentMutationStatus.Applied
                    ? NCPropertyAggregateMutationStatus.Applied
                    : NCPropertyAggregateMutationStatus.Duplicate, listing, loaded, null, null);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NCPropertyMarketMutationResult> ReserveAsync(NCReservePropertyQuoteRequest request)
    {
        if (!TryPolicy(out var policy) || !Valid(request, policy) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject(_clock.IsReady ? NCPropertyAggregateMutationStatus.Rejected :
                NCPropertyAggregateMutationStatus.ClockUnavailable, "The property reservation request is invalid.");
        var initialQuote = await GetQuoteAsync(request.QuoteId, true);
        if (initialQuote == null)
            return Reject(NCPropertyAggregateMutationStatus.NotFound, "The property quote does not exist.");
        var gates = await AcquireGates(request.ReservationId.Value, request.QuoteId.Value,
            initialQuote.Snapshot.ListingId.Value);
        try
        {
            var quote = await GetQuoteAsync(request.QuoteId, true);
            if (quote == null)
                return Reject(NCPropertyAggregateMutationStatus.NotFound, "The property quote does not exist.");
            var listing = await GetListingAsync(quote.Snapshot.ListingId, true);
            var existing = await GetReservationAsync(request.ReservationId, true);
            if (existing != null)
                return existing.Snapshot.QuoteId == request.QuoteId && existing.Snapshot.Buyer == request.Buyer
                    ? new(NCPropertyAggregateMutationStatus.Duplicate, listing, quote, existing, null)
                    : Reject(NCPropertyAggregateMutationStatus.Rejected,
                        "The property reservation identity belongs to different terms.");
            if (listing == null || quote.AggregateVersion != request.ExpectedQuoteVersion ||
                quote.Snapshot.Status != NCPropertyQuoteStatus.Open || quote.Snapshot.ExpiresAt <= now ||
                quote.Snapshot.Buyer != request.Buyer || listing.AggregateVersion != quote.Snapshot.ListingVersion ||
                listing.Snapshot.Status != NCPropertyListingStatus.Open)
                return Reject(NCPropertyAggregateMutationStatus.Rejected,
                    "The quote or listing can no longer be reserved.");
            var property = await _properties.GetAsync(quote.Snapshot.PropertyId, true);
            if (property?.AggregateVersion != quote.Snapshot.PropertyVersion ||
                property.Snapshot.Owner != quote.Snapshot.Seller)
                return Reject(NCPropertyAggregateMutationStatus.VersionConflict,
                    "The property changed after the quote was issued.");
            var quoteEvent = Guid.NewGuid();
            var nextQuote = quote.Snapshot with
            {
                Status = NCPropertyQuoteStatus.Reserved,
                UpdatedAt = now,
                DataVersion = checked(quote.Snapshot.DataVersion + 1),
                LastAppliedEventId = quoteEvent,
            };
            var listingEvent = Guid.NewGuid();
            var nextListing = listing.Snapshot with
            {
                Status = NCPropertyListingStatus.Reserved,
                UpdatedAt = now,
                DataVersion = checked(listing.Snapshot.DataVersion + 1),
                LastAppliedEventId = listingEvent,
            };
            var reservationEvent = Guid.NewGuid();
            var reservation = new NCPropertyReservationSnapshot(
                request.ReservationId, quote.Snapshot.QuoteId, quote.Snapshot.ListingId,
                quote.Snapshot.PropertyId, quote.Snapshot.Seller, quote.Snapshot.Buyer,
                NCPropertyReservationStatus.Active, now.Advance(request.Lifetime), null,
                now, now, 1, reservationEvent);
            var persisted = await Persist(request.CommandId, "living-city.property-reservation.create",
                "property-reservation", request.ReservationId.ToString(),
                [
                    Write(quote, nextQuote, quoteEvent, "living-city.property-quote.reserved", request.AuditSource),
                    Write(listing, nextListing, listingEvent, "living-city.property-listing.reserved",
                        request.AuditSource),
                    Write(null, reservation, reservationEvent, "living-city.property-reservation.created",
                        request.AuditSource),
                ], request.CorrelationId, request.CausationId, epoch, now);
            if (persisted.Status is not (NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted))
                return Reject(Map(persisted.Status), persisted.Error ?? "The reservation was not committed.");
            return new(persisted.Status == NCPersistentMutationStatus.Applied
                    ? NCPropertyAggregateMutationStatus.Applied
                    : NCPropertyAggregateMutationStatus.Duplicate,
                await GetListingAsync(listing.Snapshot.ListingId, true),
                await GetQuoteAsync(quote.Snapshot.QuoteId, true),
                await GetReservationAsync(request.ReservationId, true), null);
        }
        finally
        {
            ReleaseGates(gates);
        }
    }

    /// <summary>Reopens a stable mapper listing after its previous lease ended.</summary>
    public async Task<NCPropertyMarketMutationResult> ReopenListingAsync(NCReopenPropertyListingRequest request)
    {
        if (request.CommandId == Guid.Empty || !request.ListingId.IsValid || request.ExpectedListingVersion <= 0 ||
            request.ExpectedPropertyVersion <= 0 || !request.Seller.IsValid || request.CorrelationId == Guid.Empty ||
            !ValidAudit(request.AuditSource) || !_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject(NCPropertyAggregateMutationStatus.Rejected, "The listing reopen request is invalid.");
        var gate = Gate(request.ListingId.Value);
        await gate.WaitAsync();
        try
        {
            var listing = await GetListingAsync(request.ListingId, true);
            var property = listing == null ? null : await _properties.GetAsync(listing.Snapshot.PropertyId, true);
            if (listing == null || property == null)
                return Reject(NCPropertyAggregateMutationStatus.NotFound, "The listing or property does not exist.");
            if (listing.AggregateVersion != request.ExpectedListingVersion ||
                property.AggregateVersion != request.ExpectedPropertyVersion)
                return Reject(NCPropertyAggregateMutationStatus.VersionConflict,
                    "The listing or property version changed.");
            if (listing.Snapshot.Status is not (NCPropertyListingStatus.Fulfilled or
                    NCPropertyListingStatus.Expired) || property.Snapshot.Owner != request.Seller ||
                listing.Snapshot.Seller != request.Seller || property.Snapshot.LegalState != NCPropertyLegalState.Clear ||
                !Available(property.Snapshot, listing.Snapshot.OfferKind))
                return Reject(NCPropertyAggregateMutationStatus.Rejected,
                    "The stable listing cannot be reopened in the current property state.");
            var eventId = Guid.NewGuid();
            var proposed = listing.Snapshot with
            {
                Status = NCPropertyListingStatus.Open,
                ExpiresAt = null,
                UpdatedAt = now,
                DataVersion = checked(listing.Snapshot.DataVersion + 1),
                LastAppliedEventId = eventId,
            };
            var persisted = await Persist(request.CommandId, "living-city.property-listing.reopen",
                "property-listing", request.ListingId.ToString(),
                [Write(listing, proposed, eventId, "living-city.property-listing.reopened", request.AuditSource)],
                request.CorrelationId, request.CausationId, epoch, now);
            if (persisted.Status is not (NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted))
                return Reject(Map(persisted.Status), persisted.Error ?? "The listing was not reopened.");
            return new(persisted.Status == NCPersistentMutationStatus.Applied
                    ? NCPropertyAggregateMutationStatus.Applied
                    : NCPropertyAggregateMutationStatus.Duplicate,
                await GetListingAsync(request.ListingId, true), null, null, null);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NCPropertyMarketMutationResult> CloseListingAsync(NCClosePropertyListingRequest request)
    {
        if (request.CommandId == Guid.Empty || !request.ListingId.IsValid || request.ExpectedListingVersion <= 0 ||
            !request.Seller.IsValid || request.CorrelationId == Guid.Empty || !ValidAudit(request.AuditSource) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject(NCPropertyAggregateMutationStatus.Rejected, "The listing close request is invalid.");
        var gate = Gate(request.ListingId.Value);
        await gate.WaitAsync();
        try
        {
            var listing = await GetListingAsync(request.ListingId, true);
            if (listing == null)
                return Reject(NCPropertyAggregateMutationStatus.NotFound, "The listing does not exist.");
            if (listing.AggregateVersion != request.ExpectedListingVersion)
                return Reject(NCPropertyAggregateMutationStatus.VersionConflict, "The listing version changed.");
            if (listing.Snapshot.Status != NCPropertyListingStatus.Open ||
                listing.Snapshot.Seller != request.Seller ||
                request.Expired && (listing.Snapshot.ExpiresAt is not { } expiry || now < expiry))
                return Reject(NCPropertyAggregateMutationStatus.NotAuthorized,
                    "The listing cannot be closed by this seller or at this time.");
            var eventId = Guid.NewGuid();
            var proposed = listing.Snapshot with
            {
                Status = request.Expired ? NCPropertyListingStatus.Expired : NCPropertyListingStatus.Cancelled,
                UpdatedAt = now,
                DataVersion = checked(listing.Snapshot.DataVersion + 1),
                LastAppliedEventId = eventId,
            };
            var persisted = await Persist(request.CommandId, "living-city.property-listing.close",
                "property-listing", request.ListingId.ToString(),
                [Write(listing, proposed, eventId,
                    request.Expired ? "living-city.property-listing.expired" :
                        "living-city.property-listing.cancelled", request.AuditSource)],
                request.CorrelationId, request.CausationId, epoch, now);
            if (persisted.Status is not (NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted))
                return Reject(Map(persisted.Status), persisted.Error ?? "The listing was not closed.");
            return new(persisted.Status == NCPersistentMutationStatus.Applied
                    ? NCPropertyAggregateMutationStatus.Applied
                    : NCPropertyAggregateMutationStatus.Duplicate,
                await GetListingAsync(request.ListingId, true), null, null, null);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Cancels or expires an active reservation and releases its quote/listing in the same persistent batch.
    /// </summary>
    public async Task<NCPropertyMarketMutationResult> CloseReservationAsync(
        NCClosePropertyReservationRequest request)
    {
        if (request.CommandId == Guid.Empty || !request.ReservationId.IsValid ||
            request.ExpectedReservationVersion <= 0 || !request.Actor.IsValid ||
            request.CorrelationId == Guid.Empty || !ValidAudit(request.AuditSource) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject(NCPropertyAggregateMutationStatus.Rejected, "The reservation close request is invalid.");
        var initial = await GetReservationAsync(request.ReservationId, true);
        if (initial == null)
            return Reject(NCPropertyAggregateMutationStatus.NotFound, "The reservation does not exist.");
        var gates = await AcquireGates(request.ReservationId.Value, initial.Snapshot.QuoteId.Value,
            initial.Snapshot.ListingId.Value);
        try
        {
            var reservation = await GetReservationAsync(request.ReservationId, true);
            var quote = reservation == null ? null : await GetQuoteAsync(reservation.Snapshot.QuoteId, true);
            var listing = reservation == null ? null : await GetListingAsync(reservation.Snapshot.ListingId, true);
            if (reservation == null || quote == null || listing == null)
                return Reject(NCPropertyAggregateMutationStatus.NotFound,
                    "A reservation participant does not exist.");
            if (reservation.AggregateVersion != request.ExpectedReservationVersion)
                return Reject(NCPropertyAggregateMutationStatus.VersionConflict,
                    "The reservation version changed.");
            if (reservation.Snapshot.Status != NCPropertyReservationStatus.Active ||
                (!request.Expired && request.Actor != reservation.Snapshot.Buyer &&
                 request.Actor != reservation.Snapshot.Seller) ||
                (request.Expired && now < reservation.Snapshot.ExpiresAt))
                return Reject(NCPropertyAggregateMutationStatus.NotAuthorized,
                    "The reservation cannot be closed by this actor or at this time.");
            var reservationEvent = Guid.NewGuid();
            var nextReservation = reservation.Snapshot with
            {
                Status = request.Expired ? NCPropertyReservationStatus.Expired :
                    NCPropertyReservationStatus.Cancelled,
                UpdatedAt = now,
                DataVersion = checked(reservation.Snapshot.DataVersion + 1),
                LastAppliedEventId = reservationEvent,
            };
            var quoteEvent = Guid.NewGuid();
            var nextQuote = quote.Snapshot with
            {
                Status = request.Expired ? NCPropertyQuoteStatus.Expired : NCPropertyQuoteStatus.Cancelled,
                UpdatedAt = now,
                DataVersion = checked(quote.Snapshot.DataVersion + 1),
                LastAppliedEventId = quoteEvent,
            };
            var listingEvent = Guid.NewGuid();
            var nextListing = listing.Snapshot with
            {
                Status = NCPropertyListingStatus.Open,
                UpdatedAt = now,
                DataVersion = checked(listing.Snapshot.DataVersion + 1),
                LastAppliedEventId = listingEvent,
            };
            var persisted = await Persist(request.CommandId, "living-city.property-reservation.close",
                "property-reservation", request.ReservationId.ToString(),
                [
                    Write(reservation, nextReservation, reservationEvent,
                        request.Expired ? "living-city.property-reservation.expired" :
                            "living-city.property-reservation.cancelled", request.AuditSource),
                    Write(quote, nextQuote, quoteEvent,
                        request.Expired ? "living-city.property-quote.expired" :
                            "living-city.property-quote.cancelled", request.AuditSource),
                    Write(listing, nextListing, listingEvent, "living-city.property-listing.released",
                        request.AuditSource),
                ], request.CorrelationId, request.CausationId, epoch, now);
            if (persisted.Status is not (NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted))
                return Reject(Map(persisted.Status), persisted.Error ?? "The reservation was not closed.");
            return new(persisted.Status == NCPersistentMutationStatus.Applied
                    ? NCPropertyAggregateMutationStatus.Applied
                    : NCPropertyAggregateMutationStatus.Duplicate,
                await GetListingAsync(listing.Snapshot.ListingId, true),
                await GetQuoteAsync(quote.Snapshot.QuoteId, true),
                await GetReservationAsync(reservation.Snapshot.ReservationId, true), null);
        }
        finally
        {
            ReleaseGates(gates);
        }
    }

    public async Task<NCPropertyListingDiscoveryPage> DiscoverAsync(
        string? afterAggregateId,
        int limit,
        NCPropertyOfferKind? offerKind = null)
    {
        if (!TryPolicy(out var policy) || limit is <= 0 || limit > policy.MaximumDiscoveryResults ||
            !_clock.TryGetCurrentTime(out _, out var now))
            return new([], afterAggregateId, "The property discovery request is invalid.");
        var page = await _database.GetNCAggregateSnapshotPageAsync(ListingAggregateType,
            string.IsNullOrWhiteSpace(afterAggregateId) ? null : afterAggregateId.Trim(),
            Math.Min(policy.MaximumDiscoveryResults, limit * 4));
        var listings = new List<NCPropertyListingRecord>(limit);
        foreach (var stored in page)
        {
            if (listings.Count >= limit || !Guid.TryParse(stored.AggregateId, out var raw))
                continue;
            var listing = await GetListingAsync(new NCPropertyListingId(raw), true);
            if (listing?.Snapshot.Status != NCPropertyListingStatus.Open ||
                listing.Snapshot.ExpiresAt is { } expiry && now >= expiry ||
                offerKind is { } kind && listing.Snapshot.OfferKind != kind)
                continue;
            var property = await _properties.GetAsync(listing.Snapshot.PropertyId, true);
            if (property == null || property.Snapshot.Owner != listing.Snapshot.Seller ||
                property.Snapshot.LegalState != NCPropertyLegalState.Clear ||
                !Available(property.Snapshot, listing.Snapshot.OfferKind))
                continue;
            listings.Add(listing);
        }
        return new(listings, page.Count == 0 ? afterAggregateId : page[^1].AggregateId, null);
    }

    public bool TryPrepareSettlementWrites(
        NCPropertyListingRecord listing,
        NCPropertyQuoteRecord quote,
        NCPropertyReservationRecord reservation,
        NCTransactionId transactionId,
        NCActiveWorldTime now,
        string auditSource,
        out IReadOnlyList<NCAtomicLedgerParticipantWrite> writes)
    {
        writes = [];
        if (!transactionId.IsValid || !now.IsValid || string.IsNullOrWhiteSpace(auditSource) ||
            listing.Snapshot.Status != NCPropertyListingStatus.Reserved ||
            quote.Snapshot.Status != NCPropertyQuoteStatus.Reserved ||
            reservation.Snapshot.Status != NCPropertyReservationStatus.Active || reservation.Snapshot.ExpiresAt <= now ||
            quote.Snapshot.ListingId != listing.Snapshot.ListingId ||
            reservation.Snapshot.ListingId != listing.Snapshot.ListingId ||
            reservation.Snapshot.QuoteId != quote.Snapshot.QuoteId)
            return false;
        var listingEvent = Guid.NewGuid();
        var nextListing = listing.Snapshot with
        {
            Status = NCPropertyListingStatus.Fulfilled,
            UpdatedAt = now,
            DataVersion = checked(listing.Snapshot.DataVersion + 1),
            LastAppliedEventId = listingEvent,
        };
        var quoteEvent = Guid.NewGuid();
        var nextQuote = quote.Snapshot with
        {
            Status = NCPropertyQuoteStatus.Settled,
            UpdatedAt = now,
            DataVersion = checked(quote.Snapshot.DataVersion + 1),
            LastAppliedEventId = quoteEvent,
        };
        var reservationEvent = Guid.NewGuid();
        var nextReservation = reservation.Snapshot with
        {
            Status = NCPropertyReservationStatus.Settled,
            SettlementTransactionId = transactionId,
            UpdatedAt = now,
            DataVersion = checked(reservation.Snapshot.DataVersion + 1),
            LastAppliedEventId = reservationEvent,
        };
        writes =
        [
            Participant(listing, nextListing, listingEvent, "living-city.property-listing.fulfilled", auditSource),
            Participant(quote, nextQuote, quoteEvent, "living-city.property-quote.settled", auditSource),
            Participant(reservation, nextReservation, reservationEvent,
                "living-city.property-reservation.settled", auditSource),
        ];
        return true;
    }

    private async Task<TRecord?> GetAsync<TId, TRecord>(
        TId id,
        string aggregateType,
        ConcurrentDictionary<TId, TRecord> cache,
        Func<NCAggregateSnapshotData?, TId, TRecord?> deserialize,
        bool forceReload) where TId : notnull where TRecord : class
    {
        var raw = id.ToString();
        if (string.IsNullOrWhiteSpace(raw) || raw == Guid.Empty.ToString("D"))
            return null;
        if (!forceReload && cache.TryGetValue(id, out var cached))
            return cached;
        var stored = await _database.GetNCAggregateSnapshotAsync(aggregateType, raw);
        var record = deserialize(stored, id);
        if (record != null)
            cache[id] = record;
        return record;
    }

    private Task<NCPersistentBatchMutationResult> Persist(
        Guid commandId,
        string commandType,
        string scopeType,
        string scopeId,
        IReadOnlyList<NCPersistentAggregateWrite> writes,
        Guid correlationId,
        Guid? causationId,
        NCSessionEpoch epoch,
        NCActiveWorldTime now) => _database.ApplyNCPersistentBatchMutationAsync(
        new NCPersistentBatchMutationRequest(commandId, commandType, scopeType, scopeId, writes,
            JsonSerializer.Serialize(new Receipt(scopeId), JsonOptions), correlationId, causationId, epoch, now, []));

    private static NCPersistentAggregateWrite Write(NCPropertyListingRecord? current,
        NCPropertyListingSnapshot next, Guid eventId, string eventType, string audit) => new(eventId, eventType,
        ListingAggregateType, next.ListingId.ToString(), current?.AggregateVersion ?? 0, SnapshotFormatVersion,
        JsonSerializer.Serialize(next, JsonOptions), JsonSerializer.Serialize(new MarketEvent(next.ListingId.ToString(),
            audit), JsonOptions));
    private static NCPersistentAggregateWrite Write(NCPropertyQuoteRecord? current,
        NCPropertyQuoteSnapshot next, Guid eventId, string eventType, string audit) => new(eventId, eventType,
        QuoteAggregateType, next.QuoteId.ToString(), current?.AggregateVersion ?? 0, SnapshotFormatVersion,
        JsonSerializer.Serialize(next, JsonOptions), JsonSerializer.Serialize(new MarketEvent(next.QuoteId.ToString(),
            audit), JsonOptions));
    private static NCPersistentAggregateWrite Write(NCPropertyReservationRecord? current,
        NCPropertyReservationSnapshot next, Guid eventId, string eventType, string audit) => new(eventId, eventType,
        ReservationAggregateType, next.ReservationId.ToString(), current?.AggregateVersion ?? 0, SnapshotFormatVersion,
        JsonSerializer.Serialize(next, JsonOptions), JsonSerializer.Serialize(new MarketEvent(
            next.ReservationId.ToString(), audit), JsonOptions));

    private static NCAtomicLedgerParticipantWrite Participant(NCPropertyListingRecord current,
        NCPropertyListingSnapshot next, Guid eventId, string eventType, string audit) => new(eventId, eventType,
        ListingAggregateType, next.ListingId.ToString(), current.AggregateVersion, SnapshotFormatVersion,
        JsonSerializer.Serialize(next, JsonOptions), JsonSerializer.Serialize(new MarketEvent(next.ListingId.ToString(),
            audit), JsonOptions));
    private static NCAtomicLedgerParticipantWrite Participant(NCPropertyQuoteRecord current,
        NCPropertyQuoteSnapshot next, Guid eventId, string eventType, string audit) => new(eventId, eventType,
        QuoteAggregateType, next.QuoteId.ToString(), current.AggregateVersion, SnapshotFormatVersion,
        JsonSerializer.Serialize(next, JsonOptions), JsonSerializer.Serialize(new MarketEvent(next.QuoteId.ToString(),
            audit), JsonOptions));
    private static NCAtomicLedgerParticipantWrite Participant(NCPropertyReservationRecord current,
        NCPropertyReservationSnapshot next, Guid eventId, string eventType, string audit) => new(eventId, eventType,
        ReservationAggregateType, next.ReservationId.ToString(), current.AggregateVersion, SnapshotFormatVersion,
        JsonSerializer.Serialize(next, JsonOptions), JsonSerializer.Serialize(new MarketEvent(
            next.ReservationId.ToString(), audit), JsonOptions));

    private bool TryPolicy(out NCPropertyMarketPolicyPrototype policy) =>
        _prototypes.TryIndex(DefaultPolicy, out policy!) && policy.MaximumListingLifetime > TimeSpan.Zero &&
        policy.MaximumQuoteLifetime > TimeSpan.Zero && policy.MaximumReservationLifetime > TimeSpan.Zero &&
        policy.MaximumDiscoveryResults is > 0 and <= 500;
    private static bool Valid(NCCreatePropertyListingRequest value, NCPropertyMarketPolicyPrototype policy) =>
        value.CommandId != Guid.Empty && value.ListingId.IsValid && value.PropertyId.IsValid &&
        value.ExpectedPropertyVersion > 0 && !string.IsNullOrWhiteSpace(value.SourceListingId) &&
        value.SourceListingId.Length <= 128 && Enum.IsDefined(value.OfferKind) && value.Price >= 0 &&
        value.SecurityDeposit >= 0 && value.Seller.IsValid && value.CorrelationId != Guid.Empty &&
        value.OccupantPermissions != NCPropertyPermission.None &&
        (value.OccupantPermissions & ~NCPropertyPermission.All) == 0 &&
        (value.Lifetime == null || value.Lifetime > TimeSpan.Zero && value.Lifetime <= policy.MaximumListingLifetime) &&
        (value.OfferKind == NCPropertyOfferKind.Sale && value.SecurityDeposit == 0 &&
         string.IsNullOrEmpty(value.RentPolicyId) && value.BillingIntervalTicks == 0 && value.TemplateId == null &&
         value.TemplateRevision == null || value.OfferKind == NCPropertyOfferKind.Rent &&
         !string.IsNullOrWhiteSpace(value.RentPolicyId) && value.RentPolicyId.Length <= 64 &&
         value.BillingIntervalTicks > 0 && !string.IsNullOrWhiteSpace(value.TemplateId) &&
         value.TemplateId.Length <= 64 && value.TemplateRevision > 0) && ValidAudit(value.AuditSource);
    private static bool Valid(NCCreatePropertyQuoteRequest value, NCPropertyMarketPolicyPrototype policy) =>
        value.CommandId != Guid.Empty && value.QuoteId.IsValid && value.ListingId.IsValid &&
        value.ExpectedListingVersion > 0 && value.Buyer.IsValid && value.Lifetime > TimeSpan.Zero &&
        value.Lifetime <= policy.MaximumQuoteLifetime && value.CorrelationId != Guid.Empty && ValidAudit(value.AuditSource);
    private static bool Valid(NCReservePropertyQuoteRequest value, NCPropertyMarketPolicyPrototype policy) =>
        value.CommandId != Guid.Empty && value.ReservationId.IsValid && value.QuoteId.IsValid &&
        value.ExpectedQuoteVersion > 0 && value.Buyer.IsValid && value.Lifetime > TimeSpan.Zero &&
        value.Lifetime <= policy.MaximumReservationLifetime && value.CorrelationId != Guid.Empty &&
        ValidAudit(value.AuditSource);
    private static bool Same(NCPropertyListingSnapshot value, NCCreatePropertyListingRequest request) =>
        value.ListingId == request.ListingId && value.PropertyId == request.PropertyId &&
        value.SourceListingId == request.SourceListingId.Trim() && value.OfferKind == request.OfferKind &&
        value.Seller == request.Seller && value.Price == request.Price &&
        value.SecurityDeposit == request.SecurityDeposit && value.RentPolicyId == request.RentPolicyId.Trim() &&
        value.BillingIntervalTicks == request.BillingIntervalTicks && value.TemplateId == request.TemplateId?.Trim() &&
        value.TemplateRevision == request.TemplateRevision && value.OccupantPermissions == request.OccupantPermissions;
    private static bool Available(NCPropertyAggregateSnapshot property, NCPropertyOfferKind kind) => kind switch
    {
        NCPropertyOfferKind.Rent => property.Status == NCPropertyStatus.Available,
        NCPropertyOfferKind.Sale => property.Status is NCPropertyStatus.Available or NCPropertyStatus.OwnerOccupied or
            NCPropertyStatus.ResaleListed,
        _ => false,
    };
    private static bool ValidAudit(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;

    private static NCPropertyListingRecord? DeserializeListing(NCAggregateSnapshotData? stored,
        NCPropertyListingId id) => Deserialize<NCPropertyListingSnapshot, NCPropertyListingRecord>(stored,
        value => value.ListingId == id,
        value => new NCPropertyListingRecord(value, stored!.AggregateVersion, stored.SessionEpoch,
            stored.ActiveWorldTime));
    private static NCPropertyQuoteRecord? DeserializeQuote(NCAggregateSnapshotData? stored, NCPropertyQuoteId id) =>
        Deserialize<NCPropertyQuoteSnapshot, NCPropertyQuoteRecord>(stored, value => value.QuoteId == id,
            value => new NCPropertyQuoteRecord(value,
            stored!.AggregateVersion, stored.SessionEpoch, stored.ActiveWorldTime));
    private static NCPropertyReservationRecord? DeserializeReservation(NCAggregateSnapshotData? stored,
        NCPropertyReservationId id) => Deserialize<NCPropertyReservationSnapshot, NCPropertyReservationRecord>(stored,
        value => value.ReservationId == id,
        value => new NCPropertyReservationRecord(value, stored!.AggregateVersion, stored.SessionEpoch,
            stored.ActiveWorldTime));
    private static TRecord? Deserialize<TSnapshot, TRecord>(NCAggregateSnapshotData? stored,
        Func<TSnapshot, bool> valid, Func<TSnapshot, TRecord> create) where TRecord : class
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<TSnapshot>(stored.Payload, JsonOptions);
            return snapshot != null && valid(snapshot) ? create(snapshot) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private SemaphoreSlim Gate(Guid id) => _gates[(id.GetHashCode() & int.MaxValue) % _gates.Length];
    private async Task<SemaphoreSlim[]> AcquireGates(params Guid[] ids)
    {
        var gates = ids.Select(Gate).Distinct().OrderBy(gate => Array.IndexOf(_gates, gate)).ToArray();
        foreach (var gate in gates)
            await gate.WaitAsync();
        return gates;
    }
    private static void ReleaseGates(IReadOnlyList<SemaphoreSlim> gates)
    {
        for (var index = gates.Count - 1; index >= 0; index--)
            gates[index].Release();
    }
    private static NCPropertyAggregateMutationStatus Map(NCPersistentMutationStatus status) => status switch
    {
        NCPersistentMutationStatus.VersionConflict => NCPropertyAggregateMutationStatus.VersionConflict,
        NCPersistentMutationStatus.ClockRejected => NCPropertyAggregateMutationStatus.ClockUnavailable,
        _ => NCPropertyAggregateMutationStatus.StorageFailure,
    };
    private static NCPropertyMarketMutationResult Reject(NCPropertyAggregateMutationStatus status, string error) =>
        new(status, null, null, null, error);
    private sealed record MarketEvent(string SubjectId, string AuditSource);
    private sealed record Receipt(string SubjectId);
}
