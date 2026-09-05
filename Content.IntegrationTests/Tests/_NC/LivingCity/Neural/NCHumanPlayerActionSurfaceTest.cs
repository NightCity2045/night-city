// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.Cognition.Components;
using Content.Shared.Body;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Stacks;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Treatment;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.Buckle;
using Content.Shared.Buckle.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Neural;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(NCHumanVanillaActionAdapterSystem))]
public sealed class NCHumanPlayerActionSurfaceTest : GameTest
{
    private static readonly ProtoId<NCHumanActionPrototype> UnbuckleActionId =
        new("human-unbuckle-self");
    private static readonly ProtoId<NCHumanActionPrototype> TreatPersonActionId =
        new("human-use-held-on-person");

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    public async Task TypedUnbuckleUsesVanillaAuthorityAndConfirmsWorldState()
    {
        var server = Pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var buckleSystem = server.System<SharedBuckleSystem>();
        var map = await Pair.CreateTestMap();
        EntityUid actor = default;
        EntityUid chair = default;
        BuckleComponent buckle = default!;

        await server.WaitAssertion(() =>
        {
            actor = entities.SpawnEntity("MobHuman", map.GridCoords);
            chair = entities.SpawnEntity("Chair", map.GridCoords);
            Assert.That(entities.TryGetComponent(actor, out buckle), Is.True);
            Assert.That(entities.HasComponent<StrapComponent>(chair), Is.True);
            Assert.That(buckleSystem.TryBuckle(actor, actor, chair, buckle, popup: false), Is.True);
        });

        // Vanilla buckle authority enforces a short unbuckle cooldown.
        await server.WaitRunTicks(60);
        await server.WaitAssertion(() =>
        {
            var action = prototypes.Index(UnbuckleActionId);
            var now = new NCActiveWorldTime(100);
            var actorId = NCActorId.New();
            var intention = new NCCognitionIntentionSnapshot(
                Guid.NewGuid(),
                "NCCognitionUnbuckleSelf",
                NCCognitionSubjectRef.None,
                NCCognitionTaskKind.KnownProcedure,
                1f,
                1f,
                1,
                now,
                now,
                now.Advance(TimeSpan.FromMinutes(1)),
                1);
            var step = new NCCognitionPlanStep(
                "unbuckle-self",
                action.ID,
                NCCognitionSubjectRef.None,
                [],
                false);
            var command = new NCNeuralCommandProposal(
                intention.CapabilityId,
                "execute",
                string.Empty,
                1,
                "unbuckled",
                [],
                []);
            var execution = new NCHumanActionExecutionEvent(
                Guid.NewGuid(),
                actorId,
                actor,
                intention.IntentionId,
                Guid.NewGuid(),
                intention,
                step,
                NCCognitionSubjectRef.None,
                action,
                command,
                now,
                now);

            entities.EventBus.RaiseEvent(EventSource.Local, execution);

            Assert.Multiple(() =>
            {
                Assert.That(execution.Handled, Is.True);
                Assert.That(execution.Completed, Is.True);
                Assert.That(execution.Failure, Is.Null);
                Assert.That(buckle.Buckled, Is.False);
                Assert.That(buckle.BuckledTo, Is.Null);
            });
        });
    }

