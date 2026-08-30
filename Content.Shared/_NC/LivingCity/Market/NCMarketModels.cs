// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Market;

[Serializable, NetSerializable]
public enum NCMarketChannel : byte
{
    Physical,
    Agent,
    Broker,
    Direct,
}

[Serializable, NetSerializable]
public enum NCMarketVisibility : byte
{
    Public,
    Relationship,
    Faction,
    Private,
}

[Serializable, NetSerializable]
public enum NCMarketListingStatus : byte
{
    Draft,
    Open,
    Reserved,
    Fulfilled,
    Cancelled,
    Expired,
}

[Serializable, NetSerializable]
public enum NCMarketRequestKind : byte
{
    Commodity,
    UniqueItem,
    Service,
    Information,
    Shipment,
}

[Serializable, NetSerializable]
public enum NCMarketRequestStatus : byte
{
    Open,
    Matched,
    Fulfilled,
    Cancelled,
    Expired,
}

[Serializable, NetSerializable]
public enum NCBusinessKind : byte
{
    Workshop,
    Manufacturer,
    BiotechLab,
    Clinic,
    Pharmacy,
    Retailer,
    Carrier,
    Brokerage,
}

[Serializable, NetSerializable]
public enum NCBusinessStatus : byte
{
    Active,
    Shortage,
    Suspended,
    Insolvent,
    Dissolved,
}

[Serializable, NetSerializable]
public enum NCProductionJobStatus : byte
{
    Queued,
    InputsReserved,
    InProgress,
    Completed,
    Failed,
    Cancelled,
}

[Serializable, NetSerializable]
public enum NCShipmentStatus : byte
{
    Planned,
    Loading,
    InTransit,
    AtRisk,
    AwaitingHandoff,
    Intercepted,
    Lost,
    Delivered,
    Cancelled,
}

[Serializable, NetSerializable]
public enum NCShipmentHandoffKind : byte
{
    Warehouse,
    ParcelLocker,
    IntakeTerminal,
    Direct,
    DeadDrop,
}

public sealed record NCMarketAccessPolicy(
    NCMarketVisibility Visibility,
    IReadOnlyList<NCPartyRef> AllowedParties,
    string AudienceKey,
    int MinimumRelationship,
    int MinimumReputation);

public sealed record NCMarketListingSnapshot(
    NCMarketListingId ListingId,
    NCEconomicOfferKind OfferKind,
    string OfferId,
    long MaximumQuantity,
    NCPartyRef Seller,
    NCStockNodeId? SourceStockNodeId,
    NCMarketChannel Channel,
    NCMarketAccessPolicy Access,
    NCMarketListingStatus Status,
    NCActiveWorldTime? ExpiresAt,
    NCActiveWorldTime CreatedAt,
    NCActiveWorldTime UpdatedAt,
    int DataVersion,
    Guid LastAppliedEventId);

public sealed record NCMarketRequestSnapshot(
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
    NCMarketRequestStatus Status,
    NCMarketListingId? MatchedListingId,
    NCActiveWorldTime ExpiresAt,
    NCActiveWorldTime CreatedAt,
    NCActiveWorldTime UpdatedAt,
    int DataVersion,
    Guid LastAppliedEventId);

public sealed record NCBusinessOperationPolicy(
    int PriceMarkupBasisPoints,
    int RestockThresholdBasisPoints,
    long MinimumCashReserve,
    int MaximumConcurrentProduction,
    int MaximumConcurrentShipments,
    bool AllowsAggregatedDemand);

public sealed record NCBusinessSnapshot(
    NCBusinessId BusinessId,
    string StableKey,
    string DisplayName,
    NCBusinessKind Kind,
    NCZoneId OperatingZone,
    NCPartyRef BusinessParty,
    NCPartyRef Owner,
    IReadOnlyList<NCPartyRef> Operators,
    NCAccountId AccountId,
    NCStockNodeId InventoryStockNodeId,
    NCStockNodeId OutputStockNodeId,
    NCBusinessOperationPolicy Policy,
    NCBusinessStatus Status,
    IReadOnlyList<NCProductionJobId> ProductionQueue,
    IReadOnlyList<NCShipmentId> ActiveShipments,
    NCActiveWorldTime CreatedAt,
    NCActiveWorldTime UpdatedAt,
    int DataVersion,
    Guid LastAppliedEventId);

public sealed record NCProductionInputAllocation(
    NCCommodityBatchId BatchId,
    NCStockNodeId StockNodeId,
    long Quantity,
    long ReservedBatchVersion,
    long ReservedStockNodeVersion);

public sealed record NCProductionJobSnapshot(
    NCProductionJobId JobId,
    NCBusinessId BusinessId,
    string RecipeId,
    int Runs,
    IReadOnlyList<NCProductionInputAllocation> Inputs,
    NCStockNodeId OutputStockNodeId,
    NCProductionJobStatus Status,
    NCActiveWorldTime StartedAt,
    NCActiveWorldTime DueAt,
    string FailureCode,
    NCActiveWorldTime CreatedAt,
    NCActiveWorldTime UpdatedAt,
    int DataVersion,
    Guid LastAppliedEventId);

public sealed record NCShipmentAsset(
    NCEconomicTransferAssetKind Kind,
    string AssetId,
    long Quantity);

public sealed record NCShipmentRouteLeg(
    NCZoneId FromZone,
    NCZoneId ToZone,
    string TraversalProfileId,
    TimeSpan ExpectedDuration,
    int RiskBasisPoints);

public sealed record NCShipmentSnapshot(
    NCShipmentId ShipmentId,
    NCPartyRef Shipper,
    NCPartyRef Recipient,
    NCPartyRef Carrier,
    NCStockNodeId OriginStockNodeId,
    NCStockNodeId TransitStockNodeId,
    NCStockNodeId DestinationStockNodeId,
    NCZoneId OriginZone,
    NCZoneId DestinationZone,
    NCCityTravelMode TravelMode,
    IReadOnlyList<NCShipmentAsset> Assets,
    IReadOnlyList<NCShipmentRouteLeg> Route,
    int CurrentLeg,
    int RiskBasisPoints,
    NCShipmentHandoffKind HandoffKind,
    NCShipmentStatus Status,
    NCActiveWorldTime? ExpectedArrivalAt,
    Guid? ProofOfDeliveryEventId,
    NCDisputeId? DisputeId,
    NCActiveWorldTime CreatedAt,
    NCActiveWorldTime UpdatedAt,
    int DataVersion,
    Guid LastAppliedEventId);
