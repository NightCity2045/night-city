// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Neural;

public interface INCNeuralCognitionClient
{
    bool Submit(NCNeuralRequestEnvelope request);
    NCNeuralRuntimeSnapshot GetRuntimeSnapshot();
}

public sealed record NCNeuralRuntimeSnapshot(bool Active, NCNeuralBudgetMode BudgetMode, int Queued, int InFlight,
    int SessionInputTokens, int SessionOutputTokens, long Accepted, long Fallbacks, long Timeouts,
    long ValidationRejects, int ConsecutiveFailures, NCActiveWorldTime? CircuitOpenUntil);

/// <summary>
/// Bounded optional neural queue. Responses are suggestions only and are published as events after session, version,
/// capability and schema validation; this system has no dependency on gameplay mutation authorities.
/// </summary>
public sealed partial class NCNeuralCognitionClientSystem : EntitySystem, INCNeuralCognitionClient
{
    private static readonly ProtoId<NCNeuralPolicyPrototype> DefaultPolicy = "Default";
    private readonly HttpClient _http = new();
    private readonly PriorityQueue<NCNeuralRequestEnvelope, (byte Priority, long Sequence)> _queue = new();
    private readonly HashSet<Guid> _known = [];
    private readonly Dictionary<(NCActorId Actor, NCNeuralRequestKind Kind), NCNeuralResponseEnvelope> _cache = [];
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ITaskManager _tasks = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    private NCNeuralPolicyPrototype? _policy;
    private NCSessionEpoch _epoch;
    private NCActiveWorldTime? _circuitOpenUntil;
    private long _sequence;
    private int _inFlight;
    private int _inputTokens;
    private int _outputTokens;
    private int _failures;
    private long _accepted;
    private long _fallbacks;
    private long _timeouts;
    private long _rejects;
    private bool _active;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(_ => _active = false);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Reset());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Reset());
    }

    public override void Shutdown()
    {
        _http.Dispose();
        base.Shutdown();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_active || _policy == null) return;
        while (_inFlight < _policy.MaximumConcurrent && _queue.TryDequeue(out var request, out _))
        {
            if (request.ExpiresAt <= CurrentTime()) Publish(Fallback(request, NCNeuralResultStatus.Expired, "expired"));
            else { _inFlight++; _ = ExecuteAsync(request); }
        }
    }

    public bool Submit(NCNeuralRequestEnvelope request)
    {
        if (!_active || _policy == null || !_known.Add(request.RequestId) || !Valid(request, _policy)) return false;
        var mode = Mode();
        if (!_policy.Enabled || mode == NCNeuralBudgetMode.FallbackOnly ||
            mode == NCNeuralBudgetMode.Restricted && request.Priority != NCNeuralPriority.P0 ||
            mode == NCNeuralBudgetMode.Economy && request.Priority > NCNeuralPriority.P2)
        {
            Publish(Fallback(request, mode == NCNeuralBudgetMode.FallbackOnly
                ? NCNeuralResultStatus.BudgetDenied : NCNeuralResultStatus.Fallback, mode.ToString()));
            return true;
        }
        if (_queue.Count >= _policy.MaximumQueued) return false;
        _queue.Enqueue(request, ((byte) request.Priority, _sequence++));
        return true;
    }

    public NCNeuralRuntimeSnapshot GetRuntimeSnapshot() => new(_active, Mode(), _queue.Count, _inFlight,
        _inputTokens, _outputTokens, _accepted, _fallbacks, _timeouts, _rejects, _failures, _circuitOpenUntil);

    /// <summary>Clears only transient failure state; token budgets, queues and accepted gameplay state are untouched.</summary>
    public bool ResetCircuitBreaker()
    {
        if (!_active)
            return false;
        _failures = 0;
        _circuitOpenUntil = null;
        return true;
    }

    private async Task ExecuteAsync(NCNeuralRequestEnvelope request)
    {
        try
        {
            var now = CurrentTime();
            if (_circuitOpenUntil is { } open && open > now)
            {
                Publish(Fallback(request, NCNeuralResultStatus.CircuitOpen, "circuit-open"));
                return;
            }
            using var cancellation = new CancellationTokenSource(_policy!.RequestTimeout);
            using var response = await _http.PostAsJsonAsync(_policy.Endpoint, request, cancellation.Token);
            response.EnsureSuccessStatusCode();
            var value = await response.Content.ReadFromJsonAsync<NCNeuralResponseEnvelope>(cancellation.Token);
            var result = await ValidateAsync(request, value);
            if (result.Status == NCNeuralResultStatus.Accepted)
            {
                _inputTokens = checked(_inputTokens + value!.InputTokens);
                _outputTokens = checked(_outputTokens + value.OutputTokens);
                _failures = 0;
                _accepted++;
                _cache[(request.ActorId, request.Kind)] = value;
            }
            else { _rejects++; FailCircuit(); }
            Publish(result);
        }
        catch (OperationCanceledException)
        {
            _timeouts++; FailCircuit();
            Publish(Fallback(request, NCNeuralResultStatus.Timeout, "timeout"));
        }
        catch (Exception exception)
        {
            FailCircuit();
            Publish(Fallback(request, NCNeuralResultStatus.Fallback, exception.GetType().Name));
        }
        finally { _inFlight--; }
    }

    private async Task<NCNeuralResult> ValidateAsync(NCNeuralRequestEnvelope request,
        NCNeuralResponseEnvelope? response)
    {
        if (!_active || request.SessionEpoch != _epoch) return new(NCNeuralResultStatus.Stale, request, null, "epoch");
        if (request.ExpiresAt <= CurrentTime()) return new(NCNeuralResultStatus.Expired, request, null, "expiry");
        var actor = await _actors.GetAsync(request.ActorId, true);
        if (actor == null || actor.AggregateVersion != request.ContextVersion)
            return new(NCNeuralResultStatus.Stale, request, null, "context-version");
        if (response == null || response.RequestId != request.RequestId || response.SchemaVersion != 1 ||
            response.ContextVersion != request.ContextVersion || response.Confidence is < 0 or > 1000 ||
            response.InputTokens < 0 || response.OutputTokens < 0 ||
            !request.AllowedCapabilityIds.Contains(response.SelectedCapabilityId) ||
            response.SpeechActKey.Length > 96 || response.SummaryKey.Length > 128 ||
            response.RenderedText.Length > _policy!.MaximumTextLength)
            return new(NCNeuralResultStatus.Invalid, request, null, "schema-or-capability");
        return new(NCNeuralResultStatus.Accepted, request, response, string.Empty);
    }

    private NCNeuralResult Fallback(NCNeuralRequestEnvelope request, NCNeuralResultStatus status, string reason)
    {
        _fallbacks++;
        if (_cache.TryGetValue((request.ActorId, request.Kind), out var cached) &&
            cached.ContextVersion == request.ContextVersion && request.AllowedCapabilityIds.Contains(
                cached.SelectedCapabilityId)) return new(NCNeuralResultStatus.Fallback, request, cached, $"cached:{reason}");
        var capability = request.AllowedCapabilityIds.FirstOrDefault() ?? string.Empty;
        return new(status, request, new NCNeuralResponseEnvelope(request.RequestId, 1, request.ContextVersion,
            capability, "safe-default", string.Empty, "safe-default", 0, 0, 0, string.Empty), reason);
    }

    private void FailCircuit()
    {
        if (++_failures >= _policy!.CircuitFailureThreshold)
            _circuitOpenUntil = CurrentTime().Advance(_policy.CircuitOpenDuration);
    }
    private void Publish(NCNeuralResult result) => _tasks.RunOnMainThread(() =>
        RaiseLocalEvent(new NCNeuralResultEvent(result)));
    private NCNeuralBudgetMode Mode()
    {
        if (_policy == null || !_policy.Enabled || _inputTokens + _outputTokens >= _policy.SessionTokenHardCap)
            return NCNeuralBudgetMode.FallbackOnly;
        var used = _inputTokens + _outputTokens;
        return used >= _policy.RestrictedThreshold ? NCNeuralBudgetMode.Restricted :
            used >= _policy.EconomyThreshold ? NCNeuralBudgetMode.Economy : NCNeuralBudgetMode.Normal;
    }
    private NCActiveWorldTime CurrentTime() => _clock.TryGetCurrentTime(out _, out var now) ? now : default;
    private void OnStarted(NCActiveWorldSessionStartedEvent args)
    {
        Reset();
        if (_prototypes.TryIndex(DefaultPolicy, out NCNeuralPolicyPrototype? policy) && Valid(policy))
        { _policy = policy; _epoch = args.Epoch; _active = true; }
    }
    private void Reset()
    {
        _active = false; _queue.Clear(); _known.Clear(); _cache.Clear(); _inFlight = 0; _inputTokens = 0;
        _outputTokens = 0; _failures = 0; _circuitOpenUntil = null;
    }
    private static bool Valid(NCNeuralPolicyPrototype value) => value.MaximumQueued is > 0 and <= 4096 &&
        value.MaximumConcurrent is > 0 and <= 32 && value.MaximumFacts is > 0 and <= 256 &&
        value.MaximumCapabilities is > 0 and <= 128 && value.MaximumTextLength is > 0 and <= 4096 &&
        value.EconomyThreshold > 0 && value.RestrictedThreshold >= value.EconomyThreshold &&
        value.SessionTokenHardCap >= value.RestrictedThreshold && value.RequestTimeout > TimeSpan.Zero &&
        value.RequestLifetime >= value.RequestTimeout &&
        Uri.TryCreate(value.Endpoint, UriKind.Absolute, out _);
    private static bool Valid(NCNeuralRequestEnvelope value, NCNeuralPolicyPrototype policy) =>
        value.RequestId != Guid.Empty && value.ActorId.IsValid && value.SessionEpoch.IsValid &&
        value.ContextVersion > 0 && value.ExpiresAt.IsValid && value.Facts.Count <= policy.MaximumFacts &&
        value.AllowedCapabilityIds.Count is > 0 && value.AllowedCapabilityIds.Count <= policy.MaximumCapabilities &&
        value.AllowedCapabilityIds.All(Stable) && value.TopicKeys.All(Stable) && value.Facts.All(fact =>
            Stable(fact.Domain) && Stable(fact.Key) && Stable(fact.Subject) && float.IsFinite(fact.Confidence) &&
            fact.Confidence is >= 0 and <= 1 && Stable(fact.Privacy));
    private static bool Stable(string value) => value.Length is > 0 and <= 128 && value.All(character =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');
}

public sealed class NCNeuralResultEvent(NCNeuralResult result) : EntityEventArgs
{ public NCNeuralResult Result { get; } = result; }
