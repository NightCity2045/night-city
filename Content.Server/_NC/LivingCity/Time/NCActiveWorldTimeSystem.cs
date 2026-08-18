// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.CCVar;
using Robust.Shared.Asynchronous;
using Robust.Shared.Configuration;
using Robust.Shared.Timing;

namespace Content.Server._NC.LivingCity.Time;

/// <summary>
/// Owns the authoritative city clock. It advances only while the game ticker is in a live round and
/// persists coalesced checkpoints instead of performing database work from every simulation tick.
/// </summary>
public sealed partial class NCActiveWorldTimeSystem : EntitySystem
{
    private static readonly TimeSpan MinimumCheckpointInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PersistenceRetryDelay = TimeSpan.FromSeconds(5);

    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private ITaskManager _taskManager = default!;

    private readonly LinkedList<ClockOperation> _operations = [];
    private ActiveSessionState? _current;
    private bool _operationInFlight;
    private TimeSpan _checkpointInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// True only after the current round epoch has been durably activated and no clock conflict occurred.
    /// Persistent A-Life mutation systems must fail closed while this is false.
    /// </summary>
    public bool IsReady => _current is { Active: true, Started: true, Failed: false };

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnRunLevelChanged);
        Subs.CVar(
            _configuration,
            CCVars.NCActiveWorldClockCheckpointSeconds,
            ConfigureCheckpointInterval,
            true);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_current is not { Active: true, Started: true, Failed: false } state)
            return;

        SampleActiveTime(state);
        if (!state.CheckpointQueued && state.Pending >= _checkpointInterval)
            QueueCheckpoint(state);
    }

    /// <summary>
    /// Returns the live active-world timestamp without forcing a database checkpoint.
    /// </summary>
    public bool TryGetCurrentTime(out NCSessionEpoch epoch, out NCActiveWorldTime time)
    {
        epoch = default;
        time = default;
        if (_current is not { Active: true, Started: true, Failed: false } state)
            return false;

        var unsampled = _timing.CurTime - state.LastSample;
        if (unsampled < TimeSpan.Zero)
            unsampled = TimeSpan.Zero;

        try
        {
            epoch = state.Epoch;
            time = state.PersistedTime.Advance(state.Pending + state.InFlight + unsampled);
            return true;
        }
        catch (OverflowException exception)
        {
            FailState(state, $"active-world time overflowed: {exception.Message}");
            return false;
        }
    }

    private void OnRunLevelChanged(GameRunLevelChangedEvent args)
    {
        if (args.New == GameRunLevel.InRound && args.Old != GameRunLevel.InRound)
        {
            StartRoundSession();
            return;
        }

        if (args.Old == GameRunLevel.InRound && args.New != GameRunLevel.InRound)
            CloseRoundSession();
    }

    private void StartRoundSession()
    {
        // A forced run-level transition can begin a new round before a prior close callback returns.
        // Queue ordering guarantees that the prior close reaches durable storage before this epoch starts.
        if (_current is { Active: true } previous)
            PrepareRoundClose(previous);

        var state = new ActiveSessionState(NCSessionEpoch.New(), _timing.CurTime);
        _current = state;
        QueueStart(state);
    }

    private void CloseRoundSession()
    {
        if (_current is not { Active: true } state)
            return;

        PrepareRoundClose(state);
    }

    private void PrepareRoundClose(ActiveSessionState state)
    {
        SampleActiveTime(state);
        NCActiveWorldTime finalTime;
        try
        {
            finalTime = state.PersistedTime.Advance(state.Pending + state.InFlight);
        }
        catch (OverflowException exception)
        {
            state.Active = false;
            FailState(state, $"final active-world time overflowed: {exception.Message}");
            return;
        }
        var closing = new NCActiveWorldSessionClosingEvent(state.Epoch, finalTime);

        // Closing handlers run synchronously while the clock is still available, so they can capture immutable
        // world state and start their durable writes. The queue then waits for those writes before closing the epoch.
        RaiseLocalEvent(closing);
        state.Active = false;
        if (closing.PendingWrites.Count != 0)
            QueueCloseBarrier(state, closing.PendingWrites);
        else
            QueueClose(state);
    }

    private void QueueCloseBarrier(ActiveSessionState state, IReadOnlyCollection<Task> pendingWrites)
    {
        var writes = new Task[pendingWrites.Count];
        var index = 0;
        foreach (var write in pendingWrites)
            writes[index++] = write;
        var operation = new ClockOperation(
            $"durability barrier for epoch {state.Epoch}",
            async () =>
            {
                await Task.WhenAll(writes);
                return null;
            },
            _ => QueueClose(state),
            exception =>
            {
                // Closing an epoch after a failed final write would make that lost state impossible to repair.
                // Keep the database epoch active and fail all further persistent mutations closed.
                FailState(state, $"final durability barrier failed: {exception.Message}");
            });
        EnqueueLast(operation);
    }

    private void SampleActiveTime(ActiveSessionState state)
    {
        var now = _timing.CurTime;
        var elapsed = now - state.LastSample;
        state.LastSample = now;
        if (elapsed <= TimeSpan.Zero)
            return;

        try
        {
            state.Pending += elapsed;
        }
        catch (OverflowException exception)
        {
            FailState(state, $"pending active-world time overflowed: {exception.Message}");
        }
    }

    private void ConfigureCheckpointInterval(float seconds)
    {
        if (!float.IsFinite(seconds))
        {
            Log.Warning($"Ignored non-finite active-world checkpoint interval '{seconds}'.");
            return;
        }

        _checkpointInterval = TimeSpan.FromSeconds(Math.Max(
            MinimumCheckpointInterval.TotalSeconds,
            seconds));
    }

    private void QueueStart(ActiveSessionState state)
    {
        if (state.StartQueued || state.Started || state.Failed)
            return;

        state.StartQueued = true;
        ClockOperation? operation = null;
        operation = new ClockOperation(
            $"start epoch {state.Epoch}",
            async () => await _database.StartNCActiveWorldSessionAsync(state.Epoch),
            result =>
            {
                if (result is not { RoundActive: true } || result.SessionEpoch != state.Epoch)
                {
                    state.StartQueued = false;
                    FailState(state, "database returned a different or inactive epoch during round activation");
                    return;
                }

                state.StartQueued = false;
                state.Started = true;
                ApplyPersistedState(state, result);
                RaiseLocalEvent(new NCActiveWorldSessionStartedEvent(
                    result.SessionEpoch,
                    result.Time));

                if (state.Active && state.Pending >= _checkpointInterval)
                    QueueCheckpoint(state);
            },
            exception =>
            {
                if (!state.Active)
                {
                    // The already queued close operation can idempotently activate and close this epoch.
                    state.StartQueued = false;
                    Log.Error($"Failed to activate {state.Epoch} before round close: {exception}");
                    return;
                }

                Log.Error($"Failed to activate active-world epoch {state.Epoch}; retrying: {exception}");
                RetryOperation(operation!);
            });
        EnqueueLast(operation);
    }

    private void QueueCheckpoint(ActiveSessionState state)
    {
        if (!state.Started || state.CheckpointQueued || state.Pending <= TimeSpan.Zero || state.Failed)
            return;

        var attempt = new PersistAttempt(
            state.Epoch,
            state.PersistedTime,
            state.Version,
            state.Pending);
        state.Pending = TimeSpan.Zero;
        state.InFlight = attempt.Elapsed;
        state.CheckpointQueued = true;

        ClockOperation? operation = null;
        operation = new ClockOperation(
            $"checkpoint epoch {state.Epoch} version {attempt.ExpectedVersion}",
            () => _database.AdvanceNCActiveWorldTimeAsync(
                attempt.Epoch,
                attempt.ExpectedTime,
                attempt.ExpectedVersion,
                attempt.Elapsed),
            result =>
            {
                if (result is not { RoundActive: true } || result.SessionEpoch != state.Epoch)
                {
                    state.Pending += attempt.Elapsed;
                    state.InFlight = TimeSpan.Zero;
                    state.CheckpointQueued = false;
                    FailState(state, $"checkpoint conflict at version {attempt.ExpectedVersion}");
                    return;
                }

                state.InFlight = TimeSpan.Zero;
                state.CheckpointQueued = false;
                ApplyPersistedState(state, result);

                if (state.Active)
                {
                    SampleActiveTime(state);
                    if (state.Pending >= _checkpointInterval)
                        QueueCheckpoint(state);
                }
            },
            exception =>
            {
                Log.Error($"Failed active-world checkpoint for {state.Epoch}; retrying idempotently: {exception}");
                RetryOperation(operation!);
            });
        EnqueueLast(operation);
    }

    private void QueueClose(ActiveSessionState state)
    {
        if (state.CloseQueued || state.Closed)
            return;

        state.CloseQueued = true;
        CloseAttempt? attempt = null;
        ClockOperation? operation = null;
        operation = new ClockOperation(
            $"close epoch {state.Epoch}",
            () =>
            {
                if (attempt == null)
                {
                    if (state.InFlight != TimeSpan.Zero)
                        throw new InvalidOperationException("A close operation overtook its queued checkpoint.");

                    var basis = state.Started
                        ? new NCActiveWorldClockData(
                            state.Epoch,
                            state.PersistedTime,
                            true,
                            state.Version)
                        : null;

                    attempt = new CloseAttempt(
                        state.Epoch,
                        state.Pending,
                        basis);
                    state.Pending = TimeSpan.Zero;
                }

                return ExecuteCloseAsync(attempt);
            },
            result =>
            {
                if (result is not { RoundActive: false } || result.SessionEpoch != state.Epoch)
                {
                    state.CloseQueued = false;
                    if (attempt != null)
                        state.Pending += attempt.Elapsed;
                    FailState(state, $"close conflict at version {attempt?.Basis?.Version}");
                    return;
                }

                state.CloseQueued = false;
                state.Closed = true;
                state.Started = true;
                ApplyPersistedState(state, result);
                RaiseLocalEvent(new NCActiveWorldSessionClosedEvent(
                    result.SessionEpoch,
                    result.Time));
            },
            exception =>
            {
                Log.Error($"Failed to close active-world epoch {state.Epoch}; retrying idempotently: {exception}");
                RetryOperation(operation!);
            });
        EnqueueLast(operation);
    }

    private async Task<NCActiveWorldClockData?> ExecuteCloseAsync(CloseAttempt attempt)
    {
        // Only attempt-local data is touched after awaiting the database; ECS state remains on the main thread.
        attempt.Basis ??= await _database.StartNCActiveWorldSessionAsync(attempt.Epoch);
        return await _database.CloseNCActiveWorldSessionAsync(
            attempt.Epoch,
            attempt.Basis.Time,
            attempt.Basis.Version,
            attempt.Elapsed);
    }

    private void ApplyPersistedState(ActiveSessionState state, NCActiveWorldClockData data)
    {
        state.PersistedTime = data.Time;
        state.Version = data.Version;
    }

    private void FailState(ActiveSessionState state, string reason)
    {
        if (state.Failed)
            return;

        state.Failed = true;
        Log.Error($"Active-world clock failed closed for epoch {state.Epoch}: {reason}");
        RaiseLocalEvent(new NCActiveWorldClockFailedEvent(state.Epoch, reason));
    }

    private void EnqueueLast(ClockOperation operation)
    {
        _operations.AddLast(operation);
        StartNextOperation();
    }

    private void EnqueueFirst(ClockOperation operation)
    {
        _operations.AddFirst(operation);
        StartNextOperation();
    }

    private void RetryOperation(ClockOperation operation)
    {
        var delay = new ClockOperation(
            $"retry delay for {operation.Name}",
            async () =>
            {
                await Task.Delay(PersistenceRetryDelay);
                return null;
            },
            _ => EnqueueFirst(operation),
            exception =>
            {
                Log.Error($"Unexpected active-world retry delay failure: {exception}");
                EnqueueFirst(operation);
            });
        EnqueueFirst(delay);
    }

    private void StartNextOperation()
    {
        if (_operationInFlight || _operations.First is not { } first)
            return;

        var operation = first.Value;
        _operations.RemoveFirst();
        _operationInFlight = true;

        Task<NCActiveWorldClockData?> task;
        try
        {
            task = operation.Execute();
        }
        catch (Exception exception)
        {
            CompleteOperation(operation, null, exception);
            return;
        }

        _ = ObserveOperationAsync(operation, task);
    }

    private async Task ObserveOperationAsync(
        ClockOperation operation,
        Task<NCActiveWorldClockData?> task)
    {
        NCActiveWorldClockData? result = null;
        Exception? error = null;
        try
        {
            result = await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            error = exception;
        }

        // Database continuations may run on a worker. All queue and session state returns to the ECS thread.
        _taskManager.RunOnMainThread(() => CompleteOperation(operation, result, error));
    }

    private void CompleteOperation(
        ClockOperation operation,
        NCActiveWorldClockData? result,
        Exception? exception)
    {
        try
        {
            if (exception == null)
                operation.OnSuccess(result);
            else
                operation.OnFailure(exception);
        }
        catch (Exception callbackException)
        {
            Log.Error($"Active-world clock callback '{operation.Name}' failed: {callbackException}");
        }
        finally
        {
            // Keep this true during callbacks so a callback can prepend follow-up work without racing the queue.
            _operationInFlight = false;
            StartNextOperation();
        }
    }

    private sealed class ActiveSessionState(NCSessionEpoch epoch, TimeSpan lastSample)
    {
        public readonly NCSessionEpoch Epoch = epoch;
        public TimeSpan LastSample = lastSample;
        public NCActiveWorldTime PersistedTime = NCActiveWorldTime.Zero;
        public TimeSpan Pending;
        public TimeSpan InFlight;
        public int Version;
        public bool Active = true;
        public bool StartQueued;
        public bool Started;
        public bool CheckpointQueued;
        public bool CloseQueued;
        public bool Closed;
        public bool Failed;
    }

    private sealed record PersistAttempt(
        NCSessionEpoch Epoch,
        NCActiveWorldTime ExpectedTime,
        int ExpectedVersion,
        TimeSpan Elapsed);

    private sealed class CloseAttempt(
        NCSessionEpoch epoch,
        TimeSpan elapsed,
        NCActiveWorldClockData? basis)
    {
        public NCSessionEpoch Epoch { get; } = epoch;
        public TimeSpan Elapsed { get; } = elapsed;
        public NCActiveWorldClockData? Basis { get; set; } = basis;
    }

    private sealed record ClockOperation(
        string Name,
        Func<Task<NCActiveWorldClockData?>> Execute,
        Action<NCActiveWorldClockData?> OnSuccess,
        Action<Exception> OnFailure);
}

