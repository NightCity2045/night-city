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
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Economy;

/// <summary>
/// Server-authoritative quote calculation. Callers provide bounded market context, never a final price.
/// </summary>
public sealed partial class NCEconomicAssetRegistrySystem
{
    public const string EconomicQuoteAggregateType = "living-city.economic-quote";

    private static readonly ProtoId<NCEconomicPricingPolicyPrototype> DefaultPricingPolicy = "Default";
    private const string CreateQuoteCommand = "living-city.economic-quote.create";

    private readonly ConcurrentDictionary<NCEconomicQuoteId, NCEconomicQuoteRecord> _quotes = [];

    public int CachedQuoteCount => _quotes.Count;

    public bool Unload(NCEconomicQuoteId id) => _quotes.TryRemove(id, out _);

    public async Task<NCEconomicQuoteRecord?> GetQuoteAsync(NCEconomicQuoteId id, bool forceReload = false)
    {
        if (!id.IsValid)
            return null;
        if (!forceReload && _quotes.TryGetValue(id, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(EconomicQuoteAggregateType, Key(id));
        var record = DeserializeQuote(stored, id);
        return record == null
            ? null
            : _quotes.AddOrUpdate(id, record,
                (_, current) => current.AggregateVersion > record.AggregateVersion ? current : record);
    }

    public async Task<NCEconomicQuoteMutationResult> CreateQuoteAsync(NCCreateEconomicQuoteRequest request)
    {
        if (!TryGetPricingPolicy(out var policy) || !Valid(request, policy) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return RejectQuote(_clock.IsReady
                ? NCEconomicAssetMutationStatus.Rejected
                : NCEconomicAssetMutationStatus.ClockUnavailable,
                "The quote request, pricing policy, or active-world clock is invalid.");
        }

        try
        {
            if (!await ResolveParties(request.Seller, request.Buyer, request.Initiator))
                return RejectQuote(NCEconomicAssetMutationStatus.PartyUnavailable,
                    "The seller, buyer, or initiator is unavailable.");

            var command = await InspectCommand(request.CommandId, CreateQuoteCommand, "economic-quote",
                Key(request.QuoteId));
            if (command == InitialCommandState.Collision)
                return RejectQuote(NCEconomicAssetMutationStatus.Rejected,
                    "The command ID belongs to another operation.");
            if (command == InitialCommandState.InProgress)
                return RejectQuote(NCEconomicAssetMutationStatus.StorageFailure,
                    "The quote command is still in progress.");

            var gateKeys = new List<Guid> { request.QuoteId.Value };
            if (request.SourceStockNodeId is { } stockNodeId)
                gateKeys.Add(stockNodeId.Value);
            if (Guid.TryParse(request.OfferId, out var assetId))
                gateKeys.Add(assetId);
            var gates = await AcquireGates(gateKeys.ToArray());
            try
            {
                var existing = await GetQuoteAsync(request.QuoteId, true);
                if (existing != null)
                {
                    return Same(existing.Snapshot, request)
                        ? new NCEconomicQuoteMutationResult(
                            NCEconomicAssetMutationStatus.Duplicate, existing, null)
                        : RejectQuote(NCEconomicAssetMutationStatus.Rejected,
                            "The quote identity is bound to different immutable data.");
                }
                if (command == InitialCommandState.Completed)
                    return RejectQuote(NCEconomicAssetMutationStatus.StorageFailure,
                        "The completed command is missing its quote aggregate.");

                var offer = await ResolveQuoteOffer(request, now);
                if (offer.Error != null)
                    return RejectQuote(offer.Status, offer.Error);

                var context = request.PriceContext with { Legality = LegalityInput(offer.LegalStatus) };
                if (!TryCalculatePrice(offer.BaseAmount, context, policy, out var price))
                    return RejectQuote(NCEconomicAssetMutationStatus.Rejected,
                        "The server price is unavailable or exceeds the configured quote limit.");

                var eventId = Guid.NewGuid();
                var quote = new NCEconomicQuoteSnapshot(
                    request.QuoteId,
                    request.OfferKind,
                    CanonicalOfferId(request.OfferKind, request.OfferId),
                    offer.DefinitionId,
                    request.Quantity,
                    request.Seller,
                    request.Buyer,
                    request.SourceStockNodeId,
                    request.ExpectedStockNodeVersion,
                    request.ExpectedAssetVersion,
                    policy.CurrencyId.Trim().ToUpperInvariant(),
                    context,
                    price!,
                    request.Lifetime,
                    request.Initiator,
                    now,
                    now.Advance(request.Lifetime),
                    NCEconomicQuoteStatus.Open,
                    null,
                    null,
                    null,
                    null,
                    1,
                    eventId);
                var persisted = await Persist(
                    command == InitialCommandState.Available ? request.CommandId : Guid.NewGuid(),
                    CreateQuoteCommand,
                    "economic-quote",
                    Key(request.QuoteId),
                    [QuoteWrite(null, quote, eventId, "living-city.economic-quote.created", request.AuditSource)],
                    new AssetReceipt(Key(request.QuoteId), 1),
                    request.CorrelationId,
                    request.CausationId,
                    epoch,
                    now);
                if (persisted.Status is not (NCPersistentMutationStatus.Applied or
                    NCPersistentMutationStatus.DuplicateCompleted))
                {
                    return RejectQuote(FromPersistence(persisted).Status,
                        persisted.Error ?? "The quote was not committed.");
                }

                var loaded = await GetQuoteAsync(request.QuoteId, true);
                return loaded == null
                    ? RejectQuote(NCEconomicAssetMutationStatus.StorageFailure,
                        "The committed quote could not be reloaded.")
                    : new NCEconomicQuoteMutationResult(
                        persisted.Status == NCPersistentMutationStatus.Applied
                            ? NCEconomicAssetMutationStatus.Applied
                            : NCEconomicAssetMutationStatus.Duplicate,
                        loaded,
                        null);
            }
            finally
            {
                ReleaseGates(gates);
            }
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to create economic quote {request.QuoteId}: {exception}");
            return RejectQuote(NCEconomicAssetMutationStatus.StorageFailure, exception.Message);
        }
    }

    private async Task<ResolvedQuoteOffer> ResolveQuoteOffer(
        NCCreateEconomicQuoteRequest request,
        NCActiveWorldTime now)
    {
        switch (request.OfferKind)
        {
            case NCEconomicOfferKind.CommodityBatch:
            {
                if (!TryAssetId(request.OfferId, out var assetId) || request.SourceStockNodeId is not { } nodeId)
                    return ResolvedQuoteOffer.Reject("The commodity offer reference is invalid.");
                var batch = await GetCommodityBatchAsync(new NCCommodityBatchId(assetId), true);
                var node = await GetStockNodeAsync(nodeId, true);
                if (batch == null || node == null)
                    return ResolvedQuoteOffer.NotFound("The commodity batch or stock node does not exist.");
                if (_materializationLeases.IsLeased(nodeId))
                    return ResolvedQuoteOffer.Reject("A materialized stock node requires a physical trade interaction.");
                if (batch.AggregateVersion != request.ExpectedAssetVersion ||
                    node.AggregateVersion != request.ExpectedStockNodeVersion)
                {
                    return ResolvedQuoteOffer.Conflict("The quoted commodity or stock version changed.");
                }
                if (batch.Snapshot.Owner != request.Seller || node.Snapshot.Owner != request.Seller ||
                    batch.Snapshot.Location != StockLocation(nodeId) ||
                    !node.Snapshot.CommodityBatchIds.Contains(batch.Snapshot.BatchId) ||
                    batch.Snapshot.ExpiresAt is { } expiresAt && now >= expiresAt ||
                    request.Quantity > batch.Snapshot.Quantity - batch.Snapshot.ReservedQuantity)
                {
                    return ResolvedQuoteOffer.Reject("The commodity ownership, location, or available quantity is invalid.");
                }
                if (!_prototypes.TryIndex(new ProtoId<NCCommodityPrototype>(batch.Snapshot.CommodityId),
                        out var commodity) || commodity.BaseUnitPrice is not { } baseUnitPrice)
                {
                    return ResolvedQuoteOffer.Reject("The commodity does not have an authored server price.");
                }
                if (!TryScaledAmount(baseUnitPrice, request.Quantity, batch.Snapshot.Quality, out var baseAmount))
                    return ResolvedQuoteOffer.Reject("The commodity base amount overflows the quote boundary.");
                return ResolvedQuoteOffer.Success(commodity.ID, baseAmount, batch.Snapshot.LegalStatus);
            }
            case NCEconomicOfferKind.ItemInstance:
            {
                if (!TryAssetId(request.OfferId, out var assetId) || request.SourceStockNodeId is not { } nodeId ||
                    request.Quantity != 1)
                {
                    return ResolvedQuoteOffer.Reject("The item offer reference or quantity is invalid.");
                }
                var item = await GetItemAsync(new NCItemId(assetId), true);
                var node = await GetStockNodeAsync(nodeId, true);
                if (item == null || node == null)
                    return ResolvedQuoteOffer.NotFound("The item or stock node does not exist.");
                if (_materializationLeases.IsLeased(nodeId))
                    return ResolvedQuoteOffer.Reject("A materialized stock node requires a physical trade interaction.");
                if (item.AggregateVersion != request.ExpectedAssetVersion ||
                    node.AggregateVersion != request.ExpectedStockNodeVersion)
                {
                    return ResolvedQuoteOffer.Conflict("The quoted item or stock version changed.");
                }
                if (item.Snapshot.Owner != request.Seller || node.Snapshot.Owner != request.Seller ||
                    item.Snapshot.Location != StockLocation(nodeId) ||
                    !node.Snapshot.ItemIds.Contains(item.Snapshot.ItemId) || item.Snapshot.ReservationId.HasValue)
                {
                    return ResolvedQuoteOffer.Reject("The item ownership, location, or reservation state is invalid.");
                }
                if (!_prototypes.TryIndex(new ProtoId<NCItemDefinitionPrototype>(item.Snapshot.DefinitionId),
                        out var definition) || definition.BasePrice is not { } basePrice)
                {
                    return ResolvedQuoteOffer.Reject("The item does not have an authored server price.");
                }
                if (!TryScaledAmount(basePrice, 1, item.Snapshot.Condition, out var baseAmount))
                    return ResolvedQuoteOffer.Reject("The item base amount overflows the quote boundary.");
                return ResolvedQuoteOffer.Success(definition!.ID, baseAmount, item.Snapshot.LegalStatus);
            }
            case NCEconomicOfferKind.Service:
            {
                if (!_prototypes.TryIndex(new ProtoId<NCEconomicServicePrototype>(request.OfferId), out var service) ||
                    service.BasePrice is not { } basePrice || request.SourceStockNodeId.HasValue ||
                    request.ExpectedStockNodeVersion != 0 || request.ExpectedAssetVersion != 0 ||
                    !TryScaledAmount(basePrice, request.Quantity, 1_000, out var baseAmount))
                {
                    return ResolvedQuoteOffer.Reject("The service definition, price, or non-stock shape is invalid.");
                }
                return ResolvedQuoteOffer.Success(service.ID, baseAmount, service.LegalStatus);
            }
            case NCEconomicOfferKind.InformationAccess:
            {
                if (!_prototypes.TryIndex(new ProtoId<NCEconomicInformationAccessPrototype>(request.OfferId),
                        out var access) || access.BasePrice is not { } basePrice || request.Quantity != 1 ||
                    request.SourceStockNodeId.HasValue || request.ExpectedStockNodeVersion != 0 ||
                    request.ExpectedAssetVersion != 0 ||
                    !TryScaledAmount(basePrice, 1, 1_000, out var baseAmount))
                {
                    return ResolvedQuoteOffer.Reject("The information/access definition, price, or shape is invalid.");
                }
                return ResolvedQuoteOffer.Success(access.ID, baseAmount, access.LegalStatus);
            }
            default:
                return ResolvedQuoteOffer.Reject("The offer kind is unsupported.");
        }
    }

    private static bool TryCalculatePrice(
        long baseAmount,
        NCEconomicPriceContext context,
        NCEconomicPricingPolicyPrototype policy,
        out NCEconomicPriceBreakdown? price)
    {
        price = null;
        if (baseAmount < 0)
            return false;
        try
        {
            var weighted = (long) context.Scarcity * policy.ScarcityWeightBasisPoints +
                (long) context.Demand * policy.DemandWeightBasisPoints +
                (long) context.Danger * policy.DangerWeightBasisPoints +
                (long) context.Relationship * policy.RelationshipWeightBasisPoints +
                (long) context.Legality * policy.LegalityWeightBasisPoints +
                (long) context.Urgency * policy.UrgencyWeightBasisPoints;
            var multiplier = Math.Clamp(10_000L + weighted / 1_000,
                policy.MinimumMultiplierBasisPoints, policy.MaximumMultiplierBasisPoints);
            var subtotal = RoundMoney((decimal) baseAmount * multiplier / 10_000m);
            var fee = RoundMoney((decimal) subtotal * policy.FeeBasisPoints / 10_000m);
            var tax = RoundMoney((decimal) subtotal * policy.TaxBasisPoints / 10_000m);
            var total = checked(subtotal + fee + tax);
            if (total < 0 || total > policy.MaximumQuoteTotal)
                return false;
            price = new NCEconomicPriceBreakdown(baseAmount, checked((int) multiplier), subtotal, fee, tax, total);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryScaledAmount(long unitPrice, long quantity, int quality, out long amount)
    {
        amount = 0;
        if (unitPrice < 0 || quantity <= 0 || quality is < 0 or > 1_000)
            return false;
        try
        {
            var calculated = decimal.Round((decimal) unitPrice * quantity * quality / 1_000m,
                0, MidpointRounding.AwayFromZero);
            if (calculated is < 0 or > long.MaxValue)
                return false;
            amount = (long) calculated;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static long RoundMoney(decimal value)
    {
        var rounded = decimal.Round(value, 0, MidpointRounding.AwayFromZero);
        if (rounded is < long.MinValue or > long.MaxValue)
            throw new OverflowException();
        return (long) rounded;
    }

    private NCPersistentAggregateWrite QuoteWrite(
        NCEconomicQuoteRecord? current,
        NCEconomicQuoteSnapshot next,
        Guid eventId,
        string eventType,
        string auditSource) => new(
        eventId,
        eventType,
        EconomicQuoteAggregateType,
        Key(next.QuoteId),
        current?.AggregateVersion ?? 0,
        SnapshotFormatVersion,
        JsonSerializer.Serialize(next, JsonOptions),
        JsonSerializer.Serialize(new AssetEvent(Key(next.QuoteId), auditSource.Trim()), JsonOptions));

    private bool TryGetPricingPolicy(out NCEconomicPricingPolicyPrototype policy) =>
        _prototypes.TryIndex(DefaultPricingPolicy, out policy!) && Valid(policy);

    private static bool Valid(NCEconomicPricingPolicyPrototype value) =>
        IsStableKey(value.CurrencyId, 32) && value.MaximumQuoteLifetime > TimeSpan.Zero &&
        value.MaximumQuoteLifetime <= TimeSpan.FromHours(24) && value.MaximumReservationLifetime > TimeSpan.Zero &&
        value.MaximumReservationLifetime <= value.MaximumQuoteLifetime &&
        value.MinimumMultiplierBasisPoints is >= 0 and <= 100_000 &&
        value.MaximumMultiplierBasisPoints >= value.MinimumMultiplierBasisPoints &&
        value.MaximumMultiplierBasisPoints <= 100_000 && ValidWeight(value.ScarcityWeightBasisPoints) &&
        ValidWeight(value.DemandWeightBasisPoints) && ValidWeight(value.DangerWeightBasisPoints) &&
        ValidWeight(value.RelationshipWeightBasisPoints) && ValidWeight(value.LegalityWeightBasisPoints) &&
        ValidWeight(value.UrgencyWeightBasisPoints) && value.FeeBasisPoints is >= 0 and <= 100_000 &&
        value.TaxBasisPoints is >= 0 and <= 100_000 &&
        (value.FeeBasisPoints == 0 && string.IsNullOrWhiteSpace(value.FeeReceiverServiceId) ||
            value.FeeBasisPoints > 0 && IsStableKey(value.FeeReceiverServiceId, 128)) &&
        (value.TaxBasisPoints == 0 && string.IsNullOrWhiteSpace(value.TaxReceiverServiceId) ||
            value.TaxBasisPoints > 0 && IsStableKey(value.TaxReceiverServiceId, 128)) &&
        value.MaximumQuoteTotal > 0;

    private static bool Valid(NCEconomicServicePrototype value) => IsStableKey(value.StableKey, 128) &&
        IsStableKey(value.FulfillmentKey, 128) && value.BasePrice is null or >= 0 &&
        value.ExpectedDuration >= TimeSpan.Zero && Enum.IsDefined(value.LegalStatus);

    private static bool Valid(NCEconomicInformationAccessPrototype value) => IsStableKey(value.StableKey, 128) &&
        IsStableKey(value.GrantKey, 128) && value.BasePrice is null or >= 0 && Enum.IsDefined(value.LegalStatus);

    private static bool Valid(NCCreateEconomicQuoteRequest value, NCEconomicPricingPolicyPrototype policy) =>
        value.CommandId != Guid.Empty && value.QuoteId.IsValid && Enum.IsDefined(value.OfferKind) &&
        !string.IsNullOrWhiteSpace(value.OfferId) && value.OfferId == value.OfferId.Trim() &&
        value.OfferId.Length <= 256 && value.Quantity > 0 && value.Seller.IsValid && value.Buyer.IsValid &&
        value.Seller != value.Buyer && value.ExpectedStockNodeVersion >= 0 && value.ExpectedAssetVersion >= 0 &&
        Valid(value.PriceContext) && value.Lifetime > TimeSpan.Zero && value.Lifetime <= policy.MaximumQuoteLifetime &&
        value.Initiator.IsValid && value.CorrelationId != Guid.Empty && ValidAudit(value.AuditSource);

    private static bool Valid(NCEconomicPriceContext value) => value != null && ValidInput(value.Scarcity) &&
        ValidInput(value.Demand) && ValidInput(value.Danger) && ValidInput(value.Relationship) &&
        ValidInput(value.Legality) && ValidInput(value.Urgency);

    private static bool Valid(NCEconomicQuoteSnapshot? value, NCEconomicQuoteId expected) => value != null &&
        value.QuoteId == expected && expected.IsValid && Enum.IsDefined(value.OfferKind) &&
        !string.IsNullOrWhiteSpace(value.OfferId) && value.OfferId.Length <= 256 &&
        IsPrototypeKey(value.DefinitionId, 128) && value.Quantity > 0 && value.Seller.IsValid && value.Buyer.IsValid &&
        value.Seller != value.Buyer && (!value.SourceStockNodeId.HasValue || value.SourceStockNodeId.Value.IsValid) &&
        value.QuotedStockNodeVersion >= 0 && value.QuotedAssetVersion >= 0 && ValidCurrency(value.CurrencyId) &&
        Valid(value.PriceContext) && Valid(value.Price) && value.RequestedLifetime > TimeSpan.Zero &&
        value.Initiator.IsValid && value.CreatedAt.IsValid && value.ExpiresAt > value.CreatedAt &&
        Enum.IsDefined(value.Status) && (!value.SettlementTransactionId.HasValue ||
            value.SettlementTransactionId.Value.IsValid) && (!value.RefundTransactionId.HasValue ||
            value.RefundTransactionId.Value.IsValid) && (!value.SettlementTransferId.HasValue ||
            value.SettlementTransferId.Value.IsValid) && (!value.FulfillmentId.HasValue ||
            value.FulfillmentId.Value.IsValid) && value.DataVersion > 0 && value.LastAppliedEventId != Guid.Empty;

    private static bool Valid(NCEconomicPriceBreakdown value) => value != null && value.BaseAmount >= 0 &&
        value.AppliedMultiplierBasisPoints >= 0 && value.Subtotal >= 0 && value.Fee >= 0 && value.Tax >= 0 &&
        value.Total >= 0 && value.Total == value.Subtotal + value.Fee + value.Tax;

    private static bool Same(NCEconomicQuoteSnapshot value, NCCreateEconomicQuoteRequest request) =>
        value.OfferKind == request.OfferKind && value.OfferId == CanonicalOfferId(request.OfferKind, request.OfferId) &&
        value.Quantity == request.Quantity && value.Seller == request.Seller && value.Buyer == request.Buyer &&
        value.SourceStockNodeId == request.SourceStockNodeId &&
        value.QuotedStockNodeVersion == request.ExpectedStockNodeVersion &&
        value.QuotedAssetVersion == request.ExpectedAssetVersion && value.PriceContext ==
        request.PriceContext with { Legality = value.PriceContext.Legality } &&
        value.RequestedLifetime == request.Lifetime && value.Initiator == request.Initiator;

    private static NCEconomicQuoteRecord? DeserializeQuote(NCAggregateSnapshotData? stored, NCEconomicQuoteId expected)
    {
        if (!StoredShape(stored))
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCEconomicQuoteSnapshot>(stored!.Payload, JsonOptions);
            return Valid(snapshot, expected) && snapshot!.DataVersion == stored.AggregateVersion
                ? new NCEconomicQuoteRecord(snapshot, stored.AggregateVersion, stored.SessionEpoch,
                    stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int LegalityInput(NCEconomicLegalStatus status) => status switch
    {
        NCEconomicLegalStatus.Legal => 0,
        NCEconomicLegalStatus.Restricted => 500,
        NCEconomicLegalStatus.Illegal => 1_000,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static bool ValidWeight(int value) => value is >= -100_000 and <= 100_000;
    private static bool ValidInput(int value) => value is >= -1_000 and <= 1_000;
    private static bool ValidCurrency(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 32 &&
        value == value.Trim().ToUpperInvariant() && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static bool TryAssetId(string value, out Guid id) => Guid.TryParseExact(value, "D", out id) &&
        id != Guid.Empty && value == id.ToString("D");
    private static string CanonicalOfferId(NCEconomicOfferKind kind, string value) =>
        kind is NCEconomicOfferKind.CommodityBatch or NCEconomicOfferKind.ItemInstance
            ? Guid.Parse(value).ToString("D")
            : value.Trim();

    private static NCEconomicQuoteMutationResult RejectQuote(
        NCEconomicAssetMutationStatus status,
        string error) => new(status, null, error);

    private static string Key(NCEconomicQuoteId id) => id.Value.ToString("D");

    private readonly record struct ResolvedQuoteOffer(
        NCEconomicAssetMutationStatus Status,
        string DefinitionId,
        long BaseAmount,
        NCEconomicLegalStatus LegalStatus,
        string? Error)
    {
        public static ResolvedQuoteOffer Success(string definitionId, long baseAmount, NCEconomicLegalStatus legal) =>
            new(NCEconomicAssetMutationStatus.Applied, definitionId, baseAmount, legal, null);
        public static ResolvedQuoteOffer Reject(string error) =>
            new(NCEconomicAssetMutationStatus.Rejected, string.Empty, 0, default, error);
        public static ResolvedQuoteOffer NotFound(string error) =>
            new(NCEconomicAssetMutationStatus.NotFound, string.Empty, 0, default, error);
        public static ResolvedQuoteOffer Conflict(string error) =>
            new(NCEconomicAssetMutationStatus.VersionConflict, string.Empty, 0, default, error);
    }
}
