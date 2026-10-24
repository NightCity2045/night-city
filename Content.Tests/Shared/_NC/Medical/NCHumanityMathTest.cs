// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using Content.Shared._NC.Medical.Humanity;
using NUnit.Framework;

namespace Content.Tests.Shared._NC.Medical;

[TestFixture]
public sealed class NCHumanityMathTest
{
    [Test]
    public void NewCharacterStartsWithOneHundredAndNoBurden()
    {
        var state = new NCHumanityStateSnapshot();
        Assert.That(state.Current, Is.EqualTo(100));
        Assert.That(state.Burden, Is.Zero);
        Assert.That(NCHumanityMath.Validate(state), Is.True);
    }

    [Test]
    public void ThreeNewSensesMatchTheGddExample()
    {
        var state = new NCHumanityStateSnapshot();
        Assert.That(NCHumanityMath.TryReconcile(state, new Dictionary<string, NCHumanityPackageDose>
        { ["vision"] = new(2, 4), ["hearing"] = new(2, 4), ["thermal"] = new(2, 4) }), Is.True);
        Assert.That(state.Current, Is.EqualTo(88));
        Assert.That(state.Burden, Is.EqualTo(6));
        Assert.That(NCHumanityMath.Maximum(state.Burden), Is.EqualTo(94));
    }

    [Test]
    public void FirstConversionIsOneFunctionalPackage()
    {
        var state = new NCHumanityStateSnapshot();
        Assert.That(NCHumanityMath.TryReconcile(state, new Dictionary<string, NCHumanityPackageDose>
        { ["full-conversion"] = new(20, 25) }), Is.True);
        Assert.That(state.Current, Is.EqualTo(75));
        Assert.That(NCHumanityMath.Maximum(state.Burden), Is.EqualTo(80));
    }

    [Test]
    public void RemovalAndReinstallationNeitherHealNorRepeatAdaptation()
    {
        var state = new NCHumanityStateSnapshot();
        var basic = new Dictionary<string, NCHumanityPackageDose> { ["borg"] = new(20, 25) };
        NCHumanityMath.TryReconcile(state, basic);
        state.Current = 80;
        var armed = new Dictionary<string, NCHumanityPackageDose>(basic)
        { ["reflex"] = new(8, 12), ["weapon:left"] = new(4, 8), ["weapon:right"] = new(4, 8) };
        NCHumanityMath.TryReconcile(state, armed);
        Assert.That(state.Current, Is.EqualTo(52));
        Assert.That(NCHumanityMath.Maximum(state.Burden), Is.EqualTo(64));
        NCHumanityMath.TryReconcile(state, basic);
        Assert.That(state.Current, Is.EqualTo(52));
        Assert.That(NCHumanityMath.Maximum(state.Burden), Is.EqualTo(80));
        NCHumanityMath.TryReconcile(state, armed);
        Assert.That(state.Current, Is.EqualTo(52));
    }

    [Test]
    public void MoreRadicalVersionChargesOnlyNewAdaptation()
    {
        var state = new NCHumanityStateSnapshot();
        var packages = new Dictionary<string, NCHumanityPackageDose> { ["motor:left"] = new(3, 6) };
        NCHumanityMath.TryReconcile(state, packages);
        Assert.That(state.Current, Is.EqualTo(94));
        packages["motor:left"] = new(10, 16);
        NCHumanityMath.TryReconcile(state, packages);
        Assert.That(state.Current, Is.EqualTo(84));
        NCHumanityMath.TryReconcile(state, packages);
        Assert.That(state.Current, Is.EqualTo(84));
    }

    [Test]
    public void AdaptationBelongsToTheRecipientRatherThanTheUsedItem()
    {
        var packages = new Dictionary<string, NCHumanityPackageDose> { ["motor:left"] = new(3, 6) };
        var donor = new NCHumanityStateSnapshot();
        var recipient = new NCHumanityStateSnapshot();
        NCHumanityMath.TryReconcile(donor, packages);
        NCHumanityMath.TryReconcile(recipient, packages);
        Assert.That(recipient.Current, Is.EqualTo(94));
    }

