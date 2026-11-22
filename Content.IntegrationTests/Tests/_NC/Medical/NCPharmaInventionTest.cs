// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#nullable enable annotations

using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Pharmacology;
using Content.Server._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Containers.ItemSlots;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

/// <summary>
/// A player invents a compound in the research console from ingredient reagents in a beaker. Integration tests run
/// only on the owner's request.
/// </summary>
[TestFixture]
public sealed class NCPharmaInventionTest : GameTest
{
    [SidedDependency(Side.Server)] private NCPharmaRdSystem _console = default!;
    [SidedDependency(Side.Server)] private NCPharmaBatchSystem _batches = default!;
    [SidedDependency(Side.Server)] private SharedSolutionContainerSystem _solutions = default!;
    [SidedDependency(Side.Server)] private ItemSlotsSystem _slots = default!;
    [SidedDependency(Side.Server)] private SharedMapSystem _maps = default!;
    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;

    public override PoolSettings PoolSettings => new() { Connected = false };

    private (Entity<NCPharmaRdConsoleComponent> Console, EntityUid Beaker, EntityUid User) Setup(params (string Reagent, float Amount)[] contents)
    {
        _maps.CreateMap(out var map);
        var at = new MapCoordinates(Vector2.Zero, map);
        var user = SEntMan.SpawnEntity("MobHuman", at);
        var console = SEntMan.SpawnEntity("NCPharmaRdConsole", at);
        var beaker = SEntMan.SpawnEntity("Beaker", at);
        Assert.That(_solutions.TryGetSolution(beaker, "beaker", out var soln, out _), Is.True);
        foreach (var (reagent, amount) in contents)
            Assert.That(_solutions.TryAddReagent(soln!.Value, reagent, amount), Is.True);
        Assert.That(_slots.TryInsert(console, "reagents", beaker, user), Is.True, "the console takes a beaker");
        return ((console, SEntMan.GetComponent<NCPharmaRdConsoleComponent>(console)), beaker, user);
    }

    private float Held(EntityUid beaker, string reagent)
    {
        Assert.That(_solutions.TryGetSolution(beaker, "beaker", out _, out var solution), Is.True);
        return (float) solution!.GetTotalPrototypeQuantity(reagent);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ACompoundIsInventedFromTheBeakerAndSpendsTheIngredients()
    {
        var (console, beaker, user) = Setup(("Inaprovaline", 20f), ("Bicaridine", 20f), ("Ethanol", 10f));
        var notice = _console.TryInvent(console, user, ["Inaprovaline", "Bicaridine"]);

        Assert.That(notice, Is.EqualTo(NCPharmaRdNotice.Invented));
        Assert.Multiple(() =>
        {
            Assert.That(Held(beaker, "Inaprovaline"), Is.EqualTo(15f).Within(0.01f));
            Assert.That(Held(beaker, "Bicaridine"), Is.EqualTo(15f).Within(0.01f));
            Assert.That(Held(beaker, "Ethanol"), Is.EqualTo(10f), "what is not an ingredient is left alone");
        });

        var id = Content.Shared._NC.Medical.Pharmacology.Lab.NCPharmaInvention.IdFor(
            Content.Shared._NC.Medical.Pharmacology.Lab.NCPharmaInvention.KeyOf(["Inaprovaline", "Bicaridine"]));
        Assert.That(_prototypes.TryIndex<NCDrugProfilePrototype>(id, out var drug), Is.True, "a real drug profile exists");
        Assert.That(NCPharmacologySystem.ValidateDrug(drug!), Is.True);
        Assert.That(drug!.Category, Is.EqualTo(NCDrugCategory.Analgesic));
        Assert.That(_batches.TryGetFormula($"NCPharmaFormulaIsolated.{id}", out var formula), Is.True, "and a formula for it");
        Assert.That(formula!.RegulatoryStatus, Is.EqualTo(NCPharmaRegulatoryStatus.Experimental));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void TheSameIngredientsMakeTheSameCompoundAgain()
    {
        var (console, _, user) = Setup(("Ephedrine", 30f), ("Synaptizine", 30f));
        Assert.That(_console.TryInvent(console, user, ["Ephedrine", "Synaptizine"]), Is.EqualTo(NCPharmaRdNotice.Invented));
        var first = console.Comp.Selected;
        Assert.That(_console.TryInvent(console, user, ["Synaptizine", "Ephedrine"]), Is.EqualTo(NCPharmaRdNotice.Invented));
        Assert.That(console.Comp.Selected, Is.EqualTo(first), "order does not matter, and the compound is not made twice");
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void NothingIsSpentWhenTheBeakerLacksAnIngredientOrTheMixDoesNothing()
    {
        var (console, beaker, user) = Setup(("Inaprovaline", 20f), ("Bicaridine", 2f), ("Charcoal", 20f));
        Assert.That(_console.TryInvent(console, user, ["Inaprovaline", "Bicaridine"]), Is.EqualTo(NCPharmaRdNotice.NoReagents));
        Assert.That(_console.TryInvent(console, user, ["Inaprovaline", "Water"]), Is.EqualTo(NCPharmaRdNotice.NoReagents));
        Assert.That(_console.TryInvent(console, user, ["Inaprovaline"]), Is.Not.EqualTo(NCPharmaRdNotice.Invented));
        Assert.That(Held(beaker, "Inaprovaline"), Is.EqualTo(20f), "a refused invention costs nothing");
        Assert.That(Held(beaker, "Charcoal"), Is.EqualTo(20f));
    }
}
