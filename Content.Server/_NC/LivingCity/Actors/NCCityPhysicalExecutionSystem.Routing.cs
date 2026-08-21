// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Server._NC.City.Connectivity;
using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Groups;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.CombatMode;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Actors;

public sealed partial class NCCityPhysicalExecutionSystem
{
    [Dependency] private NCCityNavigationPlannerSystem _navigation = default!;
    [Dependency] private NCCitySpawnCandidateSystem _spawnCandidates = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NPCSteeringSystem _steering = default!;
    [Dependency] private HTNSystem _htn = default!;
    [Dependency] private NCCityGroupTrunkRouteSystem _groupRoutes = default!;
    [Dependency] private NCCityPhysicalStateTreeSystem _stateTree = default!;
    [Dependency] private NCCityPhysicalBehaviorAdapterSystem _behaviorAdapters = default!;
    [Dependency] private NCCityLocalPerceptionSystem _perception = default!;
    [Dependency] private NCCityPhysicalActionResolutionSystem _actionResolution = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private readonly HashSet<NCCityPhysicalCondition> _physicalConditions = [];
    private readonly List<NCCitySpawnCandidateInfo> _fallbackCandidates = [];

    private void ApplyIntent(
        EntityUid entityUid,
        NCCityPhysicalExecutionComponent component,
        NCCityActorRecord actor,
        NCActiveWorldTime now)
    {
        var snapshot = actor.Snapshot;
        var location = snapshot.Location;
        component.ActorVersion = actor.AggregateVersion;
        component.CurrentZone = location.CurrentZone;
        component.Activity = snapshot.Schedule.CurrentActivity;
        component.Goal = snapshot.Schedule.CurrentGoal;
        component.Availability = snapshot.Schedule.Availability;
        component.NextScheduleUpdateAt = snapshot.Schedule.NextUpdateAt;

        if (!_prototypes.TryIndex(
                new ProtoId<NCCityActorArchetypePrototype>(snapshot.Identity.ArchetypeId),
                out NCCityActorArchetypePrototype? archetype) || archetype == null ||
            !_prototypes.TryIndex(
                archetype.PhysicalNavigationProfile,
                out NCCityPhysicalNavigationProfilePrototype? navigationProfile) || navigationProfile == null ||
            !_prototypes.HasIndex(archetype.PhysicalStateTree) ||
            !_prototypes.HasIndex(archetype.LocalPerceptionProfile))
        {
            FailGoal(entityUid, component, now, "actor-physical-ai-profile-missing");
            return;
        }

        component.NavigationProfile = archetype.PhysicalNavigationProfile;
        component.SpawnCandidateProfile = archetype.SpawnCandidateProfile;
        component.StateTreeProfile = archetype.PhysicalStateTree;
        component.PerceptionProfile = archetype.LocalPerceptionProfile;
        component.InteractWithDoors = navigationProfile.InteractWithDoors;
        component.CanClimb = navigationProfile.CanClimb;
        component.CanPry = navigationProfile.CanPry;
        component.CanSmash = navigationProfile.CanSmash;
        if (!_perception.Configure(entityUid, component.PerceptionProfile))
        {
            FailGoal(entityUid, component, now, "actor-local-perception-profile-rejected");
            return;
        }

        if (snapshot.Lifecycle.Frozen ||
            snapshot.Lifecycle.Status is NCCityActorStatus.Dead or NCCityActorStatus.Archived ||
            !location.DestinationZone.IsValid ||
            location.TravelPhase is not (NCCityActorTravelPhase.Planned or
                NCCityActorTravelPhase.InTransit or NCCityActorTravelPhase.Blocked))
        {
            SetIdle(entityUid, component, now, "no-physical-travel-intent");
            return;
        }

        if (component.GoalId != Guid.Empty &&
            component.DestinationZone == location.DestinationZone &&
            component.TravelMode == location.TravelMode &&
            component.State is not (NCCityPhysicalExecutionState.Failed or
                NCCityPhysicalExecutionState.Idle or
                NCCityPhysicalExecutionState.LoadingIntent))
        {
            // The scheduler may update activity metadata while the physical route remains the same.
            AddTrace(component, now, "intent-refreshed", $"actor-version={actor.AggregateVersion}");
            return;
        }

        var seed = StableSelectionSeed(snapshot.Identity.ActorId.Value, location.DestinationZone.Value, actor.AggregateVersion);
        if (!_spawnCandidates.TrySelectCandidate(
                archetype.SpawnCandidateProfile,
                location.DestinationZone,
                seed,
                out var destination) ||
            (destination.TravelMode & location.TravelMode) != location.TravelMode)
        {
            FailGoal(entityUid, component, now, "destination-zone-has-no-compatible-navigation-target");
            return;
        }

        StopSteering(entityUid, component);
        component.GoalId = Guid.NewGuid();
        component.DestinationZone = location.DestinationZone;
        component.Destination = destination.Target;
        component.TravelMode = location.TravelMode;
        component.Waypoints.Clear();
        component.WaypointIndex = 0;
        component.ReplanAttempts = 0;
        component.RejectedRoomPortals.Clear();
        component.RejectedCityPortals.Clear();
        component.PlanVersion = default;
        component.LastFailure = null;
        component.State = NCCityPhysicalExecutionState.Planning;
        component.Trace.Clear();
        component.NextTraceSequence = 0;
        SuppressProceduralAI(entityUid, component);
        AddTrace(component, now, "intent-accepted", $"destination-zone={component.DestinationZone}");
        ScheduleExecution(entityUid, now);
    }

