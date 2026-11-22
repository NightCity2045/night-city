// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#nullable enable annotations

using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared._NC.Medical.Pharmacology.Lab;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

/// <summary>
/// Every compound that can be produced needs raw material of a known kind, and what a material can make follows
/// from that data. Integration tests run only on the owner's request.
/// </summary>
[TestFixture]
public sealed class NCPharmaFeedstockTest : GameTest
{
    private static readonly ProtoId<NCDrugProfilePrototype> Vasopressor = "NCDrugVasopressor";

    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;

    public override PoolSettings PoolSettings => new() { Connected = false };

    [Test]
    [RunOnSide(Side.Server)]
    public void EveryFormulaCompoundHasKnownFeedstock()
    {
        foreach (var formula in _prototypes.EnumeratePrototypes<NCPharmaFormulaPrototype>())
        {
            var compound = _prototypes.Index(formula.Compound);
            Assert.That(compound.Feedstock, Is.Not.Null, $"{formula.ID}: {compound.ID} has no feedstock");
            Assert.That(_prototypes.HasIndex(compound.Feedstock!.Value), $"{compound.ID}: unknown main feedstock");
            foreach (var (id, share) in compound.AlternateFeedstocks)
            {
                Assert.That(_prototypes.HasIndex(id), $"{compound.ID}: unknown alternative {id}");
                Assert.That(id, Is.Not.EqualTo(compound.Feedstock), $"{compound.ID}: alternative repeats the main one");
                Assert.That(share, Is.GreaterThan(0f).And.LessThan(1f), $"{compound.ID}: alternative yield");
            }
        }
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void YieldFollowsTheKindOfMaterial()
    {
        var vasopressor = _prototypes.Index(Vasopressor);
        Assert.Multiple(() =>
        {
            Assert.That(NCPharmaBatchMath.FeedstockYield(vasopressor, "NCFeedstockCardio"), Is.EqualTo(1f));
            Assert.That(NCPharmaBatchMath.FeedstockYield(vasopressor, "NCFeedstockNeuro"), Is.EqualTo(0.6f));
            Assert.That(NCPharmaBatchMath.FeedstockYield(vasopressor, "NCFeedstockMicrobial"), Is.EqualTo(0f));
            Assert.That(NCPharmaBatchMath.FeedstockYield(vasopressor, null), Is.EqualTo(0f));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void EveryFeedstockCanBeBrewed()
    {
        foreach (var feedstock in _prototypes.EnumeratePrototypes<NCPharmaFeedstockPrototype>())
            Assert.That(_prototypes.HasIndex(feedstock.Broth), $"{feedstock.ID}: no broth reagent");
    }
}
