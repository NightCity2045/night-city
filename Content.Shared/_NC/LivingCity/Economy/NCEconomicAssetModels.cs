// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Economy;

[Serializable, NetSerializable]
public enum NCCommodityUnit : byte
{
    Unit,
    Dose,
    Round,
    Gram,
    Milliliter,
    Meter,
}

[Serializable, NetSerializable]
public enum NCEconomicAssetDomain : byte
{
    Generic,
    WeaponAssembly,
    Pharmaceutical,
    Biotech,
    Cyberware,
    Document,
    Credential,
}

[Serializable, NetSerializable]
public enum NCEconomicLegalStatus : byte
{
    Legal,
    Restricted,
    Illegal,
}

[Serializable, NetSerializable]
public enum NCEconomicTradeStatus : byte
{
    Clean,
    Stolen,
    Fenced,
    Evidence,
}

[Serializable, NetSerializable]
public enum NCEconomicAssetLifecycle : byte
{
    Active,
    Consumed,
    Destroyed,
}

[Serializable, NetSerializable]
public enum NCEconomicAssetLocationKind : byte
{
    StockNode,
    PartyInventory,
    Property,
    Shipment,
    Escrow,
    InstalledInActor,
    InstalledInItem,
    PhysicalCustody,
    Consumed,
    Destroyed,
}

[Serializable, NetSerializable]
public enum NCStockNodeKind : byte
{
    Shop,
    Warehouse,
    Workshop,
    PropertyStorage,
    CharacterInventory,
    ActorInventory,
    OrganizationInventory,
    BusinessInventory,
    Escrow,
    Shipment,
    CityService,
}

[Serializable, NetSerializable]
public enum NCEconomicTransferAssetKind : byte
{
    CommodityBatch,
    ItemInstance,
}

/// <summary>
/// Persistent asset location. StableId is a persistent ID, canonical party string, or round-scoped materialization
/// token; an EntityUid is never stored here.
/// </summary>
public readonly record struct NCEconomicAssetLocation(
    NCEconomicAssetLocationKind Kind,
    string StableId);

/// <summary>
/// Bounded immutable manufacturing/acquisition provenance. Generated descriptions are not authoritative provenance.
/// </summary>
public sealed record NCEconomicAssetProvenance(
    string OriginKey,
    NCPartyRef Manufacturer,
    IReadOnlyList<Guid> SourceWorldEventIds,
    NCActiveWorldTime ProducedAt);

public sealed record NCCommodityBatchSnapshot(
    NCCommodityBatchId BatchId,
    string CommodityId,
    long Quantity,
    long ReservedQuantity,
    int Quality,
    NCPartyRef Owner,
    NCEconomicAssetLocation Location,
    NCEconomicLegalStatus LegalStatus,
    NCEconomicTradeStatus TradeStatus,
    NCEconomicAssetLifecycle Lifecycle,
    NCEconomicAssetProvenance Provenance,
    NCActiveWorldTime? ExpiresAt,
    NCActiveWorldTime CreatedAt,
    NCActiveWorldTime UpdatedAt,
    int DataVersion,
    Guid LastAppliedEventId);

/// <summary>
/// Unique persistent item. Assembly children are other item instances whose location is InstalledInItem.
/// </summary>
public sealed record NCItemInstanceSnapshot(
    NCItemId ItemId,
    string DefinitionId,
    string SerialNumber,
    int Condition,
    NCEconomicReservationId? ReservationId,
    NCPartyRef Owner,
    NCEconomicAssetLocation Location,
    NCEconomicLegalStatus LegalStatus,
    NCEconomicTradeStatus TradeStatus,
    NCEconomicAssetLifecycle Lifecycle,
    NCEconomicAssetProvenance Provenance,
    NCItemId? ParentItemId,
    IReadOnlyList<NCItemId> InstalledPartIds,
    NCActiveWorldTime CreatedAt,
    NCActiveWorldTime UpdatedAt,
    int DataVersion,
    Guid LastAppliedEventId);

public sealed record NCStockNodeSnapshot(
    NCStockNodeId StockNodeId,
    string StableKey,
    NCStockNodeKind Kind,
    NCPartyRef Owner,
    string SemanticLocationKey,
    IReadOnlyList<NCCommodityBatchId> CommodityBatchIds,
    IReadOnlyList<NCItemId> ItemIds,
    bool AcceptsDeposits,
    bool AllowsWithdrawals,
    NCActiveWorldTime CreatedAt,
    NCActiveWorldTime UpdatedAt,
    int DataVersion,
    Guid LastAppliedEventId);

/// <summary>
/// Immutable audit aggregate for one ownership/custody transfer. A partial commodity transfer names both the source
/// batch and the newly created split batch; unique items retain their persistent identity.
/// </summary>
public sealed record NCEconomicAssetTransferSnapshot(
    NCEconomicTransferId TransferId,
    NCEconomicQuoteId? QuoteId,
    NCEconomicReservationId? ReservationId,
    NCEconomicTransferAssetKind AssetKind,
    string SourceAssetId,
    string DestinationAssetId,
    long Quantity,
    NCStockNodeId SourceStockNodeId,
    NCStockNodeId DestinationStockNodeId,
    NCPartyRef PreviousOwner,
    NCPartyRef NewOwner,
    NCPartyRef Initiator,
    NCActiveWorldTime OccurredAt,
    int DataVersion,
    Guid LastAppliedEventId);
