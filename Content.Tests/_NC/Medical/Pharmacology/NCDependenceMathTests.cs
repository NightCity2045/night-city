// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Reflection;
using Content.Shared._NC.Medical.Pharmacology;
using NUnit.Framework;

namespace Content.Tests._NC.Medical.Pharmacology;

[TestFixture]
public sealed class NCDependenceMathTests
{
    private static NCDependenceProfilePrototype Profile() => new();

    private static TimeSpan Seconds(double value) => TimeSpan.FromSeconds(value);

    [Test]
    public void OneStandardDoseNeverCreatesAWithdrawal()
    {
        var profile = Profile();
        var state = new NCDrugDependenceState();
        NCDependenceMath.RegisterDose(profile, state, 6f, Seconds(0));

        var outcome = NCDependenceMath.ResolveWithdrawal(profile, state, Seconds(3600), false, false);

        Assert.That(state.Dependence, Is.LessThan(profile.WithdrawalThreshold));
        Assert.That(outcome.Stage, Is.EqualTo(NCWithdrawalStage.None));
        Assert.That(outcome.SymptomConcentration, Is.EqualTo(0f));
    }

    [Test]
    public void RepeatedDosesBuildToleranceAndDependence()
    {
        var profile = Profile();
        var state = new NCDrugDependenceState();
        for (var dose = 0; dose < 15; dose++)
            NCDependenceMath.RegisterDose(profile, state, 6f, Seconds(dose * 60));

        Assert.That(state.Tolerance, Is.GreaterThan(0.2f));
        Assert.That(state.Dependence, Is.GreaterThanOrEqualTo(profile.WithdrawalThreshold));
    }

    [Test]
    public void ToleranceNeverRemovesMoreThanItsMaximum()
    {
        var profile = Profile();
        var state = new NCDrugDependenceState();
        for (var dose = 0; dose < 500; dose++)
            NCDependenceMath.RegisterDose(profile, state, 30f, Seconds(dose));

        Assert.That(state.Tolerance, Is.LessThanOrEqualTo(profile.MaximumTolerance));
        Assert.That(NCDependenceMath.EffectFactor(profile, state), Is.GreaterThanOrEqualTo(1f - profile.MaximumTolerance));
    }

    private static NCDrugDependenceState Dependent(NCDependenceProfilePrototype profile)
    {
        var state = new NCDrugDependenceState();
        for (var dose = 0; dose < 40; dose++)
            NCDependenceMath.RegisterDose(profile, state, 6f, Seconds(dose));
        return state;
    }

    [Test]
    public void WithdrawalWaitsForTheOnsetThenGrows()
    {
        var profile = Profile();
        var state = Dependent(profile);
        var last = state.LastDose;

        var early = NCDependenceMath.ResolveWithdrawal(profile, state, last + Seconds(30), false, false);
        var middle = NCDependenceMath.ResolveWithdrawal(profile, state, last + profile.WithdrawalOnset + Seconds(120), false, false);
        var late = NCDependenceMath.ResolveWithdrawal(profile, state, last + profile.WithdrawalOnset + profile.WithdrawalRamp, false, false);

        Assert.That(early.Stage, Is.EqualTo(NCWithdrawalStage.None));
        Assert.That(middle.SymptomConcentration, Is.GreaterThan(0f));
        Assert.That(late.SymptomConcentration, Is.GreaterThan(middle.SymptomConcentration));
        Assert.That(late.Stage, Is.GreaterThan(middle.Stage));
    }

    [Test]
    public void ActiveCompoundOrReplacementSuppressesWithdrawal()
    {
        var profile = Profile();
        var state = Dependent(profile);
        var later = state.LastDose + Seconds(3600);

        Assert.That(NCDependenceMath.ResolveWithdrawal(profile, state, later, true, false).Stage, Is.EqualTo(NCWithdrawalStage.None));
        Assert.That(NCDependenceMath.ResolveWithdrawal(profile, state, later, false, true).Stage, Is.EqualTo(NCWithdrawalStage.None));
        Assert.That(NCDependenceMath.ResolveWithdrawal(profile, state, later, false, false).Stage, Is.Not.EqualTo(NCWithdrawalStage.None));
    }

    [Test]
    public void ReplacementTaperFadesDependenceFaster()
    {
        var profile = Profile();
        var natural = Dependent(profile);
        var tapered = Dependent(profile);

        NCDependenceMath.Decay(profile, natural, 600f, false);
        NCDependenceMath.Decay(profile, tapered, 600f, true);

        Assert.That(tapered.Dependence, Is.LessThan(natural.Dependence));
    }

