// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._NC.City.Connectivity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Actors;

/// <summary>
/// Bounded active-round executor for actors without ECS entities. It converts Planned intent into an
/// active-world-time journey and completes arrival; materialized actors remain the responsibility of local AI.
/// </summary>
public sealed partial class NCCityAbstractTravelSystem : EntitySystem
{
    private const string DefaultPolicyId = "NCDefaultAbstractTravelPolicy";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ITaskManager _taskManager = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCCityConnectivitySystem _city = default!;

    private readonly PriorityQueue<ScheduledTravel, TravelPriority> _queue = new();
    private readonly Dictionary<NCActorId, NCActiveWorldTime> _scheduled = [];
    private readonly Dictionary<NCActorId, NCCityActorLocationMutationRequest> _pendingMutations = [];
    private readonly List<NCCityRouteStep> _route = [];

    private NCCityAbstractTravelPolicyPrototype? _policy;
    private NCSessionEpoch _epoch;
    private NCActorId? _pageCursor;
    private NCActiveWorldTime _pageRetryAt;
    private int _generation;
    private int _inFlight;
    private long _sequence;
    private bool _active;
    private bool _pageLoadInFlight;

    public int QueuedActorCount => _scheduled.Count;
    public int InFlightCount => _inFlight;
    public int PendingMutationCount => _pendingMutations.Count;
    public bool InitialLoadComplete { get; private set; }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(OnSessionClosed);
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(OnClockFailed);
        SubscribeLocalEvent<NCCityActorPersistedEvent>(OnActorPersisted);
        SubscribeLocalEvent<NCCityActorMaterializationChangedEvent>(OnMaterializationChanged);
        SubscribeLocalEvent<NCCityConnectivityChangedEvent>(OnConnectivityChanged);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_active || _policy == null ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
        {
            return;
        }

        if (!InitialLoadComplete && !_pageLoadInFlight && now >= _pageRetryAt)
            BeginPageLoad(_generation, _pageCursor);

        var dispatched = 0;
        while (dispatched < _policy.MaximumDispatchesPerTick &&
               _inFlight < _policy.MaximumConcurrentMutations &&
               _queue.TryPeek(out var scheduled, out var priority) &&
               priority.DueTicks <= now.Ticks)
        {
            _queue.Dequeue();
            if (!_scheduled.TryGetValue(scheduled.ActorId, out var dueAt) || dueAt != scheduled.DueAt)
                continue;

            _scheduled.Remove(scheduled.ActorId);
            _inFlight++;
            dispatched++;
            if (_pendingMutations.TryGetValue(scheduled.ActorId, out var pending))
                BeginMutation(_generation, pending);
            else
                BeginActorLoad(_generation, scheduled.ActorId);
        }
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        Reset();
        if (!_prototypes.TryIndex(
                new ProtoId<NCCityAbstractTravelPolicyPrototype>(DefaultPolicyId),
                out NCCityAbstractTravelPolicyPrototype? policy) ||
            policy == null || !ValidatePolicy(policy))
        {
            Log.Error($"Abstract actor travel cannot start: policy {DefaultPolicyId} is missing or invalid.");
            return;
        }

        _policy = policy;
        _epoch = args.Epoch;
        _active = true;
        _pageRetryAt = args.Time;
        BeginPageLoad(_generation, null);
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
        if (!_active || args.SessionEpoch != _epoch ||
            args.Frozen || args.Status is NCCityActorStatus.Dead or NCCityActorStatus.Archived ||
            _runtime.TryGetEntity(args.ActorId, out _))
        {
            _scheduled.Remove(args.ActorId);
            _pendingMutations.Remove(args.ActorId);
            return;
        }

