// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Housing;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Housing;

[Serializable, NetSerializable]
public readonly record struct NCPropertyListingId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

[Serializable, NetSerializable]
public readonly record struct NCPropertyQuoteId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

[Serializable, NetSerializable]
public readonly record struct NCPropertyReservationId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => Value.ToString("D");
}

[Serializable, NetSerializable]
public enum NCPropertyOfferKind : byte
{
    Sale,
    Rent,
}

[Serializable, NetSerializable]
public enum NCPropertyListingStatus : byte
{
    Open,
    Reserved,
    Fulfilled,
    Cancelled,
    Expired,
}

[Serializable, NetSerializable]
public enum NCPropertyQuoteStatus : byte
{
    Open,
    Reserved,
    Settled,
    Cancelled,
    Expired,
}

[Serializable, NetSerializable]
public enum NCPropertyReservationStatus : byte
{
    Active,
    Settled,
    Cancelled,
    Expired,
}

public sealed record NCPropertyListingSnapshot(
    NCPropertyListingId ListingId,
    string SourceListingId,
    NCApartmentId PropertyId,
    NCPropertyOfferKind OfferKind,
    NCPartyRef Seller,
    long Price,
    long SecurityDeposit,
    string RentPolicyId,
    long BillingIntervalTicks,
    string? TemplateId,
    int? TemplateRevision,
    NCPropertyPermission OccupantPermissions,
    NCPropertyListingStatus Status,
    NCActiveWorldTime? ExpiresAt,
    NCActiveWorldTime CreatedAt,
    NCActiveWorldTime UpdatedAt,
    int DataVersion,
    Guid LastAppliedEventId);

public sealed record NCPropertyQuoteSnapshot(
    NCPropertyQuoteId QuoteId,
    NCPropertyListingId ListingId,
    long ListingVersion,
    NCApartmentId PropertyId,
    long PropertyVersion,
    NCPropertyOfferKind OfferKind,
    NCPartyRef Seller,
    NCPartyRef Buyer,
    long Price,
    long SecurityDeposit,
    string RentPolicyId,
    long BillingIntervalTicks,
    string? TemplateId,
    int? TemplateRevision,
    NCPropertyPermission OccupantPermissions,
    NCPropertyQuoteStatus Status,
    NCActiveWorldTime ExpiresAt,
    NCActiveWorldTime CreatedAt,
    NCActiveWorldTime UpdatedAt,
    int DataVersion,
    Guid LastAppliedEventId);

public sealed record NCPropertyReservationSnapshot(
    NCPropertyReservationId ReservationId,
    NCPropertyQuoteId QuoteId,
    NCPropertyListingId ListingId,
    NCApartmentId PropertyId,
    NCPartyRef Seller,
    NCPartyRef Buyer,
    NCPropertyReservationStatus Status,
    NCActiveWorldTime ExpiresAt,
    NCTransactionId? SettlementTransactionId,
    NCActiveWorldTime CreatedAt,
    NCActiveWorldTime UpdatedAt,
    int DataVersion,
    Guid LastAppliedEventId);
