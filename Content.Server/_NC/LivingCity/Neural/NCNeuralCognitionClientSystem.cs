// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.Configuration;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Neural;

public interface INCNeuralCognitionClient
{
    bool Submit(NCNeuralRequestEnvelope request);
    NCNeuralRuntimeSnapshot GetRuntimeSnapshot();
    bool TryGetActorRuntimeSnapshot(NCActorId actorId, out NCNeuralActorRuntimeSnapshot snapshot);
}

public sealed record NCNeuralRuntimeSnapshot(bool Active, bool AdministrativelyDisabled, bool BudgetReady,
    NCNeuralBudgetMode BudgetMode,
    int Queued, int InFlight,
    int SessionInputTokens, int SessionOutputTokens, long SessionCostUsdMicros,
    string WeeklyPeriodKey, long WeeklyCostUsdMicros,
    long SocialCognitionRequests, long StrategicDecisionRequests, long DialogueContinuationRequests,
    long MemorySummaryRequests, long NarrativeRenderRequests,
    long Accepted, long Stale, long Invalid, long ProviderFailures, long Fallbacks,
    long Timeouts, long ValidationRejects, int ConsecutiveFailures, NCActiveWorldTime? CircuitOpenUntil,
    NCNeuralFailureKind LastFailureKind, string LastFailureReason);

/// <summary>
/// Bounded round-runtime observability for one authored Actor. It contains usage metadata and stable semantic keys,
/// never prompts, rendered dialogue, private facts, or provider credentials.
/// </summary>
public sealed record NCNeuralActorRuntimeSnapshot(
    int InputTokens,
    int CachedInputTokens,
    int OutputTokens,
    long CostUsdMicros,
    long Results,
    long Accepted,
    Guid LastRequestId,
    NCNeuralRequestKind LastRequestKind,
    NCNeuralResultStatus LastStatus,
    NCNeuralFailureKind LastFailureKind,
    string LastReason,
    string LastCapabilityId,
    string LastSummaryKey,
    string LastProviderModel);

/// <summary>
/// Bounded optional neural queue. Responses are suggestions only and are published as events after session, version,
/// capability and schema validation; this system has no dependency on gameplay mutation authorities.
/// </summary>
public sealed partial class NCNeuralCognitionClientSystem : EntitySystem, INCNeuralCognitionClient
{
    private static readonly ProtoId<NCNeuralPolicyPrototype> DefaultPolicy = "Default";
    private readonly HttpClient _http = new();
    private readonly PriorityQueue<NCNeuralRequestEnvelope, (byte Priority, long Sequence)> _queue = new();
    private readonly ConcurrentQueue<TransportCompletion> _transportCompletions = new();
    private readonly ConcurrentQueue<UsageCompletion> _usageCompletions = new();
    private readonly ConcurrentQueue<BudgetLoadCompletion> _budgetLoadCompletions = new();
    private readonly HashSet<Guid> _known = [];
    private readonly Dictionary<(NCActorId Actor, NCNeuralRequestKind Kind), NCNeuralResponseEnvelope> _cache = [];
    private readonly Dictionary<NCActorId, ActorRuntimeState> _actorRuntime = [];
    private readonly HashSet<Guid> _budgetReservations = [];
    private readonly Dictionary<NCActorId, int> _actorReservations = [];
    private readonly Dictionary<NCNeuralRequestKind, int> _kindReservations = [];
    private readonly Dictionary<NCActorId, long> _weeklyActorCosts = [];
    private readonly Dictionary<NCNeuralRequestKind, long> _weeklyKindRequests = [];
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCNeuralUsageLedgerSystem _usageLedger = default!;
    private NCNeuralPolicyPrototype? _policy;
    private NCSessionEpoch _epoch;
    private NCActiveWorldTime? _circuitOpenUntil;
    private long _sequence;
    private long _actorRuntimeSequence;
    private int _inFlight;
    private int _inputTokens;
    private int _outputTokens;
    private long _costUsdMicros;
    private long _weeklyCostUsdMicros;
    private string _weeklyPeriodKey = string.Empty;
    private int _failures;
    private long _accepted;
    private long _stale;
    private long _invalid;
    private long _providerFailures;
    private long _fallbacks;
    private long _timeouts;
    private long _rejects;
    private NCNeuralFailureKind _lastFailureKind;
    private string _lastFailureReason = string.Empty;
    private bool _active;
    private bool _administrativelyDisabled;
    private bool _budgetReady;
    private int _generation;
    private CancellationTokenSource? _sessionCancellation;

