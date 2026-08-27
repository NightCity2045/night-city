// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Actors.Domains;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Groups.Components;
using Content.Server._NC.LivingCity.Simulation;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.CombatMode;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Robust.Server.Player;
using Robust.Shared.Asynchronous;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.LivingCity.Actors;

/// <summary>
/// Bounded physical-to-strategic request queue. It captures ECS state on the simulation thread, commits all
/// mandatory aggregates atomically, then revalidates relevance and physical revisions before removing the entity.
/// </summary>
public sealed partial class NCCityActorDematerializationSystem : EntitySystem
{
    private const string DefaultPolicyId = "NCDefaultCityRelevancePolicy";
    private const int MaximumSourceLength = 128;

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ITaskManager _taskManager = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCCityActorDomainRegistrySystem _domains = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCCityRelevanceAnchorIndexSystem _anchorIndex = default!;
    [Dependency] private NCAILoadGovernorSystem _loadGovernor = default!;
    [Dependency] private NCEconomicPhysicalizationSystem _physicalization = default!;
    [Dependency] private SharedCombatModeSystem _combatMode = default!;
    [Dependency] private HTNSystem _htn = default!;
    [Dependency] private NPCSteeringSystem _steering = default!;
    [Dependency] private PullingSystem _pulling = default!;

    private readonly Dictionary<Guid, NCCityDematerializationRequestState> _states = [];
    private readonly Dictionary<Guid, PendingRequest> _pending = [];
    private readonly Dictionary<NCActorId, Guid> _requestByActor = [];
    private readonly Dictionary<NCActorId, NCActiveWorldTime> _cooldowns = [];
    private readonly Dictionary<BlockerLeaseKey, int> _blockerLeases = [];
    private readonly List<EntityUid> _anchorBuffer = [];
    private readonly Queue<Guid> _completedOrder = [];
    private readonly PriorityQueue<QueuedRequest, RequestPriority> _queue = new();
    private readonly Queue<PreparedDespawn> _despawnQueue = [];

    private NCCityRelevancePolicyPrototype? _policy;
    private NCSessionEpoch _epoch;
    private int _generation;
    private int _inFlight;
    private long _sequence;
    private long _queuedEntityDeletions;
    private EntityUid? _lastQueuedEntityDeletion;
    private GameTick _lastQueuedEntityDeletionTick;
    private bool _active;

    public bool IsActive => _active;
    public int PendingRequestCount => _pending.Count;
    public int InFlightCount => _inFlight;
    public int RetainedResultCount => _states.Count - _pending.Count;
    public int PreparedDespawnCount => _despawnQueue.Count;
    public long QueuedEntityDeletionCount => _queuedEntityDeletions;
    public EntityUid? LastQueuedEntityDeletion => _lastQueuedEntityDeletion;
    public GameTick LastQueuedEntityDeletionTick => _lastQueuedEntityDeletionTick;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(OnSessionClosed);
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(OnClockFailed);
        SubscribeLocalEvent<NCCityActorRehydratedEvent>(OnActorRehydrated);
        SubscribeLocalEvent<NCCityActorMaterializationChangedEvent>(OnMaterializationChanged);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        FlushPreparedDespawns();
        if (!_active || _policy == null ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
        {
            return;
        }

        using var work = _loadGovernor.MeasureWork(NCAILoadWorkKind.ActorDematerialization);
        _loadGovernor.ReportQueue(
            NCAILoadQueueKind.Dematerialization,
            _pending.Count,
            _policy.MaximumQueuedDematerializations);
        var dispatched = 0;
        while (dispatched < _policy.MaximumDematerializationDispatchesPerTick &&
               _inFlight < _policy.MaximumConcurrentDematerializations &&
               _queue.TryPeek(out var queued, out var priority) && priority.DueTicks <= now.Ticks)
        {
            _queue.Dequeue();
            if (!_pending.TryGetValue(queued.RequestId, out var pending) || pending.Sequence != queued.Sequence)
                continue;
            if (pending.ExpiresAt < now)
            {
                CompleteTerminal(pending, NCCityDematerializationStatus.Expired,
                    "The dematerialization request expired before a safe hand-off completed.");
                continue;
            }

            if (!TryResolveBinding(pending, out _))
            {
                CompleteTerminal(pending, NCCityDematerializationStatus.AlreadyDematerialized, null);
                continue;
            }

            pending = pending with { Attempts = pending.Attempts + 1 };
            _pending[pending.Request.RequestId] = pending;
            SetState(pending, NCCityDematerializationStatus.Loading, null);
            _inFlight++;
            dispatched++;
            _ = ObserveLoad(_generation, pending);
        }
    }