    private void ProcessExecution(EntityUid entityUid, NCActiveWorldTime now)
    {
        if (_policy == null || !Exists(entityUid) ||
            !TryComp<NCCityPhysicalExecutionComponent>(entityUid, out var component) ||
            !_runtime.TryGetActor(entityUid, out var actorId) || actorId != component.ActorId)
        {
            CleanupEntity(entityUid, true);
            return;
        }

        _behaviorAdapters.BuildConditions(entityUid, component, _physicalConditions);
        if (!_stateTree.TryResolve(
                component.StateTreeProfile,
                _physicalConditions,
                out var physicalRule,
                out var blockedFallback))
        {
            StopSteering(entityUid, component);
            ReleaseProceduralAI(entityUid, component);
            component.State = NCCityPhysicalExecutionState.Failed;
            component.LastFailure = "physical-state-tree-invalid-or-unmatched";
            AddTrace(component, now, "state-tree-failed", component.LastFailure);
            _failedGoals++;
            return;
        }
        component.BlockedFallback = blockedFallback;
        if (component.Mode != physicalRule.Mode || component.BehaviorOwner != physicalRule.Owner)
        {
            component.Mode = physicalRule.Mode;
            component.BehaviorOwner = physicalRule.Owner;
            _stateTree.RecordTransition();
            AddTrace(component, now, "physical-mode-transition",
                $"mode={component.Mode}; owner={component.BehaviorOwner}");
        }

        if (physicalRule.Owner != NCCityPhysicalBehaviorOwner.RouteExecutor)
        {
            StopSteering(entityUid, component);
            if (physicalRule.Owner == NCCityPhysicalBehaviorOwner.ExistingNpc)
                ReleaseProceduralAI(entityUid, component);
            else
                SuppressProceduralAI(entityUid, component);
            component.State = physicalRule.Mode == NCCityPhysicalMode.SafeIdle
                ? NCCityPhysicalExecutionState.Idle
                : NCCityPhysicalExecutionState.Suspended;
            ScheduleExecution(
                entityUid,
                now.Advance(_loadGovernor.ScaleEvaluationInterval(physicalRule.EvaluationInterval)));
            return;
        }

        SuppressProceduralAI(entityUid, component);
        if (component.State == NCCityPhysicalExecutionState.Suspended)
        {
            component.State = NCCityPhysicalExecutionState.Replanning;
            component.ReplanAttempts = 0;
            AddTrace(component, now, "execution-resumed", "route-executor-reacquired");
        }

        if (component.PlanVersion != default &&
            (component.PlanVersion.CityGraphVersion != _city.GraphVersion ||
             component.PlanVersion.RoomGraphVersion != _rooms.GraphVersion) &&
            component.State is NCCityPhysicalExecutionState.Moving or NCCityPhysicalExecutionState.TraversingPortal)
        {
            // Structural changes may reopen an earlier route, so goal-local exclusions are no longer authoritative.
            ClearRejectedPortals(component);
            RequestReplan(entityUid, component, now, "navigation-plan-version-invalidated");
            return;
        }

        switch (component.State)
        {
            case NCCityPhysicalExecutionState.Planning:
            case NCCityPhysicalExecutionState.Replanning:
                BuildPlan(entityUid, component, now);
                break;
            case NCCityPhysicalExecutionState.Moving:
                ProcessMovement(entityUid, component, now);
                break;
            case NCCityPhysicalExecutionState.TraversingPortal:
                ProcessPortalTraversal(entityUid, component, now);
                break;
            case NCCityPhysicalExecutionState.Committing:
                TryDispatchPendingMutation(entityUid, component, now);
                break;
            case NCCityPhysicalExecutionState.Blocked:
                component.ReplanAttempts = 0;
                // A blocked retry is a new observation window: doors or deployment links may have changed meanwhile.
                ClearRejectedPortals(component);
                component.State = NCCityPhysicalExecutionState.Replanning;
                SuppressProceduralAI(entityUid, component);
                AddTrace(component, now, "blocked-retry", "retry-window-reached");
                BuildPlan(entityUid, component, now);
                break;
        }
    }

