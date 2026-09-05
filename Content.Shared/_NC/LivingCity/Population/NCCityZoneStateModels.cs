// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Population;

[Serializable, NetSerializable]
public enum NCCityStateSignalKind : byte
{
    Activity,
    Scarcity,
    Threat,
    Control,
}

[Serializable, NetSerializable]
public enum NCCityStateSignalAvailability : byte
{
    Unavailable,
    Available,
}

public sealed record NCCityPopulationStatusCount(NCCityActorStatus Status, int Count);

public sealed record NCCityPopulationCognitionCount(NCCityActorCognitionTier Tier, int Count);

public sealed record NCCityPopulationAvailabilityCount(NCCityActorAvailability Availability, int Count);

public sealed record NCCityPopulationArchetypeCount(string ArchetypeId, int Count);

/// <summary>
/// Bounded population projection for one authored district or sector. Archived actors are deliberately excluded;
/// no ECS identity, physical coordinate or runtime navigation node crosses this persistence boundary.
/// </summary>
public sealed record NCCityPopulationAggregate(
    int TotalActors,
    int LivingActors,
    int DeadActors,
    int FrozenActors,
    int TravelingActors,
    float WeightedActivityUnits,
    NCCityPopulationStatusCount[] Statuses,
    NCCityPopulationCognitionCount[] CognitionTiers,
    NCCityPopulationAvailabilityCount[] Availability,
    NCCityPopulationArchetypeCount[] Archetypes);

/// <summary>
/// One normalized 0..1 city signal. Unavailable is distinct from an authoritative value of zero.
/// Source version lets future economy, incident and territory domains replace their own projection monotonically.
/// </summary>
public sealed record NCCityStateSignal(
    NCCityStateSignalKind Kind,
    NCCityStateSignalAvailability Availability,
    float Value,
    string Source,
    long SourceVersion);

/// <summary>
/// Persistent city-level read model rooted in an exact city_zone.yml district or sector identity.
/// </summary>
public sealed record NCCityZoneStateSnapshot(
    NCZoneId ScopeZone,
    NCZoneContextSlot ScopeSlot,
    NCCityPopulationAggregate Population,
    NCCityStateSignal[] Signals,
    NCActiveWorldTime CapturedAt,
    int DataVersion,
    Guid LastAppliedEventId);

