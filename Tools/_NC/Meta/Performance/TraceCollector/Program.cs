// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var index = 0; index < args.Length; index += 2)
{
    if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(args[index], args[index + 1]))
        throw new ArgumentException("Expected unique --name value arguments.");
}
var metadataPath = Path.GetFullPath(options["--processes"]);
var output = Path.GetFullPath(options["--output"]);
var seconds = int.Parse(options.GetValueOrDefault("--seconds", "60"), System.Globalization.CultureInfo.InvariantCulture);
var maximumBytes = checked(long.Parse(options.GetValueOrDefault("--max-trace-mib", "512"), System.Globalization.CultureInfo.InvariantCulture) * 1024 * 1024);
if (seconds is < 1 or > 120 || maximumBytes is < 64 * 1024 * 1024 or > 2048L * 1024 * 1024 || Directory.Exists(output))
    throw new ArgumentException("Use a fresh output, duration1..120s and trace64..2048MiB.");
var metadataBytes = await File.ReadAllBytesAsync(metadataPath);
var expectedSeal = (await File.ReadAllTextAsync(metadataPath + ".sha256")).Trim();
if (Convert.ToHexStringLower(SHA256.HashData(metadataBytes)) != expectedSeal) throw new InvalidDataException("Owned process manifest seal mismatch.");
using var metadata = JsonDocument.Parse(metadataBytes);
var owned = metadata.RootElement.GetProperty("processes").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "server");
var pid = owned.GetProperty("pid").GetInt32(); var startTicks = owned.GetProperty("startUtcTicks").GetInt64();
using var process = Process.GetProcessById(pid);
void ValidateLifetime()
{
    process.Refresh();
    if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != startTicks) throw new InvalidOperationException("The exact owned server process lifetime ended.");
}
ValidateLifetime(); Directory.CreateDirectory(output);
var manifest = new Dictionary<string, object?>
{
    ["schemaVersion"] = 1, ["ownedProcessId"] = pid, ["ownedProcessStartUtcTicks"] = startTicks, ["processManifestSha256"] = expectedSeal,
    ["coverage"] = "one-owned-runtime-EventPipe-endpoint;no-system-ETW-or-foreign-processes;CPU-stack-samples+CLR+Robust.GameLoop",
    ["requestedSeconds"] = seconds, ["frequency"] = Stopwatch.Frequency, ["startedUtc"] = DateTimeOffset.UtcNow,
    ["collectorAssemblySha256"] = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(typeof(OwnedTrace).Assembly.Location))),
    ["clientLibraryVersion"] = typeof(DiagnosticsClient).Assembly.GetName().Version?.ToString(),
    ["parserLibraryVersion"] = typeof(TraceLog).Assembly.GetName().Version?.ToString(),
};
var tracePath = Path.Combine(output, "server.nettrace");
try
{
    EventPipeProvider[] providers =
    [
        new("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational),
        new("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, 0x40014019),
        new("Robust.GameLoop", EventLevel.Informational),
    ];
    manifest["providers"] = providers.Select(provider => new { provider.Name, provider.EventLevel, provider.Keywords }).ToArray();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 45));
    var client = new DiagnosticsClient(pid);
    using var session = await client.StartEventPipeSessionAsync(providers, true, 64, deadline.Token);
    var stopped = false;
    try
    {
        ValidateLifetime();
        manifest["captureStartTimestamp"] = Stopwatch.GetTimestamp(); manifest["threadsAtStart"] = OwnedTrace.ThreadCpu(process);
        var cpuBefore = process.TotalProcessorTime;
        await using var file = new FileStream(tracePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, true);
        var copy = OwnedTrace.CopyBounded(session.EventStream, file, maximumBytes, deadline.Token);
        var duration = Task.Delay(TimeSpan.FromSeconds(seconds), deadline.Token);
        var first = await Task.WhenAny(copy, duration);
        if (first == copy) { await copy; throw new InvalidDataException("Runtime EventPipe ended before the requested capture duration."); }
        ValidateLifetime(); manifest["captureStopTimestamp"] = Stopwatch.GetTimestamp(); manifest["threadsAtEnd"] = OwnedTrace.ThreadCpu(process);
        manifest["wholeProcessCpuDeltaTicks"] = (process.TotalProcessorTime - cpuBefore).Ticks;
        await session.StopAsync(deadline.Token); stopped = true; await copy; await file.FlushAsync(deadline.Token);
        manifest["traceBytes"] = file.Length;
    }
    finally
    {
        if (!stopped)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await session.StopAsync(stop.Token); } catch (Exception error) { manifest["sessionStopFailure"] = error.GetType().Name; }
        }
    }
    manifest["traceSha256"] = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(tracePath)));
    manifest["result"] = "recorded";
    manifest["analysis"] = OwnedTrace.Analyze(tracePath, output, pid, (long) manifest["captureStartTimestamp"]!, (long) manifest["captureStopTimestamp"]!);
}
catch (Exception error)
{
    manifest["result"] = "failed-or-incomplete"; manifest["failure"] = error.GetType().Name + ":" + error.Message;
    Environment.ExitCode = 1;
}
finally
{
    manifest["finishedUtc"] = DateTimeOffset.UtcNow;
    var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    await File.WriteAllBytesAsync(Path.Combine(output, "manifest.json"), bytes);
    await File.WriteAllTextAsync(Path.Combine(output, "manifest.json.sha256"), Convert.ToHexStringLower(SHA256.HashData(bytes)) + "\n");
}

