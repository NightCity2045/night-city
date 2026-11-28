// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

#nullable enable annotations

using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared.Containers.ItemSlots;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

/// <summary>
/// A formula of two compounds made from different raw materials takes each compound from its own canister: the
/// synthesizer and the reactor have a second raw-material slot. Integration tests run only on the owner's request.
/// </summary>
[TestFixture]
public sealed class NCPharmaTwoSourceTest : GameTest
{
    private const string Autoinjector = "NCPharmaDeliveryAutoinjector";
    private const string Standard = "NCPharmaProductionStandard";
    private const string Canister = "NCPharmaSourceCanisterStreet";

    [SidedDependency(Side.Server)] private NCPharmaBatchSystem _batches = default!;
    [SidedDependency(Side.Server)] private NCPharmaSourceSystem _sources = default!;
    [SidedDependency(Side.Server)] private NCPharmaSynthesisSystem _synthesis = default!;
    [SidedDependency(Side.Server)] private NCPharmaReactorSystem _reactor = default!;
    [SidedDependency(Side.Server)] private ItemSlotsSystem _slots = default!;
    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;
    [SidedDependency(Side.Server)] private SharedMapSystem _maps = default!;

    public override PoolSettings PoolSettings => new() { Connected = false, Fresh = true, Dirty = true };

    private MapCoordinates Spot()
    {
        _maps.CreateMap(out var map);
        return new MapCoordinates(Vector2.Zero, map);
    }

    /// <summary>A new line of two compounds that no single raw material makes, and the main material of each.</summary>
    private (NCPharmaFormulaPrototype Formula, string First, string Second) ComposeSplitPair()
    {
        var cores = _batches.AvailableCores().Select(core => _prototypes.Index(core)).ToList();
        foreach (var a in cores)
        foreach (var b in cores)
        {
            if (a.ID == b.ID || a.Feedstock is not { } first || b.Feedstock is not { } second || first == second)
                continue;
            if (!_batches.TryComposeFormula(null, "Tester", a.ID, b.ID, Autoinjector, a.TherapeuticConcentration, [],
                    Standard, out var formula, out _))
            {
                continue;
            }

            if (_batches.ResolveFeedstockYield(formula!, first.Id) > 0f ||
                _batches.ResolveFeedstockYield(formula!, second.Id) > 0f)
            {
                continue;
            }

            return (formula!, first.Id, second.Id);
        }

        Assert.Fail("no pair of compounds needs two raw materials");
        return default;
    }

    private EntityUid Fill(MapCoordinates at, string feedstock, float units, float purity, float contaminants)
    {
        var canister = SEntMan.SpawnEntity(Canister, at);
        Assert.That(_sources.Fill(canister, feedstock, units, purity, contaminants), Is.True);
        return canister;
    }