    public NCCityDematerializationRequestState Request(NCCityDematerializationRequest request)
    {
        if (_states.TryGetValue(request.RequestId, out var replay))
            return replay;

        var now = default(NCActiveWorldTime);
        if (!_active || _policy == null ||
            !_clock.TryGetCurrentTime(out var epoch, out now) || epoch != _epoch)
        {
            return RejectedState(request, now, "Dematerialization requires an active A-Life round.");
        }

        if (!TryValidateRequest(request, out var error))
        {
            var rejected = RejectedState(request, now, error!);
            if (request.RequestId != Guid.Empty)
                RetainTerminal(rejected);
            return rejected;
        }

        if (!_runtime.TryGetEntity(request.ActorId, out var bound) || bound != request.EntityUid || !Exists(bound))
        {
            var completed = new NCCityDematerializationRequestState(
                request.RequestId,
                request.ActorId,
                request.EntityUid,
                request.Reason,
                NCCityDematerializationStatus.AlreadyDematerialized,
                0,
                now,
                now,
                null);
            RetainTerminal(completed);
            return completed;
        }

        if (_requestByActor.TryGetValue(request.ActorId, out var activeRequest) &&
            _states.TryGetValue(activeRequest, out var activeState))
        {
            var rejected = RejectedState(request, now,
                $"Actor already has active dematerialization request {activeState.RequestId}.");
            RetainTerminal(rejected);
            return rejected;
        }

        if (_pending.Count >= _policy.MaximumQueuedDematerializations)
        {
            var rejected = RejectedState(request, now, "The bounded dematerialization queue is full.");
            RetainTerminal(rejected);
            return rejected;
        }

        var pending = new PendingRequest(
            request,
            now,
            now.Advance(_policy.DematerializationMaximumWait),
            0,
            ++_sequence,
            now,
            Guid.NewGuid(),
            null,
            null);
        _pending.Add(request.RequestId, pending);
        _requestByActor.Add(request.ActorId, request.RequestId);
        SetState(pending, NCCityDematerializationStatus.Requested, null);
        Enqueue(pending);
        return _states[request.RequestId];
    }

    public bool TryGetRequest(Guid requestId, out NCCityDematerializationRequestState state)
    {
        return _states.TryGetValue(requestId, out state!);
    }

    /// <summary>
    /// Gameplay systems acquire explicit blockers instead of the relevance loop guessing combat or interaction state.
    /// </summary>
    public bool SetBlocker(EntityUid entityUid, NCCityDematerializationBlockerFlags blocker, bool enabled)
    {
        if (blocker == NCCityDematerializationBlockerFlags.None || !Exists(entityUid))
            return false;

        var component = EnsureComp<NCCityDematerializationBlockerComponent>(entityUid);
        component.Flags = enabled ? component.Flags | blocker : component.Flags & ~blocker;
        if (component.Flags == NCCityDematerializationBlockerFlags.None)
            RemCompDeferred<NCCityDematerializationBlockerComponent>(entityUid);
        return true;
    }

    public bool BeginBlocker(EntityUid entityUid, NCCityDematerializationBlockerFlags blocker)
    {
        if (blocker == NCCityDematerializationBlockerFlags.None || !Exists(entityUid))
            return false;
        var key = new BlockerLeaseKey(entityUid, blocker);
        _blockerLeases[key] = checked(_blockerLeases.GetValueOrDefault(key) + 1);
        return SetBlocker(entityUid, blocker, true);
    }

    public bool EndBlocker(EntityUid entityUid, NCCityDematerializationBlockerFlags blocker)
    {
        var key = new BlockerLeaseKey(entityUid, blocker);
        if (!_blockerLeases.TryGetValue(key, out var count))
            return false;
        if (count > 1)
        {
            _blockerLeases[key] = count - 1;
            return true;
        }

        _blockerLeases.Remove(key);
        return SetBlocker(entityUid, blocker, false);
    }

