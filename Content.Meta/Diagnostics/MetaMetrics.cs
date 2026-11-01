// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Diagnostics;
using System.Threading;

namespace Content.Meta.Diagnostics;

/// <summary>Fixed, trusted labels. Source text, principals and authority handles are never metric labels.</summary>
public enum MetaMetric
{
    RuntimeElapsed, SchedulerElapsed, VmSliceElapsed, GuestGcElapsed, HostReadElapsed, HostCommitElapsed,
    HardwareBeforeElapsed, HardwareAfterElapsed, CompletionPumpElapsed, PublishElapsed, EventIngressElapsed,
    NativeNetworkElapsed, NativeSensorElapsed, NativeActuatorElapsed, ApplicationElapsed, ViewProjectionElapsed,
    EventQueueLatency, EventSlotLatency, CommandAdmissionLatency, CompletionLatency,
    EventsAccepted, EventsRejected, EventsCoalesced, EventsStarted, CommandsAccepted, CommandsRejected,
    CompletionsAccepted, NativeDeliveredBytes, NativeExpiredBytes, NativeRejectedBytes,
    AppFullBytes, AppPatchBytes, AppRecipients, AppResyncs,
    GuestLiveBytes, ReservedBytes, RegisteredProcesses, RegisteredPrincipals, RunnablePrincipals, QueuedIntents,
    ManagedBytes, ProcessWorkingSetBytes, AllocatedBytes, GcPauseTicks, CompilerQueued, CompilerRunning,
    CompilerQueueLatency, CompilerElapsed, CompilerRejected, NativeJobElapsed, NativeJobRejected, GasUsed, Slices, InstrumentationElapsed,
    EngineTickElapsed, EngineListenerElapsed,
    Count
}

public enum MetaMetricUnit { ElapsedStopwatchTicks, Count, Bytes }
public interface IMetaMetrics
{
    bool Enabled { get; }
    void Record(MetaMetric metric, long value, long context = 0);
}

