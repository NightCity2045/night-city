// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Market;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Market;

public sealed record NCProductionJobRecord(
    NCProductionJobSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCStartProductionRequest(
    Guid CommandId,
    NCProductionJobId JobId,
    NCBusinessId BusinessId,
    long ExpectedBusinessVersion,
    ProtoId<NCProductionRecipePrototype> Recipe,
    int Runs,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "production-start");

public sealed record NCCancelProductionRequest(
    Guid CommandId,
    NCProductionJobId JobId,
    long ExpectedJobVersion,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "production-cancel");

public sealed record NCProductionMutationResult(
    NCMarketMutationStatus Status,
    NCProductionJobRecord? Job,
    NCBusinessRecord? Business,
    string? Error);