        ScheduleFromEvent(args);
    }

    private void OnMaterializationChanged(NCCityActorMaterializationChangedEvent args)
    {
        if (!_active)
            return;
        if (args.Materialized)
        {
            _scheduled.Remove(args.ActorId);
            _pendingMutations.Remove(args.ActorId);
            return;
        }

        if (_clock.TryGetCurrentTime(out var epoch, out var now) && epoch == _epoch)
            Enqueue(args.ActorId, now);
    }

    private void ScheduleFromEvent(NCCityActorPersistedEvent args)
    {
        switch (args.TravelPhase)
        {
            case NCCityActorTravelPhase.Planned:
                if (_clock.TryGetCurrentTime(out var epoch, out var now) && epoch == _epoch)
                    Enqueue(args.ActorId, now);
                break;
            case NCCityActorTravelPhase.InTransit when args.ExpectedArrivalAt is { IsValid: true } arrival:
                Enqueue(args.ActorId, arrival);
                break;
            case NCCityActorTravelPhase.Blocked:
                if (_clock.TryGetCurrentTime(out var blockedEpoch, out var blockedNow) && blockedEpoch == _epoch &&
                    _policy != null)
                {
                    Enqueue(args.ActorId, blockedNow.Advance(_policy.RetryDelay));
                }
                break;
            default:
                _scheduled.Remove(args.ActorId);
                _pendingMutations.Remove(args.ActorId);
                break;
        }
    }

    private void OnConnectivityChanged(ref NCCityConnectivityChangedEvent args)
    {
        if (!_active || !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
            return;

        // Only tracked travelers are reconsidered; a graph rebuild never triggers a persistent actor scan.
        foreach (var actorId in _scheduled.Keys.ToArray())
            Enqueue(actorId, now);
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
            page = await _actors.GetPageResultAsync(afterActorId, pageSize);
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
            Log.Error($"Abstract travel actor page load failed: {error}");
            if (_clock.TryGetCurrentTime(out var epoch, out var now) && epoch == _epoch)
                _pageRetryAt = now.Advance(_policy.RetryDelay);
            return;
        }

        foreach (var actor in page.Records)
            ScheduleFromSnapshot(actor);

        if (page.HasMore && page.ContinuationActorId is { IsValid: true } continuation)
        {
            _pageCursor = continuation;
            BeginPageLoad(generation, continuation);
            return;
        }

        InitialLoadComplete = true;
        _pageCursor = null;
    }

    private void ScheduleFromSnapshot(NCCityActorRecord actor)
    {
        var snapshot = actor.Snapshot;
        if (snapshot.Lifecycle.Frozen ||
            snapshot.Lifecycle.Status is NCCityActorStatus.Dead or NCCityActorStatus.Archived ||
            _runtime.TryGetEntity(snapshot.Identity.ActorId, out _))
        {
            return;
        }

        switch (snapshot.Location.TravelPhase)
        {
            case NCCityActorTravelPhase.Planned:
                if (_clock.TryGetCurrentTime(out var epoch, out var now) && epoch == _epoch)
                    Enqueue(snapshot.Identity.ActorId, now);
                break;
            case NCCityActorTravelPhase.Blocked:
                if (_clock.TryGetCurrentTime(out var blockedEpoch, out var blockedNow) && blockedEpoch == _epoch &&
                    _policy != null)
                {
                    Enqueue(snapshot.Identity.ActorId, blockedNow.Advance(_policy.RetryDelay));
                }
                break;
            case NCCityActorTravelPhase.InTransit when snapshot.Location.ExpectedArrivalAt is { IsValid: true } arrival:
                Enqueue(snapshot.Identity.ActorId, arrival);
                break;
        }
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
            actor = await _actors.GetAsync(actorId);
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
        if (error != null || actor == null)
        {
            Log.Error($"Failed to load abstract traveler {actorId}: {error}");
            Retry(actorId, now);
            return;
        }
        if (actor.Snapshot.Lifecycle.Frozen ||
            actor.Snapshot.Lifecycle.Status is NCCityActorStatus.Dead or NCCityActorStatus.Archived ||
            _runtime.TryGetEntity(actorId, out _))
        {
            Finish(actorId);
            return;
        }

        var location = actor.Snapshot.Location;
        switch (location.TravelPhase)
        {
            case NCCityActorTravelPhase.Planned:
            case NCCityActorTravelPhase.Blocked:
                BeginTravel(actor, now);
                return;
            case NCCityActorTravelPhase.InTransit:
                if (location.ExpectedArrivalAt is { IsValid: true } arrival && arrival > now)
                {
                    Enqueue(actorId, arrival);
                    Finish(actorId, unload: false);
                    return;
                }

                CompleteArrival(actor);
                return;
            default:
                Finish(actorId);
                return;
        }
    }

    private void BeginTravel(NCCityActorRecord actor, NCActiveWorldTime now)
    {
        var location = actor.Snapshot.Location;
        if (!location.DestinationZone.IsValid || !HasSingleTravelMode(location.TravelMode))
        {
            Finish(actor.Snapshot.Identity.ActorId);
            return;
        }

        if (location.CurrentZone == location.DestinationZone)
        {
            CompleteArrival(actor);
            return;
        }

        if (!_city.TryFindRoute(location.CurrentZone, location.DestinationZone, location.TravelMode, _route))
        {
            // A blocked route is a durable state, while its next check is scheduler state. Do not append
            // an identical actor event on every retry; topology changes wake the actor independently.
            if (location.TravelPhase == NCCityActorTravelPhase.Blocked)
            {
                Retry(actor.Snapshot.Identity.ActorId, now);
                return;
            }

            BeginMutation(actor, location with
            {
                TravelPhase = NCCityActorTravelPhase.Blocked,
                ExpectedArrivalAt = null,
            });
            return;
        }

        double seconds = 0;
        foreach (var step in _route)
        {
            if (!_prototypes.TryIndex(step.TraversalProfile, out NCCityTraversalProfilePrototype? traversal) ||
                traversal == null || !float.IsFinite(traversal.BaseTravelSeconds) || traversal.BaseTravelSeconds <= 0f)
            {
                BeginMutation(actor, location with
                {
                    TravelPhase = NCCityActorTravelPhase.Blocked,
                    ExpectedArrivalAt = null,
                });
                return;
            }

            seconds += traversal.BaseTravelSeconds;
        }

        seconds *= _policy!.DurationMultiplier;
        var duration = TimeSpan.FromSeconds(Math.Clamp(
            seconds,
            _policy.MinimumTravelDuration.TotalSeconds,
            _policy.MaximumTravelDuration.TotalSeconds));
        BeginMutation(actor, location with
        {
            TravelPhase = NCCityActorTravelPhase.InTransit,
            ExpectedArrivalAt = now.Advance(duration),
        });
    }

    private void CompleteArrival(NCCityActorRecord actor)
    {
        var location = actor.Snapshot.Location;
        if (!location.DestinationZone.IsValid)
        {
            Finish(actor.Snapshot.Identity.ActorId);
            return;
        }

        BeginMutation(actor, location with
        {
            CurrentZone = location.DestinationZone,
            DestinationZone = default,
            TravelPhase = NCCityActorTravelPhase.Stationary,
            ExpectedArrivalAt = null,
        });
    }

    private void BeginMutation(NCCityActorRecord actor, NCCityActorLocation location)
    {
        var schedule = actor.Snapshot.Schedule;
        var request = new NCCityActorLocationMutationRequest(
            Guid.NewGuid(),
            actor.Snapshot.Identity.ActorId,
            actor.AggregateVersion,
            location.CurrentZone,
            location.DestinationZone,
            location.TravelPhase,
            location.TravelMode,
            location.ExpectedArrivalAt,
            schedule.CurrentActivity,
            schedule.CurrentGoal,
            schedule.Availability,
            schedule.NextUpdateAt,
            Guid.NewGuid(),
            AuditSource: "system:abstract-travel");
        _pendingMutations[request.ActorId] = request;
        BeginMutation(_generation, request);
    }

    private void BeginMutation(int generation, NCCityActorLocationMutationRequest request)
    {
        _ = ObserveMutation(generation, request);
    }

    private async Task ObserveMutation(int generation, NCCityActorLocationMutationRequest request)
    {
        NCCityActorMutationResult? result = null;
        Exception? error = null;
        try
        {
            result = await _actors.SetLocationAsync(request);
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
            Log.Error($"Abstract travel mutation for {actorId} failed before returning a result: {error}");
            Retry(actorId, now, keepPending: true);
            return;
        }

        switch (result.Status)
        {
            case NCCityActorMutationStatus.Applied:
            case NCCityActorMutationStatus.Duplicate:
                _pendingMutations.Remove(actorId);
                if (result.Record != null)
                    ScheduleFromSnapshot(result.Record);
                Finish(actorId, unload: false);
                return;
            case NCCityActorMutationStatus.StorageFailure:
                Retry(actorId, now, keepPending: true);
                return;
            case NCCityActorMutationStatus.VersionConflict:
                _pendingMutations.Remove(actorId);
                Retry(actorId, now);
                return;
            case NCCityActorMutationStatus.ClockUnavailable:
                Finish(actorId);
                return;
            default:
                Log.Error($"Abstract travel mutation for {actorId} was rejected: {result.Error}");
                _pendingMutations.Remove(actorId);
                Finish(actorId);
                return;
        }
    }

    private void Retry(NCActorId actorId, NCActiveWorldTime now, bool keepPending = false)
    {
        if (_policy != null)
            Enqueue(actorId, now.Advance(_policy.RetryDelay));
        if (!keepPending)
            _pendingMutations.Remove(actorId);
        Finish(actorId, unload: false);
    }

    private void Enqueue(NCActorId actorId, NCActiveWorldTime dueAt)
    {
        if (!actorId.IsValid || !dueAt.IsValid)
            return;

        _scheduled[actorId] = dueAt;
        _queue.Enqueue(new ScheduledTravel(actorId, dueAt), new TravelPriority(dueAt.Ticks, _sequence++));
    }

    private void Finish(NCActorId actorId, bool unload = true)
    {
        if (_inFlight > 0)
            _inFlight--;
        if (unload)
            _actors.Unload(actorId);
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
        _pendingMutations.Clear();
    }

    private static bool ValidatePolicy(NCCityAbstractTravelPolicyPrototype policy)
    {
        return policy.SnapshotPageSize is >= 1 and <= 500 &&
               policy.MaximumDispatchesPerTick is >= 1 and <= 64 &&
               policy.MaximumConcurrentMutations is >= 1 and <= 128 &&
               policy.RetryDelay > TimeSpan.Zero &&
               policy.MinimumTravelDuration > TimeSpan.Zero &&
               policy.MaximumTravelDuration >= policy.MinimumTravelDuration &&
               policy.MaximumTravelDuration <= TimeSpan.FromDays(7) &&
               float.IsFinite(policy.DurationMultiplier) && policy.DurationMultiplier > 0f;
    }

    private static bool HasSingleTravelMode(NCCityTravelMode travelMode)
    {
        var raw = (byte) travelMode;
        return raw != 0 && (raw & (raw - 1)) == 0;
    }

    private readonly record struct ScheduledTravel(NCActorId ActorId, NCActiveWorldTime DueAt);

    private readonly record struct TravelPriority(long DueTicks, long Sequence) : IComparable<TravelPriority>
    {
        public int CompareTo(TravelPriority other)
        {
            var due = DueTicks.CompareTo(other.DueTicks);
            return due != 0 ? due : Sequence.CompareTo(other.Sequence);
        }
    }
}
