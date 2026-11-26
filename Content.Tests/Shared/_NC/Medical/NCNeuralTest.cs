// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Medical.Cyberware.Neural;
using NUnit.Framework;

namespace Content.Tests.Shared._NC.Medical;

[TestFixture]
public sealed class NCNeuralTest
{
    [TestCase(1f, .15f)]
    [TestCase(.5f, .075f)]
    [TestCase(50f, .15f)]
    [TestCase(0f, 0f)]
    [TestCase(float.NaN, 0f)]
    [TestCase(float.PositiveInfinity, 0f)]
    public void EvasionRespectsDamageAndHardCap(float efficiency, float expected)
        => Assert.That(NCNeuralMath.DodgeChance(efficiency), Is.EqualTo(expected).Within(.00001f));
}
