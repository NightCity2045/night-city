// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Market;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Fixers;

[Serializable, NetSerializable]
public readonly record struct NCFixerNetworkId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

[Serializable, NetSerializable]
public readonly record struct NCFixerDealId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

[Serializable, NetSerializable]
public readonly record struct NCNightMarketId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

[Serializable, NetSerializable]
public enum NCFixerAccessStatus : byte
{
    Active,
    Suspended,
    Revoked,
}

[Serializable, NetSerializable]
public enum NCFixerDealStatus : byte
{
    Draft,
    Offered,
    Quoted,
    Reserved,
    EscrowFunded,
    InProgress,
    AwaitingHandoff,
    Fulfilled,
    Disputed,
    Arbitrated,
    Cancelled,
    Expired,
}

[Serializable, NetSerializable]
public enum NCFixerDealLegKind : byte
{
    Money,
    Commodity,
    UniqueAsset,
    Service,
    Shipment,
    Information,
    Access,
    DeadDrop,
    Obligation,
}

[Serializable, NetSerializable]
public enum NCFixerDealLegStatus : byte
{
    Planned,
    Ready,
    InProgress,
    Delivered,
    Failed,
    Waived,
}

[Serializable, NetSerializable]
public enum NCFixerEvidenceKind : byte
{
    LedgerTransaction,
    ShipmentProof,
    WorldEvent,
    WitnessStatement,
    DeviceRecord,
    PhysicalItem,
}

[Serializable, NetSerializable]
public enum NCFixerArbitrationOutcome : byte
{
    None,
    BuyerFavored,
    SellerFavored,
    Split,
    DealVoided,
}

[Serializable, NetSerializable]
public enum NCNightMarketStatus : byte
{
    Scheduled,
    Assembling,
    Open,
    Closing,
    Completed,
    Cancelled,
}

public sealed record NCFixerAccessGrant(
    NCPartyRef Client,
    NCPartyRef Sponsor,
    int MinimumTrust,
    int MinimumReputation,
    string ReputationAudienceKey,
    IReadOnlyList<NCSocialObligationId> RequiredObligations,
    NCFixerAccessStatus Status,
    NCActiveWorldTime GrantedAt,
    NCActiveWorldTime? ExpiresAt,
    string Reason);

public sealed record NCFixerNetworkSnapshot(
    NCFixerNetworkId NetworkId,
    NCPartyRef Fixer,
    IReadOnlyList<NCFixerAccessGrant> Access,
    IReadOnlyList<NCPartyRef> Delegates,
    int DataVersion,
    Guid LastAppliedEventId);

/// <summary>
/// A leg references another authoritative aggregate or a mapper zone. Payload text is an identifier, never a free-form
/// instruction to mutate gameplay state.
/// </summary>
public sealed record NCFixerDealLeg(
    Guid LegId,
    NCFixerDealLegKind Kind,
    NCPartyRef From,
    NCPartyRef To,
    string DefinitionId,
    long Quantity,
    long MoneyAmount,
    NCZoneId HandoffZone,
    NCShipmentHandoffKind? HandoffKind,
    NCMarketRequestId? MarketRequestId,
    NCShipmentId? ShipmentId,
    NCSocialObligationId? ObligationId,
    NCSocialObligationKind? ObligationKind,
    NCFixerDealLegStatus Status,
    Guid? ProofWorldEventId);

public sealed record NCFixerDealPlan(
    IReadOnlyList<NCFixerDealLeg> Legs,
    long EscrowAmount,
    int BrokerFeeBasisPoints,
    int RiskBasisPoints,
    bool RequiresPhysicalHandoff);

public sealed record NCFixerEvidence(
    Guid EvidenceId,
    NCFixerEvidenceKind Kind,
    NCPartyRef SubmittedBy,
    string ReferenceId,
    string SummaryKey,
    NCActiveWorldTime SubmittedAt);

public sealed record NCFixerDispute(
    Guid DisputeId,
    NCPartyRef OpenedBy,
    string ReasonKey,
    IReadOnlyList<NCFixerEvidence> Evidence,
    NCFixerArbitrationOutcome Outcome,
    NCPartyRef Arbitrator,
    string ResolutionKey,
    NCActiveWorldTime OpenedAt,
    NCActiveWorldTime? ResolvedAt);

public sealed record NCFixerDealSnapshot(
    NCFixerDealId DealId,
    NCFixerNetworkId NetworkId,
    NCPartyRef Fixer,
    NCPartyRef Buyer,
    NCPartyRef Seller,
    NCFixerDealPlan Plan,
    NCFixerDealStatus Status,
    long QuotedAmount,
    NCAccountId EscrowAccountId,
    NCTransactionId? FundingTransactionId,
    NCTransactionId? SettlementTransactionId,
    NCMarketRequestId? MarketRequestId,
    NCMarketRequestId? ServiceRequestId,
    NCShipmentId? ShipmentId,
    NCFixerDispute? Dispute,
    NCActiveWorldTime? QuoteExpiresAt,
    NCActiveWorldTime? ReservationExpiresAt,
    NCActiveWorldTime CreatedAt,
    NCActiveWorldTime UpdatedAt,
    int DataVersion,
    Guid LastAppliedEventId);

public sealed record NCNightMarketVendor(
    NCPartyRef Vendor,
    NCStockNodeId StockNodeId,
    NCFixerNetworkId NetworkId,
    bool Confirmed,
    NCActiveWorldTime? ArrivedAt,
    NCActiveWorldTime? DepartedAt);

public sealed record NCNightMarketSnapshot(
    NCNightMarketId NightMarketId,
    string TemplateId,
    NCZoneId VenueZone,
    NCPartyRef Organizer,
    NCPartyRef SecurityProvider,
    IReadOnlyList<NCNightMarketVendor> Vendors,
    NCNightMarketStatus Status,
    NCActiveWorldTime AssemblyAt,
    NCActiveWorldTime OpensAt,
    NCActiveWorldTime ClosesAt,
    Guid? OpeningWorldEventId,
    Guid? ClosingWorldEventId,
    IReadOnlyList<Guid> IncidentWorldEventIds,
    NCActiveWorldTime CreatedAt,
    NCActiveWorldTime UpdatedAt,
    int DataVersion,
    Guid LastAppliedEventId);