    private void BuildPlan(EntityUid entityUid, NCCityPhysicalExecutionComponent component, NCActiveWorldTime now)
    {
        if (_policy == null)
            return;
        StopSteering(entityUid, component);
        component.Waypoints.Clear();
        component.WaypointIndex = 0;
        var currentVersion = new NCCityNavigationPlanVersion(_city.GraphVersion, _rooms.GraphVersion);
        var hasPlan = _groupRoutes.TryCopyForMember(
                entityUid,
                component.DestinationZone,
                component.TravelMode,
                currentVersion,
                component.Waypoints);
        if (!hasPlan && _navigation.TryPlan(
                entityUid,
                component.Destination,
                component.TravelMode,
                component.Waypoints,
                out var version,
                portal => !component.RejectedCityPortals.Contains(portal.Id),
                portal => !component.RejectedRoomPortals.Contains(portal.Id)))
        {
            // Reject an oversized route before it can enter the shared group cache.
            if (component.Waypoints.Count <= _policy.MaximumWaypoints)
            {
                var freshlyPlanned = new NCCityNavigationWaypoint[component.Waypoints.Count];
                component.Waypoints.CopyTo(freshlyPlanned);
                hasPlan = _groupRoutes.PublishAndCopyForMember(
                    entityUid,
                    component.DestinationZone,
                    component.TravelMode,
                    version,
                    freshlyPlanned,
                    component.Waypoints);
            }
        }
        if (!hasPlan || component.Waypoints.Count == 0 || component.Waypoints.Count > _policy.MaximumWaypoints)
        {
            RequestReplan(entityUid, component, now, "hierarchical-route-unavailable-or-too-large");
            return;
        }

        component.PlanVersion = currentVersion;
        component.State = NCCityPhysicalExecutionState.Moving;
        component.LastFailure = null;
        _plansBuilt++;
        AddTrace(component, now, "plan-built", $"waypoints={component.Waypoints.Count}");
        BeginCurrentWaypoint(entityUid, component, now);
    }

