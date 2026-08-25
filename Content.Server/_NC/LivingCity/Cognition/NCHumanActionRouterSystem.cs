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
/// Single authoritative boundary between plans and existing gameplay executors. It owns lifecycle, timeout and
/// cleanup, but delegates every world mutation to a typed ECS subscriber.
/// </summary>
public sealed partial class NCHumanActionRouterSystem : EntitySystem
{
    private const int MaximumTraceEntries = 16;
    private const int MaximumRetainedResults = 20_000;

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;

    private readonly Dictionary<NCActorId, NCHumanActionRuntimeSnapshot> _running = [];
    private readonly Dictionary<NCActorId, NCHumanActionRuntimeSnapshot> _lastResults = [];
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
        SubscribeLocalEvent<NCCognitionExecuteStepEvent>(OnExecuteStep);
        SubscribeLocalEvent<NCCognitionDecisionAppliedEvent>(OnDecisionApplied);
        SubscribeLocalEvent<NCCityActorMaterializationChangedEvent>(OnMaterializationChanged);
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

    public NCHumanActionRouterCounters GetCounters() => new(
        _active,
        _running.Count,
        _started,
        _completed,
        _failed,
        _interrupted,
        _timedOut,
        _unhandled,
        _configurationError);

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

    private void OnExecuteStep(NCCognitionExecuteStepEvent args)
    {
        args.Handled = true;
        if (!_active || !_clock.TryGetCurrentTime(out _, out var now))
        {
            args.Failure = _configurationError ?? "human-action-clock-unavailable";
            return;
        }
        if (!_prototypes.TryIndex(new ProtoId<NCHumanActionPrototype>(args.Step.ActionId),
                out NCHumanActionPrototype? action) || action == null)
        {
            args.Failure = $"human-action-not-registered:{args.Step.ActionId}";
            return;
        }

        var entity = args.EntityUid is { } entityUid && Exists(entityUid) ? entityUid : (EntityUid?) null;
        if (action.RequiresPhysicalEntity && entity == null)
        {
            args.Failure = "human-action-physical-entity-required";
            return;
        }
        var target = args.Step.Target.IsValid ? args.Step.Target : args.Intention.Target;
        if (!ValidTarget(action.TargetKind, target))
        {
            args.Failure = $"human-action-target-invalid:{action.TargetKind}";
            return;
        }

        var instanceId = NCPersistentIdentity.CognitionAction(args.PlanId, args.Step.StepId);
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
            args.PlanId,
            args.Intention,
            args.Step,
            target,
            action,
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
        NCCognitionExecuteStepEvent args,
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
            args.PlanId,
            args.Step.StepId,
            action.ID,
            action.Executor,
            target,
            NCHumanActionRuntimeState.Pending,
            now,
            now,
            now.Advance(action.Timeout),
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
        var terminal = Transition(current with { Failure = failure }, state, now,
            state == NCHumanActionRuntimeState.Completed ? "action-completed" : "action-ended",
            failure ?? state.ToString());
        _running.Remove(current.ActorId);
        Retain(terminal);
        if (state == NCHumanActionRuntimeState.Completed)
            _completed++;
        else if (state == NCHumanActionRuntimeState.Interrupted)
            _interrupted++;
        else if (failed)
            _failed++;
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

    private void OnDecisionApplied(NCCognitionDecisionAppliedEvent args)
    {
        if (_running.TryGetValue(args.ActorId, out var current) &&
            (args.Intention == null || args.Intention.IntentionId != current.IntentionId))
        {
            // A differing action will also be caught by its plan ID on the next execution pulse. This early release
            // prevents steering and DoAfter ownership from surviving a cancelled intention.
            Interrupt(args.ActorId, NCHumanActionInterruptKind.PlanChanged);
        }
    }

    private void OnMaterializationChanged(NCCityActorMaterializationChangedEvent args)
    {
        if (!args.Materialized)
            Interrupt(args.ActorId, NCHumanActionInterruptKind.Dematerialized);
    }

    private void OnRuntimeShutdown(Entity<NCHumanActionRuntimeComponent> entity, ref ComponentShutdown args)
    {
        if (_running.TryGetValue(entity.Comp.ActorId, out var current) && current.EntityUid == entity.Owner)
            Interrupt(entity.Comp.ActorId, NCHumanActionInterruptKind.Dematerialized);
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
                !float.IsFinite(action.CompletionRange) || action.MaximumRange < 0f ||
                action.CompletionRange < 0f || action.SpeechLocId.Length > 128 ||
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
        _configurationError = null;
    }
}
