// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Server.GameTicking;
using Content.Server._NC.City.Connectivity;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Groups;
using Content.Server._NC.LivingCity.Groups.Components;
using Content.Server._NC.LivingCity.Simulation;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Actors;

/// <summary>
/// Bounded physical executor for materialized persistent actors. It owns only the high-level waypoint lifecycle and
/// delegates every local movement segment to the unchanged Robust NPC steering/pathfinding systems.
/// </summary>
public sealed partial class NCCityPhysicalExecutionSystem : EntitySystem
{
    private const string DefaultPolicyId = "NCDefaultCityPhysicalExecutionPolicy";
    private const int MaximumDiagnosticLength = 192;

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ITaskManager _taskManager = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCAILoadGovernorSystem _loadGovernor = default!;
    [Dependency] private NCCityConnectivitySystem _city = default!;
    [Dependency] private NCRoomPortalGraphSystem _rooms = default!;

    private readonly PriorityQueue<QueuedIntentLoad, IntentLoadPriority> _intentLoads = new();
    private readonly Dictionary<NCActorId, QueuedIntentLoad> _queuedIntentLoads = [];
    private readonly PriorityQueue<QueuedExecution, ExecutionPriority> _executionQueue = new();
    private readonly Dictionary<EntityUid, NCActiveWorldTime> _scheduledExecutions = [];
    private readonly HashSet<EntityUid> _tracked = [];
    private readonly Dictionary<NCActorId, PendingLocationMutation> _pendingMutations = [];

