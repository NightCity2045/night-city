// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#nullable enable annotations

using System.Collections.Generic;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared.Containers.ItemSlots;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._NC.Medical;

/// <summary>
/// What a laboratory has studied lives on a chip in the research console. Without the chip, or with a blank one,
/// everything is unstudied. Integration tests run only on the owner's request.
/// </summary>
[TestFixture]
public sealed class NCPharmaResearchChipTest : GameTest
{
    private const string Analgesic = "NCDrugTraumaAnalgesic";
    private const string Autoinjector = "NCPharmaDeliveryAutoinjector";
    private const string Standard = "NCPharmaProductionStandard";

    [SidedDependency(Side.Server)] private NCPharmaRdSystem _console = default!;
    [SidedDependency(Side.Server)] private NCPharmaBatchSystem _batches = default!;
    [SidedDependency(Side.Server)] private ItemSlotsSystem _slots = default!;
    [SidedDependency(Side.Server)] private SharedMapSystem _maps = default!;

    public override PoolSettings PoolSettings => new() { Connected = false };

    private (Entity<NCPharmaRdConsoleComponent> Console, EntityUid User, MapCoordinates At) Setup()
    {
        _maps.CreateMap(out var map);
        var at = new MapCoordinates(Vector2.Zero, map);
        var user = SEntMan.SpawnEntity("MobHuman", at);
        var console = SEntMan.SpawnEntity("NCPharmaRdConsole", at);
        return ((console, SEntMan.GetComponent<NCPharmaRdConsoleComponent>(console)), user, at);
    }

    private bool Compose(IReadOnlySet<string>? researched) =>
        _batches.TryComposeFormula(null, "Tester", Analgesic, null, Autoinjector, 6f, [], Standard, out _, out _, researched);

    [Test]
    [RunOnSide(Side.Server)]
    public void AConsoleStartsWithABlankChipAndNothingIsStudied()
    {
        var (console, _, _) = Setup();
        Assert.That(_console.TryGetChip(console, out var chip), Is.True, "the console comes with a chip");
        Assert.That(chip.Comp.Researched, Is.Empty, "a blank chip has studied nothing");
        Assert.That(Compose(chip.Comp.Researched), Is.False, "nothing studied, nothing can be built on");
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void WhatTheLaboratoryLearnsIsWrittenToTheChipAndOnlyToIt()
    {
        var (console, user, at) = Setup();
        Assert.That(_console.TryGetChip(console, out var chip), Is.True);
        var other = SEntMan.SpawnEntity("NCPharmaRdConsole", at);
        var otherChip = SEntMan.GetComponent<NCPharmaResearchChipComponent>(
            _slots.GetItemOrNull(other, "chip") ?? throw new InvalidOperationException("no chip"));

        var removed = _slots.GetItemOrNull(console, "chip");
        Assert.That(removed, Is.Not.Null);
        Assert.That(_slots.TryEject(console, "chip", user, out _), Is.True, "the chip can be taken out");
        _batches.RaiseResearch([NCPharmaNodeIds.Core(Analgesic)]);
        Assert.That(chip.Comp.Researched, Is.Empty, "a chip outside a console learns nothing");
        Assert.That(otherChip.Researched, Does.Contain(NCPharmaNodeIds.Core(Analgesic)), "a chip in a console does");
        Assert.That(_console.TryGetChip(console, out _), Is.False, "without the chip the console has no research");
        Assert.That(_console.TryInvent(console, user, ["Inaprovaline", "Bicaridine"]), Is.EqualTo(NCPharmaRdNotice.NoChip));

        Assert.That(_slots.TryInsert(console, "chip", removed!.Value, user), Is.True, "put back, its record is back");
        Assert.That(_console.TryGetChip(console, out var back), Is.True);
        Assert.That(back.Comp.Researched, Is.Empty, "it learnt nothing while it was out");
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ACoreCanBeBuiltOnOnlyOnceTheChipHasStudiedIt()
    {
        var (console, _, _) = Setup();
        Assert.That(_console.TryGetChip(console, out var chip), Is.True);
        Assert.That(Compose(chip.Comp.Researched), Is.False);

        _batches.RaiseResearch([NCPharmaNodeIds.Core(Analgesic)]);
        Assert.That(chip.Comp.Researched, Does.Contain(NCPharmaNodeIds.Core(Analgesic)));
        Assert.That(Compose(chip.Comp.Researched), Is.True, "once studied it can be built on");
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void AStockChipComesWithTheStockFormulasStudied()
    {
        _maps.CreateMap(out var map);
        var chip = SEntMan.SpawnEntity("NCPharmaResearchChipStock", new MapCoordinates(Vector2.Zero, map));
        var researched = SEntMan.GetComponent<NCPharmaResearchChipComponent>(chip).Researched;
        Assert.That(researched, Does.Contain(NCPharmaNodeIds.Core(Analgesic)));
        Assert.That(researched, Has.None.Matches<string>(node => !NCPharmaNodeIds.IsCore(node)),
            "a chip studies compounds only; forms, modifiers and modes are open to every laboratory");
        Assert.That(Compose(researched), Is.True);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void QualityControlOfARealBatchTeachesTheBlankChipItsFormula()
    {
        var (console, _, _) = Setup();
        Assert.That(_console.TryGetChip(console, out var chip), Is.True);
        Assert.That(chip.Comp.Researched, Is.Empty);

        Assert.That(_batches.TryCreateBatch(
            "NCPharmaFormulaTraumaAnalgesic", null, new NCPharmaProductionInputs(10f, 0.9f, 0.9f, 0.9f, 0f),
            "Tester", "Test", out var batch), Is.True);
        Assert.That(_batches.TryPerformQc(batch!.BatchId, "Tester", NCPharmaQcTests.All, out _, site: console.Owner), Is.True);

        Assert.That(chip.Comp.Researched, Does.Contain(NCPharmaNodeIds.Core(Analgesic)), "the core is now studied");
        Assert.That(Compose(chip.Comp.Researched), Is.True, "so a formula can be built on it");
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void WorkTeachesOnlyTheConsolesOfTheLaboratoryWhereItWasDone()
    {
        var (console, _, at) = Setup();
        Assert.That(_console.TryGetChip(console, out var near), Is.True);
        var far = SEntMan.SpawnEntity("NCPharmaRdConsole", new MapCoordinates(at.Position + new Vector2(100f, 0f), at.MapId));
        _maps.CreateMap(out var otherMap);
        var elsewhere = SEntMan.SpawnEntity("NCPharmaRdConsole", new MapCoordinates(Vector2.Zero, otherMap));
        var analyzer = SEntMan.SpawnEntity("NCPharmaRdConsole", new MapCoordinates(at.Position + new Vector2(3f, 0f), at.MapId));

        _batches.RecordAnalysis(Analgesic, Content.Shared._NC.Medical.Pharmacology.Lab.NCAnalysisTier.Chemical, analyzer);

        Assert.That(near.Comp.Researched, Does.Contain(NCPharmaNodeIds.Core(Analgesic)), "a console of the same laboratory learns it");
        foreach (var other in new[] { far, elsewhere })
        {
            var chip = SEntMan.GetComponent<NCPharmaResearchChipComponent>(
                _slots.GetItemOrNull(other, "chip") ?? throw new InvalidOperationException("no chip"));
            Assert.That(chip.Researched, Is.Empty, "a laboratory across the city learns nothing from it");
        }
    }
}
