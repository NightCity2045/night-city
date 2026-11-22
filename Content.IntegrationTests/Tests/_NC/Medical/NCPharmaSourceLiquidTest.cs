// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#nullable enable annotations

using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology.Lab;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._NC.Medical;

/// <summary>
/// Raw material and source canisters are liquids in real containers. Purity and contamination are the composition,
/// the substrate is made from ordinary reagents, and the supply crate carries what a first brew needs.
/// Integration tests run only on the owner's request.
/// </summary>
[TestFixture]
public sealed class NCPharmaSourceLiquidTest : GameTest
{
    [SidedDependency(Side.Server)] private NCPharmaSourceSystem _sources = default!;
    [SidedDependency(Side.Server)] private SharedSolutionContainerSystem _solutions = default!;
    [SidedDependency(Side.Server)] private SharedMapSystem _maps = default!;

    public override PoolSettings PoolSettings => new() { Connected = false };

    private MapCoordinates Spot()
    {
        _maps.CreateMap(out var map);
        return new MapCoordinates(Vector2.Zero, map);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void CompositionReadsBackAsPurityAndContamination()
    {
        var canister = SEntMan.SpawnEntity("NCPharmaSourceCanister", Spot());
        Assert.That(_sources.TryRead(canister, out _), Is.False, "an empty canister holds no source");

        Assert.That(_sources.Fill(canister, "NCFeedstockCardio", 20f, 0.8f, 0.1f), Is.True);
        Assert.That(_sources.TryRead(canister, out var reading), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(reading.Feedstock, Is.EqualTo("NCFeedstockCardio"));
            Assert.That(reading.Units, Is.EqualTo(20f).Within(0.1f));
            Assert.That(reading.Purity, Is.EqualTo(0.8f).Within(0.02f));
            Assert.That(reading.Contaminants, Is.EqualTo(0.1f).Within(0.02f));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void SpendingMaterialKeepsPurityAndLeavesTheCanisterUsable()
    {
        var canister = SEntMan.SpawnEntity("NCPharmaSourceCanisterStreet", Spot());
        Assert.That(_sources.TryRead(canister, out var before), Is.True);

        _sources.Consume(before, 10f);
        Assert.That(_sources.TryRead(canister, out var after), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(after.Units, Is.EqualTo(before.Units - 10f).Within(0.2f));
            Assert.That(after.Purity, Is.EqualTo(before.Purity).Within(0.02f));
            Assert.That(after.Contaminants, Is.EqualTo(before.Contaminants).Within(0.02f));
        });

        _sources.Consume(after, after.Units);
        Assert.That(_sources.TryRead(canister, out _), Is.False, "a spent canister is just empty, not deleted");
        Assert.That(SEntMan.EntityExists(canister), Is.True);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void PouringInAnotherKindDilutesTheCanister()
    {
        var canister = SEntMan.SpawnEntity("NCPharmaSourceCanisterClinical", Spot());
        Assert.That(_sources.TryRead(canister, out var clean), Is.True);

        Assert.That(_solutions.TryGetDrainableSolution(canister, out var soln, out _), Is.True);
        Assert.That(_solutions.TryAddReagent(soln!.Value, "NCPharmaSourceHemato", 15f), Is.True);
        Assert.That(_sources.TryRead(canister, out var mixed), Is.True);
        Assert.That(mixed.Purity, Is.LessThan(clean.Purity), "a second kind counts as impurity");
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void SubstrateIsMadeFromOrdinaryReagents()
    {
        var recipes = new (string Substrate, (string Reagent, float Amount)[] Inputs)[]
        {
            ("NCPharmaSubstrateNeuro", [("Protein", 20), ("Lithium", 10), ("Phosphorus", 10)]),
            ("NCPharmaSubstrateCardio", [("Blood", 20), ("Potassium", 10), ("Sugar", 10)]),
            ("NCPharmaSubstrateHemato", [("Blood", 20), ("Iron", 10), ("Protein", 10)]),
            ("NCPharmaSubstrateMicrobial", [("Nutriment", 20), ("Sugar", 10), ("Ammonia", 10)]),
            ("NCPharmaSubstrateImmune", [("Protein", 20), ("Nutriment", 10), ("Sodium", 10)]),
        };

        _maps.CreateMap(out var map);
        foreach (var (substrate, inputs) in recipes)
        {
            var beaker = SEntMan.SpawnEntity("Beaker", new MapCoordinates(Vector2.Zero, map));
            Assert.That(_solutions.TryGetSolution(beaker, "beaker", out var soln, out _), Is.True);
            _solutions.SetTemperature(soln!.Value, 320f);
            foreach (var (reagent, amount) in inputs)
                Assert.That(_solutions.TryAddReagent(soln.Value, reagent, amount), Is.True);

            Assert.That((float) soln.Value.Comp.Solution.GetTotalPrototypeQuantity(substrate),
                Is.GreaterThan(30f), $"{substrate} must come out of its recipe");
        }
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ReadyMadeRawMaterialIsAJugOfBroth()
    {
        foreach (var kind in new[] { "Neuro", "Cardio", "Hemato", "Microbial", "Immune" })
        {
            foreach (var grade in new[] { "Biomass", "Corporate" })
            {
                var raw = SEntMan.SpawnEntity($"NCPharmaRaw{grade}{kind}", Spot());
                Assert.That(_solutions.TryGetDrainableSolution(raw, out _, out var contents), Is.True);
                Assert.That((float) contents!.GetTotalPrototypeQuantity($"NCPharmaBroth{kind}"), Is.GreaterThan(30f));
            }
        }
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void TheBrewingCrateHoldsWhatAFirstBrewNeeds()
    {
        var crate = SEntMan.SpawnEntity("NCPharmaKitBrewing", Spot());
        Assert.That(SEntMan.EntityExists(crate), Is.True);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void TheExtractorPoursABrewIntoANewCanister()
    {
        var at = Spot();
        var user = SEntMan.SpawnEntity("MobHuman", at);
        var extractor = SEntMan.SpawnEntity("NCPharmaExtractor", at);
        var slots = SEntMan.System<Content.Shared.Containers.ItemSlots.ItemSlotsSystem>();
        var wasteCanister = SEntMan.SpawnEntity("NCPharmaWasteCanister", at);
        Assert.That(slots.TryInsert(extractor, "waste", wasteCanister, user), Is.True);
        SEntMan.GetComponent<Content.Server.Power.Components.ApcPowerReceiverComponent>(extractor).Powered = true;

        var jug = SEntMan.SpawnEntity("NCPharmaRawCorporateHemato", at);
        Assert.That(slots.TryInsert(extractor, "material", jug, user), Is.True);

        var done = new NCPharmaExtractDoAfterEvent();
        done.DoAfter = new Content.Shared.DoAfter.DoAfter(0,
            new Content.Shared.DoAfter.DoAfterArgs(SEntMan, user, TimeSpan.FromSeconds(1), done, extractor, target: extractor),
            TimeSpan.Zero);
        SEntMan.EventBus.RaiseLocalEvent(extractor, done);

        Assert.That(_solutions.TryGetDrainableSolution(jug, out _, out var left), Is.True);
        Assert.That((float) left!.Volume, Is.EqualTo(0f), "the jug is drawn out");

        var canisterFound = false;
        var query = SEntMan.EntityQueryEnumerator<Content.Shared._NC.Medical.Pharmacology.Batch.NCPharmaSourceComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            if (!_sources.TryRead(uid, out var reading))
                continue;
            if (reading.Feedstock != "NCFeedstockHemato")
                continue; // canisters of other tests share the pooled server

            canisterFound = true;
            Assert.Multiple(() =>
            {
                Assert.That(reading.Units, Is.GreaterThan(5f));
                Assert.That(reading.Purity, Is.InRange(0.5f, 0.95f));
            });
        }

        Assert.That(canisterFound, Is.True, "extraction must produce a canister holding source material");
    }
}
