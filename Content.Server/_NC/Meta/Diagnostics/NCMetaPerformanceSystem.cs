// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Meta.Diagnostics;
using Content.Meta.Language;
using Content.Meta.Verification;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;
using Robust.Shared.Timing;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Content.Server._NC.Meta.Diagnostics;

public sealed record NCMetaPerformanceRequest(string Workload = "A", string Mode = "Normal", int Seed = 90210,
    int WarmupSeconds = 30, int CaptureSeconds = 60, bool Baseline = false, string DeclaredSourceHash = "", string DeclaredResourceHash = "");
public sealed record NCMetaPerformanceStatus(Guid Id, string Stage, string Error, int RequestedInstallations,
    int RequestedLoaded, int RequestedActive, int ActualInstalled, int ActualLoaded, int PhysicalActors,
    long StimuliAccepted, long StimuliRejected, long NativeBotSteps, long StartTick, long EndTick,
    string Workload, string Mode, bool Baseline, string SourceHash, string CatalogHash,
    string ServerAssemblyHash, string ArtifactSetHash, string DeclaredSourceHash, string DeclaredResourceHash,
    string MeasurementCoverage, MetaMetricCaptureInfo? Capture, NCMetaEngineTickCoverage? EngineTicks);

/// <summary>Opt-in isolated native load and bounded recording. It is never a guest API or gameplay authority.</summary>
public sealed partial class NCMetaPerformanceSystem : EntitySystem
{
    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCMetaPersistenceSystem _persistence = default!;
    [Dependency] private IGameTiming _timing = default!;
    private Guid _id;
    private NCMetaPerformanceRequest? _request;
    private MetaMetricCapture? _capture;
    private MetaMetricCapture? _warmupMetrics;
    private string _stage = "Idle";
    private string _error = "";
    private string _assemblyHash = "";
    private long _stageDeadline;
    private long _nextProcessSample;
    private long _previousAllocated;
    private TimeSpan _previousGcPause;
    private long _startTick;
    private long _endTick;
    private Process? _process;
    private Task? _preparation;
    private NCMetaEngineTickListener? _tickListener;
    public bool CanRunIsolated => Environment.GetEnvironmentVariable("NC_DIAGNOSTICS_ISOLATED") == "1" &&
        Environment.GetEnvironmentVariable("NC_DIAGNOSTICS_PROFILE") == "fixture-admin";

