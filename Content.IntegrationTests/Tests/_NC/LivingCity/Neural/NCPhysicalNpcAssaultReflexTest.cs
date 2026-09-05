// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Server._NC.Combat.Armor;
using Content.Server._NC.GameRules;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Cognition.Components;
using Content.Server._NC.LivingCity.Neural;
using Content.Server._NC.LivingCity.Time;
using Content.Server.CombatMode;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Presets;
using Content.Server.Maps;
using Content.Server.NPC.Components;
using Content.Server.Weapons.Melee;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared._NC.Configuration;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Mobs.Components;
using Content.Shared.NPC;
using Content.Shared.Weapons.Melee;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Neural;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(NCCityLocalPerceptionSystem))]
public sealed class NCPhysicalNpcAssaultReflexTest : GameTest
{
    private const string PresetId = "NCReachNpcTest";
    private const string MapPoolId = "NCReachNpcTestMapPool";
    private const string DefensiveSteeringOwner = "human-defensive-reflex";
    private const string ActionSteeringOwner = "human-vanilla-action";
    private static readonly ProtoId<NCNeuralPromptProfilePrototype> PromptProfileId =
        new("NCLivingCityDefault");

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
        Dirty = true,
        DummyTicker = false,
        InLobby = true,
    };

    [Test]
    public async Task RealMeleeImpactWakesVictimStartsReflexAndAcceptedFleeReleasesIt()
    {
        var server = Pair.Server;
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var entities = server.ResolveDependency<IEntityManager>();
        var ticker = server.System<GameTicker>();
        var rule = server.System<NCReachNpcTestRuleSystem>();
        var runtime = server.System<NCCityActorRuntimeIndexSystem>();
        var actors = server.System<NCCityActorRegistrySystem>();
        var clock = server.System<NCActiveWorldTimeSystem>();
        var context = server.System<NCNeuralContextBuilderSystem>();
        var client = server.System<NCNeuralCognitionClientSystem>();
        var perception = server.System<NCCityLocalPerceptionSystem>();
        var execution = server.System<NCNeuralActionExecutionSystem>();
        var armorCoverage = server.System<NCArmorCoverageSystem>();
        var combatMode = server.System<CombatModeSystem>();
        var melee = server.System<MeleeWeaponSystem>();
        var transform = server.System<TransformSystem>();
        var preset = prototypes.Index<GamePresetPrototype>(PresetId);
        var pool = prototypes.Index<GameMapPoolPrototype>(MapPoolId);

        Assert.That(pool.Maps, Is.EquivalentTo(new[] { "Reach" }));
        server.CfgMan.SetCVar(CCVars.GameMap, "Reach");
        await server.WaitPost(() =>
        {
            ticker.SetGamePreset(preset);
            ticker.StartRound();
        });
        await WaitUntil(server,
            () => ticker.RunLevel == GameRunLevel.InRound && rule.IsActive,
            "The Reach NPC assault test did not enter InRound.",
            600);
        await WaitUntil(server,
            () => rule.ReadyNpcCount + rule.FailedNpcCount == NCReachNpcTestRuleSystem.ExpectedNpcCount,
            "The Reach NPC assault cohort did not finish spawning.",
            1800,
            () => $"ready={rule.ReadyNpcCount}, failed={rule.FailedNpcCount}");

        EntityUid victim = EntityUid.Invalid;
        EntityUid armoredVictim = EntityUid.Invalid;
        NCActorId actorId = default;
        NCActorId armoredActorId = default;
        await server.WaitAssertion(() =>
        {
            Assert.That(rule.FailedNpcCount, Is.Zero);
            var candidates = entities.EntityQueryEnumerator<NCNpcJobComponent>();
            while (candidates.MoveNext(out var uid, out var job))
            {
                // Passenger gear has no authored armor, so a real unarmed strike must create trauma instead of
                // turning this regression test into an armor-roll test.
                if (job.Job.Id != "Passenger" ||
                    !entities.HasComponent<NCMedicalBodyComponent>(uid) ||
                    !entities.HasComponent<NCCityLocalPerceptionComponent>(uid) ||
                    !entities.HasComponent<MobStateComponent>(uid))
                {
                    continue;
                }

                if (!victim.IsValid())
                {
                    victim = uid;
                    continue;
                }

                armoredVictim = uid;
                break;
            }

            Assert.That(victim.IsValid(), Is.True,
                "The physical Reach cohort exposed no authoritative medical NPC victim.");
            Assert.That(armoredVictim.IsValid(), Is.True,
                "The physical Reach cohort exposed no second passenger for the armor-stop edge.");
            Assert.That(runtime.TryGetActor(victim, out actorId), Is.True);
            Assert.That(runtime.TryGetActor(armoredVictim, out armoredActorId), Is.True);
        });

        EntityUid attacker = EntityUid.Invalid;
        var victimAtImpact = default(MapCoordinates);
        var attackerAtImpact = default(MapCoordinates);
        var woundsBefore = 0;
        var attemptsBefore = context.IncidentSubmissionAttempts;
        var submissionsBefore = context.IncidentSubmissions;
        await server.WaitPost(() =>
        {
            // A configured-but-unreachable loopback endpoint keeps this test independent of provider HTTP while
            // proving the production P0 request reaches the neural transport boundary.
            server.CfgMan.SetCVar(NCCVars.NeuralApiKey, "integration-test-key");
            server.CfgMan.SetCVar(NCCVars.NeuralEndpoint, "http://127.0.0.1:1/v1/cognition");

            victimAtImpact = transform.GetMapCoordinates(victim);
            // Keep both bodies within one tile so the vanilla unobstructed melee check cannot cross a nearby wall.
            attacker = entities.SpawnEntity("MobHuman", victimAtImpact.Offset(new Vector2(0.25f, 0f)));
            attackerAtImpact = transform.GetMapCoordinates(attacker);
            combatMode.SetInCombatMode(attacker, true);
            woundsBefore = entities.TryGetComponent<NCWoundContainerComponent>(victim, out var wounds)
                ? wounds.Wounds.Count
                : 0;
        });
        await server.WaitRunTicks(1);

        await server.WaitAssertion(() =>
        {
            Assert.That(melee.TryGetWeapon(attacker, out var weapon, out var weaponComponent), Is.True,
                "The physical attacker has no vanilla unarmed melee authority.");
            Assert.That(melee.AttemptLightAttack(attacker, weapon, weaponComponent!, victim), Is.True,
                "Vanilla melee rejected the in-range physical attack.");
        });

        await WaitUntil(server,
            () => entities.TryGetComponent<NCWoundContainerComponent>(victim, out var wounds) &&
                  wounds.Wounds.Count > woundsBefore,
            "The real melee contact never reached authoritative NC medicine.",
            120);
        await WaitUntil(server,
            () => context.IncidentSubmissionAttempts > attemptsBefore &&
                  context.IncidentSubmissions > submissionsBefore,
            "The assault against self did not produce a P0 neural context submission.",
            300,
            () => $"attempts={context.IncidentSubmissionAttempts - attemptsBefore}, " +
                  $"submissions={context.IncidentSubmissions - submissionsBefore}, " +
                  $"provider-enabled={client.IsProviderEnabled}");

        string attackerHandle = string.Empty;
        NCCityLocalPerceivedEvent assault = default;
        await server.WaitAssertion(() =>
        {
            Assert.That(clock.TryGetCurrentTime(out _, out var now), Is.True);
            Assert.That(perception.TryGetSnapshot(actorId, now, out var subjects, out var events), Is.True);
            assault = events.Single(value =>
                value.Kind == NCCityLocalPerceivedEventKind.Assault &&
                value.ActorEntity == attacker &&
                value.TargetEntity == victim);
            attackerHandle = assault.ActorHandle;

            Assert.Multiple(() =>
            {
                Assert.That(assault.TargetHandle, Is.EqualTo("self"));
                Assert.That(subjects.Any(value => value.EntityUid == attacker &&
                                                  value.SubjectHandle == attackerHandle &&
                                                  value.Kind == NCCityLocalObservationKind.Hostile), Is.True);
                Assert.That(entities.TryGetComponent<NCCityNpcSteeringLeaseComponent>(victim, out var lease),
                    Is.True, "The immediate defensive reflex did not acquire a steering lease.");
                Assert.That(lease!.Owners, Does.Contain(DefensiveSteeringOwner));
                Assert.That(entities.HasComponent<ActiveNPCComponent>(victim), Is.True);
                Assert.That(entities.TryGetComponent<NPCSteeringComponent>(victim, out var steering), Is.True,
                    "The reflex acquired no vanilla steering goal.");

                var destination = transform.ToMapCoordinates(steering!.Coordinates);
                var awayFromAttacker = victimAtImpact.Position - attackerAtImpact.Position;
                var reflexTravel = destination.Position - victimAtImpact.Position;
                Assert.That(destination.MapId, Is.EqualTo(victimAtImpact.MapId));
                Assert.That(Vector2.Dot(reflexTravel, awayFromAttacker), Is.GreaterThan(0f),
                    "The temporary reflex steering goal does not retreat from the confirmed attacker.");
            });
        });

        var acceptedPlan = Guid.NewGuid();
        await server.WaitAssertion(() =>
        {
            Assert.That(clock.TryGetCurrentTime(out var epoch, out var now), Is.True);
            Assert.That(actors.TryGetCached(actorId, out var actor), Is.True);
            var command = new NCNeuralCommandProposal(
                "NCCognitionRespondToThreat",
                "execute",
                attackerHandle,
                1000,
                "safe-distance-reached",
                [],
                [])
            {
                OriginalIndex = 0,
            };
            var request = new NCNeuralRequestEnvelope(
                acceptedPlan,
                NCNeuralRequestKind.StrategicDecision,
                NCNeuralPriority.P0,
                actorId,
                epoch,
                actor.AggregateVersion,
                now.Advance(TimeSpan.FromMinutes(2)),
                PromptProfileId,
                "living-city-default",
                3,
                "physical-gpt-alife-v3",
                [],
                [command.CapabilityId],
                ["direct-assault"],
                "en-US",
                PerceivedSubjects:
                [
                    new NCNeuralPerceivedSubject(
                        attackerHandle,
                        "observable hostile person",
                        "hostile",
                        1f,
                        assault.ExpiresAt.Ticks),
                ],
                PerceivedEvents:
                [
                    new NCNeuralPerceivedEvent(
                        "assault",
                        attackerHandle,
                        "self",
                        1f,
                        assault.ObservedAt.Ticks,
                        assault.ExpiresAt.Ticks),
                ]);
            var response = new NCNeuralResponseEnvelope(
                acceptedPlan,
                NCNeuralContract.SchemaVersion,
                request.ContextVersion,
                [command],
                "statement",
                string.Empty,
                false,
                "flee-direct-assault",
                1000,
                0,
                0,
                0,
                "integration-test",
                "integration-test",
                request.PromptStableKey,
                request.PromptVersion,
                request.EvaluationProfile)
            {
                DecisionMode = "act",
                Intent = new NCNeuralIntentChoice("flee-direct-assault", attackerHandle, 1000),
            };

            // This is the trusted provider-result boundary, not a direct executor mutation. The production
            // suggestion coordinator validates the sensory handle and accepts the typed flee procedure.
            entities.EventBus.RaiseEvent(EventSource.Local,
                new NCNeuralResultEvent(new NCNeuralResult(
                    NCNeuralResultStatus.Accepted,
                    request,
                    response,
                    string.Empty)));
            Assert.That(execution.TryGetSnapshot(actorId, out var accepted), Is.True);
            Assert.That(accepted.PlanId, Is.EqualTo(acceptedPlan));
        });

        await WaitUntil(server,
            () => entities.TryGetComponent<NCCityNpcSteeringLeaseComponent>(victim, out var lease) &&
                  !lease.Owners.Contains(DefensiveSteeringOwner) &&
                  lease.Owners.Contains(ActionSteeringOwner) &&
                  entities.TryGetComponent<NCHumanVanillaActionComponent>(victim, out var action) &&
                  action.Operation == NCHumanActionOperationKind.FleeFromTarget,
            "The accepted GPT flee plan did not release the bounded reflex and take over vanilla steering.",
            120,
            () => ReflexDiagnostics(entities, victim, execution, actorId));

        EntityUid armoredAttacker = EntityUid.Invalid;
        var armoredWoundsBefore = 0;
        await server.WaitPost(() =>
        {
            var coordinates = transform.GetMapCoordinates(armoredVictim);
            var armor = entities.SpawnEntity("NCArmorTestSubdermal", coordinates);
            Assert.That(armorCoverage.TryBindSource(armor, armoredVictim), Is.True,
                "The all-zone armor fixture did not bind to the second physical NPC.");
            armoredWoundsBefore = entities.TryGetComponent<NCWoundContainerComponent>(armoredVictim, out var wounds)
                ? wounds.Wounds.Count
                : 0;
            armoredAttacker = entities.SpawnEntity("MobHuman", coordinates.Offset(new Vector2(0.25f, 0f)));
            combatMode.SetInCombatMode(armoredAttacker, true);
        });
        await server.WaitRunTicks(1);
        await server.WaitAssertion(() =>
        {
            Assert.That(melee.TryGetWeapon(armoredAttacker, out var weapon, out var weaponComponent), Is.True);
            Assert.That(melee.AttemptLightAttack(
                armoredAttacker,
                weapon,
                weaponComponent!,
                armoredVictim), Is.True);
        });

        await WaitUntil(server,
            () => clock.TryGetCurrentTime(out _, out var now) &&
                  perception.TryGetSnapshot(armoredActorId, now, out _, out var events) &&
                  events.Any(value => value.Kind == NCCityLocalPerceivedEventKind.Assault &&
                                      value.ActorEntity == armoredAttacker &&
                                      value.TargetEntity == armoredVictim) &&
                  entities.TryGetComponent<NCCityNpcSteeringLeaseComponent>(armoredVictim, out var lease) &&
                  lease.Owners.Contains(DefensiveSteeringOwner),
            "A real hit fully stopped by armor was not perceived as an assault against self.",
            120);
        await server.WaitAssertion(() =>
        {
            Assert.That(entities.TryGetComponent<NCWoundContainerComponent>(armoredVictim, out var wounds), Is.True);
            Assert.That(wounds!.Wounds.Count, Is.EqualTo(armoredWoundsBefore),
                "The armor-stop fixture unexpectedly applied trauma, so it did not exercise the zero-damage edge.");
        });
    }

    private static string ReflexDiagnostics(
        IEntityManager entities,
        EntityUid victim,
        NCNeuralActionExecutionSystem execution,
        NCActorId actorId)
    {
        var owners = entities.TryGetComponent<NCCityNpcSteeringLeaseComponent>(victim, out var lease)
            ? string.Join(",", lease.Owners)
            : "none";
        var action = entities.TryGetComponent<NCHumanVanillaActionComponent>(victim, out var state)
            ? state.Operation.ToString()
            : "none";
        var plan = execution.TryGetSnapshot(actorId, out var snapshot)
            ? snapshot.PlanId.ToString()
            : "none";
        return $"owners=[{owners}], action={action}, plan={plan}";
    }

    private static async Task WaitUntil(
        RobustIntegrationTest.ServerIntegrationInstance server,
        Func<bool> predicate,
        string message,
        int maximumTicks,
        Func<string>? diagnostics = null)
    {
        const int tickBatch = 10;
        for (var elapsed = 0; elapsed < maximumTicks; elapsed += tickBatch)
        {
            if (predicate())
                return;

            await server.WaitRunTicks(Math.Min(tickBatch, maximumTicks - elapsed));
        }

        Assert.Fail(diagnostics == null ? message : $"{message} {diagnostics()}");
    }
}
