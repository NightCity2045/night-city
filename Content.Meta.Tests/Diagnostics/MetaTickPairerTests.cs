// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Diagnostics;
using NUnit.Framework;

namespace Content.Meta.Tests.Diagnostics;

[TestFixture]
public sealed class MetaTickPairerTests
{
    [Test]
    public void CompleteTicksRequireTheSameTickAndThreadAndMonotonicClock()
    {
        var pair = new MetaTickPairer(); pair.Begin(uint.MaxValue, 4, 100);
        Assert.That(pair.TryEnd(uint.MaxValue, 4, 125, out var elapsed), Is.True); Assert.That(elapsed, Is.EqualTo(25));
        pair.Begin(0, 4, 130); Assert.That(pair.TryEnd(0, 5, 150, out elapsed), Is.False); Assert.That(elapsed, Is.Zero);
        pair.Begin(1, 4, 160); Assert.That(pair.TryEnd(1, 4, 159, out _), Is.False);
        Assert.That(pair.Completed, Is.EqualTo(1)); Assert.That(pair.MissingPairs, Is.EqualTo(2));
    }
    [Test]
    public void MissingStopsAndUnexpectedStopsAreExplicitAndBoundaryIsExcluded()
    {
        var pair = new MetaTickPairer(); pair.Begin(7, 2, 10); pair.Begin(8, 2, 20);
        Assert.That(pair.MissingPairs, Is.EqualTo(1)); Assert.That(pair.TryEnd(8, 2, 25, out _), Is.True);
        Assert.That(pair.TryEnd(8, 2, 26, out _), Is.False);
        pair.Begin(9, 2, 30); pair.DiscardBoundary(); Assert.That(pair.BoundaryPartials, Is.EqualTo(1));
        Assert.That(pair.Completed, Is.EqualTo(1)); Assert.That(pair.MissingPairs, Is.EqualTo(2));
        Assert.That(MetaMetrics.Unit(MetaMetric.EngineTickElapsed), Is.EqualTo(MetaMetricUnit.ElapsedStopwatchTicks));
    }
}