    /// <summary>True only when the current round may spend provider tokens.</summary>
    public bool IsProviderEnabled => _active && ProviderEnabled;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(_ => Deactivate());
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Reset());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Reset());
    }

    public override void Shutdown()
    {
        Deactivate();
        _sessionCancellation?.Dispose();
        _http.Dispose();
        base.Shutdown();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        DrainCompletions(_policy?.MaximumCompletionsPerTick ?? 16);
        if (!_active || _policy == null)
            return;
        if (_budgetReady && WeekChanged() && _inFlight == 0 && _queue.Count == 0)
        {
            _budgetReady = false;
            _ = LoadBudgetAsync(_generation, _epoch);
            return;
        }
        if (_administrativelyDisabled)
        {
            DrainDisabledQueue(_policy.MaximumCompletionsPerTick);
            return;
        }
        while (_inFlight < _policy.MaximumConcurrent && _queue.TryDequeue(out var request, out _))
        {
            var now = CurrentTime();
            if (request.ExpiresAt <= now)
            {
                Publish(Fallback(request, NCNeuralResultStatus.Expired, "expired"));
                continue;
            }
            if (_circuitOpenUntil is { } open && open > now)
            {
                Publish(Fallback(request, NCNeuralResultStatus.CircuitOpen, "circuit-open"));
                continue;
            }
            var cancellation = _sessionCancellation;
            if (cancellation == null || !TryGetProviderUri(out var providerUri))
            {
                Publish(Fallback(request, NCNeuralResultStatus.Fallback, "provider-endpoint-insecure"));
                continue;
            }
            _inFlight++;
            _ = ExecuteAsync(request, _generation, ProviderApiKey, providerUri, _policy.RequestTimeout,
                cancellation.Token);
        }
    }

    public bool Submit(NCNeuralRequestEnvelope request)
    {
        if (!_active || !_budgetReady || WeekChanged() || _policy == null || !Valid(request, _policy) ||
            _known.Contains(request.RequestId))
            return false;
        if (!_clock.TryGetCurrentTime(out var currentEpoch, out var now) || currentEpoch != _epoch ||
            request.SessionEpoch != _epoch || request.ExpiresAt <= now)
        {
            // Reject before reservation or provider dispatch: an async context builder from an older round must
            // never consume tokens or publish a continuation into the current epoch.
            _stale++;
            return false;
        }
        if (_administrativelyDisabled)
        {
            _known.Add(request.RequestId);
            Publish(Fallback(request, NCNeuralResultStatus.BudgetDenied, "administratively-disabled"));
            return true;
        }
        var mode = Mode();
        if (!ProviderEnabled || mode == NCNeuralBudgetMode.FallbackOnly ||
            mode == NCNeuralBudgetMode.Restricted && request.Priority != NCNeuralPriority.P0 ||
            mode == NCNeuralBudgetMode.Economy && request.Priority > NCNeuralPriority.P2)
        {
            _known.Add(request.RequestId);
            Publish(Fallback(request, mode == NCNeuralBudgetMode.FallbackOnly
                ? NCNeuralResultStatus.BudgetDenied : NCNeuralResultStatus.Fallback, mode.ToString()));
            return true;
        }
        if (_queue.Count >= _policy.MaximumQueued)
            return false;
        if (!CanReserveBudget(request, out var budgetReason))
        {
            _known.Add(request.RequestId);
            Publish(Fallback(request, NCNeuralResultStatus.BudgetDenied, budgetReason));
            return true;
        }
        _known.Add(request.RequestId);
        ReserveBudget(request);
        _queue.Enqueue(request, ((byte) request.Priority, _sequence++));
        return true;
    }

    public NCNeuralRuntimeSnapshot GetRuntimeSnapshot() => new(_active, _administrativelyDisabled, _budgetReady,
        Mode(), _queue.Count, _inFlight, _inputTokens, _outputTokens, _costUsdMicros,
        _weeklyPeriodKey, _weeklyCostUsdMicros,
        _weeklyKindRequests.GetValueOrDefault(NCNeuralRequestKind.SocialCognition),
        _weeklyKindRequests.GetValueOrDefault(NCNeuralRequestKind.StrategicDecision),
        _weeklyKindRequests.GetValueOrDefault(NCNeuralRequestKind.DialogueContinuation),
        _weeklyKindRequests.GetValueOrDefault(NCNeuralRequestKind.MemorySummary),
        _weeklyKindRequests.GetValueOrDefault(NCNeuralRequestKind.NarrativeRender),
        _accepted, _stale, _invalid, _providerFailures, _fallbacks,
        _timeouts, _rejects, _failures, _circuitOpenUntil, _lastFailureKind, _lastFailureReason);

    public bool TryGetActorRuntimeSnapshot(NCActorId actorId, out NCNeuralActorRuntimeSnapshot snapshot)
    {
        if (!actorId.IsValid || !_actorRuntime.TryGetValue(actorId, out var state))
        {
            snapshot = default!;
            return false;
        }

        snapshot = new NCNeuralActorRuntimeSnapshot(
            state.InputTokens,
            state.CachedInputTokens,
            state.OutputTokens,
            state.CostUsdMicros,
            state.Results,
            state.Accepted,
            state.LastRequestId,
            state.LastRequestKind,
            state.LastStatus,
            state.LastFailureKind,
            state.LastReason,
            state.LastCapabilityId,
            state.LastSummaryKey,
            state.LastProviderModel);
        return true;
    }

    /// <summary>Clears only transient failure state; token budgets, queues and accepted gameplay state are untouched.</summary>
    public bool ResetCircuitBreaker()
    {
        if (!_active)
            return false;
        _failures = 0;
        _circuitOpenUntil = null;
        return true;
    }

    /// <summary>
    /// Cancels provider work for the current round without stopping deterministic cognition or A-Life. The normal
    /// session reset clears this administrative latch for the next round.
    /// </summary>
    public bool TryDisableProviderForRound()
    {
        if (!_active || _administrativelyDisabled)
            return false;

        _administrativelyDisabled = true;
        _sessionCancellation?.Cancel();
        // Keep the generation and reservations until cancelled transports are drained. Every caller receives an
        // explicit SessionCancelled fallback instead of leaving dialogue or planning correlations orphaned.
        return true;
    }

    private async Task ExecuteAsync(
        NCNeuralRequestEnvelope request,
        int generation,
        string providerApiKey,
        Uri providerUri,
        TimeSpan timeout,
        CancellationToken sessionToken)
    {
        NCNeuralResponseEnvelope? responseValue = null;
        NCCityActorRecord? actor = null;
        var failure = NCNeuralFailureKind.None;
        var reason = string.Empty;
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
            cancellation.CancelAfter(timeout);
            using var message = new HttpRequestMessage(HttpMethod.Post, providerUri)
            {
                // Domain wrappers and enums are intentionally flattened into a stable provider transport contract.
                // Serializing the domain record directly would expose implementation-specific JSON shapes.
                Content = JsonContent.Create(ToProviderRequest(request)),
            };
            // The configured provider credential is forwarded only to the bounded cognition gateway. It is never
            // serialized into gameplay payloads, persisted, or included in diagnostics.
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", providerApiKey);
            using var response = await _http.SendAsync(message, cancellation.Token);
            if (!response.IsSuccessStatusCode)
            {
                failure = ClassifyStatus(response.StatusCode);
                reason = $"http-{(int) response.StatusCode}";
            }
            else
            {
                responseValue = await response.Content.ReadFromJsonAsync<NCNeuralResponseEnvelope>(cancellation.Token);
                actor = await _actors.GetAsync(request.ActorId, true);
            }
        }
        catch (OperationCanceledException)
        {
            failure = sessionToken.IsCancellationRequested
                ? NCNeuralFailureKind.SessionCancelled
                : NCNeuralFailureKind.Timeout;
            reason = failure == NCNeuralFailureKind.Timeout ? "timeout" : "session-cancelled";
        }
        catch (JsonException exception)
        {
            failure = NCNeuralFailureKind.Schema;
            reason = exception.GetType().Name;
        }
        catch (NotSupportedException exception)
        {
            failure = NCNeuralFailureKind.Schema;
            reason = exception.GetType().Name;
        }
        catch (HttpRequestException exception)
        {
            failure = exception.StatusCode is { } status
                ? ClassifyStatus(status)
                : NCNeuralFailureKind.Transport;
            reason = exception.StatusCode is { } statusCode
                ? $"http-{(int) statusCode}"
                : exception.GetType().Name;
        }
        catch (Exception exception)
        {
            failure = NCNeuralFailureKind.Transport;
            reason = exception.GetType().Name;
        }

        // Worker continuations never touch ECS state. Update drains this bounded mailbox on the simulation thread.
        _transportCompletions.Enqueue(new TransportCompletion(
            generation, request, responseValue, actor, failure, reason));
    }

    private void CompleteExecution(TransportCompletion completion)
    {
        var request = completion.Request;
        if (completion.Generation != _generation || !_active || request.SessionEpoch != _epoch)
            return;
        _inFlight = Math.Max(0, _inFlight - 1);
        if (completion.Failure != NCNeuralFailureKind.None)
        {
            if (completion.Failure == NCNeuralFailureKind.Timeout)
                _timeouts++;
            if (completion.Failure == NCNeuralFailureKind.Schema)
                _rejects++;
            RecordFailure(completion.Failure, completion.Reason);
            FailCircuit();
            Publish(Fallback(
                request,
                completion.Failure == NCNeuralFailureKind.Timeout
                    ? NCNeuralResultStatus.Timeout
                    : NCNeuralResultStatus.Fallback,
                completion.Reason,
                completion.Failure));
            return;
        }

        var now = CurrentTime();
        var result = Validate(request, completion.Response, completion.Actor, now);
        if (result.Status == NCNeuralResultStatus.Invalid)
        {
            _rejects++;
            RecordFailure(NCNeuralFailureKind.Schema, result.Reason);
            FailCircuit();
        }

        // A syntactically valid provider usage receipt is billable even when the gameplay answer is stale or
        // rejected. Persist it before publishing the result so budgets cannot be bypassed with invalid responses.
        if (completion.Response is { } response && CanAccountUsage(request, response, _policy!))
        {
            AccountUsage(request, response);
            _ = PersistUsageAsync(completion.Generation, result, response, _policy!, now);
            return;
        }
        Publish(result);
    }

    private async Task PersistUsageAsync(
        int generation,
        NCNeuralResult result,
        NCNeuralResponseEnvelope response,
        NCNeuralPolicyPrototype policy,
        NCActiveWorldTime now)
    {
        try
        {
            var persisted = await _usageLedger.RecordAsync(result.Request, response, policy, now);
            _usageCompletions.Enqueue(new UsageCompletion(
                generation,
                result,
                persisted != null,
                persisted == null ? "usage-ledger-rejected" : string.Empty));
        }
        catch (Exception exception)
        {
            _usageCompletions.Enqueue(new UsageCompletion(
                generation,
                result,
                false,
                exception.GetType().Name));
        }
    }

    private void CompleteUsage(UsageCompletion completion)
    {
        var request = completion.Result.Request;
        if (completion.Generation != _generation || !_active || request.SessionEpoch != _epoch)
            return;

        if (!completion.Persisted)
        {
            RecordFailure(NCNeuralFailureKind.Persistence, completion.Reason);
            // Provider spend without a durable receipt must fail closed. Deterministic AI remains active, while
            // further Neural calls are denied until the next round instead of accumulating untracked cost.
            TryDisableProviderForRound();
            Publish(Fallback(request, NCNeuralResultStatus.Fallback, completion.Reason,
                NCNeuralFailureKind.Persistence));
            return;
        }

        if (completion.Result.Status == NCNeuralResultStatus.Accepted)
        {
            _failures = 0;
            _lastFailureKind = NCNeuralFailureKind.None;
            _lastFailureReason = string.Empty;
            _accepted++;
            // A previous answer is never a safe fallback for a new utterance, even when the Actor aggregate itself
            // did not change between turns. Strategic results retain strict context-version caching.
            if (request.Kind != NCNeuralRequestKind.DialogueContinuation)
                _cache[(request.ActorId, request.Kind)] = completion.Result.Response!;
        }
        Publish(completion.Result);
    }

    private void DrainCompletions(int maximum)
    {
        for (var processed = 0; processed < maximum; processed++)
        {
            if (_budgetLoadCompletions.TryDequeue(out var budget))
            {
                CompleteBudgetLoad(budget);
                continue;
            }

            if (_usageCompletions.TryDequeue(out var usage))
            {
                CompleteUsage(usage);
                continue;
            }

            if (_transportCompletions.TryDequeue(out var transport))
            {
                CompleteExecution(transport);
                continue;
            }

            break;
        }
    }

    private void DrainDisabledQueue(int maximum)
    {
        for (var processed = 0; processed < maximum && _queue.TryDequeue(out var request, out _); processed++)
        {
            Publish(Fallback(request, NCNeuralResultStatus.BudgetDenied, "administratively-disabled"));
        }
    }

    private async Task LoadBudgetAsync(int generation, NCSessionEpoch sessionEpoch)
    {
        try
        {
            var periodKey = NCNeuralUsageLedgerSystem.CurrentWeekKey(DateTimeOffset.UtcNow);
            var session = await _usageLedger.GetSessionAsync(sessionEpoch);
            var week = await _usageLedger.GetWeekAsync(periodKey);
            _budgetLoadCompletions.Enqueue(new BudgetLoadCompletion(
                generation,
                sessionEpoch,
                true,
                session,
                periodKey,
                week,
                string.Empty));
        }
        catch (Exception exception)
        {
            _budgetLoadCompletions.Enqueue(new BudgetLoadCompletion(
                generation,
                sessionEpoch,
                false,
                null,
                string.Empty,
                null,
                exception.GetType().Name));
        }
    }

    private void CompleteBudgetLoad(BudgetLoadCompletion completion)
    {
        if (completion.Generation != _generation || !_active || completion.SessionEpoch != _epoch)
            return;

        if (!completion.Succeeded)
        {
            RecordFailure(NCNeuralFailureKind.Persistence, completion.Reason);
            return;
        }

        var snapshot = completion.Session?.Snapshot;
        _inputTokens = snapshot?.InputTokens ?? 0;
        _outputTokens = snapshot?.OutputTokens ?? 0;
        _costUsdMicros = snapshot?.TotalCostUsdMicros ?? 0;
        _weeklyPeriodKey = completion.PeriodKey;
        _weeklyCostUsdMicros = completion.Week?.Snapshot.TotalCostUsdMicros ?? 0;
        _weeklyActorCosts.Clear();
        _weeklyKindRequests.Clear();
        if (completion.Week?.Snapshot is { } week)
        {
            foreach (var (key, cost) in week.ActorCostUsdMicros)
            {
                if (Guid.TryParse(key, out var actorId))
                    _weeklyActorCosts[new NCActorId(actorId)] = cost;
            }
            foreach (var (key, count) in week.KindRecordedRequests)
            {
                if (Enum.TryParse<NCNeuralRequestKind>(key, out var kind))
                    _weeklyKindRequests[kind] = count;
            }
        }
        _budgetReady = true;
    }

    private void AccountUsage(NCNeuralRequestEnvelope request, NCNeuralResponseEnvelope response)
    {
        _inputTokens = checked(_inputTokens + response.InputTokens);
        _outputTokens = checked(_outputTokens + response.OutputTokens);
        var uncachedInput = response.InputTokens - response.CachedInputTokens;
        var inputCost = NCNeuralUsageLedgerSystem.CalculateCost(
            uncachedInput,
            _policy!.InputPriceUsdMicrosPerMillionTokens);
        var cachedCost = NCNeuralUsageLedgerSystem.CalculateCost(
            response.CachedInputTokens,
            _policy.CachedInputPriceUsdMicrosPerMillionTokens);
        var outputCost = NCNeuralUsageLedgerSystem.CalculateCost(
            response.OutputTokens,
            _policy.OutputPriceUsdMicrosPerMillionTokens);
        var totalCost = checked(inputCost + cachedCost + outputCost);
        _costUsdMicros = checked(_costUsdMicros + totalCost);
        _weeklyCostUsdMicros = checked(_weeklyCostUsdMicros + totalCost);
        _weeklyActorCosts[request.ActorId] = checked(_weeklyActorCosts.GetValueOrDefault(request.ActorId) + totalCost);
        _weeklyKindRequests[request.Kind] = checked(_weeklyKindRequests.GetValueOrDefault(request.Kind) + 1);

        var state = GetActorRuntimeState(request.ActorId);
        _actorRuntime[request.ActorId] = state with
        {
            InputTokens = checked(state.InputTokens + response.InputTokens),
            CachedInputTokens = checked(state.CachedInputTokens + response.CachedInputTokens),
            OutputTokens = checked(state.OutputTokens + response.OutputTokens),
            CostUsdMicros = checked(state.CostUsdMicros + totalCost),
            UpdatedSequence = ++_actorRuntimeSequence,
        };
    }

    private static bool CanAccountUsage(
        NCNeuralRequestEnvelope request,
        NCNeuralResponseEnvelope response,
        NCNeuralPolicyPrototype policy) =>
        response.RequestId == request.RequestId && response.InputTokens >= 0 && response.OutputTokens >= 0 &&
        response.CachedInputTokens >= 0 && response.CachedInputTokens <= response.InputTokens &&
        response.ProviderModel == policy.ProviderModel && Stable(response.ProviderRequestId);

    private void RecordFailure(NCNeuralFailureKind kind, string reason)
    {
        _lastFailureKind = kind;
        _lastFailureReason = reason.Length <= 96 ? reason : reason[..96];
    }

    private static NCNeuralFailureKind ClassifyStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => NCNeuralFailureKind.Authentication,
        HttpStatusCode.TooManyRequests => NCNeuralFailureKind.RateLimited,
        HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
            NCNeuralFailureKind.ServiceUnavailable,
        HttpStatusCode.BadGateway or HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest =>
            NCNeuralFailureKind.ProviderRejected,
        _ => NCNeuralFailureKind.Transport,
    };

    private NCNeuralResult Validate(
        NCNeuralRequestEnvelope request,
        NCNeuralResponseEnvelope? response,
        NCCityActorRecord? actor,
        NCActiveWorldTime now)
    {
        if (request.ExpiresAt <= now)
            return new(NCNeuralResultStatus.Expired, request, null, "expiry");
        // Dialogue is correlated against the authoritative conversation version at delivery time. Unrelated routine
        // or location persistence may advance the actor aggregate while the provider is composing a harmless line.
        // Strategic suggestions retain strict optimistic concurrency because they can influence later planning.
        if (actor == null || request.Kind != NCNeuralRequestKind.DialogueContinuation &&
            actor.AggregateVersion != request.ContextVersion)
            return new(NCNeuralResultStatus.Stale, request, null, "context-version");
        if (response == null || response.RequestId != request.RequestId ||
            response.SchemaVersion != NCNeuralContract.SchemaVersion ||
            response.ContextVersion != request.ContextVersion || response.Confidence is < 0 or > 1000 ||
            response.InputTokens < 0 || response.CachedInputTokens < 0 ||
            response.CachedInputTokens > response.InputTokens || response.OutputTokens < 0 ||
            response.InputTokens > _policy!.MaximumRequestInputTokens ||
            response.OutputTokens > _policy.MaximumRequestOutputTokens ||
            response.ProviderModel != _policy.ProviderModel || !Stable(response.ProviderRequestId) ||
            !request.AllowedCapabilityIds.Contains(response.SelectedCapabilityId) ||
            response.Intent != null &&
            !response.Intent.RequestedCapabilityIds.Contains(response.SelectedCapabilityId) ||
            response.SpeechActKey.Length > 96 || !Stable(response.SummaryKey) ||
            response.RenderedText.Length > _policy.MaximumTextLength ||
            request.Kind != NCNeuralRequestKind.DialogueContinuation &&
            response.Confidence < (request.Autonomy?.MinimumIntentConfidence ?? int.MaxValue) ||
            request.Kind == NCNeuralRequestKind.DialogueContinuation &&
            string.IsNullOrWhiteSpace(response.RenderedText) || !ValidIntent(request, response.Intent) ||
            !ValidDisclosureResponse(request, response))
            return new(NCNeuralResultStatus.Invalid, request, null, "schema-or-capability",
                NCNeuralFailureKind.Schema);
        return new(NCNeuralResultStatus.Accepted, request, response, string.Empty);
    }

    private NCNeuralResult Fallback(
        NCNeuralRequestEnvelope request,
        NCNeuralResultStatus status,
        string reason,
        NCNeuralFailureKind failure = NCNeuralFailureKind.None)
    {
        _fallbacks++;
        if (request.Kind != NCNeuralRequestKind.DialogueContinuation &&
            _cache.TryGetValue((request.ActorId, request.Kind), out var cached) &&
            cached.ContextVersion == request.ContextVersion && request.AllowedCapabilityIds.Contains(
                cached.SelectedCapabilityId))
        {
            return new(NCNeuralResultStatus.Fallback, request, cached, $"cached:{reason}", failure);
        }
        var capability = request.AllowedCapabilityIds.FirstOrDefault() ?? string.Empty;
        return new(status, request, new NCNeuralResponseEnvelope(request.RequestId, NCNeuralContract.SchemaVersion,
            request.ContextVersion,
            capability, "safe-default", string.Empty, "safe-default", 0, 0, 0, 0, string.Empty,
            _policy?.ProviderModel ?? string.Empty), reason, failure);
    }

    private void FailCircuit()
    {
        if (++_failures >= _policy!.CircuitFailureThreshold)
            _circuitOpenUntil = CurrentTime().Advance(_policy.CircuitOpenDuration);
    }
    private void Publish(NCNeuralResult result)
    {
        ReleaseBudget(result.Request);
        if (result.Status == NCNeuralResultStatus.Stale)
            _stale++;
        else if (result.Status == NCNeuralResultStatus.Invalid)
            _invalid++;
        if (IsProviderFailure(result.FailureKind))
            _providerFailures++;
        var state = GetActorRuntimeState(result.Request.ActorId);
        var response = result.Response;
        _actorRuntime[result.Request.ActorId] = state with
        {
            Results = checked(state.Results + 1),
            Accepted = checked(state.Accepted + (result.Status == NCNeuralResultStatus.Accepted ? 1 : 0)),
            LastRequestId = result.Request.RequestId,
            LastRequestKind = result.Request.Kind,
            LastStatus = result.Status,
            LastFailureKind = result.FailureKind,
            LastReason = BoundDiagnostic(result.Reason),
            LastCapabilityId = response?.SelectedCapabilityId ?? string.Empty,
            LastSummaryKey = response?.SummaryKey ?? string.Empty,
            LastProviderModel = response?.ProviderModel ?? string.Empty,
            UpdatedSequence = ++_actorRuntimeSequence,
        };
        RaiseLocalEvent(new NCNeuralResultEvent(result));
    }

    private ActorRuntimeState GetActorRuntimeState(NCActorId actorId)
    {
        if (_actorRuntime.TryGetValue(actorId, out var state))
            return state;

        var maximum = _policy?.MaximumTrackedActorDiagnostics ?? 256;
        if (_actorRuntime.Count >= maximum)
        {
            var oldestActor = default(NCActorId);
            var oldestSequence = long.MaxValue;
            foreach (var pair in _actorRuntime)
            {
                if (pair.Value.UpdatedSequence >= oldestSequence)
                    continue;
                oldestActor = pair.Key;
                oldestSequence = pair.Value.UpdatedSequence;
            }

            if (oldestActor.IsValid)
                _actorRuntime.Remove(oldestActor);
        }

        return new ActorRuntimeState(UpdatedSequence: ++_actorRuntimeSequence);
    }

    private static string BoundDiagnostic(string value) => value.Length <= 96 ? value : value[..96];

    private static bool IsProviderFailure(NCNeuralFailureKind kind) => kind is
        NCNeuralFailureKind.Timeout or
        NCNeuralFailureKind.Authentication or
        NCNeuralFailureKind.ServiceUnavailable or
        NCNeuralFailureKind.RateLimited or
        NCNeuralFailureKind.ProviderRejected or
        NCNeuralFailureKind.Transport;

    private bool CanReserveBudget(NCNeuralRequestEnvelope request, out string reason)
    {
        var reservationCost = MaximumReservedRequestCost();
        var alreadyReserved = ReservedCost();
        if (_costUsdMicros + alreadyReserved + reservationCost > _policy!.SessionCostHardCapUsdMicros)
        {
            reason = "session-cost-hard-cap";
            return false;
        }
        if (_weeklyCostUsdMicros + alreadyReserved + reservationCost > _policy.WeeklyCostHardCapUsdMicros)
        {
            reason = "weekly-cost-hard-cap";
            return false;
        }

        var actorReserved = _actorReservations.GetValueOrDefault(request.ActorId);
        if (_weeklyActorCosts.GetValueOrDefault(request.ActorId) +
            checked((actorReserved + 1L) * reservationCost) > _policy.ActorWeeklyCostHardCapUsdMicros)
        {
            reason = "actor-weekly-cost-hard-cap";
            return false;
        }

        var kindReserved = _kindReservations.GetValueOrDefault(request.Kind);
        if (!_policy.RequestKindWeeklyLimits.TryGetValue(request.Kind, out var kindLimit) ||
            _weeklyKindRequests.GetValueOrDefault(request.Kind) + kindReserved >= kindLimit)
        {
            reason = "request-kind-weekly-limit";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private void ReserveBudget(NCNeuralRequestEnvelope request)
    {
        _budgetReservations.Add(request.RequestId);
        _actorReservations[request.ActorId] = checked(_actorReservations.GetValueOrDefault(request.ActorId) + 1);
        _kindReservations[request.Kind] = checked(_kindReservations.GetValueOrDefault(request.Kind) + 1);
    }

    private void ReleaseBudget(NCNeuralRequestEnvelope request)
    {
        if (!_budgetReservations.Remove(request.RequestId))
            return;

        DecrementReservation(_actorReservations, request.ActorId);
        DecrementReservation(_kindReservations, request.Kind);
    }

    private static void DecrementReservation<TKey>(Dictionary<TKey, int> reservations, TKey key)
        where TKey : notnull
    {
        if (!reservations.TryGetValue(key, out var count) || count <= 1)
            reservations.Remove(key);
        else
            reservations[key] = count - 1;
    }

    private long ReservedCost() => checked(_budgetReservations.Count * MaximumReservedRequestCost());

    private bool WeekChanged() => _weeklyPeriodKey.Length > 0 &&
        _weeklyPeriodKey != NCNeuralUsageLedgerSystem.CurrentWeekKey(DateTimeOffset.UtcNow);

    private long MaximumReservedRequestCost() => _policy == null
        ? 0
        : checked(
            NCNeuralUsageLedgerSystem.CalculateCost(
                _policy.MaximumRequestInputTokens,
                _policy.InputPriceUsdMicrosPerMillionTokens) +
            NCNeuralUsageLedgerSystem.CalculateCost(
                _policy.MaximumRequestOutputTokens,
                _policy.OutputPriceUsdMicrosPerMillionTokens));

    private void ClearBudgetReservations()
    {
        _budgetReservations.Clear();
        _actorReservations.Clear();
        _kindReservations.Clear();
    }

    private NCNeuralBudgetMode Mode()
    {
        var reservedCost = ReservedCost();
        if (_policy == null || !ProviderEnabled || _inputTokens + _outputTokens >= _policy.SessionTokenHardCap ||
            _costUsdMicros + reservedCost >= _policy.SessionCostHardCapUsdMicros ||
            _weeklyCostUsdMicros + reservedCost >= _policy.WeeklyCostHardCapUsdMicros)
            return NCNeuralBudgetMode.FallbackOnly;
        var used = _inputTokens + _outputTokens;
        return used >= _policy.RestrictedThreshold || _costUsdMicros >= _policy.RestrictedCostThresholdUsdMicros
            ? NCNeuralBudgetMode.Restricted
            : used >= _policy.EconomyThreshold || _costUsdMicros >= _policy.EconomyCostThresholdUsdMicros
                ? NCNeuralBudgetMode.Economy
                : NCNeuralBudgetMode.Normal;
    }
    private NCActiveWorldTime CurrentTime() => _clock.TryGetCurrentTime(out _, out var now) ? now : default;
    private bool ProviderEnabled =>
        !_administrativelyDisabled && _budgetReady &&
        (_policy?.Enabled == true || _configuration.GetCVar(NCCVars.NeuralEnabled)) &&
        ProviderApiKey.Length > 0;
    private string ProviderApiKey => _configuration.GetCVar(NCCVars.NeuralApiKey).Trim();
    private string ProviderEndpoint
    {
        get
        {
            var configured = _configuration.GetCVar(NCCVars.NeuralEndpoint).Trim();
            return configured.Length == 0 ? _policy!.Endpoint : configured;
        }
    }
    private bool TryGetProviderUri(out Uri providerUri)
    {
        if (!Uri.TryCreate(ProviderEndpoint, UriKind.Absolute, out providerUri!))
            return false;

        // A bearer credential may use plain HTTP only over loopback. Remote services must terminate TLS.
        return providerUri.Scheme == Uri.UriSchemeHttps ||
               providerUri.Scheme == Uri.UriSchemeHttp && providerUri.IsLoopback;
    }
    private static NCNeuralProviderRequest ToProviderRequest(NCNeuralRequestEnvelope request)
    {
        return new NCNeuralProviderRequest(
            request.RequestId,
            request.Kind.ToString(),
            request.Priority.ToString(),
            new NCNeuralProviderGuid(request.ActorId.Value),
            new NCNeuralProviderGuid(request.SessionEpoch.Value),
            request.ContextVersion,
            new NCNeuralProviderTime(request.ExpiresAt.Ticks),
            request.PromptProfile.Id,
            request.Facts,
            request.AllowedCapabilityIds,
            request.TopicKeys,
            request.Locale,
            request.Persona,
            request.Autonomy,
            request.PerceivedSubjects ?? [],
            request.PerceivedEvents ?? [],
            request.KnownLocations ?? [],
            request.DialogueInput,
            request.AllowedDirectiveActionIds ?? [],
            request.Disclosure!,
            request.MaximumOutputTokens);
    }
    private void OnStarted(NCActiveWorldSessionStartedEvent args)
    {
        Reset();
        if (_prototypes.TryIndex(DefaultPolicy, out NCNeuralPolicyPrototype? policy) && Valid(policy))
        {
            _policy = policy;
            _epoch = args.Epoch;
            _sessionCancellation = new CancellationTokenSource();
            _active = true;
            _ = LoadBudgetAsync(_generation, _epoch);
        }
    }
    private void Deactivate()
    {
        _active = false;
        _generation++;
        _sessionCancellation?.Cancel();
        _queue.Clear();
        _inFlight = 0;
    }
    private void Reset()
    {
        Deactivate();
        _sessionCancellation?.Dispose();
        _sessionCancellation = null;
        _known.Clear(); _cache.Clear(); _actorRuntime.Clear(); _actorRuntimeSequence = 0; _inputTokens = 0;
        _outputTokens = 0; _costUsdMicros = 0; _weeklyCostUsdMicros = 0; _weeklyPeriodKey = string.Empty;
        _weeklyActorCosts.Clear(); _weeklyKindRequests.Clear(); ClearBudgetReservations();
        _accepted = 0; _stale = 0; _invalid = 0; _providerFailures = 0; _fallbacks = 0;
        _timeouts = 0; _rejects = 0;
        _failures = 0; _circuitOpenUntil = null;
        _lastFailureKind = NCNeuralFailureKind.None; _lastFailureReason = string.Empty;
        _administrativelyDisabled = false;
        _budgetReady = false;
        _policy = null;
    }
    private static bool Valid(NCNeuralPolicyPrototype value) => value.MaximumQueued is > 0 and <= 4096 &&
        value.MaximumConcurrent is > 0 and <= 32 && value.MaximumCompletionsPerTick is > 0 and <= 256 &&
        value.MaximumFacts is > 0 and <= 256 &&
        value.MaximumCapabilities is > 0 and <= 128 && value.MaximumPerceivedSubjects is > 0 and <= 128 &&
        value.MaximumKnownLocations is > 0 and <= 512 &&
        value.MaximumTextLength is > 0 and <= 4096 &&
        value.MaximumDialogueInputLength is > 0 and <= 4096 &&
        value.MaximumDialogueHistoryTurns is > 0 and <= 32 &&
        value.MaximumDialogueSummaryKeys is > 0 and <= 64 &&
        value.EconomyThreshold > 0 && value.RestrictedThreshold >= value.EconomyThreshold &&
        value.SessionTokenHardCap >= value.RestrictedThreshold && value.RequestTimeout > TimeSpan.Zero &&
        Stable(value.ProviderModel) && value.InputPriceUsdMicrosPerMillionTokens > 0 &&
        value.CachedInputPriceUsdMicrosPerMillionTokens > 0 &&
        value.CachedInputPriceUsdMicrosPerMillionTokens <= value.InputPriceUsdMicrosPerMillionTokens &&
        value.OutputPriceUsdMicrosPerMillionTokens > 0 && value.EconomyCostThresholdUsdMicros > 0 &&
        value.RestrictedCostThresholdUsdMicros >= value.EconomyCostThresholdUsdMicros &&
        value.SessionCostHardCapUsdMicros >= value.RestrictedCostThresholdUsdMicros &&
        value.WeeklyCostHardCapUsdMicros >= value.SessionCostHardCapUsdMicros &&
        value.ActorWeeklyCostHardCapUsdMicros is > 0 &&
        value.ActorWeeklyCostHardCapUsdMicros <= value.WeeklyCostHardCapUsdMicros &&
        value.RequestKindWeeklyLimits.Count == Enum.GetValues<NCNeuralRequestKind>().Length &&
        Enum.GetValues<NCNeuralRequestKind>().All(kind =>
            value.RequestKindWeeklyLimits.TryGetValue(kind, out var limit) && limit > 0) &&
        value.MaximumRequestInputTokens is > 0 and <= 262144 &&
        value.MaximumRequestOutputTokens is > 0 and <= 8192 &&
        value.OrdinaryDialogueMaximumOutputTokens is > 0 &&
        value.OrdinaryDialogueMaximumOutputTokens <= value.MaximumRequestOutputTokens &&
        value.RequestLifetime >= value.RequestTimeout && value.MinimumSuggestionConfidence is >= 0 and <= 1000 &&
        float.IsFinite(value.MaximumUtilityBoost) && value.MaximumUtilityBoost is >= 0 and <= 1000 &&
        value.MaximumPendingCorrelations is > 0 and <= 16384 && value.SuggestionLifetime > TimeSpan.Zero &&
        value.MaximumAppliedSuggestions is > 0 and <= 100000 &&
        value.MaximumTrackedActorDiagnostics is > 0 and <= 4096 && value.StrategicRequestCooldown > TimeSpan.Zero &&
        Uri.TryCreate(value.Endpoint, UriKind.Absolute, out _);
    private static bool Valid(NCNeuralRequestEnvelope value, NCNeuralPolicyPrototype policy) =>
        value.RequestId != Guid.Empty && value.ActorId.IsValid && value.SessionEpoch.IsValid &&
        value.ContextVersion > 0 && value.ExpiresAt.IsValid && value.Facts.Count <= policy.MaximumFacts &&
        value.AllowedCapabilityIds.Count is > 0 && value.AllowedCapabilityIds.Count <= policy.MaximumCapabilities &&
        value.MaximumOutputTokens is > 0 && value.MaximumOutputTokens <= policy.MaximumRequestOutputTokens &&
        value.AllowedCapabilityIds.All(Stable) && value.TopicKeys.All(Stable) &&
        value.Facts.Select(fact => fact.Handle).Distinct(StringComparer.Ordinal).Count() == value.Facts.Count &&
        value.Facts.All(fact => Stable(fact.Handle) && Stable(fact.Domain) && Stable(fact.Key) &&
            Stable(fact.Subject) && float.IsFinite(fact.Confidence) &&
            fact.Confidence is >= 0 and <= 1 && Stable(fact.Privacy)) &&
        (value.PerceivedSubjects?.Count ?? 0) <= policy.MaximumPerceivedSubjects &&
        (value.PerceivedSubjects ?? []).All(subject => Stable(subject.Handle) && Stable(subject.Kind) &&
            subject.DisplayName.Length is > 0 and <= 128 &&
            subject.DisplayName.All(character => !char.IsControl(character)) &&
            float.IsFinite(subject.Confidence) && subject.Confidence is >= 0 and <= 1 && subject.ExpiresAtTicks > 0) &&
        (value.PerceivedEvents?.Count ?? 0) <= policy.MaximumPerceivedSubjects &&
        (value.PerceivedEvents ?? []).All(perceivedEvent => Stable(perceivedEvent.Kind) &&
            Stable(perceivedEvent.ActorHandle) && Stable(perceivedEvent.TargetHandle) &&
            float.IsFinite(perceivedEvent.Confidence) && perceivedEvent.Confidence is >= 0 and <= 1 &&
            perceivedEvent.ObservedAtTicks > 0 && perceivedEvent.ExpiresAtTicks > perceivedEvent.ObservedAtTicks) &&
        (value.KnownLocations?.Count ?? 0) <= policy.MaximumKnownLocations &&
        (value.KnownLocations ?? []).All(location => Stable(location.Handle) && Stable(location.Kind) &&
            location.DisplayName.Length is > 0 and <= 128 &&
            location.DisplayName.All(character => !char.IsControl(character))) &&
        (value.AllowedDirectiveActionIds?.Count ?? 0) <= 64 &&
        (value.AllowedDirectiveActionIds ?? []).All(Stable) &&
        value.Autonomy is { } autonomy && Stable(autonomy.PolicyKey) &&
        autonomy.MinimumIntentConfidence is >= 0 and <= 1000 &&
        autonomy.MaximumIntentPriority is >= 0 and <= 1000 &&
        ValidDisclosure(value) &&
        ValidDialogue(value, policy);

    private static bool ValidDisclosure(NCNeuralRequestEnvelope request)
    {
        if (request.Disclosure is not { } disclosure || !Stable(disclosure.MaximumPrivacy) ||
            disclosure.KnownFactHandles.Count > request.Facts.Count ||
            disclosure.AllowedFactHandles.Count > disclosure.KnownFactHandles.Count ||
            disclosure.ForbiddenAssertionKeys.Count > 64 || disclosure.ForbiddenTopicKeys.Count > 64 ||
            disclosure.ForbiddenAssertionKeys.Any(value => !Stable(value)) ||
            disclosure.ForbiddenTopicKeys.Any(value => !Stable(value)))
            return false;

        var factHandles = request.Facts.Select(value => value.Handle).ToHashSet(StringComparer.Ordinal);
        var known = disclosure.KnownFactHandles.ToHashSet(StringComparer.Ordinal);
        var allowed = disclosure.AllowedFactHandles.ToHashSet(StringComparer.Ordinal);
        return known.Count == disclosure.KnownFactHandles.Count && allowed.Count == disclosure.AllowedFactHandles.Count &&
            known.SetEquals(factHandles) && allowed.IsSubsetOf(known);
    }

    private static bool ValidDisclosureResponse(
        NCNeuralRequestEnvelope request,
        NCNeuralResponseEnvelope response)
    {
        if (request.Disclosure is not { } disclosure)
            return false;
        var known = disclosure.KnownFactHandles.ToHashSet(StringComparer.Ordinal);
        var allowed = disclosure.AllowedFactHandles.ToHashSet(StringComparer.Ordinal);
        var disclosed = (response.DisclosedFactHandles ?? []).ToHashSet(StringComparer.Ordinal);
        var withheld = (response.WithheldFactHandles ?? []).ToHashSet(StringComparer.Ordinal);
        return disclosed.Count == (response.DisclosedFactHandles?.Count ?? 0) &&
            withheld.Count == (response.WithheldFactHandles?.Count ?? 0) &&
            disclosed.IsSubsetOf(allowed) && withheld.IsSubsetOf(known) && !disclosed.Overlaps(withheld);
    }

    private static bool ValidIntent(NCNeuralRequestEnvelope request, NCNeuralIntentProposal? intent)
    {
        if (intent == null)
            return request.Kind == NCNeuralRequestKind.DialogueContinuation;
        var autonomy = request.Autonomy;
        if (autonomy == null || !Stable(intent.GoalKey) || !Stable(intent.ExpectedOutcomeKey) ||
            intent.Priority < 0 || intent.Priority > autonomy.MaximumIntentPriority ||
            intent.RequestedCapabilityIds.Count is < 1 or > 16 ||
            intent.RequestedCapabilityIds.Any(value => !request.AllowedCapabilityIds.Contains(value)) ||
            intent.InformationGapKeys.Count > 16 || intent.InformationGapKeys.Any(value => !Stable(value)) ||
            intent.Arguments.Count > 16 || intent.Arguments.Any(value =>
                !Stable(value.Key) || value.Value.Length > 256 || value.Value.Any(char.IsControl)))
            return false;
        if (intent.TargetHandle.Length == 0)
            return true;
        return autonomy.TargetedIntentsAllowed && Stable(intent.TargetHandle) && (request.PerceivedSubjects ?? [])
            .Any(value => value.Handle == intent.TargetHandle);
    }
    private static bool ValidDialogue(NCNeuralRequestEnvelope value, NCNeuralPolicyPrototype policy)
    {
        if (value.Kind != NCNeuralRequestKind.DialogueContinuation)
            return value.DialogueInput == null;
        return value.DialogueInput is { } dialogue &&
               dialogue.ActorDisplayName.Length is > 0 and <= 128 &&
               dialogue.Utterance.Length is > 0 && dialogue.Utterance.Length <= policy.MaximumDialogueInputLength &&
               dialogue.History.Count <= policy.MaximumDialogueHistoryTurns &&
               dialogue.History.All(turn =>
                   turn.Speaker is "self" or "interlocutor" && Stable(turn.SpeechAct) && turn.AtTicks > 0 &&
                   turn.Utterance.Length is > 0 && turn.Utterance.Length <= policy.MaximumDialogueInputLength &&
                   turn.Utterance.All(character => !char.IsControl(character))) &&
               (dialogue.EarlierSummary == null ||
                   dialogue.EarlierSummary.EarlierTurnCount >= 0 &&
                   dialogue.EarlierSummary.TopicKeys.Count <= policy.MaximumDialogueSummaryKeys &&
                   dialogue.EarlierSummary.SemanticSummaryKeys.Count <= policy.MaximumDialogueSummaryKeys &&
                   dialogue.EarlierSummary.TopicKeys.All(Stable) &&
                   dialogue.EarlierSummary.SemanticSummaryKeys.All(Stable)) &&
               (dialogue.SpeakerHandle.Length == 0 || Stable(dialogue.SpeakerHandle) &&
                   (value.PerceivedSubjects ?? []).Any(subject => subject.Handle == dialogue.SpeakerHandle)) &&
               dialogue.ActorDisplayName.All(character => !char.IsControl(character)) &&
               dialogue.Utterance.All(character => !char.IsControl(character));
    }
    private static bool Stable(string value) => value.Length is > 0 and <= 128 && value.All(character =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    private sealed record TransportCompletion(
        int Generation,
        NCNeuralRequestEnvelope Request,
        NCNeuralResponseEnvelope? Response,
        NCCityActorRecord? Actor,
        NCNeuralFailureKind Failure,
        string Reason);

    private sealed record UsageCompletion(
        int Generation,
        NCNeuralResult Result,
        bool Persisted,
        string Reason);

    private sealed record BudgetLoadCompletion(
        int Generation,
        NCSessionEpoch SessionEpoch,
        bool Succeeded,
        NCNeuralUsageSessionRecord? Session,
        string PeriodKey,
        NCNeuralUsageWeekRecord? Week,
        string Reason);

    private sealed record ActorRuntimeState(
        int InputTokens = 0,
        int CachedInputTokens = 0,
        int OutputTokens = 0,
        long CostUsdMicros = 0,
        long Results = 0,
        long Accepted = 0,
        Guid LastRequestId = default,
        NCNeuralRequestKind LastRequestKind = default,
        NCNeuralResultStatus LastStatus = default,
        NCNeuralFailureKind LastFailureKind = default,
        string LastReason = "",
        string LastCapabilityId = "",
        string LastSummaryKey = "",
        string LastProviderModel = "",
        long UpdatedSequence = 0);
}

public sealed class NCNeuralResultEvent(NCNeuralResult result) : EntityEventArgs
{ public NCNeuralResult Result { get; } = result; }

/// <summary>
/// Primitive-only HTTP contract. It prevents RobustToolbox and persistence wrapper serialization details from
/// becoming part of the external Neural Cognition Service protocol.
/// </summary>
internal sealed record NCNeuralProviderRequest(
    Guid RequestId,
    string Kind,
    string Priority,
    NCNeuralProviderGuid ActorId,
    NCNeuralProviderGuid SessionEpoch,
    long ContextVersion,
    NCNeuralProviderTime ExpiresAt,
    string PromptProfile,
    IReadOnlyList<NCNeuralFact> Facts,
    IReadOnlyList<string> AllowedCapabilityIds,
    IReadOnlyList<string> TopicKeys,
    string Locale,
    NCNeuralPersonaContext? Persona,
    NCNeuralAutonomyContext? Autonomy,
    IReadOnlyList<NCNeuralPerceivedSubject> PerceivedSubjects,
    IReadOnlyList<NCNeuralPerceivedEvent> PerceivedEvents,
    IReadOnlyList<NCNeuralKnownLocation> KnownLocations,
    NCNeuralDialogueInput? DialogueInput,
    IReadOnlyList<string> AllowedDirectiveActionIds,
    NCNeuralDisclosureContext Disclosure,
    int MaximumOutputTokens);

internal sealed record NCNeuralProviderGuid(Guid Value);
internal sealed record NCNeuralProviderTime(long Ticks);
