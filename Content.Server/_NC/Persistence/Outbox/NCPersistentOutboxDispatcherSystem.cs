// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.Persistence.Outbox;
using Robust.Shared.Asynchronous;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Persistence.Outbox;

public delegate Task NCPersistentOutboxHandler(
    NCOutboxMessageData message,
    CancellationToken cancellationToken);

/// <summary>
/// Active-round, bounded at-least-once dispatcher for transactional outbox messages. Handlers must use MessageId as
/// their idempotency key because a crash after an external side effect and before ProcessedAt can cause redelivery.
/// </summary>
public sealed partial class NCPersistentOutboxDispatcherSystem : EntitySystem
{
    private const string DefaultPolicyId = "NCDefaultPersistentOutboxPolicy";
    private const int FallbackMaximumAttempts = 8;
    private const int PersistenceReadLimit = 500;
    private const int MaximumStoredErrorLength = 2048;

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ITaskManager _taskManager = default!;
    [Dependency] private IGameTiming _timing = default!;

    private readonly Dictionary<string, NCPersistentOutboxHandler> _handlers = [];
    private readonly List<Task> _backgroundOperations = [];
    private NCPersistentOutboxPolicyPrototype? _policy;
    private CancellationTokenSource? _sessionCancellation;
    private TimeSpan _nextFetchAt;
    private int _generation;
    private int _deliveriesInFlight;
    private bool _fetchInFlight;
    private bool _active;

