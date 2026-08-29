// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.LivingCity.Simulation;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Actors;

/// <summary>
/// Active-round-only priority scheduler for persistent actor routines. Its Update path touches only due queue
/// entries, dispatches bounded asynchronous database jobs, and never scans ECS entities or all actor snapshots.
/// </summary>
public sealed partial class NCCityActorScheduleSchedulerSystem : EntitySystem
{
    private const string DefaultPolicyId = "NCDefaultCitySchedulePolicy";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ITaskManager _taskManager = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRegistrySystem _registry = default!;
    [Dependency] private NCCityRoutineSystem _routines = default!;
    [Dependency] private NCAILoadGovernorSystem _loadGovernor = default!;

    private readonly PriorityQueue<ScheduledActor, SchedulePriority> _queue = new();
    private readonly Dictionary<NCActorId, NCActiveWorldTime> _scheduled = [];
    private readonly Dictionary<NCActorId, PendingMutation> _pendingMutations = [];
    private readonly Dictionary<NCActorId, NCActiveWorldTime> _deferredSchedules = [];
    private readonly HashSet<NCActorId> _forceRoutineReevaluation = [];
    private readonly HashSet<NCActorId> _inFlightActors = [];

    private NCCitySchedulePolicyPrototype? _policy;
    private NCSessionEpoch _epoch;
    private NCActorId? _pageCursor;
    private NCActiveWorldTime _pageRetryAt;
    private int _generation;
    private int _inFlight;
    private long _sequence;
    private bool _active;
    private bool _pageLoadInFlight;

    public int QueuedActorCount => _scheduled.Count;
    public int DeferredScheduleCount => _deferredSchedules.Count;
    public int InFlightCount => _inFlight;
    public int PendingMutationCount => _pendingMutations.Count;
    public long MutationRetriesExhausted { get; private set; }
    public bool IsActive => _active;
    public bool InitialLoadComplete { get; private set; }

    public bool TryGetEarliestDue(out NCActiveWorldTime dueAt, out bool dueNow)
    {
        if (!TryPeekScheduled(out var scheduled, out _))
        {
            dueAt = default;
            dueNow = false;
            return false;
        }

        dueAt = scheduled.DueAt;
        dueNow = _clock.TryGetCurrentTime(out var epoch, out var now) && epoch == _epoch && dueAt <= now;
        return true;
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnSessionClosing);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(OnSessionClosed);
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(OnClockFailed);
        SubscribeLocalEvent<NCCityActorPersistedEvent>(OnActorPersisted);
        SubscribeLocalEvent<NCCityActorMaterializationChangedEvent>(OnMaterializationChanged);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_active || _policy == null ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
        {
            return;
        }

        using var work = _loadGovernor.MeasureWork(NCAILoadWorkKind.Schedule);
        if (!InitialLoadComplete && !_pageLoadInFlight && now >= _pageRetryAt)
            BeginPageLoad(_generation, _pageCursor);

        var dispatchLimit = _loadGovernor.TakeDispatchLimit(
            NCAILoadDispatchKind.Schedule,
            _policy.MaximumDispatchesPerTick);
        var dispatched = 0;
        while (dispatched < dispatchLimit &&
               _inFlight < _policy.MaximumConcurrentMutations &&
               TryPeekScheduled(out var scheduled, out var priority) &&
               priority.DueTicks <= now.Ticks)
        {
            _queue.Dequeue();
            _scheduled.Remove(scheduled.ActorId);
            _inFlight++;
            _inFlightActors.Add(scheduled.ActorId);
            dispatched++;
            if (_pendingMutations.TryGetValue(scheduled.ActorId, out var pending))
                BeginMutation(_generation, pending.Request);
            else
                BeginActorLoad(_generation, scheduled.ActorId);
        }

