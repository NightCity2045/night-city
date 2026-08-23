// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Identity;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Cognition;

public sealed partial class NCCognitionSystem
{
    private void CompleteDecisionLoad(
        int generation,
        NCActorId actorId,
        NCCityActorRecord? actor,
        NCCognitionRecord? cognition,
        NCBeliefSetRecord? beliefs,
        NCCognitionFact[] facts,
        NCActiveWorldTime requestedAt,
        Exception? error)
    {
        if (generation != _generation || !_active || _policy == null)
        {
            _inFlight.Remove(actorId);
            return;
        }
        if (!_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
        {
            _inFlight.Remove(actorId);
            return;
        }
        var state = GetOrCreateState(actorId);
        if (error != null || actor == null || cognition == null || beliefs == null)
        {
            state.LastFailure = error?.Message ?? "cognition-aggregate-missing";
            Trace(state, now, "load", "failed", state.LastFailure);
            _inFlight.Remove(actorId);
            ScheduleDecision(actorId, now.Advance(_policy.FailureRetryDelay));
            CopyToMaterialized(state);
            return;
        }
        if (actor.Snapshot.Lifecycle.Frozen ||
            actor.Snapshot.Lifecycle.Status is NCCityActorStatus.Dead or NCCityActorStatus.Archived)
        {
            _inFlight.Remove(actorId);
            Unschedule(actorId);
            return;
        }
        if (!_prototypes.TryIndex(new ProtoId<NCCognitionDecisionPolicyPrototype>(
                cognition.Snapshot.DecisionPolicyId), out var decisionPolicy) || decisionPolicy == null ||
            !_prototypes.TryIndex(new ProtoId<NCCognitionCapabilityProfilePrototype>(
                cognition.Snapshot.CapabilityProfileId), out var capabilityProfile) || capabilityProfile == null ||
            !_prototypes.TryIndex(new ProtoId<NCCognitionRoutingProfilePrototype>(
                cognition.Snapshot.RoutingProfileId), out var routing) || routing == null)
        {
            state.LastFailure = "cognition-profile-missing";
            Trace(state, now, "configuration", "rejected", state.LastFailure);
            _inFlight.Remove(actorId);
            ScheduleDecision(actorId, now.Advance(_policy.FailureRetryDelay));
            return;
        }

        var sink = new NCCognitionFactSink(_policy.MaximumFacts);
        foreach (var fact in facts)
            sink.Add(fact);
        RaiseLocalEvent(new NCCognitionCollectFactsEvent(actorId, actor, now, sink));
        facts = sink.ToArray();

        var projectedBeliefs = NCCognitionDecisionEngine.ProjectBeliefs(
            actorId, beliefs.Snapshot.Beliefs, facts, decisionPolicy, now);
        var capabilityIds = capabilityProfile.Capabilities.ToList();
        _professions.AddCapabilityOverlays(actorId, capabilityIds);
        var candidates = FilterTier(
            actor.Snapshot.CognitionTier,
            NCCognitionDecisionEngine.EvaluateCapabilities(
                _prototypes, capabilityIds, decisionPolicy, projectedBeliefs));
        var selection = NCCognitionDecisionEngine.SelectIntention(
            actorId,
            cognition.Snapshot.CurrentIntention,
            candidates,
            decisionPolicy,
            now,
            cognition.Snapshot.DataVersion + 1);
        var selectedDesire = selection.Intention == null
            ? null
            : selection.Desires.FirstOrDefault(value =>
                value.CapabilityId == selection.Intention.CapabilityId);
        if (!selection.Switched && cognition.Snapshot.CurrentDesire?.CapabilityId ==
            selection.Intention?.CapabilityId)
            selectedDesire = cognition.Snapshot.CurrentDesire;

        var beliefsChanged = !beliefs.Snapshot.Beliefs.SequenceEqual(projectedBeliefs);
        var intentionChanged = cognition.Snapshot.CurrentIntention != selection.Intention ||
                               cognition.Snapshot.CurrentDesire != selectedDesire;
        var persistentChange = beliefsChanged || intentionChanged;
        var resultingBeliefVersion = persistentChange
            ? beliefs.AggregateVersion + 1
            : beliefs.AggregateVersion;
        var evaluationInterval = _governor.ScaleEvaluationInterval(decisionPolicy.EvaluationInterval);
        if (!_runtime.TryGetEntity(actorId, out _))
            evaluationInterval = evaluationInterval < _policy.MinimumAbstractEvaluationInterval
                ? _policy.MinimumAbstractEvaluationInterval
                : evaluationInterval;
        var nextDecision = now.Advance(evaluationInterval);
        var plan = BuildOrReusePlan(
            state,
            actor,
            resultingBeliefVersion,
            selection.Intention,
            candidates,
            routing,
            facts,
            now,
            selection.Switched,
            out var usedFallback);

        var cognitionEvent = Guid.NewGuid();
        var beliefEvent = Guid.NewGuid();
        var nextCognition = persistentChange
            ? cognition.Snapshot with
            {
                BeliefSetVersion = resultingBeliefVersion,
                CurrentDesire = selectedDesire,
                CurrentIntention = selection.Intention,
                NextDecisionAt = nextDecision,
                DataVersion = cognition.Snapshot.DataVersion + 1,
                LastEventId = cognitionEvent,
            }
            : cognition.Snapshot;
        var nextBeliefs = persistentChange
            ? new NCBeliefSetSnapshot(
                actorId,
                projectedBeliefs,
                beliefs.Snapshot.DataVersion + 1,
                beliefEvent)
            : beliefs.Snapshot;
        var output = new NCCognitionDecisionOutput(
            nextCognition,
            nextBeliefs,
            selection.Desires,
            plan,
            selection.Switched,
            usedFallback,
            null);

        if (!persistentChange)
        {
            ApplyDecision(
                state,
                actor,
                cognition,
                beliefs,
                facts,
                projectedBeliefs,
                output,
                nextDecision,
                now);
            _inFlight.Remove(actorId);
            return;
        }

        var request = new NCCognitionCommitRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            actor.Snapshot.LastAppliedEventId,
            actorId,
            cognition.AggregateVersion,
            beliefs.AggregateVersion,
            nextCognition,
            nextBeliefs,
            "living-city.cognition");
        _ = ObserveCommit(
            generation, actor, facts, projectedBeliefs, output, nextDecision, now, request);
    }

