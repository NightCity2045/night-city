// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Economy;

public enum NCEconomicAssetMutationStatus : byte
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

public sealed record NCCommodityBatchRecord(
    NCCommodityBatchSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCItemInstanceRecord(
    NCItemInstanceSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCStockNodeRecord(
    NCStockNodeSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCEconomicAssetTransferRecord(
    NCEconomicAssetTransferSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCEconomicQuoteRecord(
    NCEconomicQuoteSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCEconomicReservationRecord(
    NCEconomicReservationSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCEconomicFulfillmentRecord(
    NCEconomicFulfillmentSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCCreateStockNodeRequest(
    Guid CommandId,
    NCStockNodeId StockNodeId,
    string StableKey,
    NCStockNodeKind Kind,
    NCPartyRef Owner,
    string SemanticLocationKey,
    bool AcceptsDeposits,
    bool AllowsWithdrawals,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "stock-node-create");

public sealed record NCCreateCommodityBatchRequest(
    Guid CommandId,
    NCCommodityBatchId BatchId,
    ProtoId<NCCommodityPrototype> Commodity,
    long Quantity,
    int? Quality,
    NCPartyRef Owner,
    NCStockNodeId StockNodeId,
    long ExpectedStockNodeVersion,
    string OriginKey,
    NCPartyRef Manufacturer,
    IReadOnlyList<Guid> SourceWorldEventIds,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "commodity-batch-create");

public sealed record NCCreateItemInstanceRequest(
    Guid CommandId,
    NCItemId ItemId,
    ProtoId<NCItemDefinitionPrototype> Definition,
    string SerialNumber,
    int? Condition,
    NCPartyRef Owner,
    NCStockNodeId StockNodeId,
    long ExpectedStockNodeVersion,
    string OriginKey,
    NCPartyRef Manufacturer,
    IReadOnlyList<Guid> SourceWorldEventIds,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "item-instance-create");

public sealed record NCTransferItemRequest(
    Guid CommandId,
    NCEconomicTransferId TransferId,
    NCItemId ItemId,
    long ExpectedItemVersion,
    NCStockNodeId SourceStockNodeId,
    long ExpectedSourceStockNodeVersion,
    NCStockNodeId DestinationStockNodeId,
    long ExpectedDestinationStockNodeVersion,
    NCPartyRef ExpectedOwner,
    NCPartyRef NewOwner,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "item-transfer");

public sealed record NCTransferCommodityRequest(
    Guid CommandId,
    NCEconomicTransferId TransferId,
    NCCommodityBatchId SourceBatchId,
    NCCommodityBatchId? SplitBatchId,
    long Quantity,
    long ExpectedSourceBatchVersion,
    NCStockNodeId SourceStockNodeId,
    long ExpectedSourceStockNodeVersion,
    NCStockNodeId DestinationStockNodeId,
    long ExpectedDestinationStockNodeVersion,
    NCPartyRef ExpectedOwner,
    NCPartyRef NewOwner,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "commodity-transfer");

public sealed record NCDestroyEconomicAssetRequest(
    Guid CommandId,
    NCEconomicTransferAssetKind AssetKind,
    string AssetId,
    long ExpectedAssetVersion,
    NCStockNodeId StockNodeId,
    long ExpectedStockNodeVersion,
    NCPartyRef Owner,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "economic-asset-destroy");

/// <summary>
/// Server-only quote input. OfferId is an asset GUID for stock offers and a prototype ID for services/access.
/// There is deliberately no caller-supplied price field.
/// </summary>
public sealed record NCCreateEconomicQuoteRequest(
    Guid CommandId,
    NCEconomicQuoteId QuoteId,
    NCEconomicOfferKind OfferKind,
    string OfferId,
    long Quantity,
    NCPartyRef Seller,
    NCPartyRef Buyer,
    NCStockNodeId? SourceStockNodeId,
    long ExpectedStockNodeVersion,
    long ExpectedAssetVersion,
    NCEconomicPriceContext PriceContext,
    TimeSpan Lifetime,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "economic-quote-create");

public sealed record NCCreateEconomicReservationRequest(
    Guid CommandId,
    NCEconomicReservationId ReservationId,
    NCEconomicQuoteId QuoteId,
    long ExpectedQuoteVersion,
    TimeSpan Lifetime,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "economic-reservation-create");

public sealed record NCReleaseEconomicReservationRequest(
    Guid CommandId,
    NCEconomicReservationId ReservationId,
    long ExpectedReservationVersion,
    NCEconomicReservationStatus FinalStatus,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "economic-reservation-release");

public sealed record NCSettleEconomicReservationRequest(
    Guid CommandId,
    NCTransactionId TransactionId,
    NCEconomicReservationId ReservationId,
    long ExpectedReservationVersion,
    NCAccountId BuyerAccountId,
    long ExpectedBuyerAccountVersion,
    NCAccountId SellerAccountId,
    long ExpectedSellerAccountVersion,
    NCAccountId? FeeAccountId,
    long ExpectedFeeAccountVersion,
    NCAccountId? TaxAccountId,
    long ExpectedTaxAccountVersion,
    NCStockNodeId? DestinationStockNodeId,
    long ExpectedDestinationStockNodeVersion,
    NCEconomicTransferId? TransferId,
    NCCommodityBatchId? SplitBatchId,
    NCEconomicFulfillmentId? FulfillmentId,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "economic-purchase-settle");

/// <summary>
/// Server-domain acknowledgement for a paid service. The economy owns the durable obligation state while the
/// medical, cyberware, repair, or other provider system owns the actual gameplay action.
/// </summary>
public sealed record NCCompleteEconomicFulfillmentRequest(
    Guid CommandId,
    NCEconomicFulfillmentId FulfillmentId,
    long ExpectedFulfillmentVersion,
    NCEconomicFulfillmentStatus FinalStatus,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "economic-fulfillment-complete");

public sealed record NCEconomicRefundAccountVersion(
    NCAccountId AccountId,
    long ExpectedVersion);

/// <summary>
/// Requests a full reversal of one purchase. Monetary amounts and account membership are intentionally absent: the
/// server derives exact opposite legs from the immutable purchase transaction.
/// </summary>
public sealed record NCRefundEconomicSettlementRequest(
    Guid CommandId,
    NCTransactionId RefundTransactionId,
    NCEconomicReservationId ReservationId,
    long ExpectedReservationVersion,
    long ExpectedQuoteVersion,
    IReadOnlyList<NCEconomicRefundAccountVersion> AccountVersions,
    long ExpectedPurchasedAssetVersion,
    long ExpectedBuyerStockNodeVersion,
    long ExpectedSellerStockNodeVersion,
    NCEconomicTransferId? RefundTransferId,
    long ExpectedFulfillmentVersion,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "economic-purchase-refund");

public sealed record NCInstallEconomicItemPartRequest(
    Guid CommandId,
    NCItemId ParentItemId,
    long ExpectedParentVersion,
    NCItemId PartItemId,
    long ExpectedPartVersion,
    NCStockNodeId StockNodeId,
    long ExpectedStockNodeVersion,
    NCPartyRef Owner,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "economic-item-part-install");

public sealed record NCRemoveEconomicItemPartRequest(
    Guid CommandId,
    NCItemId ParentItemId,
    long ExpectedParentVersion,
    NCItemId PartItemId,
    long ExpectedPartVersion,
    NCStockNodeId DestinationStockNodeId,
    long ExpectedStockNodeVersion,
    NCPartyRef Owner,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "economic-item-part-remove");

/// <summary>
/// Server-side consumption request for an abstract commodity. The caller supplies identity and quantity, while the
/// registry validates custody, expiry, reservations, and active-round time before applying the stock sink.
/// </summary>
public sealed record NCConsumeEconomicCommodityRequest(
    Guid CommandId,
    NCCommodityBatchId BatchId,
    long ExpectedBatchVersion,
    NCStockNodeId StockNodeId,
    long ExpectedStockNodeVersion,
    long Quantity,
    NCPartyRef Owner,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "economic-commodity-consume");

public sealed record NCEconomicAssetMutationResult(
    NCEconomicAssetMutationStatus Status,
    NCStockNodeRecord? StockNode,
    NCCommodityBatchRecord? CommodityBatch,
    NCItemInstanceRecord? Item,
    string? Error);

public sealed record NCEconomicAssetTransferResult(
    NCEconomicAssetMutationStatus Status,
    NCEconomicAssetTransferRecord? Transfer,
    NCStockNodeRecord? SourceStockNode,
    NCStockNodeRecord? DestinationStockNode,
    NCCommodityBatchRecord? SourceCommodityBatch,
    NCCommodityBatchRecord? DestinationCommodityBatch,
    NCItemInstanceRecord? Item,
    string? Error);

public sealed record NCEconomicQuoteMutationResult(
    NCEconomicAssetMutationStatus Status,
    NCEconomicQuoteRecord? Quote,
    string? Error);

public sealed record NCEconomicReservationMutationResult(
    NCEconomicAssetMutationStatus Status,
    NCEconomicReservationRecord? Reservation,
    NCEconomicQuoteRecord? Quote,
    NCStockNodeRecord? StockNode,
    NCCommodityBatchRecord? CommodityBatch,
    NCItemInstanceRecord? Item,
    string? Error);

public enum NCEconomicTradeMutationStatus : byte
{
    Applied,
    Duplicate,
    NotFound,
    VersionConflict,
    InsufficientFunds,
    PartyUnavailable,
    ClockUnavailable,
    Rejected,
    StorageFailure,
}

public sealed record NCEconomicTradeMutationResult(
    NCEconomicTradeMutationStatus Status,
    NCEconomyMutationResult? Ledger,
    NCEconomicReservationRecord? Reservation,
    NCEconomicQuoteRecord? Quote,
    NCEconomicAssetTransferRecord? Transfer,
    NCEconomicFulfillmentRecord? Fulfillment,
    NCStockNodeRecord? SourceStockNode,
    NCStockNodeRecord? DestinationStockNode,
    NCCommodityBatchRecord? SourceCommodityBatch,
    NCCommodityBatchRecord? DestinationCommodityBatch,
    NCItemInstanceRecord? Item,
    string? Error);

public sealed record NCEconomicFulfillmentMutationResult(
    NCEconomicAssetMutationStatus Status,
    NCEconomicFulfillmentRecord? Fulfillment,
    string? Error);

public sealed record NCEconomicItemAssemblyMutationResult(
    NCEconomicAssetMutationStatus Status,
    NCItemInstanceRecord? Parent,
    NCItemInstanceRecord? Part,
    NCStockNodeRecord? StockNode,
    string? Error);
