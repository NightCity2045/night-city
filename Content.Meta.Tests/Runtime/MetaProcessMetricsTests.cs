// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Diagnostics;
using Content.Meta.Bytecode;
using Content.Meta.Diagnostics;
using Content.Meta.Language;
using Content.Meta.Runtime;
using Content.Meta.Tests.Bytecode;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Runtime;

[TestFixture]
public sealed class MetaProcessMetricsTests
{
    [Test]
    public void LatestDeliveryMeasuresNewestPayloadAndOriginalPendingSlotSeparately()
    {
        var compiled = new MetaCompiler().Compile(new([new("test.meta", "module test.metrics; import meta.doors; link door:Door=\"door\"; watch door.stateChanged(state) {}")], "test.metrics"));
        Assert.That(compiled.Success, Is.True, string.Join(',', compiled.Diagnostics.Select(value => value.Code)));
        var module = compiled.Module!;
        var process = new MetaProcess(MetaVerifier.Verify(MetaBytecodeWriter.Write(module), MetaApiCatalog.Default.CreateVerificationCatalog(module)));
        var capture = new MetaMetricCapture(); process.Metrics = capture;
        process.TryBindLink(0, 1); process.TryStartMain();
        var stamp = process.CreateRecord(process.GetTypeId("meta.system.SampleStamp"), [MetaValue.Int(1), MetaValue.Int(1), MetaValue.Int(1)]);
        var state = process.CreateRecord(process.GetTypeId("meta.doors.DoorState"),
            [MetaValue.Bool(false), process.CreateEnum(process.GetTypeId("meta.doors.DoorPhase"), 0), stamp]);
        Assert.That(process.TryEnqueueEvent(0, [state], 1), Is.True);
        var until = Stopwatch.GetTimestamp() + Math.Max(1, Stopwatch.Frequency / 1000);
        while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(16);
        Assert.That(process.TryEnqueueEvent(0, [state], 2), Is.True);
        Assert.That(process.TryEnqueueEvent(0, [state], 2), Is.False);
        Assert.That(process.QueuedEvents, Is.EqualTo(1));
        Assert.That(process.TryStartNextEvent(), Is.True);
        process.RunSlice(100, 0);
        var samples = capture.ReadPage(0).Samples;
        Assert.Multiple(() =>
        {
            Assert.That(samples.Where(value => value.Metric == MetaMetric.EventsAccepted).Sum(value => value.Value), Is.EqualTo(2));
            Assert.That(samples.Where(value => value.Metric == MetaMetric.EventsCoalesced).Sum(value => value.Value), Is.EqualTo(1));
            Assert.That(samples.Where(value => value.Metric == MetaMetric.EventsRejected).Sum(value => value.Value), Is.EqualTo(1));
            Assert.That(samples.Where(value => value.Metric == MetaMetric.EventsStarted).Sum(value => value.Value), Is.EqualTo(1));
            Assert.That(samples.Single(value => value.Metric == MetaMetric.EventSlotLatency).Value,
                Is.GreaterThan(samples.Single(value => value.Metric == MetaMetric.EventQueueLatency).Value));
            Assert.That(process.State, Is.EqualTo(MetaProcessState.Idle));
        });
    }

    [Test]
    public void GuestCollectionSpansCarryTrustedTickAndDoNotChangeGasOrGuestState()
    {
        var artifact = MetaBytecodeTests.Verify(MetaBytecodeTests.Module([new(0, MetaOpcode.Safepoint), new(0, MetaOpcode.Br, 0)]));
        var capture = new MetaMetricCapture();
        var observed = new MetaProcess(artifact, limits: new() { MemoryBytes = 4096 }) { Metrics = capture };
        var baseline = new MetaProcess(artifact, limits: new() { MemoryBytes = 4096 });
        foreach (var process in new[] { observed, baseline })
        {
            process.SetClock(1, 17, 1); process.TryStart(0, []);
            for (var item = 0; item < 6; item++) process.CreateString(new string('x', 100));
            process.RunSlice(1, 0);
            for (var step = 0; step < 1000 && process.State == MetaProcessState.Collecting; step++) process.RunSlice(32, 0);
        }
        Assert.Multiple(() =>
        {
            Assert.That(observed.State, Is.EqualTo(baseline.State));
            Assert.That(observed.ActivationGas, Is.EqualTo(baseline.ActivationGas));
            Assert.That(observed.MemoryBytes, Is.EqualTo(baseline.MemoryBytes));
            Assert.That(capture.ReadPage(0).Samples.Where(value => value.Metric == MetaMetric.GuestGcElapsed), Is.Not.Empty);
            Assert.That(capture.ReadPage(0).Samples.Where(value => value.Metric is MetaMetric.VmSliceElapsed or MetaMetric.GuestGcElapsed)
                .All(value => value.Context == 17), Is.True);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void BrokenMetricsObserversCannotFaultGuestExecution(bool brokenGetter)
    {
        var process = new MetaProcess(MetaBytecodeTests.Verify(MetaBytecodeTests.Module(
            [new(0, MetaOpcode.Const), new(0, MetaOpcode.Return)], [MetaBytecodeTests.Int(5)], result: 3, maximum: 1)))
            { Metrics = new BrokenMetrics(brokenGetter) };
        Assert.That(process.TryStart(0, []), Is.True);
        Assert.That(process.RunSlice(100, 0).Instructions, Is.EqualTo(2));
        Assert.That(process.LastResult!.Value.AsInt(), Is.EqualTo(5));
        Assert.That(process.Fault, Is.Null);
    }

    private sealed class BrokenMetrics(bool brokenGetter) : IMetaMetrics
    {
        public bool Enabled => brokenGetter ? throw new InvalidOperationException("observer unavailable") : true;
        public void Record(MetaMetric metric, long value, long context = 0) => throw new InvalidOperationException("observer unavailable");
    }
}
