// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Actors.Domains;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Actors;

/// <summary>
/// Coalesces significant physical NPC changes into bounded durable checkpoints and flushes every bound actor before
/// the active-world epoch closes. Checkpointing never deletes, relocates, or virtualizes an entity.
/// </summary>
public sealed partial class NCPhysicalActorCheckpointSystem : EntitySystem
{
    private static readonly ProtoId<NCPhysicalActorLifecyclePolicyPrototype> DefaultPolicy =
        new("NCDefaultPhysicalActorLifecycle");

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ITaskManager _taskManager = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCCityActorDomainRegistrySystem _domains = default!;

    private readonly Dictionary<NCActorId, PendingCheckpoint> _pending = [];
    private readonly List<NCActorId> _dueActors = [];
    private readonly List<(NCActorId ActorId, EntityUid EntityUid)> _bindings = [];
    private readonly HashSet<NCActorId> _closingCaptures = [];
    private readonly HashSet<NCActorId> _terminalActors = [];

    private NCPhysicalActorLifecyclePolicyPrototype? _policy;
    private NCSessionEpoch _epoch;
    private int _generation;
    private bool _active;
    private bool _closing;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnSessionClosing);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(OnSessionClosed);
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(OnClockFailed);
        SubscribeLocalEvent<NCCityActorPersistedEvent>(OnActorPersisted);
        SubscribeLocalEvent<NCCityActorBindingChangedEvent>(OnBindingChanged);
        SubscribeLocalEvent<NCCityActorBindingComponent, NCMedicalBodyChangedEvent>(OnMedicalBodyChanged);
        SubscribeLocalEvent<NCCityActorBindingComponent, EntityTerminatingEvent>(OnPhysicalActorTerminating);
        SubscribeLocalEvent<NCCityPersistentItemAdapterComponent, EntGotInsertedIntoContainerMessage>(
            OnPersistentItemInserted);
        SubscribeLocalEvent<NCCityPersistentItemAdapterComponent, EntGotRemovedFromContainerMessage>(
            OnPersistentItemRemoved);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_active || _closing || _policy == null ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
        {
            return;
        }

        // Event handlers only move a due time. Full medical/inventory serialization stays bounded per server tick.
        _dueActors.Clear();
        foreach (var (actorId, state) in _pending)
        {
            if (_dueActors.Count >= _policy.MaximumCheckpointDispatchesPerTick)
                break;
            if (state.InFlight != null || state.RecoveryBarrier != null ||
                state.DirtySince == null || state.DueAt > now ||
                state.RetryNotBefore is { } retry && retry > now)
            {
                continue;
            }
            _dueActors.Add(actorId);
        }

        foreach (var actorId in _dueActors)
            TryStartRuntimeCheckpoint(actorId, now);
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        Reset();
        if (!_prototypes.TryIndex(DefaultPolicy, out NCPhysicalActorLifecyclePolicyPrototype? policy) ||
            policy == null || !ValidPolicy(policy))
        {
            Log.Error($"Physical actor checkpoint policy {DefaultPolicy} is missing or invalid.");
            return;
        }

        _policy = policy;
        _epoch = args.Epoch;
        _active = true;
    }

    private void OnSessionClosing(NCActiveWorldSessionClosingEvent args)
    {
        if (!_active || _closing || args.Epoch != _epoch)
            return;

        _closing = true;
        _closingCaptures.Clear();
        _runtime.GetBindings(_bindings);
        foreach (var (actorId, entityUid) in _bindings)
        {
            if (_terminalActors.Contains(actorId) ||
                _actors.TryGetCached(actorId, out var actor) && IsTerminal(actor.Snapshot.Lifecycle.Status))
                continue;

            if (!TryBuildCheckpoint(actorId, entityUid, args.Time, "physical-actor-round-checkpoint",
                    out var request, out var error))
            {
                args.AddPendingWrite(Task.FromException(new InvalidOperationException(
                    $"Physical actor checkpoint capture failed for {actorId}: {error}.")));
                continue;
            }

            _closingCaptures.Add(actorId);
            var preceding = _pending.GetValueOrDefault(actorId)?.InFlight;
            args.AddPendingWrite(CommitClosingCheckpointAsync(preceding, request));
        }

        // An entity may have unbound after its immutable capture started. Keep the old epoch open until that already
        // submitted write reaches a terminal result, even though there is no longer a body to capture again.
        foreach (var (actorId, state) in _pending)
        {
            if (_closingCaptures.Contains(actorId))
                continue;
            if (state.RecoveryBarrier != null)
                args.AddPendingWrite(state.RecoveryBarrier);
            else if (state.InFlight != null)
                args.AddPendingWrite(state.InFlight);
        }
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
        if (_active && args.SessionEpoch == _epoch && IsTerminal(args.Status))
            MarkTerminal(args.ActorId);
    }

    private void OnBindingChanged(NCCityActorBindingChangedEvent args)
    {
        if (args.Bound || !_pending.TryGetValue(args.ActorId, out var state))
            return;

        // A not-yet-captured dirty state cannot be read after the authoritative binding is gone. An already submitted
        // immutable write remains valid and is allowed to finish before lifecycle recovery reads it.
        state.DirtySince = null;
        state.RetryNotBefore = null;
        if (state.InFlight == null && state.RecoveryBarrier == null)
            _pending.Remove(args.ActorId);
    }

    private void OnMedicalBodyChanged(
        Entity<NCCityActorBindingComponent> entity,
        ref NCMedicalBodyChangedEvent args)
    {
        if (args.Domains != NCMedicalDirtyDomains.None)
            QueueCheckpoint(entity.Comp.ActorId);
    }

    /// <summary>Stops checkpoint recovery after lifecycle observes an authoritative death or gib.</summary>
    public void ObserveTerminalState(NCActorId actorId) => MarkTerminal(actorId);

    private void OnPhysicalActorTerminating(
        Entity<NCCityActorBindingComponent> entity,
        ref EntityTerminatingEvent args)
    {
        if (!_active || _closing || _policy == null ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
        {
            return;
        }

        if (!_pending.TryGetValue(entity.Comp.ActorId, out var state))
        {
            state = new PendingCheckpoint();
            _pending.Add(entity.Comp.ActorId, state);
        }
        if (_terminalActors.Contains(entity.Comp.ActorId) || state.RecoveryBarrier != null)
            return;

        Task barrier;
        if (!TryBuildCheckpoint(
                entity.Comp.ActorId,
                entity.Owner,
                now,
                "physical-actor-unbind-checkpoint",
                out var request,
                out var error,
                true))
        {
            barrier = Task.FromException(new InvalidOperationException(
                $"Physical actor unbind checkpoint capture failed for {entity.Comp.ActorId}: {error}."));
        }
        else
        {
            barrier = CommitClosingCheckpointAsync(state.InFlight, request);
        }

        state.DirtySince = null;
        state.RetryNotBefore = null;
        state.RecoveryBarrier = barrier;
        _ = ObserveRecoveryBarrierAsync(_generation, entity.Comp.ActorId, barrier);
    }

    /// <summary>
    /// Returns the immutable write that must settle before lifecycle recovery may read this actor's snapshot.
    /// A faulted barrier deliberately blocks stale recovery instead of resurrecting an older body state.
    /// </summary>
    public Task GetRecoveryBarrier(NCActorId actorId)
    {
        if (!_pending.TryGetValue(actorId, out var state))
            return Task.CompletedTask;
        return state.RecoveryBarrier ?? state.InFlight ?? Task.CompletedTask;
    }

    private void OnPersistentItemInserted(
        Entity<NCCityPersistentItemAdapterComponent> entity,
        ref EntGotInsertedIntoContainerMessage args)
    {
        QueueOwningActor(args.Container.Owner);
    }

    private void OnPersistentItemRemoved(
        Entity<NCCityPersistentItemAdapterComponent> entity,
        ref EntGotRemovedFromContainerMessage args)
    {
        // Robust raises removal after reparenting the item. The old container owner is therefore the only stable
        // route back to the actor that just lost the item.
        QueueOwningActor(args.Container.Owner);
    }

    private void QueueOwningActor(EntityUid containerOwner)
    {
        // Container events are directed to the stable item. Walking only its bounded parent chain catches hands,
        // equipment and nested carried containers without a world-wide inventory scan.
        if (TryQueueBoundActor(containerOwner))
            return;

        foreach (var container in _containers.GetContainingContainers((containerOwner, null)))
        {
            if (TryQueueBoundActor(container.Owner))
                return;
        }
    }

    private bool TryQueueBoundActor(EntityUid entityUid)
    {
        if (!TryComp<NCCityActorBindingComponent>(entityUid, out var binding) ||
            !_runtime.TryGetActor(entityUid, out var actorId) || actorId != binding.ActorId)
        {
            return false;
        }

        QueueCheckpoint(actorId);
        return true;
    }

    private void QueueCheckpoint(NCActorId actorId)
    {
        if (!_active || _closing || _policy == null || !actorId.IsValid ||
            _terminalActors.Contains(actorId) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch ||
            _actors.TryGetCached(actorId, out var actor) && IsTerminal(actor.Snapshot.Lifecycle.Status))
        {
            return;
        }

        if (!_pending.TryGetValue(actorId, out var state))
        {
            state = new PendingCheckpoint();
            _pending.Add(actorId, state);
        }
        state.ChangeRevision++;
        state.DirtySince ??= now;
        var trailingEdge = now.Advance(_policy.CheckpointDebounce);
        var maximumEdge = state.DirtySince.Value.Advance(_policy.CheckpointMaximumDeferral);
        state.DueAt = trailingEdge <= maximumEdge ? trailingEdge : maximumEdge;
    }

    private void TryStartRuntimeCheckpoint(NCActorId actorId, NCActiveWorldTime now)
    {
        if (_policy == null || !_pending.TryGetValue(actorId, out var state) ||
            state.InFlight != null || state.RecoveryBarrier != null || state.DirtySince == null)
        {
            return;
        }

        if (!_runtime.TryGetEntity(actorId, out var entityUid) || !Exists(entityUid))
        {
            _pending.Remove(actorId);
            return;
        }
        if (!TryBuildCheckpoint(actorId, entityUid, now, "physical-actor-runtime-checkpoint",
                out var request, out var error))
        {
            ScheduleRetry(state, now, error);
            return;
        }

        var capturedRevision = state.ChangeRevision;
        state.DirtySince = null;
        state.RetryNotBefore = null;
        state.LastError = null;
        var commit = _actors.CommitPhysicalCheckpointAsync(request);
        state.InFlight = commit;
        _ = ObserveRuntimeCheckpointAsync(_generation, actorId, capturedRevision, commit);
    }

    private async Task ObserveRuntimeCheckpointAsync(
        int generation,
        NCActorId actorId,
        long capturedRevision,
        Task<NCPhysicalActorCheckpointResult> commit)
    {
        NCPhysicalActorCheckpointResult? result = null;
        Exception? error = null;
        try
        {
            result = await commit;
        }
        catch (Exception exception)
        {
            error = exception;
        }

        _taskManager.RunOnMainThread(() =>
            CompleteRuntimeCheckpoint(generation, actorId, capturedRevision, commit, result, error));
    }

    private async Task ObserveRecoveryBarrierAsync(int generation, NCActorId actorId, Task barrier)
    {
        Exception? error = null;
        try
        {
            await barrier;
        }
        catch (Exception exception)
        {
            error = exception;
        }

        _taskManager.RunOnMainThread(() =>
        {
            if (generation != _generation || !_pending.TryGetValue(actorId, out var state) ||
                state.RecoveryBarrier != barrier)
            {
                return;
            }

            if (error != null)
            {
                // Retain the faulted barrier for this epoch. Any repeated recovery attempt must remain fail-closed.
                Log.Error($"Physical actor unbind checkpoint failed for {actorId}: {error}");
                return;
            }

            state.RecoveryBarrier = null;
            if (state.InFlight == null && state.DirtySince == null)
                _pending.Remove(actorId);
        });
    }

    private void CompleteRuntimeCheckpoint(
        int generation,
        NCActorId actorId,
        long capturedRevision,
        Task<NCPhysicalActorCheckpointResult> commit,
        NCPhysicalActorCheckpointResult? result,
        Exception? exception)
    {
        if (generation != _generation || !_pending.TryGetValue(actorId, out var state) || state.InFlight != commit)
            return;

        state.InFlight = null;
        if (exception == null && result?.Status is
                NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate)
        {
            if (state.ChangeRevision == capturedRevision && state.DirtySince == null &&
                state.RecoveryBarrier == null)
                _pending.Remove(actorId);
            return;
        }
        if (!_active || _closing || _policy == null ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
        {
            _pending.Remove(actorId);
            return;
        }
        if (_actors.TryGetCached(actorId, out var actor) && IsTerminal(actor.Snapshot.Lifecycle.Status))
        {
            MarkTerminal(actorId);
            return;
        }

        ScheduleRetry(state, now, exception?.Message ?? result?.Error ?? result?.Status.ToString() ??
            "unknown checkpoint failure");
    }

    private void ScheduleRetry(PendingCheckpoint state, NCActiveWorldTime now, string? error)
    {
        if (_policy == null)
            return;

        state.DirtySince ??= now;
        state.DueAt = now;
        state.RetryNotBefore = now.Advance(_policy.CheckpointRetryDelay);
        var normalized = error ?? "required cached state is missing";
        if (state.LastError == normalized)
            return;
        state.LastError = normalized;
        Log.Warning($"Physical actor runtime checkpoint deferred: {normalized}.");
    }

    private bool TryBuildCheckpoint(
        NCActorId actorId,
        EntityUid entityUid,
        NCActiveWorldTime now,
        string auditSource,
        out NCPhysicalActorCheckpointRequest request,
        out string? error,
        bool allowTerminating = false)
    {
        request = default!;
        error = null;
        if (_terminalActors.Contains(actorId) ||
            !_actors.TryGetCached(actorId, out var actor) || IsTerminal(actor.Snapshot.Lifecycle.Status) ||
            !_domains.TryGetCached(actorId, out var health, out var inventory) ||
            !_domains.TryCaptureMaterialized(
                actorId,
                entityUid,
                health.AggregateVersion,
                inventory.AggregateVersion,
                now,
                out var capture,
                out error,
                allowTerminating))
        {
            error ??= "required cached state is missing or terminal";
            return false;
        }

        var currentZone = default(NCZoneId);
        NCCityActorPhysicalRecoveryState? recovery = null;
        if (_coordinates.TryGetCoordinates(entityUid, out var coordinates) &&
            _zones.GetLocationContext(coordinates).MostSpecific is { IsValid: true } stableZone)
        {
            currentZone = stableZone;
            recovery = new NCCityActorPhysicalRecoveryState(
                coordinates.NetworkId,
                coordinates.Position.X,
                coordinates.Position.Y,
                coordinates.Z,
                currentZone);
        }

        // Ordinary maps have no durable CityGraph coordinate. Their actor domains are still saved, while the next
        // round deliberately chooses a normal SS14 spawn point instead of persisting a runtime MapId.
        var commandId = Guid.NewGuid();
        request = new NCPhysicalActorCheckpointRequest(
            commandId,
            actorId,
            actor.AggregateVersion,
            currentZone,
            capture,
            commandId,
            null,
            auditSource,
            null,
            recovery);
        return true;
    }

    private async Task CommitClosingCheckpointAsync(
        Task<NCPhysicalActorCheckpointResult>? preceding,
        NCPhysicalActorCheckpointRequest captured)
    {
        var request = captured;
        if (preceding != null)
        {
            await preceding;
            var rebased = await TryRebaseClosingCheckpointAsync(captured);
            if (rebased == null)
                return;
            request = rebased;
        }

        var result = await _actors.CommitPhysicalCheckpointAsync(request);
        if (result.Status == NCCityActorMutationStatus.VersionConflict)
        {
            var rebased = await TryRebaseClosingCheckpointAsync(captured);
            if (rebased == null)
                return;
            result = await _actors.CommitPhysicalCheckpointAsync(rebased);
        }

        if (result.Status is not (NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate))
        {
            throw new InvalidOperationException(
                $"Physical actor checkpoint commit failed for {captured.ActorId}: " +
                $"{result.Error ?? result.Status.ToString()}.");
        }
    }

    private async Task<NCPhysicalActorCheckpointRequest?> TryRebaseClosingCheckpointAsync(
        NCPhysicalActorCheckpointRequest captured)
    {
        var actorTask = _actors.GetAsync(captured.ActorId, true);
        var healthTask = _domains.GetHealthAsync(captured.ActorId, true);
        var inventoryTask = _domains.GetInventoryAsync(captured.ActorId, true);
        await Task.WhenAll(actorTask, healthTask, inventoryTask);
        var actor = await actorTask;
        var health = await healthTask;
        var inventory = await inventoryTask;
        if (actor == null || health == null || inventory == null)
        {
            throw new InvalidOperationException(
                $"Physical actor checkpoint rebase failed for {captured.ActorId}: durable state is missing.");
        }
        if (IsTerminal(actor.Snapshot.Lifecycle.Status))
            return null;

        var commandId = Guid.NewGuid();
        return captured with
        {
            CommandId = commandId,
            ExpectedActorVersion = actor.AggregateVersion,
            Domains = captured.Domains with
            {
                ExpectedHealthVersion = health.AggregateVersion,
                ExpectedInventoryVersion = inventory.AggregateVersion,
            },
            CorrelationId = commandId,
            CausationId = captured.CommandId,
        };
    }

    private void MarkTerminal(NCActorId actorId)
    {
        _terminalActors.Add(actorId);
        if (!_pending.TryGetValue(actorId, out var state))
            return;

        state.DirtySince = null;
        state.RetryNotBefore = null;
        // The death/status mutation is the only durability barrier that matters now. Any older checkpoint remains
        // observed by its continuation but may no longer delay close or recovery of this terminal actor.
        _pending.Remove(actorId);
    }

    private void Reset()
    {
        _generation++;
        _active = false;
        _closing = false;
        _policy = null;
        _epoch = default;
        _pending.Clear();
        _dueActors.Clear();
        _bindings.Clear();
        _closingCaptures.Clear();
        _terminalActors.Clear();
    }

    private static bool ValidPolicy(NCPhysicalActorLifecyclePolicyPrototype policy) =>
        policy.MaximumCheckpointDispatchesPerTick is > 0 and <= 128 &&
        policy.CheckpointDebounce > TimeSpan.Zero &&
        policy.CheckpointMaximumDeferral >= policy.CheckpointDebounce &&
        policy.CheckpointRetryDelay > TimeSpan.Zero;

    private static bool IsTerminal(NCCityActorStatus status) =>
        status is NCCityActorStatus.Dead or NCCityActorStatus.Archived;

    private sealed class PendingCheckpoint
    {
        public long ChangeRevision;
        public NCActiveWorldTime? DirtySince;
        public NCActiveWorldTime DueAt;
        public NCActiveWorldTime? RetryNotBefore;
        public Task<NCPhysicalActorCheckpointResult>? InFlight;
        public Task? RecoveryBarrier;
        public string? LastError;
    }
}