    private float Units(EntityUid canister)
    {
        Assert.That(_sources.TryRead(canister, out var reading), Is.True);
        return reading.Units;
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void TheSynthesizerMakesEachCompoundFromItsOwnCanister()
    {
        var at = Spot();
        var user = SEntMan.SpawnEntity("MobHuman", at);
        var station = SEntMan.SpawnEntity("NCPharmaSynthesizerClinical", at);
        var entity = (station, SComp<NCPharmaSynthesizerComponent>(station));
        var (formula, first, second) = ComposeSplitPair();

        var clean = Fill(at, first, 30f, 0.9f, 0.02f);
        Assert.That(_slots.TryInsert(station, "source", clean, user), Is.True);
        Assert.That(_synthesis.TryStartRun(entity, user, formula.ID, Standard, 5),
            Is.EqualTo(NCPharmaStationNotice.WrongFeedstock), "one material makes only one of the compounds");

        var shallow = Fill(at, second, 3f, 0.7f, 0.1f);
        Assert.That(_slots.TryInsert(station, "secondSource", shallow, user), Is.True);
        Assert.That(_synthesis.TryStartRun(entity, user, formula.ID, Standard, 5),
            Is.EqualTo(NCPharmaStationNotice.NotEnoughSource), "the second canister holds too little for the run");

        Assert.That(_slots.TryEject(station, "secondSource", user, out _), Is.True);
        var dirty = Fill(at, second, 30f, 0.7f, 0.1f);
        Assert.That(_slots.TryInsert(station, "secondSource", dirty, user), Is.True);
        var cleanBefore = Units(clean);
        var dirtyBefore = Units(dirty);
        Assert.That(_synthesis.TryStartRun(entity, user, formula.ID, Standard, 5), Is.EqualTo(NCPharmaStationNotice.Started));

        Assert.That(Units(clean), Is.EqualTo(cleanBefore - 5f).Within(0.05f), "each canister gives the units of the run");
        Assert.That(Units(dirty), Is.EqualTo(dirtyBefore - 5f).Within(0.05f));
        var inputs = SComp<NCPharmaSynthesisRunComponent>(station).Inputs;
        Assert.That(inputs.InputPurity, Is.EqualTo(0.7f).Within(0.02f), "the worse canister sets the purity");
        Assert.That(inputs.Contaminants, Is.EqualTo(0.1f).Within(0.02f), "and the dirtier one the contamination");
        Assert.That(inputs.InputUnits, Is.EqualTo(5f).Within(0.01f), "both materials are main ones: full yield");
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void AFormulaOneCanisterServesTakesNothingFromTheOther()
    {
        var at = Spot();
        var user = SEntMan.SpawnEntity("MobHuman", at);
        var station = SEntMan.SpawnEntity("NCPharmaSynthesizerClinical", at);
        var (pair, first, _) = ComposeSplitPair();
        var core = _prototypes.Index<NCDrugProfilePrototype>(pair.Compound);
        Assert.That(_batches.TryComposeFormula(null, "Tester", core.ID, null, Autoinjector, core.TherapeuticConcentration, [],
            Standard, out var single, out _), Is.True);

        var spare = Fill(at, first, 30f, 0.6f, 0.2f);
        var main = Fill(at, first, 30f, 0.9f, 0.02f);
        Assert.That(_slots.TryInsert(station, "source", main, user), Is.True);
        Assert.That(_slots.TryInsert(station, "secondSource", spare, user), Is.True);
        Assert.That(_synthesis.TryStartRun((station, SComp<NCPharmaSynthesizerComponent>(station)), user, single!.ID, Standard, 5),
            Is.EqualTo(NCPharmaStationNotice.Started));
        Assert.That(Units(spare), Is.EqualTo(30f).Within(0.05f), "the first canister serves on a tie");
        Assert.That(SComp<NCPharmaSynthesisRunComponent>(station).Inputs.InputPurity, Is.EqualTo(0.9f).Within(0.02f));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void TheReactorTakesTheSecondCanisterToo()
    {
        var at = Spot();
        var user = SEntMan.SpawnEntity("MobHuman", at);
        var reactor = SEntMan.SpawnEntity("NCPharmaReactor", at);
        var (formula, first, second) = ComposeSplitPair();
        Assert.That(_slots.TryInsert(reactor, "source", Fill(at, first, 30f, 0.9f, 0.02f), user), Is.True);
        Assert.That(_slots.TryInsert(reactor, "secondSource", Fill(at, second, 30f, 0.8f, 0.05f), user), Is.True);
        Assert.That(_reactor.TryStartRun((reactor, SComp<NCPharmaReactorComponent>(reactor)), user, formula.ID, 3),
            Is.EqualTo(NCPharmaReactorNotice.Started));
        var run = SComp<NCPharmaReactorRunComponent>(reactor);
        Assert.That(run.SourcePurity, Is.EqualTo(0.8f).Within(0.02f));
        Assert.That(run.FeedstockYield, Is.EqualTo(1f));
    }
}
