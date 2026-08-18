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
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Social;

public enum NCRelationshipMutationStatus : byte
{
    Applied,
    Duplicate,
    PartyUnavailable,
    VersionConflict,
    ClockUnavailable,
    Rejected,
    StorageFailure,
}

public sealed record NCRelationshipRecord(
    NCRelationshipSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCRelationshipMutationResult(
    NCRelationshipMutationStatus Status,
    NCRelationshipRecord? Relationship,
    string? Error);

/// <summary>
/// Persistent directed social state. Only authoritative world-event effects can mutate a relationship; Agent contacts,
/// neural output and materialized ECS entities are consumers, never alternate authorities.
/// </summary>
public sealed partial class NCRelationshipRegistrySystem : EntitySystem
{
    public const string RelationshipAggregateType = "living-city.relationship";
    public const string ObserverIndexAggregateType = "living-city.relationship-index";
    public const string ApplicationAggregateType = "living-city.relationship-application";
    public const int SnapshotFormatVersion = 1;

    private static readonly ProtoId<NCRelationshipPolicyPrototype> DefaultPolicy = "Default";

    private const int MutationGateStripeCount = 64;
    private const int MaximumAuditSourceLength = 128;
    private const int AbsoluteMaximumScore = 1_000_000;
    private const long AbsoluteMaximumDebt = 1_000_000_000_000;
    private const int AbsoluteMaximumRelationships = 4_096;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPartyResolverSystem _parties = default!;

    private readonly ConcurrentDictionary<NCRelationshipId, NCRelationshipRecord> _cache = [];
    private readonly ConcurrentDictionary<NCPartyRef, ObserverIndexRecord> _indexCache = [];
    private readonly SemaphoreSlim[] _gates = CreateGates();

    public int CachedRelationshipCount => _cache.Count;
    public int CachedObserverIndexCount => _indexCache.Count;

    public bool ValidatePrototypes(List<string> errors)
    {
        var initialCount = errors.Count;
        var policies = _prototypes.EnumeratePrototypes<NCRelationshipPolicyPrototype>().ToArray();
        foreach (var policy in policies)
        {
            if (!ValidPolicy(policy))
                errors.Add($"Relationship policy {policy.ID} has invalid bounds.");
        }

        if (!_prototypes.TryIndex(DefaultPolicy, out NCRelationshipPolicyPrototype? activePolicy) ||
            activePolicy == null || !ValidPolicy(activePolicy))
        {
            errors.Add("The Default relationship policy is missing or invalid.");
            return false;
        }

        foreach (var effect in _prototypes.EnumeratePrototypes<NCRelationshipEffectPrototype>())
        {
            if (!ValidEffect(effect, activePolicy))
                errors.Add($"Relationship effect {effect.ID} is empty or exceeds the active policy bounds.");
        }

        return errors.Count == initialCount;
    }

    public bool Unload(NCRelationshipId relationshipId) => _cache.TryRemove(relationshipId, out _);

    public bool UnloadObserverIndex(NCPartyRef observer) => _indexCache.TryRemove(observer, out _);

    public async Task<NCRelationshipRecord?> GetAsync(
        NCPartyRef observer,
        NCPartyRef subject,
        bool forceReload = false)
    {
        if (!observer.IsValid || !subject.IsValid || observer == subject)
            return null;

        return await GetAsync(NCPersistentIdentity.Relationship(observer, subject), forceReload);
    }

    public async Task<NCRelationshipRecord?> GetAsync(
        NCRelationshipId relationshipId,
        bool forceReload = false)
    {
        if (!relationshipId.IsValid)
            return null;
        if (!forceReload && _cache.TryGetValue(relationshipId, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(
            RelationshipAggregateType,
            RelationshipKey(relationshipId));
        if (stored == null)
            return null;
        var record = DeserializeRelationship(stored, relationshipId);
        return record == null
            ? throw new InvalidOperationException($"Relationship {relationshipId} snapshot is invalid.")
            : CacheNewest(record);
    }

    /// <summary>
    /// Loads a bounded newest-first observer projection without scanning every city relationship.
    /// </summary>
    public async Task<IReadOnlyList<NCRelationshipRecord>> GetForObserverAsync(
        NCPartyRef observer,
        int limit,
        bool forceReload = false)
    {
        if (!observer.IsValid ||
            !_prototypes.TryIndex(DefaultPolicy, out NCRelationshipPolicyPrototype? policy) || policy == null ||
            !ValidPolicy(policy) || limit < 1 || limit > policy.MaximumQueryCount)
            return [];

        var index = await GetObserverIndexAsync(observer, forceReload);
        if (index == null)
            return [];

        var result = new List<NCRelationshipRecord>(Math.Min(limit, index.Snapshot.Relationships.Count));
        foreach (var entry in index.Snapshot.Relationships.Take(limit))
        {
            var relationship = await GetAsync(entry.RelationshipId, forceReload);
            if (relationship == null || relationship.Snapshot.Observer != observer ||
                relationship.Snapshot.Subject != entry.Subject)
            {
                throw new InvalidOperationException(
                    $"Relationship index for {observer} references an invalid aggregate {entry.RelationshipId}.");
            }
            result.Add(relationship);
        }

        return result;
    }

    public async Task<NCRelationshipMutationResult> ApplyEffectAsync(NCApplyRelationshipEffectRequest request)
    {
        if (!TryValidateRequest(request, out var effect, out var policy, out var error) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(_clock.IsReady ? NCRelationshipMutationStatus.Rejected :
                NCRelationshipMutationStatus.ClockUnavailable,
                error ?? "The active-world clock is not ready.");
        }

        var relationshipId = NCPersistentIdentity.Relationship(request.Observer, request.Subject);
        var indexId = NCPersistentIdentity.RelationshipIndex(request.Observer);
        var applicationId = NCPersistentIdentity.RelationshipApplication(relationshipId, request.WorldEventId);
        var scopeId = applicationId.ToString("D");
        InitialCommandState commandAvailable;
        NCPartyResolution observer;
        NCPartyResolution subject;
        NCPartyResolution initiator;
        try
        {
            commandAvailable = await ValidateInitialCommandAsync(request.CommandId, scopeId);
            observer = await _parties.ResolveAsync(request.Observer, true);
            subject = await _parties.ResolveAsync(request.Subject, true);
            initiator = await _parties.ResolveAsync(request.Initiator, true);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to prepare relationship event {request.WorldEventId}: {exception}");
            return Reject(NCRelationshipMutationStatus.StorageFailure, exception.Message);
        }
        if (commandAvailable == InitialCommandState.Collision)
            return Reject(NCRelationshipMutationStatus.Rejected, "The command ID belongs to another operation.");
        if (commandAvailable == InitialCommandState.InProgress)
            return Reject(NCRelationshipMutationStatus.StorageFailure, "The relationship command is still in progress.");

        if (!observer.IsResolved || !subject.IsResolved || !initiator.IsResolved)
        {
            return Reject(NCRelationshipMutationStatus.PartyUnavailable,
                "Observer, subject, or initiator is unavailable.");
        }

        var gates = await AcquireGates(relationshipId.Value, indexId);
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var application = await GetApplicationAsync(applicationId);
                if (application != null)
                    return await ReplayApplicationAsync(application, request, relationshipId);
                if (attempt == 0 && commandAvailable == InitialCommandState.Completed)
                {
                    return Reject(NCRelationshipMutationStatus.StorageFailure,
                        "The completed relationship command is missing its application marker.");
                }

                var current = await GetAsync(relationshipId, true);
                if (current != null && (current.Snapshot.Observer != request.Observer ||
                    current.Snapshot.Subject != request.Subject || !WithinPolicy(current.Snapshot, policy)))
                {
                    return Reject(NCRelationshipMutationStatus.StorageFailure,
                        "The current relationship snapshot is invalid for this policy.");
                }

                var index = await GetObserverIndexAsync(request.Observer, true);
                if (index != null && index.Snapshot.Observer != request.Observer)
                {
                    return Reject(NCRelationshipMutationStatus.StorageFailure,
                        "The observer relationship index is bound to another party.");
                }

                var relationshipEventId = Guid.NewGuid();
                var indexEventId = Guid.NewGuid();
                var applicationEventId = Guid.NewGuid();
                NCRelationshipSnapshot next;
                try
                {
                    next = Apply(current?.Snapshot, relationshipId, request, effect, policy, now,
                        relationshipEventId);
                }
                catch (OverflowException)
                {
                    return Reject(NCRelationshipMutationStatus.Rejected,
                        "The relationship effect would overflow its bounded counters.");
                }

                if (!TryBuildIndex(index?.Snapshot, request.Observer, request.Subject, relationshipId, now,
                    policy, indexEventId, out var nextIndex, out error))
                {
                    return Reject(NCRelationshipMutationStatus.Rejected, error!);
                }

                var nextRelationshipVersion = (current?.AggregateVersion ?? 0) + 1;
                var marker = new RelationshipApplicationSnapshot(
                    applicationId,
                    relationshipId,
                    request.Observer,
                    request.Subject,
                    request.WorldEventId,
                    effect.ID,
                    request.Magnitude,
                    request.Initiator,
                    nextRelationshipVersion,
                    now,
                    applicationEventId);
                var receipt = new RelationshipReceipt(next, nextRelationshipVersion, epoch, now);
                var commandId = attempt == 0 && commandAvailable == InitialCommandState.Available
                    ? request.CommandId
                    : Guid.NewGuid();
                var result = await _database.ApplyNCPersistentBatchMutationAsync(
                    new NCPersistentBatchMutationRequest(
                        commandId,
                        "living-city.relationship.apply-effect",
                        "relationship-application",
                        scopeId,
                        [
                            new NCPersistentAggregateWrite(
                                relationshipEventId,
                                "living-city.relationship.effect-applied",
                                RelationshipAggregateType,
                                RelationshipKey(relationshipId),
                                current?.AggregateVersion ?? 0,
                                SnapshotFormatVersion,
                                JsonSerializer.Serialize(next, JsonOptions),
                                JsonSerializer.Serialize(new RelationshipEffectEvent(
                                    relationshipId,
                                    request.Observer,
                                    request.Subject,
                                    request.WorldEventId,
                                    effect.ID,
                                    request.Magnitude,
                                    request.Initiator,
                                    request.AuditSource.Trim()), JsonOptions)),
                            new NCPersistentAggregateWrite(
                                indexEventId,
                                "living-city.relationship.index-updated",
                                ObserverIndexAggregateType,
                                IndexKey(request.Observer),
                                index?.AggregateVersion ?? 0,
                                SnapshotFormatVersion,
                                JsonSerializer.Serialize(nextIndex, JsonOptions),
                                JsonSerializer.Serialize(new RelationshipIndexEvent(
                                    request.Observer,
                                    relationshipId,
                                    request.Subject), JsonOptions)),
                            new NCPersistentAggregateWrite(
                                applicationEventId,
                                "living-city.relationship.application-recorded",
                                ApplicationAggregateType,
                                scopeId,
                                0,
                                SnapshotFormatVersion,
                                JsonSerializer.Serialize(marker, JsonOptions),
                                JsonSerializer.Serialize(marker, JsonOptions)),
                        ],
                        JsonSerializer.Serialize(receipt, JsonOptions),
                        request.CorrelationId,
                        request.CausationId,
                        epoch,
                        now,
                        []));

                if (result.Status is NCPersistentMutationStatus.Applied or
                    NCPersistentMutationStatus.DuplicateCompleted)
                {
                    var persisted = await GetAsync(relationshipId, true);
                    var persistedIndex = await GetObserverIndexAsync(request.Observer, true);
                    if (persisted == null || persistedIndex == null)
                    {
                        return Reject(NCRelationshipMutationStatus.StorageFailure,
                            "The committed relationship or observer index could not be reloaded.");
                    }

                    return new NCRelationshipMutationResult(
                        result.Status == NCPersistentMutationStatus.Applied
                            ? NCRelationshipMutationStatus.Applied
                            : NCRelationshipMutationStatus.Duplicate,
                        persisted,
                        null);
                }

                if (result.Status is NCPersistentMutationStatus.VersionConflict or
                    NCPersistentMutationStatus.DuplicateFailed)
                {
                    commandAvailable = InitialCommandState.Failed;
                    continue;
                }

                return Reject(result.Status == NCPersistentMutationStatus.ClockRejected
                        ? NCRelationshipMutationStatus.ClockUnavailable
                        : NCRelationshipMutationStatus.StorageFailure,
                    result.Error ?? "The relationship effect was not committed.");
            }

            return Reject(NCRelationshipMutationStatus.VersionConflict,
                "The relationship effect exceeded its optimistic retry limit.");
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to apply relationship event {request.WorldEventId} to {relationshipId}: {exception}");
            return Reject(NCRelationshipMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            ReleaseGates(gates);
        }
    }

    private async Task<ObserverIndexRecord?> GetObserverIndexAsync(NCPartyRef observer, bool forceReload)
    {
        if (!observer.IsValid)
            return null;
        if (!forceReload && _indexCache.TryGetValue(observer, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(ObserverIndexAggregateType, IndexKey(observer));
        if (stored == null)
            return null;
        var record = DeserializeIndex(stored, observer);
        return record == null
            ? throw new InvalidOperationException($"Relationship index for {observer} is invalid.")
            : CacheNewest(record);
    }

    private async Task<RelationshipApplicationSnapshot?> GetApplicationAsync(Guid applicationId)
    {
        var stored = await _database.GetNCAggregateSnapshotAsync(
            ApplicationAggregateType,
            applicationId.ToString("D"));
        if (stored == null)
            return null;
        if (stored.FormatVersion != SnapshotFormatVersion || !stored.SessionEpoch.IsValid ||
            !stored.ActiveWorldTime.IsValid)
        {
            throw new InvalidOperationException("The relationship application metadata is invalid.");
        }

        try
        {
            var marker = JsonSerializer.Deserialize<RelationshipApplicationSnapshot>(stored.Payload, JsonOptions);
            return Valid(marker, applicationId) && marker!.AppliedAt == stored.ActiveWorldTime ? marker :
                throw new InvalidOperationException("The relationship application snapshot is invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The relationship application payload is invalid.", exception);
        }
    }

    private async Task<NCRelationshipMutationResult> ReplayApplicationAsync(
        RelationshipApplicationSnapshot marker,
        NCApplyRelationshipEffectRequest request,
        NCRelationshipId relationshipId)
    {
        if (marker.RelationshipId != relationshipId || marker.Observer != request.Observer ||
            marker.Subject != request.Subject || marker.WorldEventId != request.WorldEventId ||
            marker.EffectId != request.Effect.Id || marker.Magnitude != request.Magnitude ||
            marker.Initiator != request.Initiator)
        {
            return Reject(NCRelationshipMutationStatus.Rejected,
                "The world-event application identity is bound to different immutable data.");
        }

        var current = await GetAsync(relationshipId, true);
        return current == null || current.AggregateVersion < marker.ResultingRelationshipVersion
            ? Reject(NCRelationshipMutationStatus.StorageFailure,
                "The relationship application exists without its resulting relationship version.")
            : new NCRelationshipMutationResult(NCRelationshipMutationStatus.Duplicate, current, null);
    }

    private async Task<InitialCommandState> ValidateInitialCommandAsync(Guid commandId, string scopeId)
    {
        var command = await _database.GetNCPersistentCommandResultAsync(commandId);
        if (command == null)
            return InitialCommandState.Available;
        if (command.CommandType != "living-city.relationship.apply-effect" ||
            command.ScopeType != "relationship-application" || command.ScopeId != scopeId)
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

    private bool TryValidateRequest(
        NCApplyRelationshipEffectRequest request,
        out NCRelationshipEffectPrototype effect,
        out NCRelationshipPolicyPrototype policy,
        out string? error)
    {
        effect = default!;
        policy = default!;
        error = null;
        if (request.CommandId == Guid.Empty || request.WorldEventId == Guid.Empty ||
            request.CorrelationId == Guid.Empty || !request.Observer.IsValid || !request.Subject.IsValid ||
            request.Observer == request.Subject || !request.Initiator.IsValid || request.Magnitude < 1 ||
            string.IsNullOrWhiteSpace(request.AuditSource) ||
            request.AuditSource.Trim().Length > MaximumAuditSourceLength)
        {
            error = "The relationship effect request is invalid.";
            return false;
        }
        if (!_prototypes.TryIndex(DefaultPolicy, out NCRelationshipPolicyPrototype? resolvedPolicy) ||
            resolvedPolicy == null || !ValidPolicy(resolvedPolicy))
        {
            error = "The relationship policy is invalid.";
            return false;
        }
        policy = resolvedPolicy;
        if (request.Magnitude > policy.MaximumEffectMagnitude ||
            !_prototypes.TryIndex(request.Effect, out NCRelationshipEffectPrototype? resolvedEffect) ||
            resolvedEffect == null || !ValidEffect(resolvedEffect, policy))
        {
            error = "The relationship effect prototype or magnitude is invalid.";
            return false;
        }
        effect = resolvedEffect;
        return true;
    }

    private static NCRelationshipSnapshot Apply(
        NCRelationshipSnapshot? current,
        NCRelationshipId relationshipId,
        NCApplyRelationshipEffectRequest request,
        NCRelationshipEffectPrototype effect,
        NCRelationshipPolicyPrototype policy,
        NCActiveWorldTime now,
        Guid eventId)
    {
        return new NCRelationshipSnapshot(
            relationshipId,
            request.Observer,
            request.Subject,
            ApplySigned(current?.Trust ?? 0, effect.TrustDelta, request.Magnitude, policy.MaximumScore),
            ApplyUnsigned(current?.Fear ?? 0, effect.FearDelta, request.Magnitude, policy.MaximumScore),
            ApplySigned(current?.Respect ?? 0, effect.RespectDelta, request.Magnitude, policy.MaximumScore),
            ApplySigned(current?.Affection ?? 0, effect.AffectionDelta, request.Magnitude, policy.MaximumScore),
            ApplyUnsigned(current?.Hostility ?? 0, effect.HostilityDelta, request.Magnitude, policy.MaximumScore),
            ApplyDebt(current?.Debt ?? 0, effect.DebtDelta, request.Magnitude, policy.MaximumAbsoluteDebt),
            ApplyUnsigned(current?.Obligation ?? 0, effect.ObligationDelta, request.Magnitude, policy.MaximumScore),
            checked((current?.InteractionCount ?? 0) + 1),
            current?.CreatedAt ?? now,
            now,
            request.WorldEventId,
            effect.ID,
            checked((current?.DataVersion ?? 0) + 1),
            eventId);
    }

    private static int ApplySigned(int current, int delta, int magnitude, int maximum) =>
        (int) Math.Clamp(checked((long) current + (long) delta * magnitude), -maximum, maximum);

    private static int ApplyUnsigned(int current, int delta, int magnitude, int maximum) =>
        (int) Math.Clamp(checked((long) current + (long) delta * magnitude), 0, maximum);

    private static long ApplyDebt(long current, long delta, int magnitude, long maximum) =>
        Math.Clamp(checked(current + checked(delta * magnitude)), -maximum, maximum);

    private static bool TryBuildIndex(
        NCRelationshipIndexSnapshot? current,
        NCPartyRef observer,
        NCPartyRef subject,
        NCRelationshipId relationshipId,
        NCActiveWorldTime now,
        NCRelationshipPolicyPrototype policy,
        Guid eventId,
        out NCRelationshipIndexSnapshot next,
        out string? error)
    {
        error = null;
        var entries = current?.Relationships.ToList() ?? [];
        var nextSequence = checked((current?.DataVersion ?? 0) + 1);
        var index = entries.FindIndex(value => value.RelationshipId == relationshipId);
        if (index >= 0)
        {
            if (entries[index].Subject != subject)
            {
                next = default!;
                error = "The relationship index identity is bound to another subject.";
                return false;
            }
            entries[index] = entries[index] with
            {
                LastChangedAt = now,
                LastChangedSequence = nextSequence,
            };
        }
        else
        {
            if (entries.Count >= policy.MaximumRelationshipsPerObserver)
            {
                next = default!;
                error = "The observer reached the configured relationship limit.";
                return false;
            }
            entries.Add(new NCRelationshipIndexEntry(relationshipId, subject, now, nextSequence));
        }

        entries.Sort((left, right) =>
        {
            var sequence = right.LastChangedSequence.CompareTo(left.LastChangedSequence);
            return sequence != 0 ? sequence : string.CompareOrdinal(left.Subject.ToString(), right.Subject.ToString());
        });
        next = new NCRelationshipIndexSnapshot(
            observer,
            entries,
            nextSequence,
            eventId);
        return true;
    }

    private static bool ValidPolicy(NCRelationshipPolicyPrototype policy) =>
        policy.MaximumScore is > 0 and <= AbsoluteMaximumScore &&
        policy.MaximumAbsoluteDebt is > 0 and <= AbsoluteMaximumDebt &&
        policy.MaximumEffectMagnitude is > 0 and <= 1_000 &&
        policy.MaximumSingleEffectDelta is > 0 and <= AbsoluteMaximumScore &&
        policy.MaximumRelationshipsPerObserver is > 0 and <= AbsoluteMaximumRelationships &&
        policy.MaximumQueryCount is > 0 &&
        policy.MaximumQueryCount <= policy.MaximumRelationshipsPerObserver;

    private static bool ValidEffect(
        NCRelationshipEffectPrototype effect,
        NCRelationshipPolicyPrototype policy)
    {
        var maximum = policy.MaximumSingleEffectDelta;
        return !string.IsNullOrWhiteSpace(effect.ID) && effect.ID.Length <= 128 &&
               Math.Abs((long) effect.TrustDelta) <= maximum &&
               Math.Abs((long) effect.FearDelta) <= maximum &&
               Math.Abs((long) effect.RespectDelta) <= maximum &&
               Math.Abs((long) effect.AffectionDelta) <= maximum &&
               Math.Abs((long) effect.HostilityDelta) <= maximum &&
               effect.DebtDelta != long.MinValue && Math.Abs(effect.DebtDelta) <= policy.MaximumAbsoluteDebt &&
               Math.Abs((long) effect.ObligationDelta) <= maximum &&
               (effect.TrustDelta != 0 || effect.FearDelta != 0 || effect.RespectDelta != 0 ||
                effect.AffectionDelta != 0 || effect.HostilityDelta != 0 || effect.DebtDelta != 0 ||
                effect.ObligationDelta != 0);
    }

    private static bool WithinPolicy(NCRelationshipSnapshot snapshot, NCRelationshipPolicyPrototype policy) =>
        Math.Abs((long) snapshot.Trust) <= policy.MaximumScore && snapshot.Fear <= policy.MaximumScore &&
        snapshot.Fear >= 0 && Math.Abs((long) snapshot.Respect) <= policy.MaximumScore &&
        Math.Abs((long) snapshot.Affection) <= policy.MaximumScore && snapshot.Hostility >= 0 &&
        snapshot.Hostility <= policy.MaximumScore && snapshot.Debt != long.MinValue &&
        Math.Abs(snapshot.Debt) <= policy.MaximumAbsoluteDebt &&
        snapshot.Obligation >= 0 && snapshot.Obligation <= policy.MaximumScore;

    private static bool Valid(NCRelationshipSnapshot? snapshot, NCRelationshipId? expected)
    {
        return snapshot != null && snapshot.RelationshipId.IsValid &&
               (expected == null || snapshot.RelationshipId == expected) && snapshot.Observer.IsValid &&
               snapshot.Subject.IsValid && snapshot.Observer != snapshot.Subject &&
               snapshot.RelationshipId == NCPersistentIdentity.Relationship(snapshot.Observer, snapshot.Subject) &&
               Math.Abs((long) snapshot.Trust) <= AbsoluteMaximumScore && snapshot.Fear is >= 0 and <= AbsoluteMaximumScore &&
               Math.Abs((long) snapshot.Respect) <= AbsoluteMaximumScore &&
               Math.Abs((long) snapshot.Affection) <= AbsoluteMaximumScore &&
               snapshot.Hostility is >= 0 and <= AbsoluteMaximumScore &&
               snapshot.Debt != long.MinValue && Math.Abs(snapshot.Debt) <= AbsoluteMaximumDebt &&
               snapshot.Obligation is >= 0 and <= AbsoluteMaximumScore && snapshot.InteractionCount > 0 &&
               snapshot.CreatedAt.IsValid && snapshot.UpdatedAt >= snapshot.CreatedAt &&
               snapshot.LastWorldEventId != Guid.Empty && !string.IsNullOrWhiteSpace(snapshot.LastEffectId) &&
               snapshot.DataVersion > 0 && snapshot.LastAppliedEventId != Guid.Empty;
    }

    private static bool Valid(NCRelationshipIndexSnapshot? snapshot, NCPartyRef expected)
    {
        if (snapshot == null || snapshot.Observer != expected || snapshot.Relationships.Count is < 1 or
            > AbsoluteMaximumRelationships || snapshot.DataVersion < 1 || snapshot.LastAppliedEventId == Guid.Empty)
        {
            return false;
        }

        var ids = new HashSet<NCRelationshipId>();
        var subjects = new HashSet<NCPartyRef>();
        var sequences = new HashSet<int>();
        foreach (var entry in snapshot.Relationships)
        {
            if (!entry.RelationshipId.IsValid || !entry.Subject.IsValid || entry.Subject == expected ||
                entry.RelationshipId != NCPersistentIdentity.Relationship(expected, entry.Subject) ||
                !entry.LastChangedAt.IsValid || entry.LastChangedSequence is < 1 ||
                entry.LastChangedSequence > snapshot.DataVersion || !ids.Add(entry.RelationshipId) ||
                !subjects.Add(entry.Subject) || !sequences.Add(entry.LastChangedSequence))
            {
                return false;
            }
        }
        return snapshot.Relationships.Zip(snapshot.Relationships.Skip(1),
            (left, right) => left.LastChangedSequence > right.LastChangedSequence).All(value => value);
    }

    private static bool Valid(RelationshipApplicationSnapshot? snapshot, Guid expected)
    {
        return snapshot != null && snapshot.ApplicationId == expected && snapshot.ApplicationId != Guid.Empty &&
               snapshot.RelationshipId.IsValid && snapshot.Observer.IsValid && snapshot.Subject.IsValid &&
               snapshot.Observer != snapshot.Subject && snapshot.WorldEventId != Guid.Empty &&
               snapshot.RelationshipId == NCPersistentIdentity.Relationship(snapshot.Observer, snapshot.Subject) &&
               snapshot.ApplicationId == NCPersistentIdentity.RelationshipApplication(
                   snapshot.RelationshipId, snapshot.WorldEventId) &&
               !string.IsNullOrWhiteSpace(snapshot.EffectId) && snapshot.Magnitude > 0 &&
               snapshot.Initiator.IsValid &&
               snapshot.ResultingRelationshipVersion > 0 && snapshot.AppliedAt.IsValid &&
               snapshot.LastAppliedEventId != Guid.Empty;
    }

    private static NCRelationshipRecord? DeserializeRelationship(
        NCAggregateSnapshotData? stored,
        NCRelationshipId expected)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion || !stored.SessionEpoch.IsValid ||
            !stored.ActiveWorldTime.IsValid)
        {
            return null;
        }
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCRelationshipSnapshot>(stored.Payload, JsonOptions);
            return Valid(snapshot, expected) && snapshot!.DataVersion == stored.AggregateVersion
                ? new NCRelationshipRecord(snapshot!, stored.AggregateVersion, stored.SessionEpoch,
                    stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ObserverIndexRecord? DeserializeIndex(NCAggregateSnapshotData? stored, NCPartyRef expected)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion || !stored.SessionEpoch.IsValid ||
            !stored.ActiveWorldTime.IsValid)
        {
            return null;
        }
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCRelationshipIndexSnapshot>(stored.Payload, JsonOptions);
            return Valid(snapshot, expected) && snapshot!.DataVersion == stored.AggregateVersion
                ? new ObserverIndexRecord(snapshot!, stored.AggregateVersion, stored.SessionEpoch,
                    stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private NCRelationshipRecord CacheNewest(NCRelationshipRecord candidate) =>
        _cache.AddOrUpdate(candidate.Snapshot.RelationshipId, candidate,
            (_, current) => current.AggregateVersion > candidate.AggregateVersion ? current : candidate);

    private ObserverIndexRecord CacheNewest(ObserverIndexRecord candidate) =>
        _indexCache.AddOrUpdate(candidate.Snapshot.Observer, candidate,
            (_, current) => current.AggregateVersion > candidate.AggregateVersion ? current : candidate);

    private async Task<SemaphoreSlim[]> AcquireGates(Guid first, Guid second)
    {
        var indices = new[]
        {
            (first.GetHashCode() & int.MaxValue) % _gates.Length,
            (second.GetHashCode() & int.MaxValue) % _gates.Length,
        }.Distinct().Order().ToArray();
        var result = new SemaphoreSlim[indices.Length];
        for (var i = 0; i < indices.Length; i++)
        {
            result[i] = _gates[indices[i]];
            await result[i].WaitAsync();
        }
        return result;
    }

    private static void ReleaseGates(IEnumerable<SemaphoreSlim> gates)
    {
        foreach (var gate in gates.Reverse())
            gate.Release();
    }

    private static SemaphoreSlim[] CreateGates()
    {
        var gates = new SemaphoreSlim[MutationGateStripeCount];
        for (var i = 0; i < gates.Length; i++)
            gates[i] = new SemaphoreSlim(1, 1);
        return gates;
    }

    private static string RelationshipKey(NCRelationshipId relationshipId) => relationshipId.Value.ToString("D");

    private static string IndexKey(NCPartyRef observer) =>
        NCPersistentIdentity.RelationshipIndex(observer).ToString("D");

    private static NCRelationshipMutationResult Reject(NCRelationshipMutationStatus status, string error) =>
        new(status, null, error);

    private sealed record ObserverIndexRecord(
        NCRelationshipIndexSnapshot Snapshot,
        long AggregateVersion,
        NCSessionEpoch SessionEpoch,
        NCActiveWorldTime PersistedAt);

    private sealed record RelationshipApplicationSnapshot(
        Guid ApplicationId,
        NCRelationshipId RelationshipId,
        NCPartyRef Observer,
        NCPartyRef Subject,
        Guid WorldEventId,
        string EffectId,
        int Magnitude,
        NCPartyRef Initiator,
        long ResultingRelationshipVersion,
        NCActiveWorldTime AppliedAt,
        Guid LastAppliedEventId);

    private sealed record RelationshipEffectEvent(
        NCRelationshipId RelationshipId,
        NCPartyRef Observer,
        NCPartyRef Subject,
        Guid WorldEventId,
        string EffectId,
        int Magnitude,
        NCPartyRef Initiator,
        string AuditSource);

    private sealed record RelationshipIndexEvent(
        NCPartyRef Observer,
        NCRelationshipId RelationshipId,
        NCPartyRef Subject);

    private sealed record RelationshipReceipt(
        NCRelationshipSnapshot Snapshot,
        long AggregateVersion,
        NCSessionEpoch SessionEpoch,
        NCActiveWorldTime PersistedAt);

    private enum InitialCommandState : byte
    {
        Available,
        Completed,
        Failed,
        InProgress,
        Collision,
    }
}
