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
    [Dependency] private NCHumanVanillaActionAdapterSystem _vanilla = default!;

    private readonly Dictionary<NCActorId, ExecutionState> _executions = [];
    private readonly Dictionary<NCActorId, NCNeuralActionFailureSnapshot> _lastFailures = [];
    private readonly Dictionary<NCActorId, Queue<NCNeuralCommandResult>> _recentResults = [];
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
    public long InterruptedCount { get; private set; }

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
            state.Intention.ExpiresAt)
        {
            IntentKey = state.IntentKey,
            TargetHandle = state.IntentTargetHandle,
        };
        return true;
    }

    public bool TryGetLastFailure(NCActorId actorId, out NCNeuralActionFailureSnapshot failure) =>
        _lastFailures.TryGetValue(actorId, out failure!);

    public NCNeuralCommandResult[] GetRecentCommandResults(NCActorId actorId, int maximum = 8)
    {
        if (maximum <= 0 || !_recentResults.TryGetValue(actorId, out var results))
            return [];
        return results.TakeLast(Math.Min(maximum, 8)).ToArray();
    }

    /// <summary>Stops the current neural plan and its real vanilla action without recording a synthetic failure.</summary>
    public bool TryInterrupt(NCActorId actorId, NCHumanActionInterruptKind reason)
    {
        var removed = _executions.Remove(actorId, out var state);
        _router.Interrupt(actorId, reason);
        if (removed && state != null)
        {
            InterruptedCount++;
            var now = _clock.TryGetCurrentTime(out _, out var current)
                ? current
                : state.Intention.CreatedAt;
            RecordTerminatedBatchResults(
                state,
                "interrupted",
                "interrupted",
                $"action-interrupted:{reason}",
                "batch-predecessor-interrupted",
                now);
        }
        return removed;
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
        if (!_active || response.Commands is not { Count: >= 1 and <= MaximumCommandsPerDecision } commands)
        {
            error = "neural-command-capability-invalid";
            RejectedCount++;
            return false;
        }

        // Resolve the complete batch before creating any runtime state. Empty or oversized procedures reject the
        // whole provider choice; silently skipping or truncating commands would make result correlation dishonest.
        var resolved = new List<ResolvedCommand>(commands.Count);
        foreach (var command in commands)
        {
            if (!request.AllowedCapabilityIds.Contains(command.CapabilityId) ||
                !_prototypes.TryIndex(new ProtoId<NCCognitionCapabilityPrototype>(command.CapabilityId),
                    out var capability) || capability == null || !ValidGoal(capability, command.GoalKey))
            {
                error = "neural-command-capability-invalid";
                RejectedCount++;
                return false;
            }

            var target = ResolveTarget(request, command.TargetHandle);
            var authoredSteps = capability.ProcedureSteps;
            if (authoredSteps.Length == 0 && capability.StrategicOptions.Length > 0)
            {
                authoredSteps = capability.StrategicOptions
                    .First(value => value.Id == command.GoalKey).ProcedureSteps;
            }

            resolved.Add(new ResolvedCommand(command, command.OriginalIndex, capability, target, authoredSteps));
        }

        if (!TryValidateProcedureBatch(
                resolved.Select(value => (value.CommandIndex, value.ProcedureSteps.Length)).ToArray(),
                out error))
        {
            RejectedCount++;
            return false;
        }

        var planSteps = new List<ExecutionStep>(resolved.Sum(value => value.ProcedureSteps.Length));
        foreach (var selected in resolved)
        {
            var stepPrefix = $"command-{selected.CommandIndex}";
            var previous = planSteps.Count > 0 ? planSteps[^1].Step.StepId : null;
            foreach (var authored in selected.ProcedureSteps)
            {
                string[] dependencies = previous == null ? [] : [previous];
                var stepId = $"{stepPrefix}-{authored.Id}";
                planSteps.Add(new ExecutionStep(
                    new NCCognitionPlanStep(
                        stepId,
                        authored.Action,
                        selected.Target,
                        dependencies,
                        authored.Optional),
                    selected.CommandIndex,
                    selected.Command));
                previous = stepId;
            }
        }

        var primary = resolved[0];
        var primaryCommand = primary.Command;
        var intention = new NCCognitionIntentionSnapshot(
            request.RequestId,
            primary.Capability.ID,
            primary.Target,
            primary.Capability.TaskKind,
            response.Confidence / 10f,
            1f,
            primaryCommand.Priority,
            now,
            now,
            now.Advance(_policy!.ActionPlanLifetime),
            request.ContextVersion);
        var semanticIntent = response.Intent?.Key ?? primaryCommand.GoalKey;
        var semanticTarget = response.Intent?.TargetHandle ?? primaryCommand.TargetHandle;
        var state = new ExecutionState(request.ActorId, request.RequestId, intention, semanticIntent,
            semanticTarget, planSteps.ToArray(), 0, ++_sequence, request.Priority);
        // A GPT replan owns the actor's next action sequence, so cancel any adapter still executing the old plan.
        _vanilla.TryEndDefensiveReflex(request.ActorId);
        if (_executions.ContainsKey(request.ActorId))
            TryInterrupt(request.ActorId, NCHumanActionInterruptKind.PlanChanged);
        _executions[request.ActorId] = state;
        Enqueue(state, now);
        AcceptedCount++;
        return true;
    }

    /// <summary>Converts only handles already carried by this validated request into typed cognition references.</summary>
    private static NCCognitionSubjectRef ResolveTarget(NCNeuralRequestEnvelope request, string handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
            return default;
        if ((request.KnownLocations ?? []).Any(location => location.Handle == handle) &&
            handle.StartsWith("zone:", StringComparison.Ordinal) &&
            Guid.TryParse(handle["zone:".Length..], out var zoneId))
        {
            return new NCCognitionSubjectRef(NCCognitionReferenceKind.Zone, zoneId.ToString("D"));
        }
        return new NCCognitionSubjectRef(NCCognitionReferenceKind.StableKey, handle);
    }

    private static bool ValidGoal(NCCognitionCapabilityPrototype capability, string goalKey) =>
        capability.StrategicOptions.Length == 0
            ? goalKey == "execute"
            : capability.StrategicOptions.Any(option => option.Id == goalKey);

    internal static bool TryValidateProcedureBatch(
        IReadOnlyList<(int CommandIndex, int StepCount)> procedures,
        out string? error)
    {
        error = null;
        if (procedures.Count is < 1 or > MaximumCommandsPerDecision)
        {
            error = "neural-command-capability-invalid";
            return false;
        }

        var seen = new HashSet<int>();
        var stepCount = 0;
        foreach (var procedure in procedures)
        {
            if (procedure.CommandIndex is < 0 or >= MaximumCommandsPerDecision ||
                !seen.Add(procedure.CommandIndex))
            {
                error = "neural-command-correlation-invalid";
                return false;
            }

            if (procedure.StepCount <= 0)
            {
                error = "neural-command-procedure-empty";
                return false;
            }

            if (procedure.StepCount > MaximumActionStepsPerDecision - stepCount)
            {
                error = "neural-command-procedure-too-large";
                return false;
            }

            stepCount += procedure.StepCount;
        }

        return true;
    }

    private void Pulse(ExecutionState state, NCActiveWorldTime now)
    {
        if (state.Intention.ExpiresAt <= now)
        {
            Fail(state, "neural-plan-expired", now);
            return;
        }
        if (!_runtime.TryGetEntity(state.ActorId, out var entity) || !Exists(entity))
        {
            Fail(state, "neural-actor-entity-unavailable", now);
            return;
        }
        if (state.CurrentStep >= state.Steps.Length)
        {
            _executions.Remove(state.ActorId);
            CompletedCount++;
            RaiseLocalEvent(new NCNeuralActionExecutionCompletedEvent(
                state.ActorId,
                state.PlanId,
                state.Priority,
                state.IntentKey,
                state.IntentTargetHandle,
                now));
            return;
        }

        var executionStep = state.Steps[state.CurrentStep];
        var step = executionStep.Step;
        var command = executionStep.Command;
        var execution = new NCNeuralActionStepEvent(
            state.ActorId,
            entity,
            state.PlanId,
            state.Intention,
            step,
            command);
        RaiseLocalEvent(execution);
        if (!execution.Handled || !string.IsNullOrWhiteSpace(execution.Failure))
        {
            if (!step.Optional)
            {
                var failure = string.IsNullOrWhiteSpace(execution.Failure)
                    ? $"neural-action-unhandled:{step.ActionId}"
                    : execution.Failure;
                Fail(state, failure, now);
                return;
            }
            execution.Completed = true;
        }

        if (execution.Completed)
        {
            var nextStep = state.CurrentStep + 1;
            if (nextStep >= state.Steps.Length ||
                state.Steps[nextStep].CommandIndex != executionStep.CommandIndex)
            {
                RecordCommandResult(
                    state.ActorId,
                    state.PlanId,
                    command,
                    executionStep.CommandIndex,
                    step.ActionId,
                    SuccessfulCommandState(step.ActionId),
                    "none",
                    string.Empty,
                    now);
            }
            state = state with { CurrentStep = state.CurrentStep + 1, Sequence = ++_sequence };
        }
        else
            state = state with { Sequence = ++_sequence };
        _executions[state.ActorId] = state;
        Enqueue(state, now.Advance(_policy!.ExecutionPulseInterval));
    }

    private void Enqueue(ExecutionState state, NCActiveWorldTime due) =>
        _queue.Enqueue(new ScheduledPulse(state.ActorId, state.Sequence), (due.Ticks, state.Sequence));

    private void Fail(ExecutionState state, string failure, NCActiveWorldTime now)
    {
        _executions.Remove(state.ActorId);
        _router.Interrupt(state.ActorId, NCHumanActionInterruptKind.ExecutorFailed);
        FailedCount++;
        var normalized = NormalizeFailure(failure);
        var snapshot = new NCNeuralActionFailureSnapshot(state.ActorId, state.PlanId, state.Priority, normalized, now);
        _lastFailures[state.ActorId] = snapshot;
        RecordTerminatedBatchResults(
            state,
            "failed",
            "executor",
            normalized,
            "batch-predecessor-failed",
            now);
        RaiseLocalEvent(new NCNeuralActionExecutionFailedEvent(
            state.ActorId,
            state.PlanId,
            state.Priority,
            normalized,
            now));
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
            TryInterrupt(args.ActorId, NCHumanActionInterruptKind.EntityUnbound);
        }
    }

    private void Reset()
    {
        _active = false;
        _policy = null;
        _epoch = default;
        _sequence = 0;
        _executions.Clear();
        _lastFailures.Clear();
        _recentResults.Clear();
        _queue.Clear();
        AcceptedCount = 0;
        RejectedCount = 0;
        CompletedCount = 0;
        FailedCount = 0;
        InterruptedCount = 0;
    }

    private static string NormalizeFailure(string? failure)
    {
        if (string.IsNullOrWhiteSpace(failure))
            return "neural-action-failed";
        var normalized = new string(failure.Trim().Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':'
                ? character
                : '-').ToArray());
        return normalized[..Math.Min(normalized.Length, 128)];
    }

    private void RecordTerminatedBatchResults(
        ExecutionState state,
        string currentStatus,
        string currentFailureKind,
        string currentFailure,
        string skippedFailure,
        NCActiveWorldTime now)
    {
        if (state.CurrentStep < 0 || state.CurrentStep >= state.Steps.Length)
            return;

        var commandIndices = state.Steps.Select(value => value.CommandIndex).ToArray();
        foreach (var (stepIndex, status) in BuildTerminalCommandOutcomes(
                     commandIndices,
                     state.CurrentStep,
                     currentStatus))
        {
            var step = state.Steps[stepIndex];
            var current = stepIndex == state.CurrentStep;
            RecordCommandResult(
                state.ActorId,
                state.PlanId,
                step.Command,
                step.CommandIndex,
                step.Step.ActionId,
                status,
                current ? currentFailureKind : "dependency",
                current ? currentFailure : skippedFailure,
                now);
        }
    }

    /// <summary>
    /// Produces exactly one terminal result per command from the interrupted step onward. The active command owns
    /// the real terminal state; every later command was accepted as part of the batch but is explicitly skipped.
    /// </summary>
    internal static (int StepIndex, string State)[] BuildTerminalCommandOutcomes(
        IReadOnlyList<int> commandIndices,
        int currentStep,
        string currentState)
    {
        if (currentStep < 0 || currentStep >= commandIndices.Count)
            return [];

        var outcomes = new List<(int StepIndex, string State)>();
        var seen = new HashSet<int>();
        for (var stepIndex = currentStep; stepIndex < commandIndices.Count; stepIndex++)
        {
            if (!seen.Add(commandIndices[stepIndex]))
                continue;
            outcomes.Add((stepIndex, stepIndex == currentStep ? currentState : "skipped"));
        }
        return outcomes.ToArray();
    }

    private void RecordCommandResult(
        NCActorId actorId,
        Guid requestId,
        NCNeuralCommandProposal command,
        int commandIndex,
        string actionId,
        string status,
        string failureKind,
        string failure,
        NCActiveWorldTime now)
    {
        if (!_recentResults.TryGetValue(actorId, out var results))
        {
            results = new Queue<NCNeuralCommandResult>(8);
            _recentResults.Add(actorId, results);
        }
        while (results.Count >= 8)
            results.Dequeue();
        var result = CreateCommandResult(
            requestId,
            commandIndex,
            command.CapabilityId,
            actionId,
            status,
            failureKind,
            failure,
            command.GoalKey,
            command.ExpectedOutcomeKey,
            now);
        results.Enqueue(result);
        RaiseLocalEvent(new NCNeuralCommandResultEvent(actorId, result));
    }

    private string SuccessfulCommandState(string actionId)
    {
        if (!_prototypes.TryIndex(new ProtoId<NCHumanActionPrototype>(actionId), out var action) || action == null)
            return "completed";

        return SuccessfulCommandState(action.Operation);
    }

    internal static string SuccessfulCommandState(NCHumanActionOperationKind operation) => "completed";

    internal static NCNeuralCommandResult CreateCommandResult(
        Guid requestId,
        int commandIndex,
        string capabilityId,
        string actionId,
        string status,
        string failureKind,
        string failure,
        string goalKey,
        string expectedOutcomeKey,
        NCActiveWorldTime now) =>
        new(
            actionId,
            status,
            failureKind,
            string.IsNullOrWhiteSpace(failure) ? string.Empty : NormalizeFailure(failure),
            now.Ticks)
        {
            RequestId = requestId,
            CapabilityId = capabilityId,
            CommandIndex = commandIndex,
            GoalKey = goalKey,
            ExpectedOutcomeKey = expectedOutcomeKey,
        };

    private sealed record ExecutionState(
        NCActorId ActorId,
        Guid PlanId,
        NCCognitionIntentionSnapshot Intention,
        string IntentKey,
        string IntentTargetHandle,
        ExecutionStep[] Steps,
        int CurrentStep,
        long Sequence,
        NCNeuralPriority Priority);

    private sealed record ResolvedCommand(
        NCNeuralCommandProposal Command,
        int CommandIndex,
        NCCognitionCapabilityPrototype Capability,
        NCCognitionSubjectRef Target,
        NCCognitionProcedureStep[] ProcedureSteps);

    private readonly record struct ExecutionStep(
        NCCognitionPlanStep Step,
        int CommandIndex,
        NCNeuralCommandProposal Command);

    private readonly record struct ScheduledPulse(NCActorId ActorId, long Sequence);
}

