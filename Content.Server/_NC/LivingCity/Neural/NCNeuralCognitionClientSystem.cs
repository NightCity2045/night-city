// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

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
using Robust.Shared.Asynchronous;
using Robust.Shared.Configuration;
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
    [Dependency] private IConfigurationManager _configuration = default!;
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
        if (!_active || _policy == null) return;
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
            _ = ExecuteAsync(request, _generation, ServiceToken, providerUri, _policy.RequestTimeout,
                cancellation.Token);
        }
    }

    public bool Submit(NCNeuralRequestEnvelope request)
    {
        if (!_active || _policy == null || !Valid(request, _policy) || _known.Contains(request.RequestId))
            return false;
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
        _known.Add(request.RequestId);
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

    private async Task ExecuteAsync(
        NCNeuralRequestEnvelope request,
        int generation,
        string serviceToken,
        Uri providerUri,
        TimeSpan timeout,
        CancellationToken sessionToken)
    {
        NCNeuralResponseEnvelope? responseValue = null;
        NCCityActorRecord? actor = null;
        Exception? error = null;
        var timedOut = false;
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
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
            using var response = await _http.SendAsync(message, cancellation.Token);
            response.EnsureSuccessStatusCode();
            responseValue = await response.Content.ReadFromJsonAsync<NCNeuralResponseEnvelope>(cancellation.Token);
            actor = await _actors.GetAsync(request.ActorId, true);
        }
        catch (OperationCanceledException exception)
        {
            error = exception;
            timedOut = !sessionToken.IsCancellationRequested;
        }
        catch (Exception exception)
        {
            error = exception;
        }
        _tasks.RunOnMainThread(() => CompleteExecution(
            generation, request, responseValue, actor, error, timedOut));
    }

    private void CompleteExecution(
        int generation,
        NCNeuralRequestEnvelope request,
        NCNeuralResponseEnvelope? response,
        NCCityActorRecord? actor,
        Exception? error,
        bool timedOut)
    {
        if (generation != _generation || !_active || request.SessionEpoch != _epoch)
            return;
        _inFlight = Math.Max(0, _inFlight - 1);
        if (timedOut)
        {
            _timeouts++;
            FailCircuit();
            Publish(Fallback(request, NCNeuralResultStatus.Timeout, "timeout"));
            return;
        }
        if (error != null)
        {
            FailCircuit();
            Publish(Fallback(request, NCNeuralResultStatus.Fallback, error.GetType().Name));
            return;
        }

        var result = Validate(request, response, actor, CurrentTime());
        if (result.Status == NCNeuralResultStatus.Accepted)
        {
            _inputTokens = checked(_inputTokens + response!.InputTokens);
            _outputTokens = checked(_outputTokens + response.OutputTokens);
            _failures = 0;
            _accepted++;
            _cache[(request.ActorId, request.Kind)] = response;
        }
        else if (result.Status == NCNeuralResultStatus.Invalid)
        {
            _rejects++;
            FailCircuit();
        }
        Publish(result);
    }

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
        if (response == null || response.RequestId != request.RequestId || response.SchemaVersion != 2 ||
            response.ContextVersion != request.ContextVersion || response.Confidence is < 0 or > 1000 ||
            response.InputTokens < 0 || response.OutputTokens < 0 ||
            !request.AllowedCapabilityIds.Contains(response.SelectedCapabilityId) ||
            response.Intent != null &&
            !response.Intent.RequestedCapabilityIds.Contains(response.SelectedCapabilityId) ||
            response.SpeechActKey.Length > 96 || response.SummaryKey.Length > 128 ||
            response.RenderedText.Length > _policy!.MaximumTextLength ||
            request.Kind != NCNeuralRequestKind.DialogueContinuation &&
            response.Confidence < (request.Autonomy?.MinimumIntentConfidence ?? int.MaxValue) ||
            request.Kind == NCNeuralRequestKind.DialogueContinuation &&
            string.IsNullOrWhiteSpace(response.RenderedText) || !ValidIntent(request, response.Intent))
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
        return new(status, request, new NCNeuralResponseEnvelope(request.RequestId, 2, request.ContextVersion,
            capability, "safe-default", string.Empty, "safe-default", 0, 0, 0, string.Empty), reason);
    }

    private void FailCircuit()
    {
        if (++_failures >= _policy!.CircuitFailureThreshold)
            _circuitOpenUntil = CurrentTime().Advance(_policy.CircuitOpenDuration);
    }
    private void Publish(NCNeuralResult result) => RaiseLocalEvent(new NCNeuralResultEvent(result));
    private NCNeuralBudgetMode Mode()
    {
        if (_policy == null || !ProviderEnabled || _inputTokens + _outputTokens >= _policy.SessionTokenHardCap)
            return NCNeuralBudgetMode.FallbackOnly;
        var used = _inputTokens + _outputTokens;
        return used >= _policy.RestrictedThreshold ? NCNeuralBudgetMode.Restricted :
            used >= _policy.EconomyThreshold ? NCNeuralBudgetMode.Economy : NCNeuralBudgetMode.Normal;
    }
    private NCActiveWorldTime CurrentTime() => _clock.TryGetCurrentTime(out _, out var now) ? now : default;
    private bool ProviderEnabled =>
        (_policy?.Enabled == true || _configuration.GetCVar(NCCVars.NeuralEnabled)) && ServiceToken.Length > 0;
    private string ServiceToken
    {
        get
        {
            var token = _configuration.GetCVar(NCCVars.NeuralServiceToken).Trim();
            return token.Length > 0 ? token : _configuration.GetCVar(NCCVars.NeuralApiKey).Trim();
        }
    }
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
            request.AllowedDirectiveActionIds ?? []);
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
        _known.Clear(); _cache.Clear(); _inputTokens = 0;
        _outputTokens = 0; _failures = 0; _circuitOpenUntil = null;
    }
    private static bool Valid(NCNeuralPolicyPrototype value) => value.MaximumQueued is > 0 and <= 4096 &&
        value.MaximumConcurrent is > 0 and <= 32 && value.MaximumFacts is > 0 and <= 256 &&
        value.MaximumCapabilities is > 0 and <= 128 && value.MaximumPerceivedSubjects is > 0 and <= 128 &&
        value.MaximumKnownLocations is > 0 and <= 512 &&
        value.MaximumTextLength is > 0 and <= 4096 &&
        value.MaximumDialogueInputLength is > 0 and <= 4096 &&
        value.EconomyThreshold > 0 && value.RestrictedThreshold >= value.EconomyThreshold &&
        value.SessionTokenHardCap >= value.RestrictedThreshold && value.RequestTimeout > TimeSpan.Zero &&
        value.RequestLifetime >= value.RequestTimeout && value.MinimumSuggestionConfidence is >= 0 and <= 1000 &&
        float.IsFinite(value.MaximumUtilityBoost) && value.MaximumUtilityBoost is >= 0 and <= 1000 &&
        value.MaximumPendingCorrelations is > 0 and <= 16384 && value.SuggestionLifetime > TimeSpan.Zero &&
        value.MaximumAppliedSuggestions is > 0 and <= 100000 && value.StrategicRequestCooldown > TimeSpan.Zero &&
        Uri.TryCreate(value.Endpoint, UriKind.Absolute, out _);
    private static bool Valid(NCNeuralRequestEnvelope value, NCNeuralPolicyPrototype policy) =>
        value.RequestId != Guid.Empty && value.ActorId.IsValid && value.SessionEpoch.IsValid &&
        value.ContextVersion > 0 && value.ExpiresAt.IsValid && value.Facts.Count <= policy.MaximumFacts &&
        value.AllowedCapabilityIds.Count is > 0 && value.AllowedCapabilityIds.Count <= policy.MaximumCapabilities &&
        value.AllowedCapabilityIds.All(Stable) && value.TopicKeys.All(Stable) && value.Facts.All(fact =>
            Stable(fact.Domain) && Stable(fact.Key) && Stable(fact.Subject) && float.IsFinite(fact.Confidence) &&
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
        ValidDialogue(value, policy);

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
               (dialogue.SpeakerHandle.Length == 0 || Stable(dialogue.SpeakerHandle) &&
                   (value.PerceivedSubjects ?? []).Any(subject => subject.Handle == dialogue.SpeakerHandle)) &&
               dialogue.ActorDisplayName.All(character => !char.IsControl(character)) &&
               dialogue.Utterance.All(character => !char.IsControl(character));
    }
    private static bool Stable(string value) => value.Length is > 0 and <= 128 && value.All(character =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');
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
    IReadOnlyList<string> AllowedDirectiveActionIds);

internal sealed record NCNeuralProviderGuid(Guid Value);
internal sealed record NCNeuralProviderTime(long Ticks);
