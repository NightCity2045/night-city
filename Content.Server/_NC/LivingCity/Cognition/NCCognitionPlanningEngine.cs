// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Diagnostics;
using System.Linq;
using Content.Server._NC.LivingCity.Identity;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Cognition;

/// <summary>
/// Pure Plan Router and bounded planner helpers. Planners only emit typed plans; they cannot mutate the world.
/// </summary>
public static class NCCognitionPlanningEngine
{
    public static NCCognitionPlannerKind Route(
        NCCognitionTaskKind task,
        NCCognitionPlannerKind preferred,
        NCCognitionPlannerKind fallback,
        NCCognitionRoutingProfilePrototype routing)
    {
        var routed = routing.Routes.GetValueOrDefault(task, preferred);
        return HasBudget(routing, routed)
            ? routed
            : HasBudget(routing, fallback) ? fallback : NCCognitionPlannerKind.DeterministicFallback;
    }

    public static NCCognitionPlanSnapshot BuildPlan(
        NCCognitionCapabilityPrototype capability,
        NCCognitionIntentionSnapshot intention,
        NCCognitionRoutingProfilePrototype routing,
        IReadOnlyList<NCCognitionPlanDependency> dependencies,
        NCActiveWorldTime now,
        int maximumSteps,
        out bool usedFallback)
    {
        usedFallback = false;
        var planner = Route(capability.TaskKind, capability.PreferredPlanner, capability.FallbackPlanner, routing);
        var steps = ConvertSteps(capability, intention.Target, maximumSteps, out var error);
        if (error != null)
        {
            planner = NCCognitionPlannerKind.DeterministicFallback;
            usedFallback = true;
            steps = [new NCCognitionPlanStep("safe-fallback", "remain-available", intention.Target, [], false)];
        }
        if (planner == NCCognitionPlannerKind.PartialOrder && !ValidatePartialOrder(steps, out _))
        {
            planner = capability.FallbackPlanner;
            usedFallback = true;
        }

        var dependencyVersion = Math.Max(1, dependencies.Aggregate(17L,
            (current, value) => unchecked(current * 31 + value.Version)) & long.MaxValue);
        var planId = NCPersistentIdentity.CognitionPlan(intention.IntentionId, planner.ToString(), dependencyVersion);
        var status = planner == NCCognitionPlannerKind.Mcts
            ? NCCognitionPlanStatus.Planning
            : NCCognitionPlanStatus.Ready;
        return new NCCognitionPlanSnapshot(
            planId,
            intention.IntentionId,
            planner,
            status,
            dependencies.ToArray(),
            status == NCCognitionPlanStatus.Planning ? [] : steps,
            0,
            0,
            DeterministicSeed(planId),
            now,
            now,
            null);
    }

    public static NCCognitionPlanSnapshot AdvanceMcts(
        NCCognitionPlanSnapshot plan,
        NCCognitionCapabilityPrototype capability,
        NCCognitionSubjectRef target,
        NCCognitionRoutingProfilePrototype routing,
        NCCognitionMctsFrontierState? frontier,
        NCActiveWorldTime now,
        int maximumSteps,
        out NCCognitionMctsFrontierState? resultingFrontier,
        out int expanded,
        out string? selectedOption)
    {
        expanded = 0;
        selectedOption = null;
        resultingFrontier = frontier;
        if (plan.Planner != NCCognitionPlannerKind.Mcts || plan.Status != NCCognitionPlanStatus.Planning)
            return plan;
        var budget = routing.Budgets.FirstOrDefault(value => value.Planner == NCCognitionPlannerKind.Mcts);
        var options = capability.StrategicOptions.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray();
        if (budget == null || budget.MaximumNodesPerTick <= 0 || budget.MaximumTotalNodes <= 0 || options.Length < 2)
            return plan with { Status = NCCognitionPlanStatus.Failed, UpdatedAt = now, Failure = "mcts-budget-missing" };

        if (frontier == null || frontier.PlanId != plan.PlanId ||
            !frontier.Branches.Select(value => value.OptionId)
                .SequenceEqual(options.Select(value => value.Id), StringComparer.Ordinal))
        {
            frontier = new NCCognitionMctsFrontierState(plan.PlanId,
                options.Select(value => new NCCognitionMctsBranchState(value.Id)).ToArray());
        }
        resultingFrontier = frontier;

        var started = Stopwatch.GetTimestamp();
        while (expanded < budget.MaximumNodesPerTick && frontier.TotalVisits < budget.MaximumTotalNodes)
        {
            if (expanded > 0 && Stopwatch.GetElapsedTime(started).TotalMilliseconds >=
                budget.MaximumMillisecondsPerTick)
                break;

            var branchIndex = SelectBranch(frontier, budget.ExplorationConstant);
            var option = options[branchIndex];
            var reward = option.MinimumOutcome + (option.MaximumOutcome - option.MinimumOutcome) *
                DeterministicUnit(plan.DeterministicSeed, frontier.RolloutSequence, option.Id);
            var branch = frontier.Branches[branchIndex];
            branch.Visits++;
            branch.TotalReward += reward;
            frontier.TotalVisits++;
            frontier.RolloutSequence++;
            expanded++;
        }

        if (frontier.TotalVisits < budget.MaximumTotalNodes)
            return plan with { ExpandedNodes = frontier.TotalVisits, UpdatedAt = now };

        var selected = frontier.Branches
            .OrderByDescending(value => value.Visits == 0 ? double.NegativeInfinity : value.TotalReward / value.Visits)
            .ThenBy(value => value.OptionId, StringComparer.Ordinal)
            .First();
        selectedOption = selected.OptionId;
        var selectedDefinition = options.First(value => value.Id == selected.OptionId);
        var steps = ConvertSteps(selectedDefinition.ProcedureSteps, target, maximumSteps, out var error);
        return error == null
            ? plan with
            {
                Status = NCCognitionPlanStatus.Ready,
                Steps = steps,
                ExpandedNodes = frontier.TotalVisits,
                UpdatedAt = now,
            }
            : plan with
            {
                Planner = NCCognitionPlannerKind.DeterministicFallback,
                Status = NCCognitionPlanStatus.Ready,
                Steps = [new NCCognitionPlanStep("safe-fallback", "remain-available", target, [], false)],
                ExpandedNodes = frontier.TotalVisits,
                UpdatedAt = now,
                Failure = error,
            };
    }