    private void BeginCurrentWaypoint(
        EntityUid entityUid,
        NCCityPhysicalExecutionComponent component,
        NCActiveWorldTime now)
    {
        if (_policy == null)
            return;

        // Adjacent semantic entry/exit records can resolve to the same coordinate. Consume only a small bounded run.
        var immediatelyCompleted = 0;
        while (component.WaypointIndex < component.Waypoints.Count &&
               immediatelyCompleted < _policy.MaximumImmediateWaypointCompletions)
        {
            var waypoint = component.Waypoints[component.WaypointIndex];
            if (IsVerticalTransition(waypoint))
            {
                BeginPortalTraversal(entityUid, component, waypoint, now);
                return;
            }
            if (IsReached(entityUid, waypoint.Target, _policy.WaypointReachRange))
            {
                CompleteWaypoint(component, now, "already-in-range");
                immediatelyCompleted++;
                continue;
            }
            if (!TryResolveEntityCoordinates(waypoint.Target, out var target))
            {
                RequestReplan(entityUid, component, now, "waypoint-coordinate-unavailable", true);
                return;
            }

            var steering = _steering.Register(entityUid, target);
            steering.Range = _policy.WaypointReachRange;
            steering.ArriveOnLineOfSight = false;
            steering.Flags = BuildPathFlags(component);
            component.OwnsSteering = true;
            component.State = NCCityPhysicalExecutionState.Moving;
            component.StepStartedAt = now;
            AddTrace(component, now, "waypoint-started", waypoint.Target.ToString() ?? string.Empty);
            ScheduleNext(entityUid, now);
            return;
        }

        if (component.WaypointIndex >= component.Waypoints.Count)
        {
            BeginArrivalMutation(entityUid, component, now);
            return;
        }

        ScheduleExecution(entityUid, now);
    }

    private void ProcessMovement(
        EntityUid entityUid,
        NCCityPhysicalExecutionComponent component,
        NCActiveWorldTime now)
    {
        if (_policy == null)
            return;
        if (component.WaypointIndex >= component.Waypoints.Count)
        {
            BeginArrivalMutation(entityUid, component, now);
            return;
        }

        var waypoint = component.Waypoints[component.WaypointIndex];
        if (IsReached(entityUid, waypoint.Target, _policy.WaypointReachRange) ||
            TryComp<NPCSteeringComponent>(entityUid, out var steering) && steering.Status == SteeringStatus.InRange)
        {
            StopSteering(entityUid, component);
            CompleteWaypoint(component, now, "arrived");
            BeginCurrentWaypoint(entityUid, component, now);
            return;
        }

        if (!TryComp<NPCSteeringComponent>(entityUid, out steering) || steering.Status == SteeringStatus.NoPath)
        {
            RequestReplan(entityUid, component, now, "local-pathfinding-reported-no-path", true);
            return;
        }
        if (now.ElapsedSince(component.StepStartedAt) >= _policy.WaypointTimeout)
        {
            RequestReplan(entityUid, component, now, "waypoint-timeout", true);
            return;
        }

        ScheduleNext(entityUid, now);
    }

    private void BeginPortalTraversal(
        EntityUid entityUid,
        NCCityPhysicalExecutionComponent component,
        NCCityNavigationWaypoint waypoint,
        NCActiveWorldTime now)
    {
        StopSteering(entityUid, component);
        component.State = NCCityPhysicalExecutionState.TraversingPortal;
        component.StepStartedAt = now;
        component.LastPortalInteractionAt = default;
        AddTrace(component, now, "portal-traversal-started", $"city-portal={waypoint.CityPortalId}");
        TryInteractWithPortal(entityUid, component, waypoint, now);
    }

