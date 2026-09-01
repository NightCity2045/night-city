// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.City.ZoneTemplates;
using Content.Server._NC.LivingCity.CityServices;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.LivingCity.Police.NCPD;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Police.NCPD;

/// <summary>
/// Bridges a persisted NCPD city-service dispatch to an always-physical patrol.
/// </summary>
public sealed partial class NCNcpdServiceDispatchSystem : EntitySystem
{
    private static readonly ProtoId<NCNcpdDispatchPolicyPrototype> DefaultPolicy = "NCDefaultNcpdDispatchPolicy";
    private static readonly NCCityServiceId NcpdService =
        new(NCPersistentIdentity.CityService("ncpd").PersistentId);

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ITaskManager _taskManager = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCPhysicalActorLifecycleSystem _physicalActors = default!;
    [Dependency] private NCCityServiceSystem _services = default!;
    [Dependency] private NCCityIncidentRegistrySystem _incidents = default!;
    [Dependency] private NCNcpdUnitSystem _units = default!;
    [Dependency] private NCNcpdCommandSystem _commands = default!;
    [Dependency] private NCNcpdWorkforceProjectionSystem _workforce = default!;
    [Dependency] private NCZoneOccupancySystem _occupancy = default!;

    private readonly Dictionary<NCServiceDispatchId, DispatchWork> _work = [];
    private readonly Dictionary<NCServiceDispatchId, NCActiveWorldTime> _scheduled = [];
    private readonly Dictionary<(NCServiceDispatchId, NCActorId), Guid> _materializationRequests = [];
    private readonly HashSet<(NCServiceDispatchId DispatchId, NCActorId ActorId)> _physicalDispatchActors = [];
    private readonly HashSet<NCCityIncidentId> _investigationIncidents = [];
    private readonly HashSet<NCCityIncidentId> _recoveredIncidents = [];
    private readonly HashSet<NCCityIncidentId> _incidentRecoveries = [];
    private readonly PriorityQueue<ScheduledDispatch, DispatchPriority> _queue = new();
    private readonly List<NCNcpdUnitSnapshot> _unitBuffer = [];
    private readonly List<NCActorId> _arrivalActorBuffer = [];
    private long _sequence;
    private int _generation;
    private bool _active;

