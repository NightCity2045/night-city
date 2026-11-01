// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Cyberware;
using NUnit.Framework;

namespace Content.Tests.Shared._NC.Medical;

[TestFixture]
public sealed class NCCyberwareConditionTest
{
    [TestCase(0f, 100f, 1f)]
    [TestCase(0.25f, 75f, 0.75f)]
    [TestCase(1f, 0f, 0f)]
    public void FullyServicedPartRetainsItsPermanentLoss(float damage, float ceiling, float efficiency)
    {
        var repaired = NCCyberwareCondition.ServiceCeiling(100f, damage);
        Assert.Multiple(() =>
        {
            Assert.That(repaired, Is.EqualTo(ceiling).Within(0.0001f));
            Assert.That(NCCyberwareCondition.Efficiency(repaired, 100f),
                Is.EqualTo(efficiency).Within(0.0001f));
        });
    }

    [Test]
    public void OnlyTheImpactBeyondTheConfiguredThresholdAddsPermanentDamage()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NCCyberwareCondition.PermanentDamageAfterImpact(0.2f, 50f, 100f, 0.5f, 0.25f),
                Is.EqualTo(0.2f).Within(0.0001f));
            Assert.That(NCCyberwareCondition.PermanentDamageAfterImpact(0.2f, 100f, 100f, 0.5f, 0.25f),
                Is.EqualTo(0.325f).Within(0.0001f));
            Assert.That(NCCyberwareCondition.PermanentDamageAfterImpact(0.9f, 200f, 100f, 0.5f, 1f),
                Is.EqualTo(1f));
        });
    }

    [Test]
    public void InvalidInputsNeverCreateFreeEfficiencyOrNegativeRepairCeilings()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NCCyberwareCondition.ServiceCeiling(100f, float.NaN), Is.Zero);
            Assert.That(NCCyberwareCondition.ServiceCeiling(-1f, 0f), Is.Zero);
            Assert.That(NCCyberwareCondition.ServiceCeiling(100f, 2f), Is.Zero);
            Assert.That(NCCyberwareCondition.Efficiency(float.PositiveInfinity, 100f), Is.Zero);
            Assert.That(NCCyberwareCondition.Efficiency(20f, 0f), Is.Zero);
            Assert.That(NCCyberwareCondition.PermanentDamageAfterImpact(0f, float.NaN, 100f, 0.5f, 1f),
                Is.EqualTo(1f));
        });
    }
}