public static class MetaMetrics
{
    public static MetaMetricUnit Unit(MetaMetric metric) => metric switch
    {
        <= MetaMetric.CompletionLatency or MetaMetric.GcPauseTicks or MetaMetric.CompilerQueueLatency or MetaMetric.CompilerElapsed or MetaMetric.NativeJobElapsed or MetaMetric.InstrumentationElapsed or MetaMetric.EngineTickElapsed or MetaMetric.EngineListenerElapsed => MetaMetricUnit.ElapsedStopwatchTicks,
        MetaMetric.NativeDeliveredBytes or MetaMetric.NativeExpiredBytes or MetaMetric.NativeRejectedBytes or MetaMetric.AppFullBytes or MetaMetric.AppPatchBytes or
            MetaMetric.GuestLiveBytes or MetaMetric.ReservedBytes or MetaMetric.ManagedBytes or MetaMetric.ProcessWorkingSetBytes or MetaMetric.AllocatedBytes => MetaMetricUnit.Bytes,
        _ => MetaMetricUnit.Count
    };
    public static bool IsEnabled(IMetaMetrics? sink)
    {
        try { return sink?.Enabled == true; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { return false; }
    }
    public static long Timestamp(IMetaMetrics? sink) => IsEnabled(sink) ? Stopwatch.GetTimestamp() : 0;
    public static void Record(IMetaMetrics? sink, MetaMetric metric, long value = 1, long context = 0)
    {
        if (!IsEnabled(sink)) return;
        try { sink!.Record(metric, value, context); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { }
    }
    public static void Latency(IMetaMetrics? sink, MetaMetric metric, long started, long context = 0)
    {
        if (started != 0 && IsEnabled(sink)) Record(sink, metric, Math.Max(0, Stopwatch.GetTimestamp() - started), context);
    }
}

/// <summary>No clock read or allocation when disabled; measures elapsed occupancy, never claims CPU time.</summary>
public readonly struct MetaMetricScope : IDisposable
{
    private readonly IMetaMetrics? _sink;
    private readonly MetaMetric _metric;
    private readonly long _started;
    private readonly long _context;
    private MetaMetricScope(IMetaMetrics? sink, MetaMetric metric, long context)
    { _sink = sink; _metric = metric; _context = context; _started = MetaMetrics.Timestamp(sink); }
    public static MetaMetricScope Measure(IMetaMetrics? sink, MetaMetric metric, long context = 0) => new(sink, metric, context);
    public void Dispose() => MetaMetrics.Latency(_sink, _metric, _started, _context);
}

public readonly record struct MetaMetricSample(long Timestamp, long Value, long Context, MetaMetric Metric);
public sealed record MetaMetricSummary(MetaMetric Metric, MetaMetricUnit Unit, long Count, long Sum, long Minimum, long Maximum);
public sealed record MetaMetricPage(int Offset, int NextOffset, bool Complete, MetaMetricSample[] Samples);
public sealed record MetaMetricCaptureInfo(long Frequency, int Capacity, int ReservedSamples, long DroppedSamples, bool Enabled, MetaMetricSummary[] Summaries);

/// <summary>Fixed native buffer, no overwrite, bounded cold pages. Overflow invalidates raw coverage explicitly.</summary>
public sealed class MetaMetricCapture : IMetaMetrics
{
    public const int MaximumSamples = 262144;
    private readonly MetaMetricSample[] _samples;
    private readonly int[] _published;
    private readonly long[] _counts = new long[(int) MetaMetric.Count];
    private readonly long[] _sums = new long[(int) MetaMetric.Count];
    private readonly long[] _minima = Enumerable.Repeat(long.MaxValue, (int) MetaMetric.Count).ToArray();
    private readonly long[] _maxima = new long[(int) MetaMetric.Count];
    private int _reserved;
    private int _enabled = 1;
    private long _dropped;
    public bool Enabled => Volatile.Read(ref _enabled) != 0;
    public long DroppedSamples => Interlocked.Read(ref _dropped);
    public MetaMetricCapture(int maximumSamples = MaximumSamples)
    {
        if (maximumSamples is < 1 or > MaximumSamples) throw new ArgumentOutOfRangeException(nameof(maximumSamples));
        _samples = new MetaMetricSample[maximumSamples]; _published = new int[maximumSamples];
    }
    public void Stop() => Interlocked.Exchange(ref _enabled, 0);
    public void Record(MetaMetric metric, long value, long context = 0)
    {
        if (!Enabled || metric is < 0 or >= MetaMetric.Count || value < 0) return;
        var label = (int) metric;
        Interlocked.Increment(ref _counts[label]); AddSaturated(ref _sums[label], value);
        long observed;
        while (value < (observed = Interlocked.Read(ref _minima[label])) && Interlocked.CompareExchange(ref _minima[label], value, observed) != observed) { }
        while (value > (observed = Interlocked.Read(ref _maxima[label])) && Interlocked.CompareExchange(ref _maxima[label], value, observed) != observed) { }
        var index = Interlocked.Increment(ref _reserved) - 1;
        if ((uint) index >= (uint) _samples.Length) { Interlocked.Increment(ref _dropped); return; }
        _samples[index] = new(Stopwatch.GetTimestamp(), value, context, metric); Volatile.Write(ref _published[index], 1);
    }
    private static void AddSaturated(ref long target, long value)
    {
        long previous;
        do { previous = Interlocked.Read(ref target); }
        while (Interlocked.CompareExchange(ref target, previous > long.MaxValue - value ? long.MaxValue : previous + value, previous) != previous);
    }
    public MetaMetricCaptureInfo GetInfo()
    {
        var values = new List<MetaMetricSummary>();
        for (var label = 0; label < (int) MetaMetric.Count; label++)
        {
            var count = Interlocked.Read(ref _counts[label]);
            if (count == 0) continue;
            values.Add(new((MetaMetric) label, MetaMetrics.Unit((MetaMetric) label), count, Interlocked.Read(ref _sums[label]),
                Interlocked.Read(ref _minima[label]), Interlocked.Read(ref _maxima[label])));
        }
        return new(Stopwatch.Frequency, _samples.Length, Math.Min(Volatile.Read(ref _reserved), _samples.Length), DroppedSamples, Enabled, values.ToArray());
    }
    public MetaMetricPage ReadPage(int offset, int count = 512)
    {
        if (offset < 0 || count is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(offset));
        var end = Math.Min(Volatile.Read(ref _reserved), _samples.Length);
        if (offset > end) throw new ArgumentOutOfRangeException(nameof(offset));
        var values = new List<MetaMetricSample>(Math.Min(count, end - offset));
        var next = offset;
        while (next < end && values.Count < count && Volatile.Read(ref _published[next]) != 0) values.Add(_samples[next++]);
        return new(offset, next, !Enabled && next == end && DroppedSamples == 0, values.ToArray());
    }
}
