// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#nullable enable annotations

using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Pharmacology.Batch;
using Content.Server._NC.Medical.Pharmacology.Lab;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Containers.ItemSlots;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

/// <summary>
/// The process consumables and the simple injectors: a stabilizer is used up by the run that needs it, a catalyst
/// vial feeds the reactor, and a prefilled injector is only a sprite and a dose, with no syringe behaviour.
/// Integration tests run only on the owner's request.
/// </summary>
[TestFixture]
public sealed class NCPharmaConsumablesTest : GameTest
{
    private const string Analgesic = "NCDrugTraumaAnalgesic";
    private const string Autoinjector = "NCPharmaDeliveryAutoinjector";
    private const string Standard = "NCPharmaProductionStandard";
    private const string Stabilizer = "NCPharmaModifierStabilizer";

    [SidedDependency(Side.Server)] private NCPharmaBatchSystem _batches = default!;
    [SidedDependency(Side.Server)] private NCPharmaSynthesisSystem _synthesis = default!;
    [SidedDependency(Side.Server)] private NCPharmaReactorSystem _reactor = default!;
    [SidedDependency(Side.Server)] private NCLabSystem _lab = default!;
    [SidedDependency(Side.Server)] private ItemSlotsSystem _slots = default!;
    [SidedDependency(Side.Server)] private SharedMapSystem _maps = default!;
    [SidedDependency(Side.Server)] private SharedSolutionContainerSystem _solutions = default!;

    private static readonly ProtoId<ReagentPrototype> StabilizerReagent = "NCPharmaStabilizer";
    private static readonly ProtoId<ReagentPrototype> CatalystReagent = "NCPharmaCatalyst";

    private float Held(EntityUid vial, ProtoId<ReagentPrototype> reagent)
    {
        Assert.That(_solutions.TryGetDrainableSolution(vial, out _, out var contents), Is.True);
        return (float) contents!.GetTotalPrototypeQuantity(reagent);
    }

    public override PoolSettings PoolSettings => new() { Connected = false };

    private MapCoordinates Spot()
    {
        _maps.CreateMap(out var map);
        return new MapCoordinates(Vector2.Zero, map);
    }

    private NCPharmaFormulaPrototype Compose(params string[] modifiers)
    {
        Assert.That(_batches.TryComposeFormula(
            null, "Tester", Analgesic, null, Autoinjector, 6f, modifiers, Standard,
            out var composed, out var notice), Is.True, $"compose failed: {notice}");
        return composed!;
    }

