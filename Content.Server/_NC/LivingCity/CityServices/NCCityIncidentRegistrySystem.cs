// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Text.Json;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Social;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.CityServices;

public enum NCIncidentMutationStatus : byte { Applied, Duplicate, NotFound, VersionConflict, NotAuthorized,
    Rejected, StorageFailure }
public sealed record NCCityIncidentRecord(NCCityIncidentSnapshot Snapshot, long AggregateVersion,
    NCSessionEpoch SessionEpoch, NCActiveWorldTime PersistedAt);
public sealed record NCIncidentMutationResult(NCIncidentMutationStatus Status, NCCityIncidentRecord? Incident,
    string? Error);
public sealed record NCCreateIncidentRequest(Guid CommandId, NCCityIncidentId IncidentId, NCCityIncidentKind Kind,
    int Severity, NCZoneId LocationZone, IReadOnlyList<NCIncidentParticipant> Participants,
    NCIncidentCause Cause, NCIncidentMaterializationPolicy MaterializationPolicy, TimeSpan? EscalationDelay,
    NCPartyRef Initiator, Guid CorrelationId, Guid? CausationId = null, string AuditSource = "incident-create");
public sealed record NCAddIncidentEvidenceRequest(Guid CommandId, NCCityIncidentId IncidentId, long ExpectedVersion,
    NCIncidentEvidence Evidence, NCPartyRef Actor, Guid CorrelationId, Guid? CausationId = null,
    string AuditSource = "incident-evidence");
public sealed record NCAddWitnessStatementRequest(Guid CommandId, NCCityIncidentId IncidentId, long ExpectedVersion,
    NCIncidentWitnessStatement Statement, Guid CorrelationId, Guid? CausationId = null,
    string AuditSource = "incident-witness");
public sealed record NCChangeIncidentStageRequest(Guid CommandId, NCCityIncidentId IncidentId, long ExpectedVersion,
    NCCityIncidentStage Stage, NCPartyRef Actor, NCActiveWorldTime? NextEscalationAt, Guid CorrelationId,
    Guid? CausationId = null, string AuditSource = "incident-stage");
public sealed record NCResolveIncidentRequest(Guid CommandId, NCCityIncidentId IncidentId, long ExpectedVersion,
    NCIncidentOutcome Outcome, NCPartyRef Actor, Guid CorrelationId, Guid? CausationId = null,
    string AuditSource = "incident-resolve");

/// <summary>Persistent causal incident timeline; evidence and witness knowledge remain structured and bounded.</summary>
public sealed partial class NCCityIncidentRegistrySystem : EntitySystem
{
    public const string AggregateType = "living-city.incident";
    public const int SnapshotFormatVersion = 1;
    private static readonly ProtoId<NCIncidentPolicyPrototype> DefaultPolicy = "Default";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPartyResolverSystem _parties = default!;
    [Dependency] private NCMemoryRegistrySystem _memories = default!;

    private readonly ConcurrentDictionary<NCCityIncidentId, NCCityIncidentRecord> _cache = [];
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public int CachedCount => _cache.Count;
    public IReadOnlyList<NCCityIncidentRecord> GetCachedForZone(NCZoneId zoneId, int limit, bool includeClosed = false) =>
        !zoneId.IsValid || limit <= 0 ? [] : _cache.Values.Where(value => value.Snapshot.LocationZone == zoneId &&
            (includeClosed || value.Snapshot.Stage is not (NCCityIncidentStage.Closed or
                NCCityIncidentStage.Cancelled))).OrderByDescending(value => value.Snapshot.UpdatedAt.Ticks)
            .Take(Math.Min(limit, 256)).ToArray();

    public async Task<NCCityIncidentRecord?> GetAsync(NCCityIncidentId id, bool forceReload = false)
    {
        if (!id.IsValid)
            return null;
        if (!forceReload && _cache.TryGetValue(id, out var cached))
            return cached;
        var stored = await _database.GetNCAggregateSnapshotAsync(AggregateType, id.ToString());
        var loaded = Deserialize(stored, id);
        if (loaded != null)
            _cache[id] = loaded;
        return loaded;
    }