    private void ProcessPortalTraversal(
        EntityUid entityUid,
        NCCityPhysicalExecutionComponent component,
        NCActiveWorldTime now)
    {
        if (_policy == null || component.WaypointIndex >= component.Waypoints.Count)
        {
            RequestReplan(entityUid, component, now, "portal-waypoint-missing");
            return;
        }

        var waypoint = component.Waypoints[component.WaypointIndex];
        if (IsReached(entityUid, waypoint.Target, _policy.WaypointReachRange))
        {
            CompleteWaypoint(component, now, "portal-traversed");
            _portalTraversals++;
            BeginCurrentWaypoint(entityUid, component, now);
            return;
        }
        if (now.ElapsedSince(component.StepStartedAt) >= _policy.PortalTraversalTimeout)
        {
            RequestReplan(entityUid, component, now, "portal-traversal-timeout", true);
            return;
        }
        if (component.LastPortalInteractionAt == default ||
            now.ElapsedSince(component.LastPortalInteractionAt) >= _policy.PortalInteractionRetryInterval)
        {
            TryInteractWithPortal(entityUid, component, waypoint, now);
            return;
        }

        ScheduleNext(entityUid, now);
    }

    private void TryInteractWithPortal(
        EntityUid entityUid,
        NCCityPhysicalExecutionComponent component,
        NCCityNavigationWaypoint waypoint,
        NCActiveWorldTime now)
    {
        component.LastPortalInteractionAt = now;
        if (!waypoint.Controller.IsValid() || !Exists(waypoint.Controller))
        {
            // A removed link cannot recover through interaction retries; immediately search for an alternate portal.
            RequestReplan(entityUid, component, now, "portal-controller-unavailable", true);
            return;
        }
        var result = _actionResolution.ResolveActivate(entityUid, waypoint.Controller, true);
        if (result.Outcome != NCCityPhysicalActionOutcome.Succeeded)
        {
            component.LastFailure = result.Failure ?? "portal-controller-interaction-failed";
            AddTrace(component, now, "portal-interaction-wait", component.LastFailure);
            ScheduleNext(entityUid, now);
            return;
        }

        AddTrace(component, now, "portal-controller-activated", waypoint.Controller.ToString());
        if (_policy != null && IsReached(entityUid, waypoint.Target, _policy.WaypointReachRange))
        {
            CompleteWaypoint(component, now, "portal-traversed");
            _portalTraversals++;
            BeginCurrentWaypoint(entityUid, component, now);
            return;
        }
        ScheduleNext(entityUid, now);
    }

    private bool IsVerticalTransition(NCCityNavigationWaypoint waypoint)
    {
        return waypoint.Kind == NCCityNavigationWaypointKind.CityPortalExit &&
               waypoint.CityPortalId.IsValid &&
               _city.TryGetPortal(waypoint.CityPortalId, out var portal) &&
               portal.Kind is NCCityPortalKind.Vertical or NCCityPortalKind.DeploymentEntrance;
    }

    private void CompleteWaypoint(
        NCCityPhysicalExecutionComponent component,
        NCActiveWorldTime now,
        string detail)
    {
        AddTrace(component, now, "waypoint-completed", detail);
        component.WaypointIndex++;
        _waypointsCompleted++;
    }

    private void RequestReplan(
        EntityUid entityUid,
        NCCityPhysicalExecutionComponent component,
        NCActiveWorldTime now,
        string failure,
        bool rejectCurrentPortal = false)
    {
        if (_policy == null)
            return;
        StopSteering(entityUid, component);
        component.LastFailure = TrimDiagnostic(failure);
        if (rejectCurrentPortal)
        {
            RejectCurrentPortal(component, now);
            _groupRoutes.InvalidateForActor(entityUid, component.LastFailure);
        }
        if (component.ReplanAttempts >= _policy.MaximumReplans)
        {
            ApplyConfiguredFallback(entityUid, component, now);
            return;
        }

        component.ReplanAttempts++;
        component.State = NCCityPhysicalExecutionState.Replanning;
        _replans++;
        AddTrace(component, now, "replan-requested", component.LastFailure);
        var delay = TimeSpan.FromTicks(checked(_policy.ReplanBackoff.Ticks * component.ReplanAttempts));
        ScheduleExecution(entityUid, now.Advance(delay));
    }