    public int TrackedDispatches => _work.Count;
    public int AwaitingUnitCount => _work.Values.Count(static value => !value.UnitId.IsValid);
    public int AssignedDispatches => _work.Values.Count(static value => value.UnitId.IsValid);
    public int PreparationInFlight => _work.Values.Count(static value => value.Preparing);
    public int AbstractOnSceneCount => _work.Values.Count(static value => value.Status == "on-scene-abstract");
    public int PhysicalizationPendingCount => _work.Values.Count(static value =>
        value.Status == "on-scene-materialization-requested");
    public int RecoveryPendingCount => _incidentRecoveries.Count;
    public int RecoveredIncidentCount { get; private set; }
    public int RecoveryFailureCount { get; private set; }
    public string? LastRecoveryError { get; private set; }
    public string? ConfigurationError { get; private set; }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Clear());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Clear());
        SubscribeLocalEvent<NCServiceDispatchPersistedEvent>(OnServiceDispatch);
        SubscribeLocalEvent<NCPhysicalActorSpawnedEvent>(OnActorRehydrated);
        SubscribeLocalEvent<NCServiceDispatchArrivalAttemptEvent>(OnArrivalAttempt);
        SubscribeLocalEvent<NCCityIncidentPersistedEvent>(OnIncidentPersisted);
        SubscribeLocalEvent<NCNcpdDutyChangedEvent>(_ => ScheduleWaiting());
        SubscribeLocalEvent<NCNcpdUnitRemovedEvent>(OnUnitRemoved);
        SubscribeLocalEvent<NCNcpdCommandChangedEvent>(OnCommandChanged);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_active || !_clock.TryGetCurrentTime(out _, out var now) || !TryPolicy(out var policy))
            return;

        var handled = 0;
        while (handled < policy.MaximumAssignmentsPerEvaluation &&
               _queue.TryPeek(out var scheduled, out var priority) && priority.DueTicks <= now.Ticks)
        {
            _queue.Dequeue();
            if (!_scheduled.TryGetValue(scheduled.DispatchId, out var current) || current != scheduled.Due)
                continue;
            _scheduled.Remove(scheduled.DispatchId);
            if (_work.TryGetValue(scheduled.DispatchId, out var work) && !work.Preparing)
                Evaluate(work, policy, now);
            handled++;
        }
    }

    public void GetSnapshots(List<NCNcpdServiceDispatchAdapterSnapshot> result)
    {
        result.Clear();
        foreach (var work in _work.Values.OrderBy(static value => value.Dispatch.DispatchId.Value))
        {
            result.Add(new NCNcpdServiceDispatchAdapterSnapshot(
                work.Dispatch.DispatchId,
                work.Dispatch.IncidentId,
                work.Dispatch.Status,
                work.UnitId,
                work.CommandId,
                work.Status,
                work.Failure,
                work.UpdatedAt));
        }
    }

    public bool TryGetSnapshot(NCServiceDispatchId dispatchId, out NCNcpdServiceDispatchAdapterSnapshot snapshot)
    {
        if (_work.TryGetValue(dispatchId, out var work))
        {
            snapshot = new NCNcpdServiceDispatchAdapterSnapshot(
                work.Dispatch.DispatchId,
                work.Dispatch.IncidentId,
                work.Dispatch.Status,
                work.UnitId,
                work.CommandId,
                work.Status,
                work.Failure,
                work.UpdatedAt);
            return true;
        }

        snapshot = default!;
        return false;
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        Clear();
        _active = true;
        ValidatePolicy();
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (!args.WasModified<NCNcpdDispatchPolicyPrototype>())
            return;
        ValidatePolicy();
        ScheduleWaiting();
    }

    private void OnServiceDispatch(NCServiceDispatchPersistedEvent args)
    {
        var dispatch = args.Dispatch.Snapshot;
        if (!_active || dispatch.ServiceId != NcpdService || !_clock.TryGetCurrentTime(out _, out var now) ||
            !TryPolicy(out var policy))
            return;

        if (dispatch.Status is NCServiceDispatchStatus.Completed or NCServiceDispatchStatus.Cancelled or
            NCServiceDispatchStatus.Rejected)
        {
            FinishTerminal(dispatch.DispatchId, dispatch.Status);
            return;
        }

        if (dispatch.Status is not (NCServiceDispatchStatus.EnRoute or NCServiceDispatchStatus.OnScene))
            return;
        if (!_work.TryGetValue(dispatch.DispatchId, out var work))
        {
            if (_work.Count >= policy.MaximumTrackedDispatches)
            {
                Log.Error($"NCPD dispatch adapter capacity rejected {dispatch.DispatchId}.");
                return;
            }
            work = new DispatchWork(dispatch, now);
            work.RequiresInvestigator = _investigationIncidents.Contains(dispatch.IncidentId);
            _work.Add(dispatch.DispatchId, work);
        }
        else
        {
            work.Dispatch = dispatch;
            work.RequiresInvestigator |= _investigationIncidents.Contains(dispatch.IncidentId);
            work.UpdatedAt = now;
        }

        Schedule(dispatch.DispatchId, now);
        RecoverIncident(dispatch.IncidentId);
    }

    /// <summary>
    /// Dispatch aggregates are recovered independently from incident aggregates. Re-publishing the attached incident
    /// reconstructs investigation and custody adapters without inventing a new result or blocking the main thread.
    /// </summary>
    private void RecoverIncident(NCCityIncidentId incidentId)
    {
        if (!incidentId.IsValid || _recoveredIncidents.Contains(incidentId) ||
            !_incidentRecoveries.Add(incidentId))
        {
            return;
        }

        var generation = _generation;
        _ = RecoverIncidentAsync(generation, incidentId);
    }

    private async Task RecoverIncidentAsync(int generation, NCCityIncidentId incidentId)
    {
        NCCityIncidentRecord? incident = null;
        string? error = null;
        try
        {
            incident = await _incidents.GetAsync(incidentId, true);
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }

        _taskManager.RunOnMainThread(() =>
        {
            if (generation != _generation || !_active)
                return;
            _incidentRecoveries.Remove(incidentId);
            if (error != null || incident == null)
            {
                RecoveryFailureCount++;
                LastRecoveryError = error ?? $"Incident {incidentId} was not found during dispatch recovery.";
                return;
            }

            if (_recoveredIncidents.Add(incidentId))
            {
                RecoveredIncidentCount++;
                RaiseLocalEvent(new NCCityIncidentPersistedEvent(incident));
            }
        });
    }

    private void OnArrivalAttempt(NCServiceDispatchArrivalAttemptEvent args)
    {
        var dispatch = args.Dispatch.Snapshot;
        if (!_active || dispatch.ServiceId != NcpdService || !TryPolicy(out var policy))
            return;
        args.RetryAfter = policy.RetryDelay;
        if (!_work.TryGetValue(dispatch.DispatchId, out var work) || !work.UnitId.IsValid ||
            !_units.TryGetSnapshot(work.UnitId, out var unit))
        {
            args.Deferred = true;
            args.Reason = "No real NCPD patrol is assigned.";
            return;
        }

        var hasPhysicalMember = false;
        _arrivalActorBuffer.Clear();
        foreach (var actorId in unit.Members)
        {
            if (_runtime.TryGetEntity(actorId, out var entity) && Exists(entity))
            {
                hasPhysicalMember = true;
                continue;
            }
            if (!_actors.TryGetCached(actorId, out var actor) ||
                actor.Snapshot.Location.CurrentZone != dispatch.DestinationZone ||
                actor.Snapshot.Location.TravelPhase is not (NCCityActorTravelPhase.Stationary or
                    NCCityActorTravelPhase.Arrived))
            {
                args.Deferred = true;
                args.Reason = $"Abstract patrol member {actorId} has not completed city travel.";
                return;
            }
            _arrivalActorBuffer.Add(actorId);
        }

        if (!hasPhysicalMember)
            return;

        // A partially observed patrol cannot teleport the remaining members. Rehydrate them at their completed
        // off-screen destination, then let the ordinary leader/follower executor prove local arrival.
        foreach (var actorId in _arrivalActorBuffer)
            RequestMaterialization(dispatch, actorId, policy);
        if (!_commands.TryGetActive(work.UnitId, out var command) || command.CommandId != work.CommandId ||
            command.Status is not (NCNcpdCommandStatus.Executing or NCNcpdCommandStatus.Completed))
        {
            args.Deferred = true;
            args.Reason = "The physical patrol leader has not reached the incident zone.";
        }
    }

    private void OnIncidentPersisted(NCCityIncidentPersistedEvent args)
    {
        _recoveredIncidents.Add(args.Incident.Snapshot.IncidentId);
        if (args.Incident.Snapshot.Stage == NCCityIncidentStage.Investigation &&
            _clock.TryGetCurrentTime(out _, out var investigationNow) && TryPolicy(out _))
        {
            _investigationIncidents.Add(args.Incident.Snapshot.IncidentId);
            foreach (var work in _work.Values
                         .Where(value => value.Dispatch.IncidentId == args.Incident.Snapshot.IncidentId)
                         .ToArray())
            {
                work.RequiresInvestigator = true;
                if (work.UnitId.IsValid && IsInvestigativeUnit(work.UnitId))
                {
                    RaiseLocalEvent(new NCNcpdInvestigationUnitAssignedEvent(
                        work.Dispatch.IncidentId,
                        work.UnitId));
                    continue;
                }

                // The responder remains a valid physical officer, but the case now needs a role authorized for
                // investigation. Release only this round-local order and keep the canonical dispatch alive.
                if (work.CommandId.IsValid)
                    _commands.TryAdvanceExecution(work.CommandId, NCNcpdCommandStatus.Completed,
                        "Response handed over to an NCPD investigator.");
                work.UnitId = default;
                work.CommandId = default;
                work.Preparing = false;
                work.PreparationRevision++;
                work.Status = "awaiting-investigator";
                work.Failure = string.Empty;
                work.UpdatedAt = investigationNow;
                Schedule(work.Dispatch.DispatchId, investigationNow);
            }
            return;
        }

        if (args.Incident.Snapshot.Stage is not (NCCityIncidentStage.Resolved or NCCityIncidentStage.Closed or
            NCCityIncidentStage.Cancelled))
            return;

        _investigationIncidents.Remove(args.Incident.Snapshot.IncidentId);

        foreach (var work in _work.Values
                     .Where(value => value.Dispatch.IncidentId == args.Incident.Snapshot.IncidentId)
                     .ToArray())
        {
            if (work.CommandId.IsValid)
                _commands.TryAdvanceExecution(work.CommandId, NCNcpdCommandStatus.Completed,
                    "The persisted incident reached a terminal outcome.");
            _ = CompleteServiceDispatchAsync(work.Dispatch.DispatchId);
        }
    }

    private void OnUnitRemoved(NCNcpdUnitRemovedEvent args)
    {
        if (!_clock.TryGetCurrentTime(out _, out var now) || !TryPolicy(out var policy))
            return;
        foreach (var work in _work.Values.Where(value => value.UnitId == args.UnitId).ToArray())
        {
            work.UnitId = default;
            work.CommandId = default;
            work.Status = "awaiting-replacement-unit";
            work.Failure = "The assigned patrol dissolved before completing the response.";
            work.UpdatedAt = now;
            Schedule(work.Dispatch.DispatchId, now.Advance(policy.RetryDelay));
        }
    }

    private void OnCommandChanged(NCNcpdCommandChangedEvent args)
    {
        if (args.Status != NCNcpdCommandStatus.Failed || !_clock.TryGetCurrentTime(out _, out var now) ||
            !TryPolicy(out var policy))
            return;
        foreach (var work in _work.Values.Where(value => value.CommandId == args.CommandId).ToArray())
        {
            work.UnitId = default;
            work.CommandId = default;
            work.Status = "awaiting-replacement-unit";
            work.Failure = "The patrol could not physically execute the response route.";
            work.UpdatedAt = now;
            Schedule(work.Dispatch.DispatchId, now.Advance(policy.RetryDelay));
        }
    }

    private void Evaluate(DispatchWork work, NCNcpdDispatchPolicyPrototype policy, NCActiveWorldTime now)
    {
        if (!work.UnitId.IsValid)
        {
            if (!TrySelectUnit(work.Dispatch, policy, out var unit))
            {
                work.Status = "awaiting-eligible-unit";
                work.Failure = "No free patrol satisfies the incident severity, travel, and cadet supervision policy.";
                work.UpdatedAt = now;
                Schedule(work.Dispatch.DispatchId, now.Advance(policy.RetryDelay));
                return;
            }

            var issued = work.RequiresInvestigator
                ? _commands.TryIssueInvestigationDispatch(
                    work.Dispatch.DispatchId,
                    unit.UnitId,
                    work.Dispatch.DestinationZone,
                    Math.Clamp(work.Dispatch.ThreatBasisPoints / 10, 0, 1000),
                    out var commandId,
                    out var error)
                : _commands.TryIssueServiceDispatch(
                    work.Dispatch.DispatchId,
                    unit.UnitId,
                    work.Dispatch.DestinationZone,
                    Math.Clamp(work.Dispatch.ThreatBasisPoints / 10, 0, 1000),
                    out commandId,
                    out error);
            if (!issued)
            {
                work.Status = "assignment-rejected";
                work.Failure = error;
                work.UpdatedAt = now;
                Schedule(work.Dispatch.DispatchId, now.Advance(policy.RetryDelay));
                return;
            }

            work.UnitId = unit.UnitId;
            work.CommandId = commandId;
            work.Status = "unit-assigned";
            work.Failure = string.Empty;
            if (work.RequiresInvestigator)
                RaiseLocalEvent(new NCNcpdInvestigationUnitAssignedEvent(work.Dispatch.IncidentId, unit.UnitId));
        }

        if (!_units.TryGetSnapshot(work.UnitId, out var assigned))
        {
            work.UnitId = default;
            work.CommandId = default;
            Schedule(work.Dispatch.DispatchId, now.Advance(policy.RetryDelay));
            return;
        }

        work.Preparing = true;
        work.UpdatedAt = now;
        var generation = _generation;
        var preparationRevision = ++work.PreparationRevision;
        _ = PrepareActorsAsync(generation, preparationRevision, work.Dispatch, assigned, now);
    }

    private bool TrySelectUnit(
        NCServiceDispatchSnapshot dispatch,
        NCNcpdDispatchPolicyPrototype policy,
        out NCNcpdUnitSnapshot selected)
    {
        selected = default!;
        var severity = Math.Clamp(dispatch.ThreatBasisPoints / 10, 0, 1000);
        _units.GetSnapshots(_unitBuffer);
        var candidates = new List<(NCNcpdUnitSnapshot Unit, int Score)>();
        foreach (var unit in _unitBuffer)
        {
            if (_commands.TryGetActive(unit.UnitId, out _) ||
                !_workforce.TryGetRoleProfile(unit.LeaderActorId, out _, out var leader) ||
                WorkRequiresInvestigator(dispatch) && (!leader.CanInvestigate ||
                    !leader.AllowedAssignments.Contains(NCNcpdAssignmentKind.Investigation)) ||
                !WorkRequiresInvestigator(dispatch) &&
                    !leader.AllowedAssignments.Contains(NCNcpdAssignmentKind.Response) ||
                severity > leader.MaximumIncidentSeverity || !leader.TravelModes.Contains(dispatch.TravelMode))
                continue;

            var eligible = true;
            var hasCadet = false;
            foreach (var member in unit.Members)
            {
                if (!_workforce.TryGetRoleProfile(member, out _, out var role) ||
                    WorkRequiresInvestigator(dispatch) && (!role.CanInvestigate ||
                        !role.AllowedAssignments.Contains(NCNcpdAssignmentKind.Investigation)) ||
                    !WorkRequiresInvestigator(dispatch) &&
                        !role.AllowedAssignments.Contains(NCNcpdAssignmentKind.Response) ||
                    !role.TravelModes.Contains(dispatch.TravelMode))
                {
                    eligible = false;
                    break;
                }
                hasCadet |= role.OfficerRole == NCNcpdOfficerRole.Cadet;
            }
            if (!eligible || hasCadet && severity > policy.CadetMaximumSeverity)
                continue;

            var score = severity >= policy.InspectorMinimumSeverity && leader.CanInvestigate ? 0 : 1000;
            score += Math.Abs(leader.MaximumIncidentSeverity - severity);
            candidates.Add((unit, score));
        }

        if (candidates.Count == 0)
            return false;
        selected = candidates.OrderBy(static value => value.Score)
            .ThenBy(static value => value.Unit.UnitId.Value)
            .First().Unit;
        return true;
    }

    private async Task PrepareActorsAsync(
        int generation,
        int preparationRevision,
        NCServiceDispatchSnapshot dispatch,
        NCNcpdUnitSnapshot unit,
        NCActiveWorldTime now)
    {
        var ready = new List<NCActorId>(unit.Members.Length);
        string? failure = null;
        foreach (var actorId in unit.Members)
        {
            if (_runtime.TryGetEntity(actorId, out var entity) && Exists(entity))
                continue;
            var result = await SetResponseLocationAsync(dispatch, actorId, now);
            if (result == null)
            {
                failure = $"Actor {actorId} could not enter the persisted service-response route.";
                break;
            }
            if (dispatch.Status == NCServiceDispatchStatus.OnScene)
                ready.Add(actorId);
        }

        _taskManager.RunOnMainThread(() => CompletePreparation(
            generation,
            preparationRevision,
            dispatch.DispatchId,
            ready,
            failure));
    }

    private async Task<NCCityActorRecord?> SetResponseLocationAsync(
        NCServiceDispatchSnapshot dispatch,
        NCActorId actorId,
        NCActiveWorldTime now)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var actor = await _actors.GetAsync(actorId, true);
            if (actor == null)
                return null;
            var onScene = dispatch.Status == NCServiceDispatchStatus.OnScene;
            var expectedCurrent = onScene ? dispatch.DestinationZone : actor.Snapshot.Location.CurrentZone;
            var expectedDestination = onScene ? default : dispatch.DestinationZone;
            var expectedPhase = onScene ? NCCityActorTravelPhase.Stationary : NCCityActorTravelPhase.Planned;
            if (actor.Snapshot.Location.CurrentZone == expectedCurrent &&
                actor.Snapshot.Location.DestinationZone == expectedDestination &&
                actor.Snapshot.Location.TravelPhase == expectedPhase)
                return actor;

            var stable = $"{dispatch.DispatchId}.{actorId}.{dispatch.Status}.{actor.AggregateVersion}";
            var result = await _actors.SetLocationAsync(new NCCityActorLocationMutationRequest(
                NCPersistentIdentity.Command("ncpd-service", "route-actor", stable),
                actorId,
                actor.AggregateVersion,
                expectedCurrent,
                expectedDestination,
                expectedPhase,
                onScene ? NCCityTravelMode.None : dispatch.TravelMode,
                onScene ? null : dispatch.ExpectedArrivalAt,
                "service-response",
                dispatch.IncidentId.ToString(),
                NCCityActorAvailability.Busy,
                onScene ? now.Advance(TimeSpan.FromMinutes(1)) : dispatch.ExpectedArrivalAt ?? now,
                NCPersistentIdentity.Correlation("ncpd-service", "route-actor", stable),
                dispatch.LastAppliedEventId,
                "ncpd-service-dispatch",
                NCCityActorPresenceMode.Public));
            if (result.Status is NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate)
                return result.Record ?? await _actors.GetAsync(actorId, true);
            if (result.Status != NCCityActorMutationStatus.VersionConflict)
                return null;
        }
        return null;
    }

    private void CompletePreparation(
        int generation,
        int preparationRevision,
        NCServiceDispatchId dispatchId,
        IReadOnlyList<NCActorId> ready,
        string? failure)
    {
        if (generation != _generation || !_work.TryGetValue(dispatchId, out var work) ||
            work.PreparationRevision != preparationRevision ||
            !_clock.TryGetCurrentTime(out _, out var now) || !TryPolicy(out var policy))
            return;

        work.Preparing = false;
        work.UpdatedAt = now;
        if (failure != null)
        {
            work.Status = "route-preparation-failed";
            work.Failure = failure;
            Schedule(dispatchId, now.Advance(policy.RetryDelay));
            return;
        }

        if (work.Dispatch.Status == NCServiceDispatchStatus.OnScene)
        {
            if (_occupancy.IsPlayerObserved(work.Dispatch.DestinationZone, out _))
            {
                foreach (var actorId in ready)
                    RequestMaterialization(work.Dispatch, actorId, policy);
                work.Status = "on-scene-materialization-requested";
            }
            else
            {
                // Keep the same persistent assignment alive without spawning an unseen patrol. A bounded policy
                // poll rehydrates the unit if a player reaches the scene before abstract resolution completes.
                work.Status = "on-scene-abstract";
                Schedule(dispatchId, now.Advance(policy.RetryDelay));
            }
        }
        else
        {
            work.Status = "unit-en-route";
        }
        work.Failure = string.Empty;
    }

    private void RequestMaterialization(
        NCServiceDispatchSnapshot dispatch,
        NCActorId actorId,
        NCNcpdDispatchPolicyPrototype policy)
    {
        var key = (dispatch.DispatchId, actorId);
        if (_runtime.TryGetEntity(actorId, out var entity) && Exists(entity))
        {
            RetainPhysicalDispatchActor(dispatch.DispatchId, actorId, entity);
            return;
        }
        if (_materializationRequests.TryGetValue(key, out var existing) &&
            _physicalActors.TryGetRequest(existing, out var request) && request.Status is not
                NCPhysicalActorSpawnStatus.Rejected)
            return;

        var requestId = Guid.NewGuid();
        _materializationRequests[key] = requestId;
        _physicalActors.Request(new NCPhysicalActorSpawnRequest(
            requestId,
            actorId,
            NCPhysicalActorSpawnReason.ServiceDispatch,
            "ncpd-service-dispatch"));
    }

    private void OnActorRehydrated(NCPhysicalActorSpawnedEvent args)
    {
        foreach (var (key, requestId) in _materializationRequests)
        {
            if (requestId != args.RequestId || key.Item2 != args.ActorId)
                continue;
            RetainPhysicalDispatchActor(key.Item1, args.ActorId, args.EntityUid);
            return;
        }
    }

    private void RetainPhysicalDispatchActor(
        NCServiceDispatchId dispatchId,
        NCActorId actorId,
        EntityUid entityUid)
    {
        if (!_physicalDispatchActors.Add((dispatchId, actorId)))
            return;
    }

    private void ReleasePhysicalDispatchActors(NCServiceDispatchId dispatchId)
    {
        var actors = _physicalDispatchActors
            .Where(value => value.DispatchId == dispatchId)
            .Select(value => value.ActorId)
            .ToArray();
        foreach (var actorId in actors)
        {
            _physicalDispatchActors.Remove((dispatchId, actorId));
            // The officer remains physical after the dispatch; only dispatch ownership is released.
        }
    }

    private async Task CompleteServiceDispatchAsync(NCServiceDispatchId dispatchId)
    {
        var record = await _services.GetDispatchAsync(dispatchId, true);
        if (record?.Snapshot.Status != NCServiceDispatchStatus.OnScene)
            return;
        await _services.CompleteDispatchAsync(dispatchId, record.AggregateVersion,
            NCPartyRef.ForCityService(record.Snapshot.ServiceId));
    }

    private void FinishTerminal(NCServiceDispatchId dispatchId, NCServiceDispatchStatus status)
    {
        _scheduled.Remove(dispatchId);
        ReleasePhysicalDispatchActors(dispatchId);
        if (_work.Remove(dispatchId, out var work) && work.CommandId.IsValid &&
            status == NCServiceDispatchStatus.Completed)
            _commands.TryAdvanceExecution(work.CommandId, NCNcpdCommandStatus.Completed,
                "The city-service dispatch was completed.");
        foreach (var key in _materializationRequests.Keys.Where(key => key.Item1 == dispatchId).ToArray())
            _materializationRequests.Remove(key);
    }

    private void ScheduleWaiting()
    {
        if (!_clock.TryGetCurrentTime(out _, out var now))
            return;
        foreach (var work in _work.Values.Where(static value => !value.UnitId.IsValid && !value.Preparing))
            Schedule(work.Dispatch.DispatchId, now);
    }

    private void Schedule(NCServiceDispatchId dispatchId, NCActiveWorldTime due)
    {
        if (!_active || !_work.ContainsKey(dispatchId))
            return;
        _scheduled[dispatchId] = due;
        _queue.Enqueue(new ScheduledDispatch(dispatchId, due), new DispatchPriority(due.Ticks, _sequence++));
    }

    private bool TryPolicy(out NCNcpdDispatchPolicyPrototype policy)
    {
        if (_prototypes.TryIndex(DefaultPolicy, out NCNcpdDispatchPolicyPrototype? resolved) && resolved != null &&
            resolved.MaximumAssignmentsPerEvaluation > 0 && resolved.MaximumTrackedDispatches > 0 &&
            resolved.RetryDelay > TimeSpan.Zero && resolved.MaterializationMaximumWait > TimeSpan.Zero)
        {
            policy = resolved;
            ConfigurationError = null;
            return true;
        }
        policy = default!;
        ConfigurationError = $"NCPD dispatch policy {DefaultPolicy} is missing or invalid.";
        return false;
    }

    private void ValidatePolicy() => TryPolicy(out _);

    private bool IsInvestigativeUnit(NCNcpdUnitId unitId) =>
        _units.TryGetSnapshot(unitId, out var unit) &&
        _workforce.TryGetRoleProfile(unit.LeaderActorId, out _, out var role) &&
        role.CanInvestigate && role.AllowedAssignments.Contains(NCNcpdAssignmentKind.Investigation);

    private bool WorkRequiresInvestigator(NCServiceDispatchSnapshot dispatch) =>
        _work.TryGetValue(dispatch.DispatchId, out var work) && work.RequiresInvestigator;

    private void Clear()
    {
        _active = false;
        _generation++;
        foreach (var dispatchId in _physicalDispatchActors.Select(value => value.DispatchId).Distinct().ToArray())
            ReleasePhysicalDispatchActors(dispatchId);
        _work.Clear();
        _scheduled.Clear();
        _materializationRequests.Clear();
        _investigationIncidents.Clear();
        _recoveredIncidents.Clear();
        _incidentRecoveries.Clear();
        _queue.Clear();
        RecoveredIncidentCount = 0;
        RecoveryFailureCount = 0;
        LastRecoveryError = null;
    }

    private sealed class DispatchWork(NCServiceDispatchSnapshot dispatch, NCActiveWorldTime updatedAt)
    {
        public NCServiceDispatchSnapshot Dispatch = dispatch;
        public NCNcpdUnitId UnitId;
        public NCNcpdCommandId CommandId;
        public string Status = "awaiting-eligible-unit";
        public string Failure = string.Empty;
        public NCActiveWorldTime UpdatedAt = updatedAt;
        public bool Preparing;
        public bool RequiresInvestigator;
        public int PreparationRevision;
    }

    private readonly record struct ScheduledDispatch(NCServiceDispatchId DispatchId, NCActiveWorldTime Due);
    private readonly record struct DispatchPriority(long DueTicks, long Sequence) : IComparable<DispatchPriority>
    {
        public int CompareTo(DispatchPriority other)
        {
            var due = DueTicks.CompareTo(other.DueTicks);
            return due != 0 ? due : Sequence.CompareTo(other.Sequence);
        }
    }
}