/// <summary>
/// Raised after a round epoch is durably active. Domain schedulers may begin mutation work after this event.
/// </summary>
public sealed class NCActiveWorldSessionStartedEvent(
    NCSessionEpoch epoch,
    NCActiveWorldTime time) : EntityEventArgs
{
    public NCSessionEpoch Epoch { get; } = epoch;
    public NCActiveWorldTime Time { get; } = time;
}

/// <summary>
/// Raised while the ending epoch is still writable. Handlers capture their immutable final state immediately and
/// register only the resulting write tasks; the clock will not close the durable epoch until every task completes.
/// </summary>
public sealed class NCActiveWorldSessionClosingEvent(
    NCSessionEpoch epoch,
    NCActiveWorldTime time) : EntityEventArgs
{
    private readonly List<Task> _pendingWrites = [];

    public NCSessionEpoch Epoch { get; } = epoch;
    public NCActiveWorldTime Time { get; } = time;
    public IReadOnlyCollection<Task> PendingWrites => _pendingWrites;

    public void AddPendingWrite(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        _pendingWrites.Add(task);
    }
}

/// <summary>
/// Raised after the final round delta and inactive flag are committed together.
/// </summary>
public sealed class NCActiveWorldSessionClosedEvent(
    NCSessionEpoch epoch,
    NCActiveWorldTime time) : EntityEventArgs
{
    public NCSessionEpoch Epoch { get; } = epoch;
    public NCActiveWorldTime Time { get; } = time;
}

/// <summary>
/// Raised when optimistic clock invariants fail. Persistent A-Life mutation must remain stopped for that epoch.
/// </summary>
public sealed class NCActiveWorldClockFailedEvent(
    NCSessionEpoch epoch,
    string reason) : EntityEventArgs
{
    public NCSessionEpoch Epoch { get; } = epoch;
    public string Reason { get; } = reason;
}
