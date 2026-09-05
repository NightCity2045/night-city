// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Server._NC.GameRules;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.Neural;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Presets;
using Content.Server.Maps;
using Content.Server.Station.Components;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.Roles;
using Content.Shared._NC.Configuration;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.GameRules;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(NCReachNpcTestRuleSystem))]
public sealed class NCReachNpcTestRuleTest : GameTest
{
    private const string PresetId = "NCReachNpcTest";
    private const string MapPoolId = "NCReachNpcTestMapPool";
    private static readonly ProtoId<NCHumanActionPrototype> MoveActionId =
        new("human-move-to-station-destination");
    private static readonly ProtoId<NCCognitionCapabilityProfilePrototype> CivilianCapabilitiesId =
        new("NCDefaultCivilianCapabilities");

    private static readonly IReadOnlyDictionary<ProtoId<JobPrototype>, int> ExpectedRoster =
        new Dictionary<ProtoId<JobPrototype>, int>
        {
            ["Captain"] = 1,
            ["HeadOfSecurity"] = 1,
            ["Bartender"] = 1,
            ["Botanist"] = 1,
            ["Chef"] = 1,
            ["Janitor"] = 1,
            ["AtmosphericTechnician"] = 1,
            ["StationEngineer"] = 3,
            ["Chemist"] = 1,
            ["MedicalDoctor"] = 3,
            ["Scientist"] = 3,
            ["SecurityOfficer"] = 3,
            ["CargoTechnician"] = 2,
            ["SalvageSpecialist"] = 2,
            ["Passenger"] = 3,
            ["Clown"] = 1,
            ["Mime"] = 1,
            ["Musician"] = 1,
        };

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
        Dirty = true,
        DummyTicker = false,
        InLobby = true,
    };

    [Test]
    public async Task ReachRunsThirtyActorCorrelatedWorkplaceDecisionsThroughTheHttpBoundary()
    {
        var server = Pair.Server;
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var entities = server.ResolveDependency<IEntityManager>();
        var ticker = server.System<GameTicker>();
        var system = server.System<NCReachNpcTestRuleSystem>();
        var runtime = server.System<NCCityActorRuntimeIndexSystem>();
        var lifecycle = server.System<NCPhysicalActorLifecycleSystem>();
        var neural = server.System<NCNeuralCognitionClientSystem>();
        var brain = server.System<NCPhysicalActorBrainSystem>();
        var execution = server.System<NCNeuralActionExecutionSystem>();
        var router = server.System<NCHumanActionRouterSystem>();
        var preset = prototypes.Index<GamePresetPrototype>(PresetId);
        var pool = prototypes.Index<GameMapPoolPrototype>(MapPoolId);
        var moveAction = prototypes.Index(MoveActionId);
        var capabilities = prototypes.Index(CivilianCapabilitiesId);

        Assert.Multiple(() =>
        {
            Assert.That(pool.Maps, Is.EquivalentTo(new[] { "Reach" }));
            Assert.That(preset.Rules.Select(rule => rule.Id), Is.EqualTo(new[] { "NCReachNpcTestRule" }));
            Assert.That(ExpectedRoster.Values.Sum(), Is.EqualTo(NCReachNpcTestRuleSystem.ExpectedNpcCount));
            Assert.That(moveAction.Operation, Is.EqualTo(NCHumanActionOperationKind.MoveToStationDestination));
            Assert.That(moveAction.TargetKind, Is.EqualTo(NCHumanActionTargetKind.DomainRecord));
            Assert.That(capabilities.Capabilities.Select(value => value.Id),
                Does.Contain("NCCognitionMoveToStationDestination"));
        });

        await using var provider = new NCReachNeuralLoopbackProvider();
        var beforeRouter = router.GetCounters();
        var beforeExecutionCompleted = execution.CompletedCount;
        var beforeExecutionFailed = execution.FailedCount;
        server.CfgMan.SetCVar(CCVars.GameMap, "Reach");
        server.CfgMan.SetCVar(NCCVars.NeuralEnabled, true);
        server.CfgMan.SetCVar(NCCVars.NeuralEndpoint, provider.Endpoint.ToString());
        server.CfgMan.SetCVar(NCCVars.NeuralApiKey, NCReachNeuralLoopbackProvider.ApiKey);
        await server.WaitPost(() =>
        {
            ticker.SetGamePreset(preset);
            ticker.StartRound();
        });
        await WaitUntil(
            () => ticker.RunLevel == GameRunLevel.InRound && system.IsActive,
            "The Reach NPC test round did not enter InRound.",
            600);
        var observedSpawnErrors = new HashSet<string>();
        await WaitUntil(
            () =>
            {
                foreach (var state in system.GetSpawnStates())
                {
                    if (!string.IsNullOrWhiteSpace(state.Error))
                        observedSpawnErrors.Add(state.Error);
                }
                return system.ReadyNpcCount + system.FailedNpcCount == NCReachNpcTestRuleSystem.ExpectedNpcCount;
            },
            "The Reach NPC test cohort did not finish spawning.",
            1800,
            () => $"ready={system.ReadyNpcCount}, failed={system.FailedNpcCount}, " +
                  $"cohort={system.CohortCount}, queued={lifecycle.QueuedCount}, loads={lifecycle.LoadsInFlight}, " +
                  $"states=[{string.Join(", ", system.GetSpawnStates().GroupBy(state => state.Status)
                      .Select(group => $"{group.Key}:{group.Count()}"))}], " +
                  $"errors=[{string.Join(" | ", observedSpawnErrors)}]");

        var bodies = new Dictionary<NCActorId, EntityUid>();
        string? evaluationSummary = null;
        await server.WaitAssertion(() =>
        {
            var ordinaryBootstrap = new NCPhysicalActorCatalogBootstrapAttemptEvent(
                NCActorId.New(),
                new ProtoId<NCCityActorArchetypePrototype>("NCLivingCityResident"));
            entities.EventBus.RaiseEvent(EventSource.Local, ordinaryBootstrap);
            var unrelatedTestBootstrap = new NCPhysicalActorCatalogBootstrapAttemptEvent(
                NCActorId.New(),
                new ProtoId<NCCityActorArchetypePrototype>("NCReachTestWorker"));
            entities.EventBus.RaiseEvent(EventSource.Local, unrelatedTestBootstrap);

            Assert.Multiple(() =>
            {
                Assert.That(system.ConfigurationError, Is.Null);
                Assert.That(system.FailedNpcCount, Is.Zero);
                Assert.That(system.CohortCount, Is.EqualTo(NCReachNpcTestRuleSystem.ExpectedNpcCount));
                Assert.That(system.ReadyNpcCount, Is.EqualTo(NCReachNpcTestRuleSystem.ExpectedNpcCount));
                Assert.That(runtime.PhysicalActorCount, Is.EqualTo(NCReachNpcTestRuleSystem.ExpectedNpcCount));
                Assert.That(ordinaryBootstrap.Cancelled, Is.False,
                    "The Reach rule suppressed an ordinary persistent actor's physical bootstrap.");
                Assert.That(unrelatedTestBootstrap.Cancelled, Is.False,
                    "The Reach rule left a test actor outside its own cohort without a physical body.");
            });

            var actualRoster = new Dictionary<ProtoId<JobPrototype>, int>();
            var contextualizedActors = 0;
            var query = entities.EntityQueryEnumerator<NCNpcJobComponent>();
            while (query.MoveNext(out var uid, out var job))
            {
                Assert.That(runtime.TryGetActor(uid, out var actorId), Is.True,
                    "A Reach body has no stable ActorId binding.");
                Assert.That(bodies.TryAdd(actorId, uid), Is.True,
                    "Two physical Reach bodies share one stable ActorId.");
                actualRoster[job.Job] = actualRoster.GetValueOrDefault(job.Job) + 1;
                Assert.That(entities.HasComponent<NCCityLocalPerceptionComponent>(uid), Is.True,
                    "A Reach test NPC was spawned without production local perception.");

                contextualizedActors++;
            }

            Assert.That(actualRoster, Is.EquivalentTo(ExpectedRoster));
            Assert.That(contextualizedActors, Is.EqualTo(NCReachNpcTestRuleSystem.ExpectedNpcCount));
            Assert.That(bodies.Count, Is.EqualTo(NCReachNpcTestRuleSystem.ExpectedNpcCount));

            var stations = entities.EntityQueryEnumerator<StationJobsComponent>();
            while (stations.MoveNext(out _, out var jobs))
                Assert.That(jobs.PlayerJobs, Is.Empty, "NPC job setup consumed a player job assignment.");
        });

        // Every actor must cross the actual HTTP boundary, receive an actor-correlated command, pass schema and
        // capability validation, and terminate one normal station-movement action before explicitly choosing wait.
        await WaitUntil(
            () =>
            {
                var snapshot = provider.GetSnapshot();
                return snapshot.Actors.Count == NCReachNpcTestRuleSystem.ExpectedNpcCount &&
                       snapshot.Actors.Values.All(value => value.ActionRequestId != null);
            },
            "Not every Reach NPC received an actor-correlated workplace action from the HTTP provider.",
            3600,
            () => ProviderDiagnostics(provider, neural, brain, execution, bodies));

        await WaitUntil(
            () =>
            {
                var snapshot = provider.GetSnapshot();
                return bodies.All(pair => snapshot.Actors.TryGetValue(pair.Key.Value, out var actor) &&
                    actor.ActionRequestId is { } requestId &&
                    execution.GetRecentCommandResults(pair.Key).Any(result =>
                        result.RequestId == requestId && IsTerminal(result.State)));
            },
            "At least one Reach NPC produced no terminal result for its provider-selected workplace action.",
            20000,
            () => ProviderDiagnostics(provider, neural, brain, execution, bodies));

        await WaitUntil(
            () =>
            {
                var snapshot = provider.GetSnapshot();
                return snapshot.Actors.Count == NCReachNpcTestRuleSystem.ExpectedNpcCount &&
                       snapshot.Actors.Values.All(value => value.PostTerminalDecisions > 0);
            },
            "At least one terminal action failed to trigger a new actor-correlated GPT decision.",
            1800,
            () => ProviderDiagnostics(provider, neural, brain, execution, bodies));

        await WaitUntil(
            () => neural.GetRuntimeSnapshot() is { Queued: 0, InFlight: 0 } &&
                  brain.SubmissionsInFlight == 0 &&
                  bodies.Keys.All(actorId => !execution.TryGetSnapshot(actorId, out _)),
            "The Reach evaluation left a provider request or physical command stuck in flight.",
            600,
            () => ProviderDiagnostics(provider, neural, brain, execution, bodies));

        await server.WaitAssertion(() =>
        {
            var providerSnapshot = provider.GetSnapshot();
            var neuralSnapshot = neural.GetRuntimeSnapshot();
            var strategic = neuralSnapshot.KindRuntime[NCNeuralRequestKind.StrategicDecision];
            var terminalResults = bodies.Keys.Select(actorId =>
            {
                var providerActor = providerSnapshot.Actors[actorId.Value];
                return execution.GetRecentCommandResults(actorId)
                    .Single(value => value.RequestId == providerActor.ActionRequestId);
            }).ToArray();
            var completedActions = terminalResults.Count(value => value.State == "completed");
            var failedActions = terminalResults.Count(value => value.State == "failed");
            var failureBreakdown = terminalResults
                .Where(value => value.State == "failed")
                .GroupBy(value => value.FailureReason)
                .OrderBy(group => group.Key)
                .Select(group => $"{group.Key}:{group.Count()}")
                .ToArray();
            evaluationSummary = $"Reach neural evaluation: actors={bodies.Count}, " +
                                $"completed={completedActions}, failed={failedActions}, " +
                                $"explicit-waits={providerSnapshot.Actors.Values.Count(value => value.WaitDecisions > 0)}, " +
                                $"post-terminal={providerSnapshot.Actors.Values.Count(value => value.PostTerminalDecisions > 0)}, " +
                                $"stale-replans={neuralSnapshot.Stale}, failures=[{string.Join(",", failureBreakdown)}]";
            Assert.Multiple(() =>
            {
                Assert.That(providerSnapshot.Actors.Count, Is.EqualTo(NCReachNpcTestRuleSystem.ExpectedNpcCount));
                Assert.That(providerSnapshot.ActionOrder.Count,
                    Is.EqualTo(NCReachNpcTestRuleSystem.ExpectedNpcCount));
                Assert.That(providerSnapshot.ActionOrder.Distinct().Count(),
                    Is.EqualTo(NCReachNpcTestRuleSystem.ExpectedNpcCount),
                    "The provider repeated an action for one actor instead of giving every body a fair turn.");
                Assert.That(providerSnapshot.RejectedActor, Is.Not.Null);
                Assert.That(providerSnapshot.Actors.Values.Sum(value => value.Rejections), Is.EqualTo(1),
                    "The loopback provider rejected more than its single intentional actor-local request.");
                Assert.That(providerSnapshot.Actors.Values.All(value => value.ActionRequestId != null), Is.True);
                Assert.That(providerSnapshot.Actors.Values.All(value => value.WaitDecisions > 0), Is.True,
                    "An idle actor had no explicit provider-authored reason to wait.");
                Assert.That(providerSnapshot.Actors.Values.All(value => value.PostTerminalDecisions > 0), Is.True,
                    "A terminal vanilla action did not produce a follow-up cognition loop for its own ActorId.");

                Assert.That(neuralSnapshot.ProviderConfigured, Is.True);
                Assert.That(neuralSnapshot.CircuitOpenUntil, Is.Null,
                    "One request-specific 422 opened the global provider circuit.");
                Assert.That(neuralSnapshot.ConsecutiveFailures, Is.Zero);
                Assert.That(neuralSnapshot.ProviderFailures, Is.EqualTo(1));
                Assert.That(neuralSnapshot.Fallbacks, Is.EqualTo(1));
                Assert.That(neuralSnapshot.Invalid, Is.Zero);
                Assert.That(neuralSnapshot.Timeouts, Is.Zero);
                Assert.That(neuralSnapshot.ValidationRejects, Is.Zero);
                Assert.That(strategic.Submitted, Is.GreaterThanOrEqualTo(61));
                Assert.That(strategic.Accepted, Is.GreaterThanOrEqualTo(60));
                Assert.That(strategic.Fallback, Is.EqualTo(1));
                Assert.That(strategic.Rejected, Is.EqualTo(neuralSnapshot.Stale + neuralSnapshot.Invalid),
                    "A strategic request expired or was rejected for a reason outside normal context invalidation.");
                Assert.That(brain.DecisionsSubmitted,
                    Is.GreaterThanOrEqualTo(NCReachNpcTestRuleSystem.ExpectedNpcCount));

                Assert.That(completedActions + failedActions, Is.EqualTo(NCReachNpcTestRuleSystem.ExpectedNpcCount));
                Assert.That(completedActions, Is.GreaterThanOrEqualTo(24),
                    "Fewer than 80% of the Reach cohort completed its assigned-workplace movement.");
                Assert.That(failedActions, Is.LessThanOrEqualTo(6));
                Assert.That(terminalResults.Where(value => value.State == "failed").All(value =>
                        value.FailureReason is "human-action-timeout" or "vanilla-station-destination-no-path"),
                    Is.True,
                    "The workplace action failed for a reason other than a normal pathfinding outcome.");
                Assert.That(terminalResults.Any(value =>
                        value.FailureReason.Contains("neural-plan-expired", StringComparison.Ordinal)),
                    Is.False,
                    "A fair actor-local action slot expired before the vanilla authority returned a result.");
                Assert.That(execution.CompletedCount - beforeExecutionCompleted +
                            execution.FailedCount - beforeExecutionFailed,
                    Is.GreaterThanOrEqualTo(NCReachNpcTestRuleSystem.ExpectedNpcCount));
                var afterRouter = router.GetCounters();
                Assert.That(afterRouter.Completed - beforeRouter.Completed + afterRouter.Failed - beforeRouter.Failed,
                    Is.GreaterThanOrEqualTo(NCReachNpcTestRuleSystem.ExpectedNpcCount));
                Assert.That(runtime.PhysicalActorCount, Is.EqualTo(NCReachNpcTestRuleSystem.ExpectedNpcCount));
            });

            foreach (var (actorId, originalBody) in bodies)
            {
                Assert.That(runtime.TryGetEntity(actorId, out var currentBody), Is.True);
                Assert.That(currentBody, Is.EqualTo(originalBody),
                    $"Actor {actorId} changed bodies during the active-world session.");
                Assert.That(entities.EntityExists(currentBody), Is.True);
                Assert.That(providerSnapshot.Actors.TryGetValue(actorId.Value, out var providerActor), Is.True);
                Assert.That(providerActor!.Requests, Is.GreaterThanOrEqualTo(2));
                Assert.That(providerActor.ActionRequestId, Is.Not.Null);

                var results = execution.GetRecentCommandResults(actorId)
                    .Where(value => value.RequestId == providerActor.ActionRequestId)
                    .ToArray();
                Assert.That(results, Has.Length.EqualTo(1));
                Assert.That(results[0].CapabilityId, Is.EqualTo("NCCognitionMoveToStationDestination"));
                Assert.That(results[0].ActionId, Is.EqualTo("human-move-to-station-destination"));
                Assert.That(results[0].State, Is.AnyOf("completed", "failed"));
                if (results[0].State == "completed")
                    Assert.That(results[0].FailureKind, Is.EqualTo("none"));
                else
                {
                    Assert.That(results[0].FailureKind, Is.Not.EqualTo("none"));
                    Assert.That(results[0].FailureReason, Is.Not.Empty);
                }

                Assert.That(neural.TryGetActorRuntimeSnapshot(actorId, out var actorRuntime), Is.True);
                Assert.That(actorRuntime.LastStatus, Is.EqualTo(NCNeuralResultStatus.Accepted));
                Assert.That(actorRuntime.Accepted, Is.GreaterThanOrEqualTo(2));
                Assert.That(actorRuntime.OutstandingRequests, Is.Zero);
                Assert.That(actorRuntime.HasPendingRequest, Is.False);
            }

            var stations = entities.EntityQueryEnumerator<StationJobsComponent>();
            while (stations.MoveNext(out _, out var jobs))
                Assert.That(jobs.PlayerJobs, Is.Empty, "The completed NPC shift consumed a player job slot.");
        });

        TestContext.Progress.WriteLine(evaluationSummary);

    }

    private static string ProviderDiagnostics(
        NCReachNeuralLoopbackProvider provider,
        NCNeuralCognitionClientSystem neural,
        NCPhysicalActorBrainSystem brain,
        NCNeuralActionExecutionSystem execution,
        IReadOnlyDictionary<NCActorId, EntityUid> bodies)
    {
        var providerSnapshot = provider.GetSnapshot();
        var neuralSnapshot = neural.GetRuntimeSnapshot();
        var completed = bodies.Count(pair => providerSnapshot.Actors.TryGetValue(pair.Key.Value, out var actor) &&
            actor.ActionRequestId is { } requestId && execution.GetRecentCommandResults(pair.Key)
                .Any(result => result.RequestId == requestId && result.State == "completed"));
        var failed = bodies.Count(pair => providerSnapshot.Actors.TryGetValue(pair.Key.Value, out var actor) &&
            actor.ActionRequestId is { } requestId && execution.GetRecentCommandResults(pair.Key)
                .Any(result => result.RequestId == requestId && result.State == "failed"));
        var unresolved = bodies.Where(pair => providerSnapshot.Actors.TryGetValue(pair.Key.Value, out var actor) &&
                actor.ActionRequestId is { } requestId && execution.GetRecentCommandResults(pair.Key)
                    .All(result => result.RequestId != requestId || !IsTerminal(result.State)))
            .Select(pair => execution.TryGetSnapshot(pair.Key, out var current)
                ? $"{pair.Key.Value:N}:running-{current.CurrentStep}/{current.StepCount}"
                : execution.TryGetLastFailure(pair.Key, out var failure)
                    ? $"{pair.Key.Value:N}:{failure.Failure}"
                    : $"{pair.Key.Value:N}:no-result")
            .Take(8)
            .ToArray();
        return $"provider-actors={providerSnapshot.Actors.Count}, actions={providerSnapshot.ActionOrder.Count}, " +
               $"waits={providerSnapshot.Actors.Values.Count(value => value.WaitDecisions > 0)}, " +
               $"post-terminal={providerSnapshot.Actors.Values.Count(value => value.PostTerminalDecisions > 0)}, " +
               $"completed={completed}, failed={failed}, unresolved=[{string.Join(",", unresolved)}], " +
               $"queued={neuralSnapshot.Queued}, in-flight={neuralSnapshot.InFlight}, " +
               $"accepted={neuralSnapshot.Accepted}, fallback={neuralSnapshot.Fallbacks}, " +
               $"last-failure={neuralSnapshot.LastFailureKind}:{neuralSnapshot.LastFailureReason}, " +
               $"brain-scheduled={brain.ScheduledActors}, brain-in-flight={brain.SubmissionsInFlight}";
    }

    private static bool IsTerminal(string state) => state is "completed" or "failed";

    private async Task WaitUntil(
        Func<bool> predicate,
        string message,
        int maximumTicks,
        Func<string> diagnostics = null)
    {
        const int tickBatch = 10;
        for (var elapsed = 0; elapsed < maximumTicks; elapsed += tickBatch)
        {
            if (predicate())
                return;

            // The real round has a connected lobby client. Keep it in lockstep with the accelerated server so PVS
            // and replicated CVars cannot accumulate thousands of late network states during the long evaluation.
            await Pair.RunTicksSync(Math.Min(tickBatch, maximumTicks - elapsed));
            // HTTP completion runs on worker threads while this fixture advances simulation ticks much faster than
            // wall time. Yield a minimal slice so the real HttpClient/provider boundary cannot be starved by polling.
            await Task.Delay(1);
        }

        if (predicate())
            return;

        Assert.Fail(diagnostics == null ? message : $"{message} {diagnostics()}");
    }
}