    private async Task ObserveCommit(
        int generation,
        NCCityActorRecord actor,
        NCCognitionFact[] facts,
        NCBeliefSnapshot[] projectedBeliefs,
        NCCognitionDecisionOutput output,
        NCActiveWorldTime nextDecision,
        NCActiveWorldTime decidedAt,
        NCCognitionCommitRequest request)
    {
        NCCognitionCommitResult? result = null;
        Exception? error = null;
        try
        {
            result = await _registry.CommitAsync(request);
        }
        catch (Exception exception)
        {
            error = exception;
        }
        _taskManager.RunOnMainThread(() => CompleteCommit(
            generation, actor, facts, projectedBeliefs, output, nextDecision, decidedAt, request, result, error));
    }

    private void CompleteCommit(
        int generation,
        NCCityActorRecord actor,
        NCCognitionFact[] facts,
        NCBeliefSnapshot[] projectedBeliefs,
        NCCognitionDecisionOutput output,
        NCActiveWorldTime nextDecision,
        NCActiveWorldTime decidedAt,
        NCCognitionCommitRequest request,
        NCCognitionCommitResult? result,
        Exception? error)
    {
        var actorId = request.ActorId;
        _inFlight.Remove(actorId);
        if (generation != _generation || !_active || _policy == null)
            return;
        if (!_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
            return;
        var state = GetOrCreateState(actorId);
        if (error != null || result?.Cognition == null || result.BeliefSet == null ||
            result.Status is not (NCCognitionMutationStatus.Applied or NCCognitionMutationStatus.Duplicate))
        {
            state.LastFailure = error?.Message ?? result?.Error ?? "cognition-commit-failed";
            Trace(state, now, "persistence", "failed", state.LastFailure);
            ScheduleDecision(actorId, now.Advance(_policy.LoadRetryDelay));
            CopyToMaterialized(state);
            return;
        }
        ApplyDecision(
            state,
            actor,
            result.Cognition,
            result.BeliefSet,
            facts,
            projectedBeliefs,
            output,
            nextDecision,
            decidedAt);
    }

    private void ApplyDecision(
        RuntimeState state,
        NCCityActorRecord actor,
        NCCognitionRecord cognition,
        NCBeliefSetRecord beliefs,
        NCCognitionFact[] facts,
        NCBeliefSnapshot[] projectedBeliefs,
        NCCognitionDecisionOutput output,
        NCActiveWorldTime nextDecision,
        NCActiveWorldTime decidedAt)
    {
        var previousPlanId = state.Plan?.PlanId;
        RemoveDependencyConsumers(state.ActorId, state.Plan);
        state.ActorVersion = actor.AggregateVersion;
        state.CognitionTier = actor.Snapshot.CognitionTier;
        state.DecisionPolicyId = cognition.Snapshot.DecisionPolicyId;
        state.CapabilityProfileId = cognition.Snapshot.CapabilityProfileId;
        state.RoutingProfileId = cognition.Snapshot.RoutingProfileId;
        state.TravelPhase = actor.Snapshot.Location.TravelPhase;
        state.CurrentActivity = actor.Snapshot.Schedule.CurrentActivity;
        state.CognitionVersion = cognition.AggregateVersion;
        state.BeliefSetVersion = beliefs.AggregateVersion;
        var materialized = _runtime.TryGetEntity(state.ActorId, out var entityUid) && Exists(entityUid);
        state.Facts = materialized ? facts : [];
        state.Beliefs = materialized ? projectedBeliefs : [];
        state.Desires = materialized ? output.Desires : [];
        state.Intention = output.Cognition.CurrentIntention;
        state.Plan = output.Plan;
        if (previousPlanId != state.Plan?.PlanId)
        {
            state.CompletedStepIds.Clear();
            _mctsFrontiers.Remove(state.ActorId);
        }
        state.LastDecisionAt = decidedAt;
        state.NextDecisionAt = nextDecision;
        state.LastFailure = output.Error;
        state.DependencyVersions.Clear();
        if (state.Plan != null)
        {
            foreach (var dependency in state.Plan.Dependencies)
                state.DependencyVersions[(dependency.Kind, dependency.StableId)] = dependency.Version;
            AddDependencyConsumers(state.ActorId, state.Plan);
            if (state.Plan.Status == NCCognitionPlanStatus.Planning)
                SchedulePlanning(state.ActorId);
            else if (state.Plan.Status is NCCognitionPlanStatus.Ready or NCCognitionPlanStatus.Executing)
                ScheduleExecution(state.ActorId, decidedAt);
        }
        _decisions++;
        if (output.IntentionSwitched)
            _intentionSwitches++;
        if (output.UsedFallback)
            _fallbackPlans++;
        Trace(state, decidedAt, "bdi", output.IntentionSwitched ? "intention-switched" : "intention-retained",
            state.Intention?.CapabilityId ?? "none");
        ScheduleDecision(state.ActorId, nextDecision);
        CopyToMaterialized(state);
        RaiseLocalEvent(new NCCognitionDecisionAppliedEvent(
            state.ActorId,
            state.CognitionTier,
            state.ActorVersion,
            output.Desires,
            state.Intention,
            decidedAt));
        if (!materialized)
            _registry.Unload(state.ActorId);
    }

    private NCCognitionPlanSnapshot? BuildOrReusePlan(
        RuntimeState state,
        NCCityActorRecord actor,
        long beliefVersion,
        NCCognitionIntentionSnapshot? intention,
        IReadOnlyList<NCCognitionCapabilityCandidate> candidates,
        NCCognitionRoutingProfilePrototype routing,
        IReadOnlyList<NCCognitionFact> facts,
        NCActiveWorldTime now,
        bool intentionSwitched,
        out bool usedFallback)
    {
        usedFallback = false;
        if (intention == null)
            return null;
        var candidate = candidates.FirstOrDefault(value => value.Capability.Id == intention.CapabilityId);
        if (candidate == null || !_prototypes.TryIndex(candidate.Capability, out var capability) || capability == null)
        {
            usedFallback = true;
            return FallbackPlan(intention, actor, beliefVersion, now, "capability-missing");
        }
        var dependencies = BuildDependencies(actor, beliefVersion, intention, facts);
        var currentVersions = dependencies.ToDictionary(value => (value.Kind, value.StableId), value => value.Version);
        if (!intentionSwitched && state.Plan?.IntentionId == intention.IntentionId &&
            state.Plan.Status is not (NCCognitionPlanStatus.Invalidated or NCCognitionPlanStatus.Failed) &&
            NCCognitionPlanningEngine.ValidateDependencies(state.Plan, currentVersions, out _))
            return state.Plan;
        var cacheKey = PlanCacheKey(capability.ID, dependencies);
        if (_planCache.TryGetValue(cacheKey, out var cached))
        {
            var planner = cached.Planner;
            usedFallback = planner == NCCognitionPlannerKind.DeterministicFallback;
            var dependencyHash = Math.Max(1, dependencies.Aggregate(17L,
                (current, value) => unchecked(current * 31 + value.Version)) & long.MaxValue);
            return new NCCognitionPlanSnapshot(
                NCPersistentIdentity.CognitionPlan(intention.IntentionId, planner.ToString(), dependencyHash),
                intention.IntentionId,
                planner,
                NCCognitionPlanStatus.Ready,
                dependencies,
                cached.Steps,
                0,
                0,
                dependencyHash,
                now,
                now,
                null);
        }

        var plan = NCCognitionPlanningEngine.BuildPlan(
            capability, intention, routing, dependencies, now, _policy!.MaximumPlanSteps, out usedFallback);
        _plansBuilt++;
        if (plan.Status == NCCognitionPlanStatus.Ready)
            CachePlan(cacheKey, new CachedPlan(capability.ID, plan.Planner, dependencies, plan.Steps));
        return plan;
    }

    /// <summary>
    /// Bounds plan reuse for arbitrarily long rounds. Dependency versions are part of the key, so stale entries are
    /// harmless and leave through this FIFO without an invalidation-time full-cache scan.
    /// </summary>
    private void CachePlan(string key, CachedPlan plan)
    {
        if (_policy == null)
            return;
        if (_planCache.ContainsKey(key))
        {
            _planCache[key] = plan;
            return;
        }
        if (_planCacheOrder.Count >= _policy.MaximumCachedPlans)
        {
            var oldest = _planCacheOrder.Dequeue();
            _planCache.Remove(oldest);
        }
        _planCache[key] = plan;
        _planCacheOrder.Enqueue(key);
    }

    private static NCCognitionPlanDependency[] BuildDependencies(
        NCCityActorRecord actor,
        long beliefVersion,
        NCCognitionIntentionSnapshot intention,
        IReadOnlyList<NCCognitionFact> facts)
    {
        var actorId = actor.Snapshot.Identity.ActorId.ToString();
        var result = new List<NCCognitionPlanDependency>
        {
            new(NCCognitionDependencyKind.ActorRoot, actorId, actor.AggregateVersion),
            new(NCCognitionDependencyKind.BeliefSet, actorId, beliefVersion),
            new(NCCognitionDependencyKind.Location, actorId, actor.AggregateVersion),
        };
        var inventory = facts.Where(value => value.Source == "actor-inventory")
            .Select(value => value.SourceVersion).DefaultIfEmpty().Max();
        if (inventory > 0)
            result.Add(new NCCognitionPlanDependency(NCCognitionDependencyKind.Inventory, actorId, inventory));
        if (intention.Target.IsValid)
            result.Add(new NCCognitionPlanDependency(NCCognitionDependencyKind.Target,
                intention.Target.StableId, facts.Where(value => value.Subject == intention.Target)
                    .Select(value => value.SourceVersion).DefaultIfEmpty(1).Max()));
        return result.ToArray();
    }

    private NCCognitionPlanSnapshot FallbackPlan(
        NCCognitionIntentionSnapshot intention,
        NCCityActorRecord actor,
        long beliefVersion,
        NCActiveWorldTime now,
        string failure)
    {
        var dependencies = BuildDependencies(actor, beliefVersion, intention, []);
        var id = NCPersistentIdentity.CognitionPlan(intention.IntentionId, "fallback", beliefVersion);
        return new NCCognitionPlanSnapshot(
            id,
            intention.IntentionId,
            NCCognitionPlannerKind.DeterministicFallback,
            NCCognitionPlanStatus.Ready,
            dependencies,
            [new NCCognitionPlanStep("safe-fallback", "remain-available", intention.Target, [], false)],
            0,
            0,
            beliefVersion,
            now,
            now,
            failure);
    }

    private static NCCognitionCapabilityCandidate[] FilterTier(
        NCCityActorCognitionTier tier,
        IReadOnlyList<NCCognitionCapabilityCandidate> candidates) => tier switch
    {
        NCCityActorCognitionTier.Scripted => candidates.Where(value =>
            value.TaskKind is NCCognitionTaskKind.Routine or NCCognitionTaskKind.ImmediateReaction).ToArray(),
        NCCityActorCognitionTier.Utility => candidates.Where(value =>
            value.TaskKind is not (NCCognitionTaskKind.GroupOperation or NCCognitionTaskKind.StrategicChoice)).ToArray(),
        NCCityActorCognitionTier.Planner => candidates.Where(value =>
            value.TaskKind != NCCognitionTaskKind.StrategicChoice).ToArray(),
        _ => candidates.ToArray(),
    };

    private static string PlanCacheKey(
        string capabilityId,
        IReadOnlyList<NCCognitionPlanDependency> dependencies) =>
        $"{capabilityId}|{string.Join('|', dependencies.Select(value =>
            $"{value.Kind}:{value.StableId}:{value.Version}"))}";
}