    [Test]
    public void StandardMedicalReplacementIsFree()
    {
        var state = new NCHumanityStateSnapshot();
        NCHumanityMath.TryReconcile(state, new Dictionary<string, NCHumanityPackageDose> { ["medical:left"] = new(0, 0) });
        Assert.That(state.Current, Is.EqualTo(100));
        Assert.That(state.Burden, Is.Zero);
    }

    [Test]
    public void OverburdenIsAllowedAndDoesNotCreateADeathOrControlOutcome()
    {
        var state = new NCHumanityStateSnapshot();
        Assert.That(NCHumanityMath.TryReconcile(state, new Dictionary<string, NCHumanityPackageDose>
        { ["a"] = new(70, 16), ["b"] = new(60, 16) }), Is.True);
        Assert.That(state.Current, Is.Zero);
        Assert.That(state.Burden, Is.EqualTo(130));
        Assert.That(state.Stage, Is.EqualTo(NCCyberCrisisStage.Stable));
    }

    [Test]
    public void ExcessRecoveryCannotBeBankedAboveThePhysicalMaximum()
    {
        var state = new NCHumanityStateSnapshot { Current = 79, Burden = 20 };
        NCHumanityMath.Advance(state, Profile(), 100, 2f / 60f, 0, false, true);
        Assert.That(state.Current, Is.EqualTo(80));
        NCHumanityMath.TryReconcile(state, new Dictionary<string, NCHumanityPackageDose>());
        NCHumanityMath.Advance(state, Profile(), 100, 0, 0, false, true);
        Assert.That(state.Current, Is.EqualTo(80));
    }

    [Test]
    public void StabilizerChangesStressWithoutIncreasingHumanityOrItsMaximum()
    {
        var state = new NCHumanityStateSnapshot { Current = 10, Burden = 20, Stress = 90, Stage = NCCyberCrisisStage.Acute };
        NCHumanityMath.Advance(state, Profile(), 10, 0, 2, false, false);
        Assert.That(state.Stress, Is.EqualTo(70));
        Assert.That(state.Current, Is.EqualTo(10));
        Assert.That(NCHumanityMath.Maximum(state.Burden), Is.EqualTo(80));
    }

    [Test]
    public void SafeRecoveryDoesNotGenerateFreeHumanity()
    {
        var state = new NCHumanityStateSnapshot { Current = 52, Burden = 20, Stress = 20 };
        NCHumanityMath.Advance(state, Profile(), 60, 0, 0, false, true);
        Assert.That(state.Stress, Is.EqualTo(14).Within(0.001));
        Assert.That(state.Current, Is.EqualTo(52));
    }

    [TestCase(0, 0)]
    [TestCase(3, 0.5f)]
    [TestCase(6, 1)]
    [TestCase(12, 1)]
    [TestCase(21, 0.5f)]
    [TestCase(30, 0)]
    [TestCase(float.NaN, 0)]
    public void TherapeuticEffectIsBoundedAndFallsAtToxicConcentration(float concentration, float expected)
        => Assert.That(NCHumanityMath.TreatmentFactor(concentration, 1, 6, 12, 30), Is.EqualTo(expected).Within(0.001));

    [TestCase(49, 35, 10, NCCyberCrisisStage.Warning)]
    [TestCase(34, 60, 10, NCCyberCrisisStage.Crisis)]
    [TestCase(19, 85, 10, NCCyberCrisisStage.Acute)]
    [TestCase(0, 100, 0, NCCyberCrisisStage.Stable)]
    [TestCase(0, 0, 10, NCCyberCrisisStage.Stable)]
    [TestCase(50, 100, 10, NCCyberCrisisStage.Stable)]
    public void CrisisNeedsBothPsychologicalStateAndActualCyberware(float current, float stress, float burden,
        NCCyberCrisisStage expected)
    {
        var state = new NCHumanityStateSnapshot { Current = current, Stress = stress, Burden = burden };
        NCHumanityMath.UpdateStage(state, Profile(), 0);
        Assert.That(state.Stage, Is.EqualTo(expected));
    }

