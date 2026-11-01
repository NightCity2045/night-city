// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Diagnostics;
using NUnit.Framework;

namespace Content.Meta.Tests.Diagnostics;

[TestFixture]
public sealed class MetaMetricCaptureTests
{
    [Test]
    public void OverflowPreservesExactAggregateCountsAndNeverClaimsCompleteRawCoverage()
    {
        var capture = new MetaMetricCapture(2);
        capture.Record(MetaMetric.CommandAdmissionLatency, 10); capture.Record(MetaMetric.CommandAdmissionLatency, 30); capture.Record(MetaMetric.CommandAdmissionLatency, 20);
        capture.Stop(); var info = capture.GetInfo(); var summary = info.Summaries.Single();
        Assert.That(info.DroppedSamples, Is.EqualTo(1)); Assert.That(summary.Count, Is.EqualTo(3)); Assert.That(summary.Sum, Is.EqualTo(60));
        Assert.That(summary.Minimum, Is.EqualTo(10)); Assert.That(summary.Maximum, Is.EqualTo(30));
        var page = capture.ReadPage(0); Assert.That(page.Samples.Select(s => s.Value), Is.EqualTo(new long[] { 10, 30 })); Assert.That(page.Complete, Is.False);
    }
    [Test]
    public void StoppedCaptureRejectsFurtherWritesAndPagesHaveExplicitBounds()
    {
        var capture = new MetaMetricCapture(3); capture.Record(MetaMetric.AppPatchBytes, 16); capture.Stop(); capture.Record(MetaMetric.AppPatchBytes, 999);
        Assert.That(capture.GetInfo().Summaries.Single().Sum, Is.EqualTo(16)); Assert.That(capture.ReadPage(0).Complete, Is.True);
        Assert.Throws<ArgumentOutOfRangeException>(() => capture.ReadPage(0, 1025)); Assert.Throws<ArgumentOutOfRangeException>(() => capture.ReadPage(2));
    }
    [Test]
    public void ConcurrentNativeWritersPublishWholeSamplesAndSaturateWithoutIntegerWrap()
    {
        var capture = new MetaMetricCapture(1024);
        Parallel.For(0, 1024, _ => capture.Record(MetaMetric.EventsAccepted, 1)); capture.Stop();
        Assert.That(capture.ReadPage(0, 1024).Samples, Has.Length.EqualTo(1024)); Assert.That(capture.ReadPage(0, 1024).Complete, Is.True);
        Assert.That(capture.GetInfo().Summaries.Single().Count, Is.EqualTo(1024));
        var saturated = new MetaMetricCapture(2); saturated.Record(MetaMetric.GasUsed, long.MaxValue); saturated.Record(MetaMetric.GasUsed, 1);
        Assert.That(saturated.GetInfo().Summaries.Single().Sum, Is.EqualTo(long.MaxValue));
    }
}
