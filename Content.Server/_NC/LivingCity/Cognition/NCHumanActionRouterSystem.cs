// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Cognition.Components;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Cognition;

/// <summary>
/// Single authoritative boundary between validated neural commands and existing gameplay executors. It owns lifecycle, timeout and
/// cleanup, but delegates every world mutation to a typed ECS subscriber.
/// </summary>
public sealed partial class NCHumanActionRouterSystem : EntitySystem
{
    private const int MaximumTraceEntries = 16;
    private const int MaximumRetainedResults = 20_000;
    private const int DurationSampleCapacity = 256;

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;

    private readonly Dictionary<NCActorId, NCHumanActionRuntimeSnapshot> _running = [];
    private readonly Dictionary<NCActorId, NCHumanActionRuntimeSnapshot> _lastResults = [];
    private readonly long[] _failureReasons = new long[Enum.GetValues<NCHumanActionFailureKind>().Length];
    private readonly float[] _durationSamples = new float[DurationSampleCapacity];
    private int _durationSampleCount;
    private int _durationSampleWriteIndex;
    private long _nextTraceSequence;
    private long _started;
    private long _completed;
    private long _failed;
    private long _interrupted;
    private long _timedOut;
    private long _unhandled;
    private bool _active;
    private string? _configurationError;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCNeuralActionStepEvent>(OnExecuteStep);
        SubscribeLocalEvent<NCCityActorBindingChangedEvent>(OnBindingChanged);
        SubscribeLocalEvent<NCHumanActionRuntimeComponent, ComponentShutdown>(OnRuntimeShutdown);
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => EndRound());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => EndRound());
    }

    public bool TryGetSnapshot(NCActorId actorId, out NCHumanActionRuntimeSnapshot snapshot)
    {
        if (_running.TryGetValue(actorId, out snapshot!))
            return true;
        return _lastResults.TryGetValue(actorId, out snapshot!);
    }

    public NCHumanActionRouterCounters GetCounters()
    {
        var (p50, p95, p99) = GetDurationPercentiles();
        return new NCHumanActionRouterCounters(
            _active,
            _running.Count,
            _started,
            _completed,
            _failed,
            _interrupted,
            _timedOut,
            _unhandled,
            new NCHumanActionFailureCounters(
                FailureCount(NCHumanActionFailureKind.Configuration),
                FailureCount(NCHumanActionFailureKind.RuntimeUnavailable),
                FailureCount(NCHumanActionFailureKind.InvalidRequest),
                FailureCount(NCHumanActionFailureKind.CapabilityUnavailable),
                FailureCount(NCHumanActionFailureKind.Blocked),
                FailureCount(NCHumanActionFailureKind.Unreachable),
                FailureCount(NCHumanActionFailureKind.Busy),
                FailureCount(NCHumanActionFailureKind.TimedOut),
                FailureCount(NCHumanActionFailureKind.ExecutorUnhandled),
                FailureCount(NCHumanActionFailureKind.DomainRejected),
                FailureCount(NCHumanActionFailureKind.Unknown)),
            p50,
            p95,
            p99,
            _configurationError);
    }

    public bool Interrupt(NCActorId actorId, NCHumanActionInterruptKind reason)
    {
        if (!_running.TryGetValue(actorId, out var current))
            return false;
        Finish(current, NCHumanActionRuntimeState.Interrupted, $"action-interrupted:{reason}", false, reason);
        return true;
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        ClearState();
        _configurationError = ValidateCatalog();
        _active = _configurationError == null;
    }

    private void OnExecuteStep(NCNeuralActionStepEvent args)
    {
        args.Handled = true;
        if (!_active || !_clock.TryGetCurrentTime(out _, out var now))
        {
            Reject(args, _configurationError ?? "human-action-clock-unavailable");
            return;
        }
        if (!_prototypes.TryIndex(new ProtoId<NCHumanActionPrototype>(args.Step.ActionId),
                out NCHumanActionPrototype? action) || action == null)
        {
            Reject(args, $"human-action-not-registered:{args.Step.ActionId}");
            return;
        }

        var entity = args.EntityUid is { } entityUid && Exists(entityUid) ? entityUid : (EntityUid?) null;
        if (action.RequiresPhysicalEntity && entity == null)
        {
            Reject(args, "human-action-physical-entity-required");
            return;
        }
        var target = args.Step.Target.IsValid ? args.Step.Target : args.Intention.Target;
        if (!ValidTarget(action.TargetKind, target))
        {
            Reject(args, $"human-action-target-invalid:{action.TargetKind}");
            return;
        }

        var instanceId = NCPersistentIdentity.NeuralCommandAction(args.BatchId, args.Step.StepId);
        NCHumanActionRuntimeSnapshot? current = null;
        if (_running.TryGetValue(args.ActorId, out current) && current.InstanceId != instanceId)
        {
            Interrupt(args.ActorId, NCHumanActionInterruptKind.PlanChanged);
            current = null;
        }
        if (current == null)
        {
            current = Start(args, action, target, entity, instanceId, now);
        }

        if (now >= current.TimeoutAt)
        {
            _timedOut++;
            args.Failure = "human-action-timeout";
            Finish(current, NCHumanActionRuntimeState.Failed, args.Failure, true,
                NCHumanActionInterruptKind.TimedOut);
            return;
        }

        current = Transition(current, NCHumanActionRuntimeState.Running, now, "executor-pulse", action.Executor.ToString());
        _running[args.ActorId] = current;
        Mirror(current);

        var execution = new NCHumanActionExecutionEvent(
            current.InstanceId,
            args.ActorId,
            entity,
            args.Intention.IntentionId,
            args.BatchId,
            args.Intention,
            args.Step,
            target,
            action,
            args.Command,
            current.StartedAt,
            now);
        RaiseLocalEvent(execution);
        if (!execution.Handled)
        {
            _unhandled++;
            args.Failure = $"human-action-executor-unhandled:{action.Executor}:{action.ID}";
            Finish(current, NCHumanActionRuntimeState.Failed, args.Failure, true,
                NCHumanActionInterruptKind.ExecutorFailed);
            return;
        }
        if (!string.IsNullOrWhiteSpace(execution.Failure))
        {
            args.Failure = execution.Failure;
            Finish(current, NCHumanActionRuntimeState.Failed, args.Failure, true,
                NCHumanActionInterruptKind.ExecutorFailed);
            return;
        }
        if (execution.Completed)
        {
            args.Completed = true;
            Finish(current, NCHumanActionRuntimeState.Completed, null, false, null);
            return;
        }

        _running[args.ActorId] = Transition(current, NCHumanActionRuntimeState.Running, now,
            "executor-waiting", action.Completion.ToString());
        Mirror(_running[args.ActorId]);
    }

    private NCHumanActionRuntimeSnapshot Start(
        NCNeuralActionStepEvent args,
        NCHumanActionPrototype action,
        NCCognitionSubjectRef target,
        EntityUid? entity,
        Guid instanceId,
        NCActiveWorldTime now)
    {
        _started++;
        var runtime = new NCHumanActionRuntimeSnapshot(
            instanceId,
            args.ActorId,
            entity,
            args.Intention.IntentionId,
            args.BatchId,
            args.Step.StepId,
            action.ID,
            args.Command.CapabilityId,
            args.Command.OriginalIndex,
            action.Executor,
            target,
            NCHumanActionRuntimeState.Pending,
            now,
            now,
            now.Advance(action.Timeout),
            NCHumanActionFailureKind.None,
            null,
            []);
        runtime = Transition(runtime, NCHumanActionRuntimeState.Pending, now, "action-started", action.ID);
        _running[args.ActorId] = runtime;
        Mirror(runtime);
        return runtime;
    }

    private void Finish(
        NCHumanActionRuntimeSnapshot current,
        NCHumanActionRuntimeState state,
        string? failure,
        bool failed,
        NCHumanActionInterruptKind? cleanupReason)
    {
        if (cleanupReason is { } reason)
        {
            RaiseLocalEvent(new NCHumanActionInterruptedEvent(
                current.InstanceId,
                current.ActorId,
                current.EntityUid,
                current.ActionId,
                reason));
        }
        if (!_clock.TryGetCurrentTime(out _, out var now))
            now = current.UpdatedAt;
        var failureKind = failed ? ClassifyFailure(failure) : NCHumanActionFailureKind.None;
        var terminal = Transition(current with { FailureKind = failureKind, Failure = failure }, state, now,
            state == NCHumanActionRuntimeState.Completed ? "action-completed" : "action-ended",
            failure ?? state.ToString());
        RecordDuration(current.StartedAt, now);
        _running.Remove(current.ActorId);
        Retain(terminal);
        if (state == NCHumanActionRuntimeState.Completed)
            _completed++;
        else if (state == NCHumanActionRuntimeState.Interrupted)
            _interrupted++;
        else if (failed)
        {
            _failed++;
            RecordFailure(failureKind);
        }
        if (current.EntityUid is { } entity && Exists(entity) &&
            TryComp<NCHumanActionRuntimeComponent>(entity, out var component) &&
            component.InstanceId == current.InstanceId)
        {
            RemCompDeferred<NCHumanActionRuntimeComponent>(entity);
        }
    }

    private void Retain(NCHumanActionRuntimeSnapshot result)
    {
        _lastResults[result.ActorId] = result;
        if (_lastResults.Count <= MaximumRetainedResults)
            return;
        _lastResults.Remove(_lastResults.Keys.First());
    }

    private void Mirror(NCHumanActionRuntimeSnapshot runtime)
    {
        if (runtime.EntityUid is not { } entity || !Exists(entity))
            return;
        var component = EnsureComp<NCHumanActionRuntimeComponent>(entity);
        component.InstanceId = runtime.InstanceId;
        component.ActorId = runtime.ActorId;
        component.IntentionId = runtime.IntentionId;
        component.PlanId = runtime.PlanId;
        component.StepId = runtime.StepId;
        component.ActionId = runtime.ActionId;
        component.Executor = runtime.Executor;
        component.Target = runtime.Target;
        component.State = runtime.State;
        component.StartedAt = runtime.StartedAt;
        component.UpdatedAt = runtime.UpdatedAt;
        component.TimeoutAt = runtime.TimeoutAt;
        component.Failure = runtime.Failure;
        component.Trace.Clear();
        component.Trace.AddRange(runtime.Trace);
    }

    private NCHumanActionRuntimeSnapshot Transition(
        NCHumanActionRuntimeSnapshot runtime,
        NCHumanActionRuntimeState state,
        NCActiveWorldTime now,
        string actionEvent,
        string detail)
    {
        var trace = runtime.Trace.Length >= MaximumTraceEntries
            ? runtime.Trace[1..]
            : runtime.Trace;
        return runtime with
        {
            State = state,
            UpdatedAt = now,
            Trace = [.. trace, new NCHumanActionTraceEntry(++_nextTraceSequence, now, state, actionEvent, detail)],
        };
    }

    private void Reject(NCNeuralActionStepEvent args, string failure)
    {
        args.Failure = failure;
        _failed++;
        RecordFailure(ClassifyFailure(failure));
    }

    private void RecordFailure(NCHumanActionFailureKind kind)
    {
        if (kind != NCHumanActionFailureKind.None)
            _failureReasons[(int) kind]++;
    }

    private long FailureCount(NCHumanActionFailureKind kind) => _failureReasons[(int) kind];

    private void RecordDuration(NCActiveWorldTime startedAt, NCActiveWorldTime endedAt)
    {
        if (!startedAt.IsValid || !endedAt.IsValid || endedAt < startedAt)
            return;

        _durationSamples[_durationSampleWriteIndex] =
            Math.Max(0f, (float) (endedAt.Elapsed - startedAt.Elapsed).TotalMilliseconds);
        _durationSampleWriteIndex = (_durationSampleWriteIndex + 1) % _durationSamples.Length;
        _durationSampleCount = Math.Min(_durationSamples.Length, _durationSampleCount + 1);
    }

    private (float P50, float P95, float P99) GetDurationPercentiles()
    {
        if (_durationSampleCount == 0)
            return (0f, 0f, 0f);

        var samples = new float[_durationSampleCount];
        Array.Copy(_durationSamples, samples, samples.Length);
        Array.Sort(samples);
        return (
            Percentile(samples, 0.50f),
            Percentile(samples, 0.95f),
            Percentile(samples, 0.99f));
    }

    private static float Percentile(float[] sorted, float percentile)
    {
        var index = Math.Clamp((int) Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static NCHumanActionFailureKind ClassifyFailure(string? failure)
    {
        if (string.IsNullOrWhiteSpace(failure))
            return NCHumanActionFailureKind.Unknown;

        if (failure.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            failure.Contains("timed-out", StringComparison.OrdinalIgnoreCase))
            return NCHumanActionFailureKind.TimedOut;
        if (failure.Contains("unhandled", StringComparison.OrdinalIgnoreCase))
            return NCHumanActionFailureKind.ExecutorUnhandled;
        if (failure.Contains("configuration", StringComparison.OrdinalIgnoreCase) ||
            failure.Contains("catalog", StringComparison.OrdinalIgnoreCase) ||
            failure.Contains("not-registered", StringComparison.OrdinalIgnoreCase) ||
            failure.Contains("policy-invalid", StringComparison.OrdinalIgnoreCase))
            return NCHumanActionFailureKind.Configuration;
        if (failure.Contains("runtime-unavailable", StringComparison.OrdinalIgnoreCase) ||
            failure.Contains("clock-unavailable", StringComparison.OrdinalIgnoreCase) ||
            failure.Contains("physical-entity-required", StringComparison.OrdinalIgnoreCase))
            return NCHumanActionFailureKind.RuntimeUnavailable;
        if (failure.Contains("target-invalid", StringComparison.OrdinalIgnoreCase) ||
            failure.Contains("invalid-request", StringComparison.OrdinalIgnoreCase))
            return NCHumanActionFailureKind.InvalidRequest;
        if (failure.Contains("unreachable", StringComparison.OrdinalIgnoreCase) ||
            failure.Contains("no-path", StringComparison.OrdinalIgnoreCase))
            return NCHumanActionFailureKind.Unreachable;
        if (failure.Contains("busy", StringComparison.OrdinalIgnoreCase) ||
            failure.Contains("owned", StringComparison.OrdinalIgnoreCase))
            return NCHumanActionFailureKind.Busy;
        if (failure.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
            failure.Contains("cooldown", StringComparison.OrdinalIgnoreCase))
            return NCHumanActionFailureKind.Blocked;
        if (failure.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
            failure.Contains("missing", StringComparison.OrdinalIgnoreCase) ||
            failure.Contains("not-found", StringComparison.OrdinalIgnoreCase) ||
            failure.Contains("not-materialized", StringComparison.OrdinalIgnoreCase))
            return NCHumanActionFailureKind.CapabilityUnavailable;
        if (failure.Contains("rejected", StringComparison.OrdinalIgnoreCase) ||
            failure.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
            failure.Contains("forbidden", StringComparison.OrdinalIgnoreCase))
            return NCHumanActionFailureKind.DomainRejected;
        return NCHumanActionFailureKind.Unknown;
    }

    private void OnBindingChanged(NCCityActorBindingChangedEvent args)
    {
        if (!args.Bound)
            Interrupt(args.ActorId, NCHumanActionInterruptKind.EntityUnbound);
    }

    private void OnRuntimeShutdown(Entity<NCHumanActionRuntimeComponent> entity, ref ComponentShutdown args)
    {
        if (_running.TryGetValue(entity.Comp.ActorId, out var current) && current.EntityUid == entity.Owner)
            Interrupt(entity.Comp.ActorId, NCHumanActionInterruptKind.EntityUnbound);
    }

    private string? ValidateCatalog()
    {
        var actions = _prototypes.EnumeratePrototypes<NCHumanActionPrototype>().ToArray();
        if (actions.Length == 0)
            return "human-action-catalog-empty";
        foreach (var action in actions)
        {
            if (string.IsNullOrWhiteSpace(action.ID) || action.Timeout <= TimeSpan.Zero ||
                action.Timeout > TimeSpan.FromHours(24) || !float.IsFinite(action.MaximumRange) ||
                !float.IsFinite(action.CompletionRange) || !float.IsFinite(action.SteeringRetargetDistance) ||
                action.MaximumRange < 0f || action.CompletionRange < 0f ||
                action.SteeringRefreshInterval < TimeSpan.Zero || action.SteeringRetargetDistance < 0f ||
                action.Operation == NCHumanActionOperationKind.FleeFromTarget &&
                (action.SteeringRefreshInterval <= TimeSpan.Zero || action.SteeringRetargetDistance <= 0f) ||
                action.SpeechLocId.Length > 128 ||
                action.MaximumRange > 0f && action.CompletionRange > action.MaximumRange ||
                !Enum.IsDefined(action.Executor) || !Enum.IsDefined(action.TargetKind) ||
                !Enum.IsDefined(action.Completion) || !Enum.IsDefined(action.Operation) ||
                action.Operation == NCHumanActionOperationKind.Speak &&
                string.IsNullOrWhiteSpace(action.SpeechLocId) ||
                action.Interrupts.Any(value => !Enum.IsDefined(value)))
            {
                return $"human-action-invalid:{action.ID}";
            }
        }
        return null;
    }

    private static bool ValidTarget(NCHumanActionTargetKind expected, NCCognitionSubjectRef target) => expected switch
    {
        NCHumanActionTargetKind.None => true,
        NCHumanActionTargetKind.Person => target.IsValid && target.Kind is
            NCCognitionReferenceKind.Actor or NCCognitionReferenceKind.Party or NCCognitionReferenceKind.StableKey,
        NCHumanActionTargetKind.Zone => target.IsValid && target.Kind == NCCognitionReferenceKind.Zone,
        NCHumanActionTargetKind.Entity or NCHumanActionTargetKind.Item =>
            target.IsValid && target.Kind == NCCognitionReferenceKind.StableKey,
        NCHumanActionTargetKind.DomainRecord => target.IsValid,
        _ => false,
    };

    private void EndRound()
    {
        foreach (var actorId in _running.Keys.ToArray())
            Interrupt(actorId, NCHumanActionInterruptKind.RoundEnded);
        _active = false;
        _running.Clear();
    }

    private void ClearState()
    {
        _running.Clear();
        _lastResults.Clear();
        _nextTraceSequence = 0;
        _started = 0;
        _completed = 0;
        _failed = 0;
        _interrupted = 0;
        _timedOut = 0;
        _unhandled = 0;
        _durationSampleCount = 0;
        _durationSampleWriteIndex = 0;
        _configurationError = null;
        Array.Clear(_failureReasons);
        Array.Clear(_durationSamples);
    }
}
