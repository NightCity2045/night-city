// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Cognition;

/// <summary>
/// Durable authority for stable beliefs and BDI commitment. Runtime Blackboard and planning frontier never cross
/// this boundary. Cognition and belief-set versions advance atomically to prevent decisions over split state.
/// </summary>
public sealed partial class NCCognitionRegistrySystem : EntitySystem
{
    public const string CognitionAggregateType = "living-city.actor-cognition";
    public const string BeliefSetAggregateType = "living-city.actor-beliefs";
    public const int SnapshotFormatVersion = 1;
    private const int MutationGateStripeCount = 64;
    private const int MaximumAuditSourceLength = 128;
    private const int MaximumBeliefs = 512;
    private const int MaximumStableTextLength = 256;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;

    private readonly ConcurrentDictionary<NCActorId, NCCognitionRecord> _cognitionCache = [];
    private readonly ConcurrentDictionary<NCActorId, NCBeliefSetRecord> _beliefCache = [];
    private readonly SemaphoreSlim[] _gates = CreateGates();

    public int CachedCognitionCount => _cognitionCache.Count;
    public int CachedBeliefSetCount => _beliefCache.Count;

    public bool Unload(NCActorId actorId) =>
        _cognitionCache.TryRemove(actorId, out _) | _beliefCache.TryRemove(actorId, out _);

