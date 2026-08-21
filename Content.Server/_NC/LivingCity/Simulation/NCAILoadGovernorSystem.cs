// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Diagnostics;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Simulation;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Simulation;

/// <summary>
/// Active-round-only adaptive governor for A-Life detail and background throughput. Producers report their bounded
/// queue state in O(1); this system never scans the actor catalog or ECS population.
/// </summary>
public sealed partial class NCAILoadGovernorSystem : EntitySystem
{
    private const int TelemetryFrameCapacity = 256;
    private const string DefaultPolicyId = "NCDefaultAILoadGovernorPolicy";
    private const string DefaultRelevancePolicyId = "NCDefaultCityRelevancePolicy";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;

    private readonly Dictionary<NCAILoadQueueKind, float> _queueRatios = [];
    private readonly HashSet<NCAILoadBacklogKind> _backlogs = [];
    private readonly Dictionary<Guid, int> _materializationReservations = [];
    private readonly Dictionary<NCAILoadDispatchKind, float> _dispatchCredits = [];
    private readonly NCAILoadGovernorStateMachine _stateMachine = new();
    private readonly double[] _workMillisecondsByKindWindow = new double[Enum.GetValues<NCAILoadWorkKind>().Length];
    private readonly float[] _lastWorkMillisecondsByKind = new float[Enum.GetValues<NCAILoadWorkKind>().Length];
    private readonly float[] _frameTelemetry = new float[TelemetryFrameCapacity];

    private NCAILoadGovernorPolicyPrototype? _policy;
    private NCAILoadGovernorThresholds _thresholds;
    private NCSessionEpoch _epoch;
    private NCActiveWorldTime _nextSample;
    private NCActiveWorldTime _lastTransitionAt;
    private NCAILoadPressureReason _reasons;
    private float _smoothedFrameMilliseconds;
    private double _workMillisecondsWindow;
    private double _frameMillisecondsWindow;
    private float _lastWorkMilliseconds;
    private long _lastAllocatedBytes;
    private long _allocatedBytesSinceLastSample;
    private int _lastGen0Collections;
    private int _lastGen1Collections;
    private int _lastGen2Collections;
    private int _gen0CollectionsSinceLastSample;
    private int _gen1CollectionsSinceLastSample;
    private int _gen2CollectionsSinceLastSample;
    private int _frameTelemetryWriteIndex;
    private int _frameTelemetryCount;
    private float _alifeWorkRatio;
    private float _maximumQueueRatio;
    private int _materializedActors;
    private int _baseSoftMaterializedBudget;
    private int _hardMaterializedBudget;
    private int _reservedMaterializations;
    private int _backloggedSubsystems;
    private long _transitionCount;
    private long _admissionAccepted;
    private long _admissionRejected;
    private bool _active;
    private bool _hasFrameSample;
    private string? _configurationError;

    public bool IsActive => _active;

    public NCAILoadState State => _stateMachine.State;

