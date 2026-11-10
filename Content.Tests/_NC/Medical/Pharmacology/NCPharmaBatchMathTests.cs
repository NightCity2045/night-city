// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Collections.Generic;
using System.Reflection;
using Content.Shared._NC.Medical.Pharmacology.Batch;
using NUnit.Framework;

namespace Content.Tests._NC.Medical.Pharmacology;

[TestFixture]
public sealed class NCPharmaBatchMathTests
{
    private static T Make<T>(params (string Name, object Value)[] values) where T : new()
    {
        var instance = new T();
        foreach (var (name, value) in values)
        {
            var property = typeof(T).GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            property.SetValue(instance, value);
        }

        return instance;
    }

    private static NCPharmaFormulaPrototype Formula(float target = 6f, float stability = 0.85f) =>
        Make<NCPharmaFormulaPrototype>(
            (nameof(NCPharmaFormulaPrototype.TargetConcentration), target),
            (nameof(NCPharmaFormulaPrototype.BaseStability), stability));

    private static NCPharmaProductionProfilePrototype Standard() => Make<NCPharmaProductionProfilePrototype>();

    private static NCPharmaProductionProfilePrototype Clean() => Make<NCPharmaProductionProfilePrototype>(
        (nameof(NCPharmaProductionProfilePrototype.YieldMultiplier), 0.6f),
        (nameof(NCPharmaProductionProfilePrototype.PurityMultiplier), 1.08f),
        (nameof(NCPharmaProductionProfilePrototype.SterilityMultiplier), 1.1f),
        (nameof(NCPharmaProductionProfilePrototype.HomogeneityMultiplier), 1.1f),
        (nameof(NCPharmaProductionProfilePrototype.CostMultiplier), 2f));

    private static NCPharmaProductionInputs Inputs(
        float units = 10f,
        float purity = 0.9f,
        float sterility = 0.9f,
        float mixing = 0.9f) => new(units, purity, sterility, mixing, 0f);

    private static NCPharmaTuningPrototype Tuning() => Make<NCPharmaTuningPrototype>();

    private static NCPharmaDeliveryFormPrototype Form(float sterilityRisk = 1f, float adulterant = 1f) =>
        Make<NCPharmaDeliveryFormPrototype>(
            (nameof(NCPharmaDeliveryFormPrototype.SterilityRiskMultiplier), sterilityRisk),
            (nameof(NCPharmaDeliveryFormPrototype.AdulterantMultiplier), adulterant));

    private static NCPharmaProductionOutcome Produce(
        NCPharmaProductionProfilePrototype profile,
        NCPharmaProductionInputs inputs,
        params NCPharmaModifierPrototype[] modifiers) =>
        NCPharmaBatchMath.Produce(Formula(), modifiers, profile, inputs);

    [Test]
    public void CleanerProductionTradesYieldAndCostForQuality()
    {
        var standard = Produce(Standard(), Inputs());
        var clean = Produce(Clean(), Inputs());

        Assert.That(clean.Doses, Is.LessThan(standard.Doses));
        Assert.That(clean.CostMultiplier, Is.GreaterThan(standard.CostMultiplier));
        Assert.That(clean.Quality.Purity, Is.GreaterThan(standard.Quality.Purity));
        Assert.That(clean.Quality.Sterility, Is.GreaterThan(standard.Quality.Sterility));
        Assert.That(clean.Quality.Homogeneity, Is.GreaterThan(standard.Quality.Homogeneity));
    }

    [Test]
    public void PurityAndSterilityAreIndependent()
    {
        var pureButDirty = Produce(Standard(), Inputs(purity: 0.99f, sterility: 0.2f)).Quality;
        var impureButSterile = Produce(Standard(), Inputs(purity: 0.3f, sterility: 0.99f)).Quality;

        Assert.That(pureButDirty.Purity, Is.GreaterThan(0.95f));
        Assert.That(pureButDirty.Sterility, Is.LessThan(0.3f));
        Assert.That(impureButSterile.Purity, Is.LessThan(0.4f));
        Assert.That(impureButSterile.Sterility, Is.GreaterThan(0.95f));
    }

