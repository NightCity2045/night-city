// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.LivingCity.Simulation;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Groups;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Groups;

/// <summary>
/// Coordinates all members through the actor physicalization boundaries and publishes only complete formations.
/// Failed materialization compensates by returning actors created by that request to strategic simulation.
/// </summary>
public sealed partial class NCCityGroupPhysicalizationSystem : EntitySystem
{
    private const string DefaultPolicyId = "NCDefaultCityGroupPhysicalizationPolicy";
    private const int MaximumSourceLength = 128;

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ITaskManager _taskManager = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityGroupRegistrySystem _groups = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCCityActorMaterializationSystem _materialization = default!;
    [Dependency] private NCCityActorDematerializationSystem _dematerialization = default!;
    [Dependency] private NCCityGroupRuntimeFormationSystem _formations = default!;
    [Dependency] private NCAILoadGovernorSystem _loadGovernor = default!;

    private readonly Dictionary<Guid, NCCityGroupPhysicalizationRequestState> _states = [];
    private readonly Dictionary<Guid, PendingRequest> _pending = [];
    private readonly Dictionary<NCGroupId, Guid> _requestByGroup = [];
    private readonly Queue<Guid> _completedOrder = [];
    private readonly PriorityQueue<QueuedRequest, RequestPriority> _queue = new();
    private readonly List<Guid> _activeRequests = [];
    private readonly Dictionary<NCGroupId, GroupCatalogEntry> _catalog = [];
    private readonly Dictionary<NCActorId, NCGroupId> _groupByActor = [];

    private NCCityGroupPhysicalizationPolicyPrototype? _policy;
    private NCSessionEpoch _epoch;
    private int _generation;
    private int _inFlight;
    private int _activeCursor;
    private long _sequence;
    private bool _active;
    private bool _catalogRecovered;

    public bool IsActive => _active;
    public bool CatalogRecovered => _catalogRecovered;
    public int CatalogGroupCount => _catalog.Count;
    public int PendingRequestCount => _pending.Count;
    public int InFlightCount => _inFlight;
    public int RetainedResultCount => _states.Count - _pending.Count;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(OnSessionClosed);
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(OnClockFailed);
        SubscribeLocalEvent<NCCityGroupPersistedEvent>(OnGroupPersisted);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_active || _policy == null ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
        {
            return;
        }

