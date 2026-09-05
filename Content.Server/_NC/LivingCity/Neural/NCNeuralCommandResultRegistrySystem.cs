// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Neural;

/// <summary>
/// Keeps the last verified neural command consequences durable by ActorId. Context reads use only the bounded cache;
/// database work is serialized per actor and never blocks the simulation thread.
/// </summary>
public sealed partial class NCNeuralCommandResultRegistrySystem : EntitySystem
{
    public const string AggregateType = "living-city.neural-command-results";
    public const int SnapshotFormatVersion = 1;
    internal const int MaximumResults = 8;

    private const int MaximumPersistenceAttempts = 2;
    private const int MaximumStableKeyLength = 128;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;

    private readonly ConcurrentDictionary<NCActorId, NCNeuralCommandResultHistoryRecord> _cache = [];
    private readonly ConcurrentDictionary<NCActorId, SemaphoreSlim> _actorGates = [];
    private readonly ConcurrentDictionary<long, Task> _pendingWrites = [];
    private readonly List<(NCActorId ActorId, EntityUid EntityUid)> _bindings = [];

    private NCSessionEpoch _epoch;
    private int _generation;
    private long _writeSequence;
    private volatile bool _active;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnSessionClosing);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Reset());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Reset());
        SubscribeLocalEvent<NCCityActorBindingChangedEvent>(OnBindingChanged);
        SubscribeLocalEvent<NCNeuralCommandResultEvent>(OnCommandResult);
    }

    public bool TryGetCached(NCActorId actorId, out NCNeuralCommandResultHistoryRecord record) =>
        _cache.TryGetValue(actorId, out record!);

    public async Task<NCNeuralCommandResultHistoryRecord?> GetAsync(
        NCActorId actorId,
        bool forceReload = false)
    {
        if (!actorId.IsValid)
            return null;
        if (!forceReload && _cache.TryGetValue(actorId, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(AggregateType, ActorKey(actorId));
        var record = Deserialize(stored, actorId);
        return record == null ? null : CacheNewest(record);
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        Reset();
        _epoch = args.Epoch;
        _active = true;

        // Binding normally happens after session start. This also covers maps that already own bound persistent actors.
        _runtime.GetBindings(_bindings);
        foreach (var (actorId, _) in _bindings)
            QueuePreload(actorId);
    }

    private void OnBindingChanged(NCCityActorBindingChangedEvent args)
    {
        if (_active && args.Bound)
            QueuePreload(args.ActorId);
    }

    private void OnCommandResult(NCNeuralCommandResultEvent args)
    {
        if (!_active || !TryValidateTerminal(args.ActorId, args.Result) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != _epoch ||
            args.Result.UpdatedAtTicks > now.Ticks)
        {
            return;
        }

        var generation = Volatile.Read(ref _generation);
        TrackWrite(PersistObservedAsync(generation, args.ActorId, args.Result, epoch, now));
    }

    private void OnSessionClosing(NCActiveWorldSessionClosingEvent args)
    {
        if (!_active || args.Epoch != _epoch)
            return;

        // The clock remains writable until all captured terminal outcomes have settled.
        foreach (var pending in _pendingWrites.Values.ToArray())
            args.AddPendingWrite(pending);
    }

    private void TrackWrite(Task persistence)
    {
        var sequence = Interlocked.Increment(ref _writeSequence);
        _pendingWrites[sequence] = persistence;
        _ = ObserveTrackedWriteAsync(sequence, persistence);
    }

    private async Task ObserveTrackedWriteAsync(long sequence, Task persistence)
    {
        try
        {
            await persistence;
        }
        finally
        {
            _pendingWrites.TryRemove(sequence, out _);
        }
    }

    private void QueuePreload(NCActorId actorId)
    {
        if (!actorId.IsValid)
            return;
        var generation = Volatile.Read(ref _generation);
        _ = PreloadAsync(generation, actorId);
    }

    private async Task PreloadAsync(int generation, NCActorId actorId)
    {
        var gate = Gate(actorId);
        await gate.WaitAsync();
        try
        {
            if (!IsCurrent(generation))
                return;

            var stored = await _database.GetNCAggregateSnapshotAsync(AggregateType, ActorKey(actorId));
            var record = Deserialize(stored, actorId);
            if (IsCurrent(generation) && record != null)
                CacheNewest(record);
        }
        catch (Exception exception)
        {
            if (IsCurrent(generation))
                Log.Error($"Failed to preload neural command results for actor {actorId}: {exception}");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task PersistObservedAsync(
        int generation,
        NCActorId actorId,
        NCNeuralCommandResult observed,
        NCSessionEpoch epoch,
        NCActiveWorldTime observedAt)
    {
        var gate = Gate(actorId);
        await gate.WaitAsync();
        try
        {
            for (var attempt = 0; attempt < MaximumPersistenceAttempts && IsCurrent(generation); attempt++)
            {
                var stored = await _database.GetNCAggregateSnapshotAsync(AggregateType, ActorKey(actorId));
                var current = Deserialize(stored, actorId);
                if (stored != null && current == null)
                {
                    Log.Error($"Rejected invalid neural command-result snapshot for actor {actorId}.");
                    return;
                }

                if (!TryAppendTerminal(actorId, current?.Snapshot.Results ?? [], observed,
                        out var nextResults, out var changed))
                {
                    Log.Error($"Rejected invalid or conflicting neural command result for actor {actorId}.");
                    return;
                }
                if (!changed)
                {
                    if (current != null && IsCurrent(generation))
                        CacheNewest(current);
                    return;
                }

                var persistedAt = current != null && current.PersistedAt > observedAt
                    ? current.PersistedAt
                    : observedAt;
                var eventId = BuildEventId(observed);
                var snapshot = new NCNeuralCommandResultHistorySnapshot(
                    actorId,
                    nextResults,
                    persistedAt,
                    (current?.Snapshot.DataVersion ?? 0) + 1,
                    eventId);
                var payload = JsonSerializer.Serialize(snapshot, JsonOptions);
                NCPersistentMutationResult result;
                try
                {
                    result = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(
                        BuildPersistenceCommandId(observed, attempt),
                        "living-city.neural-command-result.record",
                        "actor",
                        ActorKey(actorId),
                        eventId,
                        "living-city.neural-command-result.recorded",
                        AggregateType,
                        ActorKey(actorId),
                        current?.AggregateVersion ?? 0,
                        SnapshotFormatVersion,
                        payload,
                        JsonSerializer.Serialize(new NCNeuralCommandResultRecordedEvent(actorId, observed),
                            JsonOptions),
                        null,
                        observed.RequestId,
                        null,
                        epoch,
                        persistedAt,
                        []));
                }
                catch (Exception exception)
                {
                    if (await TryRecoverCommittedAsync(generation, actorId, observed))
                        return;
                    if (attempt + 1 >= MaximumPersistenceAttempts)
                        Log.Error($"Failed to persist neural command result for actor {actorId}: {exception}");
                    continue;
                }

                if (result.Status == NCPersistentMutationStatus.Applied)
                {
                    if (IsCurrent(generation))
                        CacheNewest(new NCNeuralCommandResultHistoryRecord(
                            snapshot,
                            result.AggregateVersion,
                            epoch,
                            persistedAt));
                    return;
                }

                if (result.Status == NCPersistentMutationStatus.DuplicateCompleted &&
                    await TryRecoverCommittedAsync(generation, actorId, observed))
                {
                    return;
                }

                if (result.Status is not (NCPersistentMutationStatus.VersionConflict or
                    NCPersistentMutationStatus.DuplicateFailed))
                {
                    Log.Error($"Neural command-result persistence rejected actor {actorId}: " +
                              $"{result.Error ?? result.Status.ToString()}.");
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            if (IsCurrent(generation))
                Log.Error($"Failed to persist neural command result for actor {actorId}: {exception}");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> TryRecoverCommittedAsync(
        int generation,
        NCActorId actorId,
        NCNeuralCommandResult observed)
    {
        var stored = await _database.GetNCAggregateSnapshotAsync(AggregateType, ActorKey(actorId));
        var recovered = Deserialize(stored, actorId);
        if (recovered == null || !ContainsIdentity(recovered.Snapshot.Results, observed, requireEqual: true))
            return false;
        if (IsCurrent(generation))
            CacheNewest(recovered);
        return true;
    }

    internal static bool TryAppendTerminal(
        NCActorId actorId,
        IReadOnlyList<NCNeuralCommandResult> existing,
        NCNeuralCommandResult result,
        out NCNeuralCommandResult[] merged,
        out bool changed)
    {
        merged = [];
        changed = false;
        if (!TryValidateTerminal(actorId, result) || existing.Count > MaximumResults ||
            existing.Any(value => !TryValidateTerminal(actorId, value)) || HasDuplicateIdentities(existing))
        {
            return false;
        }

        var matching = existing.FirstOrDefault(value => SameIdentity(value, result));
        if (matching != null)
        {
            if (matching != result)
                return false;
            merged = Order(existing).ToArray();
            return true;
        }

        merged = Order(existing.Append(result)).TakeLast(MaximumResults).ToArray();
        changed = true;
        return true;
    }

    internal static bool TryValidateTerminal(NCActorId actorId, NCNeuralCommandResult? result)
    {
        if (!actorId.IsValid || result == null || result.RequestId == Guid.Empty ||
            result.CommandIndex is < 0 or >= MaximumResults || result.UpdatedAtTicks < 0 ||
            !ValidKey(result.CapabilityId) || !ValidKey(result.ActionId) ||
            !ValidOptionalKey(result.GoalKey) || !ValidOptionalKey(result.ExpectedOutcomeKey) ||
            !ValidKey(result.FailureKind) || !ValidOptionalKey(result.FailureReason) ||
            !IsTerminalState(result.State))
        {
            return false;
        }

        return result.State == "completed"
            ? result.FailureKind == "none" && result.FailureReason.Length == 0
            : result.FailureKind != "none" && result.FailureReason.Length > 0;
    }

    internal static Guid BuildPersistenceCommandId(NCNeuralCommandResult result, int attempt = 0) =>
        NCPersistentIdentity.Command(
            "neural-command-result",
            attempt == 0 ? "record" : $"record-retry-{attempt}",
            $"{result.RequestId:D}.{result.CommandIndex}.{result.State}");

    private static Guid BuildEventId(NCNeuralCommandResult result) =>
        NCPersistentIdentity.Command(
            "neural-command-result",
            "event",
            $"{result.RequestId:D}.{result.CommandIndex}.{result.State}");

    private static NCNeuralCommandResultHistoryRecord? Deserialize(
        NCAggregateSnapshotData? stored,
        NCActorId actorId)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion ||
            !stored.SessionEpoch.IsValid || !stored.ActiveWorldTime.IsValid)
        {
            return null;
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<NCNeuralCommandResultHistorySnapshot>(
                stored.Payload,
                JsonOptions);
            if (!ValidSnapshot(snapshot, actorId, stored.ActiveWorldTime))
                return null;
            return new NCNeuralCommandResultHistoryRecord(
                snapshot!,
                stored.AggregateVersion,
                stored.SessionEpoch,
                stored.ActiveWorldTime);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool ValidSnapshot(
        NCNeuralCommandResultHistorySnapshot? snapshot,
        NCActorId actorId,
        NCActiveWorldTime persistedAt)
    {
        if (snapshot == null || snapshot.ActorId != actorId || snapshot.Results == null ||
            snapshot.Results.Length is < 1 or > MaximumResults || snapshot.UpdatedAt != persistedAt ||
            snapshot.DataVersion < 1 || snapshot.LastAppliedEventId == Guid.Empty ||
            snapshot.Results.Any(value => !TryValidateTerminal(actorId, value)) ||
            snapshot.Results.Any(value => value.UpdatedAtTicks > snapshot.UpdatedAt.Ticks) ||
            HasDuplicateIdentities(snapshot.Results))
        {
            return false;
        }

        return snapshot.Results.SequenceEqual(Order(snapshot.Results));
    }

    private static IEnumerable<NCNeuralCommandResult> Order(IEnumerable<NCNeuralCommandResult> results) =>
        results.OrderBy(value => value.UpdatedAtTicks)
            .ThenBy(value => value.RequestId)
            .ThenBy(value => value.CommandIndex)
            .ThenBy(value => value.State, StringComparer.Ordinal);

    private static bool ContainsIdentity(
        IEnumerable<NCNeuralCommandResult> values,
        NCNeuralCommandResult result,
        bool requireEqual) =>
        values.Any(value => SameIdentity(value, result) && (!requireEqual || value == result));

    private static bool HasDuplicateIdentities(IReadOnlyList<NCNeuralCommandResult> values)
    {
        var identities = new HashSet<(Guid RequestId, int CommandIndex, string State)>();
        return values.Any(value => !identities.Add((value.RequestId, value.CommandIndex, value.State)));
    }

    private static bool SameIdentity(NCNeuralCommandResult left, NCNeuralCommandResult right) =>
        left.RequestId == right.RequestId && left.CommandIndex == right.CommandIndex && left.State == right.State;

    private static bool IsTerminalState(string state) =>
        state is "completed" or "failed" or "interrupted" or "skipped";

    private static bool ValidKey(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumStableKeyLength &&
        value.All(IsStableKeyCharacter);

    private static bool ValidOptionalKey(string? value) =>
        value != null && value.Length <= MaximumStableKeyLength && value.All(IsStableKeyCharacter);

    private static bool IsStableKeyCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '-' or '_' or '.' or ':';

    private NCNeuralCommandResultHistoryRecord CacheNewest(NCNeuralCommandResultHistoryRecord candidate) =>
        _cache.AddOrUpdate(candidate.Snapshot.ActorId, candidate,
            (_, current) => current.AggregateVersion > candidate.AggregateVersion ? current : candidate);

    private SemaphoreSlim Gate(NCActorId actorId) =>
        _actorGates.GetOrAdd(actorId, static _ => new SemaphoreSlim(1, 1));

    private bool IsCurrent(int generation) =>
        _active && Volatile.Read(ref _generation) == generation;

    private void Reset()
    {
        _active = false;
        Interlocked.Increment(ref _generation);
        _epoch = default;
        _cache.Clear();
        _pendingWrites.Clear();
    }

    private static string ActorKey(NCActorId actorId) => actorId.Value.ToString("D");
}

public sealed record NCNeuralCommandResultHistorySnapshot(
    NCActorId ActorId,
    NCNeuralCommandResult[] Results,
    NCActiveWorldTime UpdatedAt,
    int DataVersion,
    Guid LastAppliedEventId);

public sealed record NCNeuralCommandResultHistoryRecord(
    NCNeuralCommandResultHistorySnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

internal sealed record NCNeuralCommandResultRecordedEvent(
    NCActorId ActorId,
    NCNeuralCommandResult Result);
