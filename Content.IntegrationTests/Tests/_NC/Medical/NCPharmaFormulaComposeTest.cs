// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#nullable enable annotations

using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

/// <summary>
/// Building a formula on the tree: one or two cores, an automatic name, lines of versions, what the laboratory
/// knows about each node, and saving. Integration tests run only on the owner's request.
/// </summary>
[TestFixture]
public sealed class NCPharmaFormulaComposeTest : GameTest
{
    private const string Analgesic = "NCDrugTraumaAnalgesic";
    private const string Sedative = "NCDrugSedative";
    private const string Vasopressor = "NCDrugVasopressor";
    private const string Autoinjector = "NCPharmaDeliveryAutoinjector";
    private const string Standard = "NCPharmaProductionStandard";
    private const string Blocker = "NCPharmaModifierMetabolicBlocker";

    [SidedDependency(Side.Server)] private NCPharmaBatchSystem _batches = default!;
    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;

    public override PoolSettings PoolSettings => new() { Connected = false };

    private NCPharmaFormulaPrototype Compose(string? parent, string first, string? second, params string[] modifiers)
    {
        Assert.That(_batches.TryComposeFormula(
            parent, "Tester", first, second, Autoinjector, 6f, modifiers, Standard,
            out var composed, out var notice), Is.True, $"compose failed: {notice}");
        return composed!;
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ComposedFormulaGetsAnAutomaticNameAndAnExperimentalStatus()
    {
        var formula = Compose(null, Analgesic, null);
        Assert.Multiple(() =>
        {
            Assert.That(formula.CustomName, Is.Null, "a player never types the name");
            Assert.That(formula.Name.Id, Does.StartWith("nc-medical-diagnostics-drug-"));
            Assert.That(formula.RegulatoryStatus, Is.EqualTo(NCPharmaRegulatoryStatus.Experimental));
            Assert.That(formula.Version, Is.EqualTo(1));
            Assert.That(formula.DerivedFrom, Is.Null);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void SameCoresContinueTheLineAndOtherCoresStartANewOne()
    {
        var first = Compose(null, Analgesic, null);
        var next = Compose(first.ID, Analgesic, null, Blocker);
        var other = Compose(first.ID, Sedative, null);
        Assert.Multiple(() =>
        {
            Assert.That(next.Version, Is.EqualTo(2));
            Assert.That(next.DerivedFrom, Is.EqualTo(first.ID));
            Assert.That(other.Version, Is.EqualTo(1), "another core is a new line");
            Assert.That(other.DerivedFrom, Is.Null);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void TwoCoresAreMoreToxicAndNeedMaterialBothCanUse()
    {
        var pair = Compose(null, Analgesic, Sedative);
        var single = Compose(null, Analgesic, null);
        var profile = _prototypes.Index<NCPharmaProductionProfilePrototype>(Standard);
        var inputs = new NCPharmaProductionInputs(10f, 0.9f, 0.9f, 0.9f, 0f);
        var pairToxicity = NCPharmaBatchMath.Produce(pair, [], profile, inputs).ModifierToxicity;
        var singleToxicity = NCPharmaBatchMath.Produce(single, [], profile, inputs).ModifierToxicity;
        Assert.Multiple(() =>
        {
            Assert.That(pair.SecondCompound?.Id, Is.EqualTo(Sedative));
            Assert.That(pairToxicity, Is.EqualTo(singleToxicity * NCPharmaBatchMath.CombinationToxicity).Within(0.001f));
            // Both cores are neuro-organic, and both accept the immune kind at half the doses.
            Assert.That(_batches.ResolveFeedstockYield(pair, "NCFeedstockNeuro"), Is.EqualTo(1f));
            Assert.That(_batches.ResolveFeedstockYield(pair, "NCFeedstockImmune"), Is.EqualTo(0.5f));
            Assert.That(_batches.ResolveFeedstockYield(pair, "NCFeedstockCardio"), Is.EqualTo(0f));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void TheSameCoreTwiceIsRefused()
    {
        Assert.That(_batches.TryComposeFormula(
            null, "Tester", Analgesic, Analgesic, Autoinjector, 6f, [], Standard,
            out _, out var notice), Is.False);
        Assert.That(notice, Is.EqualTo(NCPharmaRdNotice.SameCore));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void AnUntestedNodeBecomesSuspectedOnceAFormulaUsesIt()
    {
        string State(string id) => _batches.BuildNodeKnowledge()
            .FirstOrDefault(entry => entry.Id == NCPharmaNodeIds.Modifier(id)).State.ToString();

        Assume.That(State(Blocker), Is.EqualTo(nameof(NCPharmaNodeState.Unknown)), "stock formulas must not use it");
        Compose(null, Analgesic, null, Blocker);
        Assert.That(State(Blocker), Is.EqualTo(nameof(NCPharmaNodeState.Suspected)));
        Assert.That(_batches.BuildNodeKnowledge().First(entry => entry.Id == NCPharmaNodeIds.Core(Analgesic)).State,
            Is.EqualTo(NCPharmaNodeState.Confirmed));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ASavedFormulaWithTwoCoresComesBackTheSame()
    {
        var pair = Compose(null, Vasopressor, Sedative, Blocker);
        var snapshot = NCPharmaBatchSystem.SnapshotOf(pair);
        Assert.That(_batches.TryFormulaFromSnapshot(snapshot, out var restored), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(restored.ID, Is.EqualTo(pair.ID));
            Assert.That(restored.Compound, Is.EqualTo(pair.Compound));
            Assert.That(restored.SecondCompound, Is.EqualTo(pair.SecondCompound));
            Assert.That(restored.Modifiers.Select(m => m.Id), Is.EqualTo(pair.Modifiers.Select(m => m.Id)));
        });
    }
}
