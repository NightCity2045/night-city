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
using Content.Shared._NC.LivingCity.Actors;
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

public sealed record NCNeuralRuntimeSnapshot(bool Active, bool AdministrativelyDisabled, bool ProviderConfigured,
    bool UsagePersistenceHealthy,
    int Queued, int InFlight,
    int SessionInputTokens, int SessionOutputTokens, long SessionCostUsdMicros,
    string WeeklyPeriodKey, long WeeklyCostUsdMicros,
    long SocialCognitionRequests, long StrategicDecisionRequests, long DialogueContinuationRequests,
    long MemorySummaryRequests, long NarrativeRenderRequests,
    long Accepted, long Stale, long Invalid, long ProviderFailures, long Fallbacks, long Coalesced,
    long UsagePersistenceFailures,
    long Timeouts, long ValidationRejects, int ConsecutiveFailures, NCActiveWorldTime? CircuitOpenUntil,
    NCNeuralFailureKind LastFailureKind, string LastFailureReason,
    IReadOnlyDictionary<NCNeuralRequestKind, NCNeuralKindRuntimeSnapshot> KindRuntime);

/// <summary>Bounded aggregate telemetry for one request kind. Payloads and credentials are never retained.</summary>
public sealed record NCNeuralKindRuntimeSnapshot(
    long Submitted,
    long Accepted,
    long Fallback,
    long Rejected,
    long InputTokens,
    long OutputTokens,
    long CostUsdMicros);

/// <summary>
/// Bounded round-runtime observability for one authored Actor. It contains usage metadata and stable semantic keys,
/// never prompts, rendered dialogue, private facts, or provider credentials.
/// </summary>
public sealed record NCNeuralActorRuntimeSnapshot(
    NCNeuralAccessTier AccessTier,
    int OutstandingRequests,
    bool HasPendingRequest,
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
    string LastProviderModel,
    string LastPromptStableKey,
    int LastPromptVersion,
    string LastEvaluationProfile,
    int LastSchemaVersion);

/// <summary>
/// Bounded neural transport queue. Validated responses are published to the physical action authority; this client
/// never mutates gameplay state itself.
/// </summary>
public sealed partial class NCNeuralCognitionClientSystem : EntitySystem, INCNeuralCognitionClient
{
    private static readonly ProtoId<NCNeuralPolicyPrototype> DefaultPolicy = "Default";
    private readonly HttpClient _http = new();
    private readonly NCNeuralPendingRequestQueue _queue = new();
    private readonly ConcurrentQueue<TransportCompletion> _transportCompletions = new();
    private readonly ConcurrentQueue<UsageCompletion> _usageCompletions = new();
    private readonly HashSet<Guid> _known = [];
    private readonly Dictionary<Guid, int> _transportRetries = [];
    private readonly Dictionary<NCActorId, long> _newestWakeRevisions = [];
    private readonly Dictionary<NCActorId, long> _actorDecisionRevisions = [];
    private readonly HashSet<NCActorId> _inFlightActors = [];
    private readonly Dictionary<NCActorId, NCNeuralRequestEnvelope> _inFlightRequests = [];
    private readonly HashSet<Guid> _supersededRequests = [];
    private readonly HashSet<NCActorId> _unblockedActors = [];
    private readonly Dictionary<NCActorId, ActorRuntimeState> _actorRuntime = [];
    private readonly Dictionary<NCNeuralRequestKind, KindRuntimeState> _kindRuntime = [];
    private readonly Dictionary<NCNeuralRequestKind, long> _weeklyKindRequests = [];
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCNeuralUsageLedgerSystem _usageLedger = default!;
    private NCNeuralPolicyPrototype? _policy;
    private NCSessionEpoch _epoch;
    private NCActiveWorldTime? _circuitOpenUntil;
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
    private long _coalesced;
    private long _usagePersistenceFailures;
    private long _timeouts;
    private long _rejects;
    private NCNeuralFailureKind _lastFailureKind;
    private string _lastFailureReason = string.Empty;
    private bool _active;
    private bool _administrativelyDisabled;
    private bool _usagePersistenceHealthy;
    private int _generation;
    private CancellationTokenSource? _sessionCancellation;

    /// <summary>True when transport is configured. Spend authority remains entirely with the provider.</summary>
    public bool IsProviderEnabled => _active && ProviderConfigured;

    /// <summary>Only explicitly authored important characters may spend provider tokens. Missing data fails closed.</summary>
    public bool CanUseGpt(NCActorId actorId)
    {
        if (!_actors.TryGetCached(actorId, out var actor) || actor == null ||
            !_prototypes.TryIndex(new ProtoId<NCCityActorArchetypePrototype>(actor.Snapshot.Identity.ArchetypeId),
                out var archetype))
            return false;
        var profile = !string.IsNullOrWhiteSpace(actor.Snapshot.Identity.NarrativeActorProfileId)
            ? new ProtoId<NCNarrativeActorProfilePrototype>(actor.Snapshot.Identity.NarrativeActorProfileId)
            : archetype.NarrativeActorProfile;
        return profile is { } selected && _prototypes.HasIndex(selected);
    }

    /// <summary>Background polling must not repeatedly supersede an answer still being generated for this body.</summary>
    public bool HasOutstandingRequest(NCActorId actorId) =>
        _inFlightActors.Contains(actorId) || _queue.Contains(actorId);

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
        if (_administrativelyDisabled)
        {
            DrainDisabledQueue(_policy.MaximumCompletionsPerTick);
            return;
        }

        var now = CurrentTime();
        if (!ProviderConfigured || !TryGetProviderUri(out var providerUri) || _sessionCancellation == null)
            return;

