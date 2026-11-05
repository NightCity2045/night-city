// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Medical.Cyberware.Neural;
using NUnit.Framework;

namespace Content.Tests.Shared._NC.Medical;

[TestFixture]
public sealed class NCNeuralTest
{
    [TestCase(.7f, 1f, .105f)]
    [TestCase(1f, 1f, .15f)]
    [TestCase(1.25f, 1f, .1875f)]
    [TestCase(1.5f, 1f, .225f)]
    [TestCase(1.5f, .5f, .1125f)]
    [TestCase(50f, 50f, .225f)]
    [TestCase(1f, 0f, 0f)]
    [TestCase(float.NaN, 1f, 0f)]
    [TestCase(1f, float.PositiveInfinity, 0f)]
    public void EvasionRespectsQualityDamageAndHardCap(float quality, float efficiency, float expected)
        => Assert.That(NCNeuralMath.DodgeChance(quality, efficiency), Is.EqualTo(expected).Within(.00001f));
}
