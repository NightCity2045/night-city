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

public enum NCReputationMutationStatus : byte
{
    Applied,
    Duplicate,
    PartyUnavailable,
    VersionConflict,
    ClockUnavailable,
    Rejected,
    StorageFailure,
}

public sealed record NCReputationRecord(
    NCReputationSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCReputationMutationResult(
    NCReputationMutationStatus Status,
    NCReputationRecord? Reputation,
    string? Error);

/// <summary>
/// Canonical reputation authority. It stores bounded per-subject projections and exact world-event application markers;
/// relationships, rumors, neural context, and materialized entities may consume the projection but cannot mutate it.
/// </summary>
public sealed partial class NCReputationRegistrySystem : EntitySystem
{
    public const string ReputationAggregateType = "living-city.reputation";
    public const string ApplicationAggregateType = "living-city.reputation-application";
    public const int SnapshotFormatVersion = 1;

    private static readonly ProtoId<NCReputationPolicyPrototype> DefaultPolicy = "Default";

    private const string CommandType = "living-city.reputation.apply-effect";
    private const string ScopeType = "reputation-application";
    private const int MutationGateStripeCount = 64;
    private const int MaximumAuditSourceLength = 128;
    private const int MaximumStableKeyLength = 128;
    private const int AbsoluteMaximumScore = 1_000_000;
    private const int AbsoluteMaximumEvidence = 1_000_000_000;
    private const int AbsoluteMaximumEntries = 4_096;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPartyResolverSystem _parties = default!;

    private readonly ConcurrentDictionary<NCReputationId, NCReputationRecord> _cache = [];
    private readonly SemaphoreSlim[] _gates = CreateGates();

    public int CachedReputationCount => _cache.Count;

    public bool ValidatePrototypes(List<string> errors)
    {
        var initialCount = errors.Count;
        var policies = _prototypes.EnumeratePrototypes<NCReputationPolicyPrototype>().ToArray();
        foreach (var policy in policies)
        {
            if (!ValidPolicy(policy))
                errors.Add($"Reputation policy {policy.ID} has invalid bounds.");
        }

        if (!_prototypes.TryIndex(DefaultPolicy, out NCReputationPolicyPrototype? activePolicy) ||
            activePolicy == null || !ValidPolicy(activePolicy))
        {
            errors.Add("The Default reputation policy is missing or invalid.");
            return false;
        }

        var stableKeys = new HashSet<(NCReputationChannel Channel, string Key)>();
        foreach (var domain in _prototypes.EnumeratePrototypes<NCReputationDomainPrototype>())
        {
            if (!ValidDomain(domain))
            {
                errors.Add($"Reputation domain {domain.ID} is invalid or duplicates a stable channel key.");
                continue;
            }

            var key = NormalizeStableKey(domain.StableKey);
            if (!stableKeys.Add((domain.Channel, key)))
                errors.Add($"Reputation domain {domain.ID} is invalid or duplicates a stable channel key.");
        }

        foreach (var effect in _prototypes.EnumeratePrototypes<NCReputationEffectPrototype>())
        {
            if (!ValidEffect(effect, activePolicy))
                errors.Add($"Reputation effect {effect.ID} is empty or exceeds the active policy bounds.");
        }

        return errors.Count == initialCount;
    }

    public bool Unload(NCPartyRef subject)
    {
        if (!subject.IsValid)
            return false;
        return _cache.TryRemove(NCPersistentIdentity.Reputation(subject), out _);
    }

    public async Task<NCReputationRecord?> GetAsync(NCPartyRef subject, bool forceReload = false)
    {
        if (!subject.IsValid)
            return null;

        var reputationId = NCPersistentIdentity.Reputation(subject);
        if (!forceReload && _cache.TryGetValue(reputationId, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(
            ReputationAggregateType,
            ReputationKey(reputationId));
        if (stored == null)
            return null;
        var record = Deserialize(stored, reputationId, subject);
        return record == null
            ? throw new InvalidOperationException($"Reputation {reputationId} snapshot is invalid.")
            : CacheNewest(record);
    }

    /// <summary>
    /// Returns a bounded newest-first projection for cognition without a global entity or aggregate scan.
    /// </summary>
    public async Task<IReadOnlyList<NCReputationEntrySnapshot>> GetEntriesAsync(
        NCPartyRef subject,
        NCReputationChannel? channel,
        int limit,
        bool forceReload = false)
    {
        if (!_prototypes.TryIndex(DefaultPolicy, out NCReputationPolicyPrototype? policy) || policy == null ||
            !ValidPolicy(policy) || limit < 1 || limit > policy.MaximumQueryCount)
        {
            return [];
        }

        var record = await GetAsync(subject, forceReload);
        if (record == null)
            return [];
        return record.Snapshot.Entries
            .Where(entry => channel == null || entry.Audience.Channel == channel)
            .Take(limit)
            .ToArray();
    }

    public async Task<NCReputationMutationResult> ApplyEffectAsync(NCApplyReputationEffectRequest request)
    {
        if (!TryValidateRequest(request, out var audience, out var effect, out var policy, out var error) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(_clock.IsReady ? NCReputationMutationStatus.Rejected :
                NCReputationMutationStatus.ClockUnavailable,
                error ?? "The active-world clock is not ready.");
        }

        var reputationId = NCPersistentIdentity.Reputation(request.Subject);
        var audienceKey = AudienceKey(audience);
        var entryId = NCPersistentIdentity.ReputationEntry(reputationId, audienceKey);
        var applicationId = NCPersistentIdentity.ReputationApplication(entryId, request.WorldEventId);
        var scopeId = applicationId.ToString("D");

        InitialCommandState commandState;
        NCPartyResolution subject;
        NCPartyResolution initiator;
        NCPartyResolution? audienceParty = null;
        try
        {
            commandState = await ValidateInitialCommandAsync(request.CommandId, scopeId);
            subject = await _parties.ResolveAsync(request.Subject, true);
            initiator = await _parties.ResolveAsync(request.Initiator, true);
            if (audience.Party.IsValid)
                audienceParty = await _parties.ResolveAsync(audience.Party, true);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to prepare reputation event {request.WorldEventId}: {exception}");
            return Reject(NCReputationMutationStatus.StorageFailure, exception.Message);
        }

        if (commandState == InitialCommandState.Collision)
            return Reject(NCReputationMutationStatus.Rejected, "The command ID belongs to another operation.");
        if (commandState == InitialCommandState.InProgress)
            return Reject(NCReputationMutationStatus.StorageFailure, "The reputation command is still in progress.");
        if (!subject.IsResolved || !initiator.IsResolved || audienceParty is { IsResolved: false })
        {
            return Reject(NCReputationMutationStatus.PartyUnavailable,
                "Subject, initiator, or party audience is unavailable.");
        }

        var gate = _gates[(reputationId.Value.GetHashCode() & int.MaxValue) % _gates.Length];
        await gate.WaitAsync();
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var application = await GetApplicationAsync(applicationId);
                if (application != null)
                    return await ReplayApplicationAsync(application, request, reputationId, entryId, audience);
                if (attempt == 0 && commandState == InitialCommandState.Completed)
                {
                    return Reject(NCReputationMutationStatus.StorageFailure,
                        "The completed reputation command is missing its application marker.");
                }

                var current = await GetAsync(request.Subject, true);
                if (current != null && !WithinPolicy(current.Snapshot, policy))
                {
                    return Reject(NCReputationMutationStatus.StorageFailure,
                        "The current reputation snapshot violates the active policy.");
                }

                var reputationEventId = Guid.NewGuid();
                var applicationEventId = Guid.NewGuid();
                NCReputationSnapshot next;
                try
                {
                    next = Apply(current?.Snapshot, reputationId, entryId, request, audience, effect, policy, now,
                        reputationEventId);
                }
                catch (OverflowException)
                {
                    return Reject(NCReputationMutationStatus.Rejected,
                        "The reputation effect would overflow its bounded counters.");
                }
                catch (InvalidOperationException exception)
                {
                    return Reject(NCReputationMutationStatus.Rejected, exception.Message);
                }

                var nextVersion = (current?.AggregateVersion ?? 0) + 1;
                var marker = new ReputationApplicationSnapshot(
                    applicationId,
                    reputationId,
                    entryId,
                    request.Subject,
                    audience,
                    request.WorldEventId,
                    effect.ID,
                    request.Magnitude,
                    request.Initiator,
                    next.Entries.Single(entry => entry.EntryId == entryId).DataVersion,
                    nextVersion,
                    now,
                    applicationEventId);
                var receipt = new ReputationReceipt(next, nextVersion, epoch, now);
                var commandId = attempt == 0 && commandState == InitialCommandState.Available
                    ? request.CommandId
                    : Guid.NewGuid();
                var result = await _database.ApplyNCPersistentBatchMutationAsync(
                    new NCPersistentBatchMutationRequest(
                        commandId,
                        CommandType,
                        ScopeType,
                        scopeId,
                        [
                            new NCPersistentAggregateWrite(
                                reputationEventId,
                                "living-city.reputation.effect-applied",
                                ReputationAggregateType,
                                ReputationKey(reputationId),
                                current?.AggregateVersion ?? 0,
                                SnapshotFormatVersion,
                                JsonSerializer.Serialize(next, JsonOptions),
                                JsonSerializer.Serialize(new ReputationEffectEvent(
                                    reputationId,
                                    entryId,
                                    request.Subject,
                                    audience,
                                    request.WorldEventId,
                                    effect.ID,
                                    request.Magnitude,
                                    request.Initiator,
                                    request.AuditSource.Trim()), JsonOptions)),
                            new NCPersistentAggregateWrite(
                                applicationEventId,
                                "living-city.reputation.application-recorded",
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
                    var persisted = await GetAsync(request.Subject, true);
                    if (persisted == null || persisted.AggregateVersion < nextVersion)
                    {
                        return Reject(NCReputationMutationStatus.StorageFailure,
                            "The committed reputation could not be reloaded.");
                    }

                    return new NCReputationMutationResult(
                        result.Status == NCPersistentMutationStatus.Applied
                            ? NCReputationMutationStatus.Applied
                            : NCReputationMutationStatus.Duplicate,
                        persisted,
                        null);
                }

                if (result.Status is NCPersistentMutationStatus.VersionConflict or
                    NCPersistentMutationStatus.DuplicateFailed)
                {
                    commandState = InitialCommandState.Failed;
                    continue;
                }

                return Reject(result.Status == NCPersistentMutationStatus.ClockRejected
                        ? NCReputationMutationStatus.ClockUnavailable
                        : NCReputationMutationStatus.StorageFailure,
                    result.Error ?? "The reputation effect was not committed.");
            }

            return Reject(NCReputationMutationStatus.VersionConflict,
                "The reputation effect exceeded its optimistic retry limit.");
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to apply reputation event {request.WorldEventId} to {reputationId}: {exception}");
            return Reject(NCReputationMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ReputationApplicationSnapshot?> GetApplicationAsync(Guid applicationId)
    {
        var stored = await _database.GetNCAggregateSnapshotAsync(
            ApplicationAggregateType,
            applicationId.ToString("D"));
        if (stored == null)
            return null;
        if (stored.FormatVersion != SnapshotFormatVersion || !stored.SessionEpoch.IsValid ||
            !stored.ActiveWorldTime.IsValid)
        {
            throw new InvalidOperationException("The reputation application metadata is invalid.");
        }

        try
        {
            var marker = JsonSerializer.Deserialize<ReputationApplicationSnapshot>(stored.Payload, JsonOptions);
            return Valid(marker, applicationId) && marker!.AppliedAt == stored.ActiveWorldTime ? marker :
                throw new InvalidOperationException("The reputation application snapshot is invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The reputation application payload is invalid.", exception);
        }
    }

    private async Task<NCReputationMutationResult> ReplayApplicationAsync(
        ReputationApplicationSnapshot marker,
        NCApplyReputationEffectRequest request,
        NCReputationId reputationId,
        NCReputationEntryId entryId,
        NCReputationAudience audience)
    {
        if (marker.ReputationId != reputationId || marker.EntryId != entryId ||
            marker.Subject != request.Subject || marker.Audience != audience ||
            marker.WorldEventId != request.WorldEventId || marker.EffectId != request.Effect.Id ||
            marker.Magnitude != request.Magnitude || marker.Initiator != request.Initiator)
        {
            return Reject(NCReputationMutationStatus.Rejected,
                "The world-event application identity is bound to different immutable data.");
        }

        var current = await GetAsync(request.Subject, true);
        var currentEntry = current?.Snapshot.Entries.SingleOrDefault(entry => entry.EntryId == entryId);
        return current == null || current.AggregateVersion < marker.ResultingReputationVersion ||
               currentEntry == null || currentEntry.DataVersion < marker.ResultingEntryVersion
            ? Reject(NCReputationMutationStatus.StorageFailure,
                "The application exists without its resulting reputation version.")
            : new NCReputationMutationResult(NCReputationMutationStatus.Duplicate, current, null);
    }

    private async Task<InitialCommandState> ValidateInitialCommandAsync(Guid commandId, string scopeId)
    {
        var command = await _database.GetNCPersistentCommandResultAsync(commandId);
        if (command == null)
            return InitialCommandState.Available;
        if (command.CommandType != CommandType || command.ScopeType != ScopeType || command.ScopeId != scopeId)
            return InitialCommandState.Collision;

        return command.Status switch
        {
            NCPersistentMutationStatus.InProgress => InitialCommandState.InProgress,
            NCPersistentMutationStatus.DuplicateCompleted => InitialCommandState.Completed,
            _ => InitialCommandState.Failed,
        };
    }

    private bool TryValidateRequest(
        NCApplyReputationEffectRequest request,
        out NCReputationAudience audience,
        out NCReputationEffectPrototype effect,
        out NCReputationPolicyPrototype policy,
        out string? error)
    {
        audience = default;
        effect = default!;
        policy = default!;
        error = null;
        if (request.CommandId == Guid.Empty || request.WorldEventId == Guid.Empty ||
            request.CorrelationId == Guid.Empty || !request.Subject.IsValid || !request.Initiator.IsValid ||
            request.Magnitude < 1 || string.IsNullOrWhiteSpace(request.AuditSource) ||
            request.AuditSource.Trim().Length > MaximumAuditSourceLength)
        {
            error = "The reputation effect request is invalid.";
            return false;
        }
        if (!_prototypes.TryIndex(DefaultPolicy, out NCReputationPolicyPrototype? resolvedPolicy) ||
            resolvedPolicy == null || !ValidPolicy(resolvedPolicy))
        {
            error = "The reputation policy is invalid.";
            return false;
        }
        policy = resolvedPolicy;
        if (request.Magnitude > policy.MaximumEffectMagnitude ||
            !_prototypes.TryIndex(request.Effect, out NCReputationEffectPrototype? resolvedEffect) ||
            resolvedEffect == null || !ValidEffect(resolvedEffect, policy) ||
            !resolvedEffect.AllowedChannels.Contains(request.Audience.Channel))
        {
            error = "The reputation effect prototype, channel, or magnitude is invalid.";
            return false;
        }
        effect = resolvedEffect;
        return TryResolveAudience(request.Subject, request.Audience, out audience, out error);
    }

    private bool TryResolveAudience(
        NCPartyRef subject,
        NCReputationAudienceRequest request,
        out NCReputationAudience audience,
        out string? error)
    {
        audience = default;
        error = null;
        if (request.Channel is NCReputationChannel.Personal or NCReputationChannel.Faction)
        {
            var validKind = request.Channel == NCReputationChannel.Personal
                ? request.Party.Kind is NCPartyKind.Character or NCPartyKind.Actor
                : request.Party.Kind == NCPartyKind.Organization;
            if (!request.Party.IsValid || request.Party == subject || !validKind || request.Domain != null)
            {
                error = "The party reputation audience is invalid for its channel.";
                return false;
            }

            audience = new NCReputationAudience(request.Channel, request.Party, string.Empty);
            return true;
        }

        if (request.Party.IsValid || request.Domain is not { } domainId ||
            !_prototypes.TryIndex(domainId, out NCReputationDomainPrototype? domain) || domain == null ||
            !ValidDomain(domain) || domain.Channel != request.Channel)
        {
            error = "The professional or public reputation domain is invalid for its channel.";
            return false;
        }

        audience = new NCReputationAudience(request.Channel, default, NormalizeStableKey(domain.StableKey));
        return true;
    }

    private static NCReputationSnapshot Apply(
        NCReputationSnapshot? current,
        NCReputationId reputationId,
        NCReputationEntryId entryId,
        NCApplyReputationEffectRequest request,
        NCReputationAudience audience,
        NCReputationEffectPrototype effect,
        NCReputationPolicyPrototype policy,
        NCActiveWorldTime now,
        Guid eventId)
    {
        var entries = current?.Entries.ToList() ?? [];
        var nextSequence = checked((current?.DataVersion ?? 0) + 1);
        var index = entries.FindIndex(entry => entry.EntryId == entryId);
        var existing = index >= 0 ? entries[index] : null;
        if (existing != null && existing.Audience != audience)
            throw new InvalidOperationException("The reputation entry identity is bound to another audience.");
        if (existing == null && entries.Count >= policy.MaximumEntriesPerSubject)
            throw new InvalidOperationException("The subject reached the configured reputation entry limit.");

        var nextEntry = new NCReputationEntrySnapshot(
            entryId,
            audience,
            ApplySigned(existing?.Score ?? 0, effect.ScoreDelta, request.Magnitude, policy.MaximumScore),
            ApplyUnsigned(existing?.Visibility ?? 0, effect.VisibilityDelta, request.Magnitude,
                policy.MaximumVisibility),
            ApplyUnsigned(existing?.Evidence ?? 0, effect.EvidenceDelta, request.Magnitude, policy.MaximumEvidence),
            checked((existing?.EventCount ?? 0) + 1),
            nextSequence,
            existing?.CreatedAt ?? now,
            now,
            request.WorldEventId,
            effect.ID,
            checked((existing?.DataVersion ?? 0) + 1),
            eventId);
        if (index >= 0)
            entries[index] = nextEntry;
        else
            entries.Add(nextEntry);

        entries.Sort((left, right) =>
        {
            var sequence = right.LastChangedSequence.CompareTo(left.LastChangedSequence);
            return sequence != 0 ? sequence : left.EntryId.Value.CompareTo(right.EntryId.Value);
        });
        return new NCReputationSnapshot(
            reputationId,
            request.Subject,
            entries,
            nextSequence,
            eventId);
    }

    private static int ApplySigned(int current, int delta, int magnitude, int maximum) =>
        (int) Math.Clamp(checked((long) current + (long) delta * magnitude), -maximum, maximum);

    private static int ApplyUnsigned(int current, int delta, int magnitude, int maximum) =>
        (int) Math.Clamp(checked((long) current + (long) delta * magnitude), 0, maximum);

    private static bool ValidPolicy(NCReputationPolicyPrototype policy) =>
        policy.MaximumScore is > 0 and <= AbsoluteMaximumScore &&
        policy.MaximumVisibility is > 0 and <= AbsoluteMaximumScore &&
        policy.MaximumEvidence is > 0 and <= AbsoluteMaximumEvidence &&
        policy.MaximumEffectMagnitude is > 0 and <= 1_000 &&
        policy.MaximumSingleEffectDelta is > 0 and <= AbsoluteMaximumScore &&
        policy.MaximumEntriesPerSubject is > 0 and <= AbsoluteMaximumEntries &&
        policy.MaximumQueryCount is > 0 && policy.MaximumQueryCount <= policy.MaximumEntriesPerSubject;

    private static bool ValidDomain(NCReputationDomainPrototype domain) =>
        !string.IsNullOrWhiteSpace(domain.ID) && domain.ID.Length <= MaximumStableKeyLength &&
        domain.Channel is NCReputationChannel.Professional or NCReputationChannel.Public &&
        IsStableKey(domain.StableKey);

    private static bool ValidEffect(NCReputationEffectPrototype effect, NCReputationPolicyPrototype policy)
    {
        var maximum = policy.MaximumSingleEffectDelta;
        return !string.IsNullOrWhiteSpace(effect.ID) && effect.ID.Length <= MaximumStableKeyLength &&
               effect.AllowedChannels.Count is > 0 and <= 4 &&
               effect.AllowedChannels.Distinct().Count() == effect.AllowedChannels.Count &&
               effect.AllowedChannels.All(Enum.IsDefined) && Math.Abs((long) effect.ScoreDelta) <= maximum &&
               Math.Abs((long) effect.VisibilityDelta) <= maximum &&
               Math.Abs((long) effect.EvidenceDelta) <= maximum &&
               (effect.ScoreDelta != 0 || effect.VisibilityDelta != 0 || effect.EvidenceDelta != 0);
    }

    private static bool WithinPolicy(NCReputationSnapshot snapshot, NCReputationPolicyPrototype policy) =>
        snapshot.Entries.Count is > 0 && snapshot.Entries.Count <= policy.MaximumEntriesPerSubject &&
        snapshot.Entries.All(entry => WithinPolicy(entry, policy));

    private static bool WithinPolicy(NCReputationEntrySnapshot entry, NCReputationPolicyPrototype policy) =>
        Math.Abs((long) entry.Score) <= policy.MaximumScore && entry.Visibility is >= 0 &&
        entry.Visibility <= policy.MaximumVisibility && entry.Evidence is >= 0 &&
        entry.Evidence <= policy.MaximumEvidence;

    private static bool Valid(NCReputationSnapshot? snapshot, NCReputationId expected, NCPartyRef subject)
    {
        if (snapshot == null || snapshot.ReputationId != expected || !snapshot.ReputationId.IsValid ||
            snapshot.Subject != subject || !subject.IsValid || snapshot.Entries.Count is < 1 or > AbsoluteMaximumEntries ||
            snapshot.DataVersion < 1 || snapshot.LastAppliedEventId == Guid.Empty)
        {
            return false;
        }

        var ids = new HashSet<NCReputationEntryId>();
        var audiences = new HashSet<NCReputationAudience>();
        var sequences = new HashSet<int>();
        foreach (var entry in snapshot.Entries)
        {
            if (!Valid(entry, subject) || !ids.Add(entry.EntryId) || !audiences.Add(entry.Audience) ||
                entry.LastChangedSequence > snapshot.DataVersion || !sequences.Add(entry.LastChangedSequence) ||
                entry.EntryId != NCPersistentIdentity.ReputationEntry(expected, AudienceKey(entry.Audience)))
            {
                return false;
            }
        }
        return snapshot.Entries.Zip(snapshot.Entries.Skip(1),
            (left, right) => left.LastChangedSequence > right.LastChangedSequence).All(value => value);
    }

    private static bool Valid(NCReputationEntrySnapshot entry, NCPartyRef subject) =>
        entry.EntryId.IsValid && ValidAudience(entry.Audience, subject) &&
        Math.Abs((long) entry.Score) <= AbsoluteMaximumScore && entry.Visibility is >= 0 and <= AbsoluteMaximumScore &&
        entry.Evidence is >= 0 and <= AbsoluteMaximumEvidence && entry.EventCount > 0 &&
        entry.LastChangedSequence > 0 &&
        entry.CreatedAt.IsValid && entry.UpdatedAt >= entry.CreatedAt && entry.LastWorldEventId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(entry.LastEffectId) && entry.DataVersion > 0 &&
        entry.LastAppliedEventId != Guid.Empty;

    private static bool ValidAudience(NCReputationAudience audience, NCPartyRef subject)
    {
        return audience.Channel switch
        {
            NCReputationChannel.Personal => audience.Party.IsValid && audience.Party != subject &&
                audience.Party.Kind is NCPartyKind.Character or NCPartyKind.Actor &&
                string.IsNullOrEmpty(audience.DomainKey),
            NCReputationChannel.Faction => audience.Party.IsValid && audience.Party != subject &&
                audience.Party.Kind == NCPartyKind.Organization && string.IsNullOrEmpty(audience.DomainKey),
            NCReputationChannel.Professional or NCReputationChannel.Public => !audience.Party.IsValid &&
                IsStableKey(audience.DomainKey),
            _ => false,
        };
    }

    private static bool Valid(ReputationApplicationSnapshot? marker, Guid expected) =>
        marker != null && marker.ApplicationId == expected && marker.ApplicationId != Guid.Empty &&
        marker.ReputationId.IsValid && marker.EntryId.IsValid && marker.Subject.IsValid &&
        marker.ReputationId == NCPersistentIdentity.Reputation(marker.Subject) &&
        ValidAudience(marker.Audience, marker.Subject) && marker.WorldEventId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(marker.EffectId) && marker.Magnitude > 0 && marker.Initiator.IsValid &&
        marker.ResultingEntryVersion > 0 && marker.ResultingReputationVersion > 0 && marker.AppliedAt.IsValid &&
        marker.LastAppliedEventId != Guid.Empty &&
        marker.EntryId == NCPersistentIdentity.ReputationEntry(marker.ReputationId, AudienceKey(marker.Audience)) &&
        marker.ApplicationId == NCPersistentIdentity.ReputationApplication(marker.EntryId, marker.WorldEventId);

    private static NCReputationRecord? Deserialize(
        NCAggregateSnapshotData stored,
        NCReputationId expected,
        NCPartyRef subject)
    {
        if (stored.FormatVersion != SnapshotFormatVersion || !stored.SessionEpoch.IsValid ||
            !stored.ActiveWorldTime.IsValid)
        {
            return null;
        }
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCReputationSnapshot>(stored.Payload, JsonOptions);
            return Valid(snapshot, expected, subject) && snapshot!.DataVersion == stored.AggregateVersion
                ? new NCReputationRecord(snapshot!, stored.AggregateVersion, stored.SessionEpoch,
                    stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private NCReputationRecord CacheNewest(NCReputationRecord candidate) =>
        _cache.AddOrUpdate(candidate.Snapshot.ReputationId, candidate,
            (_, current) => current.AggregateVersion > candidate.AggregateVersion ? current : candidate);

    private static string AudienceKey(NCReputationAudience audience) => audience.Channel switch
    {
        NCReputationChannel.Personal => $"personal/{audience.Party}",
        NCReputationChannel.Faction => $"faction/{audience.Party}",
        NCReputationChannel.Professional => $"professional/{audience.DomainKey}",
        NCReputationChannel.Public => $"public/{audience.DomainKey}",
        _ => throw new ArgumentOutOfRangeException(nameof(audience)),
    };

    private static bool IsStableKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumStableKeyLength ||
            value != value.Trim().ToLowerInvariant())
        {
            return false;
        }
        return value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    private static string NormalizeStableKey(string value) => value.Trim().ToLowerInvariant();

    private static string ReputationKey(NCReputationId reputationId) => reputationId.Value.ToString("D");

    private static NCReputationMutationResult Reject(NCReputationMutationStatus status, string error) =>
        new(status, null, error);

    private static SemaphoreSlim[] CreateGates()
    {
        var gates = new SemaphoreSlim[MutationGateStripeCount];
        for (var i = 0; i < gates.Length; i++)
            gates[i] = new SemaphoreSlim(1, 1);
        return gates;
    }

    private sealed record ReputationApplicationSnapshot(
        Guid ApplicationId,
        NCReputationId ReputationId,
        NCReputationEntryId EntryId,
        NCPartyRef Subject,
        NCReputationAudience Audience,
        Guid WorldEventId,
        string EffectId,
        int Magnitude,
        NCPartyRef Initiator,
        int ResultingEntryVersion,
        long ResultingReputationVersion,
        NCActiveWorldTime AppliedAt,
        Guid LastAppliedEventId);

    private sealed record ReputationEffectEvent(
        NCReputationId ReputationId,
        NCReputationEntryId EntryId,
        NCPartyRef Subject,
        NCReputationAudience Audience,
        Guid WorldEventId,
        string EffectId,
        int Magnitude,
        NCPartyRef Initiator,
        string AuditSource);

    private sealed record ReputationReceipt(
        NCReputationSnapshot Snapshot,
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