    public async Task<NCCognitionRecord?> GetCognitionAsync(NCActorId actorId, bool forceReload = false)
    {
        if (!actorId.IsValid)
            return null;
        if (!forceReload && _cognitionCache.TryGetValue(actorId, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(CognitionAggregateType, ActorKey(actorId));
        var record = DeserializeCognition(stored, actorId);
        return record == null ? null : CacheNewest(record);
    }

    public async Task<NCBeliefSetRecord?> GetBeliefsAsync(NCActorId actorId, bool forceReload = false)
    {
        if (!actorId.IsValid)
            return null;
        if (!forceReload && _beliefCache.TryGetValue(actorId, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(BeliefSetAggregateType, ActorKey(actorId));
        var record = DeserializeBeliefs(stored, actorId);
        return record == null ? null : CacheNewest(record);
    }

    /// <summary>
    /// Adds mandatory cognition subdomains to the actor creation transaction.
    /// </summary>
    public IReadOnlyList<NCPersistentAggregateWrite> BuildInitialWrites(
        NCActorId actorId,
        NCCityActorArchetypePrototype archetype,
        NCActiveWorldTime now,
        string auditSource)
    {
        var cognitionEvent = Guid.NewGuid();
        var beliefEvent = Guid.NewGuid();
        var cognition = new NCCognitionSnapshot(
            actorId,
            archetype.CognitionDecisionPolicy.Id,
            archetype.CognitionCapabilityProfile.Id,
            archetype.CognitionRoutingProfile.Id,
            1,
            null,
            null,
            now,
            1,
            cognitionEvent);
        var beliefs = new NCBeliefSetSnapshot(actorId, [], 1, beliefEvent);
        return
        [
            Write(CognitionAggregateType, "living-city.actor-cognition.initialized", cognitionEvent,
                actorId, 0, cognition, auditSource),
            Write(BeliefSetAggregateType, "living-city.actor-beliefs.initialized", beliefEvent,
                actorId, 0, beliefs, auditSource),
        ];
    }

    public async Task<NCCognitionCommitResult> CommitAsync(NCCognitionCommitRequest request)
    {
        if (!ValidRequest(request, out var error))
            return Reject(NCCognitionMutationStatus.Rejected, error!);
        if (!_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject(NCCognitionMutationStatus.ClockUnavailable, "The active-world clock is not ready.");

        var gate = GetGate(request.ActorId);
        await gate.WaitAsync();
        try
        {
            var cognitionEvent = request.Cognition.LastEventId;
            var beliefEvent = request.BeliefSet.LastEventId;
            var receipt = JsonSerializer.Serialize(new CommitReceipt(
                request.Cognition,
                request.ExpectedCognitionVersion + 1,
                request.BeliefSet,
                request.ExpectedBeliefSetVersion + 1,
                epoch,
                now), JsonOptions);
            var writes = new NCPersistentAggregateWrite[]
            {
                Write(CognitionAggregateType, "living-city.actor-cognition.committed", cognitionEvent,
                    request.ActorId, request.ExpectedCognitionVersion, request.Cognition, request.AuditSource),
                Write(BeliefSetAggregateType, "living-city.actor-beliefs.projected", beliefEvent,
                    request.ActorId, request.ExpectedBeliefSetVersion, request.BeliefSet, request.AuditSource),
            };
            var persistence = await _database.ApplyNCPersistentBatchMutationAsync(
                new NCPersistentBatchMutationRequest(
                    request.CommandId,
                    "living-city.cognition.commit",
                    "actor",
                    ActorKey(request.ActorId),
                    writes,
                    receipt,
                    request.CorrelationId,
                    request.CausationId,
                    epoch,
                    now,
                    []));

            if (persistence.Status is NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted)
            {
                var decoded = DeserializeReceipt(persistence.ResultPayload ?? receipt, request.ActorId);
                if (decoded == null)
                    return Reject(NCCognitionMutationStatus.StorageFailure, "The cognition receipt is invalid.");
                var cognition = CacheNewest(new NCCognitionRecord(
                    decoded.Cognition, decoded.CognitionVersion, decoded.Epoch, decoded.PersistedAt));
                var beliefs = CacheNewest(new NCBeliefSetRecord(
                    decoded.Beliefs, decoded.BeliefVersion, decoded.Epoch, decoded.PersistedAt));
                return new NCCognitionCommitResult(
                    persistence.Status == NCPersistentMutationStatus.Applied
                        ? NCCognitionMutationStatus.Applied
                        : NCCognitionMutationStatus.Duplicate,
                    cognition,
                    beliefs,
                    null);
            }

            return Reject(persistence.Status switch
            {
                NCPersistentMutationStatus.VersionConflict => NCCognitionMutationStatus.VersionConflict,
                NCPersistentMutationStatus.ClockRejected => NCCognitionMutationStatus.ClockUnavailable,
                _ => NCCognitionMutationStatus.StorageFailure,
            }, persistence.Error ?? "The cognition transaction failed.");
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to commit cognition for {request.ActorId}: {exception}");
            return Reject(NCCognitionMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private static NCPersistentAggregateWrite Write<T>(
        string aggregateType,
        string eventType,
        Guid eventId,
        NCActorId actorId,
        long expectedVersion,
        T snapshot,
        string auditSource)
    {
        return new NCPersistentAggregateWrite(
            eventId,
            eventType,
            aggregateType,
            ActorKey(actorId),
            expectedVersion,
            SnapshotFormatVersion,
            JsonSerializer.Serialize(snapshot, JsonOptions),
            JsonSerializer.Serialize(new CognitionEvent<T>(actorId, eventType, auditSource, snapshot), JsonOptions));
    }

    private static bool ValidRequest(NCCognitionCommitRequest request, out string? error)
    {
        if (request.CommandId == Guid.Empty || request.CorrelationId == Guid.Empty || !request.ActorId.IsValid ||
            request.ExpectedCognitionVersion < 1 || request.ExpectedBeliefSetVersion < 1 ||
            string.IsNullOrWhiteSpace(request.AuditSource) ||
            request.AuditSource.Length > MaximumAuditSourceLength ||
            !ValidCognition(request.Cognition, request.ActorId) ||
            !ValidBeliefs(request.BeliefSet, request.ActorId) ||
            request.Cognition.BeliefSetVersion != request.ExpectedBeliefSetVersion + 1)
        {
            error = "The cognition commit is invalid or exceeds its bounded schema.";
            return false;
        }
        error = null;
        return true;
    }

    private static bool ValidCognition(NCCognitionSnapshot? value, NCActorId actorId)
    {
        return value != null && value.ActorId == actorId && value.DataVersion >= 1 &&
               value.LastEventId != Guid.Empty && value.NextDecisionAt.IsValid &&
               Stable(value.DecisionPolicyId) && Stable(value.CapabilityProfileId) && Stable(value.RoutingProfileId) &&
               ValidDesire(value.CurrentDesire) && ValidIntention(value.CurrentIntention) &&
               (value.CurrentDesire == null || value.CurrentIntention == null ||
                value.CurrentDesire.CapabilityId == value.CurrentIntention.CapabilityId);
    }

    private static bool ValidBeliefs(NCBeliefSetSnapshot? value, NCActorId actorId)
    {
        if (value == null || value.ActorId != actorId || value.Beliefs == null ||
            value.Beliefs.Length > MaximumBeliefs || value.DataVersion < 1 || value.LastEventId == Guid.Empty)
            return false;
        return value.Beliefs.All(belief => belief != null && belief.BeliefId != Guid.Empty &&
            Stable(belief.Key) && Stable(belief.Source) && belief.SourceVersion >= 0 &&
            float.IsFinite(belief.Confidence) && belief.Confidence is >= 0f and <= 1f &&
            belief.ObservedAt.IsValid && belief.ExpiresAt >= belief.ObservedAt && belief.Revision >= 1 &&
            ValidSubject(belief.Subject) && ValidValue(belief.Value));
    }

    private static bool ValidDesire(NCCognitionDesireSnapshot? value) => value == null ||
        value.DesireId != Guid.Empty && Stable(value.CapabilityId) && ValidSubject(value.Target) &&
        float.IsFinite(value.Utility) && value.ExpiresAt >= value.EvaluatedAt;

    private static bool ValidIntention(NCCognitionIntentionSnapshot? value) => value == null ||
        value.IntentionId != Guid.Empty && Stable(value.CapabilityId) && ValidSubject(value.Target) &&
        float.IsFinite(value.UtilityAtCommitment) && float.IsFinite(value.Commitment) &&
        value.Commitment is >= 0f and <= 1f && value.MinimumCommitmentUntil >= value.CreatedAt &&
        value.ExpiresAt >= value.MinimumCommitmentUntil && value.Revision >= 1;

    private static bool ValidSubject(NCCognitionSubjectRef subject) =>
        subject.Kind == NCCognitionReferenceKind.None
            ? string.IsNullOrEmpty(subject.StableId)
            : subject.IsValid && subject.StableId.Length <= MaximumStableTextLength;

    private static bool ValidValue(NCCognitionValue value) => value.Kind switch
    {
        NCCognitionValueKind.Boolean => true,
        NCCognitionValueKind.Number => float.IsFinite(value.Number),
        NCCognitionValueKind.StableKey => Stable(value.StableKey),
        _ => false,
    };

    private static bool Stable(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumStableTextLength;

    private static NCCognitionRecord? DeserializeCognition(NCAggregateSnapshotData? stored, NCActorId actorId)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var value = JsonSerializer.Deserialize<NCCognitionSnapshot>(stored.Payload, JsonOptions);
            return ValidCognition(value, actorId)
                ? new NCCognitionRecord(value!, stored.AggregateVersion, stored.SessionEpoch, stored.ActiveWorldTime)
                : null;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static NCBeliefSetRecord? DeserializeBeliefs(NCAggregateSnapshotData? stored, NCActorId actorId)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var value = JsonSerializer.Deserialize<NCBeliefSetSnapshot>(stored.Payload, JsonOptions);
            return ValidBeliefs(value, actorId)
                ? new NCBeliefSetRecord(value!, stored.AggregateVersion, stored.SessionEpoch, stored.ActiveWorldTime)
                : null;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static CommitReceipt? DeserializeReceipt(string? payload, NCActorId actorId)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;
        try
        {
            var value = JsonSerializer.Deserialize<CommitReceipt>(payload, JsonOptions);
            return value != null && value.CognitionVersion >= 1 && value.BeliefVersion >= 1 &&
                   value.Epoch.IsValid && value.PersistedAt.IsValid &&
                   ValidCognition(value.Cognition, actorId) && ValidBeliefs(value.Beliefs, actorId)
                ? value
                : null;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private NCCognitionRecord CacheNewest(NCCognitionRecord value) =>
        _cognitionCache.AddOrUpdate(value.Snapshot.ActorId, value,
            (_, current) => current.AggregateVersion > value.AggregateVersion ? current : value);

    private NCBeliefSetRecord CacheNewest(NCBeliefSetRecord value) =>
        _beliefCache.AddOrUpdate(value.Snapshot.ActorId, value,
            (_, current) => current.AggregateVersion > value.AggregateVersion ? current : value);

    private SemaphoreSlim GetGate(NCActorId actorId) =>
        _gates[(actorId.Value.GetHashCode() & int.MaxValue) % _gates.Length];

    private static SemaphoreSlim[] CreateGates()
    {
        var values = new SemaphoreSlim[MutationGateStripeCount];
        for (var index = 0; index < values.Length; index++)
            values[index] = new SemaphoreSlim(1, 1);
        return values;
    }

    private static string ActorKey(NCActorId actorId) => actorId.Value.ToString("D");

    private static NCCognitionCommitResult Reject(NCCognitionMutationStatus status, string error) =>
        new(status, null, null, error);

    private sealed record CognitionEvent<T>(NCActorId ActorId, string EventType, string AuditSource, T Snapshot);
    private sealed record CommitReceipt(
        NCCognitionSnapshot Cognition,
        long CognitionVersion,
        NCBeliefSetSnapshot Beliefs,
        long BeliefVersion,
        NCSessionEpoch Epoch,
        NCActiveWorldTime PersistedAt);
}
