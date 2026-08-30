// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Fixers;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Fixers;

public enum NCFixerMutationStatus : byte
{
    Applied,
    Duplicate,
    NotFound,
    VersionConflict,
    NotAuthorized,
    CapacityExceeded,
    ClockUnavailable,
    Rejected,
    StorageFailure,
}

public sealed record NCFixerNetworkRecord(NCFixerNetworkSnapshot Snapshot, long AggregateVersion,
    NCSessionEpoch SessionEpoch, NCActiveWorldTime PersistedAt);
public sealed record NCFixerDealRecord(NCFixerDealSnapshot Snapshot, long AggregateVersion,
    NCSessionEpoch SessionEpoch, NCActiveWorldTime PersistedAt);
public sealed record NCNightMarketRecord(NCNightMarketSnapshot Snapshot, long AggregateVersion,
    NCSessionEpoch SessionEpoch, NCActiveWorldTime PersistedAt);

public sealed record NCEnsureFixerNetworkRequest(
    Guid CommandId,
    NCPartyRef Fixer,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "fixer-network-ensure");

public sealed record NCGrantFixerAccessRequest(
    Guid CommandId,
    NCPartyRef Fixer,
    NCPartyRef Client,
    NCPartyRef Sponsor,
    int MinimumTrust,
    int MinimumReputation,
    string ReputationAudienceKey,
    IReadOnlyList<Content.Shared._NC.LivingCity.Social.NCSocialObligationId> RequiredObligations,
    NCActiveWorldTime? ExpiresAt,
    string Reason,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "fixer-access-grant");

public sealed record NCCreateFixerDealRequest(
    Guid CommandId,
    NCFixerDealId DealId,
    NCFixerNetworkId NetworkId,
    NCPartyRef Fixer,
    NCPartyRef Buyer,
    NCPartyRef Seller,
    NCFixerDealPlan Plan,
    long QuotedAmount,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "fixer-deal-create");

public sealed record NCChangeFixerDealRequest(
    Guid CommandId,
    NCFixerDealId DealId,
    long ExpectedVersion,
    NCFixerDealStatus Status,
    NCPartyRef Actor,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "fixer-deal-status",
    NCFixerBreachReason BreachReason = NCFixerBreachReason.None);

public sealed record NCAdvanceFixerDealLegRequest(
    Guid CommandId,
    NCFixerDealId DealId,
    long ExpectedVersion,
    Guid LegId,
    NCFixerDealLegStatus Status,
    Guid? ProofWorldEventId,
    NCPartyRef Actor,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "fixer-deal-leg");

public sealed record NCOpenFixerDisputeRequest(Guid CommandId, NCFixerDealId DealId, long ExpectedVersion,
    Guid DisputeId, NCPartyRef Actor, string ReasonKey, Guid CorrelationId, Guid? CausationId = null,
    string AuditSource = "fixer-dispute-open");
public sealed record NCSubmitFixerEvidenceRequest(Guid CommandId, NCFixerDealId DealId, long ExpectedVersion,
    NCFixerEvidence Evidence, NCPartyRef Actor, Guid CorrelationId, Guid? CausationId = null,
    string AuditSource = "fixer-dispute-evidence");
public sealed record NCArbitrateFixerDisputeRequest(Guid CommandId, NCFixerDealId DealId, long ExpectedVersion,
    NCFixerArbitrationOutcome Outcome, NCPartyRef Arbitrator, string ResolutionKey, Guid CorrelationId,
    Guid? CausationId = null, string AuditSource = "fixer-dispute-arbitrate");

public sealed record NCScheduleNightMarketRequest(Guid CommandId, NCNightMarketId NightMarketId, string TemplateId,
    Content.Shared._NC.City.Zones.NCZoneId VenueZone, NCPartyRef Organizer, NCPartyRef SecurityProvider,
    IReadOnlyList<NCNightMarketVendor> Vendors, NCActiveWorldTime AssemblyAt, NCActiveWorldTime OpensAt,
    NCActiveWorldTime ClosesAt, Guid CorrelationId, Guid? CausationId = null,
    string AuditSource = "night-market-schedule");

public sealed record NCFixerMutationResult(NCFixerMutationStatus Status, NCFixerNetworkRecord? Network,
    NCFixerDealRecord? Deal, NCNightMarketRecord? NightMarket, string? Error);
