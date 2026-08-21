// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text.Json;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Cognition;

[TestFixture]
[TestOf(typeof(NCCognitionSystem))]
public sealed class NCCognitionPolicyTest : GameTest
{
    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    public void DefaultProfilesCoverEveryPlannerRouteAndAtomicActorSubdomain()
    {
        var prototypes = Pair.Server.ResolveDependency<IPrototypeManager>();
        var routing = prototypes.Index<NCCognitionRoutingProfilePrototype>("NCDefaultCognitionRouting");
        var policy = prototypes.Index<NCCognitionRuntimePolicyPrototype>("NCDefaultCognitionRuntimePolicy");
        var archetype = prototypes.EnumeratePrototypes<NCCityActorArchetypePrototype>().First();
        var registry = Pair.Server.System<NCCognitionRegistrySystem>();
        var actorId = NCActorId.New();
        var writes = registry.BuildInitialWrites(actorId, archetype, NCActiveWorldTime.Zero, "test");

        Assert.Multiple(() =>
        {
            Assert.That(routing.Routes.Keys, Is.EquivalentTo(Enum.GetValues<NCCognitionTaskKind>()));
            Assert.That(routing.Routes[NCCognitionTaskKind.Routine], Is.EqualTo(NCCognitionPlannerKind.StateTree));
            Assert.That(routing.Routes[NCCognitionTaskKind.ImmediateReaction], Is.EqualTo(NCCognitionPlannerKind.Reactive));
            Assert.That(routing.Routes[NCCognitionTaskKind.KnownProcedure], Is.EqualTo(NCCognitionPlannerKind.Htn));
            Assert.That(routing.Routes[NCCognitionTaskKind.GroupOperation], Is.EqualTo(NCCognitionPlannerKind.PartialOrder));
            Assert.That(routing.Routes[NCCognitionTaskKind.StrategicChoice], Is.EqualTo(NCCognitionPlannerKind.Mcts));
            Assert.That(routing.Routes[NCCognitionTaskKind.SocialConversation],
                Is.EqualTo(NCCognitionPlannerKind.ConversationStateTree));
            Assert.That(policy.MaximumTrackedActors, Is.EqualTo(10_000));
            Assert.That(policy.MaximumQueuedActors, Is.EqualTo(50_000));
            Assert.That(policy.MaximumAbstractTraceEntries, Is.EqualTo(8));
            Assert.That(policy.MaximumCachedPlans, Is.EqualTo(20_000));
            Assert.That(policy.MaximumInvalidationsPerTick, Is.EqualTo(64));
            Assert.That(policy.MaximumPlanningMillisecondsPerTick, Is.EqualTo(3f));
            Assert.That(writes.Select(value => value.AggregateType), Is.EquivalentTo(new[]
            {
                NCCognitionRegistrySystem.CognitionAggregateType,
                NCCognitionRegistrySystem.BeliefSetAggregateType,
            }));
        });

        var cognition = JsonSerializer.Deserialize<NCCognitionSnapshot>(
            writes.Single(value => value.AggregateType == NCCognitionRegistrySystem.CognitionAggregateType)
                .SnapshotPayload,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.Multiple(() =>
        {
            Assert.That(cognition, Is.Not.Null);
            Assert.That(cognition!.ActorId, Is.EqualTo(actorId));
            Assert.That(cognition.BeliefSetVersion, Is.EqualTo(1));
            Assert.That(cognition.CurrentIntention, Is.Null);
        });
    }

    [Test]
    public void BeliefsAreVersionedAndBdiCommitmentPreventsTwitching()
    {
        var prototypes = Pair.Server.ResolveDependency<IPrototypeManager>();
        var policy = prototypes.Index<NCCognitionDecisionPolicyPrototype>("NCDefaultCognitionDecisionPolicy");
        var actorId = NCActorId.New();
        var now = NCActiveWorldTime.Zero;
        var fact = new NCCognitionFact(
            "actor.can-act",
            NCCognitionSubjectRef.None,
            NCCognitionDomain.Core,
            "actor-root",
            7,
            NCCognitionValue.FromBoolean(true),
            now,
            now.Advance(policy.BeliefLifetime));
        var beliefs = NCCognitionDecisionEngine.ProjectBeliefs(actorId, [], [fact], policy, now);
        var replay = NCCognitionDecisionEngine.ProjectBeliefs(
            actorId, beliefs, [fact], policy, now.Advance(TimeSpan.FromSeconds(1)));
        var aging = NCCognitionDecisionEngine.ProjectBeliefs(
            actorId, beliefs, [], policy, now.Advance(TimeSpan.FromSeconds(30)));
        var staleBeliefs = NCCognitionDecisionEngine.ProjectBeliefs(
            actorId, beliefs, [], policy, now.Advance(TimeSpan.FromMinutes(2)));

        var routine = new NCCognitionCapabilityCandidate(
            new ProtoId<NCCognitionCapabilityPrototype>("NCCognitionFollowRoutine"),
            NCCognitionSubjectRef.None,
            20f,
            100,
            NCCognitionTaskKind.Routine,
            NCCognitionPlannerKind.StateTree,
            NCCognitionPlannerKind.DeterministicFallback,
            false,
            false);
        var first = NCCognitionDecisionEngine.SelectIntention(actorId, null, [routine], policy, now, 1);
        var tempting = routine with
        {
            Capability = new ProtoId<NCCognitionCapabilityPrototype>("NCCognitionStrategicFallback"),
            Utility = routine.Utility + policy.SwitchUtilityDelta + policy.SwitchCost - 1f,
        };
        var retained = NCCognitionDecisionEngine.SelectIntention(
            actorId, first.Intention, [tempting, routine], policy, now.Advance(TimeSpan.FromSeconds(5)), 2);
        var critical = tempting with
        {
            Capability = new ProtoId<NCCognitionCapabilityPrototype>("NCCognitionRespondToThreat"),
            Priority = 900,
            InterruptsRoutine = true,
        };
        var interrupted = NCCognitionDecisionEngine.SelectIntention(
            actorId, first.Intention, [critical, routine], policy, now.Advance(TimeSpan.FromSeconds(5)), 3);

        Assert.Multiple(() =>
        {
            Assert.That(beliefs, Has.Length.EqualTo(1));
            Assert.That(beliefs[0].SourceVersion, Is.EqualTo(7));
            Assert.That(replay[0].BeliefId, Is.EqualTo(beliefs[0].BeliefId));
            Assert.That(aging[0].Freshness, Is.EqualTo(NCBeliefFreshness.Aging));
            Assert.That(aging[0].Confidence, Is.EqualTo(policy.AgingConfidenceMultiplier).Within(0.0001f));
            Assert.That(staleBeliefs[0].Freshness, Is.EqualTo(NCBeliefFreshness.Stale));
            Assert.That(staleBeliefs[0].Confidence,
                Is.EqualTo(policy.StaleConfidenceMultiplier).Within(0.0001f));
            Assert.That(retained.Intention!.IntentionId, Is.EqualTo(first.Intention!.IntentionId),
                "A small Utility change must not cause intention twitching inside the commitment window.");
            Assert.That(retained.Switched, Is.False);
            Assert.That(interrupted.Intention!.CapabilityId, Is.EqualTo("NCCognitionRespondToThreat"));
            Assert.That(interrupted.Switched, Is.True,
                "A higher-priority explicit interrupt must break routine commitment.");
        });
    }

    [Test]
    public void PartialOrderMctsAndDependencyInvalidationStayBoundedAndDeterministic()
    {
        var prototypes = Pair.Server.ResolveDependency<IPrototypeManager>();
        var routing = prototypes.Index<NCCognitionRoutingProfilePrototype>("NCDefaultCognitionRouting");
        var capability = prototypes.Index<NCCognitionCapabilityPrototype>("NCCognitionStrategicFallback");
        var group = prototypes.Index<NCCognitionCapabilityPrototype>("NCCognitionGroupDirective");
        var actorId = NCActorId.New();
        var now = NCActiveWorldTime.Zero;
        var target = new NCCognitionSubjectRef(NCCognitionReferenceKind.Actor,
            "22222222-3333-4444-5555-666666666666");
        var intention = new NCCognitionIntentionSnapshot(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            capability.ID,
            target,
            capability.TaskKind,
            10f,
            1f,
            1,
            now,
            now,
            now.Advance(TimeSpan.FromMinutes(1)),
            1);
        var dependencies = new[]
        {
            new NCCognitionPlanDependency(NCCognitionDependencyKind.ActorRoot, actorId.ToString(), 9),
            new NCCognitionPlanDependency(NCCognitionDependencyKind.BeliefSet, actorId.ToString(), 4),
        };
        var plan = NCCognitionPlanningEngine.BuildPlan(
            capability, intention, routing, dependencies, now, 128, out var fallback);
        var replay = NCCognitionPlanningEngine.BuildPlan(
            capability, intention, routing, dependencies, now, 128, out _);
        var maximumPerTick = routing.Budgets.Single(value => value.Planner == NCCognitionPlannerKind.Mcts)
            .MaximumNodesPerTick;
        var maximumTotal = routing.Budgets.Single(value => value.Planner == NCCognitionPlannerKind.Mcts)
            .MaximumTotalNodes;
        var maximumObserved = 0;
        NCCognitionMctsFrontierState? frontier = null;
        string? selectedOption = null;
        while (plan.Status == NCCognitionPlanStatus.Planning)
        {
            plan = NCCognitionPlanningEngine.AdvanceMcts(
                plan, capability, target, routing, frontier, now, 128,
                out frontier, out var expanded, out selectedOption);
            maximumObserved = Math.Max(maximumObserved, expanded);
        }
        NCCognitionMctsFrontierState? replayFrontier = null;
        string? replayOption = null;
        while (replay.Status == NCCognitionPlanStatus.Planning)
        {
            replay = NCCognitionPlanningEngine.AdvanceMcts(
                replay, capability, target, routing, replayFrontier, now, 128,
                out replayFrontier, out _, out replayOption);
        }

        var groupIntention = intention with
        {
            IntentionId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            CapabilityId = group.ID,
            TaskKind = group.TaskKind,
        };
        var partialOrder = NCCognitionPlanningEngine.BuildPlan(
            group, groupIntention, routing, dependencies, now, 128, out var groupFallback);
        var currentVersions = dependencies.ToDictionary(value => (value.Kind, value.StableId), value => value.Version);
        var valid = NCCognitionPlanningEngine.ValidateDependencies(partialOrder, currentVersions, out _);
        currentVersions[(NCCognitionDependencyKind.ActorRoot, actorId.ToString())] = 10;
        var stale = NCCognitionPlanningEngine.ValidateDependencies(partialOrder, currentVersions, out var staleReason);
        var cycle = new[]
        {
            new NCCognitionPlanStep("first", "wait", NCCognitionSubjectRef.None, ["second"], false),
            new NCCognitionPlanStep("second", "wait", NCCognitionSubjectRef.None, ["first"], false),
        };
        var duplicate = new[]
        {
            new NCCognitionPlanStep("same", "wait", NCCognitionSubjectRef.None, [], false),
            new NCCognitionPlanStep("same", "wait", NCCognitionSubjectRef.None, [], false),
        };
        var invalidatedAtScale = 0;
        for (var i = 0; i < 10_000; i++)
        {
            if (!NCCognitionPlanningEngine.ValidateDependencies(partialOrder, currentVersions, out _))
                invalidatedAtScale++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(fallback, Is.False);
            Assert.That(replay.PlanId, Is.EqualTo(
                NCCognitionPlanningEngine.BuildPlan(capability, intention, routing, dependencies, now, 128, out _).PlanId));
            Assert.That(maximumObserved, Is.LessThanOrEqualTo(maximumPerTick));
            Assert.That(plan.ExpandedNodes, Is.EqualTo(maximumTotal));
            Assert.That(plan.Status, Is.EqualTo(NCCognitionPlanStatus.Ready));
            Assert.That(frontier!.Branches.Sum(value => value.Visits), Is.EqualTo(maximumTotal));
            Assert.That(selectedOption, Is.Not.Null.And.EqualTo(replayOption));
            Assert.That(plan.Steps.Select(value => value.ActionId),
                Is.EqualTo(replay.Steps.Select(value => value.ActionId)));
            Assert.That(plan.Steps, Has.All.Property(nameof(NCCognitionPlanStep.Target)).EqualTo(target));
            Assert.That(groupFallback, Is.False);
            Assert.That(partialOrder.Planner, Is.EqualTo(NCCognitionPlannerKind.PartialOrder));
            Assert.That(NCCognitionPlanningEngine.ValidatePartialOrder(partialOrder.Steps, out _), Is.True);
            Assert.That(NCCognitionPlanningEngine.ValidatePartialOrder(cycle, out _), Is.False);
            Assert.That(NCCognitionPlanningEngine.ValidatePartialOrder(duplicate, out _), Is.False);
            Assert.That(valid, Is.True);
            Assert.That(stale, Is.False);
            Assert.That(staleReason, Is.EqualTo("stale-actorroot"));
            Assert.That(invalidatedAtScale, Is.EqualTo(10_000));
            Assert.That((int) Math.Ceiling(invalidatedAtScale / 64d), Is.EqualTo(157),
                "The runtime policy drains a mass invalidation through bounded batches instead of a replan spike.");
        });
    }
}