internal static class OwnedTrace
{
    internal static object[] ThreadCpu(Process process)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Thread CPU endpoints require Windows or Linux.");
        if (process.Threads.Count > 4096) throw new InvalidOperationException("Owned process thread inventory exceeds the capture bound.");
        var rows = new List<object>();
        foreach (ProcessThread thread in process.Threads)
        {
            try
            {
                var started = OperatingSystem.IsWindows() || OperatingSystem.IsLinux() ? thread.StartTime.ToUniversalTime().Ticks : throw new PlatformNotSupportedException();
                rows.Add(new { thread.Id, startUtcTicks = started, kernelTicks = thread.PrivilegedProcessorTime.Ticks, userTicks = thread.UserProcessorTime.Ticks });
            }
            catch (InvalidOperationException) { }
        }
        return rows.ToArray();
    }
    internal static async Task CopyBounded(Stream input, Stream output, long limit, CancellationToken cancellation)
    {
        var buffer = new byte[64 * 1024]; long count = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellation); if (read == 0) return;
            if (count > limit - read) throw new InvalidDataException("Declared EventPipe trace byte limit reached.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellation); count += read;
        }
    }
    internal static object Analyze(string path, string output, int pid, long captureStart, long captureStop)
    {
        var etlx = TraceLog.CreateFromEventPipeDataFile(path, Path.Combine(output, "server.etlx"));
        using var log = new TraceLog(etlx);
        var gameThreads = new HashSet<int>(); var groups = new Dictionary<string, long>(StringComparer.Ordinal);
        var gcEvents = new Dictionary<string, long>(StringComparer.Ordinal); var intervals = new List<double>(); var pauses = new List<double>();
        var tickStarts = new Dictionary<int, (uint Tick, long Started)>(); var tickDurations = new List<double>(); var invalidTicks = 0; var partialTicks = 0;
        double? suspendedAt = null; var invalidPauses = 0; var samples = 0L; var unknown = 0L; var events = 0L;
        long? firstQpc = null; var firstRelativeMs = 0.0; var checkedFrequency = false; var observedFrequency = 0.0;
        foreach (var item in log.Events)
        {
            if (++events > 4_000_000) throw new InvalidDataException("Offline event-analysis bound exceeded.");
            var timestamp = Qpc(item);
            if (firstQpc == null) { firstQpc = timestamp; firstRelativeMs = item.TimeStampRelativeMSec; }
            else if (!checkedFrequency && item.TimeStampRelativeMSec - firstRelativeMs >= 100)
            {
                observedFrequency = (timestamp - firstQpc.Value) * 1000.0 / (item.TimeStampRelativeMSec - firstRelativeMs);
                if (Math.Abs(observedFrequency - Stopwatch.Frequency) > Stopwatch.Frequency * 0.00001)
                    throw new InvalidDataException("Trace and collector monotonic clocks cannot be directly correlated.");
                checkedFrequency = true;
            }
            if (item.ProcessID != pid || timestamp < captureStart || timestamp > captureStop || item.ProviderName != "Robust.GameLoop" || (int) item.ID is not (4 or 5)) continue;
            var tick = Convert.ToUInt32(item.PayloadValue(0), System.Globalization.CultureInfo.InvariantCulture);
            if ((int) item.ID == 4)
            {
                if (tickStarts.ContainsKey(item.ThreadID)) invalidTicks++;
                if (gameThreads.Count >= 16 && !gameThreads.Contains(item.ThreadID)) throw new InvalidDataException("Game-loop thread inventory bound exceeded.");
                gameThreads.Add(item.ThreadID); tickStarts[item.ThreadID] = (tick, timestamp);
            }
            else if ((int) item.ID == 5)
            {
                if (tickStarts.Remove(item.ThreadID, out var start) && start.Tick == tick && timestamp >= start.Started && tickDurations.Count < 32768)
                    tickDurations.Add((timestamp - start.Started) * 1000.0 / Stopwatch.Frequency);
                else if (!gameThreads.Contains(item.ThreadID)) partialTicks++;
                else invalidTicks++;
            }
        }
        if (!checkedFrequency) throw new InvalidDataException("The owned trace is too short to verify its timestamp frequency.");
        using var raw = new StreamWriter(Path.Combine(output, "cpu-samples.jsonl"));
        var previous = new Dictionary<int, double>();
        foreach (var item in log.Events)
        {
            var timestamp = Qpc(item);
            if (item.ProcessID != pid || timestamp < captureStart || timestamp > captureStop) continue;
            if (item.ProviderName == "Microsoft-Windows-DotNETRuntime" && item.EventName.StartsWith("GC/", StringComparison.Ordinal))
            {
                if (gcEvents.Count < 128 || gcEvents.ContainsKey(item.EventName)) gcEvents[item.EventName] = gcEvents.GetValueOrDefault(item.EventName) + 1;
                if (item.EventName.StartsWith("GC/SuspendEEStart", StringComparison.Ordinal))
                { if (suspendedAt != null) invalidPauses++; suspendedAt = item.TimeStampRelativeMSec; }
                else if (item.EventName.StartsWith("GC/RestartEEStop", StringComparison.Ordinal))
                {
                    if (suspendedAt is { } start && item.TimeStampRelativeMSec >= start && pauses.Count < 16384) pauses.Add(item.TimeStampRelativeMSec - start);
                    else invalidPauses++;
                    suspendedAt = null;
                }
            }
            if (item.ProviderName != "Microsoft-DotNETCore-SampleProfiler" || !gameThreads.Contains(item.ThreadID)) continue;
            if (++samples > 200000) throw new InvalidDataException("Offline CPU sample bound exceeded.");
            var group = "unresolved-or-runtime"; var method = "";
            var stack = item.CallStack();
            for (var depth = 0; depth < 128 && stack != null; depth++, stack = stack.Caller)
            {
                var name = stack.CodeAddress.FullMethodName ?? "";
                if (name.StartsWith("Content.", StringComparison.Ordinal) && name.Contains("System.", StringComparison.Ordinal))
                { method = name.Length <= 512 ? name : name[..512]; group = name[..Math.Min(512, name.IndexOf("System.", StringComparison.Ordinal) + 6)]; break; }
                if (name.StartsWith("Content.Meta.Runtime.", StringComparison.Ordinal)) { method = name.Length <= 512 ? name : name[..512]; group = "Content.Meta.Runtime"; break; }
            }
            if (method.Length == 0) unknown++;
            if (groups.Count < 512 || groups.ContainsKey(group)) groups[group] = groups.GetValueOrDefault(group) + 1;
            else groups["other-bounded-groups"] = groups.GetValueOrDefault("other-bounded-groups") + 1;
            if (previous.TryGetValue(item.ThreadID, out var before) && intervals.Count < 200000) intervals.Add(item.TimeStampRelativeMSec - before);
            previous[item.ThreadID] = item.TimeStampRelativeMSec;
            var sampleType = item.PayloadNames.Contains("Type", StringComparer.Ordinal) ? Convert.ToString(item.PayloadByName("Type"), System.Globalization.CultureInfo.InvariantCulture) : null;
            raw.WriteLine(JsonSerializer.Serialize(new { timestampRelativeMs = item.TimeStampRelativeMSec, item.ThreadID, item.EventName, sampleType, group, method }));
        }
        return new { lostEvents = log.EventsLost, observedTraceFrequency = observedFrequency, gameThreads = gameThreads.Order().ToArray(), samples, unresolvedSamples = unknown,
            fullEngineTickElapsedMs = log.EventsLost == 0 && invalidTicks == 0 ? Quantiles(tickDurations) : null,
            invalidTickPairs = invalidTicks, partialBoundaryTicks = partialTicks + tickStarts.Count,
            perSystemGameThreadStackSampleCounts = groups.OrderByDescending(pair => pair.Value).ToArray(), clrGcEventCounts = gcEvents,
            observedSampleIntervalsMs = Quantiles(intervals), pairedClrSuspensionElapsedMs = Quantiles(pauses), invalidClrSuspensionPairs = invalidPauses, openClrSuspensionAtEnd = suspendedAt != null,
            coverage = "owned-process-engine-thread-stack-samples;GC-events;OS-thread-CPU-endpoints-recorded-separately;sample-counts-are-not-exact-CPU-duration-or-per-system-p99",
            perSystemCpuTimingAccepted = false, requiredReview = "Inspect sample type/blocked-state/lost-events, runtime/rundown symbols, thread lifetime CPU deltas and sampling uncertainty before attributing CPU costs." };
    }
    private static object? Quantiles(List<double> values)
    {
        if (values.Count == 0) return null; values.Sort();
        double At(double percentile) => values[Math.Max(0, (int) Math.Ceiling(values.Count * percentile) - 1)];
        return new { count = values.Count, p50 = At(.5), p95 = At(.95), p99 = At(.99), maximum = values[^1] };
    }
    private static long Qpc(TraceEvent item)
    {
        // Absolute QPC is required to exclude provider startup/rundown outside the independently
        // captured owned-process interval. Relative trace times alone cannot prove that alignment.
#pragma warning disable CS0618
        return item.TimeStampQPC;
#pragma warning restore CS0618
    }
}
