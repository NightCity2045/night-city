// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Text.Json;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Market;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Market;

/// <summary>
/// Persistent discovery authority. A listing advertises an economic asset but never transfers it; purchase callers
/// must cross the existing quote/reservation/settlement boundary after this system revalidates access and stock.
/// </summary>
public sealed partial class NCMarketRegistrySystem : EntitySystem
{
    public const string ListingAggregateType = "living-city.market-listing";
    public const string RequestAggregateType = "living-city.market-request";
    private const string PartyIndexAggregateType = "living-city.market-party-index";
    private const int SnapshotFormatVersion = 1;
    private static readonly ProtoId<NCMarketPolicyPrototype> DefaultPolicy = "Default";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPartyResolverSystem _parties = default!;
    [Dependency] private NCEconomicAssetRegistrySystem _assets = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ITaskManager _tasks = default!;

    private readonly ConcurrentDictionary<NCMarketListingId, NCMarketListingRecord> _listings = [];
    private readonly ConcurrentDictionary<NCMarketRequestId, NCMarketRequestRecord> _requests = [];
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public int CachedListingCount => _listings.Count;
    public int CachedRequestCount => _requests.Count;

    public async Task<NCMarketListingRecord?> GetListingAsync(NCMarketListingId id, bool forceReload = false)
    {
        if (!id.IsValid)
            return null;
        if (!forceReload && _listings.TryGetValue(id, out var cached))
            return cached;
        var stored = await _database.GetNCAggregateSnapshotAsync(ListingAggregateType, id.ToString());
        var record = DeserializeListing(stored, id);
        return record == null ? null : _listings.AddOrUpdate(id, record,
            (_, current) => current.AggregateVersion > record.AggregateVersion ? current : record);
    }

    public async Task<NCMarketRequestRecord?> GetRequestAsync(NCMarketRequestId id, bool forceReload = false)
    {
        if (!id.IsValid)
            return null;
        if (!forceReload && _requests.TryGetValue(id, out var cached))
            return cached;
        var stored = await _database.GetNCAggregateSnapshotAsync(RequestAggregateType, id.ToString());
        var record = DeserializeRequest(stored, id);
        return record == null ? null : _requests.AddOrUpdate(id, record,
            (_, current) => current.AggregateVersion > record.AggregateVersion ? current : record);
    }

