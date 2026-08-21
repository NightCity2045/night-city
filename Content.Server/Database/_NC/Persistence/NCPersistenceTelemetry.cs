// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Diagnostics;
using System.Threading;
using Prometheus;

namespace Content.Server.Database;

/// <summary>
/// Thread-safe bounded telemetry for the neutral Living City persistence boundary.
/// </summary>
public sealed record NCPersistenceTelemetrySnapshot(
    long Reads,
    long Writes,
    long Failures,
    long VersionConflicts,
    long DuplicateReplays,
    double LatencyP50Milliseconds,
    double LatencyP95Milliseconds,
    double LatencyP99Milliseconds,
    int LatencySamples);

public static class NCPersistenceTelemetry
{
    private const int LatencyCapacity = 256;
    private static readonly object Sync = new();
    private static readonly double[] Latencies = new double[LatencyCapacity];
    private static readonly Counter Operations = Metrics.CreateCounter(
        "nc_living_city_persistence_operations_total",
        "Living City persistence operations by kind and result.",
        new CounterConfiguration { LabelNames = ["kind", "result"] });
    private static readonly Histogram Latency = Metrics.CreateHistogram(
        "nc_living_city_persistence_latency_ms",
        "Living City persistence boundary latency in milliseconds.",
        new HistogramConfiguration
        {
            LabelNames = ["kind"],
            Buckets = Histogram.ExponentialBuckets(0.25, 2, 12),
        });

    private static long _reads;
    private static long _writes;
    private static long _failures;
    private static long _versionConflicts;
    private static long _duplicateReplays;
    private static int _latencyWriteIndex;
    private static int _latencyCount;

    public static void Record(bool write, TimeSpan elapsed, object? result, bool failed)
    {
        var kind = write ? "write" : "read";
        if (write)
            Interlocked.Increment(ref _writes);
        else
            Interlocked.Increment(ref _reads);

        var status = MutationStatus(result);
        if (failed)
            Interlocked.Increment(ref _failures);
        if (status == NCPersistentMutationStatus.VersionConflict)
            Interlocked.Increment(ref _versionConflicts);
        if (status is NCPersistentMutationStatus.DuplicateCompleted or NCPersistentMutationStatus.InProgress)
            Interlocked.Increment(ref _duplicateReplays);

        var resultLabel = failed ? "failure" : status?.ToString() ?? "success";
        Operations.WithLabels(kind, resultLabel).Inc();
        Latency.WithLabels(kind).Observe(elapsed.TotalMilliseconds);
        lock (Sync)
        {
            Latencies[_latencyWriteIndex] = elapsed.TotalMilliseconds;
            _latencyWriteIndex = (_latencyWriteIndex + 1) % Latencies.Length;
            _latencyCount = Math.Min(Latencies.Length, _latencyCount + 1);
        }
    }

    public static NCPersistenceTelemetrySnapshot Snapshot()
    {
        double[] values;
        lock (Sync)
        {
            values = new double[_latencyCount];
            Array.Copy(Latencies, values, values.Length);
        }
        Array.Sort(values);
        return new NCPersistenceTelemetrySnapshot(
            Interlocked.Read(ref _reads),
            Interlocked.Read(ref _writes),
            Interlocked.Read(ref _failures),
            Interlocked.Read(ref _versionConflicts),
            Interlocked.Read(ref _duplicateReplays),
            Percentile(values, 0.50),
            Percentile(values, 0.95),
            Percentile(values, 0.99),
            values.Length);
    }

    private static NCPersistentMutationStatus? MutationStatus(object? result) => result switch
    {
        NCPersistentMutationResult mutation => mutation.Status,
        NCPersistentBatchMutationResult batch => batch.Status,
        _ => null,
    };

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0)
            return 0d;
        var index = Math.Clamp((int) Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }
}

public sealed partial class ServerDbManager
{
    private async Task<T> RunNCPersistentDbCommand<T>(bool write, Func<Task<T>> command)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var result = await RunDbCommand(command);
            NCPersistenceTelemetry.Record(write, Stopwatch.GetElapsedTime(startedAt), result, false);
            return result;
        }
        catch
        {
            NCPersistenceTelemetry.Record(write, Stopwatch.GetElapsedTime(startedAt), null, true);
            throw;
        }
    }
}
