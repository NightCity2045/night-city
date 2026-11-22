// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
#nullable enable annotations
using System.Collections.Generic;
using Content.Shared._NC.Medical.Pharmacology.Lab;
using NUnit.Framework;

namespace Content.Tests._NC.Medical.Pharmacology;

[TestFixture]
public sealed class NCPharmaInventionTests
{
    private static NCIngredientTraits Ingredient(
        string reagent, string category, float potency, float toxicity, string side = "", string? dependence = null) =>
        new(reagent, category, potency, toxicity, side, dependence);

    private static readonly NCIngredientTraits PainA = Ingredient("A", "Analgesic", 0.6f, 0.2f);
    private static readonly NCIngredientTraits PainB = Ingredient("B", "Analgesic", 0.5f, 0.1f);
    private static readonly NCIngredientTraits Calm = Ingredient("C", "Sedative", 0.5f, 0.15f);
    private static readonly NCIngredientTraits Poison = Ingredient("D", "Stimulant", 0.7f, 0.8f, "cardiac");
    private static readonly NCIngredientTraits Charcoal = Ingredient("E", "", 0f, -0.3f);

    [Test]
    public void TheSameIngredientsAlwaysMakeTheSameCompoundInAnyOrder()
    {
        Assert.That(NCPharmaInvention.TryCombine([PainA, PainB], out var first), Is.True);
        Assert.That(NCPharmaInvention.TryCombine([PainB, PainA], out var second), Is.True);
        Assert.That(second, Is.EqualTo(first));
        Assert.That(NCPharmaInvention.KeyOf(["B", "A"]), Is.EqualTo(NCPharmaInvention.KeyOf(["A", "B"])));
        Assert.That(NCPharmaInvention.IdFor(NCPharmaInvention.KeyOf(["B", "A"])), Is.EqualTo(NCPharmaInvention.IdFor("A+B")));
        Assert.That(NCPharmaInvention.IdFor("A+B"), Is.Not.EqualTo(NCPharmaInvention.IdFor("A+C")));
    }

    [Test]
    public void TheDominantLeaningDecidesWhatTheCompoundDoes()
    {
        Assert.That(NCPharmaInvention.TryCombine([PainA, PainB, Calm], out var profile), Is.True);
        Assert.That(profile.Category, Is.EqualTo("Analgesic"));
    }

    [Test]
    public void IngredientsPullingTheSameWayMakeItStrongerAndNeedLessOfIt()
    {
        Assert.That(NCPharmaInvention.TryCombine([PainA, Charcoal], out var weak), Is.True);
        Assert.That(NCPharmaInvention.TryCombine([PainA, PainB], out var strong), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(strong.Strength, Is.GreaterThan(weak.Strength));
            Assert.That(strong.Primary, Is.GreaterThan(weak.Primary));
            Assert.That(strong.Therapeutic, Is.LessThan(weak.Therapeutic));
        });
    }

    [Test]
    public void AHarmfulIngredientNarrowsTheSafeWindowAndANeutraliserWidensIt()
    {
        Assert.That(NCPharmaInvention.TryCombine([PainA, PainB], out var plain), Is.True);
        Assert.That(NCPharmaInvention.TryCombine([PainA, Poison], out var harmful), Is.True);
        Assert.That(NCPharmaInvention.TryCombine([PainA, PainB, Charcoal], out var cleaned), Is.True);

        var plainMargin = plain.Toxic / plain.Therapeutic;
        Assert.That(harmful.Toxic / harmful.Therapeutic, Is.LessThan(plainMargin));
        Assert.That(cleaned.Toxic / cleaned.Therapeutic, Is.GreaterThan(plainMargin));
    }

    [Test]
    public void SideEffectsAndDependenceComeFromTheIngredientsThatCarryThem()
    {
        var addictive = Ingredient("F", "Stimulant", 0.4f, 0.5f, dependence: "NCDependenceSocial");
        Assert.That(NCPharmaInvention.TryCombine([Poison, addictive], out var profile), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(profile.SideField, Is.EqualTo("cardiacOutputMultiplierAtTherapeutic"));
            Assert.That(profile.Dependence, Is.EqualTo("NCDependenceSocial"));
        });
    }

    [Test]
    public void NothingThatLeansTowardsADrugActionMakesNoCompound()
    {
        Assert.That(NCPharmaInvention.TryCombine([Charcoal, Charcoal], out _), Is.False);
        Assert.That(NCPharmaInvention.TryCombine([PainA], out _), Is.False, "one ingredient is not a combination");
        Assert.That(NCPharmaInvention.TryCombine([PainA, PainB, Calm, Poison], out _), Is.False, "at most three");
    }

    [Test]
    public void TheProfileTextHasEveryFieldADrugProfileNeedsAndKeepsTheWindowOrdered()
    {
        Assert.That(NCPharmaInvention.TryCombine([PainA, PainB, Poison], out var profile), Is.True);
        var yaml = NCPharmaInvention.ToYaml("A+B+D", profile);
        foreach (var field in new[]
                 {
                     "- type: ncDrugProfile", "category:", "feedstock:", "maximumConcentration:", "therapeuticConcentration:",
                     "toxicConcentration:", "clearancePerSecond:", "overdoseBurdenPerSecondAtMaximum:", "codename: nc-lab-codename-invented",
                 })
        {
            Assert.That(yaml, Does.Contain(field));
        }

        Assert.Multiple(() =>
        {
            Assert.That(profile.Therapeutic, Is.GreaterThan(0f));
            Assert.That(profile.Toxic, Is.GreaterThan(profile.Therapeutic));
            Assert.That(profile.Maximum, Is.GreaterThan(profile.Toxic));
        });
    }
}