    public bool IsActive => _active;
    public bool FetchInFlight => _fetchInFlight;
    public int DeliveriesInFlight => _deliveriesInFlight;
    public int RegisteredHandlerCount => _handlers.Count;
    public int MaximumAttempts
    {
        get
        {
            if (_policy != null)
                return _policy.MaximumAttempts;
            return _prototypes.TryIndex(
                    new ProtoId<NCPersistentOutboxPolicyPrototype>(DefaultPolicyId),
                    out NCPersistentOutboxPolicyPrototype? policy) && policy != null
                ? policy.MaximumAttempts
                : FallbackMaximumAttempts;
        }
    }
    public long DeliveredThisSession { get; private set; }
    public long FailedThisSession { get; private set; }
    public long ExhaustedThisSession { get; private set; }
    public string? LastFetchError { get; private set; }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnSessionClosing);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(OnSessionClosed);
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(OnClockFailed);
    }

    public override void Shutdown()
    {
        StopSession();
        _handlers.Clear();
        base.Shutdown();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_active || _policy == null || _fetchInFlight || _deliveriesInFlight != 0 ||
            _timing.CurTime < _nextFetchAt)
        {
            return;
        }

        BeginFetch(_generation, _policy);
    }

    /// <summary>
    /// Registers one process-local handler before an active round starts. Duplicate message-type ownership is rejected.
    /// </summary>
    public bool RegisterHandler(string messageType, NCPersistentOutboxHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentNullException.ThrowIfNull(handler);
        if (messageType.Length > 96 || _handlers.ContainsKey(messageType))
            return false;
        _handlers.Add(messageType, handler);
        return true;
    }

    public bool UnregisterHandler(string messageType, NCPersistentOutboxHandler handler)
    {
        if (!_handlers.TryGetValue(messageType, out var current) || current != handler)
            return false;
        return _handlers.Remove(messageType);
    }

    /// <summary>
    /// Wakes polling after an operator requeues a message or a producer commits latency-sensitive work.
    /// </summary>
    public void Wake()
    {
        if (_active)
            _nextFetchAt = TimeSpan.Zero;
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        StopSession();
        if (!_prototypes.TryIndex(
                new ProtoId<NCPersistentOutboxPolicyPrototype>(DefaultPolicyId),
                out NCPersistentOutboxPolicyPrototype? policy) ||
            policy == null || !ValidatePolicy(policy))
        {
            Log.Error($"Persistent outbox dispatcher cannot start: policy {DefaultPolicyId} is missing or invalid.");
            return;
        }

        _policy = policy;
        _sessionCancellation = new CancellationTokenSource();
        _active = true;
        _nextFetchAt = _timing.CurTime;
        DeliveredThisSession = 0;
        FailedThisSession = 0;
        ExhaustedThisSession = 0;
        LastFetchError = null;
    }

    private void OnSessionClosed(NCActiveWorldSessionClosedEvent args)
    {
        StopSession();
    }

    private void OnSessionClosing(NCActiveWorldSessionClosingEvent args)
    {
        // Stop producing work before the clock closes. The clock's durability barrier then waits for any fetch or
        // delivery already in progress, preventing it from leaking into the next vanilla round transaction.
        StopSession();
        args.AddPendingWrite(WaitForIdleAsync());
    }

    private void OnClockFailed(NCActiveWorldClockFailedEvent args)
    {
        StopSession();
    }

    private void StopSession()
    {
        _active = false;
        _generation++;
        _fetchInFlight = false;
        _deliveriesInFlight = 0;
        _policy = null;
        _sessionCancellation?.Cancel();
        _sessionCancellation?.Dispose();
        _sessionCancellation = null;
    }

    private void BeginFetch(int generation, NCPersistentOutboxPolicyPrototype policy)
    {
        _fetchInFlight = true;
        var limit = Math.Min(policy.FetchBatchSize, policy.MaximumConcurrentDeliveries);
        Track(ObserveFetch(generation, limit, policy.MaximumAttempts));
    }

    private async Task ObserveFetch(int generation, int limit, int maximumAttempts)
    {
        IReadOnlyList<NCOutboxMessageData>? messages = null;
        Exception? error = null;
        try
        {
            messages = await _database.GetPendingNCOutboxMessagesAsync(DateTime.UtcNow, limit, maximumAttempts);
        }
        catch (Exception exception)
        {
            error = exception;
        }

        _taskManager.RunOnMainThread(() => CompleteFetch(generation, messages, error));
    }

    private void CompleteFetch(
        int generation,
        IReadOnlyList<NCOutboxMessageData>? messages,
        Exception? error)
    {
        if (generation != _generation || !_active || _policy == null)
            return;

        _fetchInFlight = false;
        _nextFetchAt = _timing.CurTime + _policy.PollInterval;
        if (error != null)
        {
            LastFetchError = TruncateError(error);
            FailedThisSession++;
            Log.Error($"Failed to fetch persistent outbox messages: {error}");
            return;
        }

        LastFetchError = null;
        if (messages == null || messages.Count == 0)
            return;

        var token = _sessionCancellation?.Token ?? CancellationToken.None;
        foreach (var message in messages)
        {
            _handlers.TryGetValue(message.MessageType, out var handler);
            _deliveriesInFlight++;
            Track(ObserveDelivery(generation, message, handler, token, _policy));
        }
    }

    /// <summary>
    /// Returns a snapshot barrier for already-started outbox operations. StopSession must be called first when this
    /// is used during a round transition so no new work can enter after the snapshot.
    /// </summary>
    public Task WaitForIdleAsync()
    {
        _backgroundOperations.RemoveAll(task => task.IsCompleted);
        return _backgroundOperations.Count == 0
            ? Task.CompletedTask
            : Task.WhenAll(_backgroundOperations.ToArray());
    }

    private void Track(Task operation)
    {
        _backgroundOperations.RemoveAll(task => task.IsCompleted);
        _backgroundOperations.Add(operation);
    }

    private async Task ObserveDelivery(
        int generation,
        NCOutboxMessageData message,
        NCPersistentOutboxHandler? handler,
        CancellationToken cancellationToken,
        NCPersistentOutboxPolicyPrototype policy)
    {
        var delivered = false;
        var failureRecorded = false;
        var exhausted = false;
        string? error = null;
        try
        {
            using var deliveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deliveryCancellation.CancelAfter(policy.DeliveryTimeout);
            var deliveryToken = deliveryCancellation.Token;
            deliveryToken.ThrowIfCancellationRequested();
            if (handler == null)
                throw new InvalidOperationException($"No outbox handler is registered for '{message.MessageType}'.");

            // WaitAsync releases the bounded dispatcher even if a faulty adapter ignores cooperative cancellation.
            // The handler can still finish later, so MessageId idempotency remains mandatory for every side effect.
            await handler(message, deliveryToken).WaitAsync(deliveryToken);
            deliveryToken.ThrowIfCancellationRequested();
            delivered = await _database.MarkNCOutboxMessageProcessedAsync(message.MessageId, DateTime.UtcNow);
            if (!delivered)
                error = "The outbox message was already processed or removed before acknowledgement.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Round shutdown leaves the message untouched so the next active round can resume delivery.
        }
        catch (Exception exception)
        {
            error = exception is OperationCanceledException
                ? $"Delivery timed out after {policy.DeliveryTimeout}."
                : TruncateError(exception);
            var retryAt = DateTime.UtcNow + CalculateRetryDelay(message.Attempts, policy);
            try
            {
                failureRecorded = await _database.RecordNCOutboxMessageFailureAsync(
                    message.MessageId,
                    error,
                    retryAt);
                exhausted = failureRecorded && message.Attempts + 1 >= policy.MaximumAttempts;
            }
            catch (Exception persistenceException)
            {
                error = TruncateError(persistenceException);
            }
        }

        _taskManager.RunOnMainThread(() => CompleteDelivery(
            generation,
            message,
            delivered,
            failureRecorded,
            exhausted,
            error));
    }

    private void CompleteDelivery(
        int generation,
        NCOutboxMessageData message,
        bool delivered,
        bool failureRecorded,
        bool exhausted,
        string? error)
    {
        if (generation != _generation || !_active)
            return;

        _deliveriesInFlight = Math.Max(0, _deliveriesInFlight - 1);
        if (delivered)
        {
            DeliveredThisSession++;
        }
        else if (failureRecorded)
        {
            FailedThisSession++;
            if (exhausted)
            {
                ExhaustedThisSession++;
                Log.Error(
                    $"Persistent outbox message {message.MessageId} ({message.MessageType}) exhausted delivery attempts: {error}");
            }
            else
            {
                Log.Warning(
                    $"Persistent outbox message {message.MessageId} ({message.MessageType}) will retry: {error}");
            }
        }
        else if (error != null)
        {
            FailedThisSession++;
            Log.Warning($"Persistent outbox message {message.MessageId} acknowledgement was inconclusive: {error}");
        }

        if (_deliveriesInFlight == 0)
            _nextFetchAt = _timing.CurTime;
    }

    private static TimeSpan CalculateRetryDelay(int priorAttempts, NCPersistentOutboxPolicyPrototype policy)
    {
        var exponent = Math.Max(0, priorAttempts);
        var seconds = policy.InitialRetryDelay.TotalSeconds * Math.Pow(policy.RetryMultiplier, exponent);
        if (!double.IsFinite(seconds))
            seconds = policy.MaximumRetryDelay.TotalSeconds;
        return TimeSpan.FromSeconds(Math.Min(seconds, policy.MaximumRetryDelay.TotalSeconds));
    }

    private static string TruncateError(Exception exception)
    {
        var value = $"{exception.GetType().Name}: {exception.Message}";
        return value.Length <= MaximumStoredErrorLength ? value : value[..MaximumStoredErrorLength];
    }

    private static bool ValidatePolicy(NCPersistentOutboxPolicyPrototype policy)
    {
        return policy.FetchBatchSize is >= 1 and <= PersistenceReadLimit &&
               policy.MaximumConcurrentDeliveries is >= 1 and <= PersistenceReadLimit &&
               policy.MaximumAttempts is >= 1 and <= 100 &&
               policy.PollInterval > TimeSpan.Zero &&
               policy.InitialRetryDelay > TimeSpan.Zero &&
               policy.MaximumRetryDelay >= policy.InitialRetryDelay &&
               policy.DeliveryTimeout > TimeSpan.Zero &&
               float.IsFinite(policy.RetryMultiplier) && policy.RetryMultiplier >= 1f;
    }
}
