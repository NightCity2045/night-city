// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Economy;

[Serializable, NetSerializable]
public enum NCEconomicOfferKind : byte
{
    CommodityBatch,
    ItemInstance,
    Service,
    InformationAccess,
}

[Serializable, NetSerializable]
public enum NCEconomicQuoteStatus : byte
{
    Open,
    Reserved,
    Settled,
    Refunded,
    Cancelled,
    Expired,
}

[Serializable, NetSerializable]
public enum NCEconomicReservationStatus : byte
{
    Active,
    Settled,
    Refunded,
    Released,
    Expired,
}

[Serializable, NetSerializable]
public enum NCEconomicFulfillmentStatus : byte
{
    Pending,
    Granted,
    Completed,
    Revoked,
    Failed,
}

/// <summary>
/// Bounded inputs calculated by server domain systems. Each value uses a -1000..1000 scale and is persisted so an
/// administrator can explain a quote without relying on generated text.
/// </summary>
public sealed record NCEconomicPriceContext(
    int Scarcity,
    int Demand,
    int Danger,
    int Relationship,
    int Legality,
    int Urgency);

public sealed record NCEconomicPriceBreakdown(
    long BaseAmount,
    int AppliedMultiplierBasisPoints,
    long Subtotal,
    long Fee,
    long Tax,
    long Total);

public sealed record NCEconomicQuoteSnapshot(
    NCEconomicQuoteId QuoteId,
    NCEconomicOfferKind OfferKind,
    string OfferId,
    string DefinitionId,
    long Quantity,
    NCPartyRef Seller,
    NCPartyRef Buyer,
    NCStockNodeId? SourceStockNodeId,
    long QuotedStockNodeVersion,
    long QuotedAssetVersion,
    string CurrencyId,
    NCEconomicPriceContext PriceContext,
    NCEconomicPriceBreakdown Price,
    TimeSpan RequestedLifetime,
    NCPartyRef Initiator,
    NCActiveWorldTime CreatedAt,
    NCActiveWorldTime ExpiresAt,
    NCEconomicQuoteStatus Status,
    NCTransactionId? SettlementTransactionId,
    NCTransactionId? RefundTransactionId,
    NCEconomicTransferId? SettlementTransferId,
    NCEconomicFulfillmentId? FulfillmentId,
    int DataVersion,
    Guid LastAppliedEventId);

public sealed record NCEconomicReservationSnapshot(
    NCEconomicReservationId ReservationId,
    NCEconomicQuoteId QuoteId,
    NCEconomicOfferKind OfferKind,
    string OfferId,
    long Quantity,
    NCPartyRef Seller,
    NCPartyRef Buyer,
    NCStockNodeId? SourceStockNodeId,
    long ReservedStockNodeVersion,
    long ReservedAssetVersion,
    TimeSpan RequestedLifetime,
    NCPartyRef Initiator,
    NCActiveWorldTime CreatedAt,
    NCActiveWorldTime ExpiresAt,
    NCEconomicReservationStatus Status,
    NCTransactionId? SettlementTransactionId,
    NCTransactionId? RefundTransactionId,
    int DataVersion,
    Guid LastAppliedEventId);

/// <summary>
/// Persistent result of buying a service or an information/access grant. Domain systems consume this record and own
/// the actual surgery, repair, credential, or disclosure mechanics.
/// </summary>
public sealed record NCEconomicFulfillmentSnapshot(
    NCEconomicFulfillmentId FulfillmentId,
    NCEconomicQuoteId QuoteId,
    NCEconomicOfferKind OfferKind,
    string DefinitionId,
    NCPartyRef Provider,
    NCPartyRef Recipient,
    NCEconomicFulfillmentStatus Status,
    NCTransactionId? RefundTransactionId,
    NCActiveWorldTime CreatedAt,
    NCActiveWorldTime UpdatedAt,
    int DataVersion,
    Guid LastAppliedEventId);
