// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Cognition.Components;
using Content.Server._NC.LivingCity.Employment;
using Content.Server._NC.LivingCity.Simulation;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Cognition;

/// <summary>
/// Event-driven active-round cognition scheduler. Update touches only bounded due queues, planning frontiers and
/// invalidations; it never scans all persistent actors or all ECS entities.
/// </summary>
public sealed partial class NCCognitionSystem : EntitySystem
{
    private const string DefaultPolicyId = "NCDefaultCognitionRuntimePolicy";
    private const int CatalogPageSize = 250;

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ITaskManager _taskManager = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCCognitionRegistrySystem _registry = default!;
    [Dependency] private NCCognitionCapabilityProviderSystem _providers = default!;
    [Dependency] private NCProfessionProfileSystem _professions = default!;
    [Dependency] private NCAILoadGovernorSystem _governor = default!;

    private readonly PriorityQueue<ScheduledActor, SchedulePriority> _decisionQueue = new();
    private readonly Dictionary<NCActorId, NCActiveWorldTime> _scheduledDecisions = [];
    private readonly Dictionary<NCActorId, RuntimeState> _states = [];
    private readonly HashSet<NCActorId> _inFlight = [];
    private readonly Queue<NCActorId> _planningQueue = [];
    private readonly HashSet<NCActorId> _planningSet = [];
    private readonly Dictionary<NCActorId, NCCognitionMctsFrontierState> _mctsFrontiers = [];
    private readonly PriorityQueue<ScheduledActor, SchedulePriority> _executionQueue = new();
    private readonly Dictionary<NCActorId, NCActiveWorldTime> _scheduledExecutions = [];
    private readonly Queue<DependencyInvalidationKey> _invalidations = [];
    private readonly Dictionary<DependencyInvalidationKey, long> _pendingInvalidationVersions = [];
    private readonly Dictionary<(NCCognitionDependencyKind Kind, string StableId), HashSet<NCActorId>>
        _dependencyConsumers = [];
    private readonly Dictionary<string, CachedPlan> _planCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _planCacheOrder = [];
    private DependencyInvalidation? _activeInvalidation;
    private readonly HashSet<NCActorId> _activeInvalidationConsumers = [];

    private NCCognitionRuntimePolicyPrototype? _policy;
    private NCSessionEpoch _epoch;
    private NCActorId? _pageCursor;
    private NCActiveWorldTime _pageRetryAt;
    private long _sequence;
    private int _generation;
    private bool _active;
    private bool _pageLoadInFlight;
    private bool _catalogRecovered;
    private long _decisions;
    private long _intentionSwitches;
    private long _plansBuilt;
    private long _plansInvalidated;
    private long _fallbackPlans;
    private long _mctsNodesExpanded;
    private string? _configurationError;