    public bool IsEligible(NCActorId actorId, EntityUid entityUid, out string? error)
    {
        return IsEligible(actorId, entityUid, false, false, out error);
    }

    public bool IsEligibleForOffscreenHandOff(NCActorId actorId, EntityUid entityUid, out string? error)
    {
        return IsEligible(actorId, entityUid, false, true, out error);
    }

    public bool IsEligibleForGroupHandOff(NCActorId actorId, EntityUid entityUid, out string? error)
    {
        return IsEligible(actorId, entityUid, true, false, out error);
    }

    public bool IsEligibleForOffscreenGroupHandOff(NCActorId actorId, EntityUid entityUid, out string? error)
    {
        return IsEligible(actorId, entityUid, true, true, out error);
    }

    private bool IsEligible(
        NCActorId actorId,
        EntityUid entityUid,
        bool allowGroupHandOff,
        bool allowOffscreenCombat,
        out string? error)
    {
        error = null;
        if (!_active || _policy == null || !TryResolveBinding(actorId, entityUid))
        {
            error = "The actor has no current physical binding.";
            return false;
        }

        if (_clock.TryGetCurrentTime(out var epoch, out var now) && epoch == _epoch &&
            _cooldowns.TryGetValue(actorId, out var cooldownUntil) && cooldownUntil > now)
        {
            error = "The actor materialization cooldown has not elapsed.";
            return false;
        }

        if (TryComp<NCCityDematerializationBlockerComponent>(entityUid, out var blocker) &&
            blocker.Flags != NCCityDematerializationBlockerFlags.None)
        {
            error = $"The actor has active dematerialization blockers: {blocker.Flags}.";
            return false;
        }

        if (!allowGroupHandOff && HasComp<NCCityGroupFormationComponent>(entityUid))
        {
            error = "The actor belongs to a complete physical formation and requires a group hand-off.";
            return false;
        }

        if (!allowOffscreenCombat &&
            TryComp<CombatModeComponent>(entityUid, out var combat) && combat.IsInCombatMode)
        {
            error = "The actor is in physical combat mode.";
            return false;
        }

        foreach (var session in _players.Sessions)
        {
            if (session.AttachedEntity == entityUid)
            {
                error = "A player is attached to the actor entity.";
                return false;
            }
        }

        var target = _transform.GetMapCoordinates(entityUid);

        if (IsNearPlayerOrAnchor(target, _policy.DematerializationRadius))
        {
            error = "The actor remains inside player or camera relevance.";
            return false;
        }

        return true;
    }

    private async Task ObserveLoad(int generation, PendingRequest pending)
    {
        NCCityActorRecord? actor = null;
        NCCityActorHealthRecord? health = null;
        NCCityActorInventoryRecord? inventory = null;
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
        }
        catch (Exception exception)
        {
            error = exception;
        }

