// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.Lathe;
using Content.Server.Power.Components;
using Content.Shared.Lathe;
using Content.Shared.Materials;
using Content.Shared.Research.Prototypes;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._NC.Meta;

[TestFixture]
[TestOf(typeof(LatheSystem))]
public sealed class NCMetaLatheAutomationTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = false };
    [SidedDependency(Side.Server)] private LatheSystem _lathes = default!;
    [SidedDependency(Side.Server)] private SharedMaterialStorageSystem _materials = default!;

    [TestPrototypes]
    private const string Prototypes = """
        - type: latheRecipe
          id: NCMetaTestInstantRecipe
          result: Wirecutter
          completetime: 0
          materials:
            Steel: 200
        - type: latheRecipe
          id: NCMetaTestTimedRecipe
          result: Wirecutter
          completetime: 5
          materials:
            Steel: 200
        - type: latheRecipePack
          id: NCMetaTestAutomationRecipes
          recipes:
          - NCMetaTestInstantRecipe
          - NCMetaTestTimedRecipe
        - type: entity
          parent: Autolathe
          id: NCMetaTestAutomationLathe
          components:
          - type: ApcPowerReceiver
            needsPower: false
          - type: Lathe
            staticPacks:
            - NCMetaTestAutomationRecipes
        """;

    private EntityUid Machine(EntityCoordinates coordinates)
    {
        var uid = SSpawnAtPosition("NCMetaTestAutomationLathe", coordinates);
        // Arrange powered hardware. This suite tests real material debit/output, rather than the power grid.
        SComp<ApcPowerReceiverComponent>(uid).Powered = true;
        Assert.That(_materials.TryChangeMaterialAmount(uid, "Steel", 600), Is.True);
        return uid;
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SynchronousCancellationInsideActualOutputNeverRefundsTheInFlightItem(bool cancelInsideOutput)
    {
        var map = await Pair.CreateTestMap();
        await Server.WaitAssertion(() =>
        {
            var uid = Machine(map.GridCoords);
            var id = Guid.NewGuid();
            var outcomes = new List<NCLatheAutomationOutcome>();
            var products = 0;
            void OnProduced(Entity<MetaDataComponent> ent)
            {
                if (ent.Comp.EntityPrototype?.ID != "Wirecutter") return;
                products++;
                if (cancelInsideOutput && products == 1)
                    Assert.That(_lathes.TryCancelAutomationJob(uid, id), Is.True);
            }
            void OnCompleted(NCLatheAutomationOutcome result) { if (result.OperationId == id) outcomes.Add(result); }
            SEntMan.EntityInitialized += OnProduced;
            _lathes.AutomationCompleted += OnCompleted;
            try
            {
                var recipe = SProtoMan.Index<LatheRecipePrototype>("NCMetaTestInstantRecipe");
                Assert.That(_lathes.TryStartAutomationJob(uid, id, recipe, 3, () => true), Is.True);
                Assert.That(outcomes, Has.Count.EqualTo(1));
                Assert.That(products, Is.EqualTo(cancelInsideOutput ? 1 : 3));
                Assert.That(_materials.GetMaterialAmount(uid, "Steel"), Is.EqualTo(cancelInsideOutput ? 400 : 0));
                Assert.That(outcomes[0].Applied, Is.EqualTo(!cancelInsideOutput));
                Assert.That(outcomes[0].Completed, Is.EqualTo(cancelInsideOutput ? 0 : 3));
                Assert.That(outcomes[0].Error, Is.EqualTo(cancelInsideOutput ? "OutcomeUnknown" : string.Empty));
                Assert.That(SComp<LatheComponent>(uid).Queue, Is.Empty);
                Assert.That(SComp<LatheComponent>(uid).CurrentRecipe, Is.Null);
            }
            finally
            {
                SEntMan.EntityInitialized -= OnProduced;
                _lathes.AutomationCompleted -= OnCompleted;
            }
        });
    }

    [Test]
    public async Task ExactOperationCancellationRefundsOnlyItsUnproducedReservedBatch()
    {
        var map = await Pair.CreateTestMap();
        await Server.WaitAssertion(() =>
        {
            var uid = Machine(map.GridCoords);
            var id = Guid.NewGuid();
            var recipe = SProtoMan.Index<LatheRecipePrototype>("NCMetaTestTimedRecipe");
            Assert.That(_lathes.TryStartAutomationJob(uid, id, recipe, 3, () => true), Is.True);
            Assert.That(_materials.GetMaterialAmount(uid, "Steel"), Is.Zero);
            Assert.That(_lathes.TryCancelAutomationJob(uid, Guid.NewGuid()), Is.False);
            Assert.That(_materials.GetMaterialAmount(uid, "Steel"), Is.Zero);
            Assert.That(_lathes.TryCancelAutomationJob(uid, id), Is.True);
            Assert.That(_materials.GetMaterialAmount(uid, "Steel"), Is.EqualTo(600));
            Assert.That(_lathes.TryCancelAutomationJob(uid, id), Is.False);
            Assert.That(_materials.GetMaterialAmount(uid, "Steel"), Is.EqualTo(600));
            Assert.That(SComp<LatheComponent>(uid).Queue, Is.Empty);
            Assert.That(SComp<LatheComponent>(uid).CurrentRecipe, Is.Null);
        });
    }
}
