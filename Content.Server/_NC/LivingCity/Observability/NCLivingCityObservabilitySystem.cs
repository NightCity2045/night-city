// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Database;
using Content.Server._NC.LivingCity.Admin;
using Content.Server._NC.LivingCity.Neural;
using Content.Server._NC.LivingCity.Simulation;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Observability;
using Content.Shared._NC.Persistence.Time;
using Prometheus;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Observability;

public enum NCLivingCityAlertSeverity : byte
{
    Warning,
    Critical,
}

public sealed record NCLivingCityOperationalAlert(
    string Id,
    NCLivingCityAlertSeverity Severity,
    bool Active,
    double Value,
    double Threshold,
    int Occurrences,
    NCActiveWorldTime FirstObservedAt,
    NCActiveWorldTime LastObservedAt);

public sealed record NCLivingCityObservabilitySnapshot(
    bool Active,
    NCAILoadTelemetrySnapshot Load,
    NCPersistenceTelemetrySnapshot Persistence,
    NCLivingCityAdminTelemetrySnapshot Admin,
    IReadOnlyList<NCLivingCityOperationalAlert> Alerts,
    string? ConfigurationError);

internal readonly record struct NCLivingCityAlertThresholdResult(
    bool Active,
    NCLivingCityAlertSeverity Severity,
    double Threshold);

/// <summary>
/// Evaluates O(1) operational counters during active round time and retains a bounded alert history for F7.
/// </summary>
public sealed partial class NCLivingCityObservabilitySystem : EntitySystem
{
    private const string DefaultPolicyId = "NCDefaultLivingCityObservabilityPolicy";
    private static readonly Gauge AlertGauge = Metrics.CreateGauge(
        "nc_living_city_operational_alert",
        "Whether a Living City operational alert is active.",
        new GaugeConfiguration { LabelNames = ["alert", "severity"] });

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCAILoadGovernorSystem _load = default!;
    [Dependency] private NCNeuralCognitionClientSystem _neural = default!;