    /// <summary>
    /// Finds one still-live request without exposing the party index as a gameplay authority.
    /// </summary>
    public async Task<NCMarketRequestRecord?> FindOpenRequestAsync(
        NCPartyRef requester,
        NCMarketRequestKind kind,
        string definitionId)
    {
        if (!requester.IsValid || string.IsNullOrWhiteSpace(definitionId) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
            return null;
        var index = await GetIndexAsync(requester);
        foreach (var id in index.RequestIds)
        {
            var request = await GetRequestAsync(id, true);
            if (request?.SessionEpoch == epoch && request.Snapshot.Status == NCMarketRequestStatus.Open &&
                request.Snapshot.ExpiresAt > now &&
                request.Snapshot.Kind == kind && request.Snapshot.DefinitionId == definitionId.Trim())
                return request;
        }
        return null;
    }

    /// <summary>
    /// Returns a bounded page of live persistent demand for server-side planners. Old round epochs and expired
    /// requests are excluded so offline state cannot create an event in an unrelated round.
    /// </summary>
    public async Task<IReadOnlyList<NCMarketRequestRecord>> GetOpenRequestsAsync(int limit)
    {
        if (!TryPolicy(out var policy) || limit <= 0 || limit > policy.MaximumDiscoveryResults ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
            return [];

        var result = new List<NCMarketRequestRecord>(limit);
        var maximumScanned = Math.Min(policy.MaximumDiscoveryResults, Math.Max(limit * 4, limit));
        var scanned = 0;
        string? after = null;
        while (result.Count < limit && scanned < maximumScanned)
        {
            var pageSize = Math.Min(64, maximumScanned - scanned);
            var page = await _database.GetNCAggregateSnapshotPageAsync(RequestAggregateType, after, pageSize);
            if (page.Count == 0)
                break;
            scanned += page.Count;
            after = page[^1].AggregateId;
            foreach (var stored in page)
            {
                if (result.Count >= limit || !Guid.TryParse(stored.AggregateId, out var raw))
                    continue;
                var request = await GetRequestAsync(new NCMarketRequestId(raw), true);
                if (request?.SessionEpoch != epoch || request.Snapshot.Status != NCMarketRequestStatus.Open ||
                    request.Snapshot.ExpiresAt <= now)
                    continue;
                result.Add(request);
            }
            if (page.Count < pageSize)
                break;
        }
        return result;
    }

    public async Task<NCMarketListingMutationResult> CreateListingAsync(NCCreateMarketListingRequest request)
    {
        if (!TryPolicy(out var policy) || !Valid(request, policy) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
            return RejectListing(_clock.IsReady ? NCMarketMutationStatus.Rejected : NCMarketMutationStatus.ClockUnavailable,
                "The listing request, policy, or active-world clock is invalid.");
        if (!(await _parties.ResolveAsync(request.Seller, true)).IsResolved ||
            !(await _parties.ResolveAsync(request.Initiator, true)).IsResolved || request.Initiator != request.Seller)
            return RejectListing(NCMarketMutationStatus.PartyUnavailable,
                "Only the active seller can publish this listing.");

        var gate = Gate(request.ListingId.Value);
        await gate.WaitAsync();
        try
        {
            var existing = await GetListingAsync(request.ListingId, true);
            if (existing != null)
                return Same(existing.Snapshot, request)
                    ? new(NCMarketMutationStatus.Duplicate, existing, null)
                    : RejectListing(NCMarketMutationStatus.Rejected,
                        "The listing identity belongs to different immutable data.");

            var offerError = await ValidateOfferAsync(request);
            if (offerError != null)
                return RejectListing(NCMarketMutationStatus.Rejected, offerError);
            var index = await CompactIndexAsync(await GetIndexAsync(request.Seller), now);
            if (index.ListingIds.Count >= policy.MaximumListingsPerParty)
                return RejectListing(NCMarketMutationStatus.CapacityExceeded,
                    "The seller listing capacity is exhausted.");

            var listingEvent = Guid.NewGuid();
            var listing = new NCMarketListingSnapshot(
                request.ListingId,
                request.OfferKind,
                CanonicalOffer(request.OfferKind, request.OfferId),
                request.MaximumQuantity,
                request.Seller,
                request.SourceStockNodeId,
                request.Channel,
                Normalize(request.Access),
                NCMarketListingStatus.Open,
                request.Lifetime is { } lifetime ? now.Advance(lifetime) : null,
                now,
                now,
                1,
                listingEvent);
            var indexEvent = Guid.NewGuid();
            var nextIndex = index with
            {
                ListingIds = index.ListingIds.Append(request.ListingId).OrderBy(id => id.Value).ToArray(),
                UpdatedAt = now,
                DataVersion = checked(index.DataVersion + 1),
                LastAppliedEventId = indexEvent,
            };
            var result = await _database.ApplyNCPersistentBatchMutationAsync(new NCPersistentBatchMutationRequest(
                request.CommandId,
                "living-city.market-listing.create",
                "market-listing",
                request.ListingId.ToString(),
                [
                    Write(null, listing, listingEvent, "living-city.market-listing.created", request.AuditSource),
                    Write(index, nextIndex, indexEvent, "living-city.market-party-index.listing-added",
                        request.AuditSource),
                ],
                JsonSerializer.Serialize(new Receipt(request.ListingId.ToString(), 1), JsonOptions),
                request.CorrelationId,
                request.CausationId,
                epoch,
                now,
                []));
            if (result.Status is not (NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted))
                return RejectListing(Map(result.Status), result.Error ?? "The listing was not committed.");
            var loaded = await GetListingAsync(request.ListingId, true);
            return loaded == null
                ? RejectListing(NCMarketMutationStatus.StorageFailure, "The committed listing could not be reloaded.")
                : new(result.Status == NCPersistentMutationStatus.Applied
                    ? NCMarketMutationStatus.Applied
                    : NCMarketMutationStatus.Duplicate, loaded, null);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to create market listing {request.ListingId}: {exception}");
            return RejectListing(NCMarketMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NCMarketRequestMutationResult> CreateRequestAsync(NCCreateMarketRequestRequest request)
    {
        if (!TryPolicy(out var policy) || !Valid(request, policy) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
            return RejectRequest(_clock.IsReady ? NCMarketMutationStatus.Rejected : NCMarketMutationStatus.ClockUnavailable,
                "The market request, policy, or active-world clock is invalid.");
        if (!(await _parties.ResolveAsync(request.Requester, true)).IsResolved || request.Initiator != request.Requester)
            return RejectRequest(NCMarketMutationStatus.PartyUnavailable,
                "Only the active requester can publish this request.");

        var gate = Gate(request.RequestId.Value);
        await gate.WaitAsync();
        try
        {
            var existing = await GetRequestAsync(request.RequestId, true);
            if (existing != null)
                return Same(existing.Snapshot, request)
                    ? new(NCMarketMutationStatus.Duplicate, existing, null)
                    : RejectRequest(NCMarketMutationStatus.Rejected,
                        "The request identity belongs to different immutable data.");
            var index = await CompactIndexAsync(await GetIndexAsync(request.Requester), now);
            if (index.RequestIds.Count >= policy.MaximumRequestsPerParty)
                return RejectRequest(NCMarketMutationStatus.CapacityExceeded,
                    "The requester capacity is exhausted.");

            var requestEvent = Guid.NewGuid();
            var snapshot = new NCMarketRequestSnapshot(
                request.RequestId,
                request.Kind,
                request.DefinitionId.Trim(),
                request.Quantity,
                request.MaximumUnitPrice,
                request.MinimumQuality,
                request.Requester,
                request.Channel,
                Normalize(request.Access),
                request.PreferredZone,
                NCMarketRequestStatus.Open,
                null,
                now.Advance(request.Lifetime),
                now,
                now,
                1,
                requestEvent);
            var indexEvent = Guid.NewGuid();
            var nextIndex = index with
            {
                RequestIds = index.RequestIds.Append(request.RequestId).OrderBy(id => id.Value).ToArray(),
                UpdatedAt = now,
                DataVersion = checked(index.DataVersion + 1),
                LastAppliedEventId = indexEvent,
            };
            var result = await _database.ApplyNCPersistentBatchMutationAsync(new NCPersistentBatchMutationRequest(
                request.CommandId,
                "living-city.market-request.create",
                "market-request",
                request.RequestId.ToString(),
                [
                    Write(null, snapshot, requestEvent, "living-city.market-request.created", request.AuditSource),
                    Write(index, nextIndex, indexEvent, "living-city.market-party-index.request-added",
                        request.AuditSource),
                ],
                JsonSerializer.Serialize(new Receipt(request.RequestId.ToString(), 1), JsonOptions),
                request.CorrelationId,
                request.CausationId,
                epoch,
                now,
                []));
            if (result.Status is not (NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted))
                return RejectRequest(Map(result.Status), result.Error ?? "The request was not committed.");
            var loaded = await GetRequestAsync(request.RequestId, true);
            if (loaded == null)
                return RejectRequest(NCMarketMutationStatus.StorageFailure,
                    "The committed request could not be reloaded.");
            Publish(new NCMarketAggregateChangedEvent(RequestAggregateType, request.RequestId.ToString()));
            return new(result.Status == NCPersistentMutationStatus.Applied
                ? NCMarketMutationStatus.Applied
                : NCMarketMutationStatus.Duplicate, loaded, null);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to create market request {request.RequestId}: {exception}");
            return RejectRequest(NCMarketMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NCMarketDiscoveryPage> DiscoverAsync(NCMarketDiscoveryRequest request)
    {
        if (!TryPolicy(out var policy) || request.Limit is <= 0 || request.Limit > policy.MaximumDiscoveryResults ||
            request.Query.Length > 128 || !Valid(request.Viewer) ||
            !(await _parties.ResolveAsync(request.Viewer.Viewer, true)).IsResolved ||
            !_clock.TryGetCurrentTime(out _, out var now))
            return new([], null, "The discovery request is invalid or the active round is unavailable.");
        var page = await _database.GetNCAggregateSnapshotPageAsync(
            ListingAggregateType,
            string.IsNullOrWhiteSpace(request.AfterAggregateId) ? null : request.AfterAggregateId.Trim(),
            Math.Min(policy.MaximumDiscoveryResults, Math.Max(request.Limit * 4, request.Limit)));
        var result = new List<NCMarketListingRecord>(request.Limit);
        foreach (var stored in page)
        {
            if (result.Count >= request.Limit || !Guid.TryParse(stored.AggregateId, out var raw))
                continue;
            var listing = await GetListingAsync(new NCMarketListingId(raw), true);
            if (listing == null || !CanView(listing.Snapshot, request.Viewer, now) ||
                request.Channel is { } channel && listing.Snapshot.Channel != channel ||
                request.OfferKind is { } kind && listing.Snapshot.OfferKind != kind ||
                !string.IsNullOrWhiteSpace(request.Query) &&
                !listing.Snapshot.OfferId.Contains(request.Query.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;
            if (!await IsOfferAvailableAsync(listing.Snapshot))
                continue;
            result.Add(listing);
        }
        return new(result, page.Count == 0 ? request.AfterAggregateId : page[^1].AggregateId, null);
    }

    public async Task<NCMarketListingMutationResult> ChangeListingStatusAsync(
        NCChangeMarketListingStatusRequest request)
    {
        if (request.CommandId == Guid.Empty || !request.ListingId.IsValid || request.ExpectedVersion <= 0 ||
            !Enum.IsDefined(request.Status) || !request.Initiator.IsValid || request.CorrelationId == Guid.Empty ||
            !ValidAudit(request.AuditSource) || !_clock.TryGetCurrentTime(out var epoch, out var now))
            return RejectListing(_clock.IsReady ? NCMarketMutationStatus.Rejected : NCMarketMutationStatus.ClockUnavailable,
                "The listing status request is invalid.");
        var gate = Gate(request.ListingId.Value);
        await gate.WaitAsync();
        try
        {
            var current = await GetListingAsync(request.ListingId, true);
            if (current == null)
                return RejectListing(NCMarketMutationStatus.NotFound, "The listing does not exist.");
            if (current.AggregateVersion != request.ExpectedVersion)
                return RejectListing(NCMarketMutationStatus.VersionConflict, "The listing version changed.");
            if (request.Initiator != current.Snapshot.Seller ||
                !CanTransition(current.Snapshot.Status, request.Status) ||
                request.Status == NCMarketListingStatus.Expired &&
                (current.Snapshot.ExpiresAt is not { } expiry || now < expiry))
                return RejectListing(NCMarketMutationStatus.Rejected, "The listing transition is not allowed.");
            var eventId = Guid.NewGuid();
            var next = current.Snapshot with
            {
                Status = request.Status,
                UpdatedAt = now,
                DataVersion = checked(current.Snapshot.DataVersion + 1),
                LastAppliedEventId = eventId,
            };
            var persisted = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(
                request.CommandId, "living-city.market-listing.status-changed", "market-listing",
                request.ListingId.ToString(), eventId, "living-city.market-listing.status-changed",
                ListingAggregateType, request.ListingId.ToString(), current.AggregateVersion, SnapshotFormatVersion,
                JsonSerializer.Serialize(next, JsonOptions),
                JsonSerializer.Serialize(new MarketEvent(request.ListingId.ToString(), request.AuditSource), JsonOptions),
                JsonSerializer.Serialize(new Receipt(request.ListingId.ToString(), current.AggregateVersion + 1),
                    JsonOptions), request.CorrelationId, request.CausationId, epoch, now, []));
            if (persisted.Status is not (NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted))
                return RejectListing(Map(persisted.Status), persisted.Error ?? "The listing status was not committed.");
            var loaded = await GetListingAsync(request.ListingId, true);
            return loaded == null
                ? RejectListing(NCMarketMutationStatus.StorageFailure, "The listing status could not be reloaded.")
                : new(persisted.Status == NCPersistentMutationStatus.Applied
                    ? NCMarketMutationStatus.Applied
                    : NCMarketMutationStatus.Duplicate, loaded, null);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NCMarketRequestMutationResult> ChangeRequestStatusAsync(
        NCChangeMarketRequestStatusRequest request)
    {
        if (request.CommandId == Guid.Empty || !request.RequestId.IsValid || request.ExpectedVersion <= 0 ||
            !Enum.IsDefined(request.Status) || request.MatchedListingId is { IsValid: false } ||
            !request.Initiator.IsValid || request.CorrelationId == Guid.Empty || !ValidAudit(request.AuditSource) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
            return RejectRequest(_clock.IsReady ? NCMarketMutationStatus.Rejected : NCMarketMutationStatus.ClockUnavailable,
                "The market request status change is invalid.");
        var gate = Gate(request.RequestId.Value);
        await gate.WaitAsync();
        try
        {
            var current = await GetRequestAsync(request.RequestId, true);
            if (current == null)
                return RejectRequest(NCMarketMutationStatus.NotFound, "The market request does not exist.");
            if (current.AggregateVersion != request.ExpectedVersion)
                return RejectRequest(NCMarketMutationStatus.VersionConflict, "The market request version changed.");
            var matchedListing = current.Snapshot.MatchedListingId is { } matchedId
                ? await GetListingAsync(matchedId, true)
                : null;
            var providerCompleting = request.Status == NCMarketRequestStatus.Fulfilled &&
                                     matchedListing?.Snapshot.Seller == request.Initiator;
            if (request.Initiator != current.Snapshot.Requester && !providerCompleting ||
                !CanTransition(current.Snapshot.Status, request.Status) ||
                request.Status == NCMarketRequestStatus.Matched &&
                request.MatchedListingId is not { IsValid: true } ||
                request.Status == NCMarketRequestStatus.Fulfilled &&
                current.Snapshot.Status == NCMarketRequestStatus.Matched &&
                request.MatchedListingId is not { IsValid: true } ||
                request.Status == NCMarketRequestStatus.Expired && now < current.Snapshot.ExpiresAt)
                return RejectRequest(NCMarketMutationStatus.Rejected, "The market request transition is not allowed.");
            var eventId = Guid.NewGuid();
            var next = current.Snapshot with
            {
                Status = request.Status,
                MatchedListingId = request.MatchedListingId ?? current.Snapshot.MatchedListingId,
                UpdatedAt = now,
                DataVersion = checked(current.Snapshot.DataVersion + 1),
                LastAppliedEventId = eventId,
            };
            var persisted = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(
                request.CommandId, "living-city.market-request.status-changed", "market-request",
                request.RequestId.ToString(), eventId, "living-city.market-request.status-changed",
                RequestAggregateType, request.RequestId.ToString(), current.AggregateVersion, SnapshotFormatVersion,
                JsonSerializer.Serialize(next, JsonOptions),
                JsonSerializer.Serialize(new MarketEvent(request.RequestId.ToString(), request.AuditSource), JsonOptions),
                JsonSerializer.Serialize(new Receipt(request.RequestId.ToString(), current.AggregateVersion + 1),
                    JsonOptions), request.CorrelationId, request.CausationId, epoch, now, []));
            if (persisted.Status is not (NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted))
                return RejectRequest(Map(persisted.Status), persisted.Error ?? "The request status was not committed.");
            var loaded = await GetRequestAsync(request.RequestId, true);
            if (loaded == null)
                return RejectRequest(NCMarketMutationStatus.StorageFailure,
                    "The request status could not be reloaded.");
            Publish(new NCMarketAggregateChangedEvent(RequestAggregateType, request.RequestId.ToString()));
            return new(persisted.Status == NCPersistentMutationStatus.Applied
                ? NCMarketMutationStatus.Applied
                : NCMarketMutationStatus.Duplicate, loaded, null);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NCEconomicQuoteMutationResult> CreateQuoteAsync(NCQuoteMarketListingRequest request)
    {
        if (!request.ListingId.IsValid || request.ExpectedListingVersion <= 0 || request.Quantity <= 0 ||
            !Valid(request.Buyer) || !_clock.TryGetCurrentTime(out _, out var now))
            return new(NCEconomicAssetMutationStatus.Rejected, null, "The listing quote request is invalid.");
        var listing = await GetListingAsync(request.ListingId, true);
        if (listing == null)
            return new(NCEconomicAssetMutationStatus.NotFound, null, "The market listing does not exist.");
        if (listing.AggregateVersion != request.ExpectedListingVersion)
            return new(NCEconomicAssetMutationStatus.VersionConflict, null, "The listing version changed.");
        if (!CanView(listing.Snapshot, request.Buyer, now) || request.Quantity > listing.Snapshot.MaximumQuantity)
            return new(NCEconomicAssetMutationStatus.Rejected, null, "The listing is inaccessible or quantity is invalid.");

        long stockVersion = 0;
        long assetVersion = 0;
        if (listing.Snapshot.SourceStockNodeId is { } nodeId)
        {
            var node = await _assets.GetStockNodeAsync(nodeId, true);
            if (node == null)
                return new(NCEconomicAssetMutationStatus.NotFound, null, "The listing stock node is missing.");
            stockVersion = node.AggregateVersion;
            if (!Guid.TryParse(listing.Snapshot.OfferId, out var raw))
                return new(NCEconomicAssetMutationStatus.Rejected, null, "The listing asset identity is invalid.");
            assetVersion = listing.Snapshot.OfferKind == NCEconomicOfferKind.CommodityBatch
                ? (await _assets.GetCommodityBatchAsync(new NCCommodityBatchId(raw), true))?.AggregateVersion ?? 0
                : (await _assets.GetItemAsync(new NCItemId(raw), true))?.AggregateVersion ?? 0;
        }
        return await _assets.CreateQuoteAsync(new NCCreateEconomicQuoteRequest(
            request.CommandId,
            request.QuoteId,
            listing.Snapshot.OfferKind,
            listing.Snapshot.OfferId,
            request.Quantity,
            listing.Snapshot.Seller,
            request.Buyer.Viewer,
            listing.Snapshot.SourceStockNodeId,
            stockVersion,
            assetVersion,
            request.PriceContext,
            request.Lifetime,
            request.Buyer.Viewer,
            request.CorrelationId,
            request.CausationId,
            request.AuditSource));
    }

    private async Task<string?> ValidateOfferAsync(NCCreateMarketListingRequest request)
    {
        if (request.OfferKind == NCEconomicOfferKind.CommodityBatch &&
            Guid.TryParse(request.OfferId, out var batchRaw) && request.SourceStockNodeId is { } batchNodeId)
        {
            var batch = await _assets.GetCommodityBatchAsync(new NCCommodityBatchId(batchRaw), true);
            var node = await _assets.GetStockNodeAsync(batchNodeId, true);
            return batch != null && node != null && batch.Snapshot.Owner == request.Seller &&
                   node.Snapshot.Owner == request.Seller && node.Snapshot.CommodityBatchIds.Contains(batch.Snapshot.BatchId) &&
                   request.MaximumQuantity <= batch.Snapshot.Quantity - batch.Snapshot.ReservedQuantity
                ? null
                : "The listed commodity is unavailable or not owned by the seller.";
        }
        if (request.OfferKind == NCEconomicOfferKind.ItemInstance &&
            Guid.TryParse(request.OfferId, out var itemRaw) && request.SourceStockNodeId is { } itemNodeId &&
            request.MaximumQuantity == 1)
        {
            var item = await _assets.GetItemAsync(new NCItemId(itemRaw), true);
            var node = await _assets.GetStockNodeAsync(itemNodeId, true);
            return item != null && node != null && item.Snapshot.Owner == request.Seller &&
                   node.Snapshot.Owner == request.Seller && node.Snapshot.ItemIds.Contains(item.Snapshot.ItemId) &&
                   !item.Snapshot.ReservationId.HasValue
                ? null
                : "The listed item is unavailable or not owned by the seller.";
        }
        if (request.OfferKind == NCEconomicOfferKind.Service && !request.SourceStockNodeId.HasValue &&
            _prototypes.HasIndex(new ProtoId<NCEconomicServicePrototype>(request.OfferId)))
            return null;
        if (request.OfferKind == NCEconomicOfferKind.InformationAccess && !request.SourceStockNodeId.HasValue &&
            request.MaximumQuantity == 1 &&
            _prototypes.HasIndex(new ProtoId<NCEconomicInformationAccessPrototype>(request.OfferId)))
            return null;
        return "The listing offer shape or prototype is invalid.";
    }

    private async Task<bool> IsOfferAvailableAsync(NCMarketListingSnapshot listing)
    {
        if (listing.SourceStockNodeId is not { } nodeId)
            return true;
        var node = await _assets.GetStockNodeAsync(nodeId, true);
        if (node == null || node.Snapshot.Owner != listing.Seller)
            return false;
        if (!Guid.TryParse(listing.OfferId, out var raw))
            return false;
        if (listing.OfferKind == NCEconomicOfferKind.CommodityBatch)
        {
            var batch = await _assets.GetCommodityBatchAsync(new NCCommodityBatchId(raw), true);
            return batch != null && node.Snapshot.CommodityBatchIds.Contains(batch.Snapshot.BatchId) &&
                   batch.Snapshot.Quantity > batch.Snapshot.ReservedQuantity;
        }
        var item = await _assets.GetItemAsync(new NCItemId(raw), true);
        return item != null && node.Snapshot.ItemIds.Contains(item.Snapshot.ItemId) &&
               !item.Snapshot.ReservationId.HasValue;
    }

    private async Task<NCMarketPartyIndexSnapshot> GetIndexAsync(NCPartyRef owner)
    {
        var stored = await _database.GetNCAggregateSnapshotAsync(PartyIndexAggregateType, owner.ToString());
        if (stored != null)
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<NCMarketPartyIndexSnapshot>(stored.Payload, JsonOptions);
                if (snapshot != null && snapshot.Owner == owner)
                    return snapshot;
            }
            catch (JsonException)
            {
                // The caller fails closed through a version conflict if a malformed index already exists.
            }
        }
        var now = _clock.TryGetCurrentTime(out _, out var current) ? current : NCActiveWorldTime.Zero;
        return new NCMarketPartyIndexSnapshot(owner, [], [], now, now, 0, Guid.Empty);
    }

    private async Task<NCMarketPartyIndexSnapshot> CompactIndexAsync(
        NCMarketPartyIndexSnapshot index,
        NCActiveWorldTime now)
    {
        var listings = new List<NCMarketListingId>(index.ListingIds.Count);
        foreach (var id in index.ListingIds)
        {
            var listing = await GetListingAsync(id, true);
            if (listing?.Snapshot.Status is NCMarketListingStatus.Open or NCMarketListingStatus.Reserved &&
                (listing.Snapshot.ExpiresAt is not { } expiry || now < expiry))
                listings.Add(id);
        }
        var requests = new List<NCMarketRequestId>(index.RequestIds.Count);
        foreach (var id in index.RequestIds)
        {
            var request = await GetRequestAsync(id, true);
            if (request?.Snapshot.Status is NCMarketRequestStatus.Open or NCMarketRequestStatus.Matched &&
                now < request.Snapshot.ExpiresAt)
                requests.Add(id);
        }
        return index with
        {
            ListingIds = listings,
            RequestIds = requests,
        };
    }

    private static bool CanTransition(NCMarketListingStatus from, NCMarketListingStatus to) => from switch
    {
        NCMarketListingStatus.Draft => to is NCMarketListingStatus.Open or NCMarketListingStatus.Cancelled,
        NCMarketListingStatus.Open => to is NCMarketListingStatus.Reserved or NCMarketListingStatus.Fulfilled or
            NCMarketListingStatus.Cancelled or NCMarketListingStatus.Expired,
        NCMarketListingStatus.Reserved => to is NCMarketListingStatus.Open or NCMarketListingStatus.Fulfilled or
            NCMarketListingStatus.Cancelled or NCMarketListingStatus.Expired,
        _ => false,
    };

    private static bool CanTransition(NCMarketRequestStatus from, NCMarketRequestStatus to) => from switch
    {
        NCMarketRequestStatus.Open => to is NCMarketRequestStatus.Matched or NCMarketRequestStatus.Fulfilled or
            NCMarketRequestStatus.Cancelled or NCMarketRequestStatus.Expired,
        NCMarketRequestStatus.Matched => to is NCMarketRequestStatus.Open or NCMarketRequestStatus.Fulfilled or
            NCMarketRequestStatus.Cancelled or NCMarketRequestStatus.Expired,
        _ => false,
    };

    private static bool CanView(
        NCMarketListingSnapshot listing,
        NCMarketViewerContext viewer,
        NCActiveWorldTime now)
    {
        if (listing.Status != NCMarketListingStatus.Open || listing.ExpiresAt is { } expiry && now >= expiry)
            return false;
        if (viewer.Viewer == listing.Seller || listing.Access.AllowedParties.Contains(viewer.Viewer))
            return true;
        return listing.Access.Visibility switch
        {
            NCMarketVisibility.Public => true,
            NCMarketVisibility.Relationship => viewer.Relationship >= listing.Access.MinimumRelationship,
            NCMarketVisibility.Faction => viewer.AudienceKeys.Contains(listing.Access.AudienceKey),
            NCMarketVisibility.Private => false,
            _ => false,
        } && viewer.Reputation >= listing.Access.MinimumReputation;
    }

    private bool TryPolicy(out NCMarketPolicyPrototype policy) =>
        _prototypes.TryIndex(DefaultPolicy, out policy!) && policy.MaximumListingsPerParty is > 0 and <= 4096 &&
        policy.MaximumRequestsPerParty is > 0 and <= 4096 && policy.MaximumDiscoveryResults is > 0 and <= 500;

    private static bool Valid(NCCreateMarketListingRequest value, NCMarketPolicyPrototype policy) =>
        value.CommandId != Guid.Empty && value.ListingId.IsValid && Enum.IsDefined(value.OfferKind) &&
        value.OfferId == value.OfferId.Trim() && value.OfferId.Length is > 0 and <= 256 &&
        value.MaximumQuantity > 0 && value.Seller.IsValid && Enum.IsDefined(value.Channel) && Valid(value.Access) &&
        (value.Lifetime == null || value.Lifetime > TimeSpan.Zero && value.Lifetime <= policy.MaximumListingLifetime) &&
        value.Initiator.IsValid && value.CorrelationId != Guid.Empty && ValidAudit(value.AuditSource);

    private static bool Valid(NCCreateMarketRequestRequest value, NCMarketPolicyPrototype policy) =>
        value.CommandId != Guid.Empty && value.RequestId.IsValid && Enum.IsDefined(value.Kind) &&
        value.DefinitionId == value.DefinitionId.Trim() && value.DefinitionId.Length is > 0 and <= 256 &&
        value.Quantity > 0 && value.MaximumUnitPrice >= 0 && value.MinimumQuality is >= 0 and <= 1000 &&
        value.Requester.IsValid && Enum.IsDefined(value.Channel) && Valid(value.Access) &&
        value.Lifetime > TimeSpan.Zero && value.Lifetime <= policy.MaximumRequestLifetime &&
        value.Initiator == value.Requester && value.CorrelationId != Guid.Empty && ValidAudit(value.AuditSource);

    private static bool Valid(NCMarketViewerContext value) => value.Viewer.IsValid &&
        value.AudienceKeys.Count <= 64 && value.AudienceKeys.All(key => Stable(key, 128)) &&
        value.Relationship is >= -1000 and <= 1000 && value.Reputation is >= -1000 and <= 1000;

    private static bool Valid(NCMarketAccessPolicy value) => Enum.IsDefined(value.Visibility) &&
        value.AllowedParties.Count <= 64 && value.AllowedParties.All(party => party.IsValid) &&
        value.AllowedParties.Distinct().Count() == value.AllowedParties.Count &&
        (string.IsNullOrEmpty(value.AudienceKey) || Stable(value.AudienceKey, 128)) &&
        value.MinimumRelationship is >= -1000 and <= 1000 && value.MinimumReputation is >= -1000 and <= 1000;

    private static NCMarketAccessPolicy Normalize(NCMarketAccessPolicy value) => value with
    {
        AudienceKey = value.AudienceKey.Trim().ToLowerInvariant(),
        AllowedParties = value.AllowedParties.OrderBy(party => party.ToString()).ToArray(),
    };

    private static bool Same(NCMarketListingSnapshot value, NCCreateMarketListingRequest request) =>
        value.ListingId == request.ListingId && value.OfferKind == request.OfferKind &&
        value.OfferId == CanonicalOffer(request.OfferKind, request.OfferId) &&
        value.MaximumQuantity == request.MaximumQuantity && value.Seller == request.Seller &&
        value.SourceStockNodeId == request.SourceStockNodeId && value.Channel == request.Channel &&
        Same(value.Access, Normalize(request.Access));

    private static bool Same(NCMarketRequestSnapshot value, NCCreateMarketRequestRequest request) =>
        value.RequestId == request.RequestId && value.Kind == request.Kind &&
        value.DefinitionId == request.DefinitionId && value.Quantity == request.Quantity &&
        value.MaximumUnitPrice == request.MaximumUnitPrice && value.MinimumQuality == request.MinimumQuality &&
        value.Requester == request.Requester && value.Channel == request.Channel &&
        Same(value.Access, Normalize(request.Access)) && value.PreferredZone == request.PreferredZone;

    private static bool Same(NCMarketAccessPolicy left, NCMarketAccessPolicy right) =>
        left.Visibility == right.Visibility && left.AudienceKey == right.AudienceKey &&
        left.MinimumRelationship == right.MinimumRelationship && left.MinimumReputation == right.MinimumReputation &&
        left.AllowedParties.SequenceEqual(right.AllowedParties);

    private static string CanonicalOffer(NCEconomicOfferKind kind, string value) =>
        kind is NCEconomicOfferKind.CommodityBatch or NCEconomicOfferKind.ItemInstance &&
        Guid.TryParse(value, out var id) ? id.ToString("D") : value.Trim();

    private NCPersistentAggregateWrite Write(
        NCMarketListingRecord? current,
        NCMarketListingSnapshot next,
        Guid eventId,
        string eventType,
        string audit) => new(eventId, eventType, ListingAggregateType, next.ListingId.ToString(),
        current?.AggregateVersion ?? 0, SnapshotFormatVersion, JsonSerializer.Serialize(next, JsonOptions),
        JsonSerializer.Serialize(new MarketEvent(next.ListingId.ToString(), audit), JsonOptions));

    private NCPersistentAggregateWrite Write(
        NCMarketRequestRecord? current,
        NCMarketRequestSnapshot next,
        Guid eventId,
        string eventType,
        string audit) => new(eventId, eventType, RequestAggregateType, next.RequestId.ToString(),
        current?.AggregateVersion ?? 0, SnapshotFormatVersion, JsonSerializer.Serialize(next, JsonOptions),
        JsonSerializer.Serialize(new MarketEvent(next.RequestId.ToString(), audit), JsonOptions));

    private NCPersistentAggregateWrite Write(
        NCMarketPartyIndexSnapshot current,
        NCMarketPartyIndexSnapshot next,
        Guid eventId,
        string eventType,
        string audit) => new(eventId, eventType, PartyIndexAggregateType, next.Owner.ToString(),
        current.DataVersion, SnapshotFormatVersion, JsonSerializer.Serialize(next, JsonOptions),
        JsonSerializer.Serialize(new MarketEvent(next.Owner.ToString(), audit), JsonOptions));

    private static NCMarketListingRecord? DeserializeListing(NCAggregateSnapshotData? stored, NCMarketListingId id)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var value = JsonSerializer.Deserialize<NCMarketListingSnapshot>(stored.Payload, JsonOptions);
            return value?.ListingId == id
                ? new(value, stored.AggregateVersion, stored.SessionEpoch, stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static NCMarketRequestRecord? DeserializeRequest(NCAggregateSnapshotData? stored, NCMarketRequestId id)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var value = JsonSerializer.Deserialize<NCMarketRequestSnapshot>(stored.Payload, JsonOptions);
            return value?.RequestId == id
                ? new(value, stored.AggregateVersion, stored.SessionEpoch, stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Publish(NCMarketAggregateChangedEvent args) =>
        _tasks.RunOnMainThread(() => RaiseLocalEvent(args));

    private SemaphoreSlim Gate(Guid id) => _gates[(id.GetHashCode() & int.MaxValue) % _gates.Length];
    private static bool Stable(string value, int max) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= max && value == value.Trim().ToLowerInvariant();
    private static bool ValidAudit(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
    private static NCMarketMutationStatus Map(NCPersistentMutationStatus status) => status switch
    {
        NCPersistentMutationStatus.VersionConflict => NCMarketMutationStatus.VersionConflict,
        NCPersistentMutationStatus.ClockRejected => NCMarketMutationStatus.ClockUnavailable,
        _ => NCMarketMutationStatus.StorageFailure,
    };
    private static NCMarketListingMutationResult RejectListing(NCMarketMutationStatus status, string error) =>
        new(status, null, error);
    private static NCMarketRequestMutationResult RejectRequest(NCMarketMutationStatus status, string error) =>
        new(status, null, error);

    private sealed record NCMarketPartyIndexSnapshot(
        NCPartyRef Owner,
        IReadOnlyList<NCMarketListingId> ListingIds,
        IReadOnlyList<NCMarketRequestId> RequestIds,
        NCActiveWorldTime CreatedAt,
        NCActiveWorldTime UpdatedAt,
        int DataVersion,
        Guid LastAppliedEventId);
    private sealed record MarketEvent(string SubjectId, string AuditSource);
    private sealed record Receipt(string AggregateId, long AggregateVersion);
}
