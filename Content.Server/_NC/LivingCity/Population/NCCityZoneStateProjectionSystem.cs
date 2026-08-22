// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.City.Zones;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Population;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Population;

/// <summary>
/// Active-round district/sector projector. Initial recovery uses bounded keyset pages; steady-state changes are
/// driven by actor persistence events and coalesced per scope. It never scans ECS entities or all actors in Update.
/// </summary>
public sealed partial class NCCityZoneStateProjectionSystem : EntitySystem
{
    private const string DefaultPolicyId = "NCDefaultCityZoneStatePolicy";
    private const int MaximumZoneParentDepth = 16;

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ITaskManager _taskManager = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCCityZoneStateRegistrySystem _registry = default!;

    private readonly Dictionary<NCActorId, ActorContribution> _actorContributions = [];
    private readonly Dictionary<NCZoneId, PopulationBuilder> _population = [];
    private readonly Dictionary<(NCZoneId Scope, NCCityStateSignalKind Kind), NCCityStateSignal> _externalSignals = [];
    private readonly Dictionary<NCZoneId, DirtyState> _dirty = [];
    private readonly PriorityQueue<DirtyScope, DirtyPriority> _dirtyQueue = new();
    private readonly HashSet<NCZoneId> _inFlightScopes = [];
    private readonly Dictionary<NCZoneId, NCCityZoneStateMutationRequest> _pendingRetries = [];

    private NCCityZoneStatePolicyPrototype? _policy;
    private NCSessionEpoch _epoch;
    private NCActorId? _pageCursor;
    private NCActiveWorldTime _pageRetryAt;
    private int _generation;
    private int _inFlightWrites;
    private long _dirtySequence;
    private bool _active;
    private bool _pageLoadInFlight;

    public bool InitialLoadComplete { get; private set; }
    public int IndexedActorCount => _actorContributions.Count;
    public int ScopeCount => _population.Count;
    public int PendingScopeCount => _dirty.Count;
    public int InFlightWriteCount => _inFlightWrites;

    /// <summary>
    /// Copies bounded per-zone observer data from counters already maintained by actor persistence events. This is
    /// request-driven and therefore never scans the persistent actor table or ECS entities.
    /// </summary>
    public void GetObserverProjections(List<NCCityZoneObserverProjection> result)
    {
        result.Clear();
        if (_policy == null)
            return;

        foreach (var population in _population.Values)
        {
            var saturation = population.ScopeSlot == NCZoneContextSlot.District
                ? _policy.DistrictActivitySaturation
                : _policy.SectorActivitySaturation;
            result.Add(new NCCityZoneObserverProjection(
                population.ScopeZone,
                population.ScopeSlot,
                population.TotalActors,
                population.LivingActors,
                population.FrozenActors,
                population.TravelingActors,
                population.Presence(NCCityActorPresenceMode.Public),
                population.Presence(NCCityActorPresenceMode.AbstractHome),
                population.Presence(NCCityActorPresenceMode.PhysicalHome),
                Math.Clamp((float) population.WeightedActivityUnits / saturation, 0f, 1f),
                Signal(population.ScopeZone, NCCityStateSignalKind.Scarcity),
                Signal(population.ScopeZone, NCCityStateSignalKind.Threat),
                Signal(population.ScopeZone, NCCityStateSignalKind.Control)));
        }

        result.Sort(static (left, right) => left.ScopeZone.Value.CompareTo(right.ScopeZone.Value));
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(OnSessionClosed);
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(OnClockFailed);
        SubscribeLocalEvent<NCCityActorPersistedEvent>(OnActorPersisted);
        SubscribeLocalEvent<NCZoneIndexRebuiltEvent>(OnZoneIndexRebuilt);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_active || _policy == null ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
        {
            return;
        }

        if (!InitialLoadComplete)
        {
            if (!_pageLoadInFlight && now >= _pageRetryAt)
                BeginPageLoad(_generation, _pageCursor);
            return;
        }

        var dispatched = 0;
        var examined = 0;
        var maximumExamined = Math.Max(_policy.MaximumWritesPerTick * 4, 8);
        while (dispatched < _policy.MaximumWritesPerTick &&
               _inFlightWrites < _policy.MaximumConcurrentWrites &&
               examined++ < maximumExamined &&
               _dirtyQueue.TryPeek(out var queued, out var priority) &&
               priority.DueTicks <= now.Ticks)
        {
            _dirtyQueue.Dequeue();
            if (!_dirty.TryGetValue(queued.ScopeZone, out var current) || current.Sequence != queued.Sequence)
                continue;

            if (_inFlightScopes.Contains(queued.ScopeZone))
            {
                // Completion will requeue the retained dirty state once the older snapshot leaves flight.
                continue;
            }

            _dirty.Remove(queued.ScopeZone);
            BeginPrepareWrite(_generation, queued.ScopeZone);
            dispatched++;
        }
    }