    [Test]
    public void CrisisRecoveryRequiresHysteresisAndThirtySeconds()
    {
        var state = new NCHumanityStateSnapshot { Current = 10, Burden = 20, Stress = 79, Stage = NCCyberCrisisStage.Acute };
        NCHumanityMath.UpdateStage(state, Profile(), 29);
        Assert.That(state.Stage, Is.EqualTo(NCCyberCrisisStage.Acute));
        NCHumanityMath.UpdateStage(state, Profile(), 1);
        Assert.That(state.Stage, Is.EqualTo(NCCyberCrisisStage.Crisis));
    }

    [Test]
    public void CorruptPackageInputDoesNotPartiallyChangeCharacterState()
    {
        var state = new NCHumanityStateSnapshot();
        Assert.That(NCHumanityMath.TryReconcile(state, new Dictionary<string, NCHumanityPackageDose>
        { ["valid"] = new(4, 8), ["invalid"] = new(float.NaN, 12) }), Is.False);
        Assert.That(state.Current, Is.EqualTo(100));
        Assert.That(state.Adaptation, Is.Empty);
    }

    [Test]
    public void LegacyAdoptionCreditsExistingConfigurationWithoutAnAcuteBill()
    {
        var state = new NCHumanityStateSnapshot();
        NCHumanityMath.TryReconcile(state, new Dictionary<string, NCHumanityPackageDose> { ["borg"] = new(20, 25) }, true);
        Assert.That(state.Current, Is.EqualTo(80));
        Assert.That(state.Adaptation["borg"], Is.EqualTo(25));
    }

    [Test]
    public void SnapshotCloneDoesNotShareAdaptationOrRemainingCooldowns()
    {
        var original = new NCHumanityStateSnapshot();
        original.Adaptation["motor:left"] = 6;
        original.RecentImpulses.Add(new() { Source = Guid.NewGuid(), Profile = "NCHumanityNeuralTrauma", RemainingSeconds = 12 });
        var clone = NCHumanityMath.Clone(original);
        clone.Adaptation["motor:left"] = 16;
        clone.RecentImpulses[0].RemainingSeconds = 0;
        Assert.That(original.Adaptation["motor:left"], Is.EqualTo(6));
        Assert.That(original.RecentImpulses[0].RemainingSeconds, Is.EqualTo(12));
        original.Current = float.NaN;
        Assert.That(NCHumanityMath.Validate(original), Is.False);
    }

    private static NCHumanityProfilePrototype Profile()
    {
        var profile = new NCHumanityProfilePrototype();
        // Prototype setters remain private in production; pure math tests supply explicit GDD boundary inputs.
        typeof(NCHumanityProfilePrototype).GetProperty(nameof(profile.Bands))!.SetValue(profile,
            new List<NCHumanityCrisisBand> { Band(NCCyberCrisisStage.Warning, 50, 35),
                Band(NCCyberCrisisStage.Crisis, 35, 60), Band(NCCyberCrisisStage.Acute, 20, 85) });
        return profile;
    }
    private static NCHumanityCrisisBand Band(NCCyberCrisisStage stage, float humanity, float stress)
    {
        var band = new NCHumanityCrisisBand();
        typeof(NCHumanityCrisisBand).GetProperty(nameof(band.Stage))!.SetValue(band, stage);
        typeof(NCHumanityCrisisBand).GetProperty(nameof(band.HumanityBelow))!.SetValue(band, humanity);
        typeof(NCHumanityCrisisBand).GetProperty(nameof(band.StressAtLeast))!.SetValue(band, stress);
        return band;
    }
}
