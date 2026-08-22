// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Server._NC.LivingCity.Population;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Admin;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Admin;

public sealed partial class NCLivingCityAdminSystem
{
    private const int MaximumObserverZones = 64;

    /// <summary>
    /// Produces one bounded read-only view from existing A-Life projections. Exact actor observation may perform one
    /// lazy aggregate read; the district view never scans persistent actors or ECS entities.
    /// </summary>
    private async void OnObserverRequest(NCLivingCityAdminObserverRequest request, EntitySessionEventArgs args)
    {
        if (request.RequestId == Guid.Empty)
            return;
        if (!CanRead(args))
        {
            SendObserver(args, request.RequestId, false, [], [], [], null, "not-authorized");
            return;
        }
        if (request.ZoneId == null || request.ActorId == null ||
            request.ZoneId.Length > MaximumTargetLength || request.ActorId.Length > MaximumTargetLength)
        {
            SendObserver(args, request.RequestId, true, [], [], [], null, "invalid-request");
            return;
        }
        if (!AcceptObserverRate(args.SenderSession.UserId))
        {
            SendObserver(args, request.RequestId, true, [], [], [], null, "rate-limited");
            return;
        }

        try
        {
            var projections = new List<NCCityZoneObserverProjection>();
            _zoneStates.GetObserverProjections(projections);
            var summary = BuildObserverSummary(projections);
            var zones = BuildObserverZones(projections, request.ZoneId.Trim(), out var zoneError);

            NCLivingCityAdminField[] actorFields = [];
            string? actorError = null;
            if (!string.IsNullOrWhiteSpace(request.ActorId))
                (actorFields, actorError) = await BuildObserverActorAsync(request.ActorId.Trim());

            SendObserver(
                args,
                request.RequestId,
                true,
                summary,
                zones,
                actorFields,
                actorError,
                zoneError);
        }
        catch (Exception exception)
        {
            Log.Error($"Living City F7 observer request failed: {exception}");
            SendObserver(args, request.RequestId, true, [], [], [], null, "observer-failure");
        }
    }

    private NCLivingCityAdminField[] BuildObserverSummary(
        IReadOnlyList<NCCityZoneObserverProjection> projections)
    {
        var fields = new FieldBuilder();
        if (_clock.TryGetCurrentTime(out _, out var now))
        {
            fields.Add("observer-round", "state", "active");
            fields.Add("observer-round", "active-world-time", now.Elapsed);
        }
        else
        {
            fields.Add("observer-round", "state", "inactive");
        }

        var districts = projections.Where(static value => value.ScopeSlot == NCZoneContextSlot.District).ToArray();
        var districtActors = districts.Sum(static value => value.TotalActors);
        var publicActors = districts.Sum(static value => value.PublicActors);
        var abstractHomeActors = districts.Sum(static value => value.AbstractHomeActors);
        var physicalHomeActors = districts.Sum(static value => value.PhysicalHomeActors);
        fields.Add("observer-presence", "indexed-actors", _zoneStates.IndexedActorCount);
        fields.Add("observer-presence", "district-actors", districtActors);
        fields.Add("observer-presence", "materialized", _actorRuntime.MaterializedActorCount);
        fields.Add("observer-presence", "public", publicActors);
        fields.Add("observer-presence", "abstract-home", abstractHomeActors);
        fields.Add("observer-presence", "physical-home", physicalHomeActors);
        fields.Add("observer-presence", "unknown-presence",
            Math.Max(0, districtActors - publicActors - abstractHomeActors - physicalHomeActors));

        fields.Add("observer-pipeline", "projection-ready", _zoneStates.InitialLoadComplete);
        fields.Add("observer-pipeline", "projected-scopes", _zoneStates.ScopeCount);
        fields.Add("observer-pipeline", "dirty-scopes", _zoneStates.PendingScopeCount);
        fields.Add("observer-pipeline", "schedule-queued", _scheduler.QueuedActorCount);
        fields.Add("observer-pipeline", "travel-queued", _travel.QueuedActorCount);
        fields.Add("observer-pipeline", "materialization-queued", _materialization.PendingRequestCount);
        fields.Add("observer-pipeline", "dematerialization-queued", _dematerialization.PendingRequestCount);
        fields.Add("observer-pipeline", "player-clusters", _relevance.ClusterCount);
        return fields.Build();
    }

