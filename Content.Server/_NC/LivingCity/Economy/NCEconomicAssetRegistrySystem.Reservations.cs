// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Economy;

/// <summary>
/// Persistent reservations lock authoritative stock state using the same optimistic database transaction as their
/// quote transition. Expiry uses active-world time, so reservations do not age while no round is running.
/// </summary>
public sealed partial class NCEconomicAssetRegistrySystem
{
    public const string EconomicReservationAggregateType = "living-city.economic-reservation";

    private const string CreateReservationCommand = "living-city.economic-reservation.create";
    private const string ReleaseReservationCommand = "living-city.economic-reservation.release";

    private readonly ConcurrentDictionary<NCEconomicReservationId, NCEconomicReservationRecord> _reservations = [];

    public int CachedReservationCount => _reservations.Count;

    public bool Unload(NCEconomicReservationId id) => _reservations.TryRemove(id, out _);

    public async Task<NCEconomicReservationRecord?> GetReservationAsync(
        NCEconomicReservationId id,
        bool forceReload = false)
    {
        if (!id.IsValid)
            return null;
        if (!forceReload && _reservations.TryGetValue(id, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(EconomicReservationAggregateType, Key(id));
        var record = DeserializeReservation(stored, id);
        return record == null
            ? null
            : _reservations.AddOrUpdate(id, record,
                (_, current) => current.AggregateVersion > record.AggregateVersion ? current : record);
    }

    public async Task<NCEconomicReservationMutationResult> CreateReservationAsync(
        NCCreateEconomicReservationRequest request)
    {
        if (!TryGetPricingPolicy(out var policy) || !Valid(request, policy) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return RejectReservation(_clock.IsReady
                ? NCEconomicAssetMutationStatus.Rejected
                : NCEconomicAssetMutationStatus.ClockUnavailable,
                "The reservation request, policy, or active-world clock is invalid.");
        }

        try
        {
            var command = await InspectCommand(request.CommandId, CreateReservationCommand, "economic-reservation",
                Key(request.ReservationId));
            if (command == InitialCommandState.Collision)
                return RejectReservation(NCEconomicAssetMutationStatus.Rejected,
                    "The command ID belongs to another operation.");
            if (command == InitialCommandState.InProgress)
                return RejectReservation(NCEconomicAssetMutationStatus.StorageFailure,
                    "The reservation command is still in progress.");

            var initialQuote = await GetQuoteAsync(request.QuoteId, true);
            if (initialQuote == null)
                return RejectReservation(NCEconomicAssetMutationStatus.NotFound, "The quote does not exist.");
            if (!await ResolveParties(initialQuote.Snapshot.Seller, initialQuote.Snapshot.Buyer, request.Initiator))
                return RejectReservation(NCEconomicAssetMutationStatus.PartyUnavailable,
                    "The seller, buyer, or initiator is unavailable.");
            if (request.Initiator != initialQuote.Snapshot.Seller && request.Initiator != initialQuote.Snapshot.Buyer)
                return RejectReservation(NCEconomicAssetMutationStatus.Rejected,
                    "Only a quote participant can reserve the offer.");

            var gateKeys = ReservationGateKeys(request.ReservationId, initialQuote.Snapshot);
            var gates = await AcquireGates(gateKeys);
            try
            {
                var existing = await GetReservationAsync(request.ReservationId, true);
                if (existing != null)
                {
                    return Same(existing.Snapshot, request)
                        ? await LoadReservationResult(existing, NCEconomicAssetMutationStatus.Duplicate)
                        : RejectReservation(NCEconomicAssetMutationStatus.Rejected,
                            "The reservation identity is bound to different immutable data.");
                }
                if (command == InitialCommandState.Completed)
                    return RejectReservation(NCEconomicAssetMutationStatus.StorageFailure,
                        "The completed command is missing its reservation aggregate.");

                var quote = await GetQuoteAsync(request.QuoteId, true);
                if (quote == null)
                    return RejectReservation(NCEconomicAssetMutationStatus.NotFound, "The quote does not exist.");
                if (quote.AggregateVersion != request.ExpectedQuoteVersion)
                    return RejectReservation(NCEconomicAssetMutationStatus.VersionConflict,
                        "The quote version changed.");
                if (quote.Snapshot.Status != NCEconomicQuoteStatus.Open || now >= quote.Snapshot.ExpiresAt)
                    return RejectReservation(NCEconomicAssetMutationStatus.Rejected,
                        "The quote is no longer open in active-world time.");
                if (quote.Snapshot.SourceStockNodeId is { } leasedNode &&
                    _materializationLeases.IsLeased(leasedNode))
                {
                    return RejectReservation(NCEconomicAssetMutationStatus.Rejected,
                        "A materialized stock node requires a physical reservation interaction.");
                }

                var expiry = now.Advance(request.Lifetime);
                if (expiry > quote.Snapshot.ExpiresAt)
                    expiry = quote.Snapshot.ExpiresAt;

                NCStockNodeRecord? node = null;
                NCCommodityBatchRecord? batch = null;
                NCItemInstanceRecord? item = null;
                NCCommodityBatchSnapshot? nextBatch = null;
                NCItemInstanceSnapshot? nextItem = null;
                NCStockNodeSnapshot? nextNode = null;
                var assetEventId = Guid.Empty;
                var nodeEventId = Guid.Empty;

                if (quote.Snapshot.OfferKind == NCEconomicOfferKind.CommodityBatch)
                {
                    var batchId = new NCCommodityBatchId(Guid.Parse(quote.Snapshot.OfferId));
                    batch = await GetCommodityBatchAsync(batchId, true);
                    node = await GetStockNodeAsync(quote.Snapshot.SourceStockNodeId!.Value, true);
                    var error = ValidateReservationState(quote, batch, node, now);
                    if (error != null)
                        return error;
                    assetEventId = Guid.NewGuid();
                    nextBatch = batch!.Snapshot with
                    {
                        ReservedQuantity = checked(batch.Snapshot.ReservedQuantity + quote.Snapshot.Quantity),
                        UpdatedAt = now,
                        DataVersion = checked(batch.Snapshot.DataVersion + 1),
                        LastAppliedEventId = assetEventId,
                    };
                    nodeEventId = Guid.NewGuid();
                    nextNode = Touch(node!.Snapshot, nodeEventId, now);
                }
                else if (quote.Snapshot.OfferKind == NCEconomicOfferKind.ItemInstance)
                {
                    var itemId = new NCItemId(Guid.Parse(quote.Snapshot.OfferId));
                    item = await GetItemAsync(itemId, true);
                    node = await GetStockNodeAsync(quote.Snapshot.SourceStockNodeId!.Value, true);
                    var error = ValidateReservationState(quote, item, node);
                    if (error != null)
                        return error;
                    assetEventId = Guid.NewGuid();
                    nextItem = item!.Snapshot with
                    {
                        ReservationId = request.ReservationId,
                        UpdatedAt = now,
                        DataVersion = checked(item.Snapshot.DataVersion + 1),
                        LastAppliedEventId = assetEventId,
                    };
                    nodeEventId = Guid.NewGuid();
                    nextNode = Touch(node!.Snapshot, nodeEventId, now);
                }

                var quoteEventId = Guid.NewGuid();
                var nextQuote = quote.Snapshot with
                {
                    Status = NCEconomicQuoteStatus.Reserved,
                    DataVersion = checked(quote.Snapshot.DataVersion + 1),
                    LastAppliedEventId = quoteEventId,
                };
                var reservationEventId = Guid.NewGuid();
                var reservation = new NCEconomicReservationSnapshot(
                    request.ReservationId,
                    quote.Snapshot.QuoteId,
                    quote.Snapshot.OfferKind,
                    quote.Snapshot.OfferId,
                    quote.Snapshot.Quantity,
                    quote.Snapshot.Seller,
                    quote.Snapshot.Buyer,
                    quote.Snapshot.SourceStockNodeId,
                    node?.AggregateVersion + 1 ?? 0,
                    batch?.AggregateVersion + 1 ?? item?.AggregateVersion + 1 ?? 0,
                    request.Lifetime,
                    request.Initiator,
                    now,
                    expiry,
                    NCEconomicReservationStatus.Active,
                    null,
                    null,
                    1,
                    reservationEventId);
                var writes = new List<NCPersistentAggregateWrite>
                {
                    QuoteWrite(quote, nextQuote, quoteEventId, "living-city.economic-quote.reserved",
                        request.AuditSource),
                    ReservationWrite(null, reservation, reservationEventId,
                        "living-city.economic-reservation.created", request.AuditSource),
                };
                if (nextBatch != null)
                {
                    writes.Add(CommodityMutationWrite(batch, nextBatch, assetEventId,
                        "living-city.commodity-batch.reserved", request.AuditSource));
                }
                if (nextItem != null)
                {
                    writes.Add(ItemMutationWrite(item!, nextItem, assetEventId,
                        "living-city.item-instance.reserved", request.AuditSource));
                }
                if (nextNode != null)
                {
                    writes.Add(StockNodeWrite(node, nextNode, nodeEventId,
                        "living-city.stock-node.reservation-created", request.AuditSource));
                }

                var persisted = await Persist(
                    command == InitialCommandState.Available ? request.CommandId : Guid.NewGuid(),
                    CreateReservationCommand,
                    "economic-reservation",
                    Key(request.ReservationId),
                    writes,
                    new AssetReceipt(Key(request.ReservationId), 1),
                    request.CorrelationId,
                    request.CausationId,
                    epoch,
                    now);
                if (persisted.Status is not (NCPersistentMutationStatus.Applied or
                    NCPersistentMutationStatus.DuplicateCompleted))
                    return FromPersistenceReservation(persisted);

                var loaded = await GetReservationAsync(request.ReservationId, true);
                return loaded == null
                    ? RejectReservation(NCEconomicAssetMutationStatus.StorageFailure,
                        "The committed reservation could not be reloaded.")
                    : await LoadReservationResult(loaded,
                        persisted.Status == NCPersistentMutationStatus.Applied
                            ? NCEconomicAssetMutationStatus.Applied
                            : NCEconomicAssetMutationStatus.Duplicate);
            }
            finally
            {
                ReleaseGates(gates);
            }
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to create economic reservation {request.ReservationId}: {exception}");
            return RejectReservation(NCEconomicAssetMutationStatus.StorageFailure, exception.Message);
        }
    }

    public async Task<NCEconomicReservationMutationResult> ReleaseReservationAsync(
        NCReleaseEconomicReservationRequest request)
    {
        if (!Valid(request) || !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return RejectReservation(_clock.IsReady
                ? NCEconomicAssetMutationStatus.Rejected
                : NCEconomicAssetMutationStatus.ClockUnavailable,
                "The release request or active-world clock is invalid.");
        }

        try
        {
            var command = await InspectCommand(request.CommandId, ReleaseReservationCommand,
                "economic-reservation", Key(request.ReservationId));
            if (command == InitialCommandState.Collision)
                return RejectReservation(NCEconomicAssetMutationStatus.Rejected,
                    "The command ID belongs to another operation.");
            if (command == InitialCommandState.InProgress)
                return RejectReservation(NCEconomicAssetMutationStatus.StorageFailure,
                    "The release command is still in progress.");

            var initial = await GetReservationAsync(request.ReservationId, true);
            if (initial == null)
                return RejectReservation(NCEconomicAssetMutationStatus.NotFound, "The reservation does not exist.");
            if (!await ResolveParties(initial.Snapshot.Seller, initial.Snapshot.Buyer, request.Initiator))
                return RejectReservation(NCEconomicAssetMutationStatus.PartyUnavailable,
                    "The reservation parties or initiator are unavailable.");
            if (request.FinalStatus == NCEconomicReservationStatus.Released &&
                request.Initiator != initial.Snapshot.Seller && request.Initiator != initial.Snapshot.Buyer)
            {
                return RejectReservation(NCEconomicAssetMutationStatus.Rejected,
                    "Only a reservation participant can release it before expiry.");
            }

            var quote = await GetQuoteAsync(initial.Snapshot.QuoteId, true);
            if (quote == null)
                return RejectReservation(NCEconomicAssetMutationStatus.StorageFailure,
                    "The reservation quote is missing.");
            var gates = await AcquireGates(ReservationGateKeys(request.ReservationId, quote.Snapshot));
            try
            {
                var reservation = await GetReservationAsync(request.ReservationId, true);
                quote = await GetQuoteAsync(initial.Snapshot.QuoteId, true);
                if (reservation == null || quote == null)
                    return RejectReservation(NCEconomicAssetMutationStatus.StorageFailure,
                        "The reservation or quote disappeared while acquiring the mutation boundary.");
                if (reservation.Snapshot.Status != NCEconomicReservationStatus.Active ||
                    quote.Snapshot.Status != NCEconomicQuoteStatus.Reserved)
                {
                    return command == InitialCommandState.Completed &&
                           reservation.Snapshot.Status == request.FinalStatus
                        ? await LoadReservationResult(reservation, NCEconomicAssetMutationStatus.Duplicate)
                        : RejectReservation(NCEconomicAssetMutationStatus.Rejected,
                            "The reservation is no longer active.");
                }
                if (reservation.AggregateVersion != request.ExpectedReservationVersion)
                    return RejectReservation(NCEconomicAssetMutationStatus.VersionConflict,
                        "The reservation version changed.");
                if (request.FinalStatus == NCEconomicReservationStatus.Expired && now < reservation.Snapshot.ExpiresAt)
                    return RejectReservation(NCEconomicAssetMutationStatus.Rejected,
                        "The reservation has not expired in active-world time.");

                NCStockNodeRecord? node = null;
                NCCommodityBatchRecord? batch = null;
                NCItemInstanceRecord? item = null;
                NCCommodityBatchSnapshot? nextBatch = null;
                NCItemInstanceSnapshot? nextItem = null;
                NCStockNodeSnapshot? nextNode = null;
                var assetEventId = Guid.Empty;
                var nodeEventId = Guid.Empty;
                if (reservation.Snapshot.OfferKind == NCEconomicOfferKind.CommodityBatch)
                {
                    batch = await GetCommodityBatchAsync(
                        new NCCommodityBatchId(Guid.Parse(reservation.Snapshot.OfferId)), true);
                    node = await GetStockNodeAsync(reservation.Snapshot.SourceStockNodeId!.Value, true);
                    if (batch == null || node == null ||
                        batch.AggregateVersion != reservation.Snapshot.ReservedAssetVersion ||
                        node.AggregateVersion != reservation.Snapshot.ReservedStockNodeVersion ||
                        batch.Snapshot.ReservedQuantity < reservation.Snapshot.Quantity)
                    {
                        return RejectReservation(NCEconomicAssetMutationStatus.VersionConflict,
                            "The reserved commodity or stock state changed.");
                    }
                    assetEventId = Guid.NewGuid();
                    nextBatch = batch.Snapshot with
                    {
                        ReservedQuantity = batch.Snapshot.ReservedQuantity - reservation.Snapshot.Quantity,
                        UpdatedAt = now,
                        DataVersion = checked(batch.Snapshot.DataVersion + 1),
                        LastAppliedEventId = assetEventId,
                    };
                    nodeEventId = Guid.NewGuid();
                    nextNode = Touch(node.Snapshot, nodeEventId, now);
                }
                else if (reservation.Snapshot.OfferKind == NCEconomicOfferKind.ItemInstance)
                {
                    item = await GetItemAsync(new NCItemId(Guid.Parse(reservation.Snapshot.OfferId)), true);
                    node = await GetStockNodeAsync(reservation.Snapshot.SourceStockNodeId!.Value, true);
                    if (item == null || node == null ||
                        item.AggregateVersion != reservation.Snapshot.ReservedAssetVersion ||
                        node.AggregateVersion != reservation.Snapshot.ReservedStockNodeVersion ||
                        item.Snapshot.ReservationId != reservation.Snapshot.ReservationId)
                    {
                        return RejectReservation(NCEconomicAssetMutationStatus.VersionConflict,
                            "The reserved item or stock state changed.");
                    }
                    assetEventId = Guid.NewGuid();
                    nextItem = item.Snapshot with
                    {
                        ReservationId = null,
                        UpdatedAt = now,
                        DataVersion = checked(item.Snapshot.DataVersion + 1),
                        LastAppliedEventId = assetEventId,
                    };
                    nodeEventId = Guid.NewGuid();
                    nextNode = Touch(node.Snapshot, nodeEventId, now);
                }

                var reservationEventId = Guid.NewGuid();
                var nextReservation = reservation.Snapshot with
                {
                    Status = request.FinalStatus,
                    DataVersion = checked(reservation.Snapshot.DataVersion + 1),
                    LastAppliedEventId = reservationEventId,
                };
                var quoteEventId = Guid.NewGuid();
                var nextQuote = quote.Snapshot with
                {
                    Status = request.FinalStatus == NCEconomicReservationStatus.Expired
                        ? NCEconomicQuoteStatus.Expired
                        : NCEconomicQuoteStatus.Cancelled,
                    DataVersion = checked(quote.Snapshot.DataVersion + 1),
                    LastAppliedEventId = quoteEventId,
                };
                var writes = new List<NCPersistentAggregateWrite>
                {
                    ReservationWrite(reservation, nextReservation, reservationEventId,
                        "living-city.economic-reservation.released", request.AuditSource),
                    QuoteWrite(quote, nextQuote, quoteEventId, "living-city.economic-quote.closed",
                        request.AuditSource),
                };
                if (nextBatch != null)
                {
                    writes.Add(CommodityMutationWrite(batch, nextBatch, assetEventId,
                        "living-city.commodity-batch.reservation-released", request.AuditSource));
                }
                if (nextItem != null)
                {
                    writes.Add(ItemMutationWrite(item!, nextItem, assetEventId,
                        "living-city.item-instance.reservation-released", request.AuditSource));
                }
                if (nextNode != null)
                {
                    writes.Add(StockNodeWrite(node, nextNode, nodeEventId,
                        "living-city.stock-node.reservation-released", request.AuditSource));
                }

                var persisted = await Persist(
                    command == InitialCommandState.Available ? request.CommandId : Guid.NewGuid(),
                    ReleaseReservationCommand,
                    "economic-reservation",
                    Key(request.ReservationId),
                    writes,
                    new AssetReceipt(Key(request.ReservationId), reservation.AggregateVersion + 1),
                    request.CorrelationId,
                    request.CausationId,
                    epoch,
                    now);
                if (persisted.Status is not (NCPersistentMutationStatus.Applied or
                    NCPersistentMutationStatus.DuplicateCompleted))
                    return FromPersistenceReservation(persisted);
                var loaded = await GetReservationAsync(request.ReservationId, true);
                return loaded == null
                    ? RejectReservation(NCEconomicAssetMutationStatus.StorageFailure,
                        "The released reservation could not be reloaded.")
                    : await LoadReservationResult(loaded,
                        persisted.Status == NCPersistentMutationStatus.Applied
                            ? NCEconomicAssetMutationStatus.Applied
                            : NCEconomicAssetMutationStatus.Duplicate);
            }
            finally
            {
                ReleaseGates(gates);
            }
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to release economic reservation {request.ReservationId}: {exception}");
            return RejectReservation(NCEconomicAssetMutationStatus.StorageFailure, exception.Message);
        }
    }

    private NCEconomicReservationMutationResult? ValidateReservationState(
        NCEconomicQuoteRecord quote,
        NCCommodityBatchRecord? batch,
        NCStockNodeRecord? node,
        NCActiveWorldTime now)
    {
        if (batch == null || node == null)
            return RejectReservation(NCEconomicAssetMutationStatus.NotFound,
                "The quoted commodity or stock node does not exist.");
        if (batch.AggregateVersion != quote.Snapshot.QuotedAssetVersion ||
            node.AggregateVersion != quote.Snapshot.QuotedStockNodeVersion)
        {
            return RejectReservation(NCEconomicAssetMutationStatus.VersionConflict,
                "The quoted commodity or stock-node version changed.");
        }
        if (batch.Snapshot.Owner != quote.Snapshot.Seller || node.Snapshot.Owner != quote.Snapshot.Seller ||
            batch.Snapshot.Location != StockLocation(node.Snapshot.StockNodeId) ||
            !node.Snapshot.CommodityBatchIds.Contains(batch.Snapshot.BatchId) ||
            batch.Snapshot.ExpiresAt is { } expiresAt && now >= expiresAt ||
            quote.Snapshot.Quantity > batch.Snapshot.Quantity - batch.Snapshot.ReservedQuantity)
        {
            return RejectReservation(NCEconomicAssetMutationStatus.Rejected,
                "The quoted commodity is no longer available from this seller.");
        }
        return null;
    }

    private NCEconomicReservationMutationResult? ValidateReservationState(
        NCEconomicQuoteRecord quote,
        NCItemInstanceRecord? item,
        NCStockNodeRecord? node)
    {
        if (item == null || node == null)
            return RejectReservation(NCEconomicAssetMutationStatus.NotFound,
                "The quoted item or stock node does not exist.");
        if (item.AggregateVersion != quote.Snapshot.QuotedAssetVersion ||
            node.AggregateVersion != quote.Snapshot.QuotedStockNodeVersion)
        {
            return RejectReservation(NCEconomicAssetMutationStatus.VersionConflict,
                "The quoted item or stock-node version changed.");
        }
        if (item.Snapshot.Owner != quote.Snapshot.Seller || node.Snapshot.Owner != quote.Snapshot.Seller ||
            item.Snapshot.Location != StockLocation(node.Snapshot.StockNodeId) ||
            !node.Snapshot.ItemIds.Contains(item.Snapshot.ItemId) || item.Snapshot.ReservationId.HasValue)
        {
            return RejectReservation(NCEconomicAssetMutationStatus.Rejected,
                "The quoted item is no longer available from this seller.");
        }
        return null;
    }

    private async Task<NCEconomicReservationMutationResult> LoadReservationResult(
        NCEconomicReservationRecord reservation,
        NCEconomicAssetMutationStatus status)
    {
        var quote = await GetQuoteAsync(reservation.Snapshot.QuoteId, true);
        NCStockNodeRecord? node = null;
        NCCommodityBatchRecord? batch = null;
        NCItemInstanceRecord? item = null;
        if (reservation.Snapshot.SourceStockNodeId is { } nodeId)
            node = await GetStockNodeAsync(nodeId, true);
        if (reservation.Snapshot.OfferKind == NCEconomicOfferKind.CommodityBatch)
            batch = await GetCommodityBatchAsync(new NCCommodityBatchId(Guid.Parse(reservation.Snapshot.OfferId)), true);
        if (reservation.Snapshot.OfferKind == NCEconomicOfferKind.ItemInstance)
            item = await GetItemAsync(new NCItemId(Guid.Parse(reservation.Snapshot.OfferId)), true);
        return quote == null || reservation.Snapshot.SourceStockNodeId.HasValue && node == null ||
               reservation.Snapshot.OfferKind == NCEconomicOfferKind.CommodityBatch && batch == null ||
               reservation.Snapshot.OfferKind == NCEconomicOfferKind.ItemInstance && item == null
            ? RejectReservation(NCEconomicAssetMutationStatus.StorageFailure,
                "The reservation state could not be reloaded.")
            : new NCEconomicReservationMutationResult(status, reservation, quote, node, batch, item, null);
    }

    private NCPersistentAggregateWrite ReservationWrite(
        NCEconomicReservationRecord? current,
        NCEconomicReservationSnapshot next,
        Guid eventId,
        string eventType,
        string auditSource) => new(
        eventId,
        eventType,
        EconomicReservationAggregateType,
        Key(next.ReservationId),
        current?.AggregateVersion ?? 0,
        SnapshotFormatVersion,
        JsonSerializer.Serialize(next, JsonOptions),
        JsonSerializer.Serialize(new AssetEvent(Key(next.ReservationId), auditSource.Trim()), JsonOptions));

    private static NCStockNodeSnapshot Touch(NCStockNodeSnapshot value, Guid eventId, NCActiveWorldTime now) =>
        value with
        {
            UpdatedAt = now,
            DataVersion = checked(value.DataVersion + 1),
            LastAppliedEventId = eventId,
        };

    private static Guid[] ReservationGateKeys(
        NCEconomicReservationId reservationId,
        NCEconomicQuoteSnapshot quote)
    {
        var keys = new List<Guid> { reservationId.Value, quote.QuoteId.Value };
        if (quote.SourceStockNodeId is { } nodeId)
            keys.Add(nodeId.Value);
        if (Guid.TryParse(quote.OfferId, out var assetId))
            keys.Add(assetId);
        return keys.ToArray();
    }

    private static bool Valid(
        NCCreateEconomicReservationRequest value,
        NCEconomicPricingPolicyPrototype policy) => value.CommandId != Guid.Empty && value.ReservationId.IsValid &&
        value.QuoteId.IsValid && value.ExpectedQuoteVersion > 0 && value.Lifetime > TimeSpan.Zero &&
        value.Lifetime <= policy.MaximumReservationLifetime && value.Initiator.IsValid &&
        value.CorrelationId != Guid.Empty && ValidAudit(value.AuditSource);

    private static bool Valid(NCReleaseEconomicReservationRequest value) => value.CommandId != Guid.Empty &&
        value.ReservationId.IsValid && value.ExpectedReservationVersion > 0 &&
        value.FinalStatus is NCEconomicReservationStatus.Released or NCEconomicReservationStatus.Expired &&
        value.Initiator.IsValid && value.CorrelationId != Guid.Empty && ValidAudit(value.AuditSource);

    private static bool Valid(
        NCEconomicReservationSnapshot? value,
        NCEconomicReservationId expected) => value != null && value.ReservationId == expected && expected.IsValid &&
        value.QuoteId.IsValid && Enum.IsDefined(value.OfferKind) && !string.IsNullOrWhiteSpace(value.OfferId) &&
        value.OfferId.Length <= 256 && value.Quantity > 0 && value.Seller.IsValid && value.Buyer.IsValid &&
        value.Seller != value.Buyer && (!value.SourceStockNodeId.HasValue || value.SourceStockNodeId.Value.IsValid) &&
        value.ReservedStockNodeVersion >= 0 && value.ReservedAssetVersion >= 0 &&
        value.RequestedLifetime > TimeSpan.Zero && value.Initiator.IsValid && value.CreatedAt.IsValid &&
        value.ExpiresAt > value.CreatedAt && Enum.IsDefined(value.Status) && value.DataVersion > 0 &&
        (!value.SettlementTransactionId.HasValue || value.SettlementTransactionId.Value.IsValid) &&
        (!value.RefundTransactionId.HasValue || value.RefundTransactionId.Value.IsValid) &&
        value.LastAppliedEventId != Guid.Empty &&
        (value.OfferKind is NCEconomicOfferKind.CommodityBatch or NCEconomicOfferKind.ItemInstance
            ? value.SourceStockNodeId.HasValue && value.ReservedStockNodeVersion > 0 &&
              value.ReservedAssetVersion > 0 && TryAssetId(value.OfferId, out _)
            : !value.SourceStockNodeId.HasValue && value.ReservedStockNodeVersion == 0 &&
              value.ReservedAssetVersion == 0 && IsPrototypeKey(value.OfferId, 128));

    private static bool Same(
        NCEconomicReservationSnapshot value,
        NCCreateEconomicReservationRequest request) => value.QuoteId == request.QuoteId &&
        value.RequestedLifetime == request.Lifetime && value.Initiator == request.Initiator;

    private static NCEconomicReservationRecord? DeserializeReservation(
        NCAggregateSnapshotData? stored,
        NCEconomicReservationId expected)
    {
        if (!StoredShape(stored))
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCEconomicReservationSnapshot>(stored!.Payload, JsonOptions);
            return Valid(snapshot, expected) && snapshot!.DataVersion == stored.AggregateVersion
                ? new NCEconomicReservationRecord(snapshot, stored.AggregateVersion, stored.SessionEpoch,
                    stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static NCEconomicReservationMutationResult RejectReservation(
        NCEconomicAssetMutationStatus status,
        string error) => new(status, null, null, null, null, null, error);

    private static NCEconomicReservationMutationResult FromPersistenceReservation(
        NCPersistentBatchMutationResult result) => RejectReservation(result.Status switch
        {
            NCPersistentMutationStatus.VersionConflict => NCEconomicAssetMutationStatus.VersionConflict,
            NCPersistentMutationStatus.ClockRejected => NCEconomicAssetMutationStatus.ClockUnavailable,
            _ => NCEconomicAssetMutationStatus.StorageFailure,
        }, result.Error ?? "The economic reservation mutation was not committed.");

    private static string Key(NCEconomicReservationId id) => id.Value.ToString("D");
}
