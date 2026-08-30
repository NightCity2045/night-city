// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Threading.Tasks;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Actors;

public sealed partial class NCCityPhysicalExecutionSystem
{
    private void BeginArrivalMutation(
        EntityUid entityUid,
        NCCityPhysicalExecutionComponent component,
        NCActiveWorldTime now)
    {
        if (!component.DestinationZone.IsValid)
        {
            FailGoal(entityUid, component, now, "arrival-has-no-destination-zone");
            return;
        }

        QueueLocationMutation(
            entityUid,
            component,
            now,
            NCCityPhysicalMutationKind.Arrival,
            component.DestinationZone,
            default,
            NCCityActorTravelPhase.Stationary,
            null);
    }

    private void BeginBlockedMutation(
        EntityUid entityUid,
        NCCityPhysicalExecutionComponent component,
        NCActiveWorldTime now,
        string failure)
    {
        var currentZone = component.CurrentZone;
        if (_coordinates.TryGetCoordinates(entityUid, out var coordinates))
        {
            var resolved = _zones.GetLocationContext(coordinates).MostSpecific;
            if (resolved.IsValid)
                currentZone = resolved;
        }
        if (!currentZone.IsValid || !component.DestinationZone.IsValid)
        {
            FailGoal(entityUid, component, now, "blocked-location-cannot-be-resolved");
            return;
        }

        QueueLocationMutation(
            entityUid,
            component,
            now,
            NCCityPhysicalMutationKind.Blocked,
            currentZone,
            component.DestinationZone,
            NCCityActorTravelPhase.Blocked,
            failure);
    }

    private void BeginAbortMutation(
        EntityUid entityUid,
        NCCityPhysicalExecutionComponent component,
        NCActiveWorldTime now,
        string failure)
    {
        var currentZone = component.CurrentZone;
        if (_coordinates.TryGetCoordinates(entityUid, out var coordinates))
        {
            var resolved = _zones.GetLocationContext(coordinates).MostSpecific;
            if (resolved.IsValid)
                currentZone = resolved;
        }
        if (!currentZone.IsValid)
        {
            FailGoal(entityUid, component, now, "abort-location-cannot-be-resolved");
            return;
        }

        QueueLocationMutation(
            entityUid,
            component,
            now,
            NCCityPhysicalMutationKind.Aborted,
            currentZone,
            default,
            NCCityActorTravelPhase.Stationary,
            failure);
    }

    private void QueueLocationMutation(
        EntityUid entityUid,
        NCCityPhysicalExecutionComponent component,
        NCActiveWorldTime now,
        NCCityPhysicalMutationKind kind,
        Content.Shared._NC.City.Zones.NCZoneId currentZone,
        Content.Shared._NC.City.Zones.NCZoneId destinationZone,
        NCCityActorTravelPhase phase,
        string? failure)
    {
        StopSteering(entityUid, component);
        var request = new NCCityActorLocationMutationRequest(
            Guid.NewGuid(),
            component.ActorId,
            component.ActorVersion,
            currentZone,
            destinationZone,
            phase,
            destinationZone.IsValid ? component.TravelMode : NCCityTravelMode.None,
            null,
            component.Activity,
            component.Goal,
            component.Availability,
            component.NextScheduleUpdateAt,
            component.GoalId == Guid.Empty ? Guid.NewGuid() : component.GoalId,
            AuditSource: "system:physical-execution",
            PresenceMode: component.PresenceMode);
        _pendingMutations[component.ActorId] = new PendingLocationMutation(
            entityUid,
            kind,
            request,
            now,
            false,
            0,
            TrimDiagnostic(failure));
        component.State = NCCityPhysicalExecutionState.Committing;
        AddTrace(component, now, kind == NCCityPhysicalMutationKind.Arrival
            ? "arrival-commit-queued"
            : "blocked-commit-queued", failure);
        TryDispatchPendingMutation(entityUid, component, now);
    }

    private void TryDispatchPendingMutation(
        EntityUid entityUid,
        NCCityPhysicalExecutionComponent component,
        NCActiveWorldTime now)
    {
        if (_policy == null ||
            !_pendingMutations.TryGetValue(component.ActorId, out var pending) ||
            pending.EntityUid != entityUid)
        {
            FailGoal(entityUid, component, now, "pending-location-mutation-missing");
            return;
        }
        if (pending.InFlight)
        {
            ScheduleNext(entityUid, now);
            return;
        }
        if (pending.NextAttemptAt > now || _mutationsInFlight >= _policy.MaximumConcurrentMutations)
        {
            ScheduleExecution(entityUid, pending.NextAttemptAt > now
                ? pending.NextAttemptAt
                : now.Advance(_policy.EvaluationInterval));
            return;
        }

        pending = pending with
        {
            InFlight = true,
            Attempts = pending.Attempts + 1,
        };
        _pendingMutations[component.ActorId] = pending;
        _mutationsInFlight++;
        _ = ObserveLocationMutation(_generation, pending);
    }

    private async Task ObserveLocationMutation(int generation, PendingLocationMutation pending)
    {
        NCCityActorMutationResult? result = null;
        Exception? error = null;
        try
        {
            result = await _actors.SetLocationAsync(pending.Request);
        }
        catch (Exception exception)
        {
            error = exception;
        }

        _taskManager.RunOnMainThread(() => CompleteLocationMutation(generation, pending, result, error));
    }