    [Test]
    public async Task TypedTreatmentStaysRunningUntilDoAfterAppliesAuthoritativeMedicalChange()
    {
        var server = Pair.Server;
        var entities = server.ResolveDependency<IEntityManager>();
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var hands = server.System<SharedHandsSystem>();
        var map = await Pair.CreateTestMap();
        EntityUid doctor = default;
        EntityUid patient = default;
        EntityUid gauze = default;
        var woundId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var actorId = NCActorId.New();
        var startedAt = new NCActiveWorldTime(100);
        var beforeRate = 0f;
        var beforeRevision = 0u;
        var beforeStack = 0;
        NCHumanActionPrototype action = default!;
        NCCognitionIntentionSnapshot intention = default!;
        NCCognitionPlanStep step = default!;
        NCNeuralCommandProposal command = default!;

        await server.WaitAssertion(() =>
        {
            doctor = entities.SpawnEntity("MobHuman", map.GridCoords);
            patient = entities.SpawnEntity("MobHuman", map.GridCoords);
            gauze = entities.SpawnEntity("Gauze", map.GridCoords);
            Assert.That(hands.TryPickupAnyHand(doctor, gauze, animate: false), Is.True);
            Assert.That(hands.TrySelect(doctor, gauze), Is.True);

            var wounds = entities.EnsureComponent<NCWoundContainerComponent>(patient);
            wounds.Wounds.Clear();
            wounds.Wounds[woundId] = NewWound(woundId);
            var bleeding = entities.EnsureComponent<NCBleedingComponent>(patient);
            bleeding.Sources.Clear();
            bleeding.Sources.Add(NewBleedingSource(woundId));
            beforeRate = bleeding.Sources.Single().CurrentRate;
            beforeRevision = entities.GetComponent<NCMedicalBodyComponent>(patient).MedicalRevision;
            beforeStack = entities.GetComponent<StackComponent>(gauze).Count;

            action = prototypes.Index(TreatPersonActionId);
            intention = new NCCognitionIntentionSnapshot(
                Guid.NewGuid(),
                "NCCognitionUseHeldOnPerson",
                new NCCognitionSubjectRef(NCCognitionReferenceKind.StableKey, "patient-1"),
                NCCognitionTaskKind.KnownProcedure,
                1f,
                1f,
                1,
                startedAt,
                startedAt,
                startedAt.Advance(TimeSpan.FromMinutes(1)),
                1);
            step = new NCCognitionPlanStep(
                "use-held-on-person",
                action.ID,
                intention.Target,
                [],
                false);
            command = new NCNeuralCommandProposal(
                intention.CapabilityId,
                "execute",
                "patient-1",
                1,
                "patient-treated",
                [],
                []);
            var state = entities.EnsureComponent<NCHumanVanillaActionComponent>(doctor);
            state.InstanceId = instanceId;
            state.Operation = action.Operation;
            state.Target = patient;
        });

        var firstPulse = CreateTreatmentPulse(
            instanceId,
            actorId,
            doctor,
            intention,
            step,
            action,
            command,
            startedAt,
            startedAt);
        await server.WaitAssertion(() =>
        {
            entities.EventBus.RaiseEvent(EventSource.Local, firstPulse);
            Assert.Multiple(() =>
            {
                Assert.That(firstPulse.Handled, Is.True);
                Assert.That(firstPulse.Completed, Is.False,
                    "starting the treatment DoAfter must not complete the command");
                Assert.That(firstPulse.Failure, Is.Null);
                Assert.That(entities.TryGetComponent<NCHumanVanillaActionComponent>(doctor, out var state), Is.True);
                Assert.That(state!.OperationAttempted, Is.True);
                Assert.That(state.TreatmentConfirmed, Is.False);
                Assert.That(state.TreatmentResolutionId.IsValid, Is.True);
                Assert.That(entities.GetComponent<NCMedicalBodyComponent>(patient).MedicalRevision,
                    Is.EqualTo(beforeRevision));
                Assert.That(entities.GetComponent<StackComponent>(gauze).Count, Is.EqualTo(beforeStack));
            });
        });

        // NCFieldBandageCapability owns a real three-second DoAfter.
        await server.WaitRunTicks(240);
        await server.WaitAssertion(() =>
        {
            var state = entities.GetComponent<NCHumanVanillaActionComponent>(doctor);
            var source = entities.GetComponent<NCBleedingComponent>(patient).Sources.Single();
            Assert.Multiple(() =>
            {
                Assert.That(state.TreatmentConfirmed, Is.True,
                    "the adapter did not receive the correlated authoritative treatment result");
                Assert.That(entities.GetComponent<NCMedicalBodyComponent>(patient).MedicalRevision,
                    Is.GreaterThan(beforeRevision));
                Assert.That(source.CurrentRate, Is.LessThan(beforeRate));
                Assert.That(entities.GetComponent<StackComponent>(gauze).Count, Is.EqualTo(beforeStack - 1));
            });
        });

        var completionPulse = CreateTreatmentPulse(
            instanceId,
            actorId,
            doctor,
            intention,
            step,
            action,
            command,
            startedAt,
            startedAt.Advance(TimeSpan.FromSeconds(4)));
        await server.WaitAssertion(() =>
        {
            entities.EventBus.RaiseEvent(EventSource.Local, completionPulse);
            Assert.Multiple(() =>
            {
                Assert.That(completionPulse.Handled, Is.True);
                Assert.That(completionPulse.Completed, Is.True);
                Assert.That(completionPulse.Failure, Is.Null);
                Assert.That(entities.HasComponent<NCHumanVanillaActionComponent>(doctor), Is.False);
            });
        });
    }

    private static NCHumanActionExecutionEvent CreateTreatmentPulse(
        Guid instanceId,
        NCActorId actorId,
        EntityUid doctor,
        NCCognitionIntentionSnapshot intention,
        NCCognitionPlanStep step,
        NCHumanActionPrototype action,
        NCNeuralCommandProposal command,
        NCActiveWorldTime startedAt,
        NCActiveWorldTime now) =>
        new(
            instanceId,
            actorId,
            doctor,
            intention.IntentionId,
            Guid.NewGuid(),
            intention,
            step,
            intention.Target,
            action,
            command,
            startedAt,
            now);

    private static NCWoundRecord NewWound(Guid woundId) =>
        new()
        {
            WoundId = woundId,
            Prototype = "NCWoundBallistic",
            TargetZone = "NCZoneArmLeft",
            RegionCategory = "ArmLeft",
            WoundType = NCWoundType.Ballistic,
            TissueDepth = NCTissueDepth.Vascular,
            Severity = NCWoundSeverity.Severe,
            IntegrityDamage = FixedPoint2.New(10),
        };

    private static NCBleedingSourceRecord NewBleedingSource(Guid woundId) =>
        new()
        {
            SourceId = woundId,
            WoundId = woundId,
            TargetZone = "NCZoneArmLeft",
            RegionCategory = "ArmLeft",
            Channel = NCBleedingChannel.ExternalVenous,
            BaseRate = 6f,
            CurrentRate = 6f,
        };
}
