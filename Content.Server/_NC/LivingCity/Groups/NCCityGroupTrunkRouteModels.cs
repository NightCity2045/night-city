// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.City.Connectivity;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Groups;

public readonly record struct NCCityGroupTrunkRouteCounters(
    int CachedRoutes,
    long PublishedRoutes,
    long CacheHits,
    long Invalidations);

public readonly record struct NCCityGroupTrunkRouteSnapshot(
    NCGroupId GroupId,
    long SourceGroupVersion,
    NCZoneId DestinationZone,
    NCCityTravelMode TravelMode,
    NCCityNavigationPlanVersion PlanVersion,
    int WaypointCount,
    long Generation);

/// <summary>
/// Notifies bounded physical executors that members already following this group trunk must replan.
/// </summary>
public readonly record struct NCCityGroupTrunkRouteInvalidatedEvent(
    NCGroupId GroupId,
    EntityUid SourceActor,
    long Generation,
    string Reason);

internal sealed record NCCityGroupTrunkRoute(
    NCGroupId GroupId,
    long SourceGroupVersion,
    NCZoneId DestinationZone,
    NCCityTravelMode TravelMode,
    NCCityNavigationPlanVersion PlanVersion,
    NCCityNavigationWaypoint[] Waypoints,
    long Generation);