    private NCLivingCityAdminObserverZone[] BuildObserverZones(
        IReadOnlyList<NCCityZoneObserverProjection> projections,
        string filter,
        out string? error)
    {
        error = null;
        IEnumerable<NCCityZoneObserverProjection> selected;
        if (filter.Length == 0)
        {
            selected = projections.Where(static value => value.ScopeSlot == NCZoneContextSlot.District);
        }
        else
        {
            if (!NCZoneId.TryParse(filter, out var zoneId))
            {
                error = "invalid-zone-id";
                return [];
            }
            selected = projections.Where(value => value.ScopeZone == zoneId);
        }

        var result = new List<NCLivingCityAdminObserverZone>();
        foreach (var projection in selected.Take(MaximumObserverZones))
        {
            if (!_zones.TryGetZone(projection.ScopeZone, out var zone))
                continue;
            result.Add(new NCLivingCityAdminObserverZone(
                projection.ScopeZone.ToString(),
                BoundObserverText(zone.Name, 128),
                projection.ScopeSlot.ToString(),
                projection.TotalActors,
                projection.LivingActors,
                projection.FrozenActors,
                projection.TravelingActors,
                projection.PublicActors,
                projection.AbstractHomeActors,
                projection.PhysicalHomeActors,
                projection.Activity,
                projection.Scarcity,
                projection.Threat,
                projection.Control));
        }

        if (filter.Length > 0 && result.Count == 0)
            error = "zone-state-not-found";
        return result.ToArray();
    }

