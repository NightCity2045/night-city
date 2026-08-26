// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Cognition;

/// <summary>
/// Resolves planner bookkeeping and waits for authoritative schedule/travel/gameplay ownership to release. It never
/// simulates a world mutation: unavailable group and medical-service executors fail explicitly.
/// </summary>
public sealed partial class NCHumanCoreActionAdapterSystem : EntitySystem
{
    [Dependency] private NCCityActorRegistrySystem _actors = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCHumanActionExecutionEvent>(OnExecute);
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
                args.Completed = TryGetTravelPhase(args.ActorId, out var schedulePhase) &&
                                 schedulePhase is not (NCCityActorTravelPhase.Planned or
                                     NCCityActorTravelPhase.InTransit);
                return;
            case "follow-schedule-travel":
            case "follow-group-route":
                args.Handled = true;
                args.Completed = TryGetTravelPhase(args.ActorId, out var travelPhase) &&
                                 travelPhase is NCCityActorTravelPhase.Stationary or NCCityActorTravelPhase.Arrived;
                return;
            case "yield-to-medical-authority":
                ResolveAuthority(args, NCCityDematerializationBlockerFlags.PlayerTreatment);
                return;
            case "yield-to-conversation-authority":
                ResolveAuthority(args, NCCityDematerializationBlockerFlags.Conversation);
                return;
            case "select-medical-service":
                RejectMissing(args, "medical-service-selection-not-implemented");
                return;
            case "prepare-group-role":
            case "acquire-group-resources":
            case "execute-group-directive":
                RejectMissing(args, $"group-action-executor-not-implemented:{args.Action.ID}");
                return;
        }
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
}