    private static int SelectBranch(NCCognitionMctsFrontierState frontier, float explorationConstant)
    {
        for (var index = 0; index < frontier.Branches.Length; index++)
        {
            if (frontier.Branches[index].Visits == 0)
                return index;
        }

        var logarithm = Math.Log(Math.Max(1, frontier.TotalVisits));
        var selected = 0;
        var selectedScore = double.NegativeInfinity;
        for (var index = 0; index < frontier.Branches.Length; index++)
        {
            var branch = frontier.Branches[index];
            var score = branch.TotalReward / branch.Visits +
                        explorationConstant * Math.Sqrt(logarithm / branch.Visits);
            if (score <= selectedScore)
                continue;
            selected = index;
            selectedScore = score;
        }
        return selected;
    }

    private static double DeterministicUnit(long seed, long sequence, string optionId)
    {
        var state = unchecked((ulong) seed) ^ unchecked((ulong) sequence * 0x9E3779B97F4A7C15UL) ^
                    StableHash(optionId);
        state ^= state >> 30;
        state *= 0xBF58476D1CE4E5B9UL;
        state ^= state >> 27;
        state *= 0x94D049BB133111EBUL;
        state ^= state >> 31;
        return (state >> 11) * (1.0 / (1UL << 53));
    }

    private static ulong StableHash(string value)
    {
        var hash = 14695981039346656037UL;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= 1099511628211UL;
        }
        return hash;
    }

    public static bool ValidateDependencies(
        NCCognitionPlanSnapshot plan,
        IReadOnlyDictionary<(NCCognitionDependencyKind Kind, string StableId), long> versions,
        out string? error)
    {
        foreach (var dependency in plan.Dependencies)
        {
            if (!versions.TryGetValue((dependency.Kind, dependency.StableId), out var version) ||
                version != dependency.Version)
            {
                error = $"stale-{dependency.Kind.ToString().ToLowerInvariant()}";
                return false;
            }
        }
        error = null;
        return true;
    }

    public static bool ValidatePartialOrder(IReadOnlyList<NCCognitionPlanStep> steps, out string? error)
    {
        var byId = new Dictionary<string, NCCognitionPlanStep>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.StepId) || string.IsNullOrWhiteSpace(step.ActionId) ||
                !byId.TryAdd(step.StepId, step))
            {
                error = "invalid-partial-order-reference";
                return false;
            }
        }
        if (steps.Any(value => value.DependsOn.Any(dependency => !byId.ContainsKey(dependency))))
        {
            error = "invalid-partial-order-reference";
            return false;
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        bool Visit(string id)
        {
            if (!visiting.Add(id))
                return false;
            if (visited.Contains(id))
            {
                visiting.Remove(id);
                return true;
            }
            foreach (var dependency in byId[id].DependsOn)
            {
                if (!Visit(dependency))
                    return false;
            }
            visiting.Remove(id);
            visited.Add(id);
            return true;
        }

        if (steps.Any(value => !Visit(value.StepId)))
        {
            error = "cyclic-partial-order-plan";
            return false;
        }
        error = null;
        return true;
    }

    private static NCCognitionPlanStep[] ConvertSteps(
        NCCognitionCapabilityPrototype capability,
        NCCognitionSubjectRef target,
        int maximumSteps,
        out string? error) => ConvertSteps(capability.ProcedureSteps, target, maximumSteps, out error);

    private static NCCognitionPlanStep[] ConvertSteps(
        IReadOnlyList<NCCognitionProcedureStep> procedure,
        NCCognitionSubjectRef target,
        int maximumSteps,
        out string? error)
    {
        error = null;
        if (procedure.Count == 0 || procedure.Count > maximumSteps)
        {
            error = "invalid-procedure-size";
            return [];
        }
        var result = procedure.Select(value => new NCCognitionPlanStep(
            value.Id.Trim(), value.Action.Trim(), target, value.DependsOn, value.Optional)).ToArray();
        if (!ValidatePartialOrder(result, out error))
            return [];
        return result;
    }

    private static bool HasBudget(NCCognitionRoutingProfilePrototype routing, NCCognitionPlannerKind planner) =>
        routing.Budgets.Any(value => value.Planner == planner && value.MaximumNodesPerTick > 0 &&
                                     value.MaximumTotalNodes >= value.MaximumNodesPerTick);

    private static long DeterministicSeed(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);
        return BitConverter.ToInt64(bytes);
    }
}