    [Test]
    public void ToleranceAndDependenceFadeOverTime()
    {
        var profile = Profile();
        var state = Dependent(profile);
        var tolerance = state.Tolerance;
        var dependence = state.Dependence;

        NCDependenceMath.Decay(profile, state, 3000f, false);

        Assert.That(state.Tolerance, Is.LessThan(tolerance));
        Assert.That(state.Dependence, Is.LessThan(dependence));
    }

    [Test]
    public void CravingBuildsWhileDependentWithoutTheCompoundAndFadesAfterADose()
    {
        var profile = Profile();
        var state = new NCDrugDependenceState();

        NCDependenceMath.AdvanceCraving(profile, state, 60f, true, false);
        var built = state.Craving;
        NCDependenceMath.AdvanceCraving(profile, state, 60f, true, true);

        Assert.That(built, Is.GreaterThan(0f));
        Assert.That(state.Craving, Is.LessThan(built));
    }

    [Test]
    public void DefaultProfileAndStateAreValid()
    {
        Assert.That(NCDependenceMath.Validate(Profile()), Is.True);
        Assert.That(NCDependenceMath.Validate(new NCDrugDependenceState()), Is.True);
        Assert.That(NCDependenceMath.Validate(new NCDrugDependenceState { Tolerance = float.NaN }), Is.False);
    }

    [Test]
    public void DosingBuildsAComedownDebtThatIsHeldWhileTheCompoundIsActive()
    {
        var profile = Profile();
        var state = new NCDrugDependenceState();
        NCDependenceMath.RegisterDose(profile, state, 6f, Seconds(0));
        var debt = state.Comedown;

        var whileActive = NCDependenceMath.AdvanceComedown(profile, state, 60f, true);

        Assert.That(debt, Is.GreaterThan(0f));
        Assert.That(whileActive, Is.EqualTo(0f));
        Assert.That(state.Comedown, Is.EqualTo(debt));
    }

    [Test]
    public void ComedownIsPaidOutAfterTheActivePhaseAndFades()
    {
        var profile = Profile();
        var state = new NCDrugDependenceState();
        NCDependenceMath.RegisterDose(profile, state, 6f, Seconds(0));
        var debt = state.Comedown;

        var level = NCDependenceMath.AdvanceComedown(profile, state, 30f, false);

        Assert.That(level, Is.EqualTo(debt));
        Assert.That(state.Comedown, Is.LessThan(debt));
    }

    [Test]
    public void ComedownDebtIsCapped()
    {
        var profile = Profile();
        var state = new NCDrugDependenceState();
        for (var dose = 0; dose < 200; dose++)
            NCDependenceMath.RegisterDose(profile, state, 20f, Seconds(dose));

        Assert.That(state.Comedown, Is.LessThanOrEqualTo(profile.MaximumComedown));
    }

    [TestCase(0.5f, 0f, NCDrugPhase.Onset)]
    [TestCase(4f, 2f, NCDrugPhase.Peak)]
    [TestCase(4f, 4f, NCDrugPhase.Peak)]
    [TestCase(2f, 4f, NCDrugPhase.Decline)]
    public void ActivePhasesFollowTheConcentration(float concentration, float previous, NCDrugPhase expected)
    {
        var phase = NCDependenceMath.ResolvePhase(concentration, previous, 0.3f, 4f, false);

        Assert.That(phase, Is.EqualTo(expected));
    }

    [Test]
    public void AfterTheActivePhaseTheComedownOrNothingFollows()
    {
        Assert.That(NCDependenceMath.ResolvePhase(0.1f, 2f, 0.3f, 4f, true), Is.EqualTo(NCDrugPhase.Comedown));
        Assert.That(NCDependenceMath.ResolvePhase(0.1f, 2f, 0.3f, 4f, false), Is.EqualTo(NCDrugPhase.None));
    }

    [Test]
    public void ARisingDoseStartsAsOnsetAndEndsAsPeak()
    {
        var onset = NCDependenceMath.ResolvePhase(0.4f, 0f, 0.3f, 4f, false);
        var peak = NCDependenceMath.ResolvePhase(3.5f, 2f, 0.3f, 4f, false);

        Assert.That(onset, Is.EqualTo(NCDrugPhase.Onset));
        Assert.That(peak, Is.EqualTo(NCDrugPhase.Peak));
    }
}