    public async Task<NCIncidentMutationResult> CreateAsync(NCCreateIncidentRequest request)
    {
        if (!TryPolicy(out var policy) || !Valid(request, policy) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now) ||
            !(await _parties.ResolveAsync(request.Initiator, true)).IsResolved)
            return Reject(NCIncidentMutationStatus.Rejected, "The incident creation request is invalid.");
        foreach (var participant in request.Participants)
        {
            if (!(await _parties.ResolveAsync(participant.Party, true)).IsResolved)
                return Reject(NCIncidentMutationStatus.Rejected, "An incident participant is unavailable.");
        }
        var gate = Gate(request.IncidentId.Value);
        await gate.WaitAsync();
        try
        {
            var current = await GetAsync(request.IncidentId, true);
            if (current != null)
                return current.Snapshot.Causes.Any(value => value.SourceWorldEventId ==
                    request.Cause.SourceWorldEventId)
                    ? new(NCIncidentMutationStatus.Duplicate, current, null)
                    : Reject(NCIncidentMutationStatus.Rejected,
                        "The incident identity belongs to a different cause.");
            var eventId = Guid.NewGuid();
            var delay = request.EscalationDelay ?? policy.DefaultEscalationDelay;
            var snapshot = new NCCityIncidentSnapshot(request.IncidentId, request.Kind, request.Severity,
                NCCityIncidentStage.Reported, request.LocationZone,
                request.Participants.DistinctBy(value => (value.Party, value.Role)).ToArray(), [request.Cause], [], [],
                [], request.MaterializationPolicy, now.Advance(delay), null, now, now, 1, eventId);
            return await Persist(request.CommandId, "living-city.incident.create", null, snapshot, eventId,
                "living-city.incident.reported", request.CorrelationId, request.CausationId,
                request.AuditSource, epoch, now);
        }
        finally { gate.Release(); }
    }

    public Task<NCIncidentMutationResult> AddEvidenceAsync(NCAddIncidentEvidenceRequest request) =>
        MutateAsync(request.CommandId, request.IncidentId, request.ExpectedVersion, request.Actor,
            request.CorrelationId, request.CausationId, request.AuditSource, (current, policy, now) =>
            {
                var value = request.Evidence;
                if (value.EvidenceId == Guid.Empty || value.SubmittedBy != request.Actor ||
                    value.ReferenceId.Length is < 1 or > 160 || value.Reliability is < 0 or > 1000 ||
                    current.Evidence.Count >= policy.MaximumEvidence ||
                    current.Evidence.Any(entry => entry.EvidenceId == value.EvidenceId))
                    return null;
                return current with { Evidence = current.Evidence.Append(value with { CreatedAt = now }).ToArray() };
            }, "living-city.incident.evidence-added");

    public async Task<NCIncidentMutationResult> AddWitnessStatementAsync(NCAddWitnessStatementRequest request)
    {
        var statement = request.Statement;
        if (!statement.Witness.IsValid || statement.StatementId == Guid.Empty || statement.Confidence is < 0 or > 1000 ||
            statement.StatementKey.Length is < 1 or > 160)
            return Reject(NCIncidentMutationStatus.Rejected, "The witness statement is invalid.");
        if (statement.MemoryId is { } memoryId)
        {
            var memory = await _memories.GetAsync(statement.Witness, true);
            if (memory == null || memory.Snapshot.Episodes.All(value => value.MemoryId != memoryId))
                return Reject(NCIncidentMutationStatus.NotAuthorized,
                    "The statement does not reference the witness's own memory.");
        }
        return await MutateAsync(request.CommandId, request.IncidentId, request.ExpectedVersion, statement.Witness,
            request.CorrelationId, request.CausationId, request.AuditSource, (current, policy, now) =>
            {
                if (current.WitnessStatements.Count >= policy.MaximumWitnessStatements ||
                    current.WitnessStatements.Any(value => value.StatementId == statement.StatementId))
                    return null;
                var participants = current.Participants.ToList();
                if (participants.All(value => value.Party != statement.Witness ||
                        value.Role != NCIncidentParticipantRole.Witness))
                    participants.Add(new NCIncidentParticipant(statement.Witness, NCIncidentParticipantRole.Witness,
                        true));
                if (participants.Count > policy.MaximumParticipants)
                    return null;
                return current with
                {
                    Participants = participants.ToArray(),
                    WitnessStatements = current.WitnessStatements.Append(statement with { CreatedAt = now }).ToArray(),
                };
            }, "living-city.incident.witness-statement-added");
    }

    public Task<NCIncidentMutationResult> ChangeStageAsync(NCChangeIncidentStageRequest request) =>
        MutateAsync(request.CommandId, request.IncidentId, request.ExpectedVersion, request.Actor,
            request.CorrelationId, request.CausationId, request.AuditSource, (current, _, _) =>
            Allowed(current.Stage, request.Stage) ? current with { Stage = request.Stage,
                EscalatesAt = request.NextEscalationAt } : null,
            $"living-city.incident.{request.Stage.ToString().ToLowerInvariant()}");

    public Task<NCIncidentMutationResult> ResolveAsync(NCResolveIncidentRequest request) =>
        MutateAsync(request.CommandId, request.IncidentId, request.ExpectedVersion, request.Actor,
            request.CorrelationId, request.CausationId, request.AuditSource, (current, _, _) =>
            {
                if (current.Stage is NCCityIncidentStage.Closed or NCCityIncidentStage.Cancelled ||
                    string.IsNullOrWhiteSpace(request.Outcome.OutcomeKey) ||
                    request.Outcome.OutcomeKey.Length > 128 || request.Outcome.Casualties < 0 ||
                    request.Outcome.EconomicLoss < 0)
                    return null;
                return current with { Stage = NCCityIncidentStage.Resolved, Outcome = request.Outcome,
                    EscalatesAt = null };
            }, "living-city.incident.resolved");

    internal Task<NCIncidentMutationResult> AttachDispatchAsync(NCCityIncidentId incidentId, long expectedVersion,
        NCServiceDispatchId dispatchId, NCPartyRef serviceParty) => MutateAsync(
        Content.Server._NC.LivingCity.Identity.NCPersistentIdentity.Command("incident", "attach-dispatch",
            $"{incidentId}.{dispatchId}"), incidentId, expectedVersion, serviceParty,
        Content.Server._NC.LivingCity.Identity.NCPersistentIdentity.Correlation("incident", "attach-dispatch",
            $"{incidentId}.{dispatchId}"), null, "city-service-dispatch", (current, policy, _) =>
        {
            if (!dispatchId.IsValid || current.DispatchIds.Contains(dispatchId.Value) ||
                current.DispatchIds.Count >= policy.MaximumDispatches)
                return null;
            return current with { DispatchIds = current.DispatchIds.Append(dispatchId.Value).ToArray(),
                Stage = (byte) current.Stage < (byte) NCCityIncidentStage.Dispatching ? NCCityIncidentStage.Dispatching :
                    current.Stage };
        }, "living-city.incident.dispatch-attached");

    /// <summary>
    /// Re-arms an unchanged incident stage in active-world time. Lifecycle retries use this instead of inventing a
    /// synthetic state transition when a service is temporarily unavailable or a physical scene is still observed.
    /// </summary>
    internal Task<NCIncidentMutationResult> ScheduleEscalationAsync(NCCityIncidentId incidentId,
        long expectedVersion, NCActiveWorldTime nextEscalationAt, NCPartyRef actor, string stableReason) =>
        MutateAsync(NCPersistentIdentity.Command("incident", "schedule", $"{incidentId}.{stableReason}"),
            incidentId, expectedVersion, actor,
            NCPersistentIdentity.Correlation("incident", "schedule", $"{incidentId}.{stableReason}"), null,
            "incident-lifecycle", (current, _, now) => nextEscalationAt > now
                ? current with { EscalatesAt = nextEscalationAt }
                : null, "living-city.incident.escalation-scheduled");

    private async Task<NCIncidentMutationResult> MutateAsync(Guid commandId, NCCityIncidentId incidentId,
        long expectedVersion, NCPartyRef actor, Guid correlationId, Guid? causationId, string auditSource,
        Func<NCCityIncidentSnapshot, NCIncidentPolicyPrototype, NCActiveWorldTime, NCCityIncidentSnapshot?> mutate,
        string eventType)
    {
        if (!TryPolicy(out var policy) || commandId == Guid.Empty || !incidentId.IsValid || expectedVersion <= 0 ||
            !actor.IsValid || correlationId == Guid.Empty || !_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject(NCIncidentMutationStatus.Rejected, "The incident mutation request is invalid.");
        var gate = Gate(incidentId.Value);
        await gate.WaitAsync();
        try
        {
            var current = await GetAsync(incidentId, true);
            if (current == null)
                return Reject(NCIncidentMutationStatus.NotFound, "The incident does not exist.");
            if (current.AggregateVersion != expectedVersion)
                return new(NCIncidentMutationStatus.VersionConflict, current, "The incident version changed.");
            var proposed = mutate(current.Snapshot, policy, now);
            if (proposed == null)
                return Reject(NCIncidentMutationStatus.Rejected, "The incident mutation is not allowed.");
            var eventId = Guid.NewGuid();
            proposed = proposed with { UpdatedAt = now, DataVersion = current.Snapshot.DataVersion + 1,
                LastAppliedEventId = eventId };
            return await Persist(commandId, "living-city.incident.update", current, proposed, eventId,
                eventType, correlationId, causationId, auditSource, epoch, now);
        }
        finally { gate.Release(); }
    }

    private async Task<NCIncidentMutationResult> Persist(Guid commandId, string commandType,
        NCCityIncidentRecord? current, NCCityIncidentSnapshot proposed, Guid eventId, string eventType,
        Guid correlationId, Guid? causationId, string auditSource, NCSessionEpoch epoch, NCActiveWorldTime now)
    {
        var result = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(commandId,
            commandType, "incident", proposed.IncidentId.ToString(), eventId, eventType, AggregateType,
            proposed.IncidentId.ToString(), current?.AggregateVersion ?? 0, SnapshotFormatVersion,
            JsonSerializer.Serialize(proposed, JsonOptions), JsonSerializer.Serialize(new IncidentEvent(
                proposed.IncidentId, proposed.Stage, auditSource), JsonOptions), JsonSerializer.Serialize(
                new IncidentReceipt(proposed.IncidentId), JsonOptions), correlationId, causationId, epoch, now, []));
        if (result.Status is not (NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted))
            return new(result.Status == NCPersistentMutationStatus.VersionConflict
                ? NCIncidentMutationStatus.VersionConflict : NCIncidentMutationStatus.StorageFailure,
                current, result.Error ?? "The incident mutation failed.");
        var loaded = await GetAsync(proposed.IncidentId, true);
        if (loaded == null)
            return Reject(NCIncidentMutationStatus.StorageFailure, "The incident could not be reloaded.");
        RaiseLocalEvent(new NCCityIncidentPersistedEvent(loaded));
        return new(result.Status == NCPersistentMutationStatus.Applied ? NCIncidentMutationStatus.Applied :
            NCIncidentMutationStatus.Duplicate, loaded, null);
    }

    private bool TryPolicy(out NCIncidentPolicyPrototype policy) => _prototypes.TryIndex(DefaultPolicy, out policy!) &&
        policy.MaximumParticipants is > 0 and <= 256 && policy.MaximumEvidence is > 0 and <= 512 &&
        policy.MaximumWitnessStatements is > 0 and <= 256 && policy.MaximumDispatches is > 0 and <= 64 &&
        policy.MaximumSeverity > 0 && policy.DefaultEscalationDelay > TimeSpan.Zero &&
        policy.VerificationDelay > TimeSpan.Zero && policy.DispatchRetryDelay > TimeSpan.Zero &&
        policy.AbstractResolutionDelay > TimeSpan.Zero && policy.ClosureDelay > TimeSpan.Zero &&
        policy.MaximumAdvancesPerTick is > 0 and <= 64;
    private static bool Valid(NCCreateIncidentRequest value, NCIncidentPolicyPrototype policy) =>
        value.CommandId != Guid.Empty && value.IncidentId.IsValid && Enum.IsDefined(value.Kind) &&
        value.Severity is > 0 && value.Severity <= policy.MaximumSeverity && value.LocationZone.IsValid &&
        value.Participants.Count <= policy.MaximumParticipants && value.Participants.All(entry => entry.Party.IsValid) &&
        value.Cause.SourceWorldEventId != Guid.Empty && value.Cause.Domain.Length is > 0 and <= 96 &&
        value.Cause.EventType.Length is > 0 and <= 128 && value.Cause.ReferenceId.Length <= 160 &&
        (value.EscalationDelay is null || value.EscalationDelay > TimeSpan.Zero &&
            value.EscalationDelay <= TimeSpan.FromDays(7)) && value.Initiator.IsValid &&
        value.CorrelationId != Guid.Empty;
    private static bool Allowed(NCCityIncidentStage from, NCCityIncidentStage to) => (from, to) switch
    {
        (NCCityIncidentStage.Reported, NCCityIncidentStage.Verified or NCCityIncidentStage.Cancelled) => true,
        (NCCityIncidentStage.Verified, NCCityIncidentStage.Dispatching or NCCityIncidentStage.Active or
            NCCityIncidentStage.Cancelled) => true,
        (NCCityIncidentStage.Dispatching, NCCityIncidentStage.ResponseEnRoute or NCCityIncidentStage.Active) => true,
        (NCCityIncidentStage.ResponseEnRoute, NCCityIncidentStage.Active or NCCityIncidentStage.Contained) => true,
        (NCCityIncidentStage.Active, NCCityIncidentStage.Contained or NCCityIncidentStage.Investigation) => true,
        (NCCityIncidentStage.Contained, NCCityIncidentStage.Investigation or NCCityIncidentStage.Resolved) => true,
        (NCCityIncidentStage.Investigation, NCCityIncidentStage.Resolved) => true,
        (NCCityIncidentStage.Resolved, NCCityIncidentStage.Closed) => true,
        _ => false,
    };
    private static NCCityIncidentRecord? Deserialize(NCAggregateSnapshotData? stored, NCCityIncidentId id)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var value = JsonSerializer.Deserialize<NCCityIncidentSnapshot>(stored.Payload, JsonOptions);
            return value?.IncidentId == id ? new NCCityIncidentRecord(value, stored.AggregateVersion,
                stored.SessionEpoch, stored.ActiveWorldTime) : null;
        }
        catch (JsonException) { return null; }
    }
    private SemaphoreSlim Gate(Guid id) => _gates[(id.GetHashCode() & int.MaxValue) % _gates.Length];
    private static NCIncidentMutationResult Reject(NCIncidentMutationStatus status, string error) =>
        new(status, null, error);
    private sealed record IncidentReceipt(NCCityIncidentId IncidentId);
    private sealed record IncidentEvent(NCCityIncidentId IncidentId, NCCityIncidentStage Stage, string AuditSource);
}

public sealed class NCCityIncidentPersistedEvent(NCCityIncidentRecord incident) : EntityEventArgs
{
    public NCCityIncidentRecord Incident { get; } = incident;
}

/// <summary>Normalized source emitted by authoritative domains; the generator owns incident identity/idempotency.</summary>
public sealed class NCCityIncidentSourceEvent(Guid worldEventId, string domain, string eventType, string referenceId,
    NCCityIncidentKind kind, int severity, NCZoneId locationZone, IReadOnlyList<NCIncidentParticipant> participants,
    NCPartyRef initiator, NCIncidentMaterializationPolicy materializationPolicy) : EntityEventArgs
{
    public Guid WorldEventId { get; } = worldEventId;
    public string Domain { get; } = domain;
    public string EventType { get; } = eventType;
    public string ReferenceId { get; } = referenceId;
    public NCCityIncidentKind Kind { get; } = kind;
    public int Severity { get; } = severity;
    public NCZoneId LocationZone { get; } = locationZone;
    public IReadOnlyList<NCIncidentParticipant> Participants { get; } = participants;
    public NCPartyRef Initiator { get; } = initiator;
    public NCIncidentMaterializationPolicy MaterializationPolicy { get; } = materializationPolicy;
}