    private void CompleteLocationMutation(
        int generation,
        PendingLocationMutation dispatched,
        NCCityActorMutationResult? result,
        Exception? error)
    {
        if (generation != _generation)
            return;
        _mutationsInFlight = Math.Max(0, _mutationsInFlight - 1);
        if (!_active || _policy == null ||
            !_pendingMutations.TryGetValue(dispatched.Request.ActorId, out var pending) ||
            pending.Request.CommandId != dispatched.Request.CommandId ||
            !Exists(pending.EntityUid) ||
            !TryComp<NCCityPhysicalExecutionComponent>(pending.EntityUid, out var component) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
        {
            return;
        }

        pending = pending with { InFlight = false };
        _pendingMutations[component.ActorId] = pending;
        if (error != null || result == null)
        {
            component.LastFailure = TrimDiagnostic(error?.Message ?? "location-mutation-returned-no-result");
            if (!CanRetryLocationMutation(pending))
            {
                ExhaustLocationMutation(pending, component, now);
                return;
            }
            _pendingMutations[component.ActorId] = pending with
            {
                NextAttemptAt = now.Advance(_policy.MutationRetryDelay),
            };
            AddTrace(component, now, "location-commit-retry", component.LastFailure);
            ScheduleExecution(pending.EntityUid, now.Advance(_policy.MutationRetryDelay));
            return;
        }

        switch (result.Status)
        {
            case NCCityActorMutationStatus.Applied:
            case NCCityActorMutationStatus.Duplicate:
                _pendingMutations.Remove(component.ActorId);
                if (result.Record != null)
                    component.ActorVersion = result.Record.AggregateVersion;
                CompleteCommittedMutation(pending, component, now);
                return;
            case NCCityActorMutationStatus.StorageFailure:
                component.LastFailure = TrimDiagnostic(result.Error);
                if (!CanRetryLocationMutation(pending))
                {
                    ExhaustLocationMutation(pending, component, now);
                    return;
                }
                _pendingMutations[component.ActorId] = pending with
                {
                    NextAttemptAt = now.Advance(_policy.MutationRetryDelay),
                };
                AddTrace(component, now, "location-commit-retry", component.LastFailure);
                ScheduleExecution(pending.EntityUid, now.Advance(_policy.MutationRetryDelay));
                return;
            case NCCityActorMutationStatus.VersionConflict:
                _pendingMutations.Remove(component.ActorId);
                component.State = NCCityPhysicalExecutionState.LoadingIntent;
                component.LastFailure = "actor-version-conflict";
                AddTrace(component, now, "location-commit-conflict", component.LastFailure);
                QueueIntentLoad(component.ActorId, pending.EntityUid, result.AggregateVersion, now);
                return;
            default:
                _pendingMutations.Remove(component.ActorId);
                FailGoal(pending.EntityUid, component, now,
                    $"location-commit-{result.Status}: {result.Error}");
                return;
        }
    }

    private bool CanRetryLocationMutation(PendingLocationMutation pending) =>
        _policy != null && pending.Attempts < _policy.MaximumMutationAttempts;

    private void ExhaustLocationMutation(
        PendingLocationMutation pending,
        NCCityPhysicalExecutionComponent component,
        NCActiveWorldTime now)
    {
        _pendingMutations.Remove(component.ActorId);
        _mutationRetriesExhausted++;
        FailGoal(pending.EntityUid, component, now, "location-commit-retries-exhausted");
    }

    private void CompleteCommittedMutation(
        PendingLocationMutation pending,
        NCCityPhysicalExecutionComponent component,
        NCActiveWorldTime now)
    {
        switch (pending.Kind)
        {
            case NCCityPhysicalMutationKind.Arrival:
                component.CurrentZone = component.DestinationZone;
                AddTrace(component, now, "arrival-committed", component.DestinationZone.ToString());
                _arrivals++;
                SetIdle(pending.EntityUid, component, now, "arrival-committed");
                break;
            case NCCityPhysicalMutationKind.Blocked:
                StopSteering(pending.EntityUid, component);
                ReleaseProceduralAI(pending.EntityUid, component);
                component.CurrentZone = pending.Request.CurrentZone;
                component.State = NCCityPhysicalExecutionState.Blocked;
                component.LastFailure = pending.Failure;
                component.Waypoints.Clear();
                component.WaypointIndex = 0;
                component.PlanVersion = default;
                AddTrace(component, now, "goal-blocked", pending.Failure);
                _blockedGoals++;
                if (_policy != null)
                    ScheduleExecution(pending.EntityUid, now.Advance(_policy.BlockedRetryInterval));
                break;
            case NCCityPhysicalMutationKind.Aborted:
                component.CurrentZone = pending.Request.CurrentZone;
                component.LastFailure = pending.Failure;
                AddTrace(component, now, "plan-aborted", pending.Failure);
                SetIdle(pending.EntityUid, component, now, "plan-aborted");
                break;
        }
    }

    private sealed record PendingLocationMutation(
        EntityUid EntityUid,
        NCCityPhysicalMutationKind Kind,
        NCCityActorLocationMutationRequest Request,
        NCActiveWorldTime NextAttemptAt,
        bool InFlight,
        int Attempts,
        string Failure);
}
