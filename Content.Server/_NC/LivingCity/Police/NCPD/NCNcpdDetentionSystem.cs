// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Affordances;
using Content.Server._NC.LivingCity.CityServices;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Police;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Police;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Affordances;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Police;
using Content.Shared._NC.LivingCity.Police.NCPD;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.Cuffs;
using Content.Shared.Database._NC.Police;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Police.NCPD;

/// <summary>
/// Completes the physical custody chain after vanilla cuffs and pulling succeed. Semantic station destinations come
/// from the automatic city affordance catalog; persistent outcomes use actor or player police records.
/// </summary>
public sealed partial class NCNcpdDetentionSystem : EntitySystem
{
    private static readonly ProtoId<NCNcpdDetentionPolicyPrototype> DefaultPolicy = "NCDefaultNcpdDetentionPolicy";
    private const string SteeringOwner = "ncpd-detention";

    private static readonly HashSet<string> SupportedActions =
    [
        "ncpd-deliver-to-booking",
        "ncpd-book-detainee",
        "ncpd-place-in-holding",
    ];

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityLocalPerceptionSystem _perception = default!;
    [Dependency] private NCEntityPartyResolverSystem _parties = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCCityIncidentRegistrySystem _incidents = default!;
    [Dependency] private NCCityAffordanceCatalogSystem _affordances = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private NCCityNpcSteeringLeaseSystem _steeringLease = default!;
    [Dependency] private NCCityPhysicalExecutionSystem _physical = default!;
    [Dependency] private NPCSteeringSystem _steering = default!;
    [Dependency] private HTNSystem _htn = default!;
    [Dependency] private SharedCuffableSystem _cuffable = default!;
    [Dependency] private PullingSystem _pulling = default!;
    [Dependency] private NCPoliceRecordsSystem _records = default!;
    [Dependency] private NCPoliceWantedRegistrySystem _wanted = default!;
    [Dependency] private NCNcpdAssignmentExecutionSystem _assignments = default!;

    private readonly Dictionary<Guid, DetentionRoute> _routes = [];
    private readonly Dictionary<Guid, PendingCustodyMutation> _pending = [];
    private readonly HashSet<EntityUid> _steeringActors = [];
    private readonly HashSet<EntityUid> _suspendedHtn = [];