    public int ReservedMaterializationCount => _reservedMaterializations;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(OnSessionClosed);
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(OnClockFailed);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_active || _policy == null ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch)
        {
            return;
        }

        var frameMilliseconds = Math.Max(0f, frameTime * 1000f);
        _smoothedFrameMilliseconds = _hasFrameSample
            ? Lerp(_smoothedFrameMilliseconds, frameMilliseconds, _policy.FrameTimeEwmaAlpha)
            : frameMilliseconds;
        _hasFrameSample = true;
        _frameMillisecondsWindow += frameMilliseconds;
        _frameTelemetry[_frameTelemetryWriteIndex] = frameMilliseconds;
        _frameTelemetryWriteIndex = (_frameTelemetryWriteIndex + 1) % _frameTelemetry.Length;
        _frameTelemetryCount = Math.Min(_frameTelemetry.Length, _frameTelemetryCount + 1);
        if (now < _nextSample)
            return;

        _nextSample = now.Advance(_policy.SampleInterval);
        _maximumQueueRatio = 0f;
        foreach (var ratio in _queueRatios.Values)
            _maximumQueueRatio = Math.Max(_maximumQueueRatio, ratio);
        _backloggedSubsystems = _backlogs.Count;
        _alifeWorkRatio = _frameMillisecondsWindow > 0d
            ? Math.Clamp((float) (_workMillisecondsWindow / _frameMillisecondsWindow), 0f, 1f)
            : 0f;
        _lastWorkMilliseconds = (float) _workMillisecondsWindow;
        for (var index = 0; index < _workMillisecondsByKindWindow.Length; index++)
        {
            _lastWorkMillisecondsByKind[index] = (float) _workMillisecondsByKindWindow[index];
            _workMillisecondsByKindWindow[index] = 0d;
        }
        CaptureGarbageCollectorTelemetry();

        var materializedRatio = _hardMaterializedBudget > 0
            ? (float) (_materializedActors + _reservedMaterializations) / _hardMaterializedBudget
            : 0f;
        _reasons = BuildReasons(
            _smoothedFrameMilliseconds,
            _alifeWorkRatio,
            materializedRatio,
            _thresholds);
        var decision = _stateMachine.Step(
            new NCAILoadGovernorSample(
                _smoothedFrameMilliseconds,
                _alifeWorkRatio,
                _maximumQueueRatio,
                materializedRatio,
                _backloggedSubsystems),
            _reasons,
            _thresholds);
        _workMillisecondsWindow = 0d;
        _frameMillisecondsWindow = 0d;
        if (decision.Transitioned)
        {
            _transitionCount++;
            _lastTransitionAt = now;
            Log.Info($"A-Life load governor entered {decision.State}; pressure={decision.Reasons}.");
        }
    }

    public void ReportMaterializedActors(int count)
    {
        if (_active)
            _materializedActors = Math.Max(0, count);
    }

    public void ReportQueue(NCAILoadQueueKind kind, int queued, int capacity)
    {
        if (!_active)
            return;
        _queueRatios[kind] = capacity > 0 ? Math.Clamp((float) queued / capacity, 0f, 1f) : 0f;
    }

    public void ReportBacklog(NCAILoadBacklogKind kind, bool backlogged)
    {
        if (!_active)
            return;
        if (backlogged)
            _backlogs.Add(kind);
        else
            _backlogs.Remove(kind);
    }

    /// <summary>
    /// Starts an allocation-free wall-clock measurement for one A-Life hot path. The aggregate is compared with the
    /// simulation-frame budget over the current sample window, so unrelated server work is not attributed to A-Life.
    /// </summary>
    public NCAILoadWorkMeasurement MeasureWork(NCAILoadWorkKind kind) =>
        new(this, kind, _active ? Stopwatch.GetTimestamp() : 0L);

    internal void ReportWork(NCAILoadWorkKind kind, long startedAt)
    {
        if (!_active || startedAt == 0L)
            return;
        var elapsed = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        _workMillisecondsWindow += elapsed;
        _workMillisecondsByKindWindow[(int) kind] += elapsed;
    }

    /// <summary>
    /// Reserves capacity before an asynchronous materialization is queued. Protected incident/service/admin work may
    /// use headroom between the current soft limit and the absolute hard cap when its priority is high enough.
    /// </summary>
    public bool TryReserveMaterialization(
        Guid requestId,
        int actorCount,
        int priority,
        bool protectedRequest,
        out string? error)
    {
        error = null;
        if (!_active || _policy == null)
        {
            if (_configurationError == null)
                return true;
            _admissionRejected++;
            error = _configurationError;
            return false;
        }
        if (requestId == Guid.Empty)
            return true;
        if (_materializationReservations.ContainsKey(requestId))
            return true;

        actorCount = Math.Max(1, actorCount);
        var projected = _materializedActors + _reservedMaterializations + actorCount;
        if (_hardMaterializedBudget > 0 && projected > _hardMaterializedBudget)
        {
            _admissionRejected++;
            error = $"A-Life load governor hard cap {_hardMaterializedBudget} would be exceeded.";
            return false;
        }

        var softLimit = EffectiveSoftMaterializedBudget;
        if (projected > softLimit)
        {
            var minimumPriority = ProtectedMaterializationPriority;
            if (!protectedRequest || priority < minimumPriority)
            {
                _admissionRejected++;
                error = protectedRequest
                    ? $"A-Life load state {State} requires protected priority {minimumPriority} above soft cap {softLimit}."
                    : $"A-Life load state {State} defers non-critical materialization above soft cap {softLimit}.";
                return false;
            }
        }

        _materializationReservations.Add(requestId, actorCount);
        _reservedMaterializations += actorCount;
        _admissionAccepted++;
        return true;
    }

    public void ReleaseMaterialization(Guid requestId)
    {
        if (!_materializationReservations.Remove(requestId, out var actorCount))
            return;
        _reservedMaterializations = Math.Max(0, _reservedMaterializations - actorCount);
    }

    public TimeSpan ScaleEvaluationInterval(TimeSpan baseline)
    {
        var multiplier = State switch
        {
            NCAILoadState.Elevated => _policy?.ElevatedEvaluationIntervalMultiplier ?? 1f,
            NCAILoadState.Critical => _policy?.CriticalEvaluationIntervalMultiplier ?? 1f,
            _ => 1f,
        };
        return TimeSpan.FromTicks(Math.Max(1L, (long) Math.Ceiling(baseline.Ticks * multiplier)));
    }

    /// <summary>
    /// Converts a fractional mode multiplier into per-subsystem dispatch credits. This lets a baseline of one degrade
    /// to one operation every two/four ticks without starving that queue permanently.
    /// </summary>
    public int TakeDispatchLimit(NCAILoadDispatchKind kind, int baseline)
    {
        if (baseline <= 0)
            return 0;
        if (!_active || _policy == null || State == NCAILoadState.Normal)
        {
            _dispatchCredits.Remove(kind);
            return baseline;
        }
        var multiplier = State switch
        {
            NCAILoadState.Elevated => _policy?.ElevatedDispatchMultiplier ?? 1f,
            NCAILoadState.Critical => _policy?.CriticalDispatchMultiplier ?? 1f,
            _ => 1f,
        };
        var (dispatches, remainder) = AdvanceDispatchCredit(
            _dispatchCredits.GetValueOrDefault(kind),
            baseline,
            multiplier);
        _dispatchCredits[kind] = remainder;
        return dispatches;
    }

    internal static (int Dispatches, float Remainder) AdvanceDispatchCredit(
        float currentCredit,
        int baseline,
        float multiplier)
    {
        if (baseline <= 0)
            return (0, 0f);
        var credit = Math.Min(baseline, Math.Max(0f, currentCredit) + baseline * multiplier);
        var dispatches = Math.Clamp((int) Math.Floor(credit), 0, baseline);
        return (dispatches, credit - dispatches);
    }

    public int TakeAutomaticMaterializationRequests(int baseline) =>
        TakeDispatchLimit(NCAILoadDispatchKind.RelevanceMaterialization, baseline);

    public int GetEffectiveSoftMaterializedBudget(int baseline)
    {
        if (!_active || _policy == null)
            return baseline;
        return ScaleSoftBudget(baseline);
    }

    public int ScaleSoftBudget(int baseline)
    {
        if (baseline <= 0)
            return 0;
        var multiplier = State switch
        {
            NCAILoadState.Elevated => _policy?.ElevatedSoftMaterializedBudgetMultiplier ?? 1f,
            NCAILoadState.Critical => _policy?.CriticalSoftMaterializedBudgetMultiplier ?? 1f,
            _ => 1f,
        };
        return Math.Clamp((int) Math.Floor(baseline * multiplier), 1, baseline);
    }

    public int EffectiveSoftMaterializedBudget
    {
        get
        {
            if (_baseSoftMaterializedBudget <= 0)
                return 0;
            return Math.Min(ScaleSoftBudget(_baseSoftMaterializedBudget), _hardMaterializedBudget);
        }
    }

    public int ProtectedMaterializationPriority => State switch
    {
        NCAILoadState.Elevated => _policy?.ElevatedProtectedMaterializationPriority ?? 0,
        NCAILoadState.Critical => _policy?.CriticalProtectedMaterializationPriority ?? 0,
        _ => 0,
    };

    public NCAILoadGovernorSnapshot GetSnapshot() => new(
        _active,
        State,
        _reasons,
        _smoothedFrameMilliseconds,
        _alifeWorkRatio,
        _materializedActors,
        _reservedMaterializations,
        EffectiveSoftMaterializedBudget,
        _hardMaterializedBudget,
        _maximumQueueRatio,
        _backloggedSubsystems,
        _stateMachine.CandidateSamples,
        _transitionCount,
        _lastTransitionAt,
        _admissionAccepted,
        _admissionRejected,
        _configurationError);

    public NCAILoadTelemetrySnapshot GetTelemetrySnapshot()
    {
        var samples = new float[_frameTelemetryCount];
        Array.Copy(_frameTelemetry, samples, samples.Length);
        Array.Sort(samples);
        var work = new Dictionary<NCAILoadWorkKind, float>(_lastWorkMillisecondsByKind.Length);
        foreach (var kind in Enum.GetValues<NCAILoadWorkKind>())
            work.Add(kind, _lastWorkMillisecondsByKind[(int) kind]);
        return new NCAILoadTelemetrySnapshot(
            CalculatePercentile(samples, 0.50f),
            CalculatePercentile(samples, 0.95f),
            CalculatePercentile(samples, 0.99f),
            _lastWorkMilliseconds,
            work,
            _allocatedBytesSinceLastSample,
            _gen0CollectionsSinceLastSample,
            _gen1CollectionsSinceLastSample,
            _gen2CollectionsSinceLastSample,
            samples.Length);
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        Reset();
        if (!_prototypes.TryIndex(
                new ProtoId<NCAILoadGovernorPolicyPrototype>(DefaultPolicyId),
                out NCAILoadGovernorPolicyPrototype? policy) || policy == null || !ValidatePolicy(policy))
        {
            _configurationError = $"Load governor policy {DefaultPolicyId} is missing or invalid.";
            Log.Error(_configurationError);
            return;
        }
        if (!_prototypes.TryIndex(
                new ProtoId<NCCityRelevancePolicyPrototype>(DefaultRelevancePolicyId),
                out NCCityRelevancePolicyPrototype? relevance) || relevance == null ||
            relevance.GlobalSoftMaterializedBudget <= 0 ||
            relevance.GlobalHardMaterializedBudget < relevance.GlobalSoftMaterializedBudget)
        {
            _configurationError = $"Relevance policy {DefaultRelevancePolicyId} is missing or invalid.";
            Log.Error(_configurationError);
            return;
        }

        _policy = policy;
        _thresholds = NCAILoadGovernorThresholds.FromPrototype(policy);
        _baseSoftMaterializedBudget = relevance.GlobalSoftMaterializedBudget;
        _hardMaterializedBudget = relevance.GlobalHardMaterializedBudget;
        _epoch = args.Epoch;
        _nextSample = args.Time.Advance(policy.SampleInterval);
        _lastTransitionAt = args.Time;
        _active = true;
        _lastAllocatedBytes = GC.GetTotalAllocatedBytes(false);
        _lastGen0Collections = GC.CollectionCount(0);
        _lastGen1Collections = GC.CollectionCount(1);
        _lastGen2Collections = GC.CollectionCount(2);
    }

    private void OnSessionClosed(NCActiveWorldSessionClosedEvent args)
    {
        if (_active && args.Epoch == _epoch)
            Reset();
    }

    private void OnClockFailed(NCActiveWorldClockFailedEvent args)
    {
        if (_active && args.Epoch == _epoch)
            Reset();
    }

    private void Reset()
    {
        _active = false;
        _policy = null;
        _thresholds = default;
        _epoch = default;
        _nextSample = default;
        _lastTransitionAt = default;
        _reasons = NCAILoadPressureReason.None;
        _smoothedFrameMilliseconds = 0f;
        _workMillisecondsWindow = 0d;
        _frameMillisecondsWindow = 0d;
        _lastWorkMilliseconds = 0f;
        _allocatedBytesSinceLastSample = 0L;
        _gen0CollectionsSinceLastSample = 0;
        _gen1CollectionsSinceLastSample = 0;
        _gen2CollectionsSinceLastSample = 0;
        _frameTelemetryWriteIndex = 0;
        _frameTelemetryCount = 0;
        _alifeWorkRatio = 0f;
        _maximumQueueRatio = 0f;
        _materializedActors = 0;
        _baseSoftMaterializedBudget = 0;
        _hardMaterializedBudget = 0;
        _reservedMaterializations = 0;
        _backloggedSubsystems = 0;
        _transitionCount = 0;
        _admissionAccepted = 0;
        _admissionRejected = 0;
        _hasFrameSample = false;
        _configurationError = null;
        _queueRatios.Clear();
        _backlogs.Clear();
        _materializationReservations.Clear();
        _dispatchCredits.Clear();
        Array.Clear(_workMillisecondsByKindWindow);
        Array.Clear(_lastWorkMillisecondsByKind);
        Array.Clear(_frameTelemetry);
        _stateMachine.Reset();
    }

    private void CaptureGarbageCollectorTelemetry()
    {
        var allocated = GC.GetTotalAllocatedBytes(false);
        _allocatedBytesSinceLastSample = Math.Max(0L, allocated - _lastAllocatedBytes);
        _lastAllocatedBytes = allocated;
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        _gen0CollectionsSinceLastSample = Math.Max(0, gen0 - _lastGen0Collections);
        _gen1CollectionsSinceLastSample = Math.Max(0, gen1 - _lastGen1Collections);
        _gen2CollectionsSinceLastSample = Math.Max(0, gen2 - _lastGen2Collections);
        _lastGen0Collections = gen0;
        _lastGen1Collections = gen1;
        _lastGen2Collections = gen2;
    }

    internal static float CalculatePercentile(float[] sorted, float percentile)
    {
        if (sorted.Length == 0)
            return 0f;
        var index = Math.Clamp((int) Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private NCAILoadPressureReason BuildReasons(
        float frameMilliseconds,
        float workRatio,
        float materializedRatio,
        NCAILoadGovernorThresholds thresholds)
    {
        var reasons = NCAILoadPressureReason.None;
        if (frameMilliseconds >= thresholds.ElevatedEnterFrameMilliseconds)
            reasons |= NCAILoadPressureReason.FrameTime;
        if (workRatio >= thresholds.ElevatedEnterWorkRatio)
            reasons |= NCAILoadPressureReason.WorkBudget;
        if (materializedRatio >= thresholds.ElevatedEnterMaterializedRatio)
            reasons |= NCAILoadPressureReason.MaterializedActors;
        if (_queueRatios.GetValueOrDefault(NCAILoadQueueKind.Materialization) >=
            thresholds.ElevatedEnterQueueRatio)
            reasons |= NCAILoadPressureReason.MaterializationQueue;
        if (_queueRatios.GetValueOrDefault(NCAILoadQueueKind.Dematerialization) >=
            thresholds.ElevatedEnterQueueRatio)
            reasons |= NCAILoadPressureReason.DematerializationQueue;
        if (_queueRatios.GetValueOrDefault(NCAILoadQueueKind.GroupPhysicalization) >=
            thresholds.ElevatedEnterQueueRatio)
            reasons |= NCAILoadPressureReason.GroupPhysicalizationQueue;
        if (_queueRatios.GetValueOrDefault(NCAILoadQueueKind.PhysicalExecution) >=
            thresholds.ElevatedEnterQueueRatio)
            reasons |= NCAILoadPressureReason.PhysicalExecutionQueue;
        if (_queueRatios.GetValueOrDefault(NCAILoadQueueKind.PhysicalPerception) >=
            thresholds.ElevatedEnterQueueRatio)
            reasons |= NCAILoadPressureReason.PhysicalPerceptionQueue;
        if (_queueRatios.GetValueOrDefault(NCAILoadQueueKind.Cognition) >=
            thresholds.ElevatedEnterQueueRatio)
            reasons |= NCAILoadPressureReason.CognitionQueue;
        if (_backlogs.Contains(NCAILoadBacklogKind.Schedule))
            reasons |= NCAILoadPressureReason.ScheduleBacklog;
        if (_backlogs.Contains(NCAILoadBacklogKind.AbstractTravel))
            reasons |= NCAILoadPressureReason.AbstractTravelBacklog;
        if (_backlogs.Contains(NCAILoadBacklogKind.PhysicalExecution))
            reasons |= NCAILoadPressureReason.PhysicalExecutionBacklog;
        if (_backlogs.Contains(NCAILoadBacklogKind.PhysicalPerception))
            reasons |= NCAILoadPressureReason.PhysicalPerceptionBacklog;
        if (_backlogs.Contains(NCAILoadBacklogKind.Cognition))
            reasons |= NCAILoadPressureReason.CognitionBacklog;
        return reasons;
    }

    private static bool ValidatePolicy(NCAILoadGovernorPolicyPrototype policy) =>
        policy.SampleInterval > TimeSpan.Zero &&
        float.IsFinite(policy.FrameTimeEwmaAlpha) && policy.FrameTimeEwmaAlpha is > 0f and <= 1f &&
        policy.DegradeSamples > 0 && policy.RecoverSamples > 0 &&
        PositiveFinite(policy.ElevatedEnterFrameMilliseconds) &&
        PositiveFinite(policy.ElevatedExitFrameMilliseconds) &&
        PositiveFinite(policy.CriticalEnterFrameMilliseconds) &&
        PositiveFinite(policy.CriticalExitFrameMilliseconds) &&
        policy.ElevatedExitFrameMilliseconds < policy.ElevatedEnterFrameMilliseconds &&
        policy.ElevatedEnterFrameMilliseconds < policy.CriticalEnterFrameMilliseconds &&
        policy.ElevatedExitFrameMilliseconds < policy.CriticalExitFrameMilliseconds &&
        policy.CriticalExitFrameMilliseconds < policy.CriticalEnterFrameMilliseconds &&
        ExitEnterRatio(policy.ElevatedExitWorkRatio, policy.ElevatedEnterWorkRatio) &&
        ExitEnterRatio(policy.CriticalExitWorkRatio, policy.CriticalEnterWorkRatio) &&
        policy.ElevatedEnterWorkRatio < policy.CriticalEnterWorkRatio &&
        ExitEnterRatio(policy.ElevatedExitQueueRatio, policy.ElevatedEnterQueueRatio) &&
        ExitEnterRatio(policy.CriticalExitQueueRatio, policy.CriticalEnterQueueRatio) &&
        policy.ElevatedEnterQueueRatio < policy.CriticalEnterQueueRatio &&
        ExitEnterRatio(policy.ElevatedExitMaterializedRatio, policy.ElevatedEnterMaterializedRatio) &&
        ExitEnterRatio(policy.CriticalExitMaterializedRatio, policy.CriticalEnterMaterializedRatio) &&
        policy.ElevatedEnterMaterializedRatio < policy.CriticalEnterMaterializedRatio &&
        policy.ElevatedBacklogCount > 0 &&
        policy.CriticalExitBacklogCount >= policy.ElevatedBacklogCount &&
        policy.CriticalBacklogCount > policy.CriticalExitBacklogCount &&
        FiniteAtLeastOne(policy.ElevatedEvaluationIntervalMultiplier) &&
        FiniteAtLeastOne(policy.CriticalEvaluationIntervalMultiplier) &&
        policy.CriticalEvaluationIntervalMultiplier >= policy.ElevatedEvaluationIntervalMultiplier &&
        FiniteUnit(policy.ElevatedDispatchMultiplier) && FiniteUnit(policy.CriticalDispatchMultiplier) &&
        policy.CriticalDispatchMultiplier <= policy.ElevatedDispatchMultiplier &&
        FiniteUnit(policy.ElevatedSoftMaterializedBudgetMultiplier) &&
        FiniteUnit(policy.CriticalSoftMaterializedBudgetMultiplier) &&
        policy.CriticalSoftMaterializedBudgetMultiplier <= policy.ElevatedSoftMaterializedBudgetMultiplier &&
        policy.ElevatedProtectedMaterializationPriority is >= 0 and <= 1000 &&
        policy.CriticalProtectedMaterializationPriority is >= 0 and <= 1000 &&
        policy.CriticalProtectedMaterializationPriority >= policy.ElevatedProtectedMaterializationPriority;

    private static bool PositiveFinite(float value) => float.IsFinite(value) && value > 0f;

    private static bool ExitEnterRatio(float exit, float enter) =>
        float.IsFinite(exit) && float.IsFinite(enter) && exit is >= 0f and <= 1f &&
        enter is > 0f and <= 1f && exit < enter;

    private static bool FiniteAtLeastOne(float value) => float.IsFinite(value) && value >= 1f;

    private static bool FiniteUnit(float value) => float.IsFinite(value) && value is > 0f and <= 1f;

    private static float Lerp(float from, float to, float amount) => from + (to - from) * amount;
}

/// <summary>
/// Allocation-free measurement scope used by server A-Life systems. Disposal is safe on every early-return path.
/// </summary>
public readonly struct NCAILoadWorkMeasurement : IDisposable
{
    private readonly NCAILoadGovernorSystem _governor;
    private readonly NCAILoadWorkKind _kind;
    private readonly long _startedAt;

    public NCAILoadWorkMeasurement(NCAILoadGovernorSystem governor, NCAILoadWorkKind kind, long startedAt)
    {
        _governor = governor;
        _kind = kind;
        _startedAt = startedAt;
    }

    public void Dispose() => _governor.ReportWork(_kind, _startedAt);
}