        var backlogged = TryPeekScheduled(out _, out var remainingPriority) &&
            remainingPriority.DueTicks <= now.Ticks;
        _loadGovernor.ReportBacklog(NCAILoadBacklogKind.Schedule, backlogged);
    }

    public bool TryGetScheduledAt(NCActorId actorId, out NCActiveWorldTime dueAt)
    {
        return _scheduled.TryGetValue(actorId, out dueAt);
    }

    public bool ForceSchedule(NCActorId actorId, NCActiveWorldTime dueAt)
    {
        if (!_active || !actorId.IsValid || !dueAt.IsValid)
            return false;
        Enqueue(actorId, dueAt);
        return true;
    }

    /// <summary>
    /// Advances civil time and synchronously requeues every recovered actor as one main-thread operation.
    /// Normal scheduler budgets still bound the resulting evaluation work.
    /// </summary>
    public bool TryAdvanceCivilTime(
        TimeSpan advance,
        out NCCityCivilTimeSnapshot civilTime,
        out int queuedActors,
        out string? error)
    {
        civilTime = default;
        queuedActors = 0;
        if (!_active || _policy == null ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
        {
            error = "civil-time-scheduler-inactive";
            return false;
        }

        if (!_routines.TryAdvanceCivilTime(advance, out civilTime, out error))
            return false;

        var candidates = new List<NCActorId>(_scheduled.Keys);
        foreach (var actorId in _inFlightActors)
        {
            if (!_scheduled.ContainsKey(actorId))
                candidates.Add(actorId);
        }

        foreach (var actorId in candidates)
        {
            _forceRoutineReevaluation.Add(actorId);
            Enqueue(actorId, now);
        }

        queuedActors = candidates.Count;
        error = null;
        return true;
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        Reset();
        if (!_prototypes.TryIndex(
                new ProtoId<NCCitySchedulePolicyPrototype>(DefaultPolicyId),
                out NCCitySchedulePolicyPrototype? policy) ||
            policy == null || !ValidatePolicy(policy))
        {
            Log.Error($"Persistent actor scheduler cannot start: policy {DefaultPolicyId} is missing or invalid.");
            return;
        }

        _policy = policy;
        _epoch = args.Epoch;
        _active = true;
        _pageRetryAt = args.Time;
        BeginPageLoad(_generation, null);
    }

    private void OnSessionClosing(NCActiveWorldSessionClosingEvent args)
    {
        if (_active && args.Epoch == _epoch)
            Reset();
    }

    private void OnSessionClosed(NCActiveWorldSessionClosedEvent args)
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

        if (args.Frozen || args.Status is NCCityActorStatus.Dead or NCCityActorStatus.Archived or
            NCCityActorStatus.Arrested or NCCityActorStatus.Imprisoned)
        {
            _scheduled.Remove(args.ActorId);
            _deferredSchedules.Remove(args.ActorId);
            _pendingMutations.Remove(args.ActorId);
            _forceRoutineReevaluation.Remove(args.ActorId);
            return;
        }

        var dueAt = args.NextUpdateAt;
        if (_forceRoutineReevaluation.Contains(args.ActorId) &&
            _clock.TryGetCurrentTime(out var epoch, out var now) && epoch == _epoch)
        {
            dueAt = StaggerOverdueEvaluation(args.ActorId, now, now);
        }
        Enqueue(args.ActorId, dueAt);
    }

    private void OnMaterializationChanged(NCCityActorMaterializationChangedEvent args)
    {
        if (!_active || !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
            return;

        // Materialization adds vanilla hunger, stamina and sleep data; hand-off removes it. Re-evaluate through the
        // bounded persistent scheduler so neither transition needs a per-frame scan or owns routine state directly.
        _forceRoutineReevaluation.Add(args.ActorId);
        Enqueue(args.ActorId, now);
    }

    private void BeginPageLoad(int generation, NCActorId? afterActorId)
    {
        if (!_active || _policy == null || _pageLoadInFlight)
            return;

        _pageLoadInFlight = true;
        _ = ObservePageLoad(generation, afterActorId, _policy.SnapshotPageSize);
    }

    private async Task ObservePageLoad(int generation, NCActorId? afterActorId, int pageSize)
    {
        NCCityActorPage? page = null;
        Exception? error = null;
        try
        {
            page = await _registry.GetPageResultAsync(afterActorId, pageSize);
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
            Log.Error($"Persistent actor scheduler page load failed: {error}");
            if (_clock.TryGetCurrentTime(out var epoch, out var now) && epoch == _epoch)
                _pageRetryAt = now.Advance(_policy.MutationRetryDelay);
            return;
        }

        var currentTime = default(NCActiveWorldTime);
        var reevaluate = _policy.ReevaluateOnSessionStart &&
                         _clock.TryGetCurrentTime(out var currentEpoch, out currentTime) &&
                         currentEpoch == _epoch;
        foreach (var actor in page.Records)
        {
            if (!actor.Snapshot.Lifecycle.Frozen &&
                actor.Snapshot.Lifecycle.Status is not (NCCityActorStatus.Dead or NCCityActorStatus.Archived or
                    NCCityActorStatus.Arrested or NCCityActorStatus.Imprisoned))
            {
                // Session recovery reconciles stationary actors with edited routine policies without bypassing the
                // normal scheduler budget. Ongoing journeys keep their durable arrival timing.
                var forceReevaluation = reevaluate && actor.Snapshot.Location.TravelPhase is not (
                    NCCityActorTravelPhase.Planned or NCCityActorTravelPhase.InTransit);
                if (forceReevaluation)
                    _forceRoutineReevaluation.Add(actor.Snapshot.Identity.ActorId);
                var dueAt = forceReevaluation ? currentTime : actor.Snapshot.Schedule.NextUpdateAt;
                dueAt = StaggerOverdueEvaluation(actor.Snapshot.Identity.ActorId, dueAt, currentTime);
                Enqueue(actor.Snapshot.Identity.ActorId, dueAt);
            }
        }

        if (page.HasMore && page.ContinuationActorId is { IsValid: true } continuation)
        {
            _pageCursor = continuation;
            BeginPageLoad(generation, continuation);
            return;
        }

        InitialLoadComplete = true;
        _pageCursor = null;
    }

    private void BeginActorLoad(int generation, NCActorId actorId)
    {
        _ = ObserveActorLoad(generation, actorId);
    }

    private async Task ObserveActorLoad(int generation, NCActorId actorId)
    {
        NCCityActorRecord? actor = null;
        Exception? error = null;
        try
        {
            actor = await _registry.GetAsync(actorId);
        }
        catch (Exception exception)
        {
            error = exception;
        }

        _taskManager.RunOnMainThread(() => CompleteActorLoad(generation, actorId, actor, error));
    }

    private void CompleteActorLoad(
        int generation,
        NCActorId actorId,
        NCCityActorRecord? actor,
        Exception? error)
    {
        if (generation != _generation || !_active || _policy == null)
            return;

        if (!_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
        {
            Finish(actorId);
            return;
        }

        if (error != null)
        {
            Log.Error($"Failed to load scheduled actor {actorId}: {error}");
            RetryActor(actorId, now);
            return;
        }

        if (actor == null)
        {
            Log.Error($"Scheduled actor {actorId} is missing or has an unreadable snapshot; removed from the queue.");
            _forceRoutineReevaluation.Remove(actorId);
            Finish(actorId);
            return;
        }

        if (actor.Snapshot.Lifecycle.Frozen ||
            actor.Snapshot.Lifecycle.Status is NCCityActorStatus.Dead or NCCityActorStatus.Archived or
                NCCityActorStatus.Arrested or NCCityActorStatus.Imprisoned)
        {
            _forceRoutineReevaluation.Remove(actorId);
            Finish(actorId);
            return;
        }

        if (!_forceRoutineReevaluation.Contains(actorId) && actor.Snapshot.Schedule.NextUpdateAt > now)
        {
            Enqueue(actorId, actor.Snapshot.Schedule.NextUpdateAt);
            Finish(actorId);
            return;
        }

        if (!_routines.TryEvaluate(actor, now, out var decision, out var routineError))
        {
            Log.Error($"Failed to evaluate routine for actor {actorId}: {routineError}");
            RetryActor(actorId, now);
            return;
        }

        var location = actor.Snapshot.Location;
        var continuingTravel = location.TravelPhase is NCCityActorTravelPhase.Planned or NCCityActorTravelPhase.InTransit &&
                               location.DestinationZone == decision.TargetZone;
        var atTarget = location.CurrentZone == decision.TargetZone;
        var destination = atTarget && !continuingTravel ? default : decision.TargetZone;
        var phase = continuingTravel
            ? location.TravelPhase
            : atTarget
                ? NCCityActorTravelPhase.Stationary
                : NCCityActorTravelPhase.Planned;
        var request = new NCCityActorLocationMutationRequest(
            Guid.NewGuid(),
            actorId,
            actor.AggregateVersion,
            location.CurrentZone,
            destination,
            phase,
            decision.TravelMode,
            continuingTravel ? location.ExpectedArrivalAt : null,
            decision.Activity,
            decision.Goal,
            decision.Availability,
            decision.NextUpdateAt,
            Guid.NewGuid(),
            PresenceMode: decision.PresenceMode);
        _pendingMutations[actorId] = new PendingMutation(request, 0);
        BeginMutation(generation, request);
    }

    private void BeginMutation(int generation, NCCityActorLocationMutationRequest request)
    {
        if (!_pendingMutations.TryGetValue(request.ActorId, out var pending) ||
            pending.Request.CommandId != request.CommandId)
            return;
        _pendingMutations[request.ActorId] = pending with { Attempts = pending.Attempts + 1 };
        _ = ObserveMutation(generation, request);
    }

    private async Task ObserveMutation(int generation, NCCityActorLocationMutationRequest request)
    {
        NCCityActorMutationResult? result = null;
        Exception? error = null;
        try
        {
            result = await _registry.SetLocationAsync(request);
        }
        catch (Exception exception)
        {
            error = exception;
        }

        _taskManager.RunOnMainThread(() => CompleteMutation(generation, request.ActorId, result, error));
    }

    private void CompleteMutation(
        int generation,
        NCActorId actorId,
        NCCityActorMutationResult? result,
        Exception? error)
    {
        if (generation != _generation || !_active || _policy == null)
            return;

        if (!_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
        {
            Finish(actorId);
            return;
        }

        if (error != null || result == null)
        {
            Log.Error($"Scheduled actor mutation for {actorId} failed before returning a result: {error}");
            if (!CanRetryMutation(actorId))
            {
                ExhaustMutation(actorId);
                return;
            }
            RetryActor(actorId, now, keepPendingMutation: true);
            return;
        }

        switch (result.Status)
        {
            case NCCityActorMutationStatus.Applied:
            case NCCityActorMutationStatus.Duplicate:
                var nextUpdateAt = result.Record?.Snapshot.Schedule.NextUpdateAt;
                if (_pendingMutations.TryGetValue(actorId, out var completedPending) &&
                    completedPending.Request.NextUpdateAt.IsValid)
                {
                    // A pure timer advance is runtime scheduler state, not a durable actor outcome. When the
                    // registry coalesces it, continue from the requested deadline instead of replaying the old one.
                    nextUpdateAt = completedPending.Request.NextUpdateAt;
                }
                _pendingMutations.Remove(actorId);
                _forceRoutineReevaluation.Remove(actorId);
                if (nextUpdateAt is { IsValid: true } dueAt)
                    Enqueue(actorId, dueAt);
                Finish(actorId);
                return;
            case NCCityActorMutationStatus.StorageFailure:
                // Keep the exact same CommandId: a lost database response must not create a second mutation.
                if (!CanRetryMutation(actorId))
                {
                    ExhaustMutation(actorId);
                    return;
                }
                RetryActor(actorId, now, keepPendingMutation: true);
                return;
            case NCCityActorMutationStatus.VersionConflict:
                _pendingMutations.Remove(actorId);
                RetryActor(actorId, now);
                return;
            case NCCityActorMutationStatus.ClockUnavailable:
                Finish(actorId);
                return;
            default:
                Log.Error($"Scheduled actor mutation for {actorId} was rejected: {result.Error}");
                _pendingMutations.Remove(actorId);
                _forceRoutineReevaluation.Remove(actorId);
                Finish(actorId);
                return;
        }
    }

    private void RetryActor(NCActorId actorId, NCActiveWorldTime now, bool keepPendingMutation = false)
    {
        if (_policy != null)
            Enqueue(actorId, now.Advance(_policy.MutationRetryDelay));
        if (!keepPendingMutation)
            _pendingMutations.Remove(actorId);
        Finish(actorId);
    }

    private bool CanRetryMutation(NCActorId actorId) =>
        _policy != null && _pendingMutations.TryGetValue(actorId, out var pending) &&
        pending.Attempts < _policy.MaximumMutationAttempts;

    private void ExhaustMutation(NCActorId actorId)
    {
        _pendingMutations.Remove(actorId);
        _forceRoutineReevaluation.Remove(actorId);
        MutationRetriesExhausted++;
        Finish(actorId);
    }

    private void Finish(NCActorId actorId, bool unload = true)
    {
        _inFlightActors.Remove(actorId);
        if (_inFlight > 0)
            _inFlight--;
        if (_deferredSchedules.Remove(actorId, out var deferredDue))
            Enqueue(actorId, deferredDue);
        if (unload)
            _registry.Unload(actorId);
    }

    private void Enqueue(NCActorId actorId, NCActiveWorldTime dueAt)
    {
        if (!actorId.IsValid || !dueAt.IsValid)
            return;

        // A persisted-event wake may race an async actor load. Keep one earliest deferred wake instead of
        // dispatching the same persistent actor concurrently.
        if (_inFlightActors.Contains(actorId))
        {
            if (!_deferredSchedules.TryGetValue(actorId, out var deferred) || dueAt < deferred)
                _deferredSchedules[actorId] = dueAt;
            return;
        }

        if (_scheduled.TryGetValue(actorId, out var existing) && existing <= dueAt)
            return;

        _scheduled[actorId] = dueAt;
        _queue.Enqueue(
            new ScheduledActor(actorId, dueAt),
            new SchedulePriority(dueAt.Ticks, _sequence++));
    }

    private NCActiveWorldTime StaggerOverdueEvaluation(
        NCActorId actorId,
        NCActiveWorldTime dueAt,
        NCActiveWorldTime now)
    {
        if (_policy == null || dueAt > now || _policy.InitialEvaluationStagger <= TimeSpan.Zero)
            return dueAt;

        var windowTicks = Math.Max(1L, _policy.InitialEvaluationStagger.Ticks);
        var hash = unchecked((uint) actorId.Value.GetHashCode());
        return now.Advance(TimeSpan.FromTicks(hash % windowTicks));
    }

    private bool TryPeekScheduled(out ScheduledActor scheduled, out SchedulePriority priority)
    {
        while (_queue.TryPeek(out scheduled, out priority))
        {
            if (_scheduled.TryGetValue(scheduled.ActorId, out var dueAt) && dueAt == scheduled.DueAt)
                return true;
            _queue.Dequeue();
        }

        scheduled = default;
        priority = default;
        return false;
    }

    private void Reset()
    {
        _generation++;
        _active = false;
        _policy = null;
        _epoch = default;
        _pageCursor = null;
        _pageRetryAt = default;
        _pageLoadInFlight = false;
        _inFlight = 0;
        _sequence = 0;
        InitialLoadComplete = false;
        _queue.Clear();
        _scheduled.Clear();
        _deferredSchedules.Clear();
        _pendingMutations.Clear();
        _forceRoutineReevaluation.Clear();
        _inFlightActors.Clear();
        MutationRetriesExhausted = 0;
    }

    private static bool ValidatePolicy(NCCitySchedulePolicyPrototype policy)
    {
        return policy.SnapshotPageSize is >= 1 and <= 500 &&
               policy.MaximumDispatchesPerTick is >= 1 and <= 64 &&
               policy.MaximumConcurrentMutations is >= 1 and <= 128 &&
               policy.MutationRetryDelay > TimeSpan.Zero &&
               policy.InitialEvaluationStagger >= TimeSpan.Zero &&
               policy.InitialEvaluationStagger <= TimeSpan.FromMinutes(5) &&
               policy.MaximumMutationAttempts is >= 1 and <= 32;
    }

    private readonly record struct ScheduledActor(NCActorId ActorId, NCActiveWorldTime DueAt);

    private readonly record struct SchedulePriority(long DueTicks, long Sequence) : IComparable<SchedulePriority>
    {
        public int CompareTo(SchedulePriority other)
        {
            var due = DueTicks.CompareTo(other.DueTicks);
            return due != 0 ? due : Sequence.CompareTo(other.Sequence);
        }
    }

    private sealed record PendingMutation(NCCityActorLocationMutationRequest Request, int Attempts);
}