    private NCCityPhysicalExecutionPolicyPrototype? _policy;
    private NCSessionEpoch _epoch;
    private int _generation;
    private int _intentLoadsInFlight;
    private int _mutationsInFlight;
    private long _sequence;
    private long _plansBuilt;
    private long _waypointsCompleted;
    private long _replans;
    private long _portalTraversals;
    private long _arrivals;
    private long _blockedGoals;
    private long _failedGoals;
    private long _mutationRetriesExhausted;
    private bool _active;
    private string? _configurationError;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnSessionClosing);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(OnSessionClosed);
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(OnClockFailed);
        SubscribeLocalEvent<NCCityActorRehydratedEvent>(OnActorRehydrated);
        SubscribeLocalEvent<NCCityActorPersistedEvent>(OnActorPersisted);
        SubscribeLocalEvent<NCCityActorMaterializationChangedEvent>(OnMaterializationChanged);
        SubscribeLocalEvent<NCCityPhysicalExecutionComponent, ComponentShutdown>(OnExecutionShutdown);
        SubscribeLocalEvent<NCCityConnectivityChangedEvent>(OnCityGraphChanged);
        SubscribeLocalEvent<NCRoomPortalGraphChangedEvent>(OnRoomGraphChanged);
        SubscribeLocalEvent<NCCitySpawnCandidateIndexChangedEvent>(OnSpawnCandidatesChanged);
        SubscribeLocalEvent<NCCityGroupTrunkRouteInvalidatedEvent>(OnGroupTrunkInvalidated);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_active || _policy == null ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
        {
            return;
        }

        using var work = _loadGovernor.MeasureWork(NCAILoadWorkKind.PhysicalExecution);
        DispatchIntentLoads(now);
        DispatchExecutions(now);
        _loadGovernor.ReportQueue(
            NCAILoadQueueKind.PhysicalExecution,
            _queuedIntentLoads.Count + _scheduledExecutions.Count,
            _policy.MaximumQueuedIntentLoads + Math.Max(1, _tracked.Count));
        _loadGovernor.ReportBacklog(NCAILoadBacklogKind.PhysicalExecution, HasDueExecution(now));
    }

    /// <summary>
    /// Reactive gameplay layers use this typed boundary to suspend movement without replacing persistent intent.
    /// </summary>
    public bool SetInterrupt(EntityUid entityUid, NCCityPhysicalInterruptFlags flag, bool enabled)
    {
        if (flag == NCCityPhysicalInterruptFlags.None ||
            !TryComp<NCCityPhysicalExecutionComponent>(entityUid, out var component))
        {
            return false;
        }

        component.Interrupts = enabled ? component.Interrupts | flag : component.Interrupts & ~flag;
        if (_clock.TryGetCurrentTime(out var epoch, out var now) && epoch == _epoch)
            ScheduleExecution(entityUid, now);
        return true;
    }

    public bool TryGetSnapshot(NCActorId actorId, out NCCityPhysicalExecutionSnapshot snapshot)
    {
        snapshot = default!;
        if (!_runtime.TryGetEntity(actorId, out var entityUid) ||
            !TryComp<NCCityPhysicalExecutionComponent>(entityUid, out var component))
        {
            return false;
        }

        snapshot = new NCCityPhysicalExecutionSnapshot(
            actorId,
            entityUid,
            component.GoalId,
            component.ActorVersion,
            component.State,
            component.Mode,
            component.BehaviorOwner,
            component.Interrupts,
            component.DestinationZone,
            component.Destination,
            component.Activity,
            component.Goal,
            component.WaypointIndex,
            component.Waypoints.Count,
            component.ReplanAttempts,
            component.RejectedRoomPortals.Count,
            component.RejectedCityPortals.Count,
            component.PlanVersion,
            component.LastFailure,
            component.Trace.ToArray());
        return true;
    }

    public NCCityPhysicalExecutionCounters GetCounters() => new(
        _active,
        _tracked.Count,
        _scheduledExecutions.Count,
        _queuedIntentLoads.Count,
        _intentLoadsInFlight,
        _mutationsInFlight,
        _plansBuilt,
        _waypointsCompleted,
        _replans,
        _portalTraversals,
        _arrivals,
        _blockedGoals,
        _failedGoals,
        _mutationRetriesExhausted,
        _configurationError);

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        Reset();
        if (!_prototypes.TryIndex(
                new ProtoId<NCCityPhysicalExecutionPolicyPrototype>(DefaultPolicyId),
                out NCCityPhysicalExecutionPolicyPrototype? policy) ||
            policy == null || !ValidatePolicy(policy))
        {
            _configurationError = $"Physical execution policy {DefaultPolicyId} is missing or invalid.";
            Log.Error(_configurationError);
            return;
        }

        _policy = policy;
        _epoch = args.Epoch;
        _active = true;
    }

    private void OnSessionClosed(NCActiveWorldSessionClosedEvent args)
    {
        if (_active && args.Epoch == _epoch)
            Reset();
    }

    private void OnSessionClosing(NCActiveWorldSessionClosingEvent args)
    {
        if (_active && args.Epoch == _epoch)
            Reset();
    }

    private void OnClockFailed(NCActiveWorldClockFailedEvent args)
    {
        if (_active && args.Epoch == _epoch)
            Reset();
    }

    private void OnActorRehydrated(NCCityActorRehydratedEvent args)
    {
        if (!_active || _policy == null || !Exists(args.EntityUid))
            return;
        TrackAndQueueIntent(args.ActorId, args.EntityUid, 0);
    }

    private void OnActorPersisted(NCCityActorPersistedEvent args)
    {
        if (!_active || args.SessionEpoch != _epoch ||
            !_runtime.TryGetEntity(args.ActorId, out var entityUid) || !Exists(entityUid))
        {
            return;
        }

        TrackAndQueueIntent(args.ActorId, entityUid, args.AggregateVersion);
    }

    private void OnMaterializationChanged(NCCityActorMaterializationChangedEvent args)
    {
        if (!args.Materialized)
            CleanupEntity(args.EntityUid, true);
    }

    private void OnExecutionShutdown(
        Entity<NCCityPhysicalExecutionComponent> entity,
        ref ComponentShutdown args)
    {
        CleanupEntity(entity.Owner, false);
    }

    private void OnCityGraphChanged(ref NCCityConnectivityChangedEvent args)
    {
        WakeForNavigationTopologyChange("city-graph-changed");
    }

    private void OnRoomGraphChanged(ref NCRoomPortalGraphChangedEvent args)
    {
        WakeForNavigationTopologyChange("room-graph-changed");
    }

    private void OnGroupTrunkInvalidated(NCCityGroupTrunkRouteInvalidatedEvent args)
    {
        if (!_active || !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
            return;

        // This visits only currently materialized actors. It never scans the persistent city catalog.
        foreach (var entityUid in _tracked)
        {
            // The actor that rejected the trunk already owns its configured backoff. Waking it here would erase that
            // delay, while the remaining members must stop following the now-invalid shared route immediately.
            if (entityUid == args.SourceActor ||
                !TryComp<NCCityGroupFormationComponent>(entityUid, out var formation) ||
                formation.GroupId != args.GroupId ||
                !TryComp<NCCityPhysicalExecutionComponent>(entityUid, out var execution) ||
                execution.State is NCCityPhysicalExecutionState.Idle or NCCityPhysicalExecutionState.LoadingIntent)
            {
                continue;
            }

            StopSteering(entityUid, execution);
            execution.State = NCCityPhysicalExecutionState.Replanning;
            AddTrace(execution, now, "group-trunk-invalidated", args.Reason);
            ScheduleExecution(entityUid, now);
        }
    }

    private void OnSpawnCandidatesChanged(ref NCCitySpawnCandidateIndexChangedEvent args)
    {
        WakeForDestinationIndexChange(args.NewVersion);
    }

    private void WakeForNavigationTopologyChange(string reason)
    {
        if (!_active || _policy == null ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
            return;

        var topologyVersion = new NCCityNavigationPlanVersion(_city.GraphVersion, _rooms.GraphVersion);
        // This visits only the materialized execution set, never the city actor catalog.
        foreach (var entityUid in _tracked)
        {
            if (!TryComp<NCCityPhysicalExecutionComponent>(entityUid, out var component) ||
                component.State is NCCityPhysicalExecutionState.Idle or NCCityPhysicalExecutionState.LoadingIntent)
            {
                continue;
            }
            if (component.PlanVersion == topologyVersion || component.LastReplanTopologyVersion == topologyVersion)
                continue;
            WakeForTopologyChange(entityUid, component, now, topologyVersion, reason);
        }
    }

    private void WakeForDestinationIndexChange(long destinationIndexVersion)
    {
        if (!_active || _policy == null ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
            return;

        var topologyVersion = new NCCityNavigationPlanVersion(_city.GraphVersion, _rooms.GraphVersion);
        foreach (var entityUid in _tracked)
        {
            if (!TryComp<NCCityPhysicalExecutionComponent>(entityUid, out var component) ||
                component.State is NCCityPhysicalExecutionState.Idle or NCCityPhysicalExecutionState.LoadingIntent ||
                component.DestinationIndexVersion == destinationIndexVersion)
            {
                continue;
            }

            component.DestinationIndexVersion = destinationIndexVersion;
            if (!TryRefreshDestination(component))
            {
                FailGoal(entityUid, component, now, "destination-zone-has-no-compatible-navigation-target");
                continue;
            }
            WakeForTopologyChange(entityUid, component, now, topologyVersion, "destination-index-changed");
        }
    }

    /// <summary>
    /// Coalesces structural graph/index bursts before any actor asks vanilla pathfinding for a replacement route.
    /// Repeated events for an already observed version cannot reset the same route indefinitely.
    /// </summary>
    private void WakeForTopologyChange(
        EntityUid entityUid,
        NCCityPhysicalExecutionComponent component,
        NCActiveWorldTime now,
        NCCityNavigationPlanVersion topologyVersion,
        string reason)
    {
        if (_policy == null)
            return;
        component.ReplanAttempts = 0;
        component.LastFailure = reason;
        component.LastReplanAt = now;
        component.LastReplanReason = reason;
        component.LastReplanTopologyVersion = topologyVersion;
        component.State = NCCityPhysicalExecutionState.Replanning;
        ClearRejectedPortals(component);
        StopSteering(entityUid, component);
        AddTrace(component, now, reason, reason);

        // Replace an earlier movement tick with one delayed topology rebuild. Stale priority entries are discarded
        // by TryDequeueDueExecution because their due time no longer matches this authoritative schedule.
        _scheduledExecutions.Remove(entityUid);
        ScheduleExecution(entityUid, now.Advance(_policy.TopologyChangeCoalesceDelay));
    }

    private void TrackAndQueueIntent(NCActorId actorId, EntityUid entityUid, long minimumVersion)
    {
        if (!_active || _policy == null || !actorId.IsValid || !Exists(entityUid))
            return;

        var component = EnsureComp<NCCityPhysicalExecutionComponent>(entityUid);
        component.ActorId = actorId;
        if (component.State == NCCityPhysicalExecutionState.Idle || component.GoalId == Guid.Empty)
            component.State = NCCityPhysicalExecutionState.LoadingIntent;
        _tracked.Add(entityUid);

        if (!_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
            return;
        QueueIntentLoad(actorId, entityUid, minimumVersion, now);
    }

    private void QueueIntentLoad(
        NCActorId actorId,
        EntityUid entityUid,
        long minimumVersion,
        NCActiveWorldTime dueAt)
    {
        if (_policy == null)
            return;
        if (_queuedIntentLoads.TryGetValue(actorId, out var existing) &&
            existing.MinimumVersion >= minimumVersion && existing.DueAt <= dueAt)
        {
            return;
        }

        // Queue depth is intentionally not a population cap. The load governor limits dispatch throughput.
        var queued = new QueuedIntentLoad(actorId, entityUid, minimumVersion, dueAt, ++_sequence);
        _queuedIntentLoads[actorId] = queued;
        _intentLoads.Enqueue(queued, new IntentLoadPriority(dueAt.Ticks, queued.Sequence));
    }

    private void DispatchIntentLoads(NCActiveWorldTime now)
    {
        if (_policy == null)
            return;
        var limit = _loadGovernor.TakeDispatchLimit(
            NCAILoadDispatchKind.PhysicalIntentLoad,
            _policy.MaximumIntentLoadsPerTick);
        var dispatched = 0;
        while (dispatched < limit && _intentLoadsInFlight < _policy.MaximumConcurrentIntentLoads &&
               TryDequeueDueIntentLoad(now, out var queued))
        {
            _intentLoadsInFlight++;
            dispatched++;
            _ = ObserveIntentLoad(_generation, queued);
        }
    }

    private bool TryDequeueDueIntentLoad(NCActiveWorldTime now, out QueuedIntentLoad queued)
    {
        while (_intentLoads.TryPeek(out var candidate, out var priority))
        {
            if (!_queuedIntentLoads.TryGetValue(candidate.ActorId, out var current) ||
                current.Sequence != candidate.Sequence)
            {
                _intentLoads.Dequeue();
                continue;
            }
            if (priority.DueTicks > now.Ticks)
                break;
            _intentLoads.Dequeue();
            _queuedIntentLoads.Remove(candidate.ActorId);
            queued = candidate;
            return true;
        }

        queued = default;
        return false;
    }

    private async Task ObserveIntentLoad(int generation, QueuedIntentLoad queued)
    {
        NCCityActorRecord? actor = null;
        Exception? error = null;
        try
        {
            actor = await _actors.GetAsync(queued.ActorId, true);
        }
        catch (Exception exception)
        {
            error = exception;
        }

        _taskManager.RunOnMainThread(() => CompleteIntentLoad(generation, queued, actor, error));
    }

    private void CompleteIntentLoad(
        int generation,
        QueuedIntentLoad queued,
        NCCityActorRecord? actor,
        Exception? error)
    {
        if (generation != _generation)
            return;
        _intentLoadsInFlight = Math.Max(0, _intentLoadsInFlight - 1);
        if (!_active || _policy == null ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch ||
            !Exists(queued.EntityUid) ||
            !_runtime.TryGetEntity(queued.ActorId, out var bound) || bound != queued.EntityUid ||
            !TryComp<NCCityPhysicalExecutionComponent>(queued.EntityUid, out var component))
        {
            return;
        }

        if (error != null)
        {
            component.State = NCCityPhysicalExecutionState.LoadingIntent;
            component.LastFailure = TrimDiagnostic(error.Message);
            AddTrace(component, now, "intent-load-retry", component.LastFailure);
            QueueIntentLoad(
                queued.ActorId,
                queued.EntityUid,
                queued.MinimumVersion,
                now.Advance(_policy.IntentLoadRetryDelay));
            return;
        }
        if (actor == null || actor.AggregateVersion < queued.MinimumVersion)
        {
            component.State = NCCityPhysicalExecutionState.Failed;
            component.LastFailure = actor == null ? "persistent-actor-not-found" : "stale-actor-version-loaded";
            AddTrace(component, now, "intent-load-failed", component.LastFailure);
            _failedGoals++;
            return;
        }

        ApplyIntent(queued.EntityUid, component, actor, now);
    }

    private void DispatchExecutions(NCActiveWorldTime now)
    {
        if (_policy == null)
            return;
        var limit = _loadGovernor.TakeDispatchLimit(
            NCAILoadDispatchKind.PhysicalExecutionUpdate,
            _policy.MaximumExecutionUpdatesPerTick);
        var dispatched = 0;
        while (dispatched < limit && TryDequeueDueExecution(now, out var entityUid))
        {
            dispatched++;
            ProcessExecution(entityUid, now);
        }
    }

    private bool TryDequeueDueExecution(NCActiveWorldTime now, out EntityUid entityUid)
    {
        while (_executionQueue.TryPeek(out var queued, out var priority))
        {
            if (!_scheduledExecutions.TryGetValue(queued.EntityUid, out var dueAt) ||
                dueAt != queued.DueAt)
            {
                _executionQueue.Dequeue();
                continue;
            }
            if (priority.DueTicks > now.Ticks)
                break;
            _executionQueue.Dequeue();
            _scheduledExecutions.Remove(queued.EntityUid);
            entityUid = queued.EntityUid;
            return true;
        }

        entityUid = EntityUid.Invalid;
        return false;
    }

    private bool HasDueExecution(NCActiveWorldTime now)
    {
        while (_executionQueue.TryPeek(out var queued, out var priority))
        {
            if (!_scheduledExecutions.TryGetValue(queued.EntityUid, out var dueAt) || dueAt != queued.DueAt)
            {
                _executionQueue.Dequeue();
                continue;
            }
            return priority.DueTicks <= now.Ticks;
        }
        return false;
    }

    private void ScheduleExecution(EntityUid entityUid, NCActiveWorldTime dueAt)
    {
        if (!_active || !Exists(entityUid))
            return;
        if (_scheduledExecutions.TryGetValue(entityUid, out var existing) && existing <= dueAt)
            return;
        _scheduledExecutions[entityUid] = dueAt;
        var queued = new QueuedExecution(entityUid, dueAt, ++_sequence);
        _executionQueue.Enqueue(queued, new ExecutionPriority(dueAt.Ticks, queued.Sequence));
    }

    private void ScheduleNext(EntityUid entityUid, NCActiveWorldTime now)
    {
        if (_policy == null)
            return;
        ScheduleExecution(entityUid, now.Advance(_loadGovernor.ScaleEvaluationInterval(_policy.EvaluationInterval)));
    }

    private void Reset()
    {
        _active = false;
        _generation++;
        foreach (var entityUid in _tracked.ToArray())
            CleanupEntity(entityUid, true);
        _policy = null;
        _epoch = default;
        _intentLoads.Clear();
        _queuedIntentLoads.Clear();
        _executionQueue.Clear();
        _scheduledExecutions.Clear();
        _tracked.Clear();
        _pendingMutations.Clear();
        _intentLoadsInFlight = 0;
        _mutationsInFlight = 0;
        _sequence = 0;
        _plansBuilt = 0;
        _waypointsCompleted = 0;
        _replans = 0;
        _portalTraversals = 0;
        _arrivals = 0;
        _blockedGoals = 0;
        _failedGoals = 0;
        _mutationRetriesExhausted = 0;
        _configurationError = null;
        _loadGovernor.ReportBacklog(NCAILoadBacklogKind.PhysicalExecution, false);
        _loadGovernor.ReportQueue(NCAILoadQueueKind.PhysicalExecution, 0, 1);
    }

    private static bool ValidatePolicy(NCCityPhysicalExecutionPolicyPrototype policy) =>
        policy.MaximumQueuedIntentLoads > 0 &&
        policy.MaximumIntentLoadsPerTick > 0 &&
        policy.MaximumConcurrentIntentLoads > 0 &&
        policy.MaximumExecutionUpdatesPerTick > 0 &&
        policy.MaximumConcurrentMutations > 0 &&
        policy.MaximumMutationAttempts is >= 1 and <= 32 &&
        policy.MaximumWaypoints > 0 &&
        policy.MaximumImmediateWaypointCompletions > 0 &&
        policy.MaximumImmediateWaypointCompletions <= policy.MaximumWaypoints &&
        policy.MaximumReplans >= 0 &&
        policy.ReplanBackoff.Ticks <= TimeSpan.MaxValue.Ticks / Math.Max(1, policy.MaximumReplans) &&
        policy.MaximumTraceEntries > 0 &&
        policy.EvaluationInterval > TimeSpan.Zero &&
        policy.IntentLoadRetryDelay > TimeSpan.Zero &&
        policy.WaypointTimeout > policy.EvaluationInterval &&
        policy.PortalTraversalTimeout > policy.EvaluationInterval &&
        policy.PortalInteractionRetryInterval > TimeSpan.Zero &&
        policy.ReplanBackoff > TimeSpan.Zero &&
        policy.MinimumEquivalentReplanInterval >= policy.ReplanBackoff &&
        policy.TopologyChangeCoalesceDelay >= TimeSpan.Zero &&
        policy.BlockedRetryInterval > TimeSpan.Zero &&
        policy.MutationRetryDelay > TimeSpan.Zero &&
        policy.HtnResumeCooldown >= TimeSpan.Zero &&
        float.IsFinite(policy.WaypointReachRange) && policy.WaypointReachRange > 0f;

    private static string TrimDiagnostic(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unspecified";
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized[..Math.Min(normalized.Length, MaximumDiagnosticLength)];
    }

    private readonly record struct QueuedIntentLoad(
        NCActorId ActorId,
        EntityUid EntityUid,
        long MinimumVersion,
        NCActiveWorldTime DueAt,
        long Sequence);

    private readonly record struct IntentLoadPriority(long DueTicks, long Sequence) : IComparable<IntentLoadPriority>
    {
        public int CompareTo(IntentLoadPriority other)
        {
            var due = DueTicks.CompareTo(other.DueTicks);
            return due != 0 ? due : Sequence.CompareTo(other.Sequence);
        }
    }

    private readonly record struct QueuedExecution(EntityUid EntityUid, NCActiveWorldTime DueAt, long Sequence);

    private readonly record struct ExecutionPriority(long DueTicks, long Sequence) : IComparable<ExecutionPriority>
    {
        public int CompareTo(ExecutionPriority other)
        {
            var due = DueTicks.CompareTo(other.DueTicks);
            return due != 0 ? due : Sequence.CompareTo(other.Sequence);
        }
    }
}