    public Guid TryStart(NCMetaPerformanceRequest request)
    {
        if (!CanRunIsolated || _stage is not ("Idle" or "Finished" or "Failed" or "Stopped") ||
            request.Workload is not ("A" or "B" or "C" or "C-resident") || request.Mode is not ("Normal" or "Burst" or "Heavy" or "Hostile") ||
            request.WarmupSeconds is < 0 or > 120 || request.CaptureSeconds is < 1 or > 60 ||
            !ValidDeclaredHash(request.DeclaredSourceHash) || !ValidDeclaredHash(request.DeclaredResourceHash) ||
            !_persistence.TryCaptureTime(out _, out _)) return Guid.Empty;
        // A previous fixture must be explicitly cleaned before another can replace it.
        if (_ownedEntities.Count != 0) return Guid.Empty;
        _id = Guid.NewGuid(); _request = request; _stage = "Preparing"; _error = ""; _capture = null;
        _startTick = _endTick = 0; _assemblyHash = ""; _installed = _loaded = 0; _artifactDigests.Clear();
        _stimuliAccepted = _stimuliRejected = _botSteps = 0;
        _preparation = PrepareWorkloadAsync(_id, request);
        return _id;
    }
    private static bool ValidDeclaredHash(string hash) => hash.Length == 0 || hash.Length == 64 && hash.All(char.IsAsciiHexDigit);
    public bool TryStop(Guid id, bool cleanUp = false)
    {
        if (!CanRunIsolated || id != _id || _id == Guid.Empty) return false;
        _capture?.Stop(); _warmupMetrics?.Stop(); _runtime.SetMetrics(null); _stage = "Stopped"; _endTick = _timing.CurTick.Value;
        _tickListener?.Dispose();
        StopWorkload(); if (cleanUp) CleanUpWorkload(); return true;
    }
    public NCMetaPerformanceStatus? GetStatus(Guid id)
    {
        if (id != _id || id == Guid.Empty || _request == null) return null;
        var shape = Shape(_request.Workload);
        return new(_id, _stage, _error, shape.Installed, shape.Loaded, shape.Active, _installed, _loaded,
            _owners.Count, _stimuliAccepted, _stimuliRejected, _botSteps, _startTick, _endTick,
            _request.Workload, _request.Mode, _request.Baseline, WorkloadSourceHash, MetaApiCatalog.Default.Hash,
            _assemblyHash, Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', _artifactDigests.Order(StringComparer.Ordinal))))), _request.DeclaredSourceHash, _request.DeclaredResourceHash,
            "native-physical-test-bots-and-production-messageport-runtime;elapsed-not-cpu;no-fabricated-player-sessions", _capture?.GetInfo(), _tickListener?.GetCoverage());
    }
    public MetaMetricPage? ReadSamples(Guid id, int offset, int count) => id == _id && _capture != null && !_capture.Enabled ? _capture.ReadPage(offset, count) : null;
    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        using var instrumentation = MetaMetricScope.Measure(_stage == "Capturing" ? _capture : null, MetaMetric.InstrumentationElapsed, _timing.CurTick.Value);
        PumpWorkloadCallbacks();
        if (_request == null || _stage is not ("Warmup" or "Capturing")) return;
        if (!_persistence.TryCaptureTime(out _, out _)) { FailRun("WorldInactive"); return; }
        AdvanceNativeBots(); DriveNativeStimuli();
        var now = Stopwatch.GetTimestamp();
        if (_stage == "Warmup" && now >= _stageDeadline)
        {
            _warmupMetrics?.Stop(); _runtime.SetMetrics(_capture); _stage = "Capturing";
            _tickListener?.SetSink(_capture);
            _stimuliAccepted = _stimuliRejected = _botSteps = 0;
            _startTick = _timing.CurTick.Value; _stageDeadline = now + _request.CaptureSeconds * Stopwatch.Frequency;
            _nextProcessSample = now; _previousAllocated = GC.GetTotalAllocatedBytes(); _previousGcPause = GC.GetTotalPauseDuration();
            _process?.Dispose(); _process = Process.GetCurrentProcess();
        }
        if (_stage != "Capturing") return;
        if (now >= _nextProcessSample)
        {
            _nextProcessSample = now + Stopwatch.Frequency;
            MetaMetrics.Record(_capture, MetaMetric.ManagedBytes, GC.GetTotalMemory(false), _timing.CurTick.Value);
            _process!.Refresh(); MetaMetrics.Record(_capture, MetaMetric.ProcessWorkingSetBytes, _process.WorkingSet64, _timing.CurTick.Value);
            var allocated = GC.GetTotalAllocatedBytes(); MetaMetrics.Record(_capture, MetaMetric.AllocatedBytes, Math.Max(0, allocated - _previousAllocated), _timing.CurTick.Value); _previousAllocated = allocated;
            var pause = GC.GetTotalPauseDuration(); MetaMetrics.Record(_capture, MetaMetric.GcPauseTicks,
                Math.Max(0, (long) ((pause - _previousGcPause).TotalSeconds * Stopwatch.Frequency)), _timing.CurTick.Value); _previousGcPause = pause;
        }
        if (now < _stageDeadline) return;
        _capture!.Stop(); _runtime.SetMetrics(null); _endTick = _timing.CurTick.Value; _stage = "Finished";
        _tickListener?.Dispose();
        StopWorkload(); _process?.Dispose(); _process = null;
    }
    private void FailRun(string error)
    {
        _error = error; _stage = "Failed"; _capture?.Stop(); _warmupMetrics?.Stop(); _runtime.SetMetrics(null); _endTick = _timing.CurTick.Value;
        _tickListener?.Dispose();
        StopWorkload(); _process?.Dispose(); _process = null;
    }
    private static async Task<string> AssemblyHashAsync()
    {
        await using var stream = File.OpenRead(typeof(NCMetaRuntimeSystem).Assembly.Location);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }
    public override void Shutdown()
    {
        _capture?.Stop(); _warmupMetrics?.Stop(); _runtime.SetMetrics(null); StopWorkload(); _process?.Dispose();
        _tickListener?.Dispose();
        if (_compiler != null) _ = _compiler.DisposeAsync();
        base.Shutdown();
    }
}