        _taskManager.RunOnMainThread(() => CompleteLoad(
            generation,
            pending.Request.RequestId,
            actor,
            health,
            inventory,
            error));
    }

    private void CompleteLoad(
        int generation,
        Guid requestId,
        NCCityActorRecord? actor,
        NCCityActorHealthRecord? health,
        NCCityActorInventoryRecord? inventory,
        Exception? loadError)
    {
        if (generation != _generation || !_active || _policy == null ||
            !_pending.TryGetValue(requestId, out var pending))
        {
            return;
        }

        if (!_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
            return;
        if (loadError != null)
        {
            _inFlight = Math.Max(0, _inFlight - 1);
            Retry(pending, now, $"Persistent actor load failed: {loadError.Message}", false);
            return;
        }
        if (actor == null || health == null || inventory == null)
        {
            _inFlight = Math.Max(0, _inFlight - 1);
            CompleteTerminal(pending, NCCityDematerializationStatus.Rejected,
                "The actor root or a mandatory domain aggregate is missing or unreadable.");
            return;
        }
        // NC: Arrested means an active physical escort. Imprisoned actors may return to the abstract holding state
        // once the detention system releases its explicit blocker and ordinary player relevance permits it.
        if (actor.Snapshot.Lifecycle.Status == NCCityActorStatus.Arrested)
        {
            _inFlight = Math.Max(0, _inFlight - 1);
            Retry(pending, now, "An arrested physical actor cannot be dematerialized during escort.", false);
            return;
        }
        if (!TryResolveBinding(pending, out var entityUid))
        {
            _inFlight = Math.Max(0, _inFlight - 1);
            CompleteTerminal(pending, NCCityDematerializationStatus.AlreadyDematerialized, null);
            return;
        }
        var allowOffscreenCombat = pending.Request.Reason is
            NCCityDematerializationReason.PlayerIrrelevant or NCCityDematerializationReason.BudgetPressure or
            NCCityDematerializationReason.GroupHandOff;
        if (!IsEligible(
                pending.Request.ActorId,
                entityUid,
                pending.Request.Reason == NCCityDematerializationReason.GroupHandOff,
                allowOffscreenCombat,
                out var error))
        {
            _inFlight = Math.Max(0, _inFlight - 1);
            Retry(pending, now, error!, false);
            return;
        }
        if (!_coordinates.TryGetCoordinates(entityUid, out var stableCoordinates))
        {
            _inFlight = Math.Max(0, _inFlight - 1);
            Retry(pending, now, "The actor has no stable city coordinates.", false);
            return;
        }

        var currentZone = _zones.GetLocationContext(stableCoordinates).MostSpecific;
        if (!currentZone.IsValid ||
            !_domains.TryCaptureMaterialized(
                pending.Request.ActorId,
                entityUid,
                health.AggregateVersion,
                inventory.AggregateVersion,
                now,
                out var capture,
                out error))
        {
            _inFlight = Math.Max(0, _inFlight - 1);
            Retry(pending, now, error ?? "The physical actor could not be captured safely.", false);
            return;
        }

        if (!_physicalization.TryBuildDematerializationWrites(
                pending.Request.ActorId,
                capture.Inventory.Summary,
                now,
                pending.Request.Source,
                out var economyWrites,
                out error))
        {
            _inFlight = Math.Max(0, _inFlight - 1);
            Retry(pending, now, error ?? "The physical economy state could not be captured safely.", false);
            return;
        }

        pending = pending with { Capture = capture, EconomyWrites = economyWrites };
        _pending[requestId] = pending;
        SetState(pending, NCCityDematerializationStatus.Committing, null);
        _ = ObserveCommit(generation, pending, actor.AggregateVersion, currentZone, capture);
    }

    private async Task ObserveCommit(
        int generation,
        PendingRequest pending,
        long expectedActorVersion,
        Content.Shared._NC.City.Zones.NCZoneId currentZone,
        NCCityActorDomainCapture capture)
    {
        NCCityActorDematerializationCommitResult result;
        try
        {
            result = await _actors.CommitDematerializationAsync(
                new NCCityActorDematerializationCommitRequest(
                    pending.CommitCommandId,
                    pending.Request.ActorId,
                    expectedActorVersion,
                    currentZone,
                    capture,
                    pending.Request.RequestId,
                    null,
                    pending.Request.Source,
                    pending.EconomyWrites));
        }
        catch (Exception exception)
        {
            result = new NCCityActorDematerializationCommitResult(
                NCCityActorMutationStatus.StorageFailure,
                pending.Request.ActorId,
                null,
                null,
                null,
                exception.Message);
        }

        _taskManager.RunOnMainThread(() => CompleteCommit(generation, pending.Request.RequestId, result));
    }

    private void CompleteCommit(
        int generation,
        Guid requestId,
        NCCityActorDematerializationCommitResult result)
    {
        if (generation != _generation || !_active || _policy == null ||
            !_pending.TryGetValue(requestId, out var pending))
        {
            return;
        }

        _inFlight = Math.Max(0, _inFlight - 1);
        if (!_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
            return;
        if (result.Status is not (NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate))
        {
            var resetCommand = result.Status == NCCityActorMutationStatus.VersionConflict;
            Retry(pending, now, result.Error ?? "The actor hand-off transaction failed.", resetCommand);
            return;
        }

        if (!TryResolveBinding(pending, out var entityUid))
        {
            _physicalization.TryRelease(pending.Request.ActorId, pending.Request.EntityUid);
            CompleteTerminal(pending, NCCityDematerializationStatus.Dematerialized, null);
            RaiseLocalEvent(new NCCityActorDematerializedEvent(
                requestId,
                pending.Request.ActorId,
                pending.Request.EntityUid,
                pending.Request.Reason));
            return;
        }

        var changedDuringCommit = PhysicalStateChangedDuringCommit(pending, entityUid, now);
        string? eligibilityError = null;
        var allowOffscreenCombat = pending.Request.Reason is
            NCCityDematerializationReason.PlayerIrrelevant or NCCityDematerializationReason.BudgetPressure or
            NCCityDematerializationReason.GroupHandOff;
        var remainsEligible = IsEligible(
            pending.Request.ActorId,
            entityUid,
            pending.Request.Reason == NCCityDematerializationReason.GroupHandOff,
            allowOffscreenCombat,
            out eligibilityError);
        if (changedDuringCommit || !remainsEligible)
        {
            if (result.Health != null)
            {
                _domains.TrySetMaterializedHealthRevision(
                    pending.Request.ActorId,
                    entityUid,
                    result.Health.AggregateVersion);
            }

            CompleteTerminal(
                pending,
                NCCityDematerializationStatus.CommittedRetained,
                changedDuringCommit
                    ? "Physical state changed during commit; the entity was retained for a fresh capture."
                    : eligibilityError);
            return;
        }

        if (!TryPrepareForDespawn(entityUid, out var preparationError))
        {
            if (result.Health != null)
            {
                _domains.TrySetMaterializedHealthRevision(
                    pending.Request.ActorId,
                    entityUid,
                    result.Health.AggregateVersion);
            }
            CompleteTerminal(pending, NCCityDematerializationStatus.CommittedRetained, preparationError);
            return;
        }

        _runtime.Unbind(entityUid);
        _physicalization.TryRelease(pending.Request.ActorId, entityUid);
        _despawnQueue.Enqueue(new PreparedDespawn(entityUid, _timing.CurTick + 1));
        CompleteTerminal(pending, NCCityDematerializationStatus.Dematerialized, null);
        RaiseLocalEvent(new NCCityActorDematerializedEvent(
            requestId,
            pending.Request.ActorId,
            entityUid,
            pending.Request.Reason));
    }

    /// <summary>
    /// Ends transient vanilla NPC ownership before the entity leaves the physical world. Pull joints and steering
    /// are detached first so the following tick cannot delete an entity from a stale interaction hierarchy.
    /// </summary>
    private bool TryPrepareForDespawn(EntityUid entityUid, out string? error)
    {
        error = null;
        if (!Exists(entityUid))
            return true;

        if (TryComp<PullableComponent>(entityUid, out var pullable) &&
            pullable.Puller != null && !_pulling.TryStopPull(entityUid, pullable))
        {
            error = "The actor could not leave an active pull relationship.";
            return false;
        }
        if (TryComp<PullerComponent>(entityUid, out var puller) && puller.Pulling is { } pulled &&
            TryComp<PullableComponent>(pulled, out var pulledComponent) &&
            !_pulling.TryStopPull(pulled, pulledComponent, entityUid))
        {
            error = "The actor could not release the entity it was pulling.";
            return false;
        }

        _steering.Unregister(entityUid);
        if (TryComp<HTNComponent>(entityUid, out var htn))
            _htn.SetHTNEnabled((entityUid, htn), false);
        if (TryComp<CombatModeComponent>(entityUid, out var combat))
            _combatMode.SetInCombatMode(entityUid, false, combat);
        return true;
    }

    private void FlushPreparedDespawns()
    {
        while (_despawnQueue.TryPeek(out var prepared) && prepared.ReadyAt <= _timing.CurTick)
        {
            _despawnQueue.Dequeue();
            if (!Exists(prepared.EntityUid))
                continue;

            QueueDel(prepared.EntityUid);
            _queuedEntityDeletions++;
            _lastQueuedEntityDeletion = prepared.EntityUid;
            _lastQueuedEntityDeletionTick = _timing.CurTick;
        }
    }

    private bool PhysicalStateChangedDuringCommit(PendingRequest pending, EntityUid entityUid, NCActiveWorldTime now)
    {
        var original = pending.Capture;
        if (original == null || !_coordinates.TryGetCoordinates(entityUid, out var coordinates) ||
            _zones.GetLocationContext(coordinates).MostSpecific is not { IsValid: true } currentZone)
        {
            return true;
        }

        if (!_actors.TryGetCached(pending.Request.ActorId, out var actor) ||
            actor.Snapshot.Location.CurrentZone != currentZone)
        {
            return true;
        }

        return _domains.HasPhysicalStateChanged(entityUid, original);
    }

    private bool IsNearPlayerOrAnchor(MapCoordinates target, float radius)
    {
        var radiusSquared = radius * radius;
        foreach (var session in _players.Sessions)
        {
            if (session.AttachedEntity is not { } attached || !Exists(attached))
            {
                continue;
            }

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
            if (!TryComp<NCCityRelevanceAnchorComponent>(anchor, out var component) || !component.Enabled ||
                !Exists(anchor))
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

    private bool TryResolveBinding(PendingRequest pending, out EntityUid entityUid)
    {
        entityUid = pending.Request.EntityUid;
        return TryResolveBinding(pending.Request.ActorId, entityUid);
    }

    private bool TryResolveBinding(NCActorId actorId, EntityUid entityUid)
    {
        return Exists(entityUid) && _runtime.TryGetEntity(actorId, out var bound) && bound == entityUid &&
               _runtime.TryGetActor(entityUid, out var boundActor) && boundActor == actorId;
    }

    private void Retry(PendingRequest pending, NCActiveWorldTime now, string error, bool resetCommand)
    {
        if (_policy == null || pending.ExpiresAt <= now)
        {
            CompleteTerminal(pending, NCCityDematerializationStatus.Expired, error);
            return;
        }

        pending = pending with
        {
            DueAt = now.Advance(_policy.DematerializationRetryDelay),
            Sequence = ++_sequence,
            CommitCommandId = resetCommand ? Guid.NewGuid() : pending.CommitCommandId,
            Capture = null,
        };
        _pending[pending.Request.RequestId] = pending;
        SetState(pending, NCCityDematerializationStatus.Retrying, error);
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

    private void SetState(PendingRequest pending, NCCityDematerializationStatus status, string? error)
    {
        _states[pending.Request.RequestId] = new NCCityDematerializationRequestState(
            pending.Request.RequestId,
            pending.Request.ActorId,
            pending.Request.EntityUid,
            pending.Request.Reason,
            status,
            pending.Attempts,
            pending.RequestedAt,
            pending.ExpiresAt,
            error);
    }

    private void CompleteTerminal(PendingRequest pending, NCCityDematerializationStatus status, string? error)
    {
        _pending.Remove(pending.Request.RequestId);
        _requestByActor.Remove(pending.Request.ActorId);
        SetState(pending, status, error);
        _completedOrder.Enqueue(pending.Request.RequestId);
        TrimCompletedHistory();
    }

    private void RetainTerminal(NCCityDematerializationRequestState state)
    {
        _states[state.RequestId] = state;
        _completedOrder.Enqueue(state.RequestId);
        TrimCompletedHistory();
    }

    private void TrimCompletedHistory()
    {
        if (_policy == null)
            return;
        while (_completedOrder.Count > _policy.MaximumCompletedDematerializationHistory)
        {
            var requestId = _completedOrder.Dequeue();
            if (!_pending.ContainsKey(requestId))
                _states.Remove(requestId);
        }
    }

    private static bool TryValidateRequest(NCCityDematerializationRequest request, out string? error)
    {
        if (request.RequestId == Guid.Empty || !request.ActorId.IsValid || !request.EntityUid.IsValid() ||
            !Enum.IsDefined(request.Reason) || request.Priority < 0 || request.Priority > 1000 ||
            string.IsNullOrWhiteSpace(request.Source) || request.Source.Length > MaximumSourceLength)
        {
            error = "The dematerialization request identity, priority, reason, or source is invalid.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool ValidatePolicy(NCCityRelevancePolicyPrototype policy)
    {
        return policy.EvaluationInterval > TimeSpan.Zero &&
               float.IsFinite(policy.ClusterMergeRadius) && policy.ClusterMergeRadius > 0f &&
               float.IsFinite(policy.ClusterSplitRadius) && policy.ClusterSplitRadius > policy.ClusterMergeRadius &&
               float.IsFinite(policy.DematerializationRadius) && policy.DematerializationRadius > 0f &&
               policy.MaterializationCooldown >= TimeSpan.Zero &&
               policy.MaximumAnchors > 0 && policy.CatalogPageSize > 0 &&
               policy.MaximumCandidateInspectionsPerCluster > 0 &&
               policy.MaximumMaterializationRequestsPerEvaluation > 0 &&
               policy.MaximumDematerializationInspectionsPerEvaluation > 0 &&
               policy.MaximumDematerializationRequestsPerEvaluation > 0 &&
               policy.MaximumQueuedDematerializations > 0 &&
               policy.MaximumDematerializationDispatchesPerTick > 0 &&
               policy.MaximumConcurrentDematerializations > 0 &&
               policy.MaximumDematerializationDispatchesPerTick <=
               policy.MaximumConcurrentDematerializations &&
               policy.MaximumCompletedDematerializationHistory > 0 &&
               policy.DematerializationRetryDelay > TimeSpan.Zero &&
               policy.DematerializationMaximumWait > TimeSpan.Zero;
    }

    private NCCityDematerializationRequestState RejectedState(
        NCCityDematerializationRequest request,
        NCActiveWorldTime now,
        string error)
    {
        return new NCCityDematerializationRequestState(
            request.RequestId,
            request.ActorId,
            request.EntityUid,
            request.Reason,
            NCCityDematerializationStatus.Rejected,
            0,
            now,
            now,
            error);
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        Reset();
        if (!_prototypes.TryIndex(
                new ProtoId<NCCityRelevancePolicyPrototype>(DefaultPolicyId),
                out NCCityRelevancePolicyPrototype? policy) || policy == null || !ValidatePolicy(policy))
        {
            Log.Error($"Actor dematerialization cannot start: policy {DefaultPolicyId} is missing or invalid.");
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

    private void OnActorRehydrated(NCCityActorRehydratedEvent args)
    {
        if (_active && _policy != null &&
            _clock.TryGetCurrentTime(out var epoch, out var now) && epoch == _epoch)
        {
            _cooldowns[args.ActorId] = now.Advance(_policy.MaterializationCooldown);
        }
    }

    private void OnMaterializationChanged(NCCityActorMaterializationChangedEvent args)
    {
        if (args.Materialized)
            return;
        foreach (var key in _blockerLeases.Keys.Where(key => key.EntityUid == args.EntityUid).ToArray())
            _blockerLeases.Remove(key);
    }

    private void Reset()
    {
        _generation++;
        _active = false;
        _policy = null;
        _epoch = default;
        _inFlight = 0;
        _queuedEntityDeletions = 0;
        _lastQueuedEntityDeletion = null;
        _lastQueuedEntityDeletionTick = default;
        _pending.Clear();
        _requestByActor.Clear();
        _states.Clear();
        _cooldowns.Clear();
        _blockerLeases.Clear();
        _completedOrder.Clear();
        _queue.Clear();
        _despawnQueue.Clear();
    }

    private sealed record PendingRequest(
        NCCityDematerializationRequest Request,
        NCActiveWorldTime RequestedAt,
        NCActiveWorldTime ExpiresAt,
        int Attempts,
        long Sequence,
        NCActiveWorldTime DueAt,
        Guid CommitCommandId,
        NCCityActorDomainCapture? Capture,
        IReadOnlyList<NCPersistentAggregateWrite>? EconomyWrites);

    private readonly record struct QueuedRequest(Guid RequestId, long Sequence);

    private readonly record struct BlockerLeaseKey(
        EntityUid EntityUid,
        NCCityDematerializationBlockerFlags Blocker);

    private readonly record struct PreparedDespawn(EntityUid EntityUid, GameTick ReadyAt);

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
