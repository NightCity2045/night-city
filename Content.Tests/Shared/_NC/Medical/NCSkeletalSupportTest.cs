// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Cyberware;
using NUnit.Framework;

namespace Content.Tests.Shared._NC.Medical;

[TestFixture]
public sealed class NCSkeletalSupportTest
{
    [TestCase(0f, .3f, true, 0f)]
    [TestCase(.15f, .3f, true, .5f)]
    [TestCase(.4f, .3f, true, 1f)]
    [TestCase(.4f, .45f, true, .8888889f)]
    [TestCase(.4f, .3f, false, 0f)]
    [TestCase(.4f, 0f, true, 0f)]
    public void MotorDemandTracksSuitPenaltyAndReadiness(float penalty, float allowance, bool ready, float expected)
    {
        Assert.That(NCSkeletalSupportMath.MotorLoad(penalty, allowance, ready), Is.EqualTo(expected).Within(.00001f));
    }

    [TestCase(.6f, .15f, .75f)]
    [TestCase(.6f, .30f, .90f)]
    [TestCase(.6f, .45f, 1f)]
    [TestCase(.3f, .45f, .75f)]
    [TestCase(1f, .45f, 1f)]
    [TestCase(1.1f, .45f, 1.1f)]
    [TestCase(.6f, 0f, .6f)]
    [TestCase(.6f, -1f, .6f)]
    [TestCase(.6f, float.NaN, .6f)]
    public void CompensationUsesPercentagePointsAndCannotCreateAnExtraSpeedBonus(float original, float allowance, float expected)
    {
        Assert.That(NCSkeletalSupportMath.Compensate(original, allowance), Is.EqualTo(expected).Within(.00001f));
    }

    [Test]
    public void UnrelatedMultipliersRemainAfterHeavySuitCompensation()
    {
        var suit = NCSkeletalSupportMath.Compensate(.6f, .3f);
        Assert.That(suit * .8f * .5f, Is.EqualTo(.36f).Within(.00001f));
    }

    [Test]
    public void BetaNeedsTwoWorkingGraftsAndUsesTheWeakerSupport()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NCSkeletalSupportMath.SupportEfficiency(new[] { 1f }, 2), Is.Zero);
            Assert.That(NCSkeletalSupportMath.SupportEfficiency(new[] { 1f, 0f }, 2), Is.Zero);
            Assert.That(NCSkeletalSupportMath.SupportEfficiency(new[] { 1f, .4f }, 2), Is.EqualTo(.4f));
            Assert.That(NCSkeletalSupportMath.SupportEfficiency(new[] { .4f, 1f }, 1), Is.EqualTo(1f));
            Assert.That(NCSkeletalSupportMath.SupportEfficiency(new[] { 1f, .8f, .1f }, 2), Is.EqualTo(.8f));
            Assert.That(NCSkeletalSupportMath.SupportEfficiency(new[] { float.NaN, 1f }, 2), Is.Zero);
        });
    }
}
