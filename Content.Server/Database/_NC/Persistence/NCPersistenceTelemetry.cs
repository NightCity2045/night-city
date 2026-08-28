// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Diagnostics;
using System.Data.Common;
using System.Threading;
using Content.Shared._NC.Configuration;
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
    long CoalescedWrites,
    long RetryAttempts,
    long BackpressureRejections,
    long CircuitRejections,
    long StaleResultsRejected,
    int QueuedOperations,
    int InFlightOperations,
    int ConsecutiveFailures,
    DateTime? CircuitOpenUntil,
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
    private static readonly Counter WriteClasses = Metrics.CreateCounter(
        "nc_living_city_persistence_write_classes_total",
        "Durable Living City writes by persistence class.",
        new CounterConfiguration { LabelNames = ["class"] });

    private static long _reads;
    private static long _writes;
    private static long _failures;
    private static long _versionConflicts;
    private static long _duplicateReplays;
    private static long _coalescedWrites;
    private static long _retryAttempts;
    private static long _backpressureRejections;
    private static long _circuitRejections;
    private static long _staleResultsRejected;
    private static int _queuedOperations;
    private static int _inFlightOperations;
    private static int _consecutiveFailures;
    private static long _circuitOpenUntilTicks;
    private static int _latencyWriteIndex;
    private static int _latencyCount;

    public static void Record(
        bool write,
        NCPersistenceWriteClass? writeClass,
        TimeSpan elapsed,
        object? result,
        bool failed)
    {
        var kind = write ? "write" : "read";
        if (write)
            Interlocked.Increment(ref _writes);
        else
            Interlocked.Increment(ref _reads);
        if (write && writeClass is { } classification)
            WriteClasses.WithLabels(classification.ToString()).Inc();

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

    /// <summary>
    /// Preserves the pre-classification telemetry API for read operations and legacy callers.
    /// New durable writes should pass an explicit <see cref="NCPersistenceWriteClass"/>.
    /// </summary>
    public static void Record(bool write, TimeSpan elapsed, object? result, bool failed)
    {
        Record(write, null, elapsed, result, failed);
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
            Interlocked.Read(ref _coalescedWrites),
            Interlocked.Read(ref _retryAttempts),
            Interlocked.Read(ref _backpressureRejections),
            Interlocked.Read(ref _circuitRejections),
            Interlocked.Read(ref _staleResultsRejected),
            Volatile.Read(ref _queuedOperations),
            Volatile.Read(ref _inFlightOperations),
            Volatile.Read(ref _consecutiveFailures),
            ReadCircuitOpenUntil(),
            Percentile(values, 0.50),
            Percentile(values, 0.95),
            Percentile(values, 0.99),
            values.Length);
    }

    public static void RecordCoalescedWrite() => Interlocked.Increment(ref _coalescedWrites);

    public static void RecordRetry() => Interlocked.Increment(ref _retryAttempts);

    public static void RecordBackpressureRejection() => Interlocked.Increment(ref _backpressureRejections);

    public static void RecordCircuitRejection() => Interlocked.Increment(ref _circuitRejections);

    public static void RecordStaleResultRejection() => Interlocked.Increment(ref _staleResultsRejected);

    public static void SetRuntimeState(int queued, int inFlight, int consecutiveFailures, DateTime? circuitOpenUntil)
    {
        Volatile.Write(ref _queuedOperations, Math.Max(0, queued));
        Volatile.Write(ref _inFlightOperations, Math.Max(0, inFlight));
        Volatile.Write(ref _consecutiveFailures, Math.Max(0, consecutiveFailures));
        Interlocked.Exchange(ref _circuitOpenUntilTicks, circuitOpenUntil?.Ticks ?? 0L);
    }

    private static DateTime? ReadCircuitOpenUntil()
    {
        var ticks = Interlocked.Read(ref _circuitOpenUntilTicks);
        return ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;
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
    private readonly object _ncPersistenceSync = new();
    private SemaphoreSlim? _ncPersistenceConcurrency;
    private int _ncPersistenceQueued;
    private int _ncPersistenceInFlight;
    private int _ncPersistenceConsecutiveFailures;
    private DateTime? _ncPersistenceCircuitOpenUntil;

    private async Task<T> RunNCPersistentDbCommand<T>(
        bool write,
        NCPersistenceWriteClass? writeClass,
        Func<Task<T>> command)
    {
        var gate = GetNCPersistenceGate();
        EnterNCPersistenceQueue();
        await gate.WaitAsync();
        MoveNCPersistenceToInFlight();
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var maximumRetries = Math.Clamp(_cfg.GetCVar(NCCVars.PersistenceMaximumRetries), 0, 8);
            for (var attempt = 0;; attempt++)
            {
                try
                {
                    var result = await RunDbCommand(command);
                    RegisterNCPersistenceSuccess();
                    NCPersistenceTelemetry.Record(
                        write,
                        writeClass,
                        Stopwatch.GetElapsedTime(startedAt),
                        result,
                        false);
                    return result;
                }
                catch (Exception exception) when (attempt < maximumRetries && IsTransient(exception))
                {
                    NCPersistenceTelemetry.RecordRetry();
                    await Task.Delay(GetRetryDelay(attempt));
                }
            }
        }
        catch
        {
            RegisterNCPersistenceFailure();
            NCPersistenceTelemetry.Record(
                write,
                writeClass,
                Stopwatch.GetElapsedTime(startedAt),
                null,
                true);
            throw;
        }
        finally
        {
            LeaveNCPersistenceInFlight();
            gate.Release();
        }
    }

    private SemaphoreSlim GetNCPersistenceGate()
    {
        lock (_ncPersistenceSync)
        {
            return _ncPersistenceConcurrency ??= new SemaphoreSlim(
                Math.Clamp(_cfg.GetCVar(NCCVars.PersistenceMaximumConcurrentOperations), 1, 256));
        }
    }

    private void EnterNCPersistenceQueue()
    {
        lock (_ncPersistenceSync)
        {
            var now = DateTime.UtcNow;
            if (_ncPersistenceCircuitOpenUntil is { } openUntil && openUntil > now)
            {
                NCPersistenceTelemetry.RecordCircuitRejection();
                throw new NCPersistenceCircuitOpenException(openUntil);
            }

            var maximumQueued = Math.Clamp(
                _cfg.GetCVar(NCCVars.PersistenceMaximumQueuedOperations), 1, 100_000);
            if (_ncPersistenceQueued >= maximumQueued)
            {
                NCPersistenceTelemetry.RecordBackpressureRejection();
                throw new NCPersistenceBackpressureException(maximumQueued);
            }

            _ncPersistenceQueued++;
            PublishNCPersistenceRuntimeState();
        }
    }

    private void MoveNCPersistenceToInFlight()
    {
        lock (_ncPersistenceSync)
        {
            _ncPersistenceQueued = Math.Max(0, _ncPersistenceQueued - 1);
            _ncPersistenceInFlight++;
            PublishNCPersistenceRuntimeState();
        }
    }

    private void LeaveNCPersistenceInFlight()
    {
        lock (_ncPersistenceSync)
        {
            _ncPersistenceInFlight = Math.Max(0, _ncPersistenceInFlight - 1);
            PublishNCPersistenceRuntimeState();
        }
    }

    private void RegisterNCPersistenceSuccess()
    {
        lock (_ncPersistenceSync)
        {
            _ncPersistenceConsecutiveFailures = 0;
            _ncPersistenceCircuitOpenUntil = null;
            PublishNCPersistenceRuntimeState();
        }
    }

    private void RegisterNCPersistenceFailure()
    {
        lock (_ncPersistenceSync)
        {
            _ncPersistenceConsecutiveFailures++;
            var threshold = Math.Clamp(_cfg.GetCVar(NCCVars.PersistenceCircuitFailureThreshold), 1, 1000);
            if (_ncPersistenceConsecutiveFailures >= threshold)
            {
                var seconds = Math.Clamp(_cfg.GetCVar(NCCVars.PersistenceCircuitOpenSeconds), 1, 3600);
                _ncPersistenceCircuitOpenUntil = DateTime.UtcNow.AddSeconds(seconds);
            }
            PublishNCPersistenceRuntimeState();
        }
    }

    private void PublishNCPersistenceRuntimeState()
    {
        NCPersistenceTelemetry.SetRuntimeState(
            _ncPersistenceQueued,
            _ncPersistenceInFlight,
            _ncPersistenceConsecutiveFailures,
            _ncPersistenceCircuitOpenUntil);
    }

    private TimeSpan GetRetryDelay(int priorAttempts)
    {
        var initial = Math.Clamp(
            _cfg.GetCVar(NCCVars.PersistenceInitialRetryDelayMilliseconds), 1, 60_000);
        var maximum = Math.Clamp(
            _cfg.GetCVar(NCCVars.PersistenceMaximumRetryDelayMilliseconds), initial, 300_000);
        var multiplier = 1L << Math.Min(priorAttempts, 20);
        return TimeSpan.FromMilliseconds(Math.Min(maximum, initial * multiplier));
    }

    private static bool IsTransient(Exception exception) =>
        exception is TimeoutException or DbException;
}

public sealed class NCPersistenceBackpressureException(int maximumQueuedOperations) : Exception(
    $"Living City persistence queue reached its configured limit of {maximumQueuedOperations} operations.");

public sealed class NCPersistenceCircuitOpenException(DateTime openUntil) : Exception(
    $"Living City persistence circuit is open until {openUntil:O}.");
