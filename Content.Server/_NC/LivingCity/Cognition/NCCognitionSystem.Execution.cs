// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Diagnostics;
using System.Linq;
using Content.Server._NC.LivingCity.Cognition.Components;
using Content.Server._NC.LivingCity.Simulation;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Cognition;

public sealed partial class NCCognitionSystem
{
    private void ProcessInvalidations(NCActiveWorldTime now)
    {
        if (_policy == null)
            return;
        var processed = 0;
        while (processed < _policy.MaximumInvalidationsPerTick)
        {
            if (_activeInvalidation == null)
            {
                if (!_invalidations.TryDequeue(out var nextKey) ||
                    !_pendingInvalidationVersions.Remove(nextKey, out var nextVersion))
                    break;
                _activeInvalidation = new DependencyInvalidation(nextKey.Kind, nextKey.StableId, nextVersion);
                _activeInvalidationConsumers.Clear();
            }

            var invalidation = _activeInvalidation.Value;
            var key = (invalidation.Kind, invalidation.StableId);
            if (!_dependencyConsumers.TryGetValue(key, out var consumers))
            {
                _activeInvalidation = null;
                continue;
            }

            // A dependency may feed thousands of actors. Remember which consumers have already been handled and
            // resume on later ticks instead of synchronously producing a city-wide replan spike.
            var exhausted = true;
            foreach (var actorId in consumers)
            {
                if (_activeInvalidationConsumers.Contains(actorId))
                    continue;
                if (processed >= _policy.MaximumInvalidationsPerTick)
                {
                    exhausted = false;
                    break;
                }
                _activeInvalidationConsumers.Add(actorId);
                processed++;
                if (!_states.TryGetValue(actorId, out var state) || state.Plan == null)
                    continue;
                state.DependencyVersions[key] = invalidation.Version;
                if (!state.Plan.Dependencies.Any(value => value.Kind == invalidation.Kind &&
                        value.StableId == invalidation.StableId && value.Version != invalidation.Version))
                    continue;
                state.Plan = state.Plan with
                {
                    Status = NCCognitionPlanStatus.Invalidated,
                    UpdatedAt = now,
                    Failure = $"stale-{invalidation.Kind.ToString().ToLowerInvariant()}",
                };
                state.LastFailure = state.Plan.Failure;
                _mctsFrontiers.Remove(actorId);
                _plansInvalidated++;
                Trace(state, now, "validation", "plan-invalidated", state.LastFailure);
                ScheduleDecision(actorId, now);
                CopyToMaterialized(state);
            }
            if (exhausted)
            {
                _activeInvalidation = null;
                _activeInvalidationConsumers.Clear();
            }
        }
    }

