// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.City.Connectivity;
using Content.Server._NC.LivingCity.Groups.Components;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Groups;

/// <summary>
/// Owns one immutable semantic trunk per complete runtime formation. Members still execute every local segment through
/// their own vanilla steering component, so a shared route never becomes shared movement state.
/// </summary>
public sealed partial class NCCityGroupTrunkRouteSystem : EntitySystem
{
    [Dependency] private NCCityGroupRuntimeFormationSystem _formations = default!;

    private readonly Dictionary<NCGroupId, NCCityGroupTrunkRoute> _routes = [];
    private long _generation;
    private long _published;
    private long _hits;
    private long _invalidations;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(_ => Reset());
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(OnSessionClosed);
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(OnClockFailed);
    }

    public bool TryCopyForMember(
        EntityUid actor,
        NCZoneId destinationZone,
        NCCityTravelMode travelMode,
        NCCityNavigationPlanVersion requiredVersion,
        List<NCCityNavigationWaypoint> result)
    {
        result.Clear();
        if (!TryGetValidFormation(actor, out var assignment, out var formation) ||
            !_routes.TryGetValue(assignment.GroupId, out var route))
        {
            return false;
        }

        if (route.SourceGroupVersion != formation.SourceGroupVersion ||
            route.DestinationZone != destinationZone ||
            route.TravelMode != travelMode ||
            route.PlanVersion != requiredVersion)
        {
            _routes.Remove(assignment.GroupId);
            return false;
        }

        foreach (var waypoint in route.Waypoints)
            result.Add(ApplyFormationOffset(waypoint, assignment.DesiredOffset));
        _hits++;
        return result.Count > 0;
    }

    public bool PublishAndCopyForMember(
        EntityUid actor,
        NCZoneId destinationZone,
        NCCityTravelMode travelMode,
        NCCityNavigationPlanVersion version,
        IReadOnlyList<NCCityNavigationWaypoint> source,
        List<NCCityNavigationWaypoint> result)
    {
        if (source.Count == 0)
            return false;
        if (!TryGetValidFormation(actor, out var assignment, out var formation))
        {
            // Solo actors retain the freshly planned route; this helper is intentionally transparent outside groups.
            result.Clear();
            foreach (var waypoint in source)
                result.Add(waypoint);
            return true;
        }

        var canonical = new NCCityNavigationWaypoint[source.Count];
        for (var i = 0; i < source.Count; i++)
            canonical[i] = source[i];
        var generation = ++_generation;
        _routes[assignment.GroupId] = new NCCityGroupTrunkRoute(
            assignment.GroupId,
            formation.SourceGroupVersion,
            destinationZone,
            travelMode,
            version,
            canonical,
            generation);
        _published++;

        result.Clear();
        foreach (var waypoint in canonical)
            result.Add(ApplyFormationOffset(waypoint, assignment.DesiredOffset));
        return true;
    }

    public bool InvalidateForActor(EntityUid actor, string reason)
    {
        if (!TryComp<NCCityGroupFormationComponent>(actor, out var assignment) ||
            !assignment.GroupId.IsValid || !_routes.Remove(assignment.GroupId))
        {
            return false;
        }

        _invalidations++;
        var generation = ++_generation;
        RaiseLocalEvent(new NCCityGroupTrunkRouteInvalidatedEvent(
            assignment.GroupId,
            actor,
            generation,
            reason));
        return true;
    }

    public NCCityGroupTrunkRouteCounters GetCounters() => new(
        _routes.Count,
        _published,
        _hits,
        _invalidations);

    public bool TryGetSnapshot(NCGroupId groupId, out NCCityGroupTrunkRouteSnapshot snapshot)
    {
        snapshot = default;
        if (!_routes.TryGetValue(groupId, out var route))
            return false;
        snapshot = new NCCityGroupTrunkRouteSnapshot(
            route.GroupId,
            route.SourceGroupVersion,
            route.DestinationZone,
            route.TravelMode,
            route.PlanVersion,
            route.Waypoints.Length,
            route.Generation);
        return true;
    }

    private bool TryGetValidFormation(
        EntityUid actor,
        out NCCityGroupFormationComponent assignment,
        out NCCityRuntimeFormationState formation)
    {
        assignment = default!;
        formation = default!;
        if (!TryComp<NCCityGroupFormationComponent>(actor, out var foundAssignment) ||
            !foundAssignment.GroupId.IsValid ||
            !_formations.TryGetGroup(foundAssignment.GroupId, out var foundFormation) ||
            foundFormation.SourceGroupVersion != foundAssignment.SourceGroupVersion)
        {
            return false;
        }

        var containsActor = false;
        foreach (var member in foundFormation.Members)
        {
            if (member.EntityUid != actor)
                continue;
            containsActor = true;
            break;
        }
        if (!containsActor)
            return false;
        assignment = foundAssignment;
        formation = foundFormation;
        return true;
    }

    internal static NCCityNavigationWaypoint ApplyFormationOffset(
        NCCityNavigationWaypoint waypoint,
        System.Numerics.Vector2 offset)
    {
        // Portals require their exact mapper/controller targets. Spacing is applied only after the shared trunk ends.
        if (waypoint.Kind != NCCityNavigationWaypointKind.Destination || offset == System.Numerics.Vector2.Zero)
            return waypoint;

        var target = new NCMapCoordinates(
            waypoint.Target.NetworkId,
            waypoint.Target.Position + offset,
            waypoint.Target.Z);
        return waypoint with { Target = target };
    }

    private void OnSessionClosed(NCActiveWorldSessionClosedEvent args) => Reset();

    private void OnClockFailed(NCActiveWorldClockFailedEvent args) => Reset();

    private void Reset()
    {
        _routes.Clear();
        _generation = 0;
        _published = 0;
        _hits = 0;
        _invalidations = 0;
    }
}