    public int ActiveCustodyCount { get; private set; }
    public int ActiveRouteCount => _routes.Count;
    public int PendingMutationCount => _pending.Count;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCNcpdEscortStartedEvent>(OnEscortStarted);
        SubscribeLocalEvent<NCHumanActionExecutionEvent>(OnExecute);
        SubscribeLocalEvent<NCHumanActionInterruptedEvent>(OnInterrupted);
        SubscribeLocalEvent<NCNcpdCustodyComponent, ComponentStartup>(OnCustodyStartup);
        SubscribeLocalEvent<NCNcpdCustodyComponent, ComponentShutdown>(OnCustodyShutdown);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Clear());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Clear());
    }

    private void OnEscortStarted(NCNcpdEscortStartedEvent args)
    {
        if (!args.IncidentId.IsValid || !_parties.TryResolve(args.Officer, out var officerParty) ||
            !_parties.TryResolve(args.Suspect, out var detaineeParty))
        {
            return;
        }

        var custody = EnsureComp<NCNcpdCustodyComponent>(args.Suspect);
        custody.BookingId = Guid.Empty;
        custody.IncidentId = args.IncidentId;
        custody.Detainee = detaineeParty;
        custody.ArrestingOfficer = officerParty;
        custody.Status = NCNcpdCustodyStatus.Escorting;
        custody.UpdatedAt = args.StartedAt;
        custody.Failure = string.Empty;
    }

    private void OnExecute(NCHumanActionExecutionEvent args)
    {
        if (!SupportedActions.Contains(args.Step.ActionId) || args.EntityUid is not { } officer ||
            !Exists(officer) || !TryComp<NCNcpdOfficerComponent>(officer, out var officerComponent) ||
            !_prototypes.TryIndex(DefaultPolicy, out NCNcpdDetentionPolicyPrototype? policy) || policy == null)
        {
            return;
        }

        args.Handled = true;
        _assignments.YieldToProfessionalAction(officer);
        if (!_clock.TryGetCurrentTime(out _, out var now) ||
            args.Target.Kind != NCCognitionReferenceKind.StableKey ||
            !_perception.TryResolveSubjectHandle(args.ActorId, args.Target.StableId, now, out var suspect) ||
            !TryComp<NCNcpdCustodyComponent>(suspect, out var custody) || !custody.IncidentId.IsValid)
        {
            args.Failure = "ncpd-custody-target-unavailable";
            return;
        }

        switch (args.Step.ActionId)
        {
            case "ncpd-deliver-to-booking":
                ExecuteDelivery(args, officer, suspect, custody, officerComponent.StationZone,
                    NCCityAffordanceKind.Booking, NCNcpdCustodyStatus.AtBooking, policy, now);
                return;
            case "ncpd-book-detainee":
                ExecuteBooking(args, officer, suspect, custody, now);
                return;
            case "ncpd-place-in-holding":
                ExecuteHolding(args, officer, suspect, custody, officerComponent.StationZone, policy, now);
                return;
        }
    }

    private void ExecuteDelivery(
        NCHumanActionExecutionEvent args,
        EntityUid officer,
        EntityUid suspect,
        NCNcpdCustodyComponent custody,
        Content.Shared._NC.City.Zones.NCZoneId stationZone,
        NCCityAffordanceKind destinationKind,
        NCNcpdCustodyStatus arrivalStatus,
        NCNcpdDetentionPolicyPrototype policy,
        NCActiveWorldTime now)
    {
        if (!_cuffable.IsCuffed(suspect))
        {
            FailCustody(custody, now, "ncpd-detainee-no-longer-restrained", args);
            return;
        }
        if (!TryComp<PullerComponent>(officer, out var puller) || puller.Pulling != suspect)
        {
            FailCustody(custody, now, "ncpd-detainee-no-longer-escorted", args);
            return;
        }

        if (!_routes.TryGetValue(args.InstanceId, out var route))
        {
            if (!_affordances.TrySelect(destinationKind, stationZone, StableSeed(args.InstanceId), out var target) ||
                !_coordinates.TryResolve(target.Target, out _))
            {
                FailCustody(custody, now, $"ncpd-{destinationKind.ToString().ToLowerInvariant()}-unavailable", args);
                return;
            }
            route = new DetentionRoute(officer, suspect, destinationKind, target.Target, 0, now);
            _routes.Add(args.InstanceId, route);
        }

        if (TryComp<NPCSteeringComponent>(officer, out var steering) && steering.Status == SteeringStatus.NoPath)
        {
            StopRoute(args.InstanceId, officer);
            route = route with { Replans = route.Replans + 1, RetryAt = now.Advance(policy.RouteRetryDelay) };
            if (route.Replans > policy.MaximumRouteReplans)
            {
                FailCustody(custody, now, "ncpd-detention-route-unavailable", args);
                return;
            }
            _routes[args.InstanceId] = route;
            return;
        }
        if (now < route.RetryAt)
            return;

        if (IsReached(officer, route.Target, policy.ArrivalRange))
        {
            StopRoute(args.InstanceId, officer);
            custody.Status = arrivalStatus;
            custody.UpdatedAt = now;
            custody.Failure = string.Empty;
            args.Completed = true;
            return;
        }

        custody.Status = destinationKind == NCCityAffordanceKind.Holding
            ? NCNcpdCustodyStatus.MovingToHolding
            : NCNcpdCustodyStatus.Escorting;
        custody.UpdatedAt = now;
        if (!BeginApproach(officer, route.Target))
            return;
    }

    private void ExecuteBooking(
        NCHumanActionExecutionEvent args,
        EntityUid officer,
        EntityUid suspect,
        NCNcpdCustodyComponent custody,
        NCActiveWorldTime now)
    {
        if (custody.Status is not (NCNcpdCustodyStatus.AtBooking or NCNcpdCustodyStatus.Booking))
        {
            args.Failure = "ncpd-detainee-not-at-booking";
            return;
        }
        if (!_cuffable.IsCuffed(suspect))
        {
            FailCustody(custody, now, "ncpd-detainee-no-longer-restrained", args);
            return;
        }

        if (!_pending.TryGetValue(args.InstanceId, out var pending))
        {
            custody.Status = NCNcpdCustodyStatus.Booking;
            custody.UpdatedAt = now;
            custody.BookingId = args.InstanceId;
            var officerName = MetaData(officer).EntityName;
            pending = new PendingCustodyMutation(officer,
                PersistBookingAsync(args.InstanceId, custody.Detainee, custody.ArrestingOfficer,
                    custody.IncidentId, officerName));
            _pending.Add(args.InstanceId, pending);
        }

        if (!pending.Operation.IsCompleted)
            return;
        _pending.Remove(args.InstanceId);
        var result = pending.Operation.GetAwaiter().GetResult();
        if (result.PlayerRecord != null)
            _records.ApplyCommittedStatus(result.PlayerRecord);
        if (!result.Success)
        {
            FailCustody(custody, now, result.Error ?? "ncpd-booking-persistence-rejected", args);
            return;
        }

        custody.Status = NCNcpdCustodyStatus.Booked;
        custody.UpdatedAt = now;
        custody.Failure = string.Empty;
        args.Completed = true;
    }

    private void ExecuteHolding(
        NCHumanActionExecutionEvent args,
        EntityUid officer,
        EntityUid suspect,
        NCNcpdCustodyComponent custody,
        Content.Shared._NC.City.Zones.NCZoneId stationZone,
        NCNcpdDetentionPolicyPrototype policy,
        NCActiveWorldTime now)
    {
        if (custody.Status is not (NCNcpdCustodyStatus.Booked or NCNcpdCustodyStatus.MovingToHolding or
                NCNcpdCustodyStatus.AtHolding))
        {
            args.Failure = "ncpd-detainee-not-booked";
            return;
        }

        if (custody.Status != NCNcpdCustodyStatus.AtHolding)
        {
            ExecuteDelivery(args, officer, suspect, custody, stationZone, NCCityAffordanceKind.Holding,
                NCNcpdCustodyStatus.AtHolding, policy, now);
            // Reaching holding is only the physical precondition; persistence below owns completion.
            args.Completed = false;
            if (!string.IsNullOrWhiteSpace(args.Failure) || custody.Status != NCNcpdCustodyStatus.AtHolding)
                return;
        }
        StopRoute(args.InstanceId, officer);

        if (!_pending.TryGetValue(args.InstanceId, out var pending))
        {
            var officerName = MetaData(officer).EntityName;
            pending = new PendingCustodyMutation(officer,
                PersistCustodyStatusAsync(args.InstanceId, custody.Detainee, NCCityActorStatus.Imprisoned,
                    NCPoliceOperationalStatus.Imprisoned, custody.IncidentId, officerName));
            _pending.Add(args.InstanceId, pending);
        }
        if (!pending.Operation.IsCompleted)
            return;

        _pending.Remove(args.InstanceId);
        var result = pending.Operation.GetAwaiter().GetResult();
        if (result.PlayerRecord != null)
            _records.ApplyCommittedStatus(result.PlayerRecord);
        if (!result.Success)
        {
            FailCustody(custody, now, result.Error ?? "ncpd-holding-persistence-rejected", args);
            return;
        }

        if (TryComp<PullableComponent>(suspect, out var pullable) && pullable.Puller == officer)
            _pulling.TryStopPull(suspect, pullable, officer);
        custody.Status = NCNcpdCustodyStatus.Held;
        custody.UpdatedAt = now;
        custody.Failure = string.Empty;
        args.Completed = true;
    }

    private async Task<CustodyMutationResult> PersistBookingAsync(
        Guid instanceId,
        NCPartyRef detainee,
        NCPartyRef officer,
        NCCityIncidentId incidentId,
        string officerName)
    {
        var custody = await PersistCustodyStatusAsync(instanceId, detainee, NCCityActorStatus.Arrested,
            NCPoliceOperationalStatus.Arrested, incidentId, officerName);
        if (!custody.Success)
            return custody;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var incident = await _incidents.GetAsync(incidentId, true);
            if (incident == null)
                return custody with { Success = false, Error = "ncpd-incident-not-found" };
            if (incident.Snapshot.Stage == NCCityIncidentStage.Resolved &&
                incident.Snapshot.Outcome?.OutcomeKey == "ncpd-physical-arrest")
                return await MarkWantedDetainedAsync(custody, detainee, officer, incidentId);

            var outcome = new NCIncidentOutcome("ncpd-physical-arrest", [detainee], [], 0, 0);
            var result = await _incidents.ResolveAsync(new NCResolveIncidentRequest(
                NCPersistentIdentity.Command("ncpd", "book-detainee", $"{incidentId}.{detainee}"),
                incidentId,
                incident.AggregateVersion,
                outcome,
                officer,
                NCPersistentIdentity.Correlation("ncpd", "book-detainee", $"{incidentId}.{detainee}"),
                incident.Snapshot.LastAppliedEventId,
                "ncpd-physical-booking"));
            if (result.Status is NCIncidentMutationStatus.Applied or NCIncidentMutationStatus.Duplicate)
                return await MarkWantedDetainedAsync(custody, detainee, officer, incidentId);
            if (result.Status != NCIncidentMutationStatus.VersionConflict)
                return custody with { Success = false, Error = result.Error ?? "ncpd-incident-resolution-rejected" };
        }
        return custody with { Success = false, Error = "ncpd-incident-version-conflict" };
    }

    private async Task<CustodyMutationResult> MarkWantedDetainedAsync(
        CustodyMutationResult custody,
        NCPartyRef detainee,
        NCPartyRef officer,
        NCCityIncidentId incidentId)
    {
        var wantedId = NCPoliceWantedRegistrySystem.Identity(incidentId, detainee);
        var existing = await _wanted.GetAsync(wantedId, true);
        if (existing == null || existing.Snapshot.Status == NCPoliceWantedStatus.Detained)
            return custody;
        var result = await _wanted.SetStatusAsync(wantedId, NCPoliceWantedStatus.Detained, officer,
            "ncpd-physical-booking");
        return result.Status is NCPoliceWantedMutationStatus.Applied or NCPoliceWantedMutationStatus.Duplicate
            ? custody
            : custody with { Success = false, Error = result.Error ?? "ncpd-wanted-close-rejected" };
    }

    private async Task<CustodyMutationResult> PersistCustodyStatusAsync(
        Guid instanceId,
        NCPartyRef detainee,
        NCCityActorStatus actorStatus,
        NCPoliceOperationalStatus playerStatus,
        NCCityIncidentId incidentId,
        string officerName)
    {
        try
        {
            if (detainee.TryGetActor(out var actorId))
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var actor = await _actors.GetAsync(actorId, true);
                    if (actor == null)
                        return new(false, null, "ncpd-detainee-actor-not-found");
                    var result = await _actors.SetStatusAsync(new NCCityActorStatusMutationRequest(
                        NCPersistentIdentity.Command("ncpd", actorStatus.ToString(),
                            $"{incidentId}.{detainee}.{instanceId}"),
                        actorId,
                        actor.AggregateVersion,
                        actorStatus,
                        NCPersistentIdentity.Correlation("ncpd", actorStatus.ToString(),
                            $"{incidentId}.{detainee}"),
                        incidentId.Value,
                        "ncpd-physical-custody"));
                    if (result.Status is NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate)
                        return new(true, null, null);
                    if (result.Status != NCCityActorMutationStatus.VersionConflict)
                        return new(false, null, result.Error ?? "ncpd-actor-custody-rejected");
                }
                return new(false, null, "ncpd-actor-version-conflict");
            }

            if (detainee.TryGetCharacter(out var characterId))
            {
                var reason = $"Physical NCPD custody for incident {incidentId}.";
                var record = await _records.PersistStatusAsync(characterId, playerStatus, reason, null, officerName);
                return record == null
                    ? new(false, null, "ncpd-player-police-record-rejected")
                    : new(true, record, null);
            }

            return new(false, null, "ncpd-detainee-party-kind-unsupported");
        }
        catch (Exception exception)
        {
            return new(false, null, $"ncpd-custody-exception:{exception.GetType().Name}");
        }
    }

    private bool BeginApproach(EntityUid officer, NCMapCoordinates target)
    {
        // Keep the current vanilla route alive; registering the same destination every action pulse would force
        // unnecessary replans and recreate the frame spikes this A-Life layer is designed to avoid.
        if (_steeringActors.Contains(officer))
            return true;
        if (!TryResolveEntityCoordinates(target, out var destination) ||
            _steeringLease.HasOtherOwner(officer, SteeringOwner) ||
            !_steeringLease.Acquire(officer, SteeringOwner))
        {
            return false;
        }
        if (_suspendedHtn.Add(officer) && TryComp<HTNComponent>(officer, out var htn) && htn.Enabled)
            _htn.SetHTNEnabled((officer, htn), false);
        _physical.SetInterrupt(officer, NCCityPhysicalInterruptFlags.Police, true);
        _steeringActors.Add(officer);
        _steering.Register(officer, destination);
        return true;
    }

    private bool TryResolveEntityCoordinates(NCMapCoordinates target, out EntityCoordinates coordinates)
    {
        coordinates = default;
        if (!_coordinates.TryResolve(target, out var mapCoordinates) ||
            !_maps.TryGetMap(mapCoordinates.MapId, out var mapUid) || mapUid is not { } map ||
            !_maps.TryFindGridAt(map, mapCoordinates.Position, out var gridUid, out _))
        {
            return false;
        }
        coordinates = _transform.ToCoordinates((gridUid, null), mapCoordinates);
        return true;
    }

    private bool IsReached(EntityUid officer, NCMapCoordinates target, float range) =>
        _coordinates.TryGetCoordinates(officer, out var current) && current.NetworkId == target.NetworkId &&
        current.Z == target.Z && Vector2.DistanceSquared(current.Position, target.Position) <= range * range;

    private void StopRoute(Guid instanceId, EntityUid officer)
    {
        _routes.Remove(instanceId);
        StopSteering(officer);
    }

    private void StopSteering(EntityUid officer)
    {
        if (_steeringActors.Remove(officer) && Exists(officer))
        {
            _steering.Unregister(officer);
            _steeringLease.Release(officer, SteeringOwner);
        }
        if (!Exists(officer))
        {
            _suspendedHtn.Remove(officer);
            return;
        }
        _physical.SetInterrupt(officer, NCCityPhysicalInterruptFlags.Police, false);
        if (_suspendedHtn.Remove(officer) && TryComp<HTNComponent>(officer, out var htn))
            _htn.SetHTNEnabled((officer, htn), true, htn.PlanCooldown);
    }

    private static void FailCustody(
        NCNcpdCustodyComponent custody,
        NCActiveWorldTime now,
        string failure,
        NCHumanActionExecutionEvent args)
    {
        custody.Status = NCNcpdCustodyStatus.Failed;
        custody.UpdatedAt = now;
        custody.Failure = failure;
        args.Failure = failure;
    }

    private void OnInterrupted(NCHumanActionInterruptedEvent args)
    {
        if (!SupportedActions.Contains(args.ActionId))
            return;
        _pending.Remove(args.InstanceId);
        if (args.EntityUid is { } officer)
            StopRoute(args.InstanceId, officer);
        else
            _routes.Remove(args.InstanceId);
    }

    private void OnCustodyStartup(Entity<NCNcpdCustodyComponent> entity, ref ComponentStartup args) =>
        ActiveCustodyCount++;

    private void OnCustodyShutdown(Entity<NCNcpdCustodyComponent> entity, ref ComponentShutdown args)
    {
        ActiveCustodyCount = Math.Max(0, ActiveCustodyCount - 1);
    }

    private void Clear()
    {
        foreach (var officer in _steeringActors.ToArray())
            StopSteering(officer);
        _routes.Clear();
        _pending.Clear();
        _steeringActors.Clear();
        _suspendedHtn.Clear();
        ActiveCustodyCount = 0;
    }

    private static uint StableSeed(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        var hash = 2166136261u;
        foreach (var value in bytes)
            hash = (hash ^ value) * 16777619u;
        return hash;
    }

    private readonly record struct DetentionRoute(
        EntityUid Officer,
        EntityUid Suspect,
        NCCityAffordanceKind Kind,
        NCMapCoordinates Target,
        int Replans,
        NCActiveWorldTime RetryAt);

    private readonly record struct PendingCustodyMutation(
        EntityUid Officer,
        Task<CustodyMutationResult> Operation);

    private readonly record struct CustodyMutationResult(
        bool Success,
        NCPoliceRecordData? PlayerRecord,
        string? Error);
}
