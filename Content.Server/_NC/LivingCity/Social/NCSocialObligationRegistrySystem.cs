// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Persistence.Outbox;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Social;

public enum NCSocialObligationMutationStatus : byte
{
    Applied,
    Duplicate,
    PartyUnavailable,
    ClockUnavailable,
    VersionConflict,
    Rejected,
    StorageFailure,
}

public sealed record NCSocialObligationRecord(
    NCSocialObligationSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCSocialObligationMutationResult(
    NCSocialObligationMutationStatus Status,
    NCSocialObligationRecord? Obligation,
    string? Error);

/// <summary>
/// Promise/favor/debt authority with participant indexes. Relationship and memory consequences use a transactional
/// outbox, so a process failure cannot permanently commit an obligation without its idempotent social effects.
/// </summary>
public sealed partial class NCSocialObligationRegistrySystem : EntitySystem
{
    public const string AggregateType = "living-city.social-obligation";
    public const string IndexAggregateType = "living-city.social-obligation-index";
    public const string ConsequenceMessageType = "living-city.social-obligation-consequence.v1";
    public const int SnapshotFormatVersion = 1;

    private const string CreateCommandType = "living-city.social-obligation.create";
    private const string ResolveCommandType = "living-city.social-obligation.resolve";
    private const string CommandScopeType = "social-obligation";
    private static readonly ProtoId<NCSocialObligationPolicyPrototype> DefaultPolicy = "Default";
    private const int GateStripeCount = 64;
    private const int MaximumStableKeyLength = 96;
    private const int MaximumAuditSourceLength = 128;
    private const int AbsoluteMaximumObligations = 2_048;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPartyResolverSystem _parties = default!;
    [Dependency] private NCPersistentOutboxDispatcherSystem _outbox = default!;
    [Dependency] private NCRelationshipRegistrySystem _relationships = default!;
    [Dependency] private NCMemoryRegistrySystem _memories = default!;

    private readonly ConcurrentDictionary<NCSocialObligationId, NCSocialObligationRecord> _cache = [];
    private readonly ConcurrentDictionary<NCPartyRef, ObligationIndexRecord> _indexCache = [];
    private readonly SemaphoreSlim[] _gates = CreateGates();
    private readonly NCPersistentOutboxHandler _consequenceHandler;

    public NCSocialObligationRegistrySystem()
    {
        _consequenceHandler = HandleConsequenceAsync;
    }

    public int CachedObligationCount => _cache.Count;
    public int CachedIndexCount => _indexCache.Count;

    public override void Initialize()
    {
        base.Initialize();
        if (!_outbox.RegisterHandler(ConsequenceMessageType, _consequenceHandler))
            throw new InvalidOperationException($"Outbox handler {ConsequenceMessageType} is already registered.");
    }

    public override void Shutdown()
    {
        _outbox.UnregisterHandler(ConsequenceMessageType, _consequenceHandler);
        base.Shutdown();
    }

    public bool Unload(NCSocialObligationId obligationId) => _cache.TryRemove(obligationId, out _);
    public bool UnloadIndex(NCPartyRef owner) => _indexCache.TryRemove(owner, out _);

    public async Task<NCSocialObligationRecord?> GetAsync(
        NCSocialObligationId obligationId,
        bool forceReload = false)
    {
        if (!obligationId.IsValid)
            return null;
        if (!forceReload && _cache.TryGetValue(obligationId, out var cached))
            return cached;
        var stored = await _database.GetNCAggregateSnapshotAsync(AggregateType, ObligationKey(obligationId));
        var record = DeserializeObligation(stored, obligationId);
        return record == null ? null : CacheNewest(record);
    }

    public async Task<IReadOnlyList<NCSocialObligationRecord>> GetForPartyAsync(
        NCPartyRef owner,
        int limit,
        bool includeClosed = false,
        bool forceReload = false)
    {
        if (!TryGetPolicy(out var policy) || !owner.IsValid || limit is < 1 || limit > policy.MaximumQueryCount)
            return [];
        var index = await GetIndexAsync(owner, forceReload);
        if (index == null)
            return [];
        var result = new List<NCSocialObligationRecord>(limit);
        foreach (var entry in index.Snapshot.Obligations)
        {
            if (!includeClosed && entry.Status != NCSocialObligationStatus.Active)
                continue;
            var obligation = await GetAsync(entry.ObligationId, forceReload);
            if (obligation == null ||
                obligation.Snapshot.Debtor != owner && obligation.Snapshot.Creditor != owner)
            {
                throw new InvalidOperationException(
                    $"Obligation index for {owner} references invalid aggregate {entry.ObligationId}.");
            }
            result.Add(obligation);
            if (result.Count == limit)
                break;
        }
        return result;
    }

    public async Task<NCSocialObligationMutationResult> CreateAsync(NCCreateSocialObligationRequest request)
    {
        if (!TryValidateCreate(request, out var policy, out var error) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(_clock.IsReady ? NCSocialObligationMutationStatus.Rejected :
                NCSocialObligationMutationStatus.ClockUnavailable,
                error ?? "The active-world clock is not ready.");
        }
        var scopeId = ObligationKey(request.ObligationId);
        InitialCommandState commandState;
        try
        {
            commandState = await ValidateInitialCommandAsync(
                request.CommandId,
                CreateCommandType,
                scopeId);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to inspect social obligation command {request.CommandId}: {exception}");
            return Reject(NCSocialObligationMutationStatus.StorageFailure, exception.Message);
        }
        if (commandState == InitialCommandState.Collision)
            return Reject(NCSocialObligationMutationStatus.Rejected, "The command ID belongs to another operation.");
        if (commandState == InitialCommandState.InProgress)
            return Reject(NCSocialObligationMutationStatus.StorageFailure,
                "The social obligation command is still in progress.");
        if (!await ResolveParties(request.Debtor, request.Creditor, request.Initiator))
            return Reject(NCSocialObligationMutationStatus.PartyUnavailable,
                "Debtor, creditor, or initiator is unavailable.");

        var gates = await AcquireGates(
            request.ObligationId.Value,
            NCPersistentIdentity.SocialObligationIndex(request.Debtor),
            NCPersistentIdentity.SocialObligationIndex(request.Creditor));
        try
        {
            var current = await GetAsync(request.ObligationId, true);
            if (current != null)
            {
                return SameCreate(current.Snapshot, request)
                    ? new NCSocialObligationMutationResult(
                        NCSocialObligationMutationStatus.Duplicate, current, null)
                    : Reject(NCSocialObligationMutationStatus.Rejected,
                        "The obligation identity is bound to different immutable terms.");
            }
            if (commandState == InitialCommandState.Completed)
            {
                return Reject(NCSocialObligationMutationStatus.StorageFailure,
                    "The completed creation command is missing its obligation aggregate.");
            }

            var debtorIndex = await GetIndexAsync(request.Debtor, true);
            var creditorIndex = await GetIndexAsync(request.Creditor, true);
            var eventId = Guid.NewGuid();
            var snapshot = new NCSocialObligationSnapshot(
                request.ObligationId,
                request.Kind,
                request.Debtor,
                request.Creditor,
                Normalize(request.Terms),
                request.Privacy,
                NCSocialObligationStatus.Active,
                request.WorldEventId,
                null,
                now,
                request.DueAt,
                null,
                1,
                eventId);
            if (!TryUpdateIndex(debtorIndex?.Snapshot, request.Debtor, snapshot, true, now, policy, out var nextDebtor,
                    out error) ||
                !TryUpdateIndex(creditorIndex?.Snapshot, request.Creditor, snapshot, false, now, policy,
                    out var nextCreditor, out error))
            {
                return Reject(NCSocialObligationMutationStatus.Rejected, error!);
            }

            var debtorEvent = Guid.NewGuid();
            nextDebtor = nextDebtor with { LastAppliedEventId = debtorEvent };
            var creditorEvent = Guid.NewGuid();
            nextCreditor = nextCreditor with { LastAppliedEventId = creditorEvent };
            var payload = new ConsequencePayload(snapshot, request.Initiator, request.CorrelationId, request.CausationId);
            var consequenceId = NCPersistentIdentity.SocialObligationConsequence(request.ObligationId, 1);
            var result = await _database.ApplyNCPersistentBatchMutationAsync(new NCPersistentBatchMutationRequest(
                commandState == InitialCommandState.Available ? request.CommandId : Guid.NewGuid(),
                CreateCommandType,
                CommandScopeType,
                scopeId,
                [
                    ObligationWrite(null, snapshot, eventId, "living-city.social-obligation.created",
                        request.AuditSource),
                    IndexWrite(debtorIndex, nextDebtor, debtorEvent),
                    IndexWrite(creditorIndex, nextCreditor, creditorEvent),
                ],
                JsonSerializer.Serialize(new ObligationReceipt(request.ObligationId, 1), JsonOptions),
                request.CorrelationId,
                request.CausationId,
                epoch,
                now,
                [new NCPersistentBatchOutboxWrite(
                    consequenceId,
                    eventId,
                    ConsequenceMessageType,
                    JsonSerializer.Serialize(payload, JsonOptions))]));
            return await CompleteMutation(result, request.ObligationId, appliedVersion: 1);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to create social obligation {request.ObligationId}: {exception}");
            return Reject(NCSocialObligationMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            ReleaseGates(gates);
        }
    }

    public async Task<NCSocialObligationMutationResult> ResolveAsync(NCResolveSocialObligationRequest request)
    {
        if (!TryValidateResolve(request, out var error) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(_clock.IsReady ? NCSocialObligationMutationStatus.Rejected :
                NCSocialObligationMutationStatus.ClockUnavailable,
                error ?? "The active-world clock is not ready.");
        }
        var scopeId = ObligationKey(request.ObligationId);
        InitialCommandState commandState;
        try
        {
            commandState = await ValidateInitialCommandAsync(
                request.CommandId,
                ResolveCommandType,
                scopeId);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to inspect social obligation command {request.CommandId}: {exception}");
            return Reject(NCSocialObligationMutationStatus.StorageFailure, exception.Message);
        }
        if (commandState == InitialCommandState.Collision)
            return Reject(NCSocialObligationMutationStatus.Rejected, "The command ID belongs to another operation.");
        if (commandState == InitialCommandState.InProgress)
            return Reject(NCSocialObligationMutationStatus.StorageFailure,
                "The social obligation command is still in progress.");
        var initial = await GetAsync(request.ObligationId, true);
        if (initial == null)
            return Reject(NCSocialObligationMutationStatus.Rejected, "The social obligation does not exist.");
        if (!await ResolveParties(initial.Snapshot.Debtor, initial.Snapshot.Creditor, request.Initiator))
            return Reject(NCSocialObligationMutationStatus.PartyUnavailable,
                "Debtor, creditor, or initiator is unavailable.");
        if (!TryGetPolicy(out var policy))
            return Reject(NCSocialObligationMutationStatus.Rejected, "The obligation policy is invalid.");

        var gates = await AcquireGates(
            request.ObligationId.Value,
            NCPersistentIdentity.SocialObligationIndex(initial.Snapshot.Debtor),
            NCPersistentIdentity.SocialObligationIndex(initial.Snapshot.Creditor));
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var current = await GetAsync(request.ObligationId, true);
                if (current == null)
                    return Reject(NCSocialObligationMutationStatus.StorageFailure, "The obligation disappeared.");
                if (current.Snapshot.Status != NCSocialObligationStatus.Active)
                {
                    return current.Snapshot.Status == request.Status &&
                           current.Snapshot.ResolvedWorldEventId == request.WorldEventId
                        ? new NCSocialObligationMutationResult(
                            NCSocialObligationMutationStatus.Duplicate, current, null)
                        : Reject(NCSocialObligationMutationStatus.Rejected,
                            "A closed obligation cannot be resolved again.");
                }
                if (attempt == 0 && commandState == InitialCommandState.Completed)
                {
                    return Reject(NCSocialObligationMutationStatus.StorageFailure,
                        "The completed resolution command did not close its obligation aggregate.");
                }

                var debtorIndex = await GetIndexAsync(current.Snapshot.Debtor, true);
                var creditorIndex = await GetIndexAsync(current.Snapshot.Creditor, true);
                if (debtorIndex == null || creditorIndex == null)
                    return Reject(NCSocialObligationMutationStatus.StorageFailure,
                        "An obligation participant index is missing.");

                var eventId = Guid.NewGuid();
                var next = current.Snapshot with
                {
                    Status = request.Status,
                    ResolvedWorldEventId = request.WorldEventId,
                    ResolvedAt = now,
                    DataVersion = checked(current.Snapshot.DataVersion + 1),
                    LastAppliedEventId = eventId,
                };
                if (!TryUpdateIndex(debtorIndex.Snapshot, next.Debtor, next, true, now, policy,
                        out var nextDebtor, out error) ||
                    !TryUpdateIndex(creditorIndex.Snapshot, next.Creditor, next, false, now, policy,
                        out var nextCreditor, out error))
                {
                    return Reject(NCSocialObligationMutationStatus.StorageFailure, error!);
                }
                var debtorEvent = Guid.NewGuid();
                nextDebtor = nextDebtor with { LastAppliedEventId = debtorEvent };
                var creditorEvent = Guid.NewGuid();
                nextCreditor = nextCreditor with { LastAppliedEventId = creditorEvent };
                var outbox = new List<NCPersistentBatchOutboxWrite>();
                if (request.Status is NCSocialObligationStatus.Fulfilled or NCSocialObligationStatus.Breached)
                {
                    var consequenceId = NCPersistentIdentity.SocialObligationConsequence(
                        request.ObligationId,
                        next.DataVersion);
                    outbox.Add(new NCPersistentBatchOutboxWrite(
                        consequenceId,
                        eventId,
                        ConsequenceMessageType,
                        JsonSerializer.Serialize(
                            new ConsequencePayload(next, request.Initiator, request.CorrelationId, request.CausationId),
                            JsonOptions)));
                }

                var result = await _database.ApplyNCPersistentBatchMutationAsync(new NCPersistentBatchMutationRequest(
                    attempt == 0 && commandState == InitialCommandState.Available
                        ? request.CommandId
                        : Guid.NewGuid(),
                    ResolveCommandType,
                    CommandScopeType,
                    scopeId,
                    [
                        ObligationWrite(current, next, eventId, "living-city.social-obligation.resolved",
                            request.AuditSource),
                        IndexWrite(debtorIndex, nextDebtor, debtorEvent),
                        IndexWrite(creditorIndex, nextCreditor, creditorEvent),
                    ],
                    JsonSerializer.Serialize(
                        new ObligationReceipt(request.ObligationId, current.AggregateVersion + 1), JsonOptions),
                    request.CorrelationId,
                    request.CausationId,
                    epoch,
                    now,
                    outbox));
                if (result.Status is NCPersistentMutationStatus.VersionConflict or
                    NCPersistentMutationStatus.DuplicateFailed)
                {
                    commandState = InitialCommandState.Failed;
                    continue;
                }
                return await CompleteMutation(result, request.ObligationId, current.AggregateVersion + 1);
            }
            return Reject(NCSocialObligationMutationStatus.VersionConflict,
                "Obligation resolution exceeded its optimistic retry limit.");
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to resolve social obligation {request.ObligationId}: {exception}");
            return Reject(NCSocialObligationMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            ReleaseGates(gates);
        }
    }

    public bool ValidatePrototypes(List<string> errors)
    {
        var initial = errors.Count;
        foreach (var value in _prototypes.EnumeratePrototypes<NCSocialObligationPolicyPrototype>())
        {
            if (!Valid(value))
                errors.Add($"Social obligation policy {value.ID} is invalid.");
        }
        if (!TryGetPolicy(out _))
            errors.Add("The Default social obligation policy is missing or invalid.");
        return errors.Count == initial;
    }

    private async Task HandleConsequenceAsync(NCOutboxMessageData message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Deserialize<ConsequencePayload>(message.Payload, JsonOptions);
        if (payload == null || !Valid(payload.Obligation, payload.Obligation.ObligationId) ||
            !payload.Initiator.IsValid || payload.CorrelationId == Guid.Empty || !TryGetPolicy(out var policy))
        {
            throw new InvalidOperationException("The social obligation consequence payload is invalid.");
        }
        var consequence = policy.Consequences.SingleOrDefault(value =>
            value.Kind == payload.Obligation.Kind && value.Status == payload.Obligation.Status);
        if (consequence == null)
            throw new InvalidOperationException("No consequence is authored for this obligation transition.");

        var worldEventId = payload.Obligation.ResolvedWorldEventId ?? payload.Obligation.CreatedWorldEventId;
        var stableKey = message.MessageId.ToString("D");
        var relationshipTask = _relationships.ApplyEffectAsync(new NCApplyRelationshipEffectRequest(
            NCPersistentIdentity.Command("social-obligation", "relationship", stableKey),
            payload.Obligation.Creditor,
            payload.Obligation.Debtor,
            consequence.RelationshipEffect,
            worldEventId,
            1,
            payload.Initiator,
            payload.CorrelationId,
            payload.CausationId,
            "social-obligation-outbox"));
        var memoryTask = _memories.RememberAsync(new NCRememberWorldEventRequest(
            NCPersistentIdentity.Command("social-obligation", "memory", stableKey),
            payload.Obligation.Creditor,
            consequence.MemoryEvent,
            worldEventId,
            [],
            [payload.Obligation.Debtor],
            new NCMemorySource(NCMemorySourceKind.DirectPerception, default),
            [],
            null,
            null,
            payload.Obligation.Privacy,
            payload.Initiator,
            payload.CorrelationId,
            payload.CausationId,
            "social-obligation-outbox"));
        await Task.WhenAll(relationshipTask, memoryTask);
        cancellationToken.ThrowIfCancellationRequested();
        var relationship = await relationshipTask;
        var memory = await memoryTask;
        if (relationship.Status is not (NCRelationshipMutationStatus.Applied or NCRelationshipMutationStatus.Duplicate))
            throw new InvalidOperationException($"Relationship consequence failed: {relationship.Error}");
        if (memory.Status is not (NCMemoryMutationStatus.Applied or NCMemoryMutationStatus.Duplicate))
            throw new InvalidOperationException($"Memory consequence failed: {memory.Error}");
    }

    private async Task<NCSocialObligationMutationResult> CompleteMutation(
        NCPersistentBatchMutationResult result,
        NCSocialObligationId obligationId,
        long appliedVersion)
    {
        if (result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted)
        {
            var record = await GetAsync(obligationId, true);
            if (record == null || record.AggregateVersion < appliedVersion)
                return Reject(NCSocialObligationMutationStatus.StorageFailure,
                    "The committed obligation could not be reloaded.");
            if (result.Status == NCPersistentMutationStatus.Applied)
                _outbox.Wake();
            return new NCSocialObligationMutationResult(
                result.Status == NCPersistentMutationStatus.Applied
                    ? NCSocialObligationMutationStatus.Applied
                    : NCSocialObligationMutationStatus.Duplicate,
                record,
                null);
        }
        return Reject(result.Status == NCPersistentMutationStatus.ClockRejected
                ? NCSocialObligationMutationStatus.ClockUnavailable
                : result.Status == NCPersistentMutationStatus.VersionConflict
                    ? NCSocialObligationMutationStatus.VersionConflict
                    : NCSocialObligationMutationStatus.StorageFailure,
            result.Error ?? "The social obligation mutation was not committed.");
    }

    private async Task<ObligationIndexRecord?> GetIndexAsync(NCPartyRef owner, bool forceReload)
    {
        if (!owner.IsValid)
            return null;
        if (!forceReload && _indexCache.TryGetValue(owner, out var cached))
            return cached;
        var stored = await _database.GetNCAggregateSnapshotAsync(IndexAggregateType, IndexKey(owner));
        var record = DeserializeIndex(stored, owner);
        if (record == null)
            return null;
        return _indexCache.AddOrUpdate(owner, record,
            (_, current) => current.AggregateVersion > record.AggregateVersion ? current : record);
    }

    private static bool TryUpdateIndex(
        NCSocialObligationIndexSnapshot? current,
        NCPartyRef owner,
        NCSocialObligationSnapshot obligation,
        bool isDebtor,
        NCActiveWorldTime now,
        NCSocialObligationPolicyPrototype policy,
        out NCSocialObligationIndexSnapshot next,
        out string? error)
    {
        var values = current?.Obligations.ToList() ?? [];
        var index = values.FindIndex(value => value.ObligationId == obligation.ObligationId);
        if (index < 0 && values.Count >= policy.MaximumObligationsPerParty)
        {
            next = default!;
            error = $"Party {owner} reached its configured obligation index limit.";
            return false;
        }
        var sequence = checked((current?.DataVersion ?? 0) + 1);
        var entry = new NCSocialObligationIndexEntry(
            obligation.ObligationId,
            isDebtor ? obligation.Creditor : obligation.Debtor,
            isDebtor,
            obligation.Kind,
            obligation.Status,
            now,
            sequence);
        if (index < 0)
            values.Add(entry);
        else
            values[index] = entry;
        values.Sort((left, right) =>
        {
            var changed = right.ChangedSequence.CompareTo(left.ChangedSequence);
            return changed != 0 ? changed : left.ObligationId.Value.CompareTo(right.ObligationId.Value);
        });
        next = new NCSocialObligationIndexSnapshot(owner, values, sequence, Guid.Empty);
        error = null;
        return true;
    }

    private static NCPersistentAggregateWrite ObligationWrite(
        NCSocialObligationRecord? current,
        NCSocialObligationSnapshot next,
        Guid eventId,
        string eventType,
        string auditSource) => new(
            eventId,
            eventType,
            AggregateType,
            ObligationKey(next.ObligationId),
            current?.AggregateVersion ?? 0,
            SnapshotFormatVersion,
            JsonSerializer.Serialize(next, JsonOptions),
            JsonSerializer.Serialize(new ObligationEvent(
                next.ObligationId, next.Status, auditSource.Trim(), next), JsonOptions));

    private static NCPersistentAggregateWrite IndexWrite(
        ObligationIndexRecord? current,
        NCSocialObligationIndexSnapshot next,
        Guid eventId) => new(
            eventId,
            "living-city.social-obligation.index-updated",
            IndexAggregateType,
            IndexKey(next.Owner),
            current?.AggregateVersion ?? 0,
            SnapshotFormatVersion,
            JsonSerializer.Serialize(next, JsonOptions),
            JsonSerializer.Serialize(new ObligationIndexEvent(next.Owner, next.DataVersion), JsonOptions));

    private bool TryValidateCreate(
        NCCreateSocialObligationRequest request,
        out NCSocialObligationPolicyPrototype policy,
        out string? error)
    {
        error = null;
        if (!TryGetPolicy(out policy) || request.CommandId == Guid.Empty || !request.ObligationId.IsValid ||
            !Enum.IsDefined(request.Kind) || !request.Debtor.IsValid || !request.Creditor.IsValid ||
            request.Debtor == request.Creditor || !Valid(request.Terms, policy) || !Enum.IsDefined(request.Privacy) ||
            request.WorldEventId == Guid.Empty || !request.Initiator.IsValid || request.CorrelationId == Guid.Empty ||
            request.DueAt is { } due && (!_clock.TryGetCurrentTime(out _, out var now) || due <= now ||
                due.ElapsedSince(now) > policy.MaximumDueTime) || string.IsNullOrWhiteSpace(request.AuditSource) ||
            request.AuditSource.Trim().Length > MaximumAuditSourceLength)
        {
            error = "The social obligation creation request is invalid.";
            return false;
        }
        return true;
    }

    private static bool TryValidateResolve(NCResolveSocialObligationRequest request, out string? error)
    {
        error = null;
        if (request.CommandId == Guid.Empty || !request.ObligationId.IsValid ||
            request.Status is not (NCSocialObligationStatus.Fulfilled or NCSocialObligationStatus.Breached or
                NCSocialObligationStatus.Forgiven or NCSocialObligationStatus.Cancelled) ||
            request.WorldEventId == Guid.Empty || !request.Initiator.IsValid || request.CorrelationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.AuditSource) ||
            request.AuditSource.Trim().Length > MaximumAuditSourceLength)
        {
            error = "The social obligation resolution request is invalid.";
            return false;
        }
        return true;
    }

    private bool TryGetPolicy(out NCSocialObligationPolicyPrototype policy) =>
        _prototypes.TryIndex(DefaultPolicy, out policy!) && Valid(policy);

    /// <summary>
    /// A failed optimistic command may be retried with a fresh internal ID, but a caller-provided ID that belongs to
    /// another operation must never be bypassed by that retry.
    /// </summary>
    private async Task<InitialCommandState> ValidateInitialCommandAsync(
        Guid commandId,
        string commandType,
        string scopeId)
    {
        var command = await _database.GetNCPersistentCommandResultAsync(commandId);
        if (command == null)
            return InitialCommandState.Available;
        if (command.CommandType != commandType || command.ScopeType != CommandScopeType ||
            command.ScopeId != scopeId)
        {
            return InitialCommandState.Collision;
        }
        return command.Status switch
        {
            NCPersistentMutationStatus.InProgress => InitialCommandState.InProgress,
            NCPersistentMutationStatus.DuplicateCompleted => InitialCommandState.Completed,
            _ => InitialCommandState.Failed,
        };
    }

    private bool Valid(NCSocialObligationPolicyPrototype value)
    {
        if (value.MaximumObligationsPerParty is < 1 or > AbsoluteMaximumObligations ||
            value.MaximumQueryCount is < 1 || value.MaximumQueryCount > value.MaximumObligationsPerParty ||
            value.MaximumQuantity < 1 || value.MaximumDueTime <= TimeSpan.Zero ||
            value.Consequences.Count != 9 || value.Consequences
                .Select(item => (item.Kind, item.Status)).Distinct().Count() != value.Consequences.Count)
        {
            return false;
        }
        foreach (var consequence in value.Consequences)
        {
            if (!Enum.IsDefined(consequence.Kind) || consequence.Status is not (
                    NCSocialObligationStatus.Active or NCSocialObligationStatus.Fulfilled or
                    NCSocialObligationStatus.Breached) ||
                !_prototypes.TryIndex(consequence.RelationshipEffect, out NCRelationshipEffectPrototype? effect) ||
                effect == null || !_prototypes.TryIndex(consequence.MemoryEvent, out NCMemoryEventPrototype? memory) ||
                memory == null)
            {
                return false;
            }
        }
        return true;
    }

    private static bool Valid(NCSocialObligationTerms value, NCSocialObligationPolicyPrototype policy) =>
        IsStableKey(value.PurposeKey) && value.Quantity is >= 0 && value.Quantity <= policy.MaximumQuantity &&
        IsStableKey(value.UnitKey) && Valid(value.Subject);

    private static bool Valid(NCSocialObligationSnapshot? value, NCSocialObligationId expected) => value != null &&
        value.ObligationId == expected && expected.IsValid && Enum.IsDefined(value.Kind) && value.Debtor.IsValid &&
        value.Creditor.IsValid && value.Debtor != value.Creditor && IsStableKey(value.Terms.PurposeKey) &&
        value.Terms.Quantity >= 0 && IsStableKey(value.Terms.UnitKey) && Valid(value.Terms.Subject) &&
        Enum.IsDefined(value.Privacy) && Enum.IsDefined(value.Status) && value.CreatedWorldEventId != Guid.Empty &&
        value.CreatedAt.IsValid && (value.DueAt == null || value.DueAt > value.CreatedAt) &&
        (value.Status == NCSocialObligationStatus.Active
            ? value.ResolvedWorldEventId == null && value.ResolvedAt == null
            : value.ResolvedWorldEventId is { } resolved && resolved != Guid.Empty && value.ResolvedAt >= value.CreatedAt) &&
        value.DataVersion > 0 && value.LastAppliedEventId != Guid.Empty;

    private static bool Valid(NCSocialObligationIndexSnapshot? value, NCPartyRef owner) => value != null &&
        value.Owner == owner && owner.IsValid && value.Obligations.Count is > 0 and <= AbsoluteMaximumObligations &&
        value.Obligations.Select(entry => entry.ObligationId).Distinct().Count() == value.Obligations.Count &&
        value.Obligations.All(entry => entry.ObligationId.IsValid && entry.Counterparty.IsValid &&
            entry.Counterparty != owner && Enum.IsDefined(entry.Kind) && Enum.IsDefined(entry.Status) &&
            entry.ChangedAt.IsValid && entry.ChangedSequence > 0 && entry.ChangedSequence <= value.DataVersion) &&
        value.DataVersion > 0 && value.LastAppliedEventId != Guid.Empty;

    private static bool Valid(NCInformationSubject value) => value.IsValid && value.StableId.Length <= 128 &&
        value.StableId == value.StableId.Trim().ToLowerInvariant();

    private static bool SameCreate(NCSocialObligationSnapshot value, NCCreateSocialObligationRequest request) =>
        value.ObligationId == request.ObligationId && value.Kind == request.Kind && value.Debtor == request.Debtor &&
        value.Creditor == request.Creditor && value.Terms == Normalize(request.Terms) &&
        value.Privacy == request.Privacy && value.CreatedWorldEventId == request.WorldEventId &&
        value.DueAt == request.DueAt;

    private static NCSocialObligationTerms Normalize(NCSocialObligationTerms value) => value with
    {
        PurposeKey = value.PurposeKey.Trim().ToLowerInvariant(),
        UnitKey = value.UnitKey.Trim().ToLowerInvariant(),
        Subject = value.Subject with { StableId = value.Subject.StableId.Trim().ToLowerInvariant() },
    };

    private async Task<bool> ResolveParties(NCPartyRef first, NCPartyRef second, NCPartyRef third)
    {
        var one = _parties.ResolveAsync(first, true);
        var two = _parties.ResolveAsync(second, true);
        var three = _parties.ResolveAsync(third, true);
        await Task.WhenAll(one, two, three);
        return (await one).IsResolved && (await two).IsResolved && (await three).IsResolved;
    }

    private static NCSocialObligationRecord? DeserializeObligation(
        NCAggregateSnapshotData? stored,
        NCSocialObligationId expected)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion || !stored.SessionEpoch.IsValid ||
            !stored.ActiveWorldTime.IsValid)
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCSocialObligationSnapshot>(stored.Payload, JsonOptions);
            return Valid(snapshot, expected) && snapshot!.DataVersion == stored.AggregateVersion
                ? new NCSocialObligationRecord(snapshot, stored.AggregateVersion, stored.SessionEpoch,
                    stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ObligationIndexRecord? DeserializeIndex(NCAggregateSnapshotData? stored, NCPartyRef owner)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion || !stored.SessionEpoch.IsValid ||
            !stored.ActiveWorldTime.IsValid)
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCSocialObligationIndexSnapshot>(stored.Payload, JsonOptions);
            return Valid(snapshot, owner) && snapshot!.DataVersion == stored.AggregateVersion
                ? new ObligationIndexRecord(snapshot, stored.AggregateVersion, stored.SessionEpoch,
                    stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private NCSocialObligationRecord CacheNewest(NCSocialObligationRecord candidate) =>
        _cache.AddOrUpdate(candidate.Snapshot.ObligationId, candidate,
            (_, current) => current.AggregateVersion > candidate.AggregateVersion ? current : candidate);

    private async Task<SemaphoreSlim[]> AcquireGates(params Guid[] keys)
    {
        var gates = keys.Select(key => _gates[(key.GetHashCode() & int.MaxValue) % _gates.Length])
            .Distinct().OrderBy(gate => Array.IndexOf(_gates, gate)).ToArray();
        foreach (var gate in gates)
            await gate.WaitAsync();
        return gates;
    }

    private static void ReleaseGates(IReadOnlyList<SemaphoreSlim> gates)
    {
        for (var index = gates.Count - 1; index >= 0; index--)
            gates[index].Release();
    }

    private static SemaphoreSlim[] CreateGates()
    {
        var result = new SemaphoreSlim[GateStripeCount];
        for (var index = 0; index < result.Length; index++)
            result[index] = new SemaphoreSlim(1, 1);
        return result;
    }

    private static bool IsStableKey(string value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumStableKeyLength && value == value.Trim().ToLowerInvariant() &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    private static string ObligationKey(NCSocialObligationId id) => id.Value.ToString("D");
    private static string IndexKey(NCPartyRef owner) =>
        NCPersistentIdentity.SocialObligationIndex(owner).ToString("D");
    private static NCSocialObligationMutationResult Reject(NCSocialObligationMutationStatus status, string error) =>
        new(status, null, error);

    private sealed record ObligationIndexRecord(
        NCSocialObligationIndexSnapshot Snapshot,
        long AggregateVersion,
        NCSessionEpoch SessionEpoch,
        NCActiveWorldTime PersistedAt);
    private sealed record ObligationReceipt(NCSocialObligationId ObligationId, long AggregateVersion);
    private sealed record ObligationEvent(
        NCSocialObligationId ObligationId,
        NCSocialObligationStatus Status,
        string AuditSource,
        NCSocialObligationSnapshot Snapshot);
    private sealed record ObligationIndexEvent(NCPartyRef Owner, int DataVersion);
    private sealed record ConsequencePayload(
        NCSocialObligationSnapshot Obligation,
        NCPartyRef Initiator,
        Guid CorrelationId,
        Guid? CausationId);

    private enum InitialCommandState : byte
    {
        Available,
        Completed,
        Failed,
        InProgress,
        Collision,
    }
}