    [Test]
    public void QualityNeverReachesPerfection()
    {
        var quality = Produce(Clean(), Inputs(purity: 1f, sterility: 1f, mixing: 1f)).Quality;

        Assert.That(quality.Purity, Is.LessThanOrEqualTo(NCPharmaBatchMath.QualityCeiling));
        Assert.That(quality.Sterility, Is.LessThanOrEqualTo(NCPharmaBatchMath.QualityCeiling));
        Assert.That(quality.Homogeneity, Is.LessThanOrEqualTo(NCPharmaBatchMath.QualityCeiling));
        Assert.That(quality.Stability, Is.LessThanOrEqualTo(NCPharmaBatchMath.QualityCeiling));
    }

    [Test]
    public void ModifiersChangeConcentrationAndCostButAlwaysHaveAPrice()
    {
        var fast = Make<NCPharmaModifierPrototype>(
            (nameof(NCPharmaModifierPrototype.ConcentrationMultiplier), 1.25f),
            (nameof(NCPharmaModifierPrototype.StabilityDelta), -0.05f),
            (nameof(NCPharmaModifierPrototype.ToxicityMultiplier), 1.2f),
            (nameof(NCPharmaModifierPrototype.CostMultiplier), 1.1f));

        var plain = Produce(Standard(), Inputs());
        var modified = Produce(Standard(), Inputs(), fast);

        Assert.That(modified.Quality.Concentration, Is.GreaterThan(plain.Quality.Concentration));
        Assert.That(modified.Quality.Stability, Is.LessThan(plain.Quality.Stability));
        Assert.That(modified.ModifierToxicity, Is.GreaterThan(plain.ModifierToxicity));
        Assert.That(modified.CostMultiplier, Is.GreaterThan(plain.CostMultiplier));
    }

    [Test]
    public void FormulaWithMoreThanTwoModifiersIsRejected()
    {
        var modifier = Make<NCPharmaModifierPrototype>();
        var formula = Make<NCPharmaFormulaPrototype>(
            (nameof(NCPharmaFormulaPrototype.TargetConcentration), 6f),
            (nameof(NCPharmaFormulaPrototype.Modifiers), new List<Robust.Shared.Prototypes.ProtoId<NCPharmaModifierPrototype>>
            {
                "a", "b", "c",
            }));

        Assert.That(
            NCPharmaBatchMath.ValidateFormula(formula, new[] { modifier, modifier, modifier }),
            Is.False);
    }

