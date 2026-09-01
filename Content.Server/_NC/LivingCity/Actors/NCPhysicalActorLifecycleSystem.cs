// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Server._NC.City.Connectivity;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Actors.Domains;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Neural;
using Content.Server._NC.LivingCity.Simulation;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Robust.Shared.Asynchronous;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Actors;

/// <summary>
/// Creates one physical entity for every living persistent NPC and keeps that identity binding for the round.
/// The queue bounds startup work only; it never caps physical population or removes off-screen actors.
/// </summary>
public sealed partial class NCPhysicalActorLifecycleSystem : EntitySystem
{
    private static readonly ProtoId<NCPhysicalActorLifecyclePolicyPrototype> DefaultPolicy =
        new("NCDefaultPhysicalActorLifecycle");

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ITaskManager _taskManager = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private TurfSystem _turf = default!;
    [Dependency] private MetaDataSystem _metadata = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCCityActorDomainRegistrySystem _domains = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCCitySpawnCandidateSystem _spawnCandidates = default!;
    [Dependency] private NCEconomicPhysicalizationSystem _physicalization = default!;
    [Dependency] private NCNeuralConversationSystem _conversations = default!;
    [Dependency] private NCAILoadGovernorSystem _loadGovernor = default!;

    private readonly Dictionary<Guid, NCPhysicalActorSpawnState> _states = [];
    private readonly Dictionary<NCActorId, PendingSpawn> _pending = [];
    private readonly Queue<Guid> _completedOrder = [];
    private readonly PriorityQueue<ScheduledSpawn, (long DueTicks, long Sequence)> _urgentQueue = new();
    private readonly PriorityQueue<ScheduledSpawn, (long DueTicks, long Sequence)> _queue = new();
    private readonly List<NCCitySpawnCandidateInfo> _candidates = [];

    private NCPhysicalActorLifecyclePolicyPrototype? _policy;
    private NCSessionEpoch _epoch;
    private long _sequence;
    private int _generation;
    private int _loadsInFlight;
    private bool _active;

