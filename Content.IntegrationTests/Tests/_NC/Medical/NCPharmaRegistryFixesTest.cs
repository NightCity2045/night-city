// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

#nullable enable annotations

using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Pharmacology;
using Content.Server._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

/// <summary>
/// The cold chain belongs to each pack, a refused dose waits instead of vanishing, and the notebook follows everything
/// the laboratory developed. Integration tests run only on the owner's request.
/// </summary>
[TestFixture]
public sealed class NCPharmaRegistryFixesTest : GameTest
{
    private const string Autoinjector = "NCPharmaDeliveryAutoinjector";
    private const string Standard = "NCPharmaProductionStandard";
    private static readonly NCPharmaProductionInputs Inputs = new(10f, 0.9f, 0.9f, 0.9f, 0f);

    [SidedDependency(Side.Server)] private NCPharmaBatchSystem _batches = default!;
    [SidedDependency(Side.Server)] private NCPharmaDepotSystem _depot = default!;
    [SidedDependency(Side.Server)] private NCPharmacologySystem _pharmacology = default!;
    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;
    [SidedDependency(Side.Server)] private SharedMapSystem _maps = default!;

    public override PoolSettings PoolSettings => new() { Connected = false, Fresh = true, Dirty = true };

    private EntityCoordinates Place()
    {
        _maps.CreateMap(out var map);
        return new EntityCoordinates(_maps.GetMap(map), Vector2.Zero);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void APackLeftOutSpoilsWithoutSpoilingItsWholeBatch()
    {
        // A street run of the field stimulant is unstable enough to spoil outside the cold.
        Assert.That(_batches.TryCreateBatch("NCPharmaFormulaFieldStimulant", "NCPharmaProductionStreet", Inputs, "Tester", "Test",
            out var batch), Is.True);
        var at = Place();
        var outside = _batches.TrySpawnContainer("NCPharmaAutoinjector", at, batch!.BatchId, 1, "Tester");
        Assert.That(outside, Is.Not.Null);
        var purity = batch.Actual.Purity;

        _batches.Update(3600f);

        var pack = SEntMan.GetComponent<NCPharmaContainerComponent>(outside!.Value);
        Assert.That(pack.PurityLoss, Is.GreaterThan(0f), "the pack left in the warmth lost purity");
        Assert.That(batch.Actual.Purity, Is.EqualTo(purity), "the batch itself, and every other pack of it, did not");
        var fresh = _batches.TrySpawnContainer("NCPharmaAutoinjector", at, batch.BatchId, 1, "Tester");
        Assert.That(SEntMan.GetComponent<NCPharmaContainerComponent>(fresh!.Value).PurityLoss, Is.Zero);
        Assert.That(_batches.PackQuality(batch, pack).Purity, Is.LessThan(purity));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ADoseABodyHasNoRoomForWaitsInsteadOfVanishing()
    {
        var body = SEntMan.SpawnEntity("MobHuman", Place());
        var drugs = _prototypes.EnumeratePrototypes<NCDrugProfilePrototype>()
            .Where(NCPharmacologySystem.ValidateDrug)
            .OrderBy(drug => drug.ID, System.StringComparer.Ordinal)
            .ToList();
        var applied = 0;
        foreach (var drug in drugs)
        {
            var result = _pharmacology.ApplyDose(body, drug.ID, drug.TherapeuticConcentration);
            if (result.CapacityReached)
                break;
            applied++;
        }

        var waiting = drugs[applied];
        Assert.That(_pharmacology.ApplyDose(body, waiting.ID, waiting.TherapeuticConcentration).CapacityReached, Is.True,
            "the body is full of compounds");
        Assert.That(_depot.TryDeposit(body, waiting.ID, waiting.TherapeuticConcentration, System.TimeSpan.Zero), Is.True,
            "the dose is taken in, not thrown away");
        Assert.That(SEntMan.GetComponent<NCPharmaDepotComponent>(body).Entries.Any(entry => entry.Drug == waiting.ID), Is.True,
            "and waits in the depot until the body has room");
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void TheNotebookFollowsANewLineFromItsFirstBatch()
    {
        var core = _batches.AvailableCores().First();
        var concentration = _prototypes.Index(core).TherapeuticConcentration;
        Assert.That(_batches.TryComposeFormula(null, "Tester", core, null, Autoinjector, concentration, [], Standard,
            out var line, out _), Is.True);
        Assert.That(line!.DerivedFrom, Is.Null, "a new line has no parent");
        Assert.That(_batches.TryCreateBatch(line.ID, null, Inputs, "Tester", "Test", out var batch), Is.True);

        var journal = _batches.BuildJournalSummary(line.ID, 50);
        Assert.That(journal.Any(entry => entry.Kind == NCPharmaJournalKind.Produced && entry.BatchCode == batch!.BatchCode), Is.True,
            "the first batch of a new line is written to its notebook");
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ASpilledDoseLeavesThePackAndIsCountedByItsBatch()
    {
        Assert.That(_batches.TryCreateBatch("NCPharmaFormulaTraumaAnalgesic", null, Inputs, "Tester", "Test", out var batch), Is.True);
        var pack = _batches.TrySpawnContainer("NCPharmaAutoinjector", Place(), batch!.BatchId, 3, "Tester");
        Assert.That(_batches.TrySpillDose(pack!.Value, "Tester"), Is.True);
        Assert.That(SEntMan.GetComponent<NCPharmaContainerComponent>(pack.Value).Doses, Is.EqualTo(2));
        Assert.That(batch.DosesLost, Is.EqualTo(1));
        Assert.That(batch.DosesAdministered + batch.DosesSampled + batch.DosesLost, Is.LessThanOrEqualTo(batch.DosesPacked));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void TwoPeopleWithTheSameNameDoNotShareTheirInfections()
    {
        var at = Place();
        var first = SEntMan.SpawnEntity("MobHuman", at);
        var second = SEntMan.SpawnEntity("MobHuman", at);
        SEntMan.System<MetaDataSystem>().SetEntityName(first, "John Doe");
        SEntMan.System<MetaDataSystem>().SetEntityName(second, "John Doe");
        Assert.That(_pharmacology.TryEnsureState(first) && _pharmacology.TryEnsureState(second), Is.True);
        var a = SEntMan.GetComponent<NCPharmacologyComponent>(first).StrainSeed;
        var b = SEntMan.GetComponent<NCPharmacologyComponent>(second).StrainSeed;
        Assert.That(a, Is.Not.Zero);
        Assert.That(b, Is.Not.Zero);
        Assert.That(a, Is.Not.EqualTo(b), "each body draws its own seed, the name does not decide the strain");
    }
}
