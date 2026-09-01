// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Groups;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.LivingCity.Groups;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.CityServices;

/// <summary>
/// Persists service assignments and route context for an authored group of already-physical actors.
/// It never spawns, despawns, or creates a parallel responder NPC model.
/// </summary>
public sealed partial class NCCityServiceGroupAdapterSystem : EntitySystem
{
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityServiceSystem _services = default!;
    [Dependency] private NCCityGroupRegistrySystem _groups = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCServiceDispatchPersistedEvent>(OnDispatchPersisted);
    }

    public async Task<NCCityServiceMutationResult> AssignAsync(NCServiceDispatchId dispatchId, NCGroupId groupId)
    {
        var dispatch = await _services.GetDispatchAsync(dispatchId, true);
        var group = await _groups.GetAsync(groupId, true);
        if (dispatch == null || group == null || !_clock.TryGetCurrentTime(out _, out var now))
            return new(NCCityServiceMutationStatus.NotFound, null, null, dispatch,
                "The dispatch, group, or active-world clock is unavailable.");
        var service = await _services.GetServiceAsync(dispatch.Snapshot.ServiceId, true);
        if (service == null || service.Snapshot.FactionId is { } faction && group.Snapshot.FactionId != faction ||
            group.Snapshot.Status is NCCityGroupStatus.Disbanded or NCCityGroupStatus.Archived ||
            group.Snapshot.Members.Length == 0 || dispatch.Snapshot.ExpectedArrivalAt is not { } arrival)
            return new(NCCityServiceMutationStatus.NotEligible, service, null, dispatch,
                "The group is not eligible for this service response.");

        var stable = $"{dispatchId}.{groupId}";
        foreach (var member in group.Snapshot.Members)
        {
            var actor = await _actors.GetAsync(member.ActorId, true);
            if (actor == null || actor.Snapshot.Location.CurrentZone != group.Snapshot.Route.CurrentZone)
                return new(NCCityServiceMutationStatus.NotEligible, service, null, dispatch,
                    "A response-group member is unavailable or not co-located.");
            await _actors.SetLocationAsync(new NCCityActorLocationMutationRequest(
                NCPersistentIdentity.Command("service-group", "deploy-member", $"{stable}.{member.ActorId}"),
                member.ActorId, actor.AggregateVersion, actor.Snapshot.Location.CurrentZone,
                dispatch.Snapshot.DestinationZone, NCCityActorTravelPhase.Planned, dispatch.Snapshot.TravelMode,
                arrival, "service-response", dispatch.Snapshot.IncidentId.ToString(),
                NCCityActorAvailability.Busy, arrival,
                NCPersistentIdentity.Correlation("service-group", "deploy-member", stable),
                dispatch.Snapshot.LastAppliedEventId, "service-group-adapter"));
        }

        var directive = await _groups.SetDirectiveAsync(new NCCityGroupSetDirectiveRequest(
            NCPersistentIdentity.Command("service-group", "directive", stable), groupId, group.AggregateVersion,
            "city-service-response", "respond", dispatchId.ToString(), dispatch.Snapshot.IncidentId.Value,
            NCPersistentIdentity.Correlation("service-group", "directive", stable),
            dispatch.Snapshot.LastAppliedEventId, "service-group-adapter"));
        group = directive.Groups.FirstOrDefault() ?? await _groups.GetAsync(groupId, true);
        if (group == null)
            return new(NCCityServiceMutationStatus.StorageFailure, service, null, dispatch,
                "The response group directive could not reload.");
        var route = await _groups.SetRouteAsync(new NCCityGroupSetRouteRequest(
            NCPersistentIdentity.Command("service-group", "route", stable), groupId, group.AggregateVersion,
            group.Snapshot.Route.CurrentZone, dispatch.Snapshot.DestinationZone, dispatch.Snapshot.TravelMode,
            arrival, NCCityGroupStatus.Traveling,
            NCPersistentIdentity.Correlation("service-group", "route", stable),
            dispatch.Snapshot.LastAppliedEventId, "service-group-adapter"));
        if (route.Status is not (NCCityGroupMutationStatus.Applied or NCCityGroupMutationStatus.Duplicate))
            return new(NCCityServiceMutationStatus.StorageFailure, service, null, dispatch, route.Error);
        dispatch = await _services.GetDispatchAsync(dispatchId, true) ?? dispatch;
        return await _services.BindResponseGroupAsync(dispatchId, dispatch.AggregateVersion, groupId,
            service.Snapshot.ServiceParty);
    }

    private void OnDispatchPersisted(NCServiceDispatchPersistedEvent args)
    {
        if (args.Dispatch.Snapshot.Status == NCServiceDispatchStatus.OnScene &&
            args.Dispatch.Snapshot.ResponseGroupId is { } groupId)
            _ = ArriveGroupAsync(args.Dispatch, new NCGroupId(groupId));
    }

    private async Task ArriveGroupAsync(NCServiceDispatchRecord dispatch, NCGroupId groupId)
    {
        var group = await _groups.GetAsync(groupId, true);
        if (group == null || !_clock.TryGetCurrentTime(out _, out var now))
            return;
        var stable = $"{dispatch.Snapshot.DispatchId}.{groupId}.arrival";
        foreach (var member in group.Snapshot.Members)
        {
            var actor = await _actors.GetAsync(member.ActorId, true);
            if (actor == null)
                continue;
            await _actors.SetLocationAsync(new NCCityActorLocationMutationRequest(
                NCPersistentIdentity.Command("service-group", "arrive-member", $"{stable}.{member.ActorId}"),
                member.ActorId, actor.AggregateVersion, dispatch.Snapshot.DestinationZone, default,
                NCCityActorTravelPhase.Stationary, NCCityTravelMode.None, null, "incident-response",
                dispatch.Snapshot.IncidentId.ToString(), NCCityActorAvailability.Busy, now.Advance(TimeSpan.FromMinutes(1)),
                NCPersistentIdentity.Correlation("service-group", "arrive-member", stable),
                dispatch.Snapshot.LastAppliedEventId, "service-group-adapter"));
        }
        group = await _groups.GetAsync(groupId, true);
        if (group == null)
            return;
        await _groups.SetRouteAsync(new NCCityGroupSetRouteRequest(
            NCPersistentIdentity.Command("service-group", "arrive-route", stable), groupId, group.AggregateVersion,
            dispatch.Snapshot.DestinationZone, default, NCCityTravelMode.None, null, NCCityGroupStatus.Engaged,
            NCPersistentIdentity.Correlation("service-group", "arrive-route", stable),
            dispatch.Snapshot.LastAppliedEventId, "service-group-adapter"));
    }
}