    /// <summary>
    /// Accepts a normalized signal from its future authoritative domain. Calls must originate on the simulation
    /// thread; source/version ownership remains with that domain, not with this read model.
    /// </summary>
    public bool PublishSignal(NCCityStateSignalUpdate update)
    {
        if (!_active || _policy == null || update.Kind == NCCityStateSignalKind.Activity ||
            !update.ScopeZone.IsValid || string.IsNullOrWhiteSpace(update.Source) || update.Source.Length > 128 ||
            update.SourceVersion < 0 || !float.IsFinite(update.Value) || update.Value is < 0f or > 1f ||
            update.Availability == NCCityStateSignalAvailability.Unavailable && update.Value != 0f ||
            !_population.ContainsKey(update.ScopeZone))
        {
            return false;
        }

        var key = (update.ScopeZone, update.Kind);
        if (_externalSignals.TryGetValue(key, out var current) &&
            current.Source == update.Source && current.SourceVersion >= update.SourceVersion)
        {
            return false;
        }

        _externalSignals[key] = new NCCityStateSignal(
            update.Kind,
            update.Availability,
            update.Availability == NCCityStateSignalAvailability.Available ? update.Value : 0f,
            update.Source.Trim(),
            update.SourceVersion);
        if (_clock.TryGetCurrentTime(out var epoch, out var now) && epoch == _epoch)
            MarkDirty(update.ScopeZone, now.Advance(_policy.CoalesceDelay));
        return true;
    }

