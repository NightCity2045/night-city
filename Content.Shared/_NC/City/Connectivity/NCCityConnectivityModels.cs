// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.City.Connectivity;

/// <summary>
/// Round-local identity for a discovered door, boundary or resolved stable link.
/// It must never be written to persistent aggregate state.
/// </summary>
public readonly record struct NCCityPortalId(long Value)
{
    public bool IsValid => Value > 0;
    public override string ToString() => Value.ToString();
}

/// <summary>
/// Persistent identity reserved for explicit vertical markers and deployment entrances.
/// Ordinary doors and inferred passages deliberately leave this value empty.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCStableCityLinkId(Guid ScopeId, string LocalId)
{
    public bool IsValid => ScopeId != Guid.Empty && !string.IsNullOrWhiteSpace(LocalId);
    public override string ToString() => $"{ScopeId:D}:{LocalId}";
}

[Flags]
public enum NCCityTravelMode : byte
{
    None = 0,
    Pedestrian = 1 << 0,
    Vehicle = 1 << 1,
    Patrol = 1 << 2,
    Emergency = 1 << 3,
}

public enum NCCityPortalKind : byte
{
    Door,
    OpenBoundary,
    Vertical,
    DeploymentEntrance,
}

/// <summary>
/// Input supplied by map analysis or a dynamic-link registry. Local pathfinding consumes Entry/Exit targets.
/// </summary>
public readonly record struct NCCityPortalRegistration(
    NCZoneId FromZone,
    NCZoneId ToZone,
    NCMapCoordinates EntryTarget,
    NCMapCoordinates ExitTarget,
    NCCityPortalKind Kind,
    ProtoId<NCCityTraversalProfilePrototype> TraversalProfile,
    NCStableCityLinkId StableId,
    EntityUid Controller = default);

public readonly record struct NCCityPortalInfo(
    NCCityPortalId Id,
    NCZoneId FromZone,
    NCZoneId ToZone,
    NCMapCoordinates EntryTarget,
    NCMapCoordinates ExitTarget,
    NCCityPortalKind Kind,
    ProtoId<NCCityTraversalProfilePrototype> TraversalProfile,
    NCStableCityLinkId StableId,
    NCCityTravelMode TravelModes,
    float BaseTravelSeconds,
    float CapacityPerMinute,
    float ThreatCost,
    bool Enabled,
    EntityUid Controller);

public readonly record struct NCCityRouteStep(
    NCCityPortalId PortalId,
    NCZoneId FromZone,
    NCZoneId ToZone,
    NCMapCoordinates EntryTarget,
    NCMapCoordinates ExitTarget,
    ProtoId<NCCityTraversalProfilePrototype> TraversalProfile,
    NCStableCityLinkId StableId,
    float Cost,
    EntityUid Controller);

public readonly record struct NCCityContainmentInfo(
    NCZoneId ZoneId,
    NCZoneId ParentZoneId);
