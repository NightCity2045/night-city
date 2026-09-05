// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Database;
using Content.Server._NC.LivingCity.Actors.Domains;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
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

/// <summary>Creates one persistent physical GPT resident without population, housing, job or faction records.</summary>
public sealed record NCCityResidentCreateRequest(
    Guid CommandId,
    NCActorId ActorId,
    string FullName,
    ProtoId<NCCityActorArchetypePrototype> Archetype,
    ProtoId<NCNeuralPersonaProfilePrototype> Persona,
    string HumanoidProfilePayload,
    NCZoneId CurrentZone,
    NCCityActorPhysicalRecoveryState? PhysicalRecovery,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "system",
    NCGeneratedPersonaDetails? GeneratedPersona = null);

/// <summary>Persists a real physical route intent or its observed completion.</summary>
public sealed record NCCityActorLocationMutationRequest(
    Guid CommandId,
    NCActorId ActorId,
    long ExpectedVersion,
    NCZoneId CurrentZone,
    NCZoneId DestinationZone,
    NCCityActorTravelPhase TravelPhase,
    NCCityTravelMode TravelMode,
    NCActiveWorldTime? ExpectedArrivalAt,
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

/// <summary>Atomic checkpoint of one still-existing physical NPC and its durable body/inventory domains.</summary>
public sealed record NCPhysicalActorCheckpointRequest(
    Guid CommandId,
    NCActorId ActorId,
    long ExpectedActorVersion,
    NCZoneId CurrentZone,
    NCCityActorDomainCapture Domains,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "system",
    IReadOnlyList<NCPersistentAggregateWrite>? ParticipantWrites = null,
    NCCityActorPhysicalRecoveryState? PhysicalRecovery = null);

public sealed record NCPhysicalActorCheckpointResult(
    NCCityActorMutationStatus Status,
    NCActorId ActorId,
    NCCityActorRecord? Actor,
    NCCityActorHealthRecord? Health,
    NCCityActorInventoryRecord? Inventory,
    string? Error);

/// <summary>Main-thread notification after the durable root of one physical actor changes.</summary>
public sealed class NCCityActorPersistedEvent(
    NCActorId actorId,
    long aggregateVersion,
    NCSessionEpoch sessionEpoch,
    NCCityActorStatus status,
    bool frozen,
    NCCityActorTravelPhase travelPhase,
    NCActiveWorldTime? expectedArrivalAt,
    NCZoneId currentZone,
    NCZoneId destinationZone,
    string archetypeId,
    bool isNarrativeActor) : EntityEventArgs
{
    public NCActorId ActorId { get; } = actorId;
    public long AggregateVersion { get; } = aggregateVersion;
    public NCSessionEpoch SessionEpoch { get; } = sessionEpoch;
    public NCCityActorStatus Status { get; } = status;
    public bool Frozen { get; } = frozen;
    public NCCityActorTravelPhase TravelPhase { get; } = travelPhase;
    public NCActiveWorldTime? ExpectedArrivalAt { get; } = expectedArrivalAt;
    public NCZoneId CurrentZone { get; } = currentZone;
    public NCZoneId DestinationZone { get; } = destinationZone;
    public string ArchetypeId { get; } = archetypeId;
    public bool IsNarrativeActor { get; } = isNarrativeActor;
}

/// <summary>Round-local notification that a persistent actor gained or lost its physical ECS entity.</summary>
public sealed class NCCityActorBindingChangedEvent(
    NCActorId actorId,
    EntityUid entityUid,
    bool bound) : EntityEventArgs
{
    public NCActorId ActorId { get; } = actorId;
    public EntityUid EntityUid { get; } = entityUid;
    public bool Bound { get; } = bound;
}
