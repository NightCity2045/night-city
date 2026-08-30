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

public enum NCRumorMutationStatus : byte
{
    Applied,
    Duplicate,
    PartyUnavailable,
    ClockUnavailable,
    VersionConflict,
    Rejected,
    StorageFailure,
}

public sealed record NCRumorBookRecord(
    NCRumorBookSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCRumorMutationResult(
    NCRumorMutationStatus Status,
    NCRumorBookRecord? Book,
    NCRumorKnowledgeSnapshot? Knowledge,
    string? Error);

/// <summary>
/// Bounded per-party information knowledge. Records always retain event provenance and confidence; they are never
/// promoted to objective truth. Propagation is an idempotent two-aggregate write into only the target's book.
/// </summary>
public sealed partial class NCRumorRegistrySystem : EntitySystem
{
    public const string BookAggregateType = "living-city.rumor-book";
    public const string ApplicationAggregateType = "living-city.rumor-propagation";
    public const int SnapshotFormatVersion = 1;

    private static readonly ProtoId<NCRumorPolicyPrototype> DefaultPolicy = "Default";
    private const int GateStripeCount = 64;
    private const int MaximumAuditSourceLength = 128;
    private const int MaximumStableKeyLength = 96;
    private const int AbsoluteMaximumKnowledge = 1_024;
    private const int AbsoluteMaximumSourceEvents = 32;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPartyResolverSystem _parties = default!;

    private readonly ConcurrentDictionary<NCPartyRef, NCRumorBookRecord> _cache = [];
    private readonly SemaphoreSlim[] _gates = CreateGates();

    public int CachedBookCount => _cache.Count;

    public bool Unload(NCPartyRef owner) => _cache.TryRemove(owner, out _);

    public async Task<NCRumorBookRecord?> GetAsync(NCPartyRef owner, bool forceReload = false)
    {
        if (!owner.IsValid)
            return null;
        if (!forceReload && _cache.TryGetValue(owner, out var cached))
            return cached;
        var stored = await _database.GetNCAggregateSnapshotAsync(BookAggregateType, BookKey(owner));
        var record = DeserializeBook(stored, owner);
        return record == null ? null : CacheNewest(record);
    }

    public async Task<IReadOnlyList<NCRumorKnowledgeSnapshot>> GetKnownAsync(
        NCPartyRef owner,
        NCMemoryPrivacy maximumPrivacy,
        int limit,
        bool forceReload = false)
    {
        if (!TryGetPolicy(out var policy) || !Enum.IsDefined(maximumPrivacy) ||
            limit is < 1 || limit > policy.MaximumQueryCount)
        {
            return [];
        }
        var book = await GetAsync(owner, forceReload);
        if (book == null)
            return [];
        var nowAvailable = _clock.TryGetCurrentTime(out _, out var now);
        return book.Snapshot.Knowledge
            .Where(value => value.Privacy <= maximumPrivacy &&
                (!nowAvailable || value.ExpiresAt is not { } expiry || expiry > now))
            .OrderByDescending(value => value.Confidence)
            .ThenByDescending(value => value.LearnedAt.Ticks)
            .ThenBy(value => value.KnowledgeId.Value)
            .Take(limit)
            .ToArray();
    }

    public async Task<NCRumorMutationResult> LearnAsync(NCLearnInformationRequest request)
    {
        if (!TryValidateLearn(request, out var topic, out var policy, out var error) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(_clock.IsReady ? NCRumorMutationStatus.Rejected : NCRumorMutationStatus.ClockUnavailable,
                error ?? "The active-world clock is not ready.");
        }

        var resolutions = await ResolveParties(request.Owner, request.Originator, request.Initiator);
        if (!resolutions)
            return Reject(NCRumorMutationStatus.PartyUnavailable, "Owner, originator, or initiator is unavailable.");

        var informationId = NCPersistentIdentity.Information(
            topic.StableKey,
            request.Subject,
            request.SourceWorldEventIds[0]);
        var knowledgeId = NCPersistentIdentity.RumorKnowledge(request.Owner, informationId);
        var gate = Gate(request.Owner);
        await gate.WaitAsync();
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var current = await GetAsync(request.Owner, true);
                var existing = current?.Snapshot.Knowledge.SingleOrDefault(value => value.KnowledgeId == knowledgeId);
                if (existing != null)
                {
                    return SameLearn(existing, request, topic)
                        ? new NCRumorMutationResult(NCRumorMutationStatus.Duplicate, current, existing, null)
                        : Reject(NCRumorMutationStatus.Rejected,
                            "The stable information identity is bound to different immutable data.");
                }

                var eventId = Guid.NewGuid();
                var confidence = request.Confidence ?? topic.DefaultConfidence;
                var privacy = request.Privacy ?? topic.DefaultPrivacy;
                var knowledge = new NCRumorKnowledgeSnapshot(
                    knowledgeId,
                    informationId,
                    request.Owner,
                    Normalize(topic.StableKey),
                    Normalize(request.Subject),
                    [request.SourceWorldEventIds[0], .. request.SourceWorldEventIds.Skip(1).Order()],
                    request.SourceKind,
                    request.Originator,
                    default,
                    confidence,
                    0,
                    0,
                    privacy,
                    topic.Critical,
                    now,
                    now,
                    topic.Lifetime == TimeSpan.Zero ? null : now.Advance(topic.Lifetime),
                    1,
                    eventId);
                if (!TryAdd(current?.Snapshot, request.Owner, knowledge, policy, now, false, eventId,
                    out var next, out error))
                {
                    return Reject(NCRumorMutationStatus.Rejected, error!);
                }

                var result = await Persist(
                    request.CommandId,
                    "living-city.rumor.learn",
                    knowledgeId.ToString(),
                    [BookWrite(current, next, eventId, "living-city.rumor.learned", request.AuditSource)],
                    new RumorReceipt(knowledgeId, (current?.AggregateVersion ?? 0) + 1),
                    request.CorrelationId,
                    request.CausationId,
                    epoch,
                    now);
                if (result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted)
                {
                    var persisted = await GetAsync(request.Owner, true);
                    var persistedKnowledge = persisted?.Snapshot.Knowledge.SingleOrDefault(
                        value => value.KnowledgeId == knowledgeId);
                    return persisted == null || persistedKnowledge == null
                        ? Reject(NCRumorMutationStatus.StorageFailure,
                            "Committed information could not be reloaded.")
                        : new NCRumorMutationResult(
                            result.Status == NCPersistentMutationStatus.Applied
                                ? NCRumorMutationStatus.Applied
                                : NCRumorMutationStatus.Duplicate,
                            persisted,
                            persistedKnowledge,
                            null);
                }
                if (result.Status is NCPersistentMutationStatus.VersionConflict or
                    NCPersistentMutationStatus.DuplicateFailed)
                {
                    continue;
                }
                return FromPersistence(result);
            }
            return Reject(NCRumorMutationStatus.VersionConflict,
                "Information learning exceeded its optimistic retry limit.");
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to learn information {knowledgeId}: {exception}");
            return Reject(NCRumorMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NCRumorMutationResult> PropagateAsync(NCPropagateInformationRequest request)
    {
        if (!TryValidatePropagation(request, out var policy, out var error) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(_clock.IsReady ? NCRumorMutationStatus.Rejected : NCRumorMutationStatus.ClockUnavailable,
                error ?? "The active-world clock is not ready.");
        }
        if (!await ResolveParties(request.SourceOwner, request.TargetOwner, request.Initiator))
            return Reject(NCRumorMutationStatus.PartyUnavailable, "Source, target, or initiator is unavailable.");

        var sourceBook = await GetAsync(request.SourceOwner, true);
        var source = sourceBook?.Snapshot.Knowledge.SingleOrDefault(
            value => value.KnowledgeId == request.SourceKnowledgeId);
        if (source == null || source.Owner != request.SourceOwner ||
            source.ExpiresAt is { } sourceExpiry && sourceExpiry <= now || source.HopCount >= policy.MaximumHopCount)
        {
            return Reject(NCRumorMutationStatus.Rejected, "The source does not hold propagatable information.");
        }

        var confidence = checked(source.Confidence * policy.ConfidenceRetentionPermille / 1_000);
        if (confidence < policy.MinimumPropagationConfidence)
            return Reject(NCRumorMutationStatus.Rejected, "The information confidence is below propagation policy.");

        var knowledgeId = NCPersistentIdentity.RumorKnowledge(request.TargetOwner, source.InformationId);
        var applicationId = NCPersistentIdentity.RumorPropagationApplication(
            knowledgeId,
            request.PropagationEventId);
        var gate = Gate(request.TargetOwner);
        await gate.WaitAsync();
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var priorApplication = await GetApplicationAsync(applicationId);
                if (priorApplication != null)
                {
                    return SamePropagation(priorApplication, request, source.InformationId, knowledgeId)
                        ? await ReplayPropagation(priorApplication, request.TargetOwner, knowledgeId)
                        : Reject(NCRumorMutationStatus.Rejected,
                            "The propagation identity is bound to different immutable data.");
                }

                var current = await GetAsync(request.TargetOwner, true);
                var existing = current?.Snapshot.Knowledge.SingleOrDefault(value => value.KnowledgeId == knowledgeId);
                var eventId = Guid.NewGuid();
                NCRumorBookSnapshot next;
                if (existing == null)
                {
                    var propagated = source with
                    {
                        KnowledgeId = knowledgeId,
                        Owner = request.TargetOwner,
                        SourceKind = request.Channel,
                        ImmediateSource = request.SourceOwner,
                        Confidence = confidence,
                        Distortion = Math.Min(1_000, checked(source.Distortion + policy.DistortionPerHop)),
                        HopCount = source.HopCount + 1,
                        LearnedAt = now,
                        DataVersion = 1,
                        LastAppliedEventId = eventId,
                    };
                    if (!TryAdd(current?.Snapshot, request.TargetOwner, propagated, policy, now, true, eventId,
                        out next, out error))
                    {
                        return Reject(NCRumorMutationStatus.Rejected, error!);
                    }
                }
                else
                {
                    next = current!.Snapshot;
                }

                var resultingVersion = (current?.AggregateVersion ?? 0) + (existing == null ? 1 : 0);
                var applicationEvent = Guid.NewGuid();
                var marker = new PropagationApplication(
                    applicationId,
                    knowledgeId,
                    source.InformationId,
                    request.SourceOwner,
                    request.TargetOwner,
                    request.SourceKnowledgeId,
                    request.Channel,
                    request.PropagationEventId,
                    resultingVersion,
                    now,
                    applicationEvent);
                var writes = new List<NCPersistentAggregateWrite>();
                if (existing == null)
                    writes.Add(BookWrite(current, next, eventId, "living-city.rumor.propagated", request.AuditSource));
                writes.Add(new NCPersistentAggregateWrite(
                    applicationEvent,
                    "living-city.rumor.propagation-recorded",
                    ApplicationAggregateType,
                    applicationId.ToString("D"),
                    0,
                    SnapshotFormatVersion,
                    JsonSerializer.Serialize(marker, JsonOptions),
                    JsonSerializer.Serialize(marker, JsonOptions)));

                var result = await Persist(
                    request.CommandId,
                    "living-city.rumor.propagate",
                    applicationId.ToString("D"),
                    writes,
                    new RumorReceipt(knowledgeId, resultingVersion),
                    request.CorrelationId,
                    request.CausationId,
                    epoch,
                    now);
                if (result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted)
                {
                    var persisted = await GetAsync(request.TargetOwner, true);
                    var persistedKnowledge = persisted?.Snapshot.Knowledge.SingleOrDefault(
                        value => value.KnowledgeId == knowledgeId);
                    return persisted == null || persistedKnowledge == null
                        ? Reject(NCRumorMutationStatus.StorageFailure,
                            "Committed propagated information could not be reloaded.")
                        : new NCRumorMutationResult(
                            existing == null && result.Status == NCPersistentMutationStatus.Applied
                                ? NCRumorMutationStatus.Applied
                                : NCRumorMutationStatus.Duplicate,
                            persisted,
                            persistedKnowledge,
                            null);
                }
                if (result.Status is NCPersistentMutationStatus.VersionConflict or
                    NCPersistentMutationStatus.DuplicateFailed)
                {
                    continue;
                }
                return FromPersistence(result);
            }
            return Reject(NCRumorMutationStatus.VersionConflict,
                "Information propagation exceeded its optimistic retry limit.");
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to propagate information {request.SourceKnowledgeId}: {exception}");
            return Reject(NCRumorMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    public bool ValidatePrototypes(List<string> errors)
    {
        var initial = errors.Count;
        foreach (var topic in _prototypes.EnumeratePrototypes<NCRumorTopicPrototype>())
        {
            if (!Valid(topic))
                errors.Add($"Rumor topic {topic.ID} is invalid.");
        }
        foreach (var policy in _prototypes.EnumeratePrototypes<NCRumorPolicyPrototype>())
        {
            if (!Valid(policy))
                errors.Add($"Rumor policy {policy.ID} is invalid.");
        }
        if (!TryGetPolicy(out _))
            errors.Add("The Default rumor policy is missing or invalid.");
        return errors.Count == initial;
    }

    private async Task<bool> ResolveParties(NCPartyRef first, NCPartyRef second, NCPartyRef third)
    {
        var firstTask = _parties.ResolveAsync(first, true);
        var secondTask = _parties.ResolveAsync(second, true);
        var thirdTask = _parties.ResolveAsync(third, true);
        await Task.WhenAll(firstTask, secondTask, thirdTask);
        return (await firstTask).IsResolved && (await secondTask).IsResolved && (await thirdTask).IsResolved;
    }

    private static bool TryAdd(
        NCRumorBookSnapshot? current,
        NCPartyRef owner,
        NCRumorKnowledgeSnapshot knowledge,
        NCRumorPolicyPrototype policy,
        NCActiveWorldTime now,
        bool propagated,
        Guid eventId,
        out NCRumorBookSnapshot next,
        out string? error)
    {
        var values = current?.Knowledge
            .Where(value => value.Critical || value.ExpiresAt is not { } expiry || expiry > now)
            .ToList() ?? [];
        var forgotten = (current?.Knowledge.Count ?? 0) - values.Count;
        while (values.Count >= policy.MaximumKnowledgePerOwner)
        {
            var removable = values
                .Where(value => !value.Critical)
                .OrderBy(value => value.Confidence)
                .ThenBy(value => value.LearnedAt.Ticks)
                .ThenBy(value => value.KnowledgeId.Value)
                .FirstOrDefault();
            if (removable == null)
            {
                next = default!;
                error = "The rumor book contains only protected critical information.";
                return false;
            }
            values.Remove(removable);
            forgotten++;
            if (values.Count <= policy.TargetKnowledgeAfterPrune)
                break;
        }
        values.Add(knowledge);
        values.Sort((left, right) =>
        {
            var learned = right.LearnedAt.Ticks.CompareTo(left.LearnedAt.Ticks);
            return learned != 0 ? learned : left.KnowledgeId.Value.CompareTo(right.KnowledgeId.Value);
        });
        next = new NCRumorBookSnapshot(
            owner,
            values,
            checked((current?.TotalLearned ?? 0) + 1),
            checked((current?.TotalPropagated ?? 0) + (propagated ? 1 : 0)),
            checked((current?.TotalForgotten ?? 0) + forgotten),
            checked((current?.DataVersion ?? 0) + 1),
            eventId);
        error = null;
        return true;
    }

    private NCPersistentAggregateWrite BookWrite(
        NCRumorBookRecord? current,
        NCRumorBookSnapshot next,
        Guid eventId,
        string eventType,
        string auditSource) => new(
            eventId,
            eventType,
            BookAggregateType,
            BookKey(next.Owner),
            current?.AggregateVersion ?? 0,
            SnapshotFormatVersion,
            JsonSerializer.Serialize(next, JsonOptions),
            JsonSerializer.Serialize(new RumorBookEvent(next.Owner, next.LastAppliedEventId, auditSource.Trim()),
                JsonOptions));

    private Task<NCPersistentBatchMutationResult> Persist(
        Guid commandId,
        string commandType,
        string scopeId,
        IReadOnlyList<NCPersistentAggregateWrite> writes,
        RumorReceipt receipt,
        Guid correlationId,
        Guid? causationId,
        NCSessionEpoch epoch,
        NCActiveWorldTime now) => _database.ApplyNCPersistentBatchMutationAsync(
        new NCPersistentBatchMutationRequest(
            commandId,
            commandType,
            "rumor",
            scopeId,
            writes,
            JsonSerializer.Serialize(receipt, JsonOptions),
            correlationId,
            causationId,
            epoch,
            now,
            []));

    private async Task<PropagationApplication?> GetApplicationAsync(Guid applicationId)
    {
        var stored = await _database.GetNCAggregateSnapshotAsync(
            ApplicationAggregateType,
            applicationId.ToString("D"));
        if (stored == null)
            return null;
        if (stored.FormatVersion != SnapshotFormatVersion || stored.AggregateVersion != 1 ||
            !stored.SessionEpoch.IsValid || !stored.ActiveWorldTime.IsValid)
        {
            throw new InvalidOperationException("The rumor propagation application metadata is invalid.");
        }
        var marker = JsonSerializer.Deserialize<PropagationApplication>(stored.Payload, JsonOptions);
        return Valid(marker, applicationId) && marker!.AppliedAt == stored.ActiveWorldTime
            ? marker
            : throw new InvalidOperationException("The rumor propagation application is invalid.");
    }

    private async Task<NCRumorMutationResult> ReplayPropagation(
        PropagationApplication application,
        NCPartyRef target,
        NCRumorKnowledgeId knowledgeId)
    {
        var current = await GetAsync(target, true);
        var knowledge = current?.Snapshot.Knowledge.SingleOrDefault(value => value.KnowledgeId == knowledgeId);
        return current == null || knowledge == null || current.AggregateVersion < application.ResultingBookVersion
            ? Reject(NCRumorMutationStatus.StorageFailure,
                "The propagation marker exists without its resulting target knowledge.")
            : new NCRumorMutationResult(NCRumorMutationStatus.Duplicate, current, knowledge, null);
    }

    private bool TryValidateLearn(
        NCLearnInformationRequest request,
        out NCRumorTopicPrototype topic,
        out NCRumorPolicyPrototype policy,
        out string? error)
    {
        topic = default!;
        policy = default!;
        error = null;
        if (!TryGetPolicy(out policy) || request.CommandId == Guid.Empty || !request.Owner.IsValid ||
            !request.Originator.IsValid || !request.Initiator.IsValid || !Valid(request.Subject) ||
            request.SourceWorldEventIds.Count is < 1 ||
            request.SourceWorldEventIds.Count > policy.MaximumSourceEvents ||
            request.SourceWorldEventIds.Any(value => value == Guid.Empty) ||
            request.SourceWorldEventIds.Distinct().Count() != request.SourceWorldEventIds.Count ||
            !Enum.IsDefined(request.SourceKind) || request.CorrelationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.AuditSource) ||
            request.AuditSource.Trim().Length > MaximumAuditSourceLength ||
            request.Confidence is < 0 or > 1_000 || request.Privacy is { } privacy && !Enum.IsDefined(privacy) ||
            !_prototypes.TryIndex(request.Topic, out NCRumorTopicPrototype? resolved) || resolved == null ||
            !Valid(resolved))
        {
            error = "The information learning request or topic is invalid.";
            return false;
        }
        topic = resolved;
        return true;
    }

    private bool TryValidatePropagation(
        NCPropagateInformationRequest request,
        out NCRumorPolicyPrototype policy,
        out string? error)
    {
        error = null;
        if (!TryGetPolicy(out policy) || request.CommandId == Guid.Empty || !request.SourceOwner.IsValid ||
            !request.TargetOwner.IsValid || request.SourceOwner == request.TargetOwner ||
            !request.SourceKnowledgeId.IsValid || !Enum.IsDefined(request.Channel) ||
            request.Channel == NCRumorSourceKind.DirectWitness || request.PropagationEventId == Guid.Empty ||
            !request.Initiator.IsValid || request.CorrelationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.AuditSource) ||
            request.AuditSource.Trim().Length > MaximumAuditSourceLength)
        {
            error = "The information propagation request is invalid.";
            return false;
        }
        return true;
    }

    private bool TryGetPolicy(out NCRumorPolicyPrototype policy) =>
        _prototypes.TryIndex(DefaultPolicy, out policy!) && Valid(policy);

    private static bool SameLearn(
        NCRumorKnowledgeSnapshot existing,
        NCLearnInformationRequest request,
        NCRumorTopicPrototype topic) => existing.Owner == request.Owner &&
        existing.TopicKey == Normalize(topic.StableKey) && existing.Subject == Normalize(request.Subject) &&
        existing.SourceWorldEventIds.SequenceEqual(
            new[] { request.SourceWorldEventIds[0] }.Concat(request.SourceWorldEventIds.Skip(1).Order())) &&
        existing.SourceKind == request.SourceKind && existing.Originator == request.Originator;

    private static bool SamePropagation(
        PropagationApplication value,
        NCPropagateInformationRequest request,
        NCInformationId informationId,
        NCRumorKnowledgeId knowledgeId) => value.KnowledgeId == knowledgeId &&
        value.InformationId == informationId && value.SourceOwner == request.SourceOwner &&
        value.TargetOwner == request.TargetOwner && value.SourceKnowledgeId == request.SourceKnowledgeId &&
        value.Channel == request.Channel && value.PropagationEventId == request.PropagationEventId;

    private static bool Valid(NCRumorPolicyPrototype value) =>
        value.MaximumKnowledgePerOwner is > 0 and <= AbsoluteMaximumKnowledge &&
        value.TargetKnowledgeAfterPrune is > 0 &&
        value.TargetKnowledgeAfterPrune < value.MaximumKnowledgePerOwner &&
        value.MaximumSourceEvents is > 0 and <= AbsoluteMaximumSourceEvents &&
        value.MaximumQueryCount is > 0 && value.MaximumQueryCount <= value.MaximumKnowledgePerOwner &&
        value.MaximumHopCount is > 0 and <= 32 && value.ConfidenceRetentionPermille is > 0 and <= 1_000 &&
        value.DistortionPerHop is >= 0 and <= 1_000 && value.MinimumPropagationConfidence is > 0 and <= 1_000;

    private static bool Valid(NCRumorTopicPrototype value) => IsStableKey(value.StableKey) &&
        value.DefaultConfidence is >= 0 and <= 1_000 && Enum.IsDefined(value.DefaultPrivacy) &&
        value.Lifetime >= TimeSpan.Zero;

    private static bool Valid(NCInformationSubject value) => value.IsValid &&
        value.StableId.Length <= 128 && value.StableId == value.StableId.Trim().ToLowerInvariant();

    private static bool Valid(NCRumorBookSnapshot? value, NCPartyRef owner) => value != null &&
        value.Owner == owner && owner.IsValid && value.Knowledge.Count is > 0 and <= AbsoluteMaximumKnowledge &&
        value.Knowledge.Select(item => item.KnowledgeId).Distinct().Count() == value.Knowledge.Count &&
        value.Knowledge.Select(item => item.InformationId).Distinct().Count() == value.Knowledge.Count &&
        value.Knowledge.All(item => Valid(item, owner)) && value.TotalLearned >= value.Knowledge.Count &&
        value.TotalPropagated >= 0 && value.TotalForgotten >= 0 && value.DataVersion > 0 &&
        value.LastAppliedEventId != Guid.Empty;

    private static bool Valid(NCRumorKnowledgeSnapshot value, NCPartyRef owner) => value.KnowledgeId.IsValid &&
        value.InformationId.IsValid && value.Owner == owner && IsStableKey(value.TopicKey) && Valid(value.Subject) &&
        value.SourceWorldEventIds.Count is > 0 and <= AbsoluteMaximumSourceEvents &&
        value.SourceWorldEventIds.All(id => id != Guid.Empty) &&
        value.SourceWorldEventIds.Distinct().Count() == value.SourceWorldEventIds.Count &&
        Enum.IsDefined(value.SourceKind) && value.Originator.IsValid &&
        (value.HopCount == 0 ? !value.ImmediateSource.IsValid : value.ImmediateSource.IsValid) &&
        value.Confidence is >= 0 and <= 1_000 && value.Distortion is >= 0 and <= 1_000 &&
        value.HopCount is >= 0 and <= 32 && Enum.IsDefined(value.Privacy) && value.OriginatedAt.IsValid &&
        value.LearnedAt >= value.OriginatedAt &&
        (value.ExpiresAt == null || value.ExpiresAt >= value.OriginatedAt) && value.DataVersion > 0 &&
        value.LastAppliedEventId != Guid.Empty &&
        value.InformationId == NCPersistentIdentity.Information(
            value.TopicKey, value.Subject, value.SourceWorldEventIds[0]) &&
        value.KnowledgeId == NCPersistentIdentity.RumorKnowledge(owner, value.InformationId);

    private static bool Valid(PropagationApplication? value, Guid expected) => value != null &&
        value.ApplicationId == expected && value.KnowledgeId.IsValid && value.InformationId.IsValid &&
        value.SourceOwner.IsValid && value.TargetOwner.IsValid && value.SourceOwner != value.TargetOwner &&
        value.SourceKnowledgeId.IsValid && Enum.IsDefined(value.Channel) &&
        value.Channel != NCRumorSourceKind.DirectWitness && value.PropagationEventId != Guid.Empty &&
        value.ResultingBookVersion > 0 && value.AppliedAt.IsValid && value.LastAppliedEventId != Guid.Empty &&
        value.ApplicationId == NCPersistentIdentity.RumorPropagationApplication(
            value.KnowledgeId, value.PropagationEventId);

    private static NCRumorBookRecord? DeserializeBook(NCAggregateSnapshotData? stored, NCPartyRef owner)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion || !stored.SessionEpoch.IsValid ||
            !stored.ActiveWorldTime.IsValid)
        {
            return null;
        }
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCRumorBookSnapshot>(stored.Payload, JsonOptions);
            return Valid(snapshot, owner) && snapshot!.DataVersion == stored.AggregateVersion
                ? new NCRumorBookRecord(snapshot, stored.AggregateVersion, stored.SessionEpoch,
                    stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private NCRumorBookRecord CacheNewest(NCRumorBookRecord candidate) =>
        _cache.AddOrUpdate(candidate.Snapshot.Owner, candidate,
            (_, current) => current.AggregateVersion > candidate.AggregateVersion ? current : candidate);

    private static NCInformationSubject Normalize(NCInformationSubject value) =>
        value with { StableId = value.StableId.Trim().ToLowerInvariant() };
    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
    private static bool IsStableKey(string value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumStableKeyLength && value == Normalize(value) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    private static string BookKey(NCPartyRef owner) => NCPersistentIdentity.RumorBook(owner).ToString("D");
    private SemaphoreSlim Gate(NCPartyRef owner) => _gates[(owner.GetHashCode() & int.MaxValue) % _gates.Length];

    private static SemaphoreSlim[] CreateGates()
    {
        var gates = new SemaphoreSlim[GateStripeCount];
        for (var index = 0; index < gates.Length; index++)
            gates[index] = new SemaphoreSlim(1, 1);
        return gates;
    }

    private static NCRumorMutationResult FromPersistence(NCPersistentBatchMutationResult result) => Reject(
        result.Status == NCPersistentMutationStatus.ClockRejected
            ? NCRumorMutationStatus.ClockUnavailable
            : NCRumorMutationStatus.StorageFailure,
        result.Error ?? "The rumor mutation was not committed.");

    private static NCRumorMutationResult Reject(NCRumorMutationStatus status, string error) =>
        new(status, null, null, error);

    private sealed record RumorReceipt(NCRumorKnowledgeId KnowledgeId, long ResultingBookVersion);
    private sealed record RumorBookEvent(NCPartyRef Owner, Guid AppliedEventId, string AuditSource);
    private sealed record PropagationApplication(
        Guid ApplicationId,
        NCRumorKnowledgeId KnowledgeId,
        NCInformationId InformationId,
        NCPartyRef SourceOwner,
        NCPartyRef TargetOwner,
        NCRumorKnowledgeId SourceKnowledgeId,
        NCRumorSourceKind Channel,
        Guid PropagationEventId,
        long ResultingBookVersion,
        NCActiveWorldTime AppliedAt,
        Guid LastAppliedEventId);
}
