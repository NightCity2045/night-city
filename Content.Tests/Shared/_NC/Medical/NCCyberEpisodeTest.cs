// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Collections.Generic;
using Content.Shared._NC.Medical.Humanity;
using NUnit.Framework;

namespace Content.Tests.Shared._NC.Medical;

[TestFixture]
public sealed class NCCyberEpisodeTest
{
    private static NCHumanityProfilePrototype Profile()
    {
        var p = new NCHumanityProfilePrototype();
        Set(p, nameof(p.EpisodesEnabled), true);
        var bands = new List<NCHumanityCrisisBand>();
        foreach (var (stage, humanity, stress) in new[] { (NCCyberCrisisStage.Warning, 50f, 35f),
                     (NCCyberCrisisStage.Crisis, 35f, 60f), (NCCyberCrisisStage.Acute, 20f, 85f) })
        {
            var band = new NCHumanityCrisisBand();
            Set(band, nameof(band.Stage), stage); Set(band, nameof(band.HumanityBelow), humanity); Set(band, nameof(band.StressAtLeast), stress);
            bands.Add(band);
        }
        Set(p, nameof(p.Bands), bands);
        return p;
    }
    private static void Set(object value, string property, object setting) => value.GetType().GetProperty(property)!.SetValue(value, setting);
    private static NCHumanityStateSnapshot Vulnerable() => new() { Current = 20, Burden = 30, Stress = 100 };

    [Test]
    public void ExtremeImpulseCannotSkipWarnings()
    {
        var state = Vulnerable(); var p = Profile();
        NCHumanityMath.UpdateStage(state, p, 0);
        Assert.That(state.Stage, Is.EqualTo(NCCyberCrisisStage.Stable));
        NCHumanityMath.UpdateStage(state, p, 7);
        Assert.That(state.Stage, Is.EqualTo(NCCyberCrisisStage.Stable));
        NCHumanityMath.UpdateStage(state, p, 1);
        Assert.That(state.Stage, Is.EqualTo(NCCyberCrisisStage.Warning));
        NCHumanityMath.UpdateStage(state, p, 8);
        Assert.That(state.Stage, Is.EqualTo(NCCyberCrisisStage.Crisis));
        NCHumanityMath.UpdateStage(state, p, 8);
        Assert.That(state.Stage, Is.EqualTo(NCCyberCrisisStage.Acute));
    }

    [Test]
    public void AcuteEpisodeIsBoundedAndRecoveryPreventsImmediateRelapse()
    {
        var state = Vulnerable(); var p = Profile(); state.Stage = NCCyberCrisisStage.Acute;
        NCHumanityMath.UpdateStage(state, p, 90);
        Assert.That(state.Stage, Is.EqualTo(NCCyberCrisisStage.Recovery));
        Assert.That(state.Current, Is.EqualTo(20));
        NCHumanityMath.UpdateStage(state, p, 119);
        Assert.That(state.Stage, Is.EqualTo(NCCyberCrisisStage.Recovery));
        NCHumanityMath.UpdateStage(state, p, 1);
        Assert.That(state.Stage, Is.EqualTo(NCCyberCrisisStage.Stable));
        NCHumanityMath.UpdateStage(state, p, 0);
        Assert.That(state.Stage, Is.EqualTo(NCCyberCrisisStage.Stable));
    }

    [Test]
    public void UnaugmentedStressAndCalmChromeDoNotCauseEpisodes()
    {
        var p = Profile();
        var ordinary = new NCHumanityStateSnapshot { Stress = 100 };
        var chrome = new NCHumanityStateSnapshot { Current = 0, Burden = 100, Exhaustion = 100 };
        for (var i = 0; i < 300; i++) { NCHumanityMath.UpdateStage(ordinary, p, 1); NCHumanityMath.UpdateStage(chrome, p, 1); }
        Assert.That(ordinary.Stage, Is.EqualTo(NCCyberCrisisStage.Stable));
        Assert.That(chrome.Stage, Is.EqualTo(NCCyberCrisisStage.Stable));
    }

    [Test]
    public void TreatmentRequiresSustainedStabilization()
    {
        var state = Vulnerable(); state.Stage = NCCyberCrisisStage.Crisis; state.Stress = 0;
        var p = Profile();
        NCHumanityMath.UpdateStage(state, p, 29);
        Assert.That(state.Stage, Is.EqualTo(NCCyberCrisisStage.Crisis));
        NCHumanityMath.UpdateStage(state, p, 1);
        Assert.That(state.Stage, Is.EqualTo(NCCyberCrisisStage.Recovery));
    }

    [Test]
    public void ClonePreservesRecoveryAndConsentWithoutSharingHistory()
    {
        var state = Vulnerable(); state.Stage = NCCyberCrisisStage.Recovery;
        state.RecoverySeconds = 71; state.Exhaustion = 25; state.SupportCooldown = 42;
        state.DeclinesSupport = true; state.MotorInhibitSeconds = 19; state.Episode = "NCCyberMotorEpisode";
        var copy = NCHumanityMath.Clone(state);
        Assert.Multiple(() =>
        {
            Assert.That(copy.RecoverySeconds, Is.EqualTo(71)); Assert.That(copy.Exhaustion, Is.EqualTo(25));
            Assert.That(copy.SupportCooldown, Is.EqualTo(42)); Assert.That(copy.DeclinesSupport, Is.True);
            Assert.That(copy.MotorInhibitSeconds, Is.EqualTo(19)); Assert.That(copy.Episode, Is.EqualTo(state.Episode));
            Assert.That(copy.Adaptation, Is.Not.SameAs(state.Adaptation));
        });
    }

    [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(-1f)] [TestCase(100001f)]
    public void RejectsInvalidPersistedDurations(float duration)
    {
        var state = Vulnerable(); state.RecoverySeconds = duration;
        Assert.That(NCHumanityMath.Validate(state), Is.False);
    }
}
