// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Meta.UI;

namespace Content.Server._NC.Meta.Applications;

public sealed record NCMetaHostingOffer(string HostDeviceId, string Provider, string PackageDigest, string View, string Profile, bool Active, long Revision);
public sealed record NCMetaHostingQuote(Guid Id, string Buyer, string Provider, string Domain, string HostDeviceId,
    string PackageDigest, string View, int Units, long DomainFee, long HostingFee, long TermTicks, int MaxSessions,
    long ExpiresAtTicks, bool Consumed, Guid LeaseId, long OfferRevision, long Revision)
{
    public int? PoolSlot { get; init; }
}
public enum NCMetaHostingLeaseStatus : byte { Active, Expired, Refunded, ProviderDefault, Withdrawn }
public sealed record NCMetaHostingLease(Guid Id, Guid QuoteId, string Buyer, string Provider, string Domain,
    string HostDeviceId, string PackageDigest, string View, int Units, int MaxSessions,
    long StartsAtTicks, long ExpiresAtTicks, long DomainFee, long HostingFee, Guid PaymentTransactionId,
    NCMetaHostingLeaseStatus Status, Guid? SettlementTransactionId, long Revision);
public sealed record NCMetaHostingDomain(string Domain, Guid LeaseId, string Owner, long ExpiresAtTicks, long Revision);
public sealed record NCMetaHostingCapacity(string HostDeviceId, Dictionary<Guid, int> Reservations, long Revision);
public sealed record NCMetaHostingOwnerIndex(string Owner, Guid[] Leases, long Revision);
public sealed record NCMetaHostingOutcome(bool Success, string MessageKey, NCMetaHostingQuoteSummary? Quote = null,
    NCMetaHostingLeaseSummary? Lease = null);