    private readonly Dictionary<string, NCLivingCityOperationalAlert> _alerts = [];
    private NCLivingCityObservabilityPolicyPrototype? _policy;
    private NCSessionEpoch _epoch;
    private NCActiveWorldTime _nextEvaluation;
    private TimeSpan? _outboxLag;
    private int _outboxExhausted;
    private bool _active;
    private string? _configurationError;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Reset());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Reset());
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_active || _policy == null || !_clock.TryGetCurrentTime(out var epoch, out var now) ||
            epoch != _epoch || now < _nextEvaluation)
            return;
        _nextEvaluation = now.Advance(_policy.EvaluationInterval);
        Evaluate(now);
    }

    public void ReportOutboxStatus(TimeSpan? oldestPendingAge, int exhausted)
    {
        _outboxLag = oldestPendingAge;
        _outboxExhausted = Math.Max(0, exhausted);
    }

    public NCLivingCityObservabilitySnapshot GetSnapshot()
    {
        if (_active && _clock.TryGetCurrentTime(out var epoch, out var now) && epoch == _epoch)
            Evaluate(now);
        return new NCLivingCityObservabilitySnapshot(
            _active,
            _load.GetTelemetrySnapshot(),
            NCPersistenceTelemetry.Snapshot(),
            NCLivingCityAdminTelemetry.Snapshot(),
            _alerts.Values.OrderByDescending(alert => alert.Active)
                .ThenByDescending(alert => alert.Severity)
                .ThenByDescending(alert => alert.LastObservedAt.Elapsed)
                .ToArray(),
            _configurationError);
    }

    private void OnStarted(NCActiveWorldSessionStartedEvent args)
    {
        Reset();
        if (!_prototypes.TryIndex(new ProtoId<NCLivingCityObservabilityPolicyPrototype>(DefaultPolicyId),
                out var policy) || policy == null || !Valid(policy))
        {
            _configurationError = $"Observability policy {DefaultPolicyId} is missing or invalid.";
            Log.Error(_configurationError);
            return;
        }
        _policy = policy;
        _epoch = args.Epoch;
        _nextEvaluation = args.Time;
        _active = true;
    }

    private void Evaluate(NCActiveWorldTime now)
    {
        var policy = _policy;
        if (policy == null)
            return;
        var load = _load.GetTelemetrySnapshot();
        var persistence = NCPersistenceTelemetry.Snapshot();
        var neural = _neural.GetRuntimeSnapshot();
        ApplyThreshold("frame-p95", load.FrameP95Milliseconds,
            policy.FrameP95WarningMilliseconds, policy.FrameP95CriticalMilliseconds, now);
        ApplyThreshold("persistence-p95", persistence.LatencyP95Milliseconds,
            policy.PersistenceP95WarningMilliseconds, policy.PersistenceP95CriticalMilliseconds, now);
        ApplyThreshold("allocated-per-sample-mb", load.AllocatedBytesSinceLastSample / 1024d / 1024d,
            policy.AllocatedMegabytesWarning, policy.AllocatedMegabytesCritical, now);
        ApplyThreshold("neural-consecutive-failures", neural.ConsecutiveFailures,
            policy.NeuralFailuresWarning, policy.NeuralFailuresCritical, now);
        ApplyThreshold("outbox-oldest-seconds", _outboxLag?.TotalSeconds ?? 0d,
            policy.OutboxLagWarningSeconds, policy.OutboxLagCriticalSeconds, now);
        ApplyThreshold("outbox-exhausted", _outboxExhausted, 1, 1, now);
        TrimResolved(policy.MaximumRetainedAlerts);
    }

    private void ApplyThreshold(string id, double value, double warning, double critical, NCActiveWorldTime now)
    {
        var classification = ClassifyThreshold(value, warning, critical);
        var active = classification.Active;
        var severity = classification.Severity;
        if (!_alerts.TryGetValue(id, out var previous))
        {
            if (!active)
                return;
            previous = new NCLivingCityOperationalAlert(id, severity, false, value,
                classification.Threshold, 0, now, now);
        }
        if (previous.Active && previous.Severity != severity)
            AlertGauge.WithLabels(id, previous.Severity.ToString()).Set(0);
        var occurrences = active && (!previous.Active || previous.Severity != severity)
            ? previous.Occurrences + 1
            : previous.Occurrences;
        var next = previous with
        {
            Severity = severity,
            Active = active,
            Value = value,
            Threshold = classification.Threshold,
            Occurrences = occurrences,
            LastObservedAt = now,
        };
        _alerts[id] = next;
        AlertGauge.WithLabels(id, next.Severity.ToString()).Set(active ? 1 : 0);
    }

    internal static NCLivingCityAlertThresholdResult ClassifyThreshold(
        double value,
        double warning,
        double critical)
    {
        var severity = value >= critical ? NCLivingCityAlertSeverity.Critical : NCLivingCityAlertSeverity.Warning;
        return new NCLivingCityAlertThresholdResult(
            value >= warning,
            severity,
            severity == NCLivingCityAlertSeverity.Critical ? critical : warning);
    }

    private void TrimResolved(int maximum)
    {
        if (_alerts.Count <= maximum)
            return;
        foreach (var alert in _alerts.Values.Where(alert => !alert.Active)
                     .OrderBy(alert => alert.LastObservedAt.Elapsed).Take(_alerts.Count - maximum).ToArray())
            _alerts.Remove(alert.Id);
    }

    private void Reset()
    {
        foreach (var alert in _alerts.Values)
            AlertGauge.WithLabels(alert.Id, alert.Severity.ToString()).Set(0);
        _alerts.Clear();
        _policy = null;
        _epoch = default;
        _nextEvaluation = default;
        _outboxLag = null;
        _outboxExhausted = 0;
        _active = false;
        _configurationError = null;
    }

    private static bool Valid(NCLivingCityObservabilityPolicyPrototype policy) =>
        policy.EvaluationInterval > TimeSpan.Zero &&
        PositivePair(policy.FrameP95WarningMilliseconds, policy.FrameP95CriticalMilliseconds) &&
        PositivePair(policy.PersistenceP95WarningMilliseconds, policy.PersistenceP95CriticalMilliseconds) &&
        PositivePair(policy.AllocatedMegabytesWarning, policy.AllocatedMegabytesCritical) &&
        policy.NeuralFailuresWarning > 0 && policy.NeuralFailuresCritical >= policy.NeuralFailuresWarning &&
        policy.OutboxLagWarningSeconds > 0 && policy.OutboxLagCriticalSeconds >= policy.OutboxLagWarningSeconds &&
        policy.MaximumRetainedAlerts is > 0 and <= 128;

    private static bool PositivePair(float warning, float critical) =>
        float.IsFinite(warning) && float.IsFinite(critical) && warning > 0f && critical >= warning;
}