    private void ApplyConfiguredFallback(
        EntityUid entityUid,
        NCCityPhysicalExecutionComponent component,
        NCActiveWorldTime now)
    {
        switch (component.BlockedFallback)
        {
            case NCCityPhysicalFallback.Retreat when TryBeginRetreat(entityUid, component, now):
            case NCCityPhysicalFallback.ReturnToPublicZone when TryBeginReturnToPublicZone(entityUid, component, now):
                return;
            case NCCityPhysicalFallback.SafeIdle:
            case NCCityPhysicalFallback.AbortPlan:
                BeginAbortMutation(entityUid, component, now, component.LastFailure ?? "physical-plan-aborted");
                return;
            default:
                BeginBlockedMutation(entityUid, component, now, component.LastFailure ?? "physical-route-blocked");
                return;
        }
    }

    private bool TryBeginReturnToPublicZone(
        EntityUid entityUid,
        NCCityPhysicalExecutionComponent component,
        NCActiveWorldTime now)
    {
        if (!_coordinates.TryGetCoordinates(entityUid, out var current))
            return false;
        var context = _zones.GetLocationContext(current);
        if (!context.StreetId.IsValid || context.MostSpecific == context.StreetId.ZoneId)
            return false;
        var seed = StableSelectionSeed(component.ActorId.Value, context.StreetId.Value, component.ActorVersion);
        if (!_spawnCandidates.TrySelectCandidate(
                component.SpawnCandidateProfile,
                context.StreetId.ZoneId,
                seed,
                out var destination,
                candidate => (candidate.TravelMode & component.TravelMode) == component.TravelMode))
        {
            return false;
        }

        BeginFallbackRoute(component, now, context.StreetId.ZoneId, destination.Target, "return-to-public-zone");
        ScheduleExecution(entityUid, now);
        return true;
    }

    private bool TryBeginRetreat(
        EntityUid entityUid,
        NCCityPhysicalExecutionComponent component,
        NCActiveWorldTime now)
    {
        if (!_coordinates.TryGetCoordinates(entityUid, out var current))
            return false;
        var zoneId = _zones.GetLocationContext(current).MostSpecific;
        if (!zoneId.IsValid)
            return false;

        var threatPosition = current.Position;
        if (TryComp<NCCityLocalPerceptionComponent>(entityUid, out var perception) &&
            perception.NearestHostile.IsValid() &&
            _coordinates.TryGetCoordinates(perception.NearestHostile, out var hostile) &&
            hostile.NetworkId == current.NetworkId && hostile.Z == current.Z)
        {
            threatPosition = hostile.Position;
        }

        _spawnCandidates.GetCandidates(component.SpawnCandidateProfile, zoneId, _fallbackCandidates);
        NCCitySpawnCandidateInfo? selected = null;
        var farthestDistance = float.MinValue;
        foreach (var candidate in _fallbackCandidates)
        {
            if ((candidate.TravelMode & component.TravelMode) != component.TravelMode ||
                candidate.Target.NetworkId != current.NetworkId || candidate.Target.Z != current.Z)
            {
                continue;
            }
            var distance = Vector2.DistanceSquared(candidate.Target.Position, threatPosition);
            if (distance <= farthestDistance)
                continue;
            selected = candidate;
            farthestDistance = distance;
        }
        if (selected is not { } retreat)
            return false;

        BeginFallbackRoute(component, now, zoneId, retreat.Target, "retreat");
        ScheduleExecution(entityUid, now);
        return true;
    }

    private void BeginFallbackRoute(
        NCCityPhysicalExecutionComponent component,
        NCActiveWorldTime now,
        Content.Shared._NC.City.Zones.NCZoneId destinationZone,
        NCMapCoordinates destination,
        string reason)
    {
        component.GoalId = Guid.NewGuid();
        component.DestinationZone = destinationZone;
        component.Destination = destination;
        component.Waypoints.Clear();
        component.WaypointIndex = 0;
        component.ReplanAttempts = 0;
        ClearRejectedPortals(component);
        component.PlanVersion = default;
        component.State = NCCityPhysicalExecutionState.Replanning;
        AddTrace(component, now, "fallback-route-started", reason);
    }

