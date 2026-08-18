// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.LivingCity.Economy;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Housing;
using Content.Shared._NC.LivingCity.Housing;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;

namespace Content.Server._NC.LivingCity.Housing;

/// <summary>
/// Versioned common property registry. It stores no runtime EntityUid and never derives mapper geography; property,
/// building and room identities must already come from city zone authoring.
/// </summary>
public sealed partial class NCPropertyRegistrySystem : EntitySystem
{
    public const string AggregateType = "living-city.property";
    public const int SnapshotFormatVersion = 1;

    private const int MaximumStringIdLength = 64;
    private const int MaximumInteriorStateLength = 128;
    private const int MaximumAuditSourceLength = 128;
    private const int MaximumRooms = 256;
    private const int MaximumOccupants = 64;
    private const int MaximumAccessGrants = 128;
    private const int MutationGateStripeCount = 64;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPartyResolverSystem _parties = default!;
    [Dependency] private ITaskManager _taskManager = default!;

    private readonly ConcurrentDictionary<NCApartmentId, NCPropertyAggregateRecord> _cache = [];
    private readonly SemaphoreSlim[] _gates = CreateGates();

    public int CachedPropertyCount => _cache.Count;

    public async Task<NCPropertyAggregateRecord?> GetAsync(NCApartmentId propertyId, bool forceReload = false)
    {
        if (!propertyId.IsValid)
            return null;
        if (!forceReload && _cache.TryGetValue(propertyId, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(AggregateType, PropertyKey(propertyId));
        var record = Deserialize(stored, propertyId);
        return record == null ? null : CacheNewest(record);
    }

    public bool Unload(NCApartmentId propertyId)
    {
        return _cache.TryRemove(propertyId, out _);
    }

    /// <summary>
    /// Prepares a validated property participant for a ledger settlement without committing or caching it. The ledger
    /// later writes this snapshot atomically with money; the caller must force-reload the property after success/replay.
    /// </summary>
    public bool TryPrepareSettlementWrite(
        NCPropertyAggregateRecord current,
        NCPropertyAggregateSnapshot proposed,
        NCTransactionId transactionId,
        string eventType,
        string auditSource,
        out NCAtomicLedgerParticipantWrite? write,
        out NCPropertyAggregateSnapshot? prepared,
        out string? error)
    {
        write = null;
        prepared = null;
        if (current.Snapshot.PropertyId != proposed.PropertyId || !transactionId.IsValid ||
            string.IsNullOrWhiteSpace(eventType) || !eventType.StartsWith("living-city.property.", StringComparison.Ordinal) ||
            eventType.Length > 96 || string.IsNullOrWhiteSpace(auditSource) ||
            auditSource.Length > MaximumAuditSourceLength || proposed.DataVersion != current.Snapshot.DataVersion ||
            !_clock.TryGetCurrentTime(out _, out var now))
        {
            error = "The atomic property settlement metadata is invalid.";
            return false;
        }

        var eventId = Guid.NewGuid();
        var next = proposed with
        {
            LastSettlementTransactionId = transactionId,
            ChangedAt = now,
            DataVersion = checked(proposed.DataVersion + 1),
            LastAppliedEventId = eventId,
        };
        if (!ValidSnapshot(next, persisted: true))
        {
            error = "The proposed property settlement state is invalid.";
            return false;
        }

        prepared = next;
        write = new NCAtomicLedgerParticipantWrite(
            eventId,
            eventType,
            AggregateType,
            PropertyKey(next.PropertyId),
            current.AggregateVersion,
            SnapshotFormatVersion,
            JsonSerializer.Serialize(next, JsonOptions),
            JsonSerializer.Serialize(new PropertySettlementEvent(
                next.PropertyId,
                transactionId,
                next.Owner,
                next.Status,
                next.Occupants.Select(value => value.Occupant).ToArray(),
                auditSource), JsonOptions));
        error = null;
        return true;
    }

    /// <summary>
    /// Registers the property described by mapper geography. Replaying the deterministic registration command is safe;
    /// no relational Housing projection is created alongside this aggregate.
    /// </summary>
    public async Task<NCPropertyAggregateMutationResult> RegisterAsync(NCRegisterPropertyRequest request)
    {
        if (!TryValidateRegistration(request, out var error) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(_clock.IsReady
                    ? NCPropertyAggregateMutationStatus.Rejected
                    : NCPropertyAggregateMutationStatus.ClockUnavailable,
                error ?? "The property registration or active-world clock is invalid.");
        }

        var resolution = await _parties.ResolveAsync(request.Owner, requireActive: true);
        if (!resolution.IsResolved)
            return Reject(NCPropertyAggregateMutationStatus.Rejected,
                resolution.Error ?? $"Property owner {request.Owner} cannot be resolved.");

        var propertyId = request.PropertyId;
        var gate = GetGate(propertyId);
        await gate.WaitAsync();
        try
        {
            var eventId = Guid.NewGuid();
            var next = new NCPropertyAggregateSnapshot(
                propertyId,
                request.BuildingId,
                request.RoomIds,
                request.ListingId.Trim(),
                request.Owner,
                [],
                [],
                NCPropertyStatus.Available,
                request.PurchasePrice,
                null,
                1,
                string.Empty,
                request.AccessPolicyId.Trim(),
                null,
                null,
                default,
                "empty",
                null,
                default,
                now,
                1,
                eventId);
            if (!ValidSnapshot(next, persisted: true))
                return Reject(NCPropertyAggregateMutationStatus.Rejected, "The registered property state is invalid.");

            var result = await PersistAsync(
                request.CommandId,
                "living-city.property.register",
                "living-city.property.registered",
                0,
                next,
                request.Owner,
                request.CorrelationId,
                request.CausationId,
                request.AuditSource,
                epoch,
                now);
            return ApplyResult(result, next, epoch, now);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to register property {propertyId}: {exception}");
            return Reject(NCPropertyAggregateMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Persists a non-monetary property transition after the caller has checked its domain rules. Immutable mapper and
    /// ownership fields cannot be changed through this path, preventing accidental bypass of a paid settlement.
    /// </summary>
    public async Task<NCPropertyAggregateMutationResult> UpdateAsync(NCUpdatePropertyRequest request)
    {
        if (!TryValidateUpdate(request, out var error) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(_clock.IsReady
                    ? NCPropertyAggregateMutationStatus.Rejected
                    : NCPropertyAggregateMutationStatus.ClockUnavailable,
                error ?? "The property update or active-world clock is invalid.");
        }

        foreach (var party in ReferencedParties(request.Proposed).Append(request.Initiator).Distinct())
        {
            var resolution = await _parties.ResolveAsync(party);
            if (!resolution.IsResolved)
                return Reject(NCPropertyAggregateMutationStatus.Rejected,
                    resolution.Error ?? $"Property party {party} cannot be resolved.");
        }

        var propertyId = request.Proposed.PropertyId;
        var gate = GetGate(propertyId);
        await gate.WaitAsync();
        try
        {
            var current = await GetAsync(propertyId, true);
            if (current == null)
                return Reject(NCPropertyAggregateMutationStatus.NotFound, "The property does not exist.");
            if (current.AggregateVersion != request.ExpectedVersion)
                return new(NCPropertyAggregateMutationStatus.VersionConflict, current, "The property version changed.");
            if (!ImmutableFieldsMatch(current.Snapshot, request.Proposed))
                return Reject(NCPropertyAggregateMutationStatus.Rejected,
                    "A non-monetary update attempted to change immutable geography, listing or ownership.");

            var eventId = Guid.NewGuid();
            var next = request.Proposed with
            {
                ChangedAt = now,
                DataVersion = checked(current.Snapshot.DataVersion + 1),
                LastAppliedEventId = eventId,
            };
            if (!ValidSnapshot(next, persisted: true))
                return Reject(NCPropertyAggregateMutationStatus.Rejected, "The proposed property state is invalid.");

            var result = await PersistAsync(
                request.CommandId,
                "living-city.property.update",
                request.EventType,
                current.AggregateVersion,
                next,
                request.Initiator,
                request.CorrelationId,
                request.CausationId,
                request.AuditSource,
                epoch,
                now);
            return ApplyResult(result, next, epoch, now);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to update property {propertyId}: {exception}");
            return Reject(NCPropertyAggregateMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private Task<NCPersistentMutationResult> PersistAsync(
        Guid commandId,
        string commandType,
        string eventType,
        long expectedVersion,
        NCPropertyAggregateSnapshot snapshot,
        NCPartyRef initiator,
        Guid correlationId,
        Guid? causationId,
        string auditSource,
        NCSessionEpoch epoch,
        NCActiveWorldTime now)
    {
        var receipt = new PropertyReceipt(snapshot, expectedVersion + 1, epoch, now);
        return _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(
            commandId,
            commandType,
            "property",
            PropertyKey(snapshot.PropertyId),
            snapshot.LastAppliedEventId,
            eventType,
            AggregateType,
            PropertyKey(snapshot.PropertyId),
            expectedVersion,
            SnapshotFormatVersion,
            JsonSerializer.Serialize(snapshot, JsonOptions),
            JsonSerializer.Serialize(new PropertyMutationEvent(
                snapshot.PropertyId,
                eventType,
                initiator,
                snapshot.Owner,
                snapshot.Status,
                snapshot.Occupants.Select(value => value.Occupant).ToArray(),
                auditSource), JsonOptions),
            JsonSerializer.Serialize(receipt, JsonOptions),
            correlationId,
            causationId,
            epoch,
            now,
            []));
    }

    private NCPropertyAggregateMutationResult ApplyResult(
        NCPersistentMutationResult result,
        NCPropertyAggregateSnapshot submitted,
        NCSessionEpoch epoch,
        NCActiveWorldTime now)
    {
        if (result.Status == NCPersistentMutationStatus.Applied)
        {
            var record = CacheNewest(new NCPropertyAggregateRecord(
                submitted,
                result.AggregateVersion,
                epoch,
                now));
            Publish(record);
            return new(NCPropertyAggregateMutationStatus.Applied, record, null);
        }

        if (result.Status == NCPersistentMutationStatus.DuplicateCompleted)
        {
            var receipt = DeserializeReceipt(result.ResultPayload);
            if (receipt == null || receipt.Snapshot.PropertyId != submitted.PropertyId)
                return Reject(NCPropertyAggregateMutationStatus.StorageFailure,
                    "The property mutation receipt is invalid.");
            var record = CacheNewest(new NCPropertyAggregateRecord(
                receipt.Snapshot,
                receipt.AggregateVersion,
                receipt.SessionEpoch,
                receipt.PersistedAt));
            Publish(record);
            return new(NCPropertyAggregateMutationStatus.Duplicate, record, null);
        }

        return Reject(result.Status is NCPersistentMutationStatus.VersionConflict or
                NCPersistentMutationStatus.DuplicateFailed
                ? NCPropertyAggregateMutationStatus.VersionConflict
                : NCPropertyAggregateMutationStatus.StorageFailure,
            result.Error ?? "The property mutation was not committed.");
    }

    private static bool TryValidateRegistration(NCRegisterPropertyRequest request, out string? error)
    {
        if (request.CommandId == Guid.Empty || request.CorrelationId == Guid.Empty || !request.PropertyId.IsValid ||
            !request.BuildingId.IsValid || request.RoomIds == null || request.RoomIds.Length is < 1 or > MaximumRooms ||
            request.RoomIds.Any(value => !value.IsValid) || request.RoomIds.Distinct().Count() != request.RoomIds.Length ||
            !ValidRequiredId(request.ListingId) || !request.Owner.IsValid || request.PurchasePrice < 0 ||
            !ValidRequiredId(request.AccessPolicyId) || string.IsNullOrWhiteSpace(request.AuditSource) ||
            request.AuditSource.Length > MaximumAuditSourceLength)
        {
            error = "The property registration command is invalid.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryValidateUpdate(NCUpdatePropertyRequest request, out string? error)
    {
        if (request.CommandId == Guid.Empty || request.CorrelationId == Guid.Empty || request.ExpectedVersion < 1 ||
            !request.Initiator.IsValid || string.IsNullOrWhiteSpace(request.EventType) ||
            !request.EventType.StartsWith("living-city.property.", StringComparison.Ordinal) ||
            request.EventType.Length > 96 || string.IsNullOrWhiteSpace(request.AuditSource) ||
            request.AuditSource.Length > MaximumAuditSourceLength || !ValidSnapshot(request.Proposed, persisted: true))
        {
            error = "The property update command is invalid.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool ImmutableFieldsMatch(
        NCPropertyAggregateSnapshot current,
        NCPropertyAggregateSnapshot proposed)
    {
        return current.PropertyId == proposed.PropertyId && current.BuildingId == proposed.BuildingId &&
               current.RoomIds.SequenceEqual(proposed.RoomIds) && current.ListingId == proposed.ListingId &&
               current.Owner == proposed.Owner && current.PurchasePrice == proposed.PurchasePrice &&
               current.AccessPolicyId == proposed.AccessPolicyId &&
               current.LastSettlementTransactionId == proposed.LastSettlementTransactionId;
    }

    private static bool ValidSnapshot(NCPropertyAggregateSnapshot? snapshot, bool persisted)
    {
        if (snapshot == null || !snapshot.PropertyId.IsValid || !snapshot.BuildingId.IsValid ||
            snapshot.RoomIds == null || snapshot.RoomIds.Length is < 1 or > MaximumRooms ||
            snapshot.RoomIds.Any(value => !value.IsValid) || snapshot.RoomIds.Distinct().Count() != snapshot.RoomIds.Length ||
            !ValidRequiredId(snapshot.ListingId) || !snapshot.Owner.IsValid || snapshot.Occupants == null ||
            snapshot.Occupants.Length > MaximumOccupants || snapshot.AccessGrants == null ||
            snapshot.AccessGrants.Length > MaximumAccessGrants || !Enum.IsDefined(snapshot.Status) ||
            snapshot.PurchasePrice < 0 || snapshot.AccessRevision < 0 || !ValidOptionalId(snapshot.RentPolicyId) ||
            !ValidRequiredId(snapshot.AccessPolicyId) || !ValidNullableId(snapshot.CurrentTemplateId) ||
            snapshot.CurrentTemplateRevision is <= 0 ||
            (snapshot.CurrentTemplateId == null) != (snapshot.CurrentTemplateRevision == null) ||
            string.IsNullOrWhiteSpace(snapshot.InteriorState) ||
            snapshot.InteriorState.Length > MaximumInteriorStateLength || snapshot.DataVersion < 0)
        {
            return false;
        }

        if (snapshot.AcquiredAt is { } acquiredAt && !acquiredAt.IsValid ||
            snapshot.NextRentDueAt is { } rentDueAt && !rentDueAt.IsValid)
            return false;

        var occupants = new HashSet<NCPartyRef>();
        foreach (var occupant in snapshot.Occupants)
        {
            if (!occupant.Occupant.IsValid || !Enum.IsDefined(occupant.Kind) || !occupant.StartedAt.IsValid ||
                occupant.ValidUntil is { } until && (!until.IsValid || until < occupant.StartedAt) ||
                !occupants.Add(occupant.Occupant))
                return false;
        }

        var grants = new HashSet<Guid>();
        var permissionMask = NCPropertyPermission.All;
        foreach (var grant in snapshot.AccessGrants)
        {
            if (grant.GrantId == Guid.Empty || !grants.Add(grant.GrantId) || !grant.Subject.IsValid ||
                grant.Permissions == NCPropertyPermission.None || (grant.Permissions & ~permissionMask) != 0 ||
                grant.AccessRevision < 1 || grant.AccessRevision > snapshot.AccessRevision ||
                !grant.IssuedAt.IsValid || grant.ValidUntil is { } until &&
                (!until.IsValid || until < grant.IssuedAt))
                return false;
        }

        var activeOwnership = snapshot.Occupants.Count(value => value.Kind == NCPropertyOccupancyKind.Ownership);
        var activeTenancy = snapshot.Occupants.Count(value => value.Kind == NCPropertyOccupancyKind.Tenancy);
        if (snapshot.Status == NCPropertyStatus.OwnerOccupied &&
            (activeOwnership != 1 || snapshot.Occupants.All(value => value.Occupant != snapshot.Owner)) ||
            snapshot.Status is NCPropertyStatus.Rented or NCPropertyStatus.Delinquent or
                NCPropertyStatus.EvictionPending && activeTenancy < 1 ||
            snapshot.Status is NCPropertyStatus.Available or NCPropertyStatus.Unavailable && activeTenancy != 0 ||
            snapshot.Status is NCPropertyStatus.Rented or NCPropertyStatus.Delinquent or
                NCPropertyStatus.EvictionPending && snapshot.NextRentDueAt == null)
            return false;

        return !persisted || snapshot.ChangedAt.IsValid && snapshot.DataVersion >= 1 &&
            snapshot.LastAppliedEventId != Guid.Empty;
    }

    private static IEnumerable<NCPartyRef> ReferencedParties(NCPropertyAggregateSnapshot snapshot)
    {
        return snapshot.Occupants.Select(value => value.Occupant)
            .Concat(snapshot.AccessGrants.Select(value => value.Subject))
            .Append(snapshot.Owner)
            .Distinct();
    }

    private static bool ValidRequiredId(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumStringIdLength && value.Trim() == value;
    }

    private static bool ValidOptionalId(string? value)
    {
        return value != null && value.Length <= MaximumStringIdLength && value.Trim() == value;
    }

    private static bool ValidNullableId(string? value)
    {
        return value == null || ValidRequiredId(value);
    }

    private static NCPropertyAggregateRecord? Deserialize(NCAggregateSnapshotData? stored, NCApartmentId propertyId)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion || !stored.SessionEpoch.IsValid ||
            !stored.ActiveWorldTime.IsValid)
            return null;

        try
        {
            var snapshot = JsonSerializer.Deserialize<NCPropertyAggregateSnapshot>(stored.Payload, JsonOptions);
            return snapshot?.PropertyId == propertyId && ValidSnapshot(snapshot, persisted: true)
                ? new NCPropertyAggregateRecord(
                    snapshot,
                    stored.AggregateVersion,
                    stored.SessionEpoch,
                    stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PropertyReceipt? DeserializeReceipt(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;
        try
        {
            var receipt = JsonSerializer.Deserialize<PropertyReceipt>(payload, JsonOptions);
            return receipt != null && receipt.AggregateVersion >= 1 && receipt.SessionEpoch.IsValid &&
                   receipt.PersistedAt.IsValid && ValidSnapshot(receipt.Snapshot, persisted: true)
                ? receipt
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private NCPropertyAggregateRecord CacheNewest(NCPropertyAggregateRecord candidate)
    {
        return _cache.AddOrUpdate(candidate.Snapshot.PropertyId, candidate,
            (_, current) => current.AggregateVersion > candidate.AggregateVersion ? current : candidate);
    }

    private void Publish(NCPropertyAggregateRecord record)
    {
        _taskManager.RunOnMainThread(() => RaiseLocalEvent(new NCPropertyAggregatePersistedEvent(record)));
    }

    private SemaphoreSlim GetGate(NCApartmentId propertyId)
    {
        return _gates[(propertyId.GetHashCode() & int.MaxValue) % _gates.Length];
    }

    private static SemaphoreSlim[] CreateGates()
    {
        var gates = new SemaphoreSlim[MutationGateStripeCount];
        for (var i = 0; i < gates.Length; i++)
            gates[i] = new SemaphoreSlim(1, 1);
        return gates;
    }

    private static string PropertyKey(NCApartmentId propertyId) => propertyId.Value.ToString("D");

    private static NCPropertyAggregateMutationResult Reject(NCPropertyAggregateMutationStatus status, string error)
    {
        return new(status, null, error);
    }

    private sealed record PropertyMutationEvent(
        NCApartmentId PropertyId,
        string EventType,
        NCPartyRef Initiator,
        NCPartyRef Owner,
        NCPropertyStatus Status,
        NCPartyRef[] Occupants,
        string AuditSource);

    private sealed record PropertySettlementEvent(
        NCApartmentId PropertyId,
        NCTransactionId TransactionId,
        NCPartyRef Owner,
        NCPropertyStatus Status,
        NCPartyRef[] Occupants,
        string AuditSource);

    private sealed record PropertyReceipt(
        NCPropertyAggregateSnapshot Snapshot,
        long AggregateVersion,
        NCSessionEpoch SessionEpoch,
        NCActiveWorldTime PersistedAt);
}
