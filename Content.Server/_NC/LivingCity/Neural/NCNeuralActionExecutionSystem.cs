// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Neural;

/// <summary>Executes validated GPT choices only through the existing typed human-action authority.</summary>
public sealed partial class NCNeuralActionExecutionSystem : EntitySystem
{
    private static readonly ProtoId<NCPhysicalActorBrainPolicyPrototype> DefaultPolicy =
        new("NCDefaultPhysicalActorBrain");
    private const int MaximumCommandsPerDecision = 8;
    private const int MaximumActionStepsPerDecision = 32;

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCHumanActionRouterSystem _router = default!;

    private readonly Dictionary<NCActorId, ExecutionState> _executions = [];
    private readonly PriorityQueue<ScheduledPulse, (long DueTicks, long Sequence)> _queue = new();
    private NCPhysicalActorBrainPolicyPrototype? _policy;
    private NCSessionEpoch _epoch;
    private long _sequence;
    private bool _active;

    public int RunningCount => _executions.Count;
    public long AcceptedCount { get; private set; }
    public long RejectedCount { get; private set; }
    public long CompletedCount { get; private set; }
    public long FailedCount { get; private set; }

    public bool TryGetSnapshot(NCActorId actorId, out NCNeuralActionExecutionSnapshot snapshot)
    {
        if (!_executions.TryGetValue(actorId, out var state))
        {
            snapshot = default!;
            return false;
        }

        snapshot = new NCNeuralActionExecutionSnapshot(
            state.PlanId,
            state.Intention.CapabilityId,
            state.CurrentStep,
            state.Steps.Length,
            state.Intention.ExpiresAt);
        return true;
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Reset());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Reset());
        SubscribeLocalEvent<NCCityActorBindingChangedEvent>(OnBindingChanged);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_active || _policy == null || !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
            return;

        var handled = 0;
        while (handled < _policy.MaximumExecutionPulsesPerTick &&
               _queue.TryPeek(out var pulse, out var priority) && priority.DueTicks <= now.Ticks)
        {
            _queue.Dequeue();
            if (!_executions.TryGetValue(pulse.ActorId, out var state) || state.Sequence != pulse.Sequence)
                continue;
            Pulse(state, now);
            handled++;
        }
    }

    public bool TryAccept(
        NCNeuralRequestEnvelope request,
        NCNeuralResponseEnvelope response,
        NCActiveWorldTime now,
        out string? error)
    {
        error = null;
        if (!_active || response.Commands.Count is < 1 or > MaximumCommandsPerDecision)
        {
            error = "neural-command-capability-invalid";
            RejectedCount++;
            return false;
        }

        var planSteps = new List<NCCognitionPlanStep>();
        NCCognitionCapabilityPrototype? primaryCapability = null;
        NCCognitionSubjectRef primaryTarget = default;
        for (var commandIndex = 0; commandIndex < response.Commands.Count; commandIndex++)
        {
            var command = response.Commands[commandIndex];
            if (!_prototypes.TryIndex(new ProtoId<NCCognitionCapabilityPrototype>(command.CapabilityId),
                    out var capability) || capability == null)
            {
                error = "neural-command-capability-invalid";
                RejectedCount++;
                return false;
            }
            primaryCapability ??= capability;
            var target = string.IsNullOrWhiteSpace(command.TargetHandle)
                ? default
                : new NCCognitionSubjectRef(NCCognitionReferenceKind.StableKey, command.TargetHandle);
            if (commandIndex == 0)
                primaryTarget = target;
            var authoredSteps = capability.ProcedureSteps;
            if (authoredSteps.Length == 0 && capability.StrategicOptions.Length > 0)
            {
                authoredSteps = capability.StrategicOptions
                    .FirstOrDefault(value => value.Id == command.GoalKey)?.ProcedureSteps ??
                    capability.StrategicOptions[0].ProcedureSteps;
            }
            if (authoredSteps.Length == 0)
                continue;
            var stepPrefix = $"command-{commandIndex}";
            var previous = planSteps.LastOrDefault()?.StepId;
            foreach (var authored in authoredSteps)
            {
                if (planSteps.Count >= MaximumActionStepsPerDecision)
                    break;
                string[] dependencies = previous == null ? [] : [previous];
                var stepId = $"{stepPrefix}-{authored.Id}";
                planSteps.Add(new NCCognitionPlanStep(stepId, authored.Action, target, dependencies, authored.Optional));
                previous = stepId;
            }
        }
        if (primaryCapability == null || planSteps.Count == 0)
        {
            error = "neural-command-procedure-empty";
            RejectedCount++;
            return false;
        }
        var primaryCommand = response.Commands[0];
        var intention = new NCCognitionIntentionSnapshot(
            request.RequestId,
            primaryCapability.ID,
            primaryTarget,
            primaryCapability.TaskKind,
            response.Confidence / 10f,
            1f,
            primaryCommand.Priority,
            now,
            now,
            now.Advance(_policy!.ActionPlanLifetime),
            request.ContextVersion);
        var state = new ExecutionState(request.ActorId, request.RequestId, intention, planSteps.ToArray(), 0,
            ++_sequence);
        // A GPT replan owns the actor's next action sequence, so cancel any adapter still executing the old plan.
        if (_executions.ContainsKey(request.ActorId))
            _router.Interrupt(request.ActorId, NCHumanActionInterruptKind.PlanChanged);
        _executions[request.ActorId] = state;
        Enqueue(state, now);
        AcceptedCount++;
        return true;
    }

    private void Pulse(ExecutionState state, NCActiveWorldTime now)
    {
        if (state.Intention.ExpiresAt <= now ||
            !_runtime.TryGetEntity(state.ActorId, out var entity) || !Exists(entity))
        {
            Fail(state.ActorId);
            return;
        }
        if (state.CurrentStep >= state.Steps.Length)
        {
            _executions.Remove(state.ActorId);
            CompletedCount++;
            return;
        }

        var step = state.Steps[state.CurrentStep];
        var execution = new NCNeuralActionStepEvent(
            state.ActorId,
            entity,
            state.PlanId,
            state.Intention,
            step);
        RaiseLocalEvent(execution);
        if (!execution.Handled || !string.IsNullOrWhiteSpace(execution.Failure))
        {
            if (!step.Optional)
            {
                Fail(state.ActorId);
                return;
            }
            execution.Completed = true;
        }

        if (execution.Completed)
            state = state with { CurrentStep = state.CurrentStep + 1, Sequence = ++_sequence };
        else
            state = state with { Sequence = ++_sequence };
        _executions[state.ActorId] = state;
        Enqueue(state, now.Advance(_policy!.ExecutionPulseInterval));
    }

    private void Enqueue(ExecutionState state, NCActiveWorldTime due) =>
        _queue.Enqueue(new ScheduledPulse(state.ActorId, state.Sequence), (due.Ticks, state.Sequence));

    private void Fail(NCActorId actorId)
    {
        _executions.Remove(actorId);
        FailedCount++;
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        Reset();
        if (!_prototypes.TryIndex(DefaultPolicy, out NCPhysicalActorBrainPolicyPrototype? policy) ||
            policy == null || policy.MaximumExecutionPulsesPerTick is < 1 or > 512 ||
            policy.ExecutionPulseInterval <= TimeSpan.Zero || policy.ActionPlanLifetime <= TimeSpan.Zero)
        {
            Log.Error($"Neural action execution policy {DefaultPolicy} is missing or invalid.");
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
            _executions.Remove(args.ActorId);
            _router.Interrupt(args.ActorId, NCHumanActionInterruptKind.EntityUnbound);
        }
    }

    private void Reset()
    {
        _active = false;
        _policy = null;
        _epoch = default;
        _sequence = 0;
        _executions.Clear();
        _queue.Clear();
        AcceptedCount = 0;
        RejectedCount = 0;
        CompletedCount = 0;
        FailedCount = 0;
    }

    private sealed record ExecutionState(
        NCActorId ActorId,
        Guid PlanId,
        NCCognitionIntentionSnapshot Intention,
        NCCognitionPlanStep[] Steps,
        int CurrentStep,
        long Sequence);

    private readonly record struct ScheduledPulse(NCActorId ActorId, long Sequence);
}

public sealed record NCNeuralActionExecutionSnapshot(
    Guid PlanId,
    string CapabilityId,
    int CurrentStep,
    int StepCount,
    NCActiveWorldTime ExpiresAt);