        using var work = _loadGovernor.MeasureWork(NCAILoadWorkKind.GroupPhysicalization);
        _loadGovernor.ReportQueue(
            NCAILoadQueueKind.GroupPhysicalization,
            _pending.Count,
            _policy.MaximumQueuedRequests);
        DispatchLoads(now);
        PollActive(now);
    }

    public bool TryGetMaterializeTogetherGroup(
        NCActorId actorId,
        out NCGroupId groupId,
        out int memberCount)
    {
        groupId = default;
        memberCount = 0;
        if (!_catalogRecovered || !_groupByActor.TryGetValue(actorId, out groupId) ||
            !_catalog.TryGetValue(groupId, out var group) || !group.MaterializeTogether || !group.Active)
        {
            return false;
        }
        memberCount = group.Members.Length;
        return true;
    }

    public bool HasActiveRequest(NCGroupId groupId) => _requestByGroup.ContainsKey(groupId);

    public bool IsEligibleForDematerialization(NCGroupId groupId, out string? error)
    {
        error = null;
        if (!_catalogRecovered || !_catalog.TryGetValue(groupId, out var group) ||
            !group.MaterializeTogether || !group.Active)
        {
            error = "The group is absent from the active materialize-together catalog.";
            return false;
        }

        var materialized = 0;
        foreach (var actorId in group.Members)
        {
            if (!_runtime.TryGetEntity(actorId, out var entity) || !Exists(entity))
                continue;
            materialized++;
            if (!_dematerialization.IsEligibleForOffscreenGroupHandOff(actorId, entity, out error))
                return false;
        }
        if (materialized == 0)
        {
            error = "The group has no physical members.";
            return false;
        }
        return true;
    }

    public NCCityGroupPhysicalizationRequestState Request(NCCityGroupPhysicalizationRequest request)
    {
        if (_states.TryGetValue(request.RequestId, out var replay))
            return replay;

        var now = default(NCActiveWorldTime);
        if (!_active || _policy == null ||
            !_clock.TryGetCurrentTime(out var epoch, out now) || epoch != _epoch)
        {
            return RejectedState(request, now, "Group physicalization requires an active A-Life round.");
        }
        if (!TryValidateRequest(request, _policy, out var error))
        {
            var rejected = RejectedState(request, now, error!);
            if (request.RequestId != Guid.Empty)
                RetainTerminal(rejected);
            return rejected;
        }
        if (_requestByGroup.TryGetValue(request.GroupId, out var activeRequest))
        {
            var rejected = RejectedState(request, now,
                $"Group already has active physicalization request {activeRequest}.");
            RetainTerminal(rejected);
            return rejected;
        }
        if (_pending.Count >= _policy.MaximumQueuedRequests)
        {
            var rejected = RejectedState(request, now, "The bounded group physicalization queue is full.");
            RetainTerminal(rejected);
            return rejected;
        }

        var maximumWait = request.MaximumWait ?? _policy.DefaultMaximumWait;
        var pending = new PendingRequest(
            request,
            now,
            now.Advance(maximumWait),
            now,
            ++_sequence);
        _pending.Add(request.RequestId, pending);
        _requestByGroup.Add(request.GroupId, request.RequestId);
        SetState(pending, NCCityGroupPhysicalizationStatus.Queued, null);
        Enqueue(pending);
        return _states[request.RequestId];
    }

    public bool TryGetRequest(Guid requestId, out NCCityGroupPhysicalizationRequestState state) =>
        _states.TryGetValue(requestId, out state!);

    private void DispatchLoads(NCActiveWorldTime now)
    {
        if (_policy == null)
            return;
        var dispatchLimit = _loadGovernor.TakeDispatchLimit(
            NCAILoadDispatchKind.GroupPhysicalization,
            _policy.MaximumDispatchesPerTick);
        var dispatched = 0;
        while (dispatched < dispatchLimit &&
               _inFlight < _policy.MaximumConcurrentLoads &&
               _queue.TryPeek(out var queued, out var priority) && priority.DueTicks <= now.Ticks)
        {
            _queue.Dequeue();
            if (!_pending.TryGetValue(queued.RequestId, out var pending) ||
                pending.Sequence != queued.Sequence || pending.Phase != PendingPhase.Queued)
            {
                continue;
            }
            if (pending.ExpiresAt <= now)
            {
                CompleteTerminal(pending, NCCityGroupPhysicalizationStatus.Expired,
                    "The group request expired before its persistent state could be loaded.");
                continue;
            }

            pending.Attempts++;
            pending.Phase = PendingPhase.Loading;
            SetState(pending, NCCityGroupPhysicalizationStatus.Loading, null);
            _inFlight++;
            dispatched++;
            _ = ObserveLoad(_generation, pending.Request.RequestId, pending.Request.GroupId);
        }
    }

    private async Task ObserveLoad(int generation, Guid requestId, NCGroupId groupId)
    {
        NCCityGroupRecord? group = null;
        List<NCCityActorRecord>? actors = null;
        Exception? error = null;
        try
        {
            group = await _groups.GetAsync(groupId, true);
            if (group != null)
            {
                actors = new List<NCCityActorRecord>(group.Snapshot.Members.Length);
                // Sequential reads keep one group load bounded instead of bursting one task per member.
                foreach (var member in group.Snapshot.Members)
                {
                    var actor = await _actors.GetAsync(member.ActorId, true);
                    if (actor != null)
                        actors.Add(actor);
                }
            }
        }
        catch (Exception exception)
        {
            error = exception;
        }

        _taskManager.RunOnMainThread(() => CompleteLoad(generation, requestId, group, actors, error));
    }

    private void CompleteLoad(
        int generation,
        Guid requestId,
        NCCityGroupRecord? group,
        List<NCCityActorRecord>? actors,
        Exception? loadError)
    {
        if (generation != _generation || !_active || _policy == null ||
            !_pending.TryGetValue(requestId, out var pending) || pending.Phase != PendingPhase.Loading)
        {
            return;
        }
        _inFlight = Math.Max(0, _inFlight - 1);
        if (!_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
            return;
        if (loadError != null)
        {
            Retry(pending, now, $"Persistent group load failed: {loadError.Message}");
            return;
        }
        if (!ValidateLoadedGroup(group, actors, _policy, out var error))
        {
            CompleteTerminal(pending, NCCityGroupPhysicalizationStatus.Rejected, error);
            return;
        }

        pending.Group = group!;
        pending.GroupVersion = group!.AggregateVersion;
        pending.TotalMembers = group.Snapshot.Members.Length;
        if (pending.Request.Direction == NCCityGroupPhysicalizationDirection.Materialize)
            BeginMaterialization(pending, now);
        else
            BeginDematerialization(pending);
    }

    private void BeginMaterialization(PendingRequest pending, NCActiveWorldTime now)
    {
        var group = pending.Group!;
        foreach (var member in group.Snapshot.Members)
        {
            if (_runtime.TryGetEntity(member.ActorId, out var existing) && Exists(existing))
            {
                pending.PreexistingActors.Add(member.ActorId);
                continue;
            }

            var childId = DeriveRequestId(pending.Request.RequestId, member.ActorId, "materialize");
            var child = _materialization.Request(new NCCityMaterializationRequest(
                childId,
                member.ActorId,
                MapMaterializationReason(pending.Request.Reason),
                pending.Request.Priority,
                pending.Request.SelectionSeed + unchecked((uint) member.ActorId.Value.GetHashCode()),
                pending.ExpiresAt.Elapsed - now.Elapsed,
                "living-city.group-physicalization"));
            pending.ActorRequests[member.ActorId] = childId;
            if (child.Status is NCCityMaterializationRequestStatus.Materialized)
                pending.NewlyMaterializedActors.Add(member.ActorId);
        }

        if (pending.ActorRequests.Count == 0)
        {
            ApplyFormation(pending, alreadyPhysical: true);
            return;
        }
        pending.Phase = PendingPhase.Materializing;
        AddActive(pending.Request.RequestId);
        SetState(pending, NCCityGroupPhysicalizationStatus.MaterializingMembers, null);
    }

    private void BeginDematerialization(PendingRequest pending)
    {
        var allowOffscreenCombat = pending.Request.Reason is NCCityGroupPhysicalizationReason.PlayerRelevance or
            NCCityGroupPhysicalizationReason.BudgetPressure;
        foreach (var member in pending.Group!.Snapshot.Members)
        {
            if (_runtime.TryGetEntity(member.ActorId, out var entity) && Exists(entity) &&
                !(allowOffscreenCombat
                    ? _dematerialization.IsEligibleForOffscreenGroupHandOff(member.ActorId, entity, out var error)
                    : _dematerialization.IsEligibleForGroupHandOff(member.ActorId, entity, out error)))
            {
                pending.Phase = PendingPhase.WaitingDematerializationEligibility;
                AddActive(pending.Request.RequestId);
                SetState(pending, NCCityGroupPhysicalizationStatus.DematerializingMembers,
                    $"Waiting for group member {member.ActorId}: {error}");
                return;
            }
        }

        foreach (var member in pending.Group!.Snapshot.Members)
        {
            if (!_runtime.TryGetEntity(member.ActorId, out var entity) || !Exists(entity))
                continue;
            var childId = DeriveRequestId(pending.Request.RequestId, member.ActorId, "dematerialize");
            _dematerialization.Request(new NCCityDematerializationRequest(
                childId,
                member.ActorId,
                entity,
                NCCityDematerializationReason.GroupHandOff,
                pending.Request.Priority,
                "living-city.group-physicalization"));
            pending.ActorRequests[member.ActorId] = childId;
        }

        if (pending.ActorRequests.Count == 0)
        {
            _formations.ClearGroup(pending.Request.GroupId);
            CompleteTerminal(pending, NCCityGroupPhysicalizationStatus.AlreadyStrategic, null);
            return;
        }
        pending.Phase = PendingPhase.Dematerializing;
        AddActive(pending.Request.RequestId);
        SetState(pending, NCCityGroupPhysicalizationStatus.DematerializingMembers, null);
    }

    private void PollActive(NCActiveWorldTime now)
    {
        if (_policy == null || _activeRequests.Count == 0)
            return;
        _activeCursor %= _activeRequests.Count;
        var polls = Math.Min(_policy.MaximumActiveRequestPollsPerTick, _activeRequests.Count);
        var requestIds = new Guid[polls];
        for (var i = 0; i < polls; i++)
            requestIds[i] = _activeRequests[(_activeCursor + i) % _activeRequests.Count];
        _activeCursor = (_activeCursor + polls) % _activeRequests.Count;

        foreach (var requestId in requestIds)
        {
            if (!_pending.TryGetValue(requestId, out var pending))
                continue;
            if (pending.Invalidated)
            {
                FailOrRollback(pending, now, "The persistent group changed during physical hand-off.");
                continue;
            }
            switch (pending.Phase)
            {
                case PendingPhase.Materializing:
                    PollMaterialization(pending, now);
                    break;
                case PendingPhase.SettlingMaterialization:
                    PollMaterializationSettlement(pending, now);
                    break;
                case PendingPhase.Dematerializing:
                    PollDematerialization(pending, now);
                    break;
                case PendingPhase.WaitingDematerializationEligibility:
                    if (pending.ExpiresAt <= now)
                        CompleteTerminal(pending, NCCityGroupPhysicalizationStatus.Expired,
                            "The group never became safe for a coordinated dematerialization.");
                    else
                        BeginDematerialization(pending);
                    break;
                case PendingPhase.RollingBack:
                    PollRollback(pending, now);
                    break;
            }
        }
    }

    private void PollMaterialization(PendingRequest pending, NCActiveWorldTime now)
    {
        var completed = pending.PreexistingActors.Count;
        foreach (var (actorId, requestId) in pending.ActorRequests)
        {
            if (!_materialization.TryGetRequest(requestId, out var state))
                return;
            switch (state.Status)
            {
                case NCCityMaterializationRequestStatus.Queued:
                case NCCityMaterializationRequestStatus.Loading:
                case NCCityMaterializationRequestStatus.Retrying:
                    continue;
                case NCCityMaterializationRequestStatus.Materialized:
                    pending.NewlyMaterializedActors.Add(actorId);
                    completed++;
                    break;
                case NCCityMaterializationRequestStatus.AlreadyMaterialized:
                    completed++;
                    break;
                default:
                    FailOrRollback(pending, now,
                        $"Actor {actorId} materialization ended as {state.Status}: {state.Error}");
                    return;
            }
        }
        pending.CompletedMembers = completed;
        SetState(pending, NCCityGroupPhysicalizationStatus.MaterializingMembers, null);
        if (completed == pending.TotalMembers)
            ApplyFormation(pending, alreadyPhysical: pending.NewlyMaterializedActors.Count == 0);
        else if (pending.ExpiresAt <= now)
            FailOrRollback(pending, now, "The group materialization request expired.");
    }

    private void ApplyFormation(PendingRequest pending, bool alreadyPhysical)
    {
        if (_policy == null)
            return;
        pending.Phase = PendingPhase.ApplyingFormation;
        SetState(pending, NCCityGroupPhysicalizationStatus.ApplyingFormation, null);
        var bindings = new Dictionary<NCActorId, EntityUid>(pending.TotalMembers);
        foreach (var member in pending.Group!.Snapshot.Members)
        {
            if (!_runtime.TryGetEntity(member.ActorId, out var entity) || !Exists(entity))
            {
                FailOrRollback(pending, CurrentTime(), $"Actor {member.ActorId} lost its binding before formation apply.");
                return;
            }
            bindings.Add(member.ActorId, entity);
        }
        if (!_formations.TryApply(pending.Group, bindings, _policy, out _, out var error))
        {
            FailOrRollback(pending, CurrentTime(), error ?? "The runtime formation could not be applied.");
            return;
        }

        pending.CompletedMembers = pending.TotalMembers;
        CompleteTerminal(
            pending,
            alreadyPhysical ? NCCityGroupPhysicalizationStatus.AlreadyPhysical :
                NCCityGroupPhysicalizationStatus.Materialized,
            null);
        RaiseLocalEvent(new NCCityGroupPhysicalizationChangedEvent(
            pending.Request.RequestId,
            pending.Request.GroupId,
            NCCityGroupPhysicalizationDirection.Materialize,
            pending.TotalMembers));
    }

    private void PollDematerialization(PendingRequest pending, NCActiveWorldTime now)
    {
        var completed = pending.TotalMembers - pending.ActorRequests.Count;
        foreach (var (actorId, requestId) in pending.ActorRequests)
        {
            if (!_dematerialization.TryGetRequest(requestId, out var state))
                return;
            switch (state.Status)
            {
                case NCCityDematerializationStatus.Requested:
                case NCCityDematerializationStatus.Loading:
                case NCCityDematerializationStatus.Capturing:
                case NCCityDematerializationStatus.Committing:
                case NCCityDematerializationStatus.Retrying:
                    continue;
                case NCCityDematerializationStatus.Dematerialized:
                case NCCityDematerializationStatus.AlreadyDematerialized:
                    completed++;
                    break;
                default:
                    CompletePartialFailure(pending,
                        $"Actor {actorId} dematerialization ended as {state.Status}: {state.Error}");
                    return;
            }
        }
        pending.CompletedMembers = completed;
        SetState(pending, NCCityGroupPhysicalizationStatus.DematerializingMembers, null);
        if (completed == pending.TotalMembers)
        {
            _formations.ClearGroup(pending.Request.GroupId);
            CompleteTerminal(pending, NCCityGroupPhysicalizationStatus.Dematerialized, null);
            RaiseLocalEvent(new NCCityGroupPhysicalizationChangedEvent(
                pending.Request.RequestId,
                pending.Request.GroupId,
                NCCityGroupPhysicalizationDirection.Dematerialize,
                pending.TotalMembers));
        }
        else if (pending.ExpiresAt <= now)
        {
            CompletePartialFailure(pending, "The group dematerialization request expired.");
        }
    }

    private void FailOrRollback(PendingRequest pending, NCActiveWorldTime now, string error)
    {
        if (pending.Request.Direction == NCCityGroupPhysicalizationDirection.Dematerialize)
        {
            CompletePartialFailure(pending, error);
            return;
        }
        if (pending.Phase is PendingPhase.SettlingMaterialization or PendingPhase.RollingBack)
            return;
        pending.Failure = error;
        if (pending.ActorRequests.Count == 0 && pending.NewlyMaterializedActors.Count == 0)
        {
            CompleteTerminal(pending, NCCityGroupPhysicalizationStatus.Rejected, error);
            return;
        }

        pending.Phase = PendingPhase.SettlingMaterialization;
        pending.ExpiresAt = now.Advance(_policy!.RollbackMaximumWait);
        SetState(pending, NCCityGroupPhysicalizationStatus.RollingBack, error);
        PollMaterializationSettlement(pending, now);
    }

    private void PollMaterializationSettlement(PendingRequest pending, NCActiveWorldTime now)
    {
        var allTerminal = true;
        foreach (var (actorId, requestId) in pending.ActorRequests)
        {
            if (!_materialization.TryGetRequest(requestId, out var state) ||
                state.Status is NCCityMaterializationRequestStatus.Queued or
                    NCCityMaterializationRequestStatus.Loading or
                    NCCityMaterializationRequestStatus.Retrying)
            {
                allTerminal = false;
                continue;
            }
            if (state.Status == NCCityMaterializationRequestStatus.Materialized)
                pending.NewlyMaterializedActors.Add(actorId);
        }
        if (!allTerminal)
        {
            if (pending.ExpiresAt <= now)
                CompletePartialFailure(pending, $"Child materialization did not settle after failure: {pending.Failure}");
            return;
        }

        BeginRollback(pending, now);
    }

    private void BeginRollback(PendingRequest pending, NCActiveWorldTime now)
    {
        pending.ActorRequests.Clear();
        foreach (var actorId in pending.NewlyMaterializedActors)
        {
            if (!_runtime.TryGetEntity(actorId, out var entity) || !Exists(entity))
                continue;
            var childId = DeriveRequestId(pending.Request.RequestId, actorId, "rollback");
            _dematerialization.Request(new NCCityDematerializationRequest(
                childId,
                actorId,
                entity,
                NCCityDematerializationReason.GroupHandOff,
                pending.Request.Priority,
                "living-city.group-rollback"));
            pending.ActorRequests[actorId] = childId;
        }
        pending.ExpiresAt = now.Advance(_policy!.RollbackMaximumWait);
        pending.Phase = PendingPhase.RollingBack;
        SetState(pending, NCCityGroupPhysicalizationStatus.RollingBack, pending.Failure);
        if (pending.ActorRequests.Count == 0)
            CompleteTerminal(pending, NCCityGroupPhysicalizationStatus.FailedRolledBack, pending.Failure);
    }

    private void PollRollback(PendingRequest pending, NCActiveWorldTime now)
    {
        var completed = 0;
        foreach (var (actorId, requestId) in pending.ActorRequests)
        {
            if (!_dematerialization.TryGetRequest(requestId, out var state))
                return;
            if (state.Status is NCCityDematerializationStatus.Dematerialized or
                NCCityDematerializationStatus.AlreadyDematerialized)
            {
                completed++;
                continue;
            }
            if (state.Status is NCCityDematerializationStatus.Rejected or NCCityDematerializationStatus.Expired or
                NCCityDematerializationStatus.CommittedRetained)
            {
                CompletePartialFailure(pending,
                    $"Rollback of actor {actorId} ended as {state.Status}: {state.Error}");
                return;
            }
        }
        pending.CompletedMembers = completed;
        SetState(pending, NCCityGroupPhysicalizationStatus.RollingBack, pending.Failure);
        if (completed == pending.ActorRequests.Count)
            CompleteTerminal(pending, NCCityGroupPhysicalizationStatus.FailedRolledBack, pending.Failure);
        else if (pending.ExpiresAt <= now)
            CompletePartialFailure(pending, $"Rollback expired after failure: {pending.Failure}");
    }

    private void CompletePartialFailure(PendingRequest pending, string error)
    {
        _formations.ClearGroup(pending.Request.GroupId);
        CompleteTerminal(pending, NCCityGroupPhysicalizationStatus.PartialFailure, error);
    }

    private void Retry(PendingRequest pending, NCActiveWorldTime now, string error)
    {
        if (_policy == null || pending.ExpiresAt <= now)
        {
            CompleteTerminal(pending, NCCityGroupPhysicalizationStatus.Expired, error);
            return;
        }
        pending.Phase = PendingPhase.Queued;
        pending.DueAt = now.Advance(_policy.RetryDelay);
        pending.Sequence = ++_sequence;
        SetState(pending, NCCityGroupPhysicalizationStatus.Retrying, error);
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

    private void SetState(PendingRequest pending, NCCityGroupPhysicalizationStatus status, string? error)
    {
        _states[pending.Request.RequestId] = new NCCityGroupPhysicalizationRequestState(
            pending.Request.RequestId,
            pending.Request.GroupId,
            pending.Request.Direction,
            pending.Request.Reason,
            status,
            pending.Request.Priority,
            pending.Attempts,
            pending.TotalMembers,
            pending.CompletedMembers,
            pending.RequestedAt,
            pending.ExpiresAt,
            error);
    }

    private void CompleteTerminal(
        PendingRequest pending,
        NCCityGroupPhysicalizationStatus status,
        string? error)
    {
        _pending.Remove(pending.Request.RequestId);
        _requestByGroup.Remove(pending.Request.GroupId);
        _activeRequests.Remove(pending.Request.RequestId);
        SetState(pending, status, error);
        _completedOrder.Enqueue(pending.Request.RequestId);
        TrimCompletedHistory();
    }

    private void RetainTerminal(NCCityGroupPhysicalizationRequestState state)
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

    private void AddActive(Guid requestId)
    {
        if (!_activeRequests.Contains(requestId))
            _activeRequests.Add(requestId);
    }

    private async Task RecoverCatalog(int generation, int pageSize)
    {
        NCGroupId? after = null;
        try
        {
            while (true)
            {
                var page = await _groups.GetPageAsync(after, pageSize, false);
                var records = page.Records.ToArray();
                _taskManager.RunOnMainThread(() => ApplyRecoveredPage(generation, records));
                if (!page.HasMore || page.ContinuationGroupId is not { IsValid: true } continuation)
                    break;
                after = continuation;
            }
            _taskManager.RunOnMainThread(() =>
            {
                if (generation == _generation)
                    _catalogRecovered = true;
            });
        }
        catch (Exception exception)
        {
            _taskManager.RunOnMainThread(() =>
            {
                if (generation == _generation)
                    Log.Error($"Living-city group catalog recovery failed: {exception}");
            });
        }
    }

    private void ApplyRecoveredPage(int generation, IReadOnlyList<NCCityGroupRecord> records)
    {
        if (generation != _generation)
            return;
        foreach (var record in records)
            ApplyCatalog(record);
    }

    private void ApplyCatalog(NCCityGroupRecord record)
    {
        var groupId = record.Snapshot.GroupId;
        if (_catalog.TryGetValue(groupId, out var previous) && previous.Version > record.AggregateVersion)
            return;
        if (previous != null)
        {
            foreach (var actorId in previous.Members)
            {
                if (_groupByActor.GetValueOrDefault(actorId) == groupId)
                    _groupByActor.Remove(actorId);
            }
        }

        var entry = new GroupCatalogEntry(
            record.AggregateVersion,
            record.Snapshot.MaterializeTogether,
            record.Snapshot.Status is not (NCCityGroupStatus.Disbanded or NCCityGroupStatus.Archived),
            record.Snapshot.Members.Select(static member => member.ActorId).ToArray());
        _catalog[groupId] = entry;
        if (!entry.MaterializeTogether || !entry.Active)
            return;
        foreach (var actorId in entry.Members)
            _groupByActor[actorId] = groupId;
    }

    private void OnGroupPersisted(NCCityGroupPersistedEvent args)
    {
        if (!_active)
            return;
        if (_requestByGroup.TryGetValue(args.GroupId, out var requestId) &&
            _pending.TryGetValue(requestId, out var pending) && pending.GroupVersion > 0 &&
            pending.GroupVersion < args.AggregateVersion)
        {
            pending.Invalidated = true;
        }
        _ = RefreshCatalog(_generation, args.GroupId);
    }

    private async Task RefreshCatalog(int generation, NCGroupId groupId)
    {
        try
        {
            var group = await _groups.GetAsync(groupId, true);
            _taskManager.RunOnMainThread(() =>
            {
                if (generation == _generation && group != null)
                    ApplyCatalog(group);
            });
        }
        catch (Exception exception)
        {
            _taskManager.RunOnMainThread(() =>
            {
                if (generation == _generation)
                    Log.Error($"Failed to refresh physical group catalog {groupId}: {exception}");
            });
        }
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        Reset();
        if (!_prototypes.TryIndex(
                new ProtoId<NCCityGroupPhysicalizationPolicyPrototype>(DefaultPolicyId),
                out NCCityGroupPhysicalizationPolicyPrototype? policy) || policy == null || !ValidatePolicy(policy))
        {
            Log.Error($"Group physicalization cannot start: policy {DefaultPolicyId} is missing or invalid.");
            return;
        }
        _policy = policy;
        _epoch = args.Epoch;
        _active = true;
        _ = RecoverCatalog(_generation, policy.CatalogPageSize);
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

    private void Reset()
    {
        _generation++;
        _active = false;
        _catalogRecovered = false;
        _policy = null;
        _epoch = default;
        _inFlight = 0;
        _activeCursor = 0;
        _states.Clear();
        _pending.Clear();
        _requestByGroup.Clear();
        _completedOrder.Clear();
        _queue.Clear();
        _activeRequests.Clear();
        _catalog.Clear();
        _groupByActor.Clear();
    }

    private NCActiveWorldTime CurrentTime()
    {
        return _clock.TryGetCurrentTime(out var epoch, out var now) && epoch == _epoch ? now : default;
    }

    private static bool ValidateLoadedGroup(
        NCCityGroupRecord? group,
        List<NCCityActorRecord>? actors,
        NCCityGroupPhysicalizationPolicyPrototype policy,
        out string? error)
    {
        error = null;
        if (group == null || actors == null)
        {
            error = "The persistent group or one of its member actors is missing or unreadable.";
            return false;
        }
        var snapshot = group.Snapshot;
        if (!snapshot.MaterializeTogether || snapshot.Members.Length == 0 ||
            snapshot.Members.Length > policy.MaximumMembersPerRequest || actors.Count != snapshot.Members.Length ||
            snapshot.Status is NCCityGroupStatus.Disbanded or NCCityGroupStatus.Archived)
        {
            error = "The persistent group is inactive, unbounded, empty, or not configured to materialize together.";
            return false;
        }
        var actorById = actors.ToDictionary(static actor => actor.Snapshot.Identity.ActorId);
        foreach (var member in snapshot.Members)
        {
            if (!actorById.TryGetValue(member.ActorId, out var actor) ||
                actor.Snapshot.Location.CurrentZone != snapshot.Route.CurrentZone ||
                actor.Snapshot.Lifecycle.Frozen ||
                actor.Snapshot.Lifecycle.Status is NCCityActorStatus.Dead or NCCityActorStatus.Archived)
            {
                error = $"Group member {member.ActorId} is unavailable or not co-located in the group route zone.";
                return false;
            }
        }
        return true;
    }

    private static bool TryValidateRequest(
        NCCityGroupPhysicalizationRequest request,
        NCCityGroupPhysicalizationPolicyPrototype policy,
        out string? error)
    {
        error = null;
        var wait = request.MaximumWait ?? policy.DefaultMaximumWait;
        if (request.RequestId == Guid.Empty || !request.GroupId.IsValid ||
            !Enum.IsDefined(request.Direction) || !Enum.IsDefined(request.Reason) ||
            request.Priority < 0 || request.Priority > policy.MaximumPriority ||
            string.IsNullOrWhiteSpace(request.Source) || request.Source.Length > MaximumSourceLength ||
            wait <= TimeSpan.Zero || wait > policy.MaximumWait)
        {
            error = "Group request identity, direction, priority, lifetime or source is invalid.";
            return false;
        }
        return true;
    }

    private static bool ValidatePolicy(NCCityGroupPhysicalizationPolicyPrototype policy)
    {
        return policy.CatalogPageSize > 0 && policy.MaximumQueuedRequests > 0 &&
               policy.MaximumDispatchesPerTick > 0 && policy.MaximumConcurrentLoads > 0 &&
               policy.MaximumDispatchesPerTick <= policy.MaximumConcurrentLoads &&
               policy.MaximumActiveRequestPollsPerTick > 0 && policy.MaximumMembersPerRequest > 0 &&
               policy.MaximumCompletedRequestHistory > 0 && policy.MaximumPriority > 0 &&
               policy.RetryDelay > TimeSpan.Zero && policy.DefaultMaximumWait > TimeSpan.Zero &&
               policy.DefaultMaximumWait <= policy.MaximumWait && policy.RollbackMaximumWait > TimeSpan.Zero &&
               float.IsFinite(policy.LooseSpacing) && policy.LooseSpacing > 0f &&
               float.IsFinite(policy.ColumnSpacing) && policy.ColumnSpacing > 0f &&
               float.IsFinite(policy.WedgeSpacing) && policy.WedgeSpacing > 0f &&
               float.IsFinite(policy.DefensiveSpacing) && policy.DefensiveSpacing > 0f;
    }

    private static Guid DeriveRequestId(Guid requestId, NCActorId actorId, string phase)
    {
        var bytes = Encoding.UTF8.GetBytes($"{requestId:D}|{actorId.Value:D}|{phase}");
        var hash = SHA256.HashData(bytes);
        return new Guid(hash[..16]);
    }

    private static NCCityMaterializationReason MapMaterializationReason(NCCityGroupPhysicalizationReason reason) =>
        reason switch
        {
            NCCityGroupPhysicalizationReason.PlayerRelevance => NCCityMaterializationReason.PlayerRelevance,
            NCCityGroupPhysicalizationReason.Incident => NCCityMaterializationReason.Incident,
            NCCityGroupPhysicalizationReason.ServiceResponse => NCCityMaterializationReason.ServiceResponse,
            _ => NCCityMaterializationReason.Admin,
        };

    private static NCCityGroupPhysicalizationRequestState RejectedState(
        NCCityGroupPhysicalizationRequest request,
        NCActiveWorldTime now,
        string error) =>
        new(
            request.RequestId,
            request.GroupId,
            request.Direction,
            request.Reason,
            NCCityGroupPhysicalizationStatus.Rejected,
            request.Priority,
            0,
            0,
            0,
            now,
            now,
            error);

    private sealed class PendingRequest(
        NCCityGroupPhysicalizationRequest request,
        NCActiveWorldTime requestedAt,
        NCActiveWorldTime expiresAt,
        NCActiveWorldTime dueAt,
        long sequence)
    {
        public NCCityGroupPhysicalizationRequest Request { get; } = request;
        public NCActiveWorldTime RequestedAt { get; } = requestedAt;
        public NCActiveWorldTime ExpiresAt { get; set; } = expiresAt;
        public NCActiveWorldTime DueAt { get; set; } = dueAt;
        public long Sequence { get; set; } = sequence;
        public int Attempts { get; set; }
        public int TotalMembers { get; set; }
        public int CompletedMembers { get; set; }
        public long GroupVersion { get; set; }
        public NCCityGroupRecord? Group { get; set; }
        public PendingPhase Phase { get; set; } = PendingPhase.Queued;
        public bool Invalidated { get; set; }
        public string? Failure { get; set; }
        public Dictionary<NCActorId, Guid> ActorRequests { get; } = [];
        public HashSet<NCActorId> PreexistingActors { get; } = [];
        public HashSet<NCActorId> NewlyMaterializedActors { get; } = [];
    }

    private enum PendingPhase : byte
    {
        Queued,
        Loading,
        Materializing,
        SettlingMaterialization,
        ApplyingFormation,
        WaitingDematerializationEligibility,
        Dematerializing,
        RollingBack,
    }

    private sealed record GroupCatalogEntry(
        long Version,
        bool MaterializeTogether,
        bool Active,
        NCActorId[] Members);

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
