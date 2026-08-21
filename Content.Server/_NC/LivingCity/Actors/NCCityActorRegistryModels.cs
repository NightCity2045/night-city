// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Server._NC.LivingCity.Actors.Domains;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Server.Database;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Actors;

public enum NCCityActorMutationStatus : byte
{
    Applied,
    Duplicate,
    NotFound,
    VersionConflict,
    ClockUnavailable,
    Rejected,
    StorageFailure,
}

public sealed record NCCityActorRecord(
    NCCityActorSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCCityActorPage(
    IReadOnlyList<NCCityActorRecord> Records,
    NCActorId? ContinuationActorId,
    bool HasMore);

public sealed record NCCityActorMutationResult(
    NCCityActorMutationStatus Status,
    NCActorId ActorId,
    long AggregateVersion,
    NCCityActorRecord? Record,
    string? Error);

/// <summary>
/// Atomic physical-to-strategic hand-off. Domain snapshots must have been captured on the simulation thread.
/// </summary>
public sealed record NCCityActorDematerializationCommitRequest(
    Guid CommandId,
    NCActorId ActorId,
    long ExpectedActorVersion,
    NCZoneId CurrentZone,
    NCCityActorDomainCapture Domains,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "system",
    IReadOnlyList<NCPersistentAggregateWrite>? ParticipantWrites = null);

public sealed record NCCityActorDematerializationCommitResult(
    NCCityActorMutationStatus Status,
    NCActorId ActorId,
    NCCityActorRecord? Actor,
    NCCityActorHealthRecord? Health,
    NCCityActorInventoryRecord? Inventory,
    string? Error);

public sealed record NCCityActorCreateRequest(
    Guid CommandId,
    Guid PopulationScopeId,
    string StableLocalId,
    string FullName,
    string[] Aliases,
    ProtoId<NCCityActorArchetypePrototype> Archetype,
    NCZoneId HomeZone,
    NCZoneId WorkZone,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "system");

public sealed record NCCityActorLocationMutationRequest(
    Guid CommandId,
    NCActorId ActorId,
    long ExpectedVersion,
    NCZoneId CurrentZone,
    NCZoneId DestinationZone,
    NCCityActorTravelPhase TravelPhase,
    NCCityTravelMode TravelMode,
    NCActiveWorldTime? ExpectedArrivalAt,
    string CurrentActivity,
    string CurrentGoal,
    NCCityActorAvailability Availability,
    NCActiveWorldTime NextUpdateAt,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "system");

public sealed record NCCityActorStatusMutationRequest(
    Guid CommandId,
    NCActorId ActorId,
    long ExpectedVersion,
    NCCityActorStatus Status,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "system");

public sealed record NCCityActorFreezeMutationRequest(
    Guid CommandId,
    NCActorId ActorId,
    long ExpectedVersion,
    bool Frozen,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "system");

public sealed record NCCityActorAddCommitmentRequest(
    Guid CommandId,
    NCActorId ActorId,
    long ExpectedVersion,
    NCCityActorCommitment Commitment,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "system");

public sealed record NCCityActorRemoveCommitmentRequest(
    Guid CommandId,
    NCActorId ActorId,
    long ExpectedVersion,
    NCCityCommitmentId CommitmentId,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "system");

/// <summary>
/// Main-thread notification published after a committed actor snapshot changes.
/// It lets bounded schedulers update their queues without scanning the registry.
/// </summary>
public sealed class NCCityActorPersistedEvent(
    NCActorId actorId,
    long aggregateVersion,
    NCSessionEpoch sessionEpoch,
    NCActiveWorldTime nextUpdateAt,
    NCCityActorStatus status,
    bool frozen,
    NCCityActorTravelPhase travelPhase,
    NCActiveWorldTime? expectedArrivalAt,
    NCZoneId currentZone,
    string archetypeId,
    NCCityActorCognitionTier cognitionTier,
    NCCityActorAvailability availability) : EntityEventArgs
{
    public NCActorId ActorId { get; } = actorId;
    public long AggregateVersion { get; } = aggregateVersion;
    public NCSessionEpoch SessionEpoch { get; } = sessionEpoch;
    public NCActiveWorldTime NextUpdateAt { get; } = nextUpdateAt;
    public NCCityActorStatus Status { get; } = status;
    public bool Frozen { get; } = frozen;
    public NCCityActorTravelPhase TravelPhase { get; } = travelPhase;
    public NCActiveWorldTime? ExpectedArrivalAt { get; } = expectedArrivalAt;
    public NCZoneId CurrentZone { get; } = currentZone;
    public string ArchetypeId { get; } = archetypeId;
    public NCCityActorCognitionTier CognitionTier { get; } = cognitionTier;
    public NCCityActorAvailability Availability { get; } = availability;
}

/// <summary>
/// Round-local notification used to hand travel ownership between abstract simulation and a physical NPC entity.
/// </summary>
public sealed class NCCityActorMaterializationChangedEvent(
    NCActorId actorId,
    EntityUid entityUid,
    bool materialized) : EntityEventArgs
{
    public NCActorId ActorId { get; } = actorId;
    public EntityUid EntityUid { get; } = entityUid;
    public bool Materialized { get; } = materialized;
}