    [Test]
    public void DoseResolutionIsDeterministic()
    {
        var batch = Guid.NewGuid();
        var quality = Produce(Standard(), Inputs(mixing: 0.4f)).Quality;
        var first = NCPharmaBatchMath.ResolveDose(Tuning(), Form(), quality, 1f, batch, 3);
        var second = NCPharmaBatchMath.ResolveDose(Tuning(), Form(), quality, 1f, batch, 3);

        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void PoorMixingMakesDosesOfOneBatchDiffer()
    {
        var batch = Guid.NewGuid();
        var quality = Produce(Standard(), Inputs(mixing: 0.2f)).Quality;
        var concentrations = new HashSet<float>();
        for (var dose = 0; dose < 20; dose++)
            concentrations.Add(NCPharmaBatchMath.ResolveDose(Tuning(), Form(), quality, 1f, batch, dose).EffectiveConcentration);

        Assert.That(concentrations.Count, Is.GreaterThan(5));
    }

    [Test]
    public void PerfectlyMixedDosesAreIdentical()
    {
        var batch = Guid.NewGuid();
        var quality = new NCPharmaQuality(6f, 0.99f, 0.99f, 1f, 0.9f);
        var first = NCPharmaBatchMath.ResolveDose(Tuning(), Form(), quality, 1f, batch, 0);
        var second = NCPharmaBatchMath.ResolveDose(Tuning(), Form(), quality, 1f, batch, 17);

        Assert.That(second.EffectiveConcentration, Is.EqualTo(first.EffectiveConcentration));
    }

    [Test]
    public void ImpurityCreatesToxinAndPurityDoesNot()
    {
        var batch = Guid.NewGuid();
        var dirty = new NCPharmaQuality(6f, 0.5f, 0.99f, 1f, 0.9f);
        var clean = new NCPharmaQuality(6f, NCPharmaBatchMath.QualityCeiling, 0.99f, 1f, 0.9f);

        var dirtyDose = NCPharmaBatchMath.ResolveDose(Tuning(), Form(), dirty, 1f, batch, 0);
        var cleanDose = NCPharmaBatchMath.ResolveDose(Tuning(), Form(), clean, 1f, batch, 0);

        Assert.That(dirtyDose.ToxinBurden, Is.GreaterThan(cleanDose.ToxinBurden * 100f));
        Assert.That(cleanDose.ToxinBurden, Is.LessThan(0.01f));
    }

    [Test]
    public void SterilityRiskScalesWithTheDeliveryForm()
    {
        var batch = Guid.NewGuid();
        var unsterile = new NCPharmaQuality(6f, 0.99f, 0.3f, 1f, 0.9f);
        var oral = NCPharmaBatchMath.ResolveDose(Tuning(), Form(sterilityRisk: 0.1f), unsterile, 1f, batch, 0);
        var infusion = NCPharmaBatchMath.ResolveDose(Tuning(), Form(sterilityRisk: 1.5f), unsterile, 1f, batch, 0);

        Assert.That(infusion.SepsisBurden, Is.GreaterThan(oral.SepsisBurden * 10f));
    }

    [Test]
    public void ConcentrationNeverCountsAsGrade()
    {
        var weak = new NCPharmaQuality(0.5f, 0.99f, 0.99f, 0.99f, 0.9f);
        var strong = new NCPharmaQuality(25f, 0.99f, 0.99f, 0.99f, 0.9f);

        Assert.That(
            NCPharmaBatchMath.ResolveGrade(Tuning(), weak),
            Is.EqualTo(NCPharmaBatchMath.ResolveGrade(Tuning(), strong)));
        Assert.That(NCPharmaBatchMath.ResolveGrade(Tuning(), weak), Is.EqualTo(NCPharmaGrade.Corporate));
    }

    [Test]
    public void StreetBatchIsGradedStreet()
    {
        var quality = new NCPharmaQuality(6f, 0.6f, 0.4f, 0.5f, 0.7f);

        Assert.That(NCPharmaBatchMath.ResolveGrade(Tuning(), quality), Is.EqualTo(NCPharmaGrade.Street));
    }

    private static NCPharmaLabel Label(float concentration = 6f, float purity = 0.9f, bool sterile = true) =>
        new("NC-0001", "Formula", 1, concentration, purity, sterile, NCPharmaGrade.Medical);

    [Test]
    public void QcOnlyRevealsRequestedTests()
    {
        var actual = new NCPharmaQuality(6f, 0.92f, 0.95f, 0.9f, 0.8f);
        var report = NCPharmaBatchMath.RunQc(Tuning(), Guid.NewGuid(), actual, Label(), NCPharmaQcTests.Purity);

        Assert.That(report.Purity, Is.EqualTo(0.92f));
        Assert.That(report.Concentration, Is.Null);
        Assert.That(report.Sterility, Is.Null);
        Assert.That(report.Homogeneity, Is.Null);
        Assert.That(report.Stability, Is.Null);
        Assert.That(report.Performed, Is.EqualTo(NCPharmaQcTests.Purity));
    }

    [Test]
    public void QcCatchesALabelThatLies()
    {
        var actual = new NCPharmaQuality(9.5f, 0.6f, 0.4f, 0.5f, 0.8f);
        var report = NCPharmaBatchMath.RunQc(Tuning(), Guid.NewGuid(), actual, Label(), NCPharmaQcTests.All);

        Assert.That(report.Failed.HasFlag(NCPharmaQcTests.Concentration), Is.True);
        Assert.That(report.Failed.HasFlag(NCPharmaQcTests.Purity), Is.True);
        Assert.That(report.Failed.HasFlag(NCPharmaQcTests.Sterility), Is.True);
        Assert.That(report.Failed.HasFlag(NCPharmaQcTests.Homogeneity), Is.True);
        Assert.That(
            NCPharmaBatchMath.ResolveQcState(NCPharmaQcState.Untested, report),
            Is.EqualTo(NCPharmaQcState.Rejected));
    }

    [Test]
    public void HonestBatchPassesFullQcAndIsCertified()
    {
        var actual = new NCPharmaQuality(6.1f, 0.93f, 0.95f, 0.92f, 0.85f);
        var report = NCPharmaBatchMath.RunQc(Tuning(), Guid.NewGuid(), actual, Label(), NCPharmaQcTests.All);

        Assert.That(report.Failed, Is.EqualTo(NCPharmaQcTests.None));
        Assert.That(
            NCPharmaBatchMath.ResolveQcState(NCPharmaQcState.Untested, report),
            Is.EqualTo(NCPharmaQcState.Certified));
    }

    [Test]
    public void PartialQcIsOnlyPartial()
    {
        var actual = new NCPharmaQuality(6f, 0.93f, 0.95f, 0.92f, 0.85f);
        var report = NCPharmaBatchMath.RunQc(
            Tuning(),
            Guid.NewGuid(),
            actual,
            Label(),
            NCPharmaQcTests.Purity | NCPharmaQcTests.Concentration);

        Assert.That(
            NCPharmaBatchMath.ResolveQcState(NCPharmaQcState.Untested, report),
            Is.EqualTo(NCPharmaQcState.PartialQc));
    }

    [Test]
    public void RecalledBatchStaysRecalled()
    {
        var actual = new NCPharmaQuality(6f, 0.93f, 0.95f, 0.92f, 0.85f);
        var report = NCPharmaBatchMath.RunQc(Tuning(), Guid.NewGuid(), actual, Label(), NCPharmaQcTests.All);

        Assert.That(
            NCPharmaBatchMath.ResolveQcState(NCPharmaQcState.Recalled, report),
            Is.EqualTo(NCPharmaQcState.Recalled));
    }

    [Test]
    public void DoseNoiseStaysInRangeAndIsStable()
    {
        var batch = Guid.NewGuid();
        for (var dose = 0; dose < 500; dose++)
        {
            var noise = NCPharmaBatchMath.DoseNoise(batch, dose);
            Assert.That(noise, Is.InRange(-1f, 1f));
            Assert.That(NCPharmaBatchMath.DoseNoise(batch, dose), Is.EqualTo(noise));
        }
    }

    [Test]
    public void ModifiersScaleAbsorptionOfTheDeliveryForm()
    {
        var form = Make<NCPharmaDeliveryFormPrototype>(
            (nameof(NCPharmaDeliveryFormPrototype.AbsorptionDuration), TimeSpan.FromSeconds(10)));
        var fast = Make<NCPharmaModifierPrototype>((nameof(NCPharmaModifierPrototype.AbsorptionMultiplier), 0.4f));
        var slow = Make<NCPharmaModifierPrototype>((nameof(NCPharmaModifierPrototype.AbsorptionMultiplier), 4f));

        Assert.That(NCPharmaBatchMath.ResolveAbsorption(form, Array.Empty<NCPharmaModifierPrototype>()),
            Is.EqualTo(TimeSpan.FromSeconds(10)));
        Assert.That(NCPharmaBatchMath.ResolveAbsorption(form, new[] { fast }), Is.EqualTo(TimeSpan.FromSeconds(4)));
        Assert.That(NCPharmaBatchMath.ResolveAbsorption(form, new[] { slow }), Is.EqualTo(TimeSpan.FromSeconds(40)));
    }

    [Test]
    public void InstantFormsAndRunawayAbsorptionAreBounded()
    {
        var instant = Make<NCPharmaDeliveryFormPrototype>();
        var slowForm = Make<NCPharmaDeliveryFormPrototype>(
            (nameof(NCPharmaDeliveryFormPrototype.AbsorptionDuration), TimeSpan.FromSeconds(200)));
        var slow = Make<NCPharmaModifierPrototype>((nameof(NCPharmaModifierPrototype.AbsorptionMultiplier), 10f));

        Assert.That(NCPharmaBatchMath.ResolveAbsorption(instant, Array.Empty<NCPharmaModifierPrototype>()),
            Is.EqualTo(TimeSpan.Zero));
        Assert.That(NCPharmaBatchMath.ResolveAbsorption(slowForm, new[] { slow }),
            Is.EqualTo(TimeSpan.FromSeconds(NCPharmaBatchMath.MaximumAbsorptionSeconds)));
    }

    [Test]
    public void ForecastMatchesWhatProductionWouldDeliver()
    {
        var fast = Make<NCPharmaModifierPrototype>(
            (nameof(NCPharmaModifierPrototype.ConcentrationMultiplier), 1.25f),
            (nameof(NCPharmaModifierPrototype.StabilityDelta), -0.05f),
            (nameof(NCPharmaModifierPrototype.ToxicityMultiplier), 1.2f),
            (nameof(NCPharmaModifierPrototype.AbsorptionMultiplier), 0.4f));
        var form = Make<NCPharmaDeliveryFormPrototype>(
            (nameof(NCPharmaDeliveryFormPrototype.AbsorptionDuration), TimeSpan.FromSeconds(10)));

        var plain = NCPharmaBatchMath.Forecast(Formula(), Array.Empty<NCPharmaModifierPrototype>(), form, Standard(), 12f);
        var boosted = NCPharmaBatchMath.Forecast(Formula(), new[] { fast }, form, Standard(), 12f);

        Assert.That(plain.PeakConcentration, Is.EqualTo(6f));
        Assert.That(boosted.PeakConcentration, Is.EqualTo(7.5f).Within(0.001f));
        Assert.That(boosted.ToxicMargin, Is.LessThan(plain.ToxicMargin));
        Assert.That(boosted.Stability, Is.LessThan(plain.Stability));
        Assert.That(boosted.Absorption, Is.LessThan(plain.Absorption));
        Assert.That(boosted.ToxicityMultiplier, Is.EqualTo(1.2f).Within(0.001f));
    }

    [Test]
    public void ForecastToxicMarginBelowOneMeansAnUnsafePeak()
    {
        var form = Make<NCPharmaDeliveryFormPrototype>();
        var forecast = NCPharmaBatchMath.Forecast(Formula(target: 20f), Array.Empty<NCPharmaModifierPrototype>(), form, Standard(), 12f);

        Assert.That(forecast.ToxicMargin, Is.LessThan(1f));
    }

    [Test]
    public void DerivedFormulaIsANewObjectAndNeverChangesItsParent()
    {
        var parent = NCPharmaFormulaPrototype.Create(
            "Parent", "name", 1, "Compound", "Form", 6f, [], "Profile", 0.85f,
            NCPharmaRegulatoryStatus.Prescription, null, null, null);
        var child = NCPharmaFormulaPrototype.Create(
            "Parent.v2", parent.Name, 2, parent.Compound, parent.DeliveryForm, 7f, [], parent.DefaultProductionProfile,
            parent.BaseStability, NCPharmaRegulatoryStatus.Experimental, parent.ID, "Better", "Dr. Test");

        Assert.That(child, Is.Not.SameAs(parent));
        Assert.That(parent.TargetConcentration, Is.EqualTo(6f));
        Assert.That(parent.RegulatoryStatus, Is.EqualTo(NCPharmaRegulatoryStatus.Prescription));
        Assert.That(child.DerivedFrom, Is.EqualTo("Parent"));
        Assert.That(child.RegulatoryStatus, Is.EqualTo(NCPharmaRegulatoryStatus.Experimental));
    }

    [Test]
    public void ApprovalChangesOnlyTheStatusOfACopy()
    {
        var experimental = NCPharmaFormulaPrototype.Create(
            "F.v2", "name", 2, "Compound", "Form", 7f, [], "Profile", 0.8f,
            NCPharmaRegulatoryStatus.Experimental, "F", "Name", "Author");
        var approved = experimental.WithStatus(NCPharmaRegulatoryStatus.Prescription);

        Assert.That(approved, Is.Not.SameAs(experimental));
        Assert.That(experimental.RegulatoryStatus, Is.EqualTo(NCPharmaRegulatoryStatus.Experimental));
        Assert.That(approved.RegulatoryStatus, Is.EqualTo(NCPharmaRegulatoryStatus.Prescription));
        Assert.That(approved.TargetConcentration, Is.EqualTo(experimental.TargetConcentration));
        Assert.That(approved.CustomName, Is.EqualTo("Name"));
        Assert.That(approved.DerivedFrom, Is.EqualTo("F"));
    }
}
