// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._NC.City.Connectivity;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Actors.Domains;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Simulation;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Connectivity;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Server.Player;
using Robust.Shared.Asynchronous;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Actors;

/// <summary>
/// Active-round request queue for persistent actor -> ECS rehydration. Database loads are bounded and asynchronous;
/// candidate selection, visibility checks, spawning, binding and adapter restore remain on the simulation thread.
/// Update touches only due requests and never scans all actors or entities.
/// </summary>
public sealed partial class NCCityActorMaterializationSystem : EntitySystem
{
    private const string DefaultPolicyId = "NCDefaultCityMaterializationPolicy";
    private const int MaximumSourceLength = 128;
    private const float MaximumPreferredRadius = 128f;

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ITaskManager _taskManager = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private MetaDataSystem _metadata = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCCityActorDomainRegistrySystem _domains = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCCityRelevanceAnchorIndexSystem _anchorIndex = default!;
    [Dependency] private NCCitySpawnCandidateSystem _spawnCandidates = default!;
    [Dependency] private NCAILoadGovernorSystem _loadGovernor = default!;
    [Dependency] private NCEconomicPhysicalizationSystem _physicalization = default!;

    private readonly Dictionary<Guid, NCCityMaterializationRequestState> _states = [];
    private readonly Dictionary<Guid, PendingRequest> _pending = [];
    private readonly Dictionary<NCActorId, Guid> _requestByActor = [];
    private readonly Queue<Guid> _completedOrder = [];
    private readonly PriorityQueue<QueuedRequest, RequestPriority> _queue = new();
    private readonly List<NCCitySpawnCandidateInfo> _candidateBuffer = [];
    private readonly List<EntityUid> _anchorBuffer = [];

    private NCCityMaterializationPolicyPrototype? _policy;
    private NCSessionEpoch _epoch;
    private int _generation;
    private int _inFlight;
    private long _sequence;
    private bool _active;

    public bool IsActive => _active;
    public int PendingRequestCount => _pending.Count;
    public int InFlightCount => _inFlight;
    public int RetainedResultCount => _states.Count - _pending.Count;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(OnSessionClosed);
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(OnClockFailed);
        SubscribeLocalEvent<NCCitySpawnCandidateIndexChangedEvent>(OnSpawnIndexChanged);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_active || _policy == null ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
        {
            return;
        }

