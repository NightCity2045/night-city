// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Actors;

[Serializable, NetSerializable]
public enum NCCityActorStatus : byte
{
    Active,
    Working,
    Traveling,
    Resting,
    Hiding,
    Injured,
    Hospitalized,
    Arrested,
    Imprisoned,
    Unconscious,
    Missing,
    Dead,
    Archived,
}

[Serializable, NetSerializable]
public enum NCCityActorCognitionTier : byte
{
    Scripted,
    Utility,
    Planner,
    NeuralEligible,
}

[Serializable, NetSerializable]
public enum NCCityActorTravelPhase : byte
{
    Stationary,
    Planned,
    InTransit,
    Blocked,
    Arrived,
}

[Serializable, NetSerializable]
public enum NCCityActorAvailability : byte
{
    Available,
    Busy,
    OnCall,
    Unavailable,
}

/// <summary>
/// Distinguishes an actor's semantic activity from whether that activity has a physical place on the loaded map.
/// AbstractHome deliberately has no coordinates and therefore must never create an ECS entity.
/// </summary>
[Serializable, NetSerializable]
public enum NCCityActorPresenceMode : byte
{
    // Reserved migration value for snapshots written before explicit presence existed.
    Unknown,
    Public,
    AbstractHome,
    PhysicalHome,
}

[Serializable, NetSerializable]
public enum NCCityRoutineTarget : byte
{
    Stay,
    Home,
    Work,
    ExplicitZone,
}

/// <summary>
/// Stable identity for an appointment or other actor-specific schedule override.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCCityCommitmentId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public static NCCityCommitmentId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// Bounded actor-specific schedule override. Completed and cancelled entries leave the root snapshot;
/// their immutable history remains in the event journal.
/// </summary>
public sealed record NCCityActorCommitment(
    NCCityCommitmentId CommitmentId,
    string Activity,
    string Goal,
    NCZoneId TargetZone,
    NCActiveWorldTime StartsAt,
    NCActiveWorldTime EndsAt,
    NCCityActorAvailability Availability,
    int Priority);

/// <summary>
/// Stable presentation identity. Appearance and biography are reproducible from seeds, not runtime EntityUid.
/// </summary>
public sealed record NCCityActorIdentity(
    NCActorId ActorId,
    string FullName,
    string[] Aliases,
    string ArchetypeId,
    int AppearanceSeed,
    int BiographySeed,
    int DataVersion)
{
    // Optional so version-one resident snapshots remain readable. Only explicitly authored people receive it.
    public string? NarrativeActorProfileId { get; init; }
}

/// <summary>
/// Semantic position and travel intent. It never persists room IDs, portal IDs, map IDs or EntityUid values.
/// </summary>
public sealed record NCCityActorLocation(
    NCZoneId CurrentZone,
    NCZoneId DestinationZone,
    NCZoneId HomeZone,
    NCZoneId WorkZone,
    NCCityActorTravelPhase TravelPhase,
    NCCityTravelMode TravelMode,
    NCActiveWorldTime? ExpectedArrivalAt,
    NCActiveWorldTime LastKnownLocationAt)
{
    // Optional so snapshots written before canonical abstract residences remain readable and migratable.
    public NCCityAbstractResidence? Residence { get; init; }
}

/// <summary>
/// Scheduler-facing durable intent summary. Blackboard and local physical plan nodes remain runtime-only.
/// </summary>
public sealed record NCCityActorScheduleSummary(
    string RoutineProfileId,
    NCCityActorCommitment[] Commitments,
    string CurrentActivity,
    string CurrentGoal,
    NCCityActorAvailability Availability,
    NCActiveWorldTime NextUpdateAt)
{
    // An init-only trailing member keeps version-one JSON snapshots readable; absent data defaults to Public.
    public NCCityActorPresenceMode PresenceMode { get; init; } = NCCityActorPresenceMode.Unknown;
}

/// <summary>
/// Lifecycle invariants shared by every actor subdomain. Dead is irreversible through normal domain commands.
/// </summary>
public sealed record NCCityActorLifecycle(
    NCCityActorStatus Status,
    bool Frozen,
    NCActiveWorldTime? DiedAt,
    NCActiveWorldTime? ArchivedAt);

/// <summary>
/// Actor root snapshot. Health, finances, inventory, social state and cognition keep their own authoritative
/// aggregates; this root stores only identity, semantic presence and scheduler/lifecycle coordination.
/// </summary>
public sealed record NCCityActorSnapshot(
    NCCityActorIdentity Identity,
    NCCityActorLocation Location,
    NCCityActorScheduleSummary Schedule,
    NCCityActorLifecycle Lifecycle,
    NCCityActorCognitionTier CognitionTier,
    Guid LastAppliedEventId);