public sealed record NCNeuralActionExecutionSnapshot(
    Guid PlanId,
    string CapabilityId,
    int CurrentStep,
    int StepCount,
    NCActiveWorldTime ExpiresAt)
{
    public string IntentKey { get; init; } = string.Empty;
    public string TargetHandle { get; init; } = string.Empty;
}

public sealed record NCNeuralActionFailureSnapshot(
    NCActorId ActorId,
    Guid PlanId,
    NCNeuralPriority Priority,
    string Failure,
    NCActiveWorldTime FailedAt);

/// <summary>
/// One authoritative command acknowledgement or result, correlated by provider request and original batch index.
/// </summary>
public sealed class NCNeuralCommandResultEvent(
    NCActorId actorId,
    NCNeuralCommandResult result) : EntityEventArgs
{
    public NCActorId ActorId { get; } = actorId;
    public NCNeuralCommandResult Result { get; } = result;
}

/// <summary>One authoritative failed physical plan. Consumers may schedule at most one bounded replan.</summary>
public sealed class NCNeuralActionExecutionFailedEvent(
    NCActorId actorId,
    Guid planId,
    NCNeuralPriority priority,
    string failure,
    NCActiveWorldTime failedAt) : EntityEventArgs
{
    public NCActorId ActorId { get; } = actorId;
    public Guid PlanId { get; } = planId;
    public NCNeuralPriority Priority { get; } = priority;
    public string Failure { get; } = failure;
    public NCActiveWorldTime FailedAt { get; } = failedAt;
}

/// <summary>A validated physical plan finished, so the actor may choose its next action immediately.</summary>
public sealed class NCNeuralActionExecutionCompletedEvent(
    NCActorId actorId,
    Guid planId,
    NCNeuralPriority priority,
    string intentKey,
    string targetHandle,
    NCActiveWorldTime completedAt) : EntityEventArgs
{
    public NCActorId ActorId { get; } = actorId;
    public Guid PlanId { get; } = planId;
    public NCNeuralPriority Priority { get; } = priority;
    public string IntentKey { get; } = intentKey;
    public string TargetHandle { get; } = targetHandle;
    public NCActiveWorldTime CompletedAt { get; } = completedAt;
}