        using var work = _loadGovernor.MeasureWork(NCAILoadWorkKind.ActorMaterialization);
        _loadGovernor.ReportMaterializedActors(_runtime.MaterializedActorCount);
        _loadGovernor.ReportQueue(
            NCAILoadQueueKind.Materialization,
            _pending.Count,
            _policy.MaximumQueuedRequests);
        var dispatchLimit = _loadGovernor.TakeDispatchLimit(
            NCAILoadDispatchKind.ActorMaterialization,
            _policy.MaximumDispatchesPerTick);
        var dispatched = 0;
        while (dispatched < dispatchLimit &&
               _inFlight < _policy.MaximumConcurrentLoads &&
               _queue.TryPeek(out var queued, out var priority) && priority.DueTicks <= now.Ticks)
        {
            _queue.Dequeue();
            if (!_pending.TryGetValue(queued.RequestId, out var pending) || pending.Sequence != queued.Sequence)
                continue;
            if (pending.ExpiresAt < now)
            {
                CompleteTerminal(pending, NCCityMaterializationRequestStatus.Expired,
                    "The materialization request expired before a safe spawn became available.");
                continue;
            }

            if (_runtime.TryGetEntity(pending.Request.ActorId, out var existing) && Exists(existing))
            {
                CompleteTerminal(pending, NCCityMaterializationRequestStatus.AlreadyMaterialized, null, existing);
                continue;
            }

            pending = pending with { Attempts = pending.Attempts + 1 };
            _pending[pending.Request.RequestId] = pending;
            SetState(pending, NCCityMaterializationRequestStatus.Loading, null);
            _inFlight++;
            dispatched++;
            _ = ObserveLoad(_generation, pending);
        }
    }

    public NCCityMaterializationRequestState Request(NCCityMaterializationRequest request)
    {
        if (_states.TryGetValue(request.RequestId, out var replay))
            return replay;
        var now = default(NCActiveWorldTime);
        if (!_active || _policy == null ||
            !_clock.TryGetCurrentTime(out var epoch, out now) || epoch != _epoch)
        {
            return new NCCityMaterializationRequestState(
                request.RequestId,
                request.ActorId,
                request.Reason,
                NCCityMaterializationRequestStatus.Rejected,
                request.Priority,
                0,
                now,
                now,
                default,
                "Materialization requires an active A-Life round.");
        }

        if (!TryValidateRequest(request, _policy, out var error))
        {
            var rejected = new NCCityMaterializationRequestState(
                request.RequestId,
                request.ActorId,
                request.Reason,
                NCCityMaterializationRequestStatus.Rejected,
                request.Priority,
                0,
                now,
                now,
                default,
                error);
            if (request.RequestId != Guid.Empty)
                RetainTerminal(rejected);
            return rejected;
        }

        if (_runtime.TryGetEntity(request.ActorId, out var existing) && Exists(existing))
        {
            var already = new NCCityMaterializationRequestState(
                request.RequestId,
                request.ActorId,
                request.Reason,
                NCCityMaterializationRequestStatus.AlreadyMaterialized,
                request.Priority,
                0,
                now,
                now,
                existing,
                null);
            RetainTerminal(already);
            return already;
        }

        if (_requestByActor.TryGetValue(request.ActorId, out var activeRequest) &&
            _states.TryGetValue(activeRequest, out var activeState))
        {
            var rejected = new NCCityMaterializationRequestState(
                request.RequestId,
                request.ActorId,
                request.Reason,
                NCCityMaterializationRequestStatus.Rejected,
                request.Priority,
                0,
                now,
                now,
                default,
                $"Actor already has active materialization request {activeState.RequestId}.");
            RetainTerminal(rejected);
            return rejected;
        }

        if (_pending.Count >= _policy.MaximumQueuedRequests)
        {
            var rejected = new NCCityMaterializationRequestState(
                request.RequestId,
                request.ActorId,
                request.Reason,
                NCCityMaterializationRequestStatus.Rejected,
                request.Priority,
                0,
                now,
                now,
                default,
                "The bounded materialization queue is full.");
            RetainTerminal(rejected);
            return rejected;
        }

        var protectedRequest = request.Reason is NCCityMaterializationReason.Incident or
            NCCityMaterializationReason.ServiceResponse or NCCityMaterializationReason.Admin;
        // Admission uses the current binding index, not the governor's previous periodic sample.
        _loadGovernor.ReportMaterializedActors(_runtime.MaterializedActorCount);
        if (!_loadGovernor.TryReserveMaterialization(
                request.RequestId,
                1,
                request.Priority,
                protectedRequest,
                out var governorError))
        {
            var rejected = new NCCityMaterializationRequestState(
                request.RequestId,
                request.ActorId,
                request.Reason,
                NCCityMaterializationRequestStatus.Rejected,
                request.Priority,
                0,
                now,
                now,
                default,
                governorError);
            RetainTerminal(rejected);
            return rejected;
        }

        var wait = request.MaximumWait ?? _policy.DefaultMaximumWait;
        var pending = new PendingRequest(request, now, now.Advance(wait), 0, ++_sequence, now);
        _pending.Add(request.RequestId, pending);
        _requestByActor.Add(request.ActorId, request.RequestId);
        SetState(pending, NCCityMaterializationRequestStatus.Queued, null);
        Enqueue(pending);
        return _states[request.RequestId];
    }

    public bool TryGetRequest(Guid requestId, out NCCityMaterializationRequestState state)
    {
        return _states.TryGetValue(requestId, out state!);
    }

    private async Task ObserveLoad(int generation, PendingRequest pending)
    {
        NCCityActorRecord? actor = null;
        NCCityActorHealthRecord? health = null;
        NCCityActorInventoryRecord? inventory = null;
        NCActorInventoryMaterializationPlan? inventoryPlan = null;
        Exception? error = null;
        try
        {
            var actorTask = _actors.GetAsync(pending.Request.ActorId, true);
            var healthTask = _domains.GetHealthAsync(pending.Request.ActorId, true);
            var inventoryTask = _domains.GetInventoryAsync(pending.Request.ActorId, true);
            await Task.WhenAll(actorTask, healthTask, inventoryTask);
            actor = await actorTask;
            health = await healthTask;
            inventory = await inventoryTask;
            if (inventory != null)
            {
                var plan = await _physicalization.BuildActorInventoryPlanAsync(
                    pending.Request.ActorId,
                    inventory.Snapshot);
                inventoryPlan = plan.Plan;
                if (!plan.IsSuccess)
                    error = new InvalidOperationException(plan.Error);
            }
        }
        catch (Exception exception)
        {
            error = exception;
        }

        _taskManager.RunOnMainThread(() => CompleteLoad(generation, pending.Request.RequestId,
            actor, health, inventory, inventoryPlan, error));
    }

    private void CompleteLoad(
        int generation,
        Guid requestId,
        NCCityActorRecord? actor,
        NCCityActorHealthRecord? health,
        NCCityActorInventoryRecord? inventory,
        NCActorInventoryMaterializationPlan? inventoryPlan,
        Exception? loadError)
    {
        if (generation != _generation || !_active || _policy == null ||
            !_pending.TryGetValue(requestId, out var pending))
        {
            return;
        }

        _inFlight = Math.Max(0, _inFlight - 1);
        if (!_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
            return;
        if (loadError != null)
        {
            Retry(pending, now, $"Persistent actor load failed: {loadError.Message}");
            return;
        }
        if (actor == null || health == null || inventory == null || inventoryPlan == null)
        {
            CompleteTerminal(pending, NCCityMaterializationRequestStatus.Rejected,
                "The actor root or one of its mandatory health/inventory aggregates is missing or unreadable.");
            return;
        }

        var snapshot = actor.Snapshot;
        if (snapshot.Lifecycle.Status is NCCityActorStatus.Dead or NCCityActorStatus.Archived ||
            snapshot.Lifecycle.Frozen)
        {
            CompleteTerminal(pending, NCCityMaterializationRequestStatus.Rejected,
                "Dead, archived or frozen actors cannot be materialized.");
            return;
        }
        // An abstract residence has no map coordinates. Reject before spawning so incident requests cannot leave
        // an armed but rejected ECS entity bound to the persistent actor.
        if (snapshot.Schedule.PresenceMode == NCCityActorPresenceMode.AbstractHome &&
            snapshot.Location.TravelPhase == NCCityActorTravelPhase.Stationary)
        {
            CompleteTerminal(pending, NCCityMaterializationRequestStatus.Rejected,
                "An actor at an abstract residence has no physical map entity.");
            return;
        }
        if (!_prototypes.TryIndex(
                new ProtoId<NCCityActorArchetypePrototype>(snapshot.Identity.ArchetypeId),
                out NCCityActorArchetypePrototype? archetype) || archetype == null)
        {
            CompleteTerminal(pending, NCCityMaterializationRequestStatus.Rejected,
                $"Actor archetype {snapshot.Identity.ArchetypeId} is missing.");
            return;
        }
        if (!TrySelectSafeCandidate(archetype, snapshot.Location.CurrentZone,
                pending.Request.SelectionSeed + (uint) pending.Attempts, pending.Request, out var candidate) ||
            !_coordinates.TryResolve(candidate.Target, out var spawnCoordinates))
        {
            Retry(pending, now, "No hidden, unoccupied spawn candidate is currently available.");
            return;
        }

        EntityUid entity;
        try
        {
            entity = Spawn(archetype.EntityPrototype, spawnCoordinates);
        }
        catch (Exception exception)
        {
            CompleteTerminal(pending, NCCityMaterializationRequestStatus.Rejected,
                $"Entity prototype {archetype.EntityPrototype} could not be spawned: {exception.Message}");
            return;
        }

        _metadata.SetEntityName(entity, snapshot.Identity.FullName);
        if (!_runtime.TryBind(snapshot.Identity.ActorId, entity))
        {
            QueueDel(entity);
            Retry(pending, now, "The one-to-one persistent actor binding was concurrently claimed.");
            return;
        }

        // The empty-inventory path is still authoritative: install its source revision so a later atomic
        // dematerialization can prove that the physical containment graph has not changed since rehydration.
        var inventoryAuthority = EnsureComp<NCCityActorInventoryAdapterComponent>(entity);
        inventoryAuthority.AuthorityVersion = inventory.Snapshot.SourceAuthorityVersion;
        inventoryAuthority.EquippedLoadoutId = inventory.Snapshot.Summary.EquippedLoadoutId;

        if (!string.IsNullOrWhiteSpace(health.Snapshot.MedicalStatePayload) &&
            !_domains.TryRestoreHealth(snapshot.Identity.ActorId, entity, health))
        {
            _runtime.Unbind(entity);
            QueueDel(entity);
            CompleteTerminal(pending, NCCityMaterializationRequestStatus.Rejected,
                "The actor's authoritative medical state could not be restored.");
            return;
        }
        if (!_physicalization.TryMaterialize(entity, inventoryPlan, out var inventoryError))
        {
            _runtime.Unbind(entity);
            QueueDel(entity);
            CompleteTerminal(pending, NCCityMaterializationRequestStatus.Rejected,
                inventoryError ?? "The actor's authoritative economic inventory could not be materialized.");
            return;
        }

        // Every physical actor has an explicit owner. Automatic relevance may later hand it back to the
        // strategic simulation; admin projections remain manual until an administrator removes them.
        var lease = EnsureComp<NCCityMaterializationLeaseComponent>(entity);
        lease.Reason = pending.Request.Reason;
        lease.MaterializedAt = now;
        lease.LastRelevantAt = now;

        CompleteTerminal(pending, NCCityMaterializationRequestStatus.Materialized, null, entity);
        RaiseLocalEvent(new NCCityActorRehydratedEvent(
            requestId,
            snapshot.Identity.ActorId,
            entity,
            pending.Request.Reason));
    }

    private bool TrySelectSafeCandidate(
        NCCityActorArchetypePrototype archetype,
        Content.Shared._NC.City.Zones.NCZoneId zoneId,
        uint seed,
        NCCityMaterializationRequest request,
        out NCCitySpawnCandidateInfo selected)
    {
        selected = default;
        _spawnCandidates.GetCandidatesInHierarchy(archetype.SpawnCandidateProfile, zoneId, _candidateBuffer);
        if (_candidateBuffer.Count == 0 || _policy == null)
            return false;

        var count = Math.Min(_candidateBuffer.Count, _policy.MaximumCandidateChecks);
        var start = (int) (seed % (uint) _candidateBuffer.Count);
        for (var i = 0; i < count; i++)
        {
            var candidate = _candidateBuffer[(start + i) % _candidateBuffer.Count];
            if (!_coordinates.TryResolve(candidate.Target, out var mapCoordinates) ||
                IsNearPlayerOrAnchor(mapCoordinates, _policy.PlayerVisibilityExclusionRadius) ||
                request.Reason == NCCityMaterializationReason.PlayerRelevance &&
                !IsNearPlayerOrAnchor(mapCoordinates, _policy.PlayerRelevanceMaximumRadius) ||
                request.PreferredTarget is { } preferred &&
                !IsWithinPreferredTarget(candidate.Target, preferred, request.PreferredRadius!.Value) ||
                IsOccupiedByActor(mapCoordinates, _policy.ActorOccupancyExclusionRadius))
            {
                continue;
            }

            selected = candidate;
            return true;
        }

        return false;
    }

    private bool IsNearPlayerOrAnchor(MapCoordinates target, float radius)
    {
        var radiusSquared = radius * radius;
        foreach (var session in _players.Sessions)
        {
            if (session.AttachedEntity is not { } attached || !Exists(attached))
                continue;
            var player = _transform.GetMapCoordinates(attached);
            if (player.MapId == target.MapId &&
                Vector2.DistanceSquared(player.Position, target.Position) <= radiusSquared)
            {
                return true;
            }
        }

        _anchorIndex.GetAnchors(_anchorBuffer);
        foreach (var anchor in _anchorBuffer)
        {
            if (!Exists(anchor) ||
                !TryComp<NCCityRelevanceAnchorComponent>(anchor, out var component) || !component.Enabled)
            {
                continue;
            }

            var coordinates = _transform.GetMapCoordinates(anchor);
            if (coordinates.MapId == target.MapId &&
                Vector2.DistanceSquared(coordinates.Position, target.Position) <= radiusSquared)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWithinPreferredTarget(
        NCMapCoordinates candidate,
        NCMapCoordinates preferred,
        float radius)
    {
        return candidate.NetworkId == preferred.NetworkId && candidate.Z == preferred.Z &&
               Vector2.DistanceSquared(candidate.Position, preferred.Position) <= radius * radius;
    }

    private bool IsOccupiedByActor(MapCoordinates target, float radius)
    {
        foreach (var entity in _lookup.GetEntitiesInRange(target, radius))
        {
            if (HasComp<NCCityActorBindingComponent>(entity))
                return true;
        }

        return false;
    }

    private void Retry(PendingRequest pending, NCActiveWorldTime now, string error)
    {
        if (_policy == null || pending.ExpiresAt <= now)
        {
            CompleteTerminal(pending, NCCityMaterializationRequestStatus.Expired, error);
            return;
        }

        pending = pending with { DueAt = now.Advance(_policy.RetryDelay), Sequence = ++_sequence };
        _pending[pending.Request.RequestId] = pending;
        SetState(pending, NCCityMaterializationRequestStatus.Retrying, error);
        Enqueue(pending);
    }

    private void Enqueue(PendingRequest pending)
    {
        _queue.Enqueue(
            new QueuedRequest(pending.Request.RequestId, pending.Sequence),
            new RequestPriority(
                pending.DueAt.Ticks,
                -pending.Request.Priority,
                pending.RequestedAt.Ticks,
                pending.Sequence));
    }

    private void SetState(
        PendingRequest pending,
        NCCityMaterializationRequestStatus status,
        string? error,
        EntityUid entityUid = default)
    {
        _states[pending.Request.RequestId] = new NCCityMaterializationRequestState(
            pending.Request.RequestId,
            pending.Request.ActorId,
            pending.Request.Reason,
            status,
            pending.Request.Priority,
            pending.Attempts,
            pending.RequestedAt,
            pending.ExpiresAt,
            entityUid,
            error);
    }

    private void CompleteTerminal(
        PendingRequest pending,
        NCCityMaterializationRequestStatus status,
        string? error,
        EntityUid entityUid = default)
    {
        _loadGovernor.ReleaseMaterialization(pending.Request.RequestId);
        _pending.Remove(pending.Request.RequestId);
        _requestByActor.Remove(pending.Request.ActorId);
        SetState(pending, status, error, entityUid);
        _completedOrder.Enqueue(pending.Request.RequestId);
        TrimCompletedHistory();
    }

    private void RetainTerminal(NCCityMaterializationRequestState state)
    {
        _states[state.RequestId] = state;
        _completedOrder.Enqueue(state.RequestId);
        TrimCompletedHistory();
    }

    private void TrimCompletedHistory()
    {
        if (_policy == null)
            return;
        while (_completedOrder.Count > _policy.MaximumCompletedRequestHistory)
        {
            var requestId = _completedOrder.Dequeue();
            if (!_pending.ContainsKey(requestId))
                _states.Remove(requestId);
        }
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        Reset();
        if (!_prototypes.TryIndex(
                new ProtoId<NCCityMaterializationPolicyPrototype>(DefaultPolicyId),
                out NCCityMaterializationPolicyPrototype? policy) || policy == null || !ValidatePolicy(policy))
        {
            Log.Error($"Actor materialization cannot start: policy {DefaultPolicyId} is missing or invalid.");
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

    private void OnClockFailed(NCActiveWorldClockFailedEvent args)
    {
        if (_active && args.Epoch == _epoch)
            Reset();
    }

    private void OnSpawnIndexChanged(ref NCCitySpawnCandidateIndexChangedEvent args)
    {
        if (!_active || !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
            return;

        // Structural graph changes are infrequent. Wake only the bounded pending request set, never all actors.
        foreach (var requestId in _pending.Keys.ToArray())
        {
            var pending = _pending[requestId];
            if (_states[requestId].Status != NCCityMaterializationRequestStatus.Retrying)
                continue;
            pending = pending with { DueAt = now, Sequence = ++_sequence };
            _pending[requestId] = pending;
            Enqueue(pending);
        }
    }

    private static bool TryValidateRequest(
        NCCityMaterializationRequest request,
        NCCityMaterializationPolicyPrototype policy,
        out string? error)
    {
        error = null;
        var wait = request.MaximumWait ?? policy.DefaultMaximumWait;
        var hasPreferredTarget = request.PreferredTarget is { NetworkId.IsValid: true };
        var hasPreferredRadius = request.PreferredRadius is { } preferredRadius &&
                                 float.IsFinite(preferredRadius) && preferredRadius > 0f &&
                                 preferredRadius <= MaximumPreferredRadius;
        if (request.RequestId == Guid.Empty || !request.ActorId.IsValid || !Enum.IsDefined(request.Reason) ||
            request.Priority < 0 || request.Priority > policy.MaximumPriority ||
            string.IsNullOrWhiteSpace(request.Source) || request.Source.Length > MaximumSourceLength ||
            wait <= TimeSpan.Zero || wait > policy.MaximumWait ||
            hasPreferredTarget != hasPreferredRadius)
        {
            error = "Materialization request identity, priority, lifetime or source is invalid.";
            return false;
        }

        return true;
    }

    private static bool ValidatePolicy(NCCityMaterializationPolicyPrototype policy)
    {
        return policy.MaximumQueuedRequests > 0 && policy.MaximumDispatchesPerTick > 0 &&
               policy.MaximumConcurrentLoads > 0 &&
               policy.MaximumDispatchesPerTick <= policy.MaximumConcurrentLoads &&
               policy.MaximumCandidateChecks > 0 && policy.MaximumCompletedRequestHistory > 0 &&
               policy.MaximumPriority > 0 &&
               float.IsFinite(policy.PlayerVisibilityExclusionRadius) && policy.PlayerVisibilityExclusionRadius > 0f &&
               float.IsFinite(policy.PlayerRelevanceMaximumRadius) &&
               policy.PlayerRelevanceMaximumRadius > policy.PlayerVisibilityExclusionRadius &&
               float.IsFinite(policy.ActorOccupancyExclusionRadius) && policy.ActorOccupancyExclusionRadius > 0f &&
               policy.RetryDelay > TimeSpan.Zero && policy.DefaultMaximumWait > TimeSpan.Zero &&
               policy.DefaultMaximumWait <= policy.MaximumWait;
    }

    private void Reset()
    {
        foreach (var requestId in _pending.Keys)
            _loadGovernor.ReleaseMaterialization(requestId);
        _generation++;
        _active = false;
        _policy = null;
        _epoch = default;
        _inFlight = 0;
        _pending.Clear();
        _requestByActor.Clear();
        _states.Clear();
        _completedOrder.Clear();
        _queue.Clear();
        _candidateBuffer.Clear();
        _anchorBuffer.Clear();
    }

    private sealed record PendingRequest(
        NCCityMaterializationRequest Request,
        NCActiveWorldTime RequestedAt,
        NCActiveWorldTime ExpiresAt,
        int Attempts,
        long Sequence,
        NCActiveWorldTime DueAt);

    private readonly record struct QueuedRequest(Guid RequestId, long Sequence);
    private readonly record struct RequestPriority(
        long DueTicks,
        int NegativePriority,
        long RequestedTicks,
        long Sequence) : IComparable<RequestPriority>
    {
        public int CompareTo(RequestPriority other)
        {
            var due = DueTicks.CompareTo(other.DueTicks);
            if (due != 0)
                return due;
            var priority = NegativePriority.CompareTo(other.NegativePriority);
            if (priority != 0)
                return priority;
            var requested = RequestedTicks.CompareTo(other.RequestedTicks);
            return requested != 0 ? requested : Sequence.CompareTo(other.Sequence);
        }
    }
}
