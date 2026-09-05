// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Actors.Domains;

public sealed record NCCityActorHealthRecord(
    NCCityActorHealthSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCCityActorInventoryRecord(
    NCCityActorInventorySnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCCityActorDomainCaptureRequest(
    Guid CommandId,
    NCActorId ActorId,
    long ExpectedHealthVersion,
    long ExpectedInventoryVersion,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "system");

public sealed record NCCityActorDomainCaptureResult(
    NCCityActorMutationStatus Status,
    NCActorId ActorId,
    NCCityActorHealthRecord? Health,
    NCCityActorInventoryRecord? Inventory,
    string? Error);

/// <summary>
/// Immutable main-thread capture used by the three-aggregate dematerialization hand-off.
/// Expected versions belong to the snapshots that were loaded before the physical entity was inspected.
/// </summary>
public sealed record NCCityActorDomainCapture(
    NCActorId ActorId,
    long ExpectedHealthVersion,
    NCCityActorHealthSnapshot Health,
    long ExpectedInventoryVersion,
    NCCityActorInventorySnapshot Inventory,
    uint MedicalRevision,
    uint BodyFunctionRevision);