    private EntityUid Canister(MapCoordinates at)
    {
        return SEntMan.SpawnEntity("NCPharmaSourceCanisterStreet", at);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void AFormulaWithAStabilizerCannotBeMadeWithoutOneAndUsesItUp()
    {
        var at = Spot();
        var station = SEntMan.SpawnEntity("NCPharmaSynthesizerClinical", at);
        var user = SEntMan.SpawnEntity("MobHuman", at);
        var formula = Compose(Stabilizer);
        Assert.That(_slots.TryInsert(station, "source", Canister(at), user), Is.True);

        var station1 = (station, SComp<NCPharmaSynthesizerComponent>(station));
        Assert.That(_synthesis.TryStartRun(station1, user, formula.ID, Standard, 5),
            Is.EqualTo(NCPharmaStationNotice.MissingAdditive));

        var vial = SEntMan.SpawnEntity("NCPharmaStabilizerVial", at);
        var before = Held(vial, StabilizerReagent);
        Assert.That(_slots.TryInsert(station, "additive", vial, user), Is.True);
        Assert.That(_synthesis.TryStartRun(station1, user, formula.ID, Standard, 5),
            Is.EqualTo(NCPharmaStationNotice.Started));
        Assert.That(SEntMan.EntityExists(vial), Is.True, "the vial stays; only the reagent is used up");
        Assert.That(Held(vial, StabilizerReagent), Is.EqualTo(before - 5f).Within(0.01f));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void AFormulaWithoutAStabilizerNeedsNoAdditive()
    {
        var at = Spot();
        var station = SEntMan.SpawnEntity("NCPharmaSynthesizerClinical", at);
        var user = SEntMan.SpawnEntity("MobHuman", at);
        var formula = Compose();
        Assert.That(_slots.TryInsert(station, "source", Canister(at), user), Is.True);
        Assert.That(_synthesis.TryStartRun((station, SComp<NCPharmaSynthesizerComponent>(station)), user, formula.ID, Standard, 5),
            Is.EqualTo(NCPharmaStationNotice.Started));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void TheReactorTakesTheCatalystVialAndWorksWithoutOne()
    {
        var at = Spot();
        var user = SEntMan.SpawnEntity("MobHuman", at);
        var formula = Compose();

        var empty = SEntMan.SpawnEntity("NCPharmaReactor", at);
        Assert.That(_slots.TryInsert(empty, "source", Canister(at), user), Is.True);
        Assert.That(_reactor.TryStartRun((empty, SComp<NCPharmaReactorComponent>(empty)), user, formula.ID, 3),
            Is.EqualTo(NCPharmaReactorNotice.Started));
        Assert.That(SComp<NCPharmaReactorRunComponent>(empty).CatalystLeft, Is.EqualTo(0), "no vial, no catalyst");

        var stocked = SEntMan.SpawnEntity("NCPharmaReactor", at);
        Assert.That(_slots.TryInsert(stocked, "source", Canister(at), user), Is.True);
        var vial = SEntMan.SpawnEntity("NCPharmaCatalystVial", at);
        var held = Held(vial, CatalystReagent);
        Assert.That(_slots.TryInsert(stocked, "catalyst", vial, user), Is.True);
        Assert.That(_reactor.TryStartRun((stocked, SComp<NCPharmaReactorComponent>(stocked)), user, formula.ID, 3),
            Is.EqualTo(NCPharmaReactorNotice.Started));
        Assert.That(SComp<NCPharmaReactorRunComponent>(stocked).CatalystLeft, Is.EqualTo(2));
        Assert.That(Held(vial, CatalystReagent), Is.EqualTo(held - 4f).Within(0.01f), "two charges take four units");
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void PrefilledInjectorsAreOnlyADoseAndASprite()
    {
        var at = Spot();
        var ids = new[]
        {
            "NCSyringeTraumaAnalgesic", "NCSyringeStimulant", "NCSyringeVasopressor", "NCSyringeProcoagulant",
            "NCSyringeSurgicalAnesthetic", "NCSyringeBroadAntibiotic", "NCSyringeSyntheticOxygenCarrier",
        };
        foreach (var id in ids)
        {
            var item = SEntMan.SpawnEntity(id, at);
            Assert.Multiple(() =>
            {
                Assert.That(SEntMan.HasComponent<InjectorComponent>(item), Is.False, $"{id}: no syringe behaviour");
                Assert.That(SEntMan.HasComponent<SolutionManagerComponent>(item), Is.False, $"{id}: no solution to pour");
                Assert.That(SComp<NCPharmaContainerComponent>(item).Doses, Is.EqualTo(1), id);
                Assert.That(SComp<NCPharmaContainerComponent>(item).BatchId, Is.Not.EqualTo(Guid.Empty), $"{id}: no batch");
            });
        }
    }

    private static readonly ProtoId<ReagentPrototype> WasteReagent = "NCPharmaWaste";

    [Test]
    [RunOnSide(Side.Server)]
    public void AStationPoursItsWasteIntoTheCanisterAndStopsWhenThereIsNoRoom()
    {
        var at = Spot();
        var user = SEntMan.SpawnEntity("MobHuman", at);
        var extractor = SEntMan.SpawnEntity("NCPharmaExtractor", at);
        var waste = SEntMan.System<NCPharmaWasteSystem>();

        Assert.That(waste.CanTake(extractor, 2f), Is.False, "no canister, no work");
        var canister = SEntMan.SpawnEntity("NCPharmaWasteCanister", at);
        Assert.That(_slots.TryInsert(extractor, "waste", canister, user), Is.True);
        Assert.That(waste.CanTake(extractor, 2f), Is.True);

        waste.Add(extractor, 2f);
        Assert.That(Held(canister, WasteReagent), Is.EqualTo(2f).Within(0.01f), "the waste is a real reagent");

        waste.Add(extractor, 100f);
        Assert.That(waste.CanTake(extractor, 2f), Is.False, "a full canister stops the station");
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void TheIncineratorBurnsTheWasteAndLeavesTheCanister()
    {
        var at = Spot();
        var user = SEntMan.SpawnEntity("MobHuman", at);
        var bin = SEntMan.SpawnEntity("NCPharmaWasteBin", at);
        var canister = SEntMan.SpawnEntity("NCPharmaWasteCanister", at);
        Assert.That(_solutions.TryGetDrainableSolution(canister, out var soln, out _), Is.True);
        Assert.That(_solutions.TryAddReagent(soln!.Value, WasteReagent, 20), Is.True);

        SEntMan.GetComponent<Content.Server.Power.Components.ApcPowerReceiverComponent>(bin).Powered = true;
        Assert.That(SEntMan.System<Content.Shared.Power.EntitySystems.SharedPowerReceiverSystem>().IsPowered(bin), Is.True, "the incinerator must count as powered");
        var used = new Content.Shared.Interaction.InteractUsingEvent(user, canister, bin, SEntMan.GetComponent<TransformComponent>(bin).Coordinates);
        SEntMan.EventBus.RaiseLocalEvent(bin, used);

        Assert.That(SEntMan.EntityExists(canister), Is.True, "the canister stays");
        Assert.That(Held(canister, WasteReagent), Is.EqualTo(0f), "the waste is burned");
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ASampleIsRealFluidAndPouringItOutSpoilsIt()
    {
        var vial = SEntMan.SpawnEntity("NCLabSampleVial", Spot());
        Assert.That(_lab.HasSampleFluid(vial), Is.False, "an empty vial is no sample");

        Assert.That(_solutions.TryGetDrainableSolution(vial, out var solution, out _), Is.True);
        Assert.That(_solutions.TryAddReagent(solution!.Value, "NCLabSampleFluid", 5f), Is.True);
        Assert.That(_lab.HasSampleFluid(vial), Is.True);

        _solutions.RemoveReagent(solution.Value, "NCLabSampleFluid", 5f);
        Assert.That(_lab.HasSampleFluid(vial), Is.False, "poured out, the sample is spoiled");
    }
}
