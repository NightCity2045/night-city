// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Cyberware;
using NUnit.Framework;

namespace Content.Tests.Shared._NC.Medical;

[TestFixture]
public sealed class NCCyberwareNeuralDependencyTest
{
    [Test]
    public void LossAndRecoveryOfFoundationDoNotLatchTheDependentDeviceOff()
    {
        var ready = NCCyberwareNeuralDependency.Evaluate(true, true, true, 1f);
        var removed = NCCyberwareNeuralDependency.Evaluate(true, false, true, 1f);
        var disconnected = NCCyberwareNeuralDependency.Evaluate(true, true, false, 1f);
        var powerless = NCCyberwareNeuralDependency.Evaluate(true, true, true, 0f);
        var restored = NCCyberwareNeuralDependency.Evaluate(true, true, true, 1f);
        Assert.Multiple(() =>
        {
            Assert.That(ready.Efficiency, Is.EqualTo(1f));
            Assert.That(removed.State, Is.EqualTo(NCCyberwareNeuralDependencyState.Missing));
            Assert.That(removed.Efficiency, Is.Zero);
            Assert.That(disconnected.State, Is.EqualTo(NCCyberwareNeuralDependencyState.Unavailable));
            Assert.That(disconnected.Efficiency, Is.Zero);
            Assert.That(powerless.Efficiency, Is.Zero);
            Assert.That(restored, Is.EqualTo(ready));
        });
    }

    [TestCase(.45f, .45f)]
    [TestCase(2f, 1f)]
    [TestCase(-1f, 0f)]
    [TestCase(float.NaN, 0f)]
    [TestCase(float.PositiveInfinity, 0f)]
    public void FoundationConditionBoundsDependentEfficiency(float condition, float expected)
    {
        var result = NCCyberwareNeuralDependency.Evaluate(true, true, true, condition);
        Assert.That(result.Efficiency, Is.EqualTo(expected));
    }

    [Test]
    public void IndependentDevicesDoNotAcquireAnImplicitFoundationRequirement()
    {
        Assert.That(NCCyberwareNeuralDependency.Evaluate(false, false, false, 0f),
            Is.EqualTo((NCCyberwareNeuralDependencyState.None, 1f)));
    }
}
