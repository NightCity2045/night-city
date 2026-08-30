// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Threading.Tasks;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Affordances;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Affordances;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Cognition;

/// <summary>
/// Resolves planner bookkeeping and waits for authoritative schedule/travel/gameplay ownership to release. It never
/// simulates a world mutation: unavailable group executors fail explicitly.
/// </summary>
public sealed partial class NCHumanCoreActionAdapterSystem : EntitySystem
{
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCCityAffordanceCatalogSystem _affordances = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;

    private readonly Dictionary<NCActorId, PendingMedicalSelection> _medicalSelections = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCHumanActionExecutionEvent>(OnExecute);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => _medicalSelections.Clear());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => _medicalSelections.Clear());
    }

    private void OnExecute(NCHumanActionExecutionEvent args)
    {
        switch (args.Action.ID)
        {
            case "evaluate-safe-option":
                ResolvePlannerReadiness(args, false);
                return;
            case "remain-available":
                // This is the terminal safe fallback, not a request to change schedule availability. Waiting for an
                // unavailable resident here recursively recreates the same fallback until its timeout expires.
                ResolvePlannerReadiness(args, false);
                return;
            case "follow-schedule":
                args.Handled = true;
                if (!TryGetTravelPhase(args.ActorId, out var schedulePhase))
                {
                    args.Failure = "human-schedule-actor-unavailable";
                    return;
                }
                args.Completed = schedulePhase is not (NCCityActorTravelPhase.Planned or
                    NCCityActorTravelPhase.InTransit);
                return;
            case "follow-schedule-travel":
            case "follow-group-route":
                args.Handled = true;
                if (!TryGetTravelPhase(args.ActorId, out var travelPhase))
                {
                    args.Failure = "human-travel-actor-unavailable";
                    return;
                }
                args.Completed = travelPhase is NCCityActorTravelPhase.Stationary or NCCityActorTravelPhase.Arrived;
                return;
            case "yield-to-medical-authority":
                ResolveAuthority(args, NCCityDematerializationBlockerFlags.PlayerTreatment);
                return;
            case "yield-to-conversation-authority":
                ResolveAuthority(args, NCCityDematerializationBlockerFlags.Conversation);
                return;
            case "select-medical-service":
                SelectMedicalService(args);
                return;
            case "prepare-group-role":
            case "acquire-group-resources":
            case "execute-group-directive":
                RejectMissing(args, $"group-action-executor-not-implemented:{args.Action.ID}");
                return;
        }
    }

    /// <summary>Selects a real medical affordance and commits the route through the actor aggregate.</summary>
    private void SelectMedicalService(NCHumanActionExecutionEvent args)
    {
        args.Handled = true;
        if (_medicalSelections.TryGetValue(args.ActorId, out var pending))
        {
            if (pending.IntentionId != args.IntentionId)
            {
                _medicalSelections.Remove(args.ActorId);
                args.Failure = "medical-service-intention-changed";
                return;
            }
            if (!pending.Mutation.IsCompleted)
                return;
            _medicalSelections.Remove(args.ActorId);
            if (pending.Mutation.IsFaulted || pending.Mutation.IsCanceled)
            {
                args.Failure = "medical-service-route-failed";
                return;
            }
            var result = pending.Mutation.GetAwaiter().GetResult();
            if (result.Status is NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate)
                args.Completed = true;
            else
                args.Failure = $"medical-service-route-rejected:{result.Status}";
            return;
        }

        if (!_clock.TryGetCurrentTime(out _, out var now) ||
            !_actors.TryGetCached(args.ActorId, out var actor) ||
            !_affordances.TrySelect(NCCityAffordanceKind.MedicalTreatment, default,
                unchecked((uint) actor.Snapshot.Identity.BiographySeed), out var service))
        {
            args.Failure = "medical-service-unavailable";
            return;
        }

        var location = actor.Snapshot.Location;
        if (location.CurrentZone == service.ZoneId)
        {
            args.Completed = true;
            return;
        }

        var mutation = _actors.SetLocationAsync(new NCCityActorLocationMutationRequest(
            Guid.NewGuid(),
            args.ActorId,
            actor.AggregateVersion,
            location.CurrentZone,
            service.ZoneId,
            NCCityActorTravelPhase.Planned,
            NCCityTravelMode.Pedestrian,
            null,
            "seek-medical-care",
            "reach-medical-service",
            NCCityActorAvailability.Busy,
            now.Advance(TimeSpan.FromMinutes(1)),
            Guid.NewGuid(),
            AuditSource: "human-medical-service"));
        _medicalSelections[args.ActorId] = new PendingMedicalSelection(args.IntentionId, mutation);
    }

    private bool TryGetTravelPhase(NCActorId actorId, out NCCityActorTravelPhase phase)
    {
        if (_actors.TryGetCached(actorId, out var actor))
        {
            phase = actor.Snapshot.Location.TravelPhase;
            return true;
        }
        phase = default;
        return false;
    }

    private void ResolvePlannerReadiness(NCHumanActionExecutionEvent args, bool requireStationaryAvailability)
    {
        args.Handled = true;
        if (!_actors.TryGetCached(args.ActorId, out var actor))
        {
            args.Failure = "human-planner-actor-unavailable";
            return;
        }

        var snapshot = actor.Snapshot;
        if (snapshot.Lifecycle.Frozen || snapshot.Lifecycle.Status is NCCityActorStatus.Missing or
            NCCityActorStatus.Dead or NCCityActorStatus.Archived or NCCityActorStatus.Unconscious)
        {
            args.Failure = "human-planner-actor-cannot-act";
            return;
        }

        // Planner bookkeeping is complete only after observing an authoritative actor state. This prevents the
        // fallback path from reporting a successful physical action without any confirmed condition.
        args.Completed = !requireStationaryAvailability ||
                         snapshot.Schedule.Availability != NCCityActorAvailability.Unavailable &&
                         snapshot.Location.TravelPhase is not (NCCityActorTravelPhase.Planned or
                             NCCityActorTravelPhase.InTransit);
    }

    private void ResolveAuthority(NCHumanActionExecutionEvent args, NCCityDematerializationBlockerFlags owner)
    {
        args.Handled = true;
        if (args.EntityUid is not { } entity || !Exists(entity))
        {
            args.Failure = "human-authority-entity-unavailable";
            return;
        }
        args.Completed = !TryComp<NCCityDematerializationBlockerComponent>(entity, out var blocker) ||
                         !blocker.Flags.HasFlag(owner);
    }

    private static void RejectMissing(NCHumanActionExecutionEvent args, string failure)
    {
        args.Handled = true;
        args.Failure = failure;
    }

    private sealed record PendingMedicalSelection(Guid IntentionId, Task<NCCityActorMutationResult> Mutation);
}
