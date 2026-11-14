// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Reflection;
using Content.Shared._NC.Medical.Pharmacology;
using NUnit.Framework;

namespace Content.Tests._NC.Medical.Pharmacology;

[TestFixture]
public sealed class NCDrugInteractionClassifierTests
{
    private static NCDrugInteractionRule Rule(float therapeutic, float toxicity)
    {
        var rule = new NCDrugInteractionRule();
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        typeof(NCDrugInteractionRule).GetProperty(nameof(NCDrugInteractionRule.TherapeuticMultiplier), flags)!.SetValue(rule, therapeutic);
        typeof(NCDrugInteractionRule).GetProperty(nameof(NCDrugInteractionRule.ToxicityMultiplier), flags)!.SetValue(rule, toxicity);
        return rule;
    }

    [Test]
    public void NeutralRuleIsCompatible()
    {
        Assert.That(NCDrugInteractionClassifier.ClassifyRule(Rule(1.05f, 1.05f)), Is.EqualTo(NCDrugInteractionClass.Compatible));
    }

    [Test]
    public void ModerateShiftIsCaution()
    {
        Assert.That(NCDrugInteractionClassifier.ClassifyRule(Rule(1f, 1.25f)), Is.EqualTo(NCDrugInteractionClass.Caution));
        Assert.That(NCDrugInteractionClassifier.ClassifyRule(Rule(0.7f, 1f)), Is.EqualTo(NCDrugInteractionClass.Caution));
    }

    [Test]
    public void SevereToxicityOrCollapsedEffectIsDangerous()
    {
        Assert.That(NCDrugInteractionClassifier.ClassifyRule(Rule(1f, 1.6f)), Is.EqualTo(NCDrugInteractionClass.Dangerous));
        Assert.That(NCDrugInteractionClassifier.ClassifyRule(Rule(0.3f, 1f)), Is.EqualTo(NCDrugInteractionClass.Dangerous));
    }

    [Test]
    public void PairTakesTheWorseDirection()
    {
        var result = NCDrugInteractionClassifier.ClassifyPair(Rule(1f, 1.2f), Rule(1f, 1.7f), true, true);
        Assert.That(result, Is.EqualTo(NCDrugInteractionClass.Dangerous));
    }

    [Test]
    public void UndeclaredPairIsCompatibleOnlyWhenBothAreCharted()
    {
        Assert.That(NCDrugInteractionClassifier.ClassifyPair(null, null, true, true), Is.EqualTo(NCDrugInteractionClass.Compatible));
        Assert.That(NCDrugInteractionClassifier.ClassifyPair(null, null, true, false), Is.EqualTo(NCDrugInteractionClass.Unknown));
    }

    [Test]
    public void KnownDangerOutranksUnknown()
    {
        Assert.That(
            NCDrugInteractionClassifier.Worst(NCDrugInteractionClass.Unknown, NCDrugInteractionClass.Dangerous),
            Is.EqualTo(NCDrugInteractionClass.Dangerous));
        Assert.That(
            NCDrugInteractionClassifier.Worst(NCDrugInteractionClass.Compatible, NCDrugInteractionClass.Unknown),
            Is.EqualTo(NCDrugInteractionClass.Unknown));
    }

    [Test]
    public void NonFiniteMultipliersAreUnknown()
    {
        Assert.That(NCDrugInteractionClassifier.ClassifyMultipliers(float.NaN, 1f), Is.EqualTo(NCDrugInteractionClass.Unknown));
    }
}