    public bool ForceRebuild()
    {
        if (!_active || !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
            return false;
        RestartInitialLoad(now);
        return true;
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        Reset();
        if (!_prototypes.TryIndex(
                new ProtoId<NCCityZoneStatePolicyPrototype>(DefaultPolicyId),
                out NCCityZoneStatePolicyPrototype? policy) || policy == null || !ValidatePolicy(policy))
        {
            Log.Error($"City zone-state projector cannot start: policy {DefaultPolicyId} is missing or invalid.");
            return;
        }

        _policy = policy;
        _epoch = args.Epoch;
        _active = true;
        RestartInitialLoad(args.Time);
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

    private void OnZoneIndexRebuilt(ref NCZoneIndexRebuiltEvent args)
    {
        if (_active && _clock.TryGetCurrentTime(out var epoch, out var now) && epoch == _epoch)
            RestartInitialLoad(now);
    }

    private void OnActorPersisted(NCCityActorPersistedEvent args)
    {
        if (!_active || _policy == null || args.SessionEpoch != _epoch)
            return;

        ApplyActor(new ActorContribution(
            args.ActorId,
            args.AggregateVersion,
            args.CurrentZone,
            ResolveScope(args.CurrentZone, NCZoneContextSlot.District),
            ResolveScope(args.CurrentZone, NCZoneContextSlot.Sector),
            args.ArchetypeId,
            args.Status,
            args.CognitionTier,
            args.Availability,
            args.Frozen,
            args.TravelPhase,
            args.PresenceMode),
            InitialLoadComplete);
    }

    private void RestartInitialLoad(NCActiveWorldTime now)
    {
        _generation++;
        _actorContributions.Clear();
        _population.Clear();
        _dirty.Clear();
        _dirtyQueue.Clear();
        _inFlightScopes.Clear();
        _pendingRetries.Clear();
        _inFlightWrites = 0;
        _pageCursor = null;
        _pageRetryAt = now;
        _pageLoadInFlight = false;
        InitialLoadComplete = false;

        var zones = new List<NCZoneInfo>();
        _zones.GetZones(zones);
        foreach (var zone in zones)
        {
            if (zone.ContextSlot is NCZoneContextSlot.District or NCZoneContextSlot.Sector)
                _population.TryAdd(zone.Id, new PopulationBuilder(zone.Id, zone.ContextSlot));
        }

        BeginPageLoad(_generation, null);
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
            Log.Error($"City zone-state actor page load failed: {error}");
            if (_clock.TryGetCurrentTime(out var epoch, out var now) && epoch == _epoch)
                _pageRetryAt = now.Advance(_policy.StorageRetryDelay);
            return;
        }

        foreach (var record in page.Records)
        {
            var actor = record.Snapshot;
            ApplyActor(new ActorContribution(
                actor.Identity.ActorId,
                record.AggregateVersion,
                actor.Location.CurrentZone,
                ResolveScope(actor.Location.CurrentZone, NCZoneContextSlot.District),
                ResolveScope(actor.Location.CurrentZone, NCZoneContextSlot.Sector),
                actor.Identity.ArchetypeId,
                actor.Lifecycle.Status,
                actor.CognitionTier,
                actor.Schedule.Availability,
                actor.Lifecycle.Frozen,
                actor.Location.TravelPhase,
                actor.Schedule.PresenceMode),
                false);
        }

        if (page.HasMore && page.ContinuationActorId is { IsValid: true } continuation)
        {
            _pageCursor = continuation;
            BeginPageLoad(generation, continuation);
            return;
        }

        InitialLoadComplete = true;
        _pageCursor = null;
        if (!_clock.TryGetCurrentTime(out var currentEpoch, out var current) || currentEpoch != _epoch)
            return;
        foreach (var scope in _population.Keys)
            MarkDirty(scope, current.Advance(_policy.CoalesceDelay));
    }

    private void ApplyActor(ActorContribution candidate, bool markDirty)
    {
        if (_policy == null || !candidate.ActorId.IsValid || candidate.AggregateVersion < 1)
            return;
        if (_actorContributions.TryGetValue(candidate.ActorId, out var previous) &&
            previous.AggregateVersion >= candidate.AggregateVersion)
        {
            return;
        }

        if (previous.ActorId.IsValid)
            ApplyToScopes(previous, -1);
        _actorContributions[candidate.ActorId] = candidate;
        ApplyToScopes(candidate, 1);

        if (!markDirty || !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
            return;
        if (previous.ActorId.IsValid)
        {
            MarkContributionScopes(previous, now.Advance(_policy.CoalesceDelay));
        }
        MarkContributionScopes(candidate, now.Advance(_policy.CoalesceDelay));
    }

    private void ApplyToScopes(ActorContribution actor, int delta)
    {
        if (_policy == null || actor.Status == NCCityActorStatus.Archived)
            return;
        if (actor.District.IsValid && _population.TryGetValue(actor.District, out var district))
            district.Apply(actor, delta, _policy);
        if (actor.Sector.IsValid && actor.Sector != actor.District &&
            _population.TryGetValue(actor.Sector, out var sector))
        {
            sector.Apply(actor, delta, _policy);
        }
    }

    private void MarkContributionScopes(ActorContribution actor, NCActiveWorldTime dueAt)
    {
        if (actor.District.IsValid && _population.ContainsKey(actor.District))
            MarkDirty(actor.District, dueAt);
        if (actor.Sector.IsValid && actor.Sector != actor.District && _population.ContainsKey(actor.Sector))
            MarkDirty(actor.Sector, dueAt);
    }

    private void MarkDirty(NCZoneId scopeZone, NCActiveWorldTime dueAt)
    {
        var state = new DirtyState(++_dirtySequence, dueAt);
        _dirty[scopeZone] = state;
        if (!_inFlightScopes.Contains(scopeZone))
            _dirtyQueue.Enqueue(new DirtyScope(scopeZone, state.Sequence), new DirtyPriority(dueAt.Ticks, state.Sequence));
    }

    private void BeginPrepareWrite(int generation, NCZoneId scopeZone)
    {
        _inFlightScopes.Add(scopeZone);
        _inFlightWrites++;
        if (_pendingRetries.TryGetValue(scopeZone, out var pending))
        {
            BeginPersist(generation, pending);
            return;
        }

        _ = ObserveExisting(generation, scopeZone);
    }

    private async Task ObserveExisting(int generation, NCZoneId scopeZone)
    {
        NCCityZoneStateRecord? existing = null;
        Exception? error = null;
        try
        {
            existing = await _registry.GetAsync(scopeZone, true);
        }
        catch (Exception exception)
        {
            error = exception;
        }

        _taskManager.RunOnMainThread(() => CompleteExisting(generation, scopeZone, existing, error));
    }

    private void CompleteExisting(
        int generation,
        NCZoneId scopeZone,
        NCCityZoneStateRecord? existing,
        Exception? error)
    {
        if (generation != _generation || !_active || _policy == null)
            return;
        if (error != null || !_population.TryGetValue(scopeZone, out var population) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
        {
            if (error != null)
                Log.Error($"Failed to load current city zone state {scopeZone}: {error}");
            FinishWriteSlot(scopeZone, true, _policy.StorageRetryDelay);
            return;
        }

        var expectedVersion = existing?.AggregateVersion ?? 0;
        var populationSnapshot = population.Build(_policy);
        var eventId = Guid.NewGuid();
        var snapshot = new NCCityZoneStateSnapshot(
            scopeZone,
            population.ScopeSlot,
            populationSnapshot,
            BuildSignals(scopeZone, population.ScopeSlot, populationSnapshot, existing?.Snapshot, expectedVersion + 1),
            now,
            1,
            eventId);
        var request = new NCCityZoneStateMutationRequest(
            Guid.NewGuid(),
            expectedVersion,
            snapshot,
            Guid.NewGuid());
        _pendingRetries[scopeZone] = request;
        BeginPersist(generation, request);
    }

    private void BeginPersist(int generation, NCCityZoneStateMutationRequest request)
    {
        _ = ObservePersist(generation, request);
    }

    private async Task ObservePersist(int generation, NCCityZoneStateMutationRequest request)
    {
        NCCityZoneStateMutationResult? result = null;
        Exception? error = null;
        try
        {
            result = await _registry.PersistAsync(request);
        }
        catch (Exception exception)
        {
            error = exception;
        }

        _taskManager.RunOnMainThread(() => CompletePersist(generation, request.Snapshot.ScopeZone, result, error));
    }

    private void CompletePersist(
        int generation,
        NCZoneId scopeZone,
        NCCityZoneStateMutationResult? result,
        Exception? error)
    {
        if (generation != _generation || !_active || _policy == null)
            return;

        if (error != null || result == null)
        {
            Log.Error($"City zone-state projection write failed for {scopeZone}: {error}");
            FinishWriteSlot(scopeZone, true, _policy.StorageRetryDelay);
            return;
        }

        switch (result.Status)
        {
            case NCCityZoneStateMutationStatus.Applied:
            case NCCityZoneStateMutationStatus.Duplicate:
                _pendingRetries.Remove(scopeZone);
                FinishWriteSlot(scopeZone, false, TimeSpan.Zero);
                break;
            case NCCityZoneStateMutationStatus.VersionConflict:
                // Reload and rebuild against the winner instead of retrying a stale expected version.
                _pendingRetries.Remove(scopeZone);
                FinishWriteSlot(scopeZone, true, TimeSpan.Zero);
                break;
            case NCCityZoneStateMutationStatus.StorageFailure:
                // Reuse the exact command ID and immutable snapshot after an ambiguous storage failure.
                FinishWriteSlot(scopeZone, true, _policy.StorageRetryDelay);
                break;
            case NCCityZoneStateMutationStatus.ClockUnavailable:
                FinishWriteSlot(scopeZone, true, _policy.StorageRetryDelay);
                break;
            default:
                _pendingRetries.Remove(scopeZone);
                Log.Error($"City zone-state projection for {scopeZone} was rejected: {result.Error}");
                FinishWriteSlot(scopeZone, false, TimeSpan.Zero);
                break;
        }
    }

    private void FinishWriteSlot(NCZoneId scopeZone, bool ensureDirty, TimeSpan delay)
    {
        _inFlightScopes.Remove(scopeZone);
        _inFlightWrites = Math.Max(0, _inFlightWrites - 1);
        if (!_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
            return;
        if (ensureDirty)
            MarkDirty(scopeZone, now.Advance(delay));
        else if (_dirty.TryGetValue(scopeZone, out var retained))
            _dirtyQueue.Enqueue(new DirtyScope(scopeZone, retained.Sequence),
                new DirtyPriority(retained.DueAt.Ticks, retained.Sequence));
    }

    private NCCityStateSignal[] BuildSignals(
        NCZoneId scopeZone,
        NCZoneContextSlot scopeSlot,
        NCCityPopulationAggregate population,
        NCCityZoneStateSnapshot? existing,
        long populationSourceVersion)
    {
        var signals = existing?.Signals.ToDictionary(signal => signal.Kind) ?? [];
        foreach (var pair in _externalSignals)
        {
            if (pair.Key.Scope != scopeZone)
                continue;
            if (!signals.TryGetValue(pair.Key.Kind, out var current) || current.Source != pair.Value.Source ||
                pair.Value.SourceVersion > current.SourceVersion)
            {
                signals[pair.Key.Kind] = pair.Value;
            }
        }

        var saturation = scopeSlot == NCZoneContextSlot.District
            ? _policy!.DistrictActivitySaturation
            : _policy!.SectorActivitySaturation;
        signals[NCCityStateSignalKind.Activity] = new NCCityStateSignal(
            NCCityStateSignalKind.Activity,
            NCCityStateSignalAvailability.Available,
            Math.Clamp(population.WeightedActivityUnits / saturation, 0f, 1f),
            NCCityZoneStateRegistrySystem.AggregateType,
            populationSourceVersion);

        foreach (var kind in Enum.GetValues<NCCityStateSignalKind>())
        {
            if (!signals.ContainsKey(kind))
            {
                signals[kind] = new NCCityStateSignal(
                    kind,
                    NCCityStateSignalAvailability.Unavailable,
                    0f,
                    "unbound",
                    0);
            }
        }

        return signals.Values.OrderBy(signal => signal.Kind).ToArray();
    }

    private NCZoneId ResolveScope(NCZoneId zoneId, NCZoneContextSlot requestedSlot)
    {
        var current = zoneId;
        var visited = new HashSet<NCZoneId>();
        for (var depth = 0; depth < MaximumZoneParentDepth && current.IsValid && visited.Add(current); depth++)
        {
            if (!_zones.TryGetZone(current, out var zone))
                return default;
            if (zone.ContextSlot == requestedSlot)
                return zone.Id;
            current = zone.Parent;
        }

        return default;
    }

    private float Signal(NCZoneId scopeZone, NCCityStateSignalKind kind)
    {
        return _externalSignals.TryGetValue((scopeZone, kind), out var signal) &&
               signal.Availability == NCCityStateSignalAvailability.Available
            ? signal.Value
            : -1f;
    }

    private static bool ValidatePolicy(NCCityZoneStatePolicyPrototype policy)
    {
        return policy.SnapshotPageSize is > 0 and <= 500 &&
               policy.MaximumWritesPerTick > 0 && policy.MaximumConcurrentWrites > 0 &&
               policy.MaximumWritesPerTick <= policy.MaximumConcurrentWrites &&
               policy.CoalesceDelay >= TimeSpan.Zero && policy.StorageRetryDelay > TimeSpan.Zero &&
               policy.MaximumArchetypesPerScope > 0 &&
               !string.IsNullOrWhiteSpace(policy.ArchetypeOverflowBucketId) &&
               policy.ArchetypeOverflowBucketId.Length <= 128 &&
               float.IsFinite(policy.DistrictActivitySaturation) && policy.DistrictActivitySaturation > 0f &&
               float.IsFinite(policy.SectorActivitySaturation) && policy.SectorActivitySaturation > 0f &&
               policy.ActivityStatusWeights.Count > 0 && policy.ActivityAvailabilityWeights.Count > 0 &&
               policy.ActivityStatusWeights.Values.All(static value => float.IsFinite(value) && value >= 0f) &&
               policy.ActivityAvailabilityWeights.Values.All(static value => float.IsFinite(value) && value >= 0f);
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
        InitialLoadComplete = false;
        _inFlightWrites = 0;
        _actorContributions.Clear();
        _population.Clear();
        _externalSignals.Clear();
        _dirty.Clear();
        _dirtyQueue.Clear();
        _inFlightScopes.Clear();
        _pendingRetries.Clear();
    }

    private readonly record struct ActorContribution(
        NCActorId ActorId,
        long AggregateVersion,
        NCZoneId CurrentZone,
        NCZoneId District,
        NCZoneId Sector,
        string ArchetypeId,
        NCCityActorStatus Status,
        NCCityActorCognitionTier CognitionTier,
        NCCityActorAvailability Availability,
        bool Frozen,
        NCCityActorTravelPhase TravelPhase,
        NCCityActorPresenceMode PresenceMode);

    private readonly record struct DirtyScope(NCZoneId ScopeZone, long Sequence);
    private readonly record struct DirtyState(long Sequence, NCActiveWorldTime DueAt);
    private readonly record struct DirtyPriority(long DueTicks, long Sequence) : IComparable<DirtyPriority>
    {
        public int CompareTo(DirtyPriority other)
        {
            var due = DueTicks.CompareTo(other.DueTicks);
            return due != 0 ? due : Sequence.CompareTo(other.Sequence);
        }
    }

    private sealed class PopulationBuilder(NCZoneId scopeZone, NCZoneContextSlot scopeSlot)
    {
        private readonly Dictionary<NCCityActorStatus, int> _statuses = [];
        private readonly Dictionary<NCCityActorCognitionTier, int> _cognition = [];
        private readonly Dictionary<NCCityActorAvailability, int> _availability = [];
        private readonly Dictionary<NCCityActorPresenceMode, int> _presence = [];
        private readonly Dictionary<string, int> _archetypes = new(StringComparer.Ordinal);

        public NCZoneId ScopeZone { get; } = scopeZone;
        public NCZoneContextSlot ScopeSlot { get; } = scopeSlot;
        public int TotalActors { get; private set; }
        public int LivingActors { get; private set; }
        public int DeadActors { get; private set; }
        public int FrozenActors { get; private set; }
        public int TravelingActors { get; private set; }
        public double WeightedActivityUnits { get; private set; }

        public void Apply(ActorContribution actor, int delta, NCCityZoneStatePolicyPrototype policy)
        {
            TotalActors += delta;
            if (actor.Status == NCCityActorStatus.Dead)
                DeadActors += delta;
            else
                LivingActors += delta;
            if (actor.Frozen)
                FrozenActors += delta;
            if (actor.TravelPhase is NCCityActorTravelPhase.Planned or NCCityActorTravelPhase.InTransit or
                NCCityActorTravelPhase.Blocked)
            {
                TravelingActors += delta;
            }

            Adjust(_statuses, actor.Status, delta);
            Adjust(_cognition, actor.CognitionTier, delta);
            Adjust(_availability, actor.Availability, delta);
            Adjust(_presence, actor.PresenceMode, delta);
            Adjust(_archetypes, actor.ArchetypeId, delta);

            var statusWeight = policy.ActivityStatusWeights.GetValueOrDefault(actor.Status);
            var availabilityWeight = policy.ActivityAvailabilityWeights.GetValueOrDefault(actor.Availability);
            WeightedActivityUnits = Math.Max(0d,
                WeightedActivityUnits + delta * statusWeight * availabilityWeight);
        }

        public NCCityPopulationAggregate Build(NCCityZoneStatePolicyPrototype policy)
        {
            var orderedArchetypes = _archetypes
                .Where(pair => pair.Value > 0)
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .ToArray();
            var archetypes = new List<NCCityPopulationArchetypeCount>();
            var directCount = Math.Min(orderedArchetypes.Length, policy.MaximumArchetypesPerScope);
            if (orderedArchetypes.Length > policy.MaximumArchetypesPerScope)
                directCount = Math.Max(0, policy.MaximumArchetypesPerScope - 1);
            for (var i = 0; i < directCount; i++)
                archetypes.Add(new NCCityPopulationArchetypeCount(orderedArchetypes[i].Key, orderedArchetypes[i].Value));
            if (directCount < orderedArchetypes.Length)
            {
                archetypes.Add(new NCCityPopulationArchetypeCount(
                    policy.ArchetypeOverflowBucketId,
                    orderedArchetypes.Skip(directCount).Sum(pair => pair.Value)));
            }

            return new NCCityPopulationAggregate(
                TotalActors,
                LivingActors,
                DeadActors,
                FrozenActors,
                TravelingActors,
                (float) WeightedActivityUnits,
                _statuses.Where(pair => pair.Value > 0)
                    .OrderBy(pair => pair.Key)
                    .Select(pair => new NCCityPopulationStatusCount(pair.Key, pair.Value)).ToArray(),
                _cognition.Where(pair => pair.Value > 0)
                    .OrderBy(pair => pair.Key)
                    .Select(pair => new NCCityPopulationCognitionCount(pair.Key, pair.Value)).ToArray(),
                _availability.Where(pair => pair.Value > 0)
                    .OrderBy(pair => pair.Key)
                    .Select(pair => new NCCityPopulationAvailabilityCount(pair.Key, pair.Value)).ToArray(),
                archetypes.ToArray());
        }

        public int Presence(NCCityActorPresenceMode mode) => _presence.GetValueOrDefault(mode);

        private static void Adjust<TKey>(Dictionary<TKey, int> counters, TKey key, int delta) where TKey : notnull
        {
            var next = counters.GetValueOrDefault(key) + delta;
            if (next <= 0)
                counters.Remove(key);
            else
                counters[key] = next;
        }
    }
}