        var providerApiKey = ProviderApiKey;
        // Bodies dispatch independently; only another outstanding request for the same actor blocks dispatch.
        while (_queue.TryDequeue(_inFlightActors, out var request, now: now.Ticks))
        {
            if (!CanUseGpt(request.ActorId))
            {
                Publish(Fallback(request, NCNeuralResultStatus.Stale, "ordinary-npc-gpt-disabled"));
                continue;
            }
            now = CurrentTime();
            if (request.ExpiresAt <= now)
            {
                Publish(Fallback(request, NCNeuralResultStatus.Expired, "expired"));
                continue;
            }
            if (!IsFreshForDispatch(request))
            {
                Publish(new NCNeuralResult(
                    NCNeuralResultStatus.Stale,
                    request,
                    null,
                    "stale-before-dispatch"));
                continue;
            }

            _inFlight++;
            _inFlightActors.Add(request.ActorId);
            _inFlightRequests[request.ActorId] = request;
            _ = ExecuteAsync(request, _generation, providerApiKey, providerUri, _policy.RequestTimeout, _policy.ProviderTimeout,
                _sessionCancellation.Token);
        }
    }

    public bool Submit(NCNeuralRequestEnvelope request)
    {
        if (request.AccessTier != NCNeuralAccessTier.NarrativeActor || !CanUseGpt(request.ActorId))
            return false;
        if (!_active || _policy == null || !Valid(request, _policy) || !MatchesCurrentPrompt(request, _policy) ||
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

        GetKindRuntimeState(request.Kind).Submitted++;
        if (_administrativelyDisabled)
        {
            _known.Add(request.RequestId);
            Publish(Fallback(request, NCNeuralResultStatus.Fallback, "administratively-disabled"));
            return true;
        }

        _known.Add(request.RequestId);
        if (request.Kind != NCNeuralRequestKind.DialogueContinuation &&
            _newestWakeRevisions.TryGetValue(request.ActorId, out var latestWakeRevision) &&
            request.WakeRevision < latestWakeRevision)
        {
            // A slower context build completed after a newer wake had already reached this queue.
            Publish(new NCNeuralResult(NCNeuralResultStatus.Stale, request, null, "older-async-build"));
            return true;
        }
        if (request.WakeRevision > _newestWakeRevisions.GetValueOrDefault(request.ActorId))
            _newestWakeRevisions[request.ActorId] = request.WakeRevision;
        // A fresh urgent observation must supersede an older answer still being composed for the same body. We do
        // not cancel the HTTP request because usage still has to be accounted, but its result may no longer act.
        if (_inFlightRequests.TryGetValue(request.ActorId, out var inFlight) &&
            ShouldSupersedeInFlight(inFlight, request))
        {
            _supersededRequests.Add(inFlight.RequestId);
        }
        var outcome = _queue.Enqueue(request, out var coalesced);
        if (outcome != NCNeuralPendingEnqueueOutcome.Added && coalesced != null)
        {
            _coalesced++;
            Publish(new NCNeuralResult(
                NCNeuralResultStatus.Stale,
                coalesced,
                null,
                outcome == NCNeuralPendingEnqueueOutcome.RetainedExisting
                    ? "coalesced-by-dominant-wake"
                    : "coalesced-by-newer-wake"));
        }
        return true;
    }

    public NCNeuralRuntimeSnapshot GetRuntimeSnapshot() => new(_active, _administrativelyDisabled,
        ProviderConfigured, _usagePersistenceHealthy, _queue.Count, _inFlight,
        _inputTokens, _outputTokens, _costUsdMicros,
        _weeklyPeriodKey, _weeklyCostUsdMicros,
        _weeklyKindRequests.GetValueOrDefault(NCNeuralRequestKind.SocialCognition),
        _weeklyKindRequests.GetValueOrDefault(NCNeuralRequestKind.StrategicDecision),
        _weeklyKindRequests.GetValueOrDefault(NCNeuralRequestKind.DialogueContinuation),
        _weeklyKindRequests.GetValueOrDefault(NCNeuralRequestKind.MemorySummary),
        _weeklyKindRequests.GetValueOrDefault(NCNeuralRequestKind.NarrativeRender),
        _accepted, _stale, _invalid, _providerFailures, _fallbacks, _coalesced,
        _usagePersistenceFailures,
        _timeouts, _rejects, _failures, _circuitOpenUntil, _lastFailureKind, _lastFailureReason,
        Enum.GetValues<NCNeuralRequestKind>().ToDictionary(
            kind => kind,
            kind => GetKindRuntimeState(kind).Snapshot()));

    public bool TryGetActorRuntimeSnapshot(NCActorId actorId, out NCNeuralActorRuntimeSnapshot snapshot)
    {
        if (!actorId.IsValid || !_actorRuntime.TryGetValue(actorId, out var state))
        {
            snapshot = default!;
            return false;
        }

        snapshot = new NCNeuralActorRuntimeSnapshot(
            state.AccessTier,
            (_inFlightActors.Contains(actorId) ? 1 : 0) + _queue.CountFor(actorId),
            _queue.Contains(actorId),
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
            state.LastProviderModel,
            state.LastPromptStableKey,
            state.LastPromptVersion,
            state.LastEvaluationProfile,
            state.LastSchemaVersion);
        return true;
    }

    /// <summary>Clears only transient provider failure state; usage telemetry and queued gameplay work are untouched.</summary>
    public bool ResetCircuitBreaker()
    {
        if (!_active)
            return false;
        _failures = 0;
        _circuitOpenUntil = null;
        return true;
    }

    /// <summary>
    /// Cancels provider work for the current round without removing physical actors. The normal
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
        TimeSpan providerTimeout,
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
            message.Headers.Add("X-NC-Provider-Timeout-Seconds",
                providerTimeout.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var response = await _http.SendAsync(message, cancellation.Token);
            if (!response.IsSuccessStatusCode)
            {
                failure = ClassifyStatus(response.StatusCode);
                reason = $"http-{(int) response.StatusCode}";
            }
            else
            {
                responseValue = await response.Content.ReadFromJsonAsync<NCNeuralResponseEnvelope>(cancellation.Token);
                // Stop waiting even if the underlying persistence operation cannot itself be cancelled.
                // Its eventual result cannot publish a second transport completion or keep the actor occupied.
                actor = await _actors.GetAsync(request.ActorId, true).WaitAsync(cancellation.Token);
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
        _inFlightActors.Remove(request.ActorId);
        if (_inFlightRequests.TryGetValue(request.ActorId, out var inFlight) &&
            inFlight.RequestId == request.RequestId)
        {
            _inFlightRequests.Remove(request.ActorId);
        }
        var superseded = _supersededRequests.Remove(request.RequestId);
        var decisionRevisionFresh = IsDecisionRevisionFresh(request);
        if (completion.Failure != NCNeuralFailureKind.None)
        {
            if (completion.Failure == NCNeuralFailureKind.Timeout)
                _timeouts++;
            if (completion.Failure == NCNeuralFailureKind.Schema)
                _rejects++;
            RecordFailure(completion.Failure, completion.Reason);
            FailCircuit(completion.Failure);
            // Preserve the original dialogue correlation across transient failures. A delayed retry frees its
            // HTTP slot, and a newer wake in the same lane can still replace it normally.
            var retries = _transportRetries.GetValueOrDefault(request.RequestId);
            if (!superseded && CountsTowardCircuit(completion.Failure) &&
                retries < _policy!.MaximumTransportRetries && request.ExpiresAt > CurrentTime())
            {
                _transportRetries[request.RequestId] = retries + 1;
                var due = CurrentTime().Advance(_policy.TransportRetryDelay * (retries + 1));
                var outcome = _queue.Enqueue(request, out var displaced, due.Ticks);
                if (outcome != NCNeuralPendingEnqueueOutcome.Added && displaced != null)
                    Publish(new NCNeuralResult(NCNeuralResultStatus.Stale, displaced, null, "retry-superseded"));
                return;
            }
            Publish(superseded
                ? new NCNeuralResult(
                    NCNeuralResultStatus.Stale,
                    request,
                    null,
                    "superseded-by-newer-wake",
                    completion.Failure)
                : Fallback(
                    request,
                    completion.Failure == NCNeuralFailureKind.Timeout
                        ? NCNeuralResultStatus.Timeout
                        : NCNeuralResultStatus.Fallback,
                    completion.Reason,
                    completion.Failure));
            return;
        }

        var now = CurrentTime();
        var latestActor = completion.Actor;
        if (_actors.TryGetCached(request.ActorId, out var cachedActor) &&
            (latestActor == null || cachedActor.AggregateVersion >= latestActor.AggregateVersion))
        {
            latestActor = cachedActor;
        }
        var result = superseded
            ? new NCNeuralResult(
                NCNeuralResultStatus.Stale,
                request,
                null,
                "superseded-by-newer-wake")
            : ValidateSafely(request, completion.Response, latestActor, decisionRevisionFresh, now);
        if (result.Status == NCNeuralResultStatus.Invalid)
        {
            _rejects++;
            RecordFailure(NCNeuralFailureKind.Schema, result.Reason);
            FailCircuit(NCNeuralFailureKind.Schema);
        }

        // Usage persistence is telemetry only. A database outage must not hold or discard a valid gameplay result.
        if (completion.Response is { } response && CanAccountUsage(request, response, _policy!))
        {
            AccountUsage(request, response);
            Publish(result);
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
                persisted != null,
                persisted == null ? "usage-ledger-rejected" : string.Empty));
        }
        catch (Exception exception)
        {
            _usageCompletions.Enqueue(new UsageCompletion(
                generation,
                false,
                exception.GetType().Name));
        }
    }

    private void CompleteUsage(UsageCompletion completion)
    {
        if (completion.Generation != _generation || !_active)
            return;

        if (!completion.Persisted)
        {
            _usagePersistenceHealthy = false;
            _usagePersistenceFailures++;
            RecordFailure(NCNeuralFailureKind.Persistence, completion.Reason);
            return;
        }
        _usagePersistenceHealthy = true;
    }

    private void DrainCompletions(int maximum)
    {
        for (var processed = 0; processed < maximum; processed++)
        {
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
        for (var processed = 0;
             processed < maximum && _queue.TryDequeue(_unblockedActors, out var request);
             processed++)
        {
            Publish(Fallback(request, NCNeuralResultStatus.Fallback, "administratively-disabled"));
        }
    }

    private void AccountUsage(NCNeuralRequestEnvelope request, NCNeuralResponseEnvelope response)
    {
        RefreshUsagePeriod();
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
        _weeklyKindRequests[request.Kind] = checked(_weeklyKindRequests.GetValueOrDefault(request.Kind) + 1);

        var kindState = GetKindRuntimeState(request.Kind);
        kindState.InputTokens = checked(kindState.InputTokens + response.InputTokens);
        kindState.OutputTokens = checked(kindState.OutputTokens + response.OutputTokens);
        kindState.CostUsdMicros = checked(kindState.CostUsdMicros + totalCost);

        var state = GetActorRuntimeState(request.ActorId);
        _actorRuntime[request.ActorId] = state with
        {
            AccessTier = request.AccessTier,
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
        response.ProviderModel == policy.ProviderModel && Stable(response.ProviderRequestId) &&
        Stable(response.PromptStableKey) && response.PromptVersion > 0 && Stable(response.EvaluationProfile);

    internal static bool ShouldSupersedeInFlight(
        NCNeuralRequestEnvelope inFlight,
        NCNeuralRequestEnvelope incoming) =>
        inFlight.ActorId == incoming.ActorId && incoming.WakeRevision > inFlight.WakeRevision &&
        (inFlight.Kind != NCNeuralRequestKind.DialogueContinuation ||
         incoming.Kind == NCNeuralRequestKind.DialogueContinuation) &&
        incoming.Priority <= inFlight.Priority;

    internal long GetActorDecisionRevision(NCActorId actorId) =>
        _actorDecisionRevisions.GetValueOrDefault(actorId);

    private bool IsDecisionRevisionFresh(NCNeuralRequestEnvelope request) =>
        IsDecisionRevisionFresh(request, GetActorDecisionRevision(request.ActorId));

    internal static bool IsDecisionRevisionFresh(NCNeuralRequestEnvelope request, long currentRevision) =>
        request.DecisionRevision == currentRevision;

    private bool IsFreshForDispatch(NCNeuralRequestEnvelope request) =>
        IsDecisionRevisionFresh(request) && _actors.TryGetCached(request.ActorId, out var actor) &&
        IsActorVersionFresh(request, null, actor.AggregateVersion);

    private void RecordFailure(NCNeuralFailureKind kind, string reason)
    {
        _lastFailureKind = kind;
        _lastFailureReason = reason.Length <= 96 ? reason : reason[..96];
    }

    internal static NCNeuralFailureKind ClassifyStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => NCNeuralFailureKind.Authentication,
        HttpStatusCode.TooManyRequests => NCNeuralFailureKind.RateLimited,
        HttpStatusCode.GatewayTimeout => NCNeuralFailureKind.Timeout,
        HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable =>
            NCNeuralFailureKind.ServiceUnavailable,
        HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest or HttpStatusCode.Conflict =>
            NCNeuralFailureKind.ProviderRejected,
        _ => NCNeuralFailureKind.Transport,
    };

    private NCNeuralResult Validate(
        NCNeuralRequestEnvelope request,
        NCNeuralResponseEnvelope? response,
        NCCityActorRecord? actor,
        bool decisionRevisionFresh,
        NCActiveWorldTime now)
    {
        if (request.ExpiresAt <= now)
            return new(NCNeuralResultStatus.Expired, request, null, "expiry");
        if (actor == null || request.Kind != NCNeuralRequestKind.DialogueContinuation &&
            (!decisionRevisionFresh || actor.AggregateVersion != request.ContextVersion))
            return new(NCNeuralResultStatus.Stale, request, null, "context-version");
        if (response == null || response.RequestId != request.RequestId ||
            response.SchemaVersion != NCNeuralContract.SchemaVersion ||
            response.ContextVersion != request.ContextVersion || response.Confidence is < 0 or > 1000 ||
            response.InputTokens < 0 || response.CachedInputTokens < 0 ||
            response.CachedInputTokens > response.InputTokens || response.OutputTokens < 0 ||
            response.InputTokens > _policy!.MaximumRequestInputTokens ||
            response.OutputTokens > _policy.MaximumRequestOutputTokens ||
            response.ProviderModel != _policy.ProviderModel || !Stable(response.ProviderRequestId) ||
            !PromptIdentityMatches(request, response) ||
            response.SpeechActKey == null || response.SpeechActKey.Length > 96 || !Stable(response.SummaryKey) ||
            response.RenderedText == null || response.RenderedText.Length > _policy.MaximumTextLength ||
            request.Kind == NCNeuralRequestKind.DialogueContinuation &&
            response.ShouldSpeak && string.IsNullOrWhiteSpace(response.RenderedText) ||
            !ValidDisclosureResponse(request, response) || !ValidResponseShape(request, response))
            return new(NCNeuralResultStatus.Invalid, request, null, "schema-or-capability",
                NCNeuralFailureKind.Schema);

        // Physical proposals are one ordered transaction. A malformed member rejects the complete physical batch;
        // compacting it could change dependencies while making command-result indices dishonest.
        var commands = SelectValidCommands(request, response.Commands ?? []);

        // An act response must retain at least one command after the server filters untrusted proposals. Treating
        // a fully rejected batch as accepted would leave the physical actor idle without a command-result wake.
        if (response.DecisionMode == "act" && commands.Length == 0)
        {
            return new NCNeuralResult(
                NCNeuralResultStatus.Invalid,
                request,
                null,
                "all-commands-rejected",
                NCNeuralFailureKind.Schema);
        }

        // A conversation can remain meaningful after an unrelated aggregate mutation, but its old commands cannot.
        // Preserve only a structurally valid spoken reply and remove stale intent/physical work atomically.
        if (request.Kind == NCNeuralRequestKind.DialogueContinuation && commands.Length > 0 &&
            (!decisionRevisionFresh || actor.AggregateVersion != request.ContextVersion))
        {
            if (!TryKeepStaleDialogueSpeech(request, response, out var speechOnly))
                return new(NCNeuralResultStatus.Stale, request, null, "context-version");
            return new(NCNeuralResultStatus.Accepted, request, speechOnly, "stale-commands-removed");
        }

        return new(NCNeuralResultStatus.Accepted, request, response with { Commands = commands }, string.Empty);
    }

    internal static bool IsActorVersionFresh(
        NCNeuralRequestEnvelope request,
        NCNeuralResponseEnvelope? response,
        long latestActorVersion) =>
        (request.Kind == NCNeuralRequestKind.DialogueContinuation && (response?.Commands?.Count ?? 0) == 0) ||
        latestActorVersion == request.ContextVersion;

    internal static bool TryKeepStaleDialogueSpeech(
        NCNeuralRequestEnvelope request,
        NCNeuralResponseEnvelope response,
        out NCNeuralResponseEnvelope speechOnly)
    {
        speechOnly = response with
        {
            Commands = [],
            DecisionMode = "speak",
            Intent = null,
        };
        return request.Kind == NCNeuralRequestKind.DialogueContinuation && response.ShouldSpeak &&
               response.SpeechActs.Count > 0 && ValidResponseShape(request, speechOnly);
    }

    /// <summary>Responses must identify the exact version-controlled prompt configuration that produced them.</summary>
    internal static bool PromptIdentityMatches(
        NCNeuralRequestEnvelope request,
        NCNeuralResponseEnvelope response) =>
        response.PromptStableKey == request.PromptStableKey &&
        response.PromptVersion == request.PromptVersion &&
        response.EvaluationProfile == request.EvaluationProfile;

    internal static NCNeuralCommandProposal[] SelectValidCommands(
        NCNeuralRequestEnvelope request,
        IReadOnlyList<NCNeuralCommandProposal> commands)
    {
        if (commands.Count > 8 || commands.Any(command => !ValidCommandShape(request, command)))
            return [];

        return commands
            .Select((command, index) => command with { OriginalIndex = index })
            .ToArray();
    }

    /// <summary>Contains every untrusted-response dereference behind null-safe structural checks.</summary>
    private NCNeuralResult ValidateSafely(
        NCNeuralRequestEnvelope request,
        NCNeuralResponseEnvelope? response,
        NCCityActorRecord? actor,
        bool decisionRevisionFresh,
        NCActiveWorldTime now)
    {
        try
        {
            return Validate(request, response, actor, decisionRevisionFresh, now);
        }
        catch (Exception exception)
        {
            // Provider JSON is an untrusted boundary. A malformed nested value must reject one response, never
            // terminate the server simulation. Payload data is deliberately omitted from this diagnostic.
            Log.Warning($"Neural response validator rejected an unsafe payload shape: {exception.GetType().Name}");
            return new NCNeuralResult(
                NCNeuralResultStatus.Invalid,
                request,
                null,
                "validator-exception",
                NCNeuralFailureKind.Schema);
        }
    }

    internal static bool ValidResponseShape(
        NCNeuralRequestEnvelope request,
        NCNeuralResponseEnvelope response)
    {
        if (response.Commands == null ||
            response.DecisionMode is not ("act" or "speak" or "sense" or "wait") ||
            response.SenseRequests == null || response.SenseRequests.Count > 2 ||
            response.SpeechActs == null || response.SpeechActs.Count > 2 ||
            response.MemorySuggestions == null || response.MemorySuggestions.Count > 16 ||
            response.ReasonTags == null || response.ReasonTags.Count > 16 ||
            response.ReasonTags.Any(value => !Stable(value)) ||
            response.Intent is { } intent &&
            (!Stable(intent.Key) || intent.Priority is < 0 or > 1000 || intent.TargetHandle == null ||
             intent.TargetHandle.Length > 0 && !KnownTarget(request, intent.TargetHandle)) ||
            response.SpeechActs.Any(value => value == null || !ValidSpeechAct(value.ActKey) ||
                string.IsNullOrWhiteSpace(value.Text) || value.Text.Length > 512 || value.Text.Any(char.IsControl)) ||
            response.MemorySuggestions.Any(value => value == null || !Stable(value.Key) ||
                value.Importance is < 0 or > 1000 || value.SubjectHandle == null ||
                value.SubjectHandle.Length > 0 && !KnownTarget(request, value.SubjectHandle)) ||
            response.SenseRequests.Any(value => !ValidSenseRequest(request, value)) ||
            response.Wake is { } wake && (wake.On == null || wake.AfterSeconds is < 0 or > 86400 || wake.On.Count > 1 ||
                wake.On.Any(value => value != "command-result")))
        {
            return false;
        }

        if (response.SpeechActs.Count > 0 &&
            (!response.ShouldSpeak || response.SpeechActs[0].ActKey != response.SpeechActKey ||
             response.SpeechActs[0].Text != response.RenderedText))
        {
            return false;
        }

        return response.DecisionMode switch
        {
            "act" => response.Commands.Count is >= 1 and <= 8 && response.SenseRequests.Count == 0,
            "speak" => response.ShouldSpeak && response.SpeechActs.Count > 0 &&
                       response.Commands.Count == 0 && response.SenseRequests.Count == 0,
            "sense" => request.SenseDepth < 2 && response.Commands.Count == 0 &&
                       response.SenseRequests.Count is >= 1 and <= 2 && !response.ShouldSpeak,
            "wait" => !response.ShouldSpeak && response.SpeechActs.Count == 0 &&
                      response.Commands.Count == 0 && response.SenseRequests.Count == 0,
            _ => false,
        };
    }

    private static bool ValidSenseRequest(NCNeuralRequestEnvelope request, NCNeuralSenseRequest? value)
    {
        if (value == null || value.Kind is not (
                "look-around" or "inspect-subject" or "inspect-object" or "check-inventory" or "check-body" or
                "query-nearby-affordances" or "query-known-locations" or "recall-memories" or "examine-route") ||
            value.TargetHandle == null)
        {
            return false;
        }

        return value.Kind switch
        {
            // Active inspection may only refine a currently perceived physical subject. A remembered zone is a
            // navigation target, not something the actor can inspect through the neural boundary.
            "inspect-subject" or "inspect-object" =>
                (request.PerceivedSubjects ?? []).Any(subject => subject.Handle == value.TargetHandle),
            "recall-memories" => KnownTarget(request, value.TargetHandle) ||
                request.Facts.Any(fact => fact.Subject == value.TargetHandle),
            "examine-route" =>
                (request.KnownLocations ?? []).Any(location => location.Handle == value.TargetHandle),
            _ => value.TargetHandle.Length == 0,
        };
    }

    private static bool KnownTarget(NCNeuralRequestEnvelope request, string handle) =>
        (request.PerceivedSubjects ?? []).Any(value => value.Handle == handle) ||
        (request.KnownLocations ?? []).Any(value => value.Handle == handle);

    private static bool ValidSpeechAct(string value) => value is
        "greeting" or "statement" or "question" or "answer" or "offer" or
        "acceptance" or "refusal" or "threat" or "farewell";

    private NCNeuralResult Fallback(
        NCNeuralRequestEnvelope request,
        NCNeuralResultStatus status,
        string reason,
        NCNeuralFailureKind failure = NCNeuralFailureKind.None)
    {
        _fallbacks++;
        return new(status, request, new NCNeuralResponseEnvelope(request.RequestId, NCNeuralContract.SchemaVersion,
            request.ContextVersion,
            [], "safe-default", string.Empty, false, "safe-default", 0, 0, 0, 0, string.Empty,
            _policy?.ProviderModel ?? string.Empty, request.PromptStableKey, request.PromptVersion,
            request.EvaluationProfile), reason, failure);
    }

    private void FailCircuit(NCNeuralFailureKind failure)
    {
        if (!CountsTowardCircuit(failure))
            return;

        // Failures remain observable, but never stop dispatch for unrelated actors.
        // Each failed strategic request is rescheduled by the brain's bounded retry cadence.
        _failures = _failures == int.MaxValue ? int.MaxValue : _failures + 1;
    }

    internal static bool CountsTowardCircuit(NCNeuralFailureKind failure) => failure is
        NCNeuralFailureKind.Timeout or
        NCNeuralFailureKind.ServiceUnavailable or
        NCNeuralFailureKind.RateLimited or
        NCNeuralFailureKind.Transport;

    private void Publish(NCNeuralResult result)
    {
        _transportRetries.Remove(result.Request.RequestId);
        PublishSingle(result);
    }

    private void PublishSingle(NCNeuralResult result)
    {
        if (result.Status == NCNeuralResultStatus.Stale)
            _stale++;
        else if (result.Status == NCNeuralResultStatus.Invalid)
            _invalid++;
        if (IsProviderFailure(result.FailureKind))
            _providerFailures++;

        var kindState = GetKindRuntimeState(result.Request.Kind);
        switch (result.Status)
        {
            case NCNeuralResultStatus.Accepted:
                kindState.Accepted++;
                break;
            case NCNeuralResultStatus.Fallback:
            case NCNeuralResultStatus.Timeout:
            case NCNeuralResultStatus.CircuitOpen:
                kindState.Fallback++;
                break;
            default:
                kindState.Rejected++;
                break;
        }

        var state = GetActorRuntimeState(result.Request.ActorId);
        var response = result.Response;
        _actorRuntime[result.Request.ActorId] = state with
        {
            AccessTier = result.Request.AccessTier,
            Results = checked(state.Results + 1),
            Accepted = checked(state.Accepted + (result.Status == NCNeuralResultStatus.Accepted ? 1 : 0)),
            LastRequestId = result.Request.RequestId,
            LastRequestKind = result.Request.Kind,
            LastStatus = result.Status,
            LastFailureKind = result.FailureKind,
            LastReason = BoundDiagnostic(result.Reason),
            LastCapabilityId = response?.Commands.FirstOrDefault()?.CapabilityId ?? string.Empty,
            LastSummaryKey = response?.SummaryKey ?? string.Empty,
            LastProviderModel = response?.ProviderModel ?? string.Empty,
            LastPromptStableKey = response?.PromptStableKey ?? result.Request.PromptStableKey,
            LastPromptVersion = response?.PromptVersion ?? result.Request.PromptVersion,
            LastEvaluationProfile = response?.EvaluationProfile ?? result.Request.EvaluationProfile,
            LastSchemaVersion = response?.SchemaVersion ?? NCNeuralContract.SchemaVersion,
            UpdatedSequence = ++_actorRuntimeSequence,
        };
        _known.Remove(result.Request.RequestId);
        if (result.Status == NCNeuralResultStatus.Accepted)
        {
            _accepted++;
            _failures = 0;
            _lastFailureKind = NCNeuralFailureKind.None;
            _lastFailureReason = string.Empty;
            _actorDecisionRevisions[result.Request.ActorId] = checked(
                GetActorDecisionRevision(result.Request.ActorId) + 1);
        }
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

    private void RefreshUsagePeriod()
    {
        var periodKey = NCNeuralUsageLedgerSystem.CurrentWeekKey(DateTimeOffset.UtcNow);
        if (_weeklyPeriodKey == periodKey)
            return;

        _weeklyPeriodKey = periodKey;
        _weeklyCostUsdMicros = 0;
        _weeklyKindRequests.Clear();
    }

    private NCActiveWorldTime CurrentTime() => _clock.TryGetCurrentTime(out _, out var now) ? now : default;
    private bool ProviderConfigured =>
        _policy != null && !_administrativelyDisabled &&
        (_policy?.Enabled == true || _configuration.GetCVar(NCCVars.NeuralEnabled)) &&
        ProviderApiKey.Length > 0 && TryGetProviderUri(out _);
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
            NCNeuralContract.SchemaVersion,
            request.RequestId,
            request.Kind.ToString(),
            request.Priority.ToString(),
            new NCNeuralProviderGuid(request.ActorId.Value),
            new NCNeuralProviderGuid(request.SessionEpoch.Value),
            request.ContextVersion,
            new NCNeuralProviderTime(request.ExpiresAt.Ticks),
            request.PromptProfile.Id,
            request.PromptStableKey,
            request.PromptVersion,
            request.EvaluationProfile,
            request.Facts,
            request.AllowedCapabilityIds,
            request.TopicKeys,
            request.Locale,
            request.Persona,
            request.Autonomy,
            request.PerceivedSubjects ?? [],
            request.PerceivedEvents ?? [],
            request.PerceptionDeltas,
            request.KnownLocations ?? [],
            request.DialogueInput,
            request.AllowedDirectiveActionIds ?? [],
            request.Disclosure!,
            request.Body,
            request.CurrentIntent,
            request.RecentCommandResults,
            request.WorldOpportunities,
            request.AvailableActions,
            request.SenseResults,
            request.SenseDepth,
            request.MaximumOutputTokens);
    }

    private KindRuntimeState GetKindRuntimeState(NCNeuralRequestKind kind)
    {
        if (_kindRuntime.TryGetValue(kind, out var state))
            return state;

        state = new KindRuntimeState();
        _kindRuntime.Add(kind, state);
        return state;
    }
    private void OnStarted(NCActiveWorldSessionStartedEvent args)
    {
        Reset();
        if (_prototypes.TryIndex(DefaultPolicy, out NCNeuralPolicyPrototype? policy) && Valid(policy))
        {
            _policy = policy;
            _epoch = args.Epoch;
            _sessionCancellation = new CancellationTokenSource();
            _usagePersistenceHealthy = true;
            RefreshUsagePeriod();
            _active = true;
        }
    }
    private void Deactivate()
    {
        _active = false;
        _generation++;
        _sessionCancellation?.Cancel();
        _queue.Clear();
        _transportRetries.Clear();
        _inFlightActors.Clear();
        _inFlightRequests.Clear();
        _supersededRequests.Clear();
        _inFlight = 0;
    }
    private void Reset()
    {
        Deactivate();
        _sessionCancellation?.Dispose();
        _sessionCancellation = null;
        _known.Clear(); _newestWakeRevisions.Clear(); _actorDecisionRevisions.Clear();
        _actorRuntime.Clear(); _actorRuntimeSequence = 0; _inputTokens = 0;
        _outputTokens = 0; _costUsdMicros = 0; _weeklyCostUsdMicros = 0; _weeklyPeriodKey = string.Empty;
        _weeklyKindRequests.Clear();
        _kindRuntime.Clear();
        _accepted = 0; _stale = 0; _invalid = 0; _providerFailures = 0; _fallbacks = 0;
        _coalesced = 0; _usagePersistenceFailures = 0;
        _timeouts = 0; _rejects = 0;
        _failures = 0; _circuitOpenUntil = null;
        _lastFailureKind = NCNeuralFailureKind.None; _lastFailureReason = string.Empty;
        _administrativelyDisabled = false;
        _usagePersistenceHealthy = true;
        _policy = null;
    }
    private static bool Valid(NCNeuralPolicyPrototype value) =>
        value.MaximumCompletionsPerTick is > 0 and <= 256 &&
        value.MaximumTransportRetries is >= 0 and <= 8 && value.TransportRetryDelay > TimeSpan.Zero &&
        value.MaximumFacts is > 0 and <= 256 &&
        value.MaximumCapabilities is > 0 and <= 128 && value.MaximumPerceivedSubjects is > 0 and <= 128 &&
        value.MaximumKnownLocations is > 0 and <= 512 &&
        value.MaximumTextLength is > 0 and <= 4096 &&
        value.MaximumDialogueInputLength is > 0 and <= 4096 &&
        value.MaximumDialogueHistoryTurns is > 0 and <= 64 &&
        value.MaximumDialogueSummaryKeys is > 0 and <= 64 &&
        value.MaximumDialogueMemorySessions is > 0 and <= 32 &&
        value.MaximumDialogueMemoryTextLength is > 0 and <= 2048 && value.RequestTimeout > TimeSpan.Zero &&
        Stable(value.ProviderModel) &&
        value.MaximumRequestInputTokens is > 0 and <= 262144 &&
        value.MaximumRequestOutputTokens is > 0 and <= 8192 &&
        value.OrdinaryDialogueMaximumOutputTokens is > 0 &&
        value.OrdinaryDialogueMaximumOutputTokens <= value.MaximumRequestOutputTokens &&
        value.OrdinaryDialogueMaximumMemorySessions is > 0 &&
        value.OrdinaryDialogueMaximumMemorySessions <= value.MaximumDialogueMemorySessions &&
        value.RequestLifetime >= value.RequestTimeout &&
        value.ProviderTimeout > TimeSpan.Zero && value.ProviderTimeout < value.RequestTimeout &&
        value.MaximumPendingCorrelations is > 0 and <= 16384 && value.SuggestionLifetime > TimeSpan.Zero &&
        value.MaximumTrackedActorDiagnostics is > 0 and <= 4096 &&
        float.IsFinite(value.DefensiveReflexRetreatDistance) &&
        value.DefensiveReflexRetreatDistance is > 0f and <= 16f &&
        Uri.TryCreate(value.Endpoint, UriKind.Absolute, out _);
    private static bool Valid(NCNeuralRequestEnvelope value, NCNeuralPolicyPrototype policy) =>
        value.RequestId != Guid.Empty && value.ActorId.IsValid && value.SessionEpoch.IsValid &&
        value.ContextVersion > 0 && value.WakeRevision > 0 && value.DecisionRevision >= 0 && value.ExpiresAt.IsValid &&
        Stable(value.PromptStableKey) && value.PromptVersion > 0 && Stable(value.EvaluationProfile) &&
        value.Facts.Count <= policy.MaximumFacts &&
        value.AllowedCapabilityIds.Count is > 0 && value.AllowedCapabilityIds.Count <= policy.MaximumCapabilities &&
        value.MaximumOutputTokens is > 0 && value.MaximumOutputTokens <= policy.MaximumRequestOutputTokens &&
        value.AllowedCapabilityIds.All(Stable) && value.TopicKeys.All(Stable) &&
        value.Facts.Select(fact => fact.Handle).Distinct(StringComparer.Ordinal).Count() == value.Facts.Count &&
        value.Facts.All(fact => Stable(fact.Handle) && Stable(fact.Domain) && Stable(fact.Key) &&
            Stable(fact.Subject) && float.IsFinite(fact.Confidence) &&
            fact.Confidence is >= 0 and <= 1 && Stable(fact.Privacy)) &&
        (value.PerceivedSubjects?.Count ?? 0) <= policy.MaximumPerceivedSubjects &&
        (value.PerceivedSubjects ?? []).Select(subject => subject.Handle).Distinct(StringComparer.Ordinal).Count() ==
            (value.PerceivedSubjects?.Count ?? 0) &&
        (value.PerceivedSubjects ?? []).All(subject => Stable(subject.Handle) && Stable(subject.Kind) &&
            subject.DisplayName.Length is > 0 and <= 128 &&
            subject.DisplayName.All(character => !char.IsControl(character)) &&
            float.IsFinite(subject.Confidence) && subject.Confidence is >= 0 and <= 1 && subject.ExpiresAtTicks > 0 &&
            (!subject.IdentityKnown || IsPersonSubjectKind(subject.Kind))) &&
        (value.PerceivedEvents?.Count ?? 0) <= policy.MaximumPerceivedSubjects &&
        (value.PerceivedEvents ?? []).All(perceivedEvent => Stable(perceivedEvent.Kind) &&
            Stable(perceivedEvent.ActorHandle) && Stable(perceivedEvent.TargetHandle) &&
            float.IsFinite(perceivedEvent.Confidence) && perceivedEvent.Confidence is >= 0 and <= 1 &&
            perceivedEvent.ObservedAtTicks > 0 && perceivedEvent.ExpiresAtTicks > perceivedEvent.ObservedAtTicks) &&
        value.PerceptionDeltas.Count <= policy.MaximumPerceivedSubjects &&
        value.PerceptionDeltas.All(delta => ValidPerceptionDelta(value, delta)) &&
        (value.KnownLocations?.Count ?? 0) <= policy.MaximumKnownLocations &&
        (value.KnownLocations ?? []).All(location => Stable(location.Handle) && Stable(location.Kind) &&
            location.DisplayName.Length is > 0 and <= 128 &&
            location.DisplayName.All(character => !char.IsControl(character))) &&
        (value.AllowedDirectiveActionIds?.Count ?? 0) <= 64 &&
        (value.AllowedDirectiveActionIds ?? []).All(Stable) &&
        value.SenseDepth is >= 0 and <= 2 &&
        (value.SenseDepth == 0 ? value.SenseResults.Count == 0 : value.SenseResults.Count is >= 1 and <= 2) &&
        value.SenseResults.All(result => ValidSenseResult(value, result)) &&
        (value.Body == null || Stable(value.Body.LifeState) && Stable(value.Body.HungerState) &&
            Stable(value.Body.ThirstState) && Stable(value.Body.MobilityState) && Stable(value.Body.PainState)) &&
        (value.CurrentIntent == null || Stable(value.CurrentIntent.CapabilityId) &&
            value.CurrentIntent.CurrentStep >= 0 &&
            value.CurrentIntent.StepCount >= value.CurrentIntent.CurrentStep &&
            value.CurrentIntent.ExpiresAtTicks > 0) &&
        value.RecentCommandResults.Count <= 8 && value.RecentCommandResults.All(ValidCommandResult) &&
        value.WorldOpportunities.Count <= policy.MaximumPerceivedSubjects &&
        value.WorldOpportunities.Select(opportunity => opportunity?.OpportunityId ?? string.Empty)
            .Distinct(StringComparer.Ordinal).Count() == value.WorldOpportunities.Count &&
        value.WorldOpportunities.All(opportunity => ValidWorldOpportunity(value, opportunity)) &&
        value.AvailableActions.Count == value.AllowedCapabilityIds.Count &&
        value.AvailableActions.Select(action => action.CapabilityId).Distinct(StringComparer.Ordinal).Count() ==
            value.AvailableActions.Count &&
        value.AvailableActions.Select(action => action.CapabilityId).ToHashSet(StringComparer.Ordinal)
            .SetEquals(value.AllowedCapabilityIds) &&
        value.AvailableActions.All(action => action != null && Stable(action.CapabilityId) &&
            Stable(action.DomainKey) && Stable(action.TaskKindKey) &&
            action.GoalKeys.Count is > 0 and <= 16 && action.GoalKeys.All(Stable) &&
            action.ActionIds.Count is > 0 and <= 32 && action.ActionIds.All(Stable) &&
            action.OperationKeys.Count is > 0 and <= 32 && action.OperationKeys.All(Stable) &&
            action.TargetKinds.Count is > 0 and <= 8 && action.TargetKinds.All(Stable)) &&
        value.Autonomy is { } autonomy && Stable(autonomy.PolicyKey) &&
        autonomy.MinimumIntentConfidence is >= 0 and <= 1000 &&
        autonomy.MaximumIntentPriority is >= 0 and <= 1000 &&
        ValidDisclosure(value) &&
        ValidDialogue(value, policy);

    private static bool ValidWorldOpportunity(
        NCNeuralRequestEnvelope request,
        NCNeuralWorldOpportunity? opportunity)
    {
        if (opportunity == null || opportunity.AffordanceKeys == null ||
            opportunity.RequiredCapabilityIds == null ||
            !Guid.TryParseExact(opportunity.OpportunityId, "D", out var opportunityId) ||
            opportunityId == Guid.Empty || !Guid.TryParseExact(opportunity.CorrelationId, "D", out var correlation) ||
            correlation == Guid.Empty || !Stable(opportunity.Kind) || !Stable(opportunity.SourceKey) ||
            opportunity.TargetKind is not ("subject" or "location") || !Stable(opportunity.TargetHandle) ||
            opportunity.AffordanceKeys.Count is < 1 or > 32 ||
            opportunity.AffordanceKeys.Distinct(StringComparer.Ordinal).Count() != opportunity.AffordanceKeys.Count ||
            opportunity.AffordanceKeys.Any(value => !Stable(value)) ||
            opportunity.RequiredCapabilityIds.Count > 32 ||
            opportunity.RequiredCapabilityIds.Distinct(StringComparer.Ordinal).Count() !=
                opportunity.RequiredCapabilityIds.Count ||
            opportunity.RequiredCapabilityIds.Any(value => !request.AllowedCapabilityIds.Contains(value) ||
                !Stable(value)) ||
            opportunity.Reward is < 0 or > 1000 || opportunity.Urgency is < 0 or > 1000 ||
            opportunity.Risk is < 0 or > 1000 || opportunity.CreatedAtTicks < 0 ||
            opportunity.ExpiresAtTicks <= opportunity.CreatedAtTicks || !opportunity.Available ||
            opportunity.UnavailableReason.Length != 0)
        {
            return false;
        }

        return opportunity.TargetKind == "subject"
            ? (request.PerceivedSubjects ?? []).Any(value => value.Handle == opportunity.TargetHandle)
            : (request.KnownLocations ?? []).Any(value => value.Handle == opportunity.TargetHandle);
    }

    internal static bool ValidPerceptionDelta(
        NCNeuralRequestEnvelope request,
        NCNeuralPerceptionDelta? delta)
    {
        if (delta == null || delta.Kind is not (
                "person-entered" or "person-left" or "item-appeared" or "item-disappeared" or
                "subject-state-changed" or "held-item-added" or "held-item-removed") ||
            !Stable(delta.SubjectHandle) || !StableOrEmpty(delta.RelatedHandle) ||
            !StableOrEmpty(delta.PreviousKind) || !StableOrEmpty(delta.CurrentKind) ||
            delta.ObservedAtTicks < 0 || delta.ExpiresAtTicks <= delta.ObservedAtTicks)
        {
            return false;
        }

        var perceived = (request.PerceivedSubjects ?? [])
            .Select(value => value.Handle)
            .ToHashSet(StringComparer.Ordinal);
        return delta.SubjectTargetable == perceived.Contains(delta.SubjectHandle) &&
               delta.RelatedTargetable == (delta.RelatedHandle.Length > 0 && perceived.Contains(delta.RelatedHandle));
    }

    internal static bool ValidSenseResult(NCNeuralRequestEnvelope request, NCNeuralSenseResult result)
    {
        if (result == null || result.Kind is not (
                "look-around" or "inspect-subject" or "inspect-object" or "check-inventory" or "check-body" or
                "query-nearby-affordances" or "query-known-locations" or "recall-memories" or "examine-route") ||
            result.Status is not ("completed" or "not-visible" or "wrong-target-kind" or "unavailable") ||
            !StableOrEmpty(result.TargetHandle) || result.ObservedHandles == null || result.FactHandles == null ||
            result.LocationHandles == null || result.OpportunityTargetHandles == null || result.DetailKeys == null ||
            result.ObservedHandles.Count > 32 || result.FactHandles.Count > 32 || result.LocationHandles.Count > 32 ||
            result.OpportunityTargetHandles.Count > 32 || result.DetailKeys.Count > 16 ||
            result.ObservedHandles.Distinct(StringComparer.Ordinal).Count() != result.ObservedHandles.Count ||
            result.FactHandles.Distinct(StringComparer.Ordinal).Count() != result.FactHandles.Count ||
            result.LocationHandles.Distinct(StringComparer.Ordinal).Count() != result.LocationHandles.Count ||
            result.OpportunityTargetHandles.Distinct(StringComparer.Ordinal).Count() !=
                result.OpportunityTargetHandles.Count ||
            result.ObservedHandles.Any(handle => !Stable(handle) ||
                !(request.PerceivedSubjects ?? []).Any(subject => subject.Handle == handle)) ||
            result.FactHandles.Any(handle => !Stable(handle) ||
                !request.Facts.Any(fact => fact.Handle == handle)) ||
            result.LocationHandles.Any(handle => !Stable(handle) ||
                !(request.KnownLocations ?? []).Any(location => location.Handle == handle)) ||
            result.OpportunityTargetHandles.Any(handle => !Stable(handle) ||
                !request.WorldOpportunities.Any(opportunity => opportunity.TargetHandle == handle)) ||
            result.DetailKeys.Any(value => !Stable(value)))
        {
            return false;
        }

        return result.Kind switch
        {
            "look-around" => result.Status == "completed" && result.TargetHandle.Length == 0 &&
                result.Body == null && result.FactHandles.Count == 0 && result.LocationHandles.Count == 0 &&
                result.DetailKeys.Count == 0,
            "check-inventory" => result.Status == "completed" && result.TargetHandle.Length == 0 &&
                result.Body == null && EmptySenseMetadata(result),
            "inspect-subject" or "inspect-object" => result.TargetHandle.Length > 0 && result.Body == null &&
                result.FactHandles.Count == 0 && result.LocationHandles.Count == 0 &&
                result.OpportunityTargetHandles.Count == 0 &&
                result.Status is "completed" or "not-visible" or "wrong-target-kind" &&
                (result.Status == "not-visible" ||
                 (request.PerceivedSubjects ?? []).Any(subject => subject.Handle == result.TargetHandle)),
            "check-body" => result.TargetHandle.Length == 0 && result.ObservedHandles.Count == 0 &&
                EmptySenseMetadata(result) &&
                (result.Status == "completed" ? result.Body != null && result.Body == request.Body :
                    result.Status == "unavailable" && result.Body == null),
            "query-nearby-affordances" => result.Status == "completed" && result.TargetHandle.Length == 0 &&
                result.Body == null && result.ObservedHandles.Count == 0 && result.FactHandles.Count == 0 &&
                result.LocationHandles.Count == 0 && result.DetailKeys.Count == 0,
            "query-known-locations" => result.Status == "completed" && result.TargetHandle.Length == 0 &&
                result.Body == null && result.ObservedHandles.Count == 0 && result.FactHandles.Count == 0 &&
                result.OpportunityTargetHandles.Count == 0 && result.DetailKeys.Count == 0,
            "recall-memories" => result.Status == "completed" && result.TargetHandle.Length > 0 &&
                result.Body == null && result.ObservedHandles.Count == 0 && result.LocationHandles.Count == 0 &&
                result.OpportunityTargetHandles.Count == 0 && result.DetailKeys.Count == 0,
            "examine-route" => result.TargetHandle.Length > 0 && result.Body == null &&
                result.ObservedHandles.Count == 0 && result.FactHandles.Count == 0 &&
                result.OpportunityTargetHandles.Count == 0 && result.DetailKeys.Count == 1 &&
                (result.Status == "completed" && result.LocationHandles.Count == 1 &&
                     result.LocationHandles[0] == result.TargetHandle && result.DetailKeys[0] == "route.resolvable" ||
                 result.Status == "unavailable" && result.LocationHandles.Count is 0 or 1 &&
                     (result.LocationHandles.Count == 0 || result.LocationHandles[0] == result.TargetHandle) &&
                     result.DetailKeys[0] == "route.unavailable"),
            _ => false,
        };
    }

    private static bool EmptySenseMetadata(NCNeuralSenseResult result) =>
        result.FactHandles.Count == 0 && result.LocationHandles.Count == 0 &&
        result.OpportunityTargetHandles.Count == 0 && result.DetailKeys.Count == 0;

    internal static bool ValidCommandResult(NCNeuralCommandResult? result) =>
        result != null && result.RequestId != Guid.Empty && Stable(result.CapabilityId) &&
        Stable(result.ActionId) &&
        result.State is "accepted" or "completed" or "failed" or "interrupted" or "skipped" &&
        Stable(result.FailureKind) && StableOrEmpty(result.FailureReason) && result.UpdatedAtTicks > 0 &&
        result.CommandIndex is >= 0 and < 8 && StableOrEmpty(result.GoalKey) &&
        StableOrEmpty(result.ExpectedOutcomeKey);

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

    private static bool ValidCommandShape(NCNeuralRequestEnvelope request, NCNeuralCommandProposal? command)
    {
        var autonomy = request.Autonomy;
        var action = request.AvailableActions.FirstOrDefault(value => value.CapabilityId == command?.CapabilityId);
        if (command == null || autonomy == null || action == null || !action.GoalKeys.Contains(command.GoalKey) ||
            !Stable(command.CapabilityId) || !Stable(command.GoalKey) ||
            !Stable(command.ExpectedOutcomeKey) ||
            command.Priority < 0 || command.Priority > autonomy.MaximumIntentPriority ||
            command.InformationGapKeys == null || command.InformationGapKeys.Count > 16 ||
            command.InformationGapKeys.Any(value => !Stable(value)) ||
            command.Arguments == null || command.Arguments.Count > 16 || command.Arguments.Any(value =>
                value == null || !Stable(value.Key) || value.Value == null ||
                value.Value.Length > 256 || value.Value.Any(char.IsControl)))
            return false;
        if (command.TargetHandle == string.Empty)
            return true;
        // Capability authorization and ephemeral target freshness are gameplay-policy checks. The suggestion layer
        // validates them again against the current world and rejects the physical batch atomically.
        return Stable(command.TargetHandle);
    }
    private static bool ValidDialogue(NCNeuralRequestEnvelope value, NCNeuralPolicyPrototype policy)
    {
        if (value.Kind != NCNeuralRequestKind.DialogueContinuation)
            return value.DialogueInput == null;
        return value.DialogueInput is { } dialogue &&
               dialogue.ActorDisplayName.Length is > 0 and <= 128 &&
               dialogue.AddressScope is "direct" or "group" && dialogue.AudienceSize is > 0 and <= 256 &&
               dialogue.Utterance.Length is > 0 && dialogue.Utterance.Length <= policy.MaximumDialogueInputLength &&
               dialogue.History.Count <= policy.MaximumDialogueHistoryTurns &&
               dialogue.History.All(turn =>
                   turn.Speaker is "self" or "interlocutor" && Stable(turn.SpeechAct) && turn.AtTicks >= 0 &&
                   turn.Utterance.Length is > 0 && turn.Utterance.Length <= policy.MaximumDialogueInputLength &&
                   turn.Utterance.All(character => !char.IsControl(character))) &&
               (dialogue.EarlierSummary == null ||
                   dialogue.EarlierSummary.EarlierTurnCount >= 0 &&
                   dialogue.EarlierSummary.TopicKeys.Count <= policy.MaximumDialogueSummaryKeys &&
                   dialogue.EarlierSummary.SemanticSummaryKeys.Count <= policy.MaximumDialogueSummaryKeys &&
                   dialogue.EarlierSummary.TopicKeys.All(Stable) &&
                   dialogue.EarlierSummary.SemanticSummaryKeys.All(Stable) &&
                   dialogue.EarlierSummary.PersistentMemories.Count <=
                       (value.AccessTier == NCNeuralAccessTier.NarrativeActor
                           ? policy.MaximumDialogueMemorySessions
                           : policy.OrdinaryDialogueMaximumMemorySessions) &&
                   dialogue.EarlierSummary.PersistentMemories.All(memory =>
                       memory.LastActivityAtTicks >= 0 &&
                       memory.TopicKeys.Count <= policy.MaximumDialogueSummaryKeys &&
                       memory.SemanticSummaryKeys.Count <= policy.MaximumDialogueSummaryKeys &&
                       memory.TopicKeys.All(Stable) && memory.SemanticSummaryKeys.All(Stable) &&
                       memory.Summary.Length is > 0 &&
                       memory.Summary.Length <= policy.MaximumDialogueMemoryTextLength &&
                       memory.Summary.All(character => !char.IsControl(character)))) &&
               (dialogue.SpeakerHandle.Length == 0 || Stable(dialogue.SpeakerHandle) &&
                   (value.PerceivedSubjects ?? []).Any(subject => subject.Handle == dialogue.SpeakerHandle)) &&
               dialogue.ActorDisplayName.All(character => !char.IsControl(character)) &&
               dialogue.Utterance.All(character => !char.IsControl(character));
    }
    private static bool Stable(string? value) => value is { Length: > 0 and <= 128 } && value.All(character =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    private static bool StableOrEmpty(string? value) => string.IsNullOrEmpty(value) || Stable(value);

    private static bool IsPersonSubjectKind(string kind) => kind is
        "self" or "neutral" or "friendly" or "hostile" or "critical" or "dead" or "suspect";

    private bool MatchesCurrentPrompt(NCNeuralRequestEnvelope request, NCNeuralPolicyPrototype policy) =>
        _prototypes.TryIndex(request.PromptProfile, out NCNeuralPromptProfilePrototype? prompt) &&
        prompt.StableKey == request.PromptStableKey && prompt.Version == request.PromptVersion &&
        prompt.EvaluationProfile == request.EvaluationProfile && prompt.Model == policy.ProviderModel &&
        prompt.AllowedKinds.Contains(request.Kind);

    private sealed record TransportCompletion(
        int Generation,
        NCNeuralRequestEnvelope Request,
        NCNeuralResponseEnvelope? Response,
        NCCityActorRecord? Actor,
        NCNeuralFailureKind Failure,
        string Reason);

    private sealed record UsageCompletion(
        int Generation,
        bool Persisted,
        string Reason);

    private sealed class KindRuntimeState
    {
        public long Submitted;
        public long Accepted;
        public long Fallback;
        public long Rejected;
        public long InputTokens;
        public long OutputTokens;
        public long CostUsdMicros;

        public NCNeuralKindRuntimeSnapshot Snapshot() => new(
            Submitted,
            Accepted,
            Fallback,
            Rejected,
            InputTokens,
            OutputTokens,
            CostUsdMicros);
    }

    private sealed record ActorRuntimeState(
        NCNeuralAccessTier AccessTier = NCNeuralAccessTier.OrdinaryResident,
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
        string LastPromptStableKey = "",
        int LastPromptVersion = 0,
        string LastEvaluationProfile = "",
        int LastSchemaVersion = 0,
        long UpdatedSequence = 0);
}

public sealed class NCNeuralResultEvent(NCNeuralResult result) : EntityEventArgs
{ public NCNeuralResult Result { get; } = result; }

/// <summary>
/// Primitive-only HTTP contract. It prevents RobustToolbox and persistence wrapper serialization details from
/// becoming part of the external Neural Cognition Service protocol.
/// </summary>
internal sealed record NCNeuralProviderRequest(
    int SchemaVersion,
    Guid RequestId,
    string Kind,
    string Priority,
    NCNeuralProviderGuid ActorId,
    NCNeuralProviderGuid SessionEpoch,
    long ContextVersion,
    NCNeuralProviderTime ExpiresAt,
    string PromptProfile,
    string PromptStableKey,
    int PromptVersion,
    string EvaluationProfile,
    IReadOnlyList<NCNeuralFact> Facts,
    IReadOnlyList<string> AllowedCapabilityIds,
    IReadOnlyList<string> TopicKeys,
    string Locale,
    NCNeuralPersonaContext? Persona,
    NCNeuralAutonomyContext? Autonomy,
    IReadOnlyList<NCNeuralPerceivedSubject> PerceivedSubjects,
    IReadOnlyList<NCNeuralPerceivedEvent> PerceivedEvents,
    IReadOnlyList<NCNeuralPerceptionDelta> PerceptionDeltas,
    IReadOnlyList<NCNeuralKnownLocation> KnownLocations,
    NCNeuralDialogueInput? DialogueInput,
    IReadOnlyList<string> AllowedDirectiveActionIds,
    NCNeuralDisclosureContext Disclosure,
    NCNeuralBodyContext? Body,
    NCNeuralCurrentIntent? CurrentIntent,
    IReadOnlyList<NCNeuralCommandResult> RecentCommandResults,
    IReadOnlyList<NCNeuralWorldOpportunity> WorldOpportunities,
    IReadOnlyList<NCNeuralAvailableAction> AvailableActions,
    IReadOnlyList<NCNeuralSenseResult> SenseResults,
    int SenseDepth,
    int MaximumOutputTokens);

internal sealed record NCNeuralProviderGuid(Guid Value);
internal sealed record NCNeuralProviderTime(long Ticks);
