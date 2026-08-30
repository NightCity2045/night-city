// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Market;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Market;

public sealed record NCBusinessRecord(
    NCBusinessSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCCreateBusinessRequest(
    Guid CommandId,
    NCBusinessId BusinessId,
    string StableKey,
    string DisplayName,
    NCBusinessKind Kind,
    NCZoneId OperatingZone,
    NCPartyRef Owner,
    IReadOnlyList<NCPartyRef> Operators,
    NCBusinessOperationPolicy Policy,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "business-create");

public sealed record NCUpdateBusinessPolicyRequest(
    Guid CommandId,
    NCBusinessId BusinessId,
    long ExpectedVersion,
    NCBusinessOperationPolicy Policy,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "business-policy-update");

public sealed record NCChangeBusinessStatusRequest(
    Guid CommandId,
    NCBusinessId BusinessId,
    long ExpectedVersion,
    NCBusinessStatus Status,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "business-status-change");

public sealed record NCBusinessMutationResult(
    NCMarketMutationStatus Status,
    NCBusinessRecord? Business,
    string? Error);

public sealed record NCBusinessPage(
    IReadOnlyList<NCBusinessRecord> Businesses,
    string? NextAfterAggregateId,
    string? Error);