    public bool IsActive => _active;
    public int QueuedCount => _pending.Count;
    public int LoadsInFlight => _loadsInFlight;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(OnSessionClosed);
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(OnClockFailed);
        SubscribeLocalEvent<NCCitySpawnCandidateIndexChangedEvent>(OnSpawnCandidateIndexChanged);
        SubscribeLocalEvent<NCCityActorBindingChangedEvent>(OnBindingChanged);
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
        _loadGovernor.ReportMaterializedActors(_runtime.PhysicalActorCount);
        _loadGovernor.ReportQueue(NCAILoadQueueKind.Materialization, _pending.Count,
            Math.Max(1, _runtime.PhysicalActorCount + _pending.Count));
        var limit = _loadGovernor.TakeDispatchLimit(
            NCAILoadDispatchKind.ActorMaterialization,
            _policy.MaximumDispatchesPerTick);
        // A permanent zero would violate the physical-population invariant. The governor may reduce cold-start work,
        // but one bounded load is always allowed to make progress.
        limit = Math.Max(1, limit);
        var dispatched = 0;
        while (dispatched < limit && _loadsInFlight < _policy.MaximumConcurrentLoads &&
               TryDequeue(now, out var pending))
        {
            if (_runtime.TryGetEntity(pending.Request.ActorId, out var existing) && Exists(existing))
            {
                if (pending.Request.Reason == NCPhysicalActorSpawnReason.Admin &&
                    pending.Request.PreferredTarget is { } preferred)
                {
                    TryRelocate(existing, preferred, pending.Request.PreferredRadius ??
                        _policy.AdminPreferredSpawnRadius, out _);
                }

                Complete(pending, NCPhysicalActorSpawnStatus.AlreadyPhysical, existing, null);
                continue;
            }

            pending = pending with { Attempts = pending.Attempts + 1 };
            _pending[pending.Request.ActorId] = pending;
            SetState(pending, NCPhysicalActorSpawnStatus.Loading, null);
            _loadsInFlight++;
            dispatched++;
            _ = ObserveLoadAsync(_generation, pending);
        }
    }

    /// <summary>
    /// Ensures a persistent NPC owns a physical entity. Duplicate calls join the actor's existing request.
    /// </summary>
    public NCPhysicalActorSpawnState Request(NCPhysicalActorSpawnRequest request)
    {
        if (_states.TryGetValue(request.RequestId, out var replay))
            return replay;
        var now = default(NCActiveWorldTime);
        if (!_active || _policy == null || !_clock.TryGetCurrentTime(out var epoch, out now) || epoch != _epoch ||
            request.RequestId == Guid.Empty || !request.ActorId.IsValid || string.IsNullOrWhiteSpace(request.Source) ||
            request.Source.Length > 128 ||
            request.PreferredRadius is { } radius && (!float.IsFinite(radius) || radius <= 0f) ||
            request.PreferredTarget.HasValue != request.PreferredRadius.HasValue)
        {
            return Retain(new NCPhysicalActorSpawnState(
                request.RequestId,
                request.ActorId,
                request.Reason,
                NCPhysicalActorSpawnStatus.Rejected,
                0,
                now,
                null,
                "Physical actor spawning requires a valid active-round request."));
        }

        if (_runtime.TryGetEntity(request.ActorId, out var existing) && Exists(existing))
        {
            string? relocationError = null;
            if (request.Reason == NCPhysicalActorSpawnReason.Admin && request.PreferredTarget is { } preferred)
                TryRelocate(existing, preferred, request.PreferredRadius!.Value, out relocationError);
            return Retain(new NCPhysicalActorSpawnState(
                request.RequestId,
                request.ActorId,
                request.Reason,
                NCPhysicalActorSpawnStatus.AlreadyPhysical,
                0,
                now,
                existing,
                relocationError));
        }

        if (_pending.TryGetValue(request.ActorId, out var joined))
        {
            if (request.Reason != NCPhysicalActorSpawnReason.Admin || request.PreferredTarget == null)
                return _states[joined.Request.RequestId];

            Retain(new NCPhysicalActorSpawnState(
                joined.Request.RequestId,
                joined.Request.ActorId,
                joined.Request.Reason,
                NCPhysicalActorSpawnStatus.Rejected,
                joined.Attempts,
                joined.RequestedAt,
                null,
                "Superseded by an explicit administrator relocation request."));
            joined = joined with { Request = request, DueAt = now, Sequence = ++_sequence };
            _pending[request.ActorId] = joined;
            SetState(joined, NCPhysicalActorSpawnStatus.Queued, null);
            Enqueue(joined);
            return _states[request.RequestId];
        }

        var pending = new PendingSpawn(request, now, now, 0, ++_sequence);
        _pending.Add(request.ActorId, pending);
        SetState(pending, NCPhysicalActorSpawnStatus.Queued, null);
        Enqueue(pending);
        return _states[request.RequestId];
    }

    /// <summary>Queues or relocates an actor near an administrator without exposing lifecycle policy values.</summary>
    public NCPhysicalActorSpawnState RequestAdmin(
        Guid requestId,
        NCActorId actorId,
        NCMapCoordinates preferredTarget,
        string source)
    {
        var radius = _policy?.AdminPreferredSpawnRadius;
        return Request(new NCPhysicalActorSpawnRequest(
            requestId,
            actorId,
            NCPhysicalActorSpawnReason.Admin,
            source,
            radius.HasValue ? preferredTarget : null,
            radius));
    }

    public bool TryGetRequest(Guid requestId, out NCPhysicalActorSpawnState state) =>
        _states.TryGetValue(requestId, out state!);

    public bool TryRelocate(EntityUid actor, NCMapCoordinates preferred, float radius, out string? error)
    {
        error = null;
        if (!Exists(actor) || !TryFindSafeAdminTarget(preferred, radius, actor, out var target))
        {
            error = "No free supported floor tile is available on the selected Z-level.";
            return false;
        }

        _transform.SetMapCoordinates(actor, target);
        return true;
    }

    private async Task ScanActorsAsync(int generation)
    {
        NCActorId? continuation = null;
        try
        {
            while (generation == _generation && _active && _policy != null)
            {
                var page = await _actors.GetPageResultAsync(continuation, _policy.SnapshotPageSize, true);
                var records = page.Records.ToArray();
                _taskManager.RunOnMainThread(() => QueuePage(generation, records));
                if (!page.HasMore || page.ContinuationActorId is not { IsValid: true } next || next == continuation)
                    break;
                continuation = next;
            }
        }
        catch (Exception exception)
        {
            Log.Error($"Physical actor catalog scan failed: {exception}");
        }
    }

    private void QueuePage(int generation, IReadOnlyList<NCCityActorRecord> records)
    {
        if (generation != _generation || !_active)
            return;
        foreach (var record in records)
        {
            if (record.Snapshot.Lifecycle.Status is NCCityActorStatus.Dead or NCCityActorStatus.Archived)
                continue;
            Request(new NCPhysicalActorSpawnRequest(
                Guid.NewGuid(),
                record.Snapshot.Identity.ActorId,
                NCPhysicalActorSpawnReason.RoundBootstrap,
                "physical-actor-round-bootstrap"));
        }
    }

    private async Task ObserveLoadAsync(int generation, PendingSpawn pending)
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

        _taskManager.RunOnMainThread(() => CompleteLoad(
            generation,
            pending.Request.ActorId,
            actor,
            health,
            inventory,
            inventoryPlan,
            error));
    }

    private void CompleteLoad(
        int generation,
        NCActorId actorId,
        NCCityActorRecord? actor,
        NCCityActorHealthRecord? health,
        NCCityActorInventoryRecord? inventory,
        NCActorInventoryMaterializationPlan? inventoryPlan,
        Exception? loadError)
    {
        if (generation != _generation || !_active || _policy == null || !_pending.TryGetValue(actorId, out var pending) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
        {
            return;
        }
        _loadsInFlight = Math.Max(0, _loadsInFlight - 1);

        if (loadError != null)
        {
            Retry(pending, now, $"Persistent actor load failed: {loadError.Message}");
            return;
        }
        if (actor == null || health == null || inventory == null || inventoryPlan == null)
        {
            Complete(pending, NCPhysicalActorSpawnStatus.Rejected, null,
                "The actor root or its mandatory physical domains are missing.");
            return;
        }
        if (actor.Snapshot.Lifecycle.Status is NCCityActorStatus.Dead or NCCityActorStatus.Archived)
        {
            Complete(pending, NCPhysicalActorSpawnStatus.Rejected, null, "Dead or archived actors are not spawned.");
            return;
        }
        if (_runtime.TryGetEntity(actorId, out var existing) && Exists(existing))
        {
            Complete(pending, NCPhysicalActorSpawnStatus.AlreadyPhysical, existing, null);
            return;
        }
        if (!_prototypes.TryIndex(
                new ProtoId<NCCityActorArchetypePrototype>(actor.Snapshot.Identity.ArchetypeId),
                out NCCityActorArchetypePrototype? archetype) || archetype == null)
        {
            Complete(pending, NCPhysicalActorSpawnStatus.Rejected, null,
                $"Actor archetype {actor.Snapshot.Identity.ArchetypeId} is missing.");
            return;
        }
        if (!TrySelectSpawnCoordinates(actor.Snapshot, archetype, pending, out var coordinates))
        {
            Retry(pending, now, "No authored safe floor is currently available for this physical actor.");
            return;
        }

        EntityUid entity;
        try
        {
            entity = Spawn(archetype.EntityPrototype, coordinates);
        }
        catch (Exception exception)
        {
            Retry(pending, now, $"Entity prototype {archetype.EntityPrototype} failed to spawn: {exception.Message}");
            return;
        }

        _metadata.SetEntityName(entity, actor.Snapshot.Identity.FullName);
        var conversation = EnsureComp<NCNeuralConversationTargetComponent>(entity);
        conversation.NarrativeActor = !string.IsNullOrWhiteSpace(actor.Snapshot.Identity.NarrativeActorProfileId) ||
                                      archetype.NarrativeActorProfile != null;
        conversation.InitiateAmbientConversations = false;
        conversation.MaximumNpcConversationTurns = conversation.NarrativeActor ? 8 : 4;
        if (!_runtime.TryBind(actorId, entity))
        {
            QueueDel(entity);
            Retry(pending, now, "The persistent actor identity was concurrently bound.");
            return;
        }
        _conversations.RefreshTarget(entity);

        var inventoryAuthority = EnsureComp<NCCityActorInventoryAdapterComponent>(entity);
        inventoryAuthority.AuthorityVersion = inventory.Snapshot.SourceAuthorityVersion;
        inventoryAuthority.EquippedLoadoutId = inventory.Snapshot.Summary.EquippedLoadoutId;
        if (!string.IsNullOrWhiteSpace(health.Snapshot.MedicalStatePayload) &&
            !_domains.TryRestoreHealth(actorId, entity, health))
        {
            _runtime.Unbind(entity);
            QueueDel(entity);
            Retry(pending, now, "The actor's medical state could not be restored.");
            return;
        }
        if (!_physicalization.TryMaterialize(entity, inventoryPlan, out var inventoryError))
        {
            _runtime.Unbind(entity);
            QueueDel(entity);
            Retry(pending, now, inventoryError ?? "The actor's inventory state could not be restored.");
            return;
        }

        Complete(pending, NCPhysicalActorSpawnStatus.Spawned, entity, null);
        RaiseLocalEvent(new NCPhysicalActorSpawnedEvent(
            pending.Request.RequestId,
            actorId,
            entity,
            pending.Request.Reason));
    }

    private bool TrySelectSpawnCoordinates(
        NCCityActorSnapshot actor,
        NCCityActorArchetypePrototype archetype,
        PendingSpawn pending,
        out MapCoordinates coordinates)
    {
        coordinates = default;
        if (pending.Request.PreferredTarget is { } preferred)
        {
            return TryFindSafeAdminTarget(
                preferred,
                pending.Request.PreferredRadius!.Value,
                null,
                out coordinates);
        }

        _spawnCandidates.GetCandidatesInHierarchy(
            archetype.SpawnCandidateProfile,
            actor.Location.CurrentZone,
            _candidates);
        if (_candidates.Count == 0)
            _spawnCandidates.GetCandidates(archetype.SpawnCandidateProfile, default, _candidates);
        if (_candidates.Count == 0 || _policy == null)
            return false;

        var raw = actor.Identity.ActorId.Value.ToByteArray();
        var start = (int) ((BitConverter.ToUInt32(raw, 0) + (uint) pending.Attempts) % (uint) _candidates.Count);
        var checks = Math.Min(_candidates.Count, _policy.MaximumCandidateChecks);
        MapCoordinates? occupiedFallback = null;
        for (var index = 0; index < checks; index++)
        {
            var candidate = _candidates[(start + index) % _candidates.Count];
            if (!_coordinates.TryResolve(candidate.Target, out var resolved) || !HasSafeFloor(resolved))
                continue;
            occupiedFallback ??= resolved;
            if (IsOccupiedByActor(resolved, _policy.ActorOccupancyExclusionRadius, null))
                continue;
            coordinates = resolved;
            return true;
        }

        // Actor count is never capped. If all authored samples are occupied, Robust physics separates the cold-start
        // overlap while normal steering takes over; leaving the actor virtual would violate the v3 invariant.
        if (occupiedFallback is not { } fallback)
            return false;
        coordinates = fallback;
        return true;
    }

    private bool TryFindSafeAdminTarget(
        NCMapCoordinates preferred,
        float radius,
        EntityUid? ignoredActor,
        out MapCoordinates target)
    {
        target = default;
        if (_policy == null || !float.IsFinite(radius) || radius <= 0f ||
            !_coordinates.TryResolve(preferred, out var preferredMap) ||
            !_maps.TryGetMap(preferredMap.MapId, out var mapUid) || mapUid is not { } map ||
            !_maps.TryFindGridAt(map, preferredMap.Position, out var gridUid, out var grid))
        {
            return false;
        }

        var origin = _maps.WorldToTile(gridUid, grid, preferredMap.Position);
        var maximumRing = Math.Max(1, (int) MathF.Ceiling(radius));
        var radiusSquared = radius * radius;
        for (var ring = 1; ring <= maximumRing; ring++)
        {
            for (var x = -ring; x <= ring; x++)
            {
                for (var y = -ring; y <= ring; y++)
                {
                    if (Math.Max(Math.Abs(x), Math.Abs(y)) != ring)
                        continue;
                    var tile = origin + new Vector2i(x, y);
                    if (!_maps.TryGetTileRef(gridUid, grid, tile, out var tileRef) || tileRef.Tile.IsEmpty ||
                        _turf.IsTileBlocked(tileRef, CollisionGroup.Impassable))
                    {
                        continue;
                    }

                    var candidate = _maps.GridTileToWorld(gridUid, grid, tile);
                    if (Vector2.DistanceSquared(candidate.Position, preferredMap.Position) > radiusSquared ||
                        IsOccupiedByActor(candidate, _policy.ActorOccupancyExclusionRadius, ignoredActor) ||
                        !_coordinates.TryConvert(candidate, out var stable) ||
                        stable.NetworkId != preferred.NetworkId || stable.Z != preferred.Z)
                    {
                        continue;
                    }
                    target = candidate;
                    return true;
                }
            }
        }
        return false;
    }

    private bool HasSafeFloor(MapCoordinates target)
    {
        if (!_maps.TryGetMap(target.MapId, out var mapUid) || mapUid is not { } map ||
            !_maps.TryFindGridAt(map, target.Position, out var gridUid, out var grid))
        {
            return false;
        }
        var tile = _maps.WorldToTile(gridUid, grid, target.Position);
        return _maps.TryGetTileRef(gridUid, grid, tile, out var tileRef) && !tileRef.Tile.IsEmpty &&
               !_turf.IsTileBlocked(tileRef, CollisionGroup.Impassable);
    }

    private bool IsOccupiedByActor(MapCoordinates target, float radius, EntityUid? ignoredActor)
    {
        foreach (var actor in _lookup.GetEntitiesInRange<NCCityActorBindingComponent>(target, radius))
        {
            if (actor.Owner == ignoredActor)
                continue;
            return true;
        }
        return false;
    }

    private bool TryDequeue(NCActiveWorldTime now, out PendingSpawn pending)
    {
        if (TryDequeue(_urgentQueue, now, out pending))
            return true;
        return TryDequeue(_queue, now, out pending);
    }

    private bool TryDequeue(
        PriorityQueue<ScheduledSpawn, (long DueTicks, long Sequence)> source,
        NCActiveWorldTime now,
        out PendingSpawn pending)
    {
        while (source.TryPeek(out var scheduled, out var priority))
        {
            if (priority.DueTicks > now.Ticks)
                break;
            source.Dequeue();
            if (_pending.TryGetValue(scheduled.ActorId, out var candidate) &&
                candidate.Sequence == scheduled.Sequence)
            {
                pending = candidate;
                return true;
            }
        }
        pending = default!;
        return false;
    }

    private void Retry(PendingSpawn pending, NCActiveWorldTime now, string error)
    {
        if (_policy == null)
            return;
        pending = pending with { DueAt = now.Advance(_policy.RetryDelay), Sequence = ++_sequence };
        _pending[pending.Request.ActorId] = pending;
        SetState(pending, NCPhysicalActorSpawnStatus.Retrying, error);
        Enqueue(pending);
    }

    private void Enqueue(PendingSpawn pending)
    {
        var queue = pending.Request.Reason == NCPhysicalActorSpawnReason.RoundBootstrap ? _queue : _urgentQueue;
        queue.Enqueue(new ScheduledSpawn(pending.Request.ActorId, pending.Sequence),
            (pending.DueAt.Ticks, pending.Sequence));
    }

    private void SetState(PendingSpawn pending, NCPhysicalActorSpawnStatus status, string? error)
    {
        _states[pending.Request.RequestId] = new NCPhysicalActorSpawnState(
            pending.Request.RequestId,
            pending.Request.ActorId,
            pending.Request.Reason,
            status,
            pending.Attempts,
            pending.RequestedAt,
            null,
            error);
    }

    private void Complete(
        PendingSpawn pending,
        NCPhysicalActorSpawnStatus status,
        EntityUid? entity,
        string? error)
    {
        _pending.Remove(pending.Request.ActorId);
        Retain(new NCPhysicalActorSpawnState(
            pending.Request.RequestId,
            pending.Request.ActorId,
            pending.Request.Reason,
            status,
            pending.Attempts,
            pending.RequestedAt,
            entity,
            error));
    }

    private NCPhysicalActorSpawnState Retain(NCPhysicalActorSpawnState state)
    {
        if (state.RequestId == Guid.Empty)
            return state;
        _states[state.RequestId] = state;
        _completedOrder.Enqueue(state.RequestId);
        while (_policy != null && _completedOrder.Count > _policy.MaximumCompletedRequestHistory)
            _states.Remove(_completedOrder.Dequeue());
        return state;
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        Reset();
        if (!_prototypes.TryIndex(DefaultPolicy, out NCPhysicalActorLifecyclePolicyPrototype? policy) ||
            policy == null || !ValidatePolicy(policy))
        {
            Log.Error($"Physical actor lifecycle policy {DefaultPolicy} is missing or invalid.");
            return;
        }
        _policy = policy;
        _epoch = args.Epoch;
        _active = true;
        _ = ScanActorsAsync(_generation);
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

    private void OnSpawnCandidateIndexChanged(ref NCCitySpawnCandidateIndexChangedEvent args)
    {
        if (!_active || !_clock.TryGetCurrentTime(out _, out var now))
            return;
        foreach (var (actorId, pending) in _pending.ToArray())
        {
            if (_states[pending.Request.RequestId].Status != NCPhysicalActorSpawnStatus.Retrying)
                continue;
            var rescheduled = pending with { DueAt = now, Sequence = ++_sequence };
            _pending[actorId] = rescheduled;
            Enqueue(rescheduled);
        }
    }

    private void OnBindingChanged(NCCityActorBindingChangedEvent args)
    {
        if (args.Bound || !_active)
            return;
        _ = ObserveBindingLossAsync(_generation, args.ActorId);
    }

    private async Task ObserveBindingLossAsync(int generation, NCActorId actorId)
    {
        NCCityActorRecord? actor = null;
        try
        {
            actor = await _actors.GetAsync(actorId, true);
        }
        catch (Exception exception)
        {
            Log.Warning($"Physical actor recovery lookup failed for {actorId}: {exception.Message}");
        }

        _taskManager.RunOnMainThread(() =>
        {
            if (generation != _generation || !_active || actor == null ||
                actor.Snapshot.Lifecycle.Status is NCCityActorStatus.Dead or NCCityActorStatus.Archived ||
                _runtime.TryGetEntity(actorId, out _))
            {
                return;
            }

            Request(new NCPhysicalActorSpawnRequest(
                Guid.NewGuid(),
                actorId,
                NCPhysicalActorSpawnReason.Recovery,
                "physical-actor-binding-recovery"));
        });
    }

    private void Reset()
    {
        _active = false;
        _policy = null;
        _epoch = default;
        _generation++;
        _loadsInFlight = 0;
        _sequence = 0;
        _states.Clear();
        _pending.Clear();
        _completedOrder.Clear();
        _urgentQueue.Clear();
        _queue.Clear();
        _candidates.Clear();
    }

    private static bool ValidatePolicy(NCPhysicalActorLifecyclePolicyPrototype policy) =>
        policy.SnapshotPageSize is > 0 and <= 1000 &&
        policy.MaximumDispatchesPerTick is > 0 and <= 128 &&
        policy.MaximumConcurrentLoads is > 0 and <= 128 &&
        policy.MaximumCandidateChecks is > 0 and <= 4096 &&
        policy.MaximumCompletedRequestHistory is > 0 and <= 100000 &&
        float.IsFinite(policy.ActorOccupancyExclusionRadius) && policy.ActorOccupancyExclusionRadius >= 0f &&
        float.IsFinite(policy.AdminPreferredSpawnRadius) && policy.AdminPreferredSpawnRadius > 0f &&
        policy.RetryDelay > TimeSpan.Zero;

    private sealed record PendingSpawn(
        NCPhysicalActorSpawnRequest Request,
        NCActiveWorldTime RequestedAt,
        NCActiveWorldTime DueAt,
        int Attempts,
        long Sequence);

    private readonly record struct ScheduledSpawn(NCActorId ActorId, long Sequence);
}
