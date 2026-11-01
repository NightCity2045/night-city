// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Diagnostics;

namespace Content.Server._NC.Meta.Runtime;

public sealed partial class NCMetaRuntimeSystem
{
    private IMetaMetrics? _metrics;
    private long _nextMetricSample;
    public IMetaMetrics? Metrics => _metrics;
    public long MetricTick => _timing.CurTick.Value;
    /// <summary>Trusted diagnostics only. Attaching a sink grants no source, process or native authority.</summary>
    public void SetMetrics(IMetaMetrics? metrics)
    {
        _metrics = metrics; _scheduler.Metrics = metrics; _nextMetricSample = 0;
        foreach (var instance in _processes.Values) instance.Process.Metrics = metrics;
    }
    private void SamplePerformanceCounters()
    {
        if (!MetaMetrics.IsEnabled(_metrics) || _now < _nextMetricSample) return;
        _nextMetricSample = _now + 1000;
        // A bounded once/second diagnostic sample; never add an every-tick resident-context scan.
        long bytes = 0;
        foreach (var instance in _processes.Values) bytes += instance.Process.MemoryBytes;
        MetaMetrics.Record(_metrics, MetaMetric.GuestLiveBytes, bytes, MetricTick);
        MetaMetrics.Record(_metrics, MetaMetric.ReservedBytes, _scheduler.ReservedMemoryBytes, MetricTick);
        MetaMetrics.Record(_metrics, MetaMetric.RegisteredProcesses, _scheduler.RegisteredProcesses, MetricTick);
        MetaMetrics.Record(_metrics, MetaMetric.RegisteredPrincipals, _scheduler.RegisteredPrincipals, MetricTick);
        MetaMetrics.Record(_metrics, MetaMetric.QueuedIntents, _intents.Count, MetricTick);
    }
}
