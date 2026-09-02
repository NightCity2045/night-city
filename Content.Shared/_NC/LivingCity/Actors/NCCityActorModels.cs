// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
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
public enum NCCityActorTravelPhase : byte
{
    Stationary,
    Planned,
    InTransit,
    Blocked,
    Arrived,
}

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

    /// <summary>
    /// Stable data-driven GPT persona selected when this person is created.
    /// </summary>
    public string? NeuralPersonaProfileId { get; init; }

    /// <summary>
    /// Server-serialized humanoid profile. It preserves the exact generated body instead of rerolling on MapInit.
    /// </summary>
    public string? HumanoidProfilePayload { get; init; }
}

/// <summary>
/// Persistent physical recovery point. It contains no runtime map or entity identity.
/// </summary>
public sealed record NCCityActorPhysicalRecoveryState(
    NCZNetworkId NetworkId,
    float X,
    float Y,
    int Z,
    NCZoneId LocationContextId);

/// <summary>
/// Semantic position and travel intent. It never persists room IDs, portal IDs, map IDs or EntityUid values.
/// </summary>
public sealed record NCCityActorLocation(
    NCZoneId CurrentZone,
    NCZoneId DestinationZone,
    NCCityActorTravelPhase TravelPhase,
    NCCityTravelMode TravelMode,
    NCActiveWorldTime? ExpectedArrivalAt,
    NCActiveWorldTime LastKnownLocationAt);

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
/// aggregates; this root stores only identity, physical recovery, semantic location and lifecycle coordination.
/// </summary>
public sealed record NCCityActorSnapshot(
    NCCityActorIdentity Identity,
    NCCityActorLocation Location,
    NCCityActorLifecycle Lifecycle,
    Guid LastAppliedEventId)
{
    public NCCityActorPhysicalRecoveryState? PhysicalRecovery { get; init; }
}