    public bool IsActive => _active;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnSessionClosing);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(OnSessionClosed);
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(OnClockFailed);
        SubscribeLocalEvent<NCCityActorPersistedEvent>(OnActorPersisted);
        SubscribeLocalEvent<NCCityActorMaterializationChangedEvent>(OnMaterializationChanged);
        SubscribeLocalEvent<NCCognitionRuntimeComponent, ComponentShutdown>(OnRuntimeShutdown);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_active || _policy == null ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
            return;
        if (_configurationError != null)
            return;

        using var work = _governor.MeasureWork(NCAILoadWorkKind.Cognition);
        if (!_catalogRecovered && !_pageLoadInFlight && now >= _pageRetryAt)
            BeginPageLoad(_generation, _pageCursor);

        ProcessInvalidations(now);
        AdvancePlanning(now);
        ExecuteDuePlans(now);

        var limit = _governor.TakeDispatchLimit(
            NCAILoadDispatchKind.CognitionDecision,
            _policy.MaximumDispatchesPerTick);
        var dispatched = 0;
        while (dispatched < limit && _inFlight.Count < _policy.MaximumConcurrentLoads &&
               TryDequeueDue(_decisionQueue, _scheduledDecisions, now, out var actorId))
        {
            BeginDecision(actorId, now);
            dispatched++;
        }

        var queuedWork = _scheduledDecisions.Count + _planningSet.Count + _scheduledExecutions.Count +
                         _pendingInvalidationVersions.Count + (_activeInvalidation.HasValue ? 1 : 0);
        _governor.ReportQueue(NCAILoadQueueKind.Cognition, queuedWork, _policy.MaximumQueuedActors);
        _governor.ReportBacklog(NCAILoadBacklogKind.Cognition,
            HasDue(_decisionQueue, _scheduledDecisions, now) || _planningQueue.Count > 0 ||
            HasDue(_executionQueue, _scheduledExecutions, now) || _activeInvalidation.HasValue ||
            _pendingInvalidationVersions.Count > 0);
    }

    public bool ForceSchedule(NCActorId actorId, NCActiveWorldTime dueAt)
    {
        if (!_active || !actorId.IsValid || !dueAt.IsValid)
            return false;
        ScheduleDecision(actorId, dueAt);
        return true;
    }

    public bool TryGetSnapshot(NCActorId actorId, out NCCognitionRuntimeSnapshot snapshot)
    {
        if (!_states.TryGetValue(actorId, out var state))
        {
            snapshot = default!;
            return false;
        }
        EntityUid? entityUid = _runtime.TryGetEntity(actorId, out var entity) ? entity : null;
        var humanActivity = BuildHumanActivitySnapshot(state);
        snapshot = new NCCognitionRuntimeSnapshot(
            actorId,
            entityUid,
            state.ActorVersion,
            state.CognitionVersion,
            state.BeliefSetVersion,
            state.Facts.ToArray(),
            state.Beliefs.ToArray(),
            state.Desires.ToArray(),
            state.Intention,
            state.Plan,
            state.Trace.ToArray(),
            state.LastDecisionAt,
            state.NextDecisionAt,
            state.LastFailure,
            humanActivity);
        return true;
    }

    private NCHumanActivityRuntimeSnapshot BuildHumanActivitySnapshot(RuntimeState state)
    {
        _actors.TryGetCached(state.ActorId, out var actor);
        var snapshot = actor?.Snapshot;
        var activity = snapshot?.Schedule.CurrentActivity ?? state.CurrentActivity;
        var failure = state.LastFailure ?? string.Empty;
        var wakeAt = state.NextDecisionAt;
        if (snapshot != null && (!wakeAt.IsValid || snapshot.Schedule.NextUpdateAt < wakeAt))
            wakeAt = snapshot.Schedule.NextUpdateAt;

        var fallback = state.Plan?.Steps.FirstOrDefault(step =>
            !state.CompletedStepIds.Contains(step.StepId))?.ActionId ?? "none";
        if (snapshot?.Lifecycle.Status is NCCityActorStatus.Resting or NCCityActorStatus.Injured or
            NCCityActorStatus.Hospitalized or NCCityActorStatus.Unconscious ||
            activity.Equals("sleep", StringComparison.OrdinalIgnoreCase) ||
            activity.Equals("rest", StringComparison.OrdinalIgnoreCase) ||
            activity.Equals("recover", StringComparison.OrdinalIgnoreCase))
            return new(NCHumanActivityRuntimeState.Recovering, activity, fallback, wakeAt);
        if (activity.Equals("unemployed-free-time", StringComparison.OrdinalIgnoreCase) ||
            snapshot != null && !snapshot.Location.WorkZone.IsValid &&
            activity.Equals("work", StringComparison.OrdinalIgnoreCase))
            return new(NCHumanActivityRuntimeState.UnemployedFreeTime,
                activity.Equals("unemployed-free-time", StringComparison.OrdinalIgnoreCase)
                    ? "unemployed-schedule"
                    : "no-work-zone",
                fallback,
                wakeAt);
        if (failure.Contains("affordance", StringComparison.OrdinalIgnoreCase))
            return new(NCHumanActivityRuntimeState.NoAffordance, failure, fallback, wakeAt);
        if (failure.Contains("unreachable", StringComparison.OrdinalIgnoreCase) ||
            failure.Contains("no-path", StringComparison.OrdinalIgnoreCase))
            return new(NCHumanActivityRuntimeState.BlockedRoute, failure, fallback, wakeAt);
        if (activity.Equals("socialize", StringComparison.OrdinalIgnoreCase) ||
            activity.Equals("leisure", StringComparison.OrdinalIgnoreCase))
            return new(NCHumanActivityRuntimeState.SocialIdle, activity, fallback, wakeAt);
        if (state.Plan == null || state.Plan.Steps.Length == 0 || state.Plan.Status == NCCognitionPlanStatus.Failed)
            return new(NCHumanActivityRuntimeState.NoValidPlan,
                state.Plan?.Failure ?? (string.IsNullOrWhiteSpace(failure) ? "no-plan" : failure), fallback, wakeAt);
        if (_clock.TryGetCurrentTime(out var epoch, out var now) && epoch == _epoch && wakeAt > now &&
            state.Plan.Status == NCCognitionPlanStatus.Completed)
            return new(NCHumanActivityRuntimeState.WaitingUntilDue, "scheduled-wake", fallback, wakeAt);
        return new(NCHumanActivityRuntimeState.Active,
            string.IsNullOrWhiteSpace(activity) ? "active" : activity, fallback, wakeAt);
    }

    public NCCognitionCounters GetCounters() => new(
        _active,
        _states.Count,
        _scheduledDecisions.Count,
        _inFlight.Count,
        _planningSet.Count,
        _planCache.Count,
        _pendingInvalidationVersions.Count + (_activeInvalidation.HasValue ? 1 : 0),
        _decisions,
        _intentionSwitches,
        _plansBuilt,
        _plansInvalidated,
        _fallbackPlans,
        _mctsNodesExpanded,
        _configurationError);

    public bool TryGetEarliestDecision(out NCActiveWorldTime dueAt, out bool dueNow)
    {
        if (!TryPeekScheduled(_decisionQueue, _scheduledDecisions, out var scheduled, out _))
        {
            dueAt = default;
            dueNow = false;
            return false;
        }

        dueAt = scheduled.DueAt;
        dueNow = _clock.TryGetCurrentTime(out var epoch, out var now) && epoch == _epoch && dueAt <= now;
        return true;
    }

    public void NotifyDependencyChanged(
        NCCognitionDependencyKind kind,
        string stableId,
        long version)
    {
        if (!_active || _policy == null || string.IsNullOrWhiteSpace(stableId) || version < 0)
            return;
        var key = new DependencyInvalidationKey(kind, stableId.Trim());
        if (_activeInvalidation is { } active && active.Kind == key.Kind && active.StableId == key.StableId)
        {
            if (version > active.Version)
            {
                _activeInvalidation = active with { Version = version };
                _activeInvalidationConsumers.Clear();
            }
            return;
        }
        if (_pendingInvalidationVersions.TryGetValue(key, out var pending))
        {
            _pendingInvalidationVersions[key] = Math.Max(pending, version);
            return;
        }
        if (_pendingInvalidationVersions.Count >= _policy.MaximumQueuedActors)
        {
            // Fail closed: a dependency update must never be silently lost while an older plan can still execute.
            _configurationError = "The cognition invalidation queue exhausted its configured capacity.";
            Log.Error(_configurationError);
            return;
        }
        _pendingInvalidationVersions.Add(key, version);
        _invalidations.Enqueue(key);
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        Reset();
        string? error = null;
        if (!_prototypes.TryIndex(new ProtoId<NCCognitionRuntimePolicyPrototype>(DefaultPolicyId), out var policy) ||
            policy == null || !ValidateConfiguration(policy, out error))
        {
            _configurationError = error ?? $"Cognition policy {DefaultPolicyId} is missing or invalid.";
            Log.Error(_configurationError);
            return;
        }
        _policy = policy;
        _epoch = args.Epoch;
        _pageRetryAt = args.Time;
        _active = true;
        BeginPageLoad(_generation, null);
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

    private void OnActorPersisted(NCCityActorPersistedEvent args)
    {
        if (!_active || args.SessionEpoch != _epoch)
            return;
        if (args.Status is NCCityActorStatus.Dead or NCCityActorStatus.Archived || args.Frozen)
        {
            Unschedule(args.ActorId);
            return;
        }
        var state = GetOrCreateState(args.ActorId);
        state.ActorVersion = args.AggregateVersion;
        state.CognitionTier = args.CognitionTier;
        NotifyDependencyChanged(NCCognitionDependencyKind.ActorRoot, args.ActorId.ToString(), args.AggregateVersion);
        var dueAt = args.NextUpdateAt;
        if (_clock.TryGetCurrentTime(out var epoch, out var now) && epoch == _epoch)
            dueAt = StaggerOverdueDecision(args.ActorId, dueAt, now);
        ScheduleDecision(args.ActorId, dueAt);
    }

    private void OnMaterializationChanged(NCCityActorMaterializationChangedEvent args)
    {
        if (!_active)
            return;
        if (args.Materialized && Exists(args.EntityUid))
        {
            var component = EnsureComp<NCCognitionRuntimeComponent>(args.EntityUid);
            component.ActorId = args.ActorId;
            CopyToComponent(args.EntityUid, component, GetOrCreateState(args.ActorId));
        }
        else
        {
            if (Exists(args.EntityUid))
                RemComp<NCCognitionRuntimeComponent>(args.EntityUid);
            if (_states.TryGetValue(args.ActorId, out var state))
            {
                // Full Blackboard data is meaningful only beside a Local/Entity actor. The abstract scheduler keeps
                // durable BDI identity and the bounded plan, but releases round-local observations immediately.
                state.Facts = [];
                state.Beliefs = [];
                state.Desires = [];
            }
            _registry.Unload(args.ActorId);
        }
        if (_clock.TryGetCurrentTime(out var epoch, out var now) && epoch == _epoch)
            ScheduleDecision(args.ActorId, now);
    }

    private void OnRuntimeShutdown(Entity<NCCognitionRuntimeComponent> entity, ref ComponentShutdown args)
    {
        // Runtime teardown never deletes persistent cognition; the next materialization receives a fresh adapter.
        entity.Comp.Facts.Clear();
        entity.Comp.Beliefs.Clear();
        entity.Comp.Desires.Clear();
        entity.Comp.Trace.Clear();
    }

    private void BeginPageLoad(int generation, NCActorId? cursor)
    {
        if (!_active || _pageLoadInFlight)
            return;
        _pageLoadInFlight = true;
        _ = ObservePageLoad(generation, cursor);
    }

    private async Task ObservePageLoad(int generation, NCActorId? cursor)
    {
        NCCityActorPage? page = null;
        Exception? error = null;
        try
        {
            page = await _actors.GetPageResultAsync(cursor, CatalogPageSize);
        }
        catch (Exception exception)
        {
            error = exception;
        }
        _taskManager.RunOnMainThread(() => CompletePageLoad(generation, page, error));
    }

    private void CompletePageLoad(int generation, NCCityActorPage? page, Exception? error)
    {
        if (generation != _generation || !_active || _policy == null)
            return;
        _pageLoadInFlight = false;
        if (error != null || page == null)
        {
            Log.Error($"Cognition catalog page load failed: {error}");
            if (_clock.TryGetCurrentTime(out var epoch, out var now) && epoch == _epoch)
                _pageRetryAt = now.Advance(_policy.LoadRetryDelay);
            return;
        }
        _clock.TryGetCurrentTime(out var currentEpoch, out var currentTime);
        foreach (var actor in page.Records)
        {
            if (_states.Count >= _policy.MaximumTrackedActors)
                break;
            if (actor.Snapshot.Lifecycle.Frozen ||
                actor.Snapshot.Lifecycle.Status is NCCityActorStatus.Dead or NCCityActorStatus.Archived)
                continue;
            var state = GetOrCreateState(actor.Snapshot.Identity.ActorId);
            state.ActorVersion = actor.AggregateVersion;
            state.CognitionTier = actor.Snapshot.CognitionTier;
            var dueAt = actor.Snapshot.Schedule.NextUpdateAt;
            if (currentEpoch == _epoch)
                dueAt = StaggerOverdueDecision(actor.Snapshot.Identity.ActorId, dueAt, currentTime);
            ScheduleDecision(actor.Snapshot.Identity.ActorId, dueAt);
        }
        if (_states.Count < _policy.MaximumTrackedActors && page.HasMore &&
            page.ContinuationActorId is { IsValid: true } continuation)
        {
            _pageCursor = continuation;
            BeginPageLoad(generation, continuation);
            return;
        }
        _catalogRecovered = true;
        _pageCursor = null;
    }

    private void BeginDecision(NCActorId actorId, NCActiveWorldTime now)
    {
        if (_policy == null || !_inFlight.Add(actorId))
            return;
        var perception = CapturePerception(actorId);
        _ = ObserveDecision(_generation, actorId, perception, now, _policy.MaximumFacts);
    }

    private async Task ObserveDecision(
        int generation,
        NCActorId actorId,
        NCCognitionPerceptionInput perception,
        NCActiveWorldTime requestedAt,
        int maximumFacts)
    {
        NCCityActorRecord? actor = null;
        NCCognitionRecord? cognition = null;
        NCBeliefSetRecord? beliefs = null;
        Exception? error = null;
        try
        {
            var actorTask = _actors.GetAsync(actorId);
            var cognitionTask = _registry.GetCognitionAsync(actorId);
            var beliefTask = _registry.GetBeliefsAsync(actorId);
            await Task.WhenAll(actorTask, cognitionTask, beliefTask);
            actor = await actorTask;
            cognition = await cognitionTask;
            beliefs = await beliefTask;
        }
        catch (Exception exception)
        {
            error = exception;
        }
        _taskManager.RunOnMainThread(() => ContinueDecisionFacts(
            generation, actorId, actor, cognition, beliefs, perception, requestedAt, maximumFacts, error));
    }

    private void ContinueDecisionFacts(
        int generation,
        NCActorId actorId,
        NCCityActorRecord? actor,
        NCCognitionRecord? cognition,
        NCBeliefSetRecord? beliefs,
        NCCognitionPerceptionInput perception,
        NCActiveWorldTime requestedAt,
        int maximumFacts,
        Exception? error)
    {
        if (generation != _generation || !_active || error != null || actor == null || cognition == null ||
            beliefs == null || !_prototypes.TryIndex(new ProtoId<NCCognitionDecisionPolicyPrototype>(
                cognition.Snapshot.DecisionPolicyId), out var decisionPolicy) || decisionPolicy == null)
        {
            CompleteDecisionLoad(generation, actorId, actor, cognition, beliefs, [], requestedAt,
                error ?? new InvalidOperationException("The actor cognition decision policy is missing."));
            return;
        }
        _ = ObserveFacts(generation, actorId, actor, cognition, beliefs, perception, requestedAt,
            decisionPolicy.BeliefLifetime, maximumFacts);
    }

    private async Task ObserveFacts(
        int generation,
        NCActorId actorId,
        NCCityActorRecord actor,
        NCCognitionRecord cognition,
        NCBeliefSetRecord beliefs,
        NCCognitionPerceptionInput perception,
        NCActiveWorldTime requestedAt,
        TimeSpan beliefLifetime,
        int maximumFacts)
    {
        NCCognitionFact[] facts = [];
        Exception? error = null;
        try
        {
            facts = await _providers.CollectAsync(actor, perception, requestedAt, beliefLifetime, maximumFacts,
                _policy!.MissingCapabilityRefreshInterval);
        }
        catch (Exception exception)
        {
            error = exception;
        }
        _taskManager.RunOnMainThread(() =>
            CompleteDecisionLoad(generation, actorId, actor, cognition, beliefs, facts, requestedAt, error));
    }

    private NCCognitionPerceptionInput CapturePerception(NCActorId actorId)
    {
        if (!_runtime.TryGetEntity(actorId, out var entityUid) || !Exists(entityUid) ||
            !TryComp<NCCityLocalPerceptionComponent>(entityUid, out var perception))
            return default;
        var conversation = TryComp<NCCityPhysicalExecutionComponent>(entityUid, out var execution) &&
                           (execution.Interrupts & NCCityPhysicalInterruptFlags.Conversation) != 0;
        return new NCCognitionPerceptionInput(
            perception.Revision,
            perception.HostileCount > 0,
            perception.CriticalCount > 0,
            conversation,
            perception.Observations
                .Where(value => value.Kind is NCCityLocalObservationKind.Hostile or
                    NCCityLocalObservationKind.Suspect or NCCityLocalObservationKind.Critical)
                .Select(value => new NCCognitionObservedSubject(
                new NCCognitionSubjectRef(NCCognitionReferenceKind.StableKey, value.SubjectHandle),
                value.Kind switch
                {
                    NCCityLocalObservationKind.Hostile => "perception.hostile-present",
                    NCCityLocalObservationKind.Suspect => "ncpd.suspect-observed",
                    NCCityLocalObservationKind.Critical => "perception.critical-person-present",
                    _ => throw new InvalidOperationException("Unsupported actionable perception kind."),
                },
                value.Confidence,
                value.ObservedAt,
                value.ExpiresAt)).ToArray());
    }

    private void ScheduleDecision(NCActorId actorId, NCActiveWorldTime dueAt)
    {
        if (_policy == null || !actorId.IsValid || !dueAt.IsValid ||
            (!_states.ContainsKey(actorId) && _states.Count >= _policy.MaximumTrackedActors))
            return;
        var state = GetOrCreateState(actorId);
        if (_scheduledDecisions.TryGetValue(actorId, out var existing) && existing <= dueAt)
        {
            state.NextDecisionAt = existing;
            return;
        }
        // The runtime snapshot exposes the actual earliest wake, including event-driven retries before the persisted
        // BDI deadline. This makes every non-active Human state diagnosable without polling the actor.
        state.NextDecisionAt = dueAt;
        _scheduledDecisions[actorId] = dueAt;
        var item = new ScheduledActor(actorId, dueAt, ++_sequence);
        _decisionQueue.Enqueue(item, new SchedulePriority(dueAt.Ticks, item.Sequence));
    }

    private NCActiveWorldTime StaggerOverdueDecision(
        NCActorId actorId,
        NCActiveWorldTime dueAt,
        NCActiveWorldTime now)
    {
        if (_policy == null || dueAt > now || _policy.InitialDecisionStagger <= TimeSpan.Zero)
            return dueAt;
        var windowTicks = Math.Max(1L, _policy.InitialDecisionStagger.Ticks);
        var hash = unchecked((uint) actorId.Value.GetHashCode());
        return now.Advance(TimeSpan.FromTicks(hash % windowTicks));
    }

    private RuntimeState GetOrCreateState(NCActorId actorId)
    {
        if (_states.TryGetValue(actorId, out var state))
            return state;
        state = new RuntimeState(actorId);
        _states.Add(actorId, state);
        return state;
    }

    private void Unschedule(NCActorId actorId)
    {
        _scheduledDecisions.Remove(actorId);
        _scheduledExecutions.Remove(actorId);
        _planningSet.Remove(actorId);
        _mctsFrontiers.Remove(actorId);
        if (_states.Remove(actorId, out var state))
            RemoveDependencyConsumers(actorId, state.Plan);
        _registry.Unload(actorId);
        if (_runtime.TryGetEntity(actorId, out var entityUid) && Exists(entityUid))
            RemComp<NCCognitionRuntimeComponent>(entityUid);
    }

    private static bool TryDequeueDue(
        PriorityQueue<ScheduledActor, SchedulePriority> queue,
        Dictionary<NCActorId, NCActiveWorldTime> scheduled,
        NCActiveWorldTime now,
        out NCActorId actorId)
    {
        while (queue.TryPeek(out var item, out var priority) && priority.DueTicks <= now.Ticks)
        {
            queue.Dequeue();
            if (!scheduled.TryGetValue(item.ActorId, out var dueAt) || dueAt != item.DueAt)
                continue;
            scheduled.Remove(item.ActorId);
            actorId = item.ActorId;
            return true;
        }
        actorId = default;
        return false;
    }

    private static bool HasDue(
        PriorityQueue<ScheduledActor, SchedulePriority> queue,
        Dictionary<NCActorId, NCActiveWorldTime> scheduled,
        NCActiveWorldTime now)
    {
        while (queue.TryPeek(out var item, out var priority))
        {
            if (!scheduled.TryGetValue(item.ActorId, out var dueAt) || dueAt != item.DueAt)
            {
                queue.Dequeue();
                continue;
            }
            return priority.DueTicks <= now.Ticks;
        }
        return false;
    }

    private static bool TryPeekScheduled(
        PriorityQueue<ScheduledActor, SchedulePriority> queue,
        Dictionary<NCActorId, NCActiveWorldTime> scheduled,
        out ScheduledActor actor,
        out SchedulePriority priority)
    {
        while (queue.TryPeek(out actor, out priority))
        {
            if (scheduled.TryGetValue(actor.ActorId, out var dueAt) && dueAt == actor.DueAt)
                return true;
            queue.Dequeue();
        }

        actor = default;
        priority = default;
        return false;
    }

    private void Reset()
    {
        _active = false;
        _policy = null;
        _epoch = default;
        _pageCursor = null;
        _pageRetryAt = default;
        _sequence = 0;
        _generation++;
        _pageLoadInFlight = false;
        _catalogRecovered = false;
        _configurationError = null;
        _decisionQueue.Clear();
        _scheduledDecisions.Clear();
        _states.Clear();
        _inFlight.Clear();
        _planningQueue.Clear();
        _planningSet.Clear();
        _mctsFrontiers.Clear();
        _executionQueue.Clear();
        _scheduledExecutions.Clear();
        _invalidations.Clear();
        _pendingInvalidationVersions.Clear();
        _dependencyConsumers.Clear();
        _planCache.Clear();
        _planCacheOrder.Clear();
        _activeInvalidation = null;
        _activeInvalidationConsumers.Clear();
        _decisions = 0;
        _intentionSwitches = 0;
        _plansBuilt = 0;
        _plansInvalidated = 0;
        _fallbackPlans = 0;
        _mctsNodesExpanded = 0;
    }

    private sealed class RuntimeState(NCActorId actorId)
    {
        public NCActorId ActorId { get; } = actorId;
        public NCCityActorCognitionTier CognitionTier;
        public string DecisionPolicyId = string.Empty;
        public string CapabilityProfileId = string.Empty;
        public string RoutingProfileId = string.Empty;
        public NCCityActorTravelPhase TravelPhase;
        public string CurrentActivity = string.Empty;
        public long ActorVersion;
        public long CognitionVersion;
        public long BeliefSetVersion;
        public NCCognitionFact[] Facts = [];
        public NCBeliefSnapshot[] Beliefs = [];
        public NCCognitionDesireSnapshot[] Desires = [];
        public NCCognitionIntentionSnapshot? Intention;
        public NCCognitionPlanSnapshot? Plan;
        public NCActiveWorldTime LastDecisionAt;
        public NCActiveWorldTime NextDecisionAt;
        public string? LastFailure;
        public long TraceSequence;
        public readonly List<NCCognitionTraceEntry> Trace = [];
        public readonly Dictionary<(NCCognitionDependencyKind Kind, string StableId), long> DependencyVersions = [];
        public readonly HashSet<string> CompletedStepIds = new(StringComparer.Ordinal);
    }

    private readonly record struct ScheduledActor(NCActorId ActorId, NCActiveWorldTime DueAt, long Sequence);
    private readonly record struct SchedulePriority(long DueTicks, long Sequence) : IComparable<SchedulePriority>
    {
        public int CompareTo(SchedulePriority other)
        {
            var time = DueTicks.CompareTo(other.DueTicks);
            return time != 0 ? time : Sequence.CompareTo(other.Sequence);
        }
    }

    private readonly record struct DependencyInvalidation(
        NCCognitionDependencyKind Kind,
        string StableId,
        long Version);

    private readonly record struct DependencyInvalidationKey(
        NCCognitionDependencyKind Kind,
        string StableId);

    private sealed record CachedPlan(
        string CapabilityId,
        NCCognitionPlannerKind Planner,
        NCCognitionPlanDependency[] Dependencies,
        NCCognitionPlanStep[] Steps);
}