    private void AdvancePlanning(NCActiveWorldTime now)
    {
        if (_policy == null)
            return;
        var limit = _governor.TakeDispatchLimit(
            NCAILoadDispatchKind.CognitionPlanning,
            _policy.MaximumDispatchesPerTick);
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < limit; index++)
        {
            if (index > 0 && Stopwatch.GetElapsedTime(started).TotalMilliseconds >=
                _policy.MaximumPlanningMillisecondsPerTick)
                break;
            if (!_planningQueue.TryDequeue(out var actorId))
                break;
            _planningSet.Remove(actorId);
            if (!_states.TryGetValue(actorId, out var state) || state.Plan == null || state.Intention == null ||
                state.Plan.Status != NCCognitionPlanStatus.Planning)
                continue;
            if (!_prototypes.TryIndex(new ProtoId<NCCognitionCapabilityPrototype>(state.Intention.CapabilityId),
                    out var capability) || capability == null ||
                !_prototypes.TryIndex(new ProtoId<NCCognitionRoutingProfilePrototype>(state.RoutingProfileId),
                    out var routing) || routing == null)
            {
                ReplaceWithRuntimeFallback(state, now, "planner-configuration-missing");
                continue;
            }
            _mctsFrontiers.TryGetValue(actorId, out var frontier);
            var advanced = NCCognitionPlanningEngine.AdvanceMcts(
                state.Plan, capability, state.Intention.Target, routing, frontier, now, _policy.MaximumPlanSteps,
                out var resultingFrontier, out var expanded, out var selectedOption);
            _mctsNodesExpanded += expanded;
            state.Plan = advanced;
            if (resultingFrontier != null && advanced.Status == NCCognitionPlanStatus.Planning)
                _mctsFrontiers[actorId] = resultingFrontier;
            else
                _mctsFrontiers.Remove(actorId);
            Trace(state, now, "planner", "mcts-advanced", $"expanded={expanded}; total={advanced.ExpandedNodes}");
            if (advanced.Status == NCCognitionPlanStatus.Planning)
                SchedulePlanning(actorId);
            else if (advanced.Status == NCCognitionPlanStatus.Ready)
            {
                Trace(state, now, "planner", "mcts-selected", selectedOption ?? "deterministic-fallback");
                CachePlan(PlanCacheKey(capability.ID, advanced.Dependencies),
                    new CachedPlan(capability.ID, advanced.Planner, advanced.Dependencies, advanced.Steps));
                ScheduleExecution(actorId, now);
                _plansBuilt++;
            }
            else
                ReplaceWithRuntimeFallback(state, now, advanced.Failure ?? "mcts-failed");
            CopyToMaterialized(state);
        }
    }

    private void ExecuteDuePlans(NCActiveWorldTime now)
    {
        if (_policy == null)
            return;
        var limit = _governor.TakeDispatchLimit(
            NCAILoadDispatchKind.CognitionExecution,
            _policy.MaximumDispatchesPerTick);
        for (var index = 0; index < limit &&
             TryDequeueDue(_executionQueue, _scheduledExecutions, now, out var actorId); index++)
        {
            if (!_states.TryGetValue(actorId, out var state) || state.Plan == null || state.Intention == null ||
                state.Plan.Status is not (NCCognitionPlanStatus.Ready or NCCognitionPlanStatus.Executing))
                continue;
            if (!NCCognitionPlanningEngine.ValidateDependencies(
                    state.Plan, state.DependencyVersions, out var dependencyError))
            {
                state.Plan = state.Plan with
                {
                    Status = NCCognitionPlanStatus.Invalidated,
                    UpdatedAt = now,
                    Failure = dependencyError,
                };
                _plansInvalidated++;
                _mctsFrontiers.Remove(actorId);
                ScheduleDecision(actorId, now);
                CopyToMaterialized(state);
                continue;
            }

            var step = state.Plan.Steps.FirstOrDefault(candidate =>
                !state.CompletedStepIds.Contains(candidate.StepId) &&
                candidate.DependsOn.All(state.CompletedStepIds.Contains));
            if (step == null)
            {
                var complete = state.CompletedStepIds.Count >= state.Plan.Steps.Length;
                state.Plan = state.Plan with
                {
                    Status = complete ? NCCognitionPlanStatus.Completed : NCCognitionPlanStatus.Failed,
                    CurrentStep = state.CompletedStepIds.Count,
                    UpdatedAt = now,
                    Failure = complete ? null : "plan-has-no-runnable-step",
                };
                Trace(state, now, "execution", complete ? "plan-completed" : "plan-failed",
                    state.Plan.Failure ?? state.Plan.PlanId.ToString("D"));
                CopyToMaterialized(state);
                continue;
            }

            var result = ExecuteStep(state, step);
            if (result.Failure != null)
            {
                if (step.Optional)
                    state.CompletedStepIds.Add(step.StepId);
                else
                {
                    ReplaceWithRuntimeFallback(state, now, result.Failure);
                    continue;
                }
            }
            else if (result.Completed)
            {
                state.CompletedStepIds.Add(step.StepId);
            }
            state.Plan = state.Plan with
            {
                Status = NCCognitionPlanStatus.Executing,
                CurrentStep = state.CompletedStepIds.Count,
                UpdatedAt = now,
            };
            Trace(state, now, "execution", result.Completed ? "step-completed" : "step-waiting", step.ActionId);
            ScheduleExecution(actorId, now.Advance(result.Completed ? TimeSpan.Zero : TimeSpan.FromSeconds(1)));
            CopyToMaterialized(state);
        }
    }

    private (bool Completed, string? Failure) ExecuteStep(RuntimeState state, NCCognitionPlanStep step)
    {
        switch (step.ActionId)
        {
            case "remain-available":
            case "yield-to-medical-authority":
            case "yield-to-combat-authority":
            case "yield-to-conversation-authority":
            case "select-medical-service":
            case "prepare-group-role":
            case "acquire-group-resources":
            case "evaluate-safe-option":
            case "execute-group-directive":
                return (true, null);
            case "follow-schedule":
                return (state.TravelPhase is not (NCCityActorTravelPhase.Planned or
                    NCCityActorTravelPhase.InTransit), null);
            case "follow-schedule-travel":
            case "follow-group-route":
                return (state.TravelPhase is NCCityActorTravelPhase.Stationary or
                    NCCityActorTravelPhase.Arrived, null);
            case "perform-authoritative-work":
                return (!state.CurrentActivity.Equals("work", StringComparison.OrdinalIgnoreCase), null);
        }

        EntityUid? entity = _runtime.TryGetEntity(state.ActorId, out var entityUid) && Exists(entityUid)
            ? entityUid
            : null;
        var execution = new NCCognitionExecuteStepEvent(state.ActorId, entity, state.Intention!, step);
        RaiseLocalEvent(execution);
        return execution.Handled
            ? (execution.Completed, execution.Failure)
            : (false, $"unhandled-action:{step.ActionId}");
    }

    private void ReplaceWithRuntimeFallback(RuntimeState state, NCActiveWorldTime now, string failure)
    {
        if (state.Intention == null)
            return;
        RemoveDependencyConsumers(state.ActorId, state.Plan);
        _mctsFrontiers.Remove(state.ActorId);
        var dependencies = state.Plan?.Dependencies ?? [];
        var version = Math.Max(1, state.BeliefSetVersion);
        state.Plan = new NCCognitionPlanSnapshot(
            Content.Server._NC.LivingCity.Identity.NCPersistentIdentity.CognitionPlan(
                state.Intention.IntentionId, "runtime-fallback", version),
            state.Intention.IntentionId,
            NCCognitionPlannerKind.DeterministicFallback,
            NCCognitionPlanStatus.Ready,
            dependencies,
            [new NCCognitionPlanStep("safe-fallback", "remain-available", state.Intention.Target, [], false)],
            0,
            0,
            version,
            now,
            now,
            failure);
        state.CompletedStepIds.Clear();
        state.LastFailure = failure;
        _fallbackPlans++;
        AddDependencyConsumers(state.ActorId, state.Plan);
        ScheduleExecution(state.ActorId, now);
        Trace(state, now, "planner", "fallback-selected", failure);
        CopyToMaterialized(state);
    }

    private void SchedulePlanning(NCActorId actorId)
    {
        if (_planningSet.Add(actorId))
            _planningQueue.Enqueue(actorId);
    }

    private void ScheduleExecution(NCActorId actorId, NCActiveWorldTime dueAt)
    {
        if (_scheduledExecutions.TryGetValue(actorId, out var existing) && existing <= dueAt)
            return;
        _scheduledExecutions[actorId] = dueAt;
        var item = new ScheduledActor(actorId, dueAt, ++_sequence);
        _executionQueue.Enqueue(item, new SchedulePriority(dueAt.Ticks, item.Sequence));
    }

    private void AddDependencyConsumers(NCActorId actorId, NCCognitionPlanSnapshot? plan)
    {
        if (plan == null)
            return;
        foreach (var dependency in plan.Dependencies)
        {
            var key = (dependency.Kind, dependency.StableId);
            if (!_dependencyConsumers.TryGetValue(key, out var consumers))
            {
                consumers = [];
                _dependencyConsumers.Add(key, consumers);
            }
            consumers.Add(actorId);
        }
    }

    private void RemoveDependencyConsumers(NCActorId actorId, NCCognitionPlanSnapshot? plan)
    {
        if (plan == null)
            return;
        foreach (var dependency in plan.Dependencies)
        {
            var key = (dependency.Kind, dependency.StableId);
            if (!_dependencyConsumers.TryGetValue(key, out var consumers))
                continue;
            consumers.Remove(actorId);
            if (consumers.Count == 0)
                _dependencyConsumers.Remove(key);
        }
    }

    private void CopyToMaterialized(RuntimeState state)
    {
        if (!_runtime.TryGetEntity(state.ActorId, out var entityUid) || !Exists(entityUid))
            return;
        var component = EnsureComp<NCCognitionRuntimeComponent>(entityUid);
        CopyToComponent(entityUid, component, state);
    }

    private void CopyToComponent(EntityUid entityUid, NCCognitionRuntimeComponent component, RuntimeState state)
    {
        component.ActorId = state.ActorId;
        component.ActorVersion = state.ActorVersion;
        component.CognitionVersion = state.CognitionVersion;
        component.BeliefSetVersion = state.BeliefSetVersion;
        component.LastDecisionAt = state.LastDecisionAt;
        component.NextDecisionAt = state.NextDecisionAt;
        component.Facts.Clear();
        component.Facts.AddRange(state.Facts);
        component.Beliefs.Clear();
        component.Beliefs.AddRange(state.Beliefs);
        component.Desires.Clear();
        component.Desires.AddRange(state.Desires);
        component.Intention = state.Intention;
        component.Plan = state.Plan;
        component.Trace.Clear();
        component.Trace.AddRange(state.Trace);
    }

    private void Trace(RuntimeState state, NCActiveWorldTime now, string stage, string @event, string detail)
    {
        if (_policy == null)
            return;
        state.Trace.Add(new NCCognitionTraceEntry(++state.TraceSequence, now, stage, @event, detail));
        var maximum = _runtime.TryGetEntity(state.ActorId, out var entityUid) && Exists(entityUid)
            ? _policy.MaximumTraceEntries
            : _policy.MaximumAbstractTraceEntries;
        if (state.Trace.Count > maximum)
            state.Trace.RemoveRange(0, state.Trace.Count - maximum);
    }

    private bool ValidateConfiguration(NCCognitionRuntimePolicyPrototype policy, out string? error)
    {
        if (policy.MaximumTrackedActors is < 1 or > 100_000 ||
            policy.MaximumQueuedActors < policy.MaximumTrackedActors ||
            policy.MaximumDispatchesPerTick is < 1 or > 1_000 ||
            policy.MaximumConcurrentLoads is < 1 or > 1_000 ||
            policy.MaximumFacts is < 1 or > 512 || policy.MaximumCapabilities is < 1 or > 128 ||
            policy.MaximumPlanSteps is < 1 or > 512 || policy.MaximumTraceEntries is < 1 or > 512 ||
            policy.MaximumAbstractTraceEntries is < 1 ||
            policy.MaximumAbstractTraceEntries > policy.MaximumTraceEntries ||
            policy.MaximumCachedPlans is < 1 or > 200_000 ||
            policy.MaximumInvalidationsPerTick is < 1 or > 10_000 ||
            !float.IsFinite(policy.MaximumPlanningMillisecondsPerTick) ||
            policy.MaximumPlanningMillisecondsPerTick is <= 0f or > 10f ||
            policy.LoadRetryDelay <= TimeSpan.Zero || policy.FailureRetryDelay <= TimeSpan.Zero ||
            policy.MinimumAbstractEvaluationInterval <= TimeSpan.Zero)
        {
            error = "The cognition runtime policy has invalid bounds.";
            return false;
        }
        foreach (var curve in _prototypes.EnumeratePrototypes<NCCognitionUtilityCurvePrototype>())
        {
            if (!float.IsFinite(curve.MinimumInput) || !float.IsFinite(curve.MaximumInput) ||
                !float.IsFinite(curve.MinimumOutput) || !float.IsFinite(curve.MaximumOutput) ||
                !float.IsFinite(curve.Threshold) || curve.MaximumInput <= curve.MinimumInput ||
                curve.MaximumOutput < curve.MinimumOutput)
            {
                error = $"Utility curve {curve.ID} is invalid.";
                return false;
            }
        }
        foreach (var decision in _prototypes.EnumeratePrototypes<NCCognitionDecisionPolicyPrototype>())
        {
            if (decision.EvaluationInterval <= TimeSpan.Zero || decision.MinimumCommitment < TimeSpan.Zero ||
                decision.IntentionLifetime < decision.MinimumCommitment || decision.BeliefLifetime <= TimeSpan.Zero ||
                decision.BeliefAgingTime <= TimeSpan.Zero || decision.BeliefAgingTime >= decision.BeliefLifetime ||
                !float.IsFinite(decision.AgingConfidenceMultiplier) ||
                decision.AgingConfidenceMultiplier is < 0f or > 1f ||
                !float.IsFinite(decision.StaleConfidenceMultiplier) ||
                decision.StaleConfidenceMultiplier is < 0f or > 1f ||
                !float.IsFinite(decision.SwitchUtilityDelta) || decision.SwitchUtilityDelta < 0f ||
                !float.IsFinite(decision.SwitchCost) || decision.SwitchCost < 0f ||
                !float.IsFinite(decision.MinimumBeliefConfidence) ||
                decision.MinimumBeliefConfidence is < 0f or > 1f ||
                decision.MaximumBeliefs is < 1 or > 512 || decision.MaximumDesires is < 1 or > 128)
            {
                error = $"Decision policy {decision.ID} is invalid.";
                return false;
            }
        }
        foreach (var routing in _prototypes.EnumeratePrototypes<NCCognitionRoutingProfilePrototype>())
        {
            if (Enum.GetValues<NCCognitionTaskKind>().Any(task => !routing.Routes.ContainsKey(task)) ||
                Enum.GetValues<NCCognitionPlannerKind>().Any(planner =>
                    routing.Budgets.All(value => value.Planner != planner)) ||
                routing.Budgets.Select(value => value.Planner).Distinct().Count() != routing.Budgets.Length ||
                routing.Budgets.Any(value => value.MaximumNodesPerTick < 1 ||
                    value.MaximumTotalNodes < value.MaximumNodesPerTick ||
                    !float.IsFinite(value.MaximumMillisecondsPerTick) ||
                    value.MaximumMillisecondsPerTick is <= 0f or > 10f ||
                    value.MaximumMillisecondsPerTick > policy.MaximumPlanningMillisecondsPerTick ||
                    !float.IsFinite(value.ExplorationConstant) || value.ExplorationConstant < 0f))
            {
                error = $"Routing profile {routing.ID} is incomplete or invalid.";
                return false;
            }
        }
        foreach (var profile in _prototypes.EnumeratePrototypes<NCCognitionCapabilityProfilePrototype>())
        {
            if (profile.Capabilities.Length is < 1 || profile.Capabilities.Length > policy.MaximumCapabilities ||
                profile.Capabilities.Distinct().Count() != profile.Capabilities.Length ||
                profile.Capabilities.Any(value => !_prototypes.HasIndex(value)))
            {
                error = $"Capability profile {profile.ID} is incomplete or exceeds its configured bounds.";
                return false;
            }
        }
        foreach (var capability in _prototypes.EnumeratePrototypes<NCCognitionCapabilityPrototype>())
        {
            var procedure = capability.ProcedureSteps.Select(value => new NCCognitionPlanStep(
                value.Id, value.Action, NCCognitionSubjectRef.None, value.DependsOn, value.Optional)).ToArray();
            var optionsValid = capability.StrategicOptions.Select(option =>
            {
                var steps = option.ProcedureSteps.Select(value => new NCCognitionPlanStep(
                    value.Id, value.Action, NCCognitionSubjectRef.None, value.DependsOn, value.Optional)).ToArray();
                return !string.IsNullOrWhiteSpace(option.Id) && option.Id.Length <= 128 &&
                       float.IsFinite(option.MinimumOutcome) && float.IsFinite(option.MaximumOutcome) &&
                       option.MinimumOutcome is >= 0f and <= 1f && option.MaximumOutcome is >= 0f and <= 1f &&
                       option.MaximumOutcome >= option.MinimumOutcome &&
                       steps.Length is >= 1 && steps.Length <= policy.MaximumPlanSteps &&
                       NCCognitionPlanningEngine.ValidatePartialOrder(steps, out _);
            }).All(value => value);
            if (!float.IsFinite(capability.BaseUtility) ||
                capability.Considerations.Any(value => string.IsNullOrWhiteSpace(value.BeliefKey) ||
                    value.BeliefKey.Length > 256 || !_prototypes.HasIndex(value.Curve) ||
                    !float.IsFinite(value.Weight) || !float.IsFinite(value.MissingInput)) ||
                capability.RequiredBeliefs.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 256) ||
                capability.ForbiddenBeliefs.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 256) ||
                capability.StrategicOptions.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() !=
                capability.StrategicOptions.Length || !optionsValid ||
                (capability.PreferredPlanner == NCCognitionPlannerKind.Mcts ||
                 capability.TaskKind == NCCognitionTaskKind.StrategicChoice) && capability.StrategicOptions.Length < 2 ||
                procedure.Length is < 1 ||
                procedure.Length > policy.MaximumPlanSteps ||
                !NCCognitionPlanningEngine.ValidatePartialOrder(procedure, out _))
            {
                error = $"Capability {capability.ID} has an invalid procedure.";
                return false;
            }
        }
        error = null;
        return true;
    }
}
