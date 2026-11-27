// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#nullable enable annotations

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

/// <summary>
/// The laboratory's registries are bounded, but the world lives for a long time: when one is full the oldest abandoned
/// work makes room, and only work still in use can stop the laboratory. It fills the registries, so the server is thrown
/// away after it. Integration tests run only on the owner's request.
/// </summary>
[TestFixture]
public sealed class NCPharmaFormulaLimitTest : GameTest
{
    private const string Autoinjector = "NCPharmaDeliveryAutoinjector";
    private const string Standard = "NCPharmaProductionStandard";
    private static readonly NCPharmaProductionInputs Inputs = new(10f, 0.9f, 0.9f, 0.9f, 0f);

    [SidedDependency(Side.Server)] private NCPharmaBatchSystem _batches = default!;
    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;
    [SidedDependency(Side.Server)] private SharedMapSystem _maps = default!;

    public override PoolSettings PoolSettings => new() { Connected = false, Fresh = true, Dirty = true };

    private NCPharmaTuningPrototype Tuning =>
        _prototypes.EnumeratePrototypes<NCPharmaTuningPrototype>().First(tuning => tuning.DefaultTuning);

    private List<string> Lines() => _batches.AllFormulas
        .Where(formula => formula.ID.StartsWith("NCRd", System.StringComparison.Ordinal))
        .Select(formula => formula.ID)
        .ToList();

    /// <summary>Composes new lines from pairs of cores, one per call, in a fixed order.</summary>
    private IEnumerator<(bool Made, NCPharmaRdNotice Notice)> Composer()
    {
        var cores = _batches.AvailableCores().Select(core => core.Id).ToList();
        for (var i = 0; i < cores.Count; i++)
        for (var j = i + 1; j < cores.Count; j++)
        {
            var concentration = _prototypes.Index<NCDrugProfilePrototype>(cores[i]).TherapeuticConcentration;
            var made = _batches.TryComposeFormula(null, "Tester", cores[i], cores[j], Autoinjector, concentration, [], Standard,
                out _, out var notice);
            yield return (made, notice);
        }
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void AFullLaboratoryRetiresItsOldestAbandonedLineAndRefusesOnlyWhenEveryLineIsInUse()
    {
        var limit = Tuning.MaximumLineages;
        var composer = Composer();
        for (var made = 0; made < limit; made++)
        {
            Assert.That(composer.MoveNext(), Is.True, "enough pairs of cores to fill the laboratory");
            Assert.That(composer.Current.Made, Is.True, $"line {made + 1} of {limit}");
        }

        var oldest = Lines().OrderBy(id => int.Parse(id[4..id.IndexOf('.')])).First();
        Assert.That(composer.MoveNext() && composer.Current.Made, Is.True, "a full laboratory still develops a new line");
        Assert.That(Lines(), Has.Count.EqualTo(limit), "the number of lines stays at the limit");
        Assert.That(Lines(), Does.Not.Contain(oldest), "the oldest line nothing was made from made room");

        // Every line is now in use: a batch was produced from each, so nothing may be retired.
        foreach (var line in Lines())
            Assert.That(_batches.TryCreateBatch(line, null, Inputs, "Tester", "Test", out _), Is.True);
        Assert.That(composer.MoveNext(), Is.True);
        Assert.That(composer.Current.Made, Is.False);
        Assert.That(composer.Current.Notice, Is.EqualTo(NCPharmaRdNotice.TooManyFormulas));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void AFullBatchRegistryRetiresTheOldestBatchNobodyCarries()
    {
        const string formula = "NCPharmaFormulaTraumaAnalgesic";
        Assert.That(_batches.TryCreateBatch(formula, null, Inputs, "Tester", "Test", out var oldest), Is.True);
        Assert.That(_batches.TryCreateBatch(formula, null, Inputs, "Tester", "Test", out var carried), Is.True);
        _maps.CreateMap(out var map);
        var pack = _batches.TrySpawnContainer("NCPharmaAutoinjector", new EntityCoordinates(_maps.GetMap(map), Vector2.Zero),
            carried!.BatchId, 1, "Tester");
        Assert.That(pack, Is.Not.Null);

        while (_batches.Batches.Count < Tuning.MaximumBatches)
            Assert.That(_batches.TryCreateBatch(formula, null, Inputs, "Tester", "Test", out _), Is.True);

        Assert.That(_batches.TryCreateBatch(formula, null, Inputs, "Tester", "Test", out var fresh), Is.True,
            "a full registry still records a new batch");
        Assert.That(_batches.Batches, Has.Count.EqualTo(Tuning.MaximumBatches));
        Assert.That(_batches.TryGetBatch(oldest!.BatchId, out _), Is.False, "the oldest batch nobody carries made room");
        Assert.That(_batches.TryGetBatch(carried.BatchId, out _), Is.True, "a batch with a pack in the world stays");
        Assert.That(_batches.TryGetBatch(fresh!.BatchId, out _), Is.True);
    }
}
