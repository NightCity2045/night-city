// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Neural;

/// <summary>
/// Schedules one independent GPT decision stream per physical NPC. Queue limits throttle calls, never bodies.
/// </summary>
public sealed partial class NCPhysicalActorBrainSystem : EntitySystem
{
    private static readonly ProtoId<NCPhysicalActorBrainPolicyPrototype> DefaultPolicy =
        new("NCDefaultPhysicalActorBrain");

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCNeuralContextBuilderSystem _context = default!;
    [Dependency] private ITaskManager _taskManager = default!;

    private readonly Dictionary<NCActorId, ScheduledDecision> _scheduled = [];
    private readonly HashSet<NCActorId> _inFlight = [];
    private readonly HashSet<NCActorId> _requestedAgain = [];
    private readonly HashSet<NCActorId> _retryRequested = [];
    private readonly PriorityQueue<QueuedDecision, (long DueTicks, long Sequence)> _queue = new();
    private NCPhysicalActorBrainPolicyPrototype? _policy;
    private NCSessionEpoch _epoch;
    private long _sequence;
    private int _generation;
    private bool _active;

    public int ScheduledActors => _scheduled.Count;
    public int SubmissionsInFlight => _inFlight.Count;
    public long DecisionsSubmitted { get; private set; }
    public long SubmissionRetries { get; private set; }

    /// <summary>Requests an immediate fresh GPT decision for a physical actor after a reactive world event.</summary>
    public bool RequestDecision(NCActorId actorId)
    {
        if (!_active || !_runtime.TryGetEntity(actorId, out var entity) || !Exists(entity) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
        {
            return false;
        }

        if (_inFlight.Contains(actorId))
            return _requestedAgain.Add(actorId);
        Schedule(actorId, now);
        return true;
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Reset());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Reset());
        SubscribeLocalEvent<NCCityActorBindingChangedEvent>(OnBindingChanged);
        SubscribeLocalEvent<NCNeuralResultEvent>(OnNeuralResult);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_active || _policy == null || !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
            return;

        var dispatched = 0;
        while (dispatched < _policy.MaximumDispatchesPerTick &&
               _queue.TryPeek(out var queued, out var priority) && priority.DueTicks <= now.Ticks)
        {
            _queue.Dequeue();
            if (!_scheduled.TryGetValue(queued.ActorId, out var current) || current.Sequence != queued.Sequence ||
                !_runtime.TryGetEntity(queued.ActorId, out var entity) || !Exists(entity))
                continue;

            _scheduled.Remove(queued.ActorId);
            if (!_inFlight.Add(queued.ActorId))
                continue;
            _ = ObserveSubmissionAsync(_generation, queued.ActorId);
            dispatched++;
        }
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        Reset();
        if (!_prototypes.TryIndex(DefaultPolicy, out NCPhysicalActorBrainPolicyPrototype? policy) ||
            policy == null || policy.DecisionInterval <= TimeSpan.Zero || policy.RetryInterval <= TimeSpan.Zero ||
            policy.InitialStagger < TimeSpan.Zero || policy.MaximumDispatchesPerTick is < 1 or > 128 ||
            policy.MaximumExecutionPulsesPerTick is < 1 or > 512 || policy.ExecutionPulseInterval <= TimeSpan.Zero)
        {
            Log.Error($"Physical actor brain policy {DefaultPolicy} is missing or invalid.");
            return;
        }
        _policy = policy;
        _epoch = args.Epoch;
        _active = true;
    }

    private void OnBindingChanged(NCCityActorBindingChangedEvent args)
    {
        if (!args.Bound)
        {
            _scheduled.Remove(args.ActorId);
            _requestedAgain.Remove(args.ActorId);
            _retryRequested.Remove(args.ActorId);
            return;
        }
        if (!_active || _policy == null || !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
            return;

        var raw = args.ActorId.Value.ToByteArray();
        var fraction = BitConverter.ToUInt32(raw, 0) / (double) uint.MaxValue;
        var stagger = TimeSpan.FromTicks((long) (_policy.InitialStagger.Ticks * fraction));
        Schedule(args.ActorId, now.Advance(stagger));
    }

    private void OnNeuralResult(NCNeuralResultEvent args)
    {
        var result = args.Result;
        if (!_active || _policy == null || result.Request.Kind != NCNeuralRequestKind.StrategicDecision ||
            result.Status == NCNeuralResultStatus.Accepted ||
            !_runtime.TryGetEntity(result.Request.ActorId, out var entity) || !Exists(entity) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
        {
            return;
        }

        // Immediate queue/budget failures can publish during Submit(); defer that retry until submission unwinds.
        if (_inFlight.Contains(result.Request.ActorId))
            _retryRequested.Add(result.Request.ActorId);
        else
            Schedule(result.Request.ActorId, now.Advance(_policy.RetryInterval));
    }

    private async Task ObserveSubmissionAsync(int generation, NCActorId actorId)
    {
        var submitted = false;
        Exception? error = null;
        try
        {
            submitted = await _context.SubmitAutonomousAsync(actorId, NCNeuralPriority.P3);
        }
        catch (Exception caught)
        {
            error = caught;
        }

        _taskManager.RunOnMainThread(() => CompleteSubmission(generation, actorId, submitted, error));
    }

    private void CompleteSubmission(int generation, NCActorId actorId, bool submitted, Exception? error)
    {
        if (generation != _generation)
            return;
        _inFlight.Remove(actorId);
        if (!_active || _policy == null || !_runtime.TryGetEntity(actorId, out var entity) || !Exists(entity) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
            return;

        if (submitted)
            DecisionsSubmitted++;
        else
        {
            SubmissionRetries++;
            if (error != null)
                Log.Warning($"Physical actor brain submission failed for {actorId}: {error.Message}");
        }

        var retryRequested = _retryRequested.Remove(actorId);
        var requestedAgain = _requestedAgain.Remove(actorId);
        Schedule(actorId, retryRequested || !submitted
            ? now.Advance(_policy.RetryInterval)
            : requestedAgain ? now : now.Advance(_policy.DecisionInterval));
    }

    private void Schedule(NCActorId actorId, NCActiveWorldTime due)
    {
        var scheduled = new ScheduledDecision(due, ++_sequence);
        _scheduled[actorId] = scheduled;
        _queue.Enqueue(new QueuedDecision(actorId, scheduled.Sequence), (due.Ticks, scheduled.Sequence));
    }

    private void Reset()
    {
        _generation++;
        _active = false;
        _policy = null;
        _epoch = default;
        _sequence = 0;
        _scheduled.Clear();
        _inFlight.Clear();
        _requestedAgain.Clear();
        _retryRequested.Clear();
        _queue.Clear();
        DecisionsSubmitted = 0;
        SubmissionRetries = 0;
    }

    private readonly record struct ScheduledDecision(NCActiveWorldTime Due, long Sequence);
    private readonly record struct QueuedDecision(NCActorId ActorId, long Sequence);
}