    private async Task<(NCLivingCityAdminField[] Fields, string? Error)> BuildObserverActorAsync(string target)
    {
        if (!Guid.TryParse(target, out var raw))
            return ([], "invalid-actor-id");
        var actorId = new NCActorId(raw);
        var actor = await _actors.GetAsync(actorId);
        if (actor == null)
            return ([], "actor-not-found");

        var fields = new FieldBuilder();
        var snapshot = actor.Snapshot;
        fields.Add("observer-actor", "actor-id", actorId);
        fields.Add("observer-actor", "full-name", snapshot.Identity.FullName);
        fields.Add("observer-actor", "archetype", snapshot.Identity.ArchetypeId);
        fields.Add("observer-actor", "status", snapshot.Lifecycle.Status);
        fields.Add("observer-actor", "frozen", snapshot.Lifecycle.Frozen);
        fields.Add("observer-actor", "cognition-tier", snapshot.CognitionTier);
        fields.Add("observer-actor", "aggregate-version", actor.AggregateVersion);

        fields.Add("observer-location", "current-zone", snapshot.Location.CurrentZone);
        fields.Add("observer-location", "destination-zone", snapshot.Location.DestinationZone);
        fields.Add("observer-location", "home-zone", snapshot.Location.HomeZone);
        fields.Add("observer-location", "work-zone", snapshot.Location.WorkZone);
        fields.Add("observer-location", "travel-phase", snapshot.Location.TravelPhase);
        fields.Add("observer-location", "travel-mode", snapshot.Location.TravelMode);
        fields.Add("observer-location", "expected-arrival", snapshot.Location.ExpectedArrivalAt?.Elapsed);
        fields.Add("observer-location", "presence", snapshot.Schedule.PresenceMode);

        fields.Add("observer-intent", "activity", snapshot.Schedule.CurrentActivity);
        fields.Add("observer-intent", "goal", snapshot.Schedule.CurrentGoal);
        fields.Add("observer-intent", "availability", snapshot.Schedule.Availability);
        fields.Add("observer-intent", "next-update", snapshot.Schedule.NextUpdateAt.Elapsed);
        fields.Add("observer-intent", "commitments", snapshot.Schedule.Commitments.Length);
        fields.Add("observer-intent", "materialized", _actorRuntime.TryGetEntity(actorId, out var entityUid));
        fields.Add("observer-intent", "entity", entityUid.IsValid() ? entityUid : "abstract");
        fields.Add("observer-intent", "lod", _relevance.TryGetLod(actorId, out var lod) ? lod : "unindexed");

        if (_physicalExecution.TryGetSnapshot(actorId, out var physical))
        {
            fields.Add("observer-physical-plan", "state", physical.State);
            fields.Add("observer-physical-plan", "mode", physical.Mode);
            fields.Add("observer-physical-plan", "behavior-owner", physical.BehaviorOwner);
            fields.Add("observer-physical-plan", "destination-zone", physical.DestinationZone);
            fields.Add("observer-physical-plan", "waypoint", $"{physical.WaypointIndex}/{physical.WaypointCount}");
            fields.Add("observer-physical-plan", "replans", physical.ReplanAttempts);
            fields.Add("observer-physical-plan", "last-failure", physical.LastFailure ?? "none");
            foreach (var trace in physical.Trace.TakeLast(8))
            {
                var section = $"observer-physical-trace-{trace.Sequence}";
                fields.Add(section, "at", trace.At.Elapsed);
                fields.Add(section, "state", trace.State);
                fields.Add(section, "event", trace.Event);
                fields.Add(section, "detail", trace.Detail);
            }
        }

        if (_cognition.TryGetSnapshot(actorId, out var cognition))
        {
            fields.Add("observer-mind", "runtime", true);
            fields.Add("observer-mind", "last-decision", cognition.LastDecisionAt.Elapsed);
            fields.Add("observer-mind", "next-decision", cognition.NextDecisionAt.Elapsed);
            fields.Add("observer-mind", "facts", cognition.Facts.Length);
            fields.Add("observer-mind", "beliefs", cognition.Beliefs.Length);
            fields.Add("observer-mind", "desires", cognition.Desires.Length);
            fields.Add("observer-mind", "last-failure", cognition.LastFailure ?? "none");
            AddIntention(fields, "observer-current-intention", cognition.Intention);
            if (cognition.Plan != null)
            {
                fields.Add("observer-cognition-plan", "planner", cognition.Plan.Planner);
                fields.Add("observer-cognition-plan", "status", cognition.Plan.Status);
                fields.Add("observer-cognition-plan", "step",
                    $"{cognition.Plan.CurrentStep}/{cognition.Plan.Steps.Length}");
                fields.Add("observer-cognition-plan", "failure", cognition.Plan.Failure ?? "none");
            }
            foreach (var trace in cognition.Trace.TakeLast(12))
            {
                var section = $"observer-cognition-trace-{trace.Sequence}";
                fields.Add(section, "at", trace.At.Elapsed);
                fields.Add(section, "stage", trace.Stage);
                fields.Add(section, "event", trace.Event);
                fields.Add(section, "detail", trace.Detail);
            }
        }
        else
        {
            fields.Add("observer-mind", "runtime", false);
        }

        return (fields.Build(), null);
    }

    private void SendObserver(
        EntitySessionEventArgs args,
        Guid requestId,
        bool authorized,
        NCLivingCityAdminField[] summary,
        NCLivingCityAdminObserverZone[] zones,
        NCLivingCityAdminField[] actorFields,
        string? actorError,
        string? error)
    {
        NCLivingCityAdminTelemetry.Record(authorized && error == null, error, summary);
        RaiseNetworkEvent(new NCLivingCityAdminObserverSnapshot(
            requestId,
            authorized,
            summary,
            zones,
            actorFields,
            actorError,
            error), args.SenderSession);
    }

    private static string BoundObserverText(string value, int maximum)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maximum ? normalized : normalized[..maximum];
    }
}