    /// <summary>
    /// Excludes a failed semantic edge for the rest of this bounded replan sequence, allowing alternate doors or
    /// floor links to win the next hierarchical route without modifying vanilla tile pathfinding.
    /// </summary>
    private void RejectCurrentPortal(NCCityPhysicalExecutionComponent component, NCActiveWorldTime now)
    {
        if (component.WaypointIndex < 0 || component.WaypointIndex >= component.Waypoints.Count)
            return;

        var waypoint = component.Waypoints[component.WaypointIndex];
        var rejected = waypoint.RoomPortalId.IsValid && component.RejectedRoomPortals.Add(waypoint.RoomPortalId);
        rejected |= waypoint.CityPortalId.IsValid && component.RejectedCityPortals.Add(waypoint.CityPortalId);
        if (rejected)
        {
            AddTrace(component, now, "portal-rejected",
                $"room={waypoint.RoomPortalId}; city={waypoint.CityPortalId}");
        }
    }

    private static void ClearRejectedPortals(NCCityPhysicalExecutionComponent component)
    {
        component.RejectedRoomPortals.Clear();
        component.RejectedCityPortals.Clear();
    }

    private void FailGoal(
        EntityUid entityUid,
        NCCityPhysicalExecutionComponent component,
        NCActiveWorldTime now,
        string failure)
    {
        StopSteering(entityUid, component);
        ReleaseProceduralAI(entityUid, component);
        component.State = NCCityPhysicalExecutionState.Failed;
        component.LastFailure = TrimDiagnostic(failure);
        AddTrace(component, now, "goal-failed", component.LastFailure);
        _failedGoals++;
    }

    private void SetIdle(
        EntityUid entityUid,
        NCCityPhysicalExecutionComponent component,
        NCActiveWorldTime now,
        string reason)
    {
        StopSteering(entityUid, component);
        ReleaseProceduralAI(entityUid, component);
        component.GoalId = Guid.Empty;
        component.DestinationZone = default;
        component.Destination = default;
        component.Waypoints.Clear();
        component.WaypointIndex = 0;
        component.ReplanAttempts = 0;
        ClearRejectedPortals(component);
        component.PlanVersion = default;
        component.LastFailure = null;
        component.State = NCCityPhysicalExecutionState.Idle;
        AddTrace(component, now, "safe-idle", reason);
        ScheduleExecution(entityUid, now);
    }

    private void SuppressProceduralAI(EntityUid entityUid, NCCityPhysicalExecutionComponent component)
    {
        if (component.HtnSuppressed || !TryComp<HTNComponent>(entityUid, out var htn))
            return;
        component.RestoreHtnEnabled = htn.Enabled;
        component.HtnSuppressed = true;
        if (htn.Enabled)
            _htn.SetHTNEnabled((entityUid, htn), false);
    }

    private void ReleaseProceduralAI(EntityUid entityUid, NCCityPhysicalExecutionComponent component)
    {
        if (!component.HtnSuppressed)
            return;
        component.HtnSuppressed = false;
        if (component.RestoreHtnEnabled && TryComp<HTNComponent>(entityUid, out var htn) && _policy != null)
            _htn.SetHTNEnabled((entityUid, htn), true, (float) _policy.HtnResumeCooldown.TotalSeconds);
        component.RestoreHtnEnabled = false;
    }

    private void StopSteering(EntityUid entityUid, NCCityPhysicalExecutionComponent component)
    {
        if (!component.OwnsSteering)
            return;
        if (Exists(entityUid))
            _steering.Unregister(entityUid);
        component.OwnsSteering = false;
    }

    private void CleanupEntity(EntityUid entityUid, bool removeComponent)
    {
        _scheduledExecutions.Remove(entityUid);
        _tracked.Remove(entityUid);
        _perception.Unconfigure(entityUid);
        if (TryComp<NCCityPhysicalExecutionComponent>(entityUid, out var component))
        {
            _queuedIntentLoads.Remove(component.ActorId);
            _pendingMutations.Remove(component.ActorId);
            StopSteering(entityUid, component);
            ReleaseProceduralAI(entityUid, component);
        }
        if (removeComponent && Exists(entityUid) && HasComp<NCCityPhysicalExecutionComponent>(entityUid))
            RemCompDeferred<NCCityPhysicalExecutionComponent>(entityUid);
    }

    private void AddTrace(
        NCCityPhysicalExecutionComponent component,
        NCActiveWorldTime now,
        string eventName,
        string? detail)
    {
        if (_policy == null)
            return;
        while (component.Trace.Count >= _policy.MaximumTraceEntries)
            component.Trace.RemoveAt(0);
        NCCityNavigationWaypointKind? kind = null;
        if (component.WaypointIndex >= 0 && component.WaypointIndex < component.Waypoints.Count)
            kind = component.Waypoints[component.WaypointIndex].Kind;
        component.Trace.Add(new NCCityPhysicalExecutionTraceEntry(
            ++component.NextTraceSequence,
            now,
            component.State,
            TrimDiagnostic(eventName),
            component.WaypointIndex,
            kind,
            TrimDiagnostic(detail)));
    }

    private bool IsReached(EntityUid entityUid, NCMapCoordinates target, float range)
    {
        return _coordinates.TryGetCoordinates(entityUid, out var current) &&
               current.NetworkId == target.NetworkId && current.Z == target.Z &&
               Vector2.DistanceSquared(current.Position, target.Position) <= range * range;
    }

    private bool TryResolveEntityCoordinates(NCMapCoordinates coordinates, out EntityCoordinates entityCoordinates)
    {
        entityCoordinates = default;
        if (!_coordinates.TryResolve(coordinates, out var mapCoordinates) ||
            !_maps.TryGetMap(mapCoordinates.MapId, out var mapUid) || mapUid is not { } map ||
            !_maps.TryFindGridAt(map, mapCoordinates.Position, out var gridUid, out _))
        {
            return false;
        }
        entityCoordinates = _transform.ToCoordinates((gridUid, null), mapCoordinates);
        return true;
    }

    private static PathFlags BuildPathFlags(NCCityPhysicalExecutionComponent component)
    {
        var flags = PathFlags.None;
        if (component.InteractWithDoors)
            flags |= PathFlags.Interact;
        if (component.CanClimb)
            flags |= PathFlags.Climbing;
        if (component.CanPry)
            flags |= PathFlags.Prying;
        if (component.CanSmash)
            flags |= PathFlags.Smashing;
        return flags;
    }

    internal static uint StableSelectionSeed(Guid actorId, Guid destinationZone, long actorVersion)
    {
        Span<byte> actorBytes = stackalloc byte[16];
        Span<byte> zoneBytes = stackalloc byte[16];
        actorId.TryWriteBytes(actorBytes);
        destinationZone.TryWriteBytes(zoneBytes);

        // FNV-1a consumes every identity byte. The previous folded XOR ignored the final GUID words and allowed two
        // distinct semantic destinations to select the same candidate before any random distribution occurred.
        var hash = 2166136261u;
        foreach (var value in actorBytes)
            hash = unchecked((hash ^ value) * 16777619u);
        foreach (var value in zoneBytes)
            hash = unchecked((hash ^ value) * 16777619u);
        var version = unchecked((ulong) actorVersion);
        for (var i = 0; i < sizeof(long); i++)
        {
            hash = unchecked((hash ^ (byte) version) * 16777619u);
            version >>= 8;
        }
        return hash;
    }
}
