// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Text.Json;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Market;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Market;

/// <summary>
/// Canonical business authority. A business is a persistent legal party with its own ledger account and finite input
/// and output stock nodes; its owner and operators are permissions, never substitutes for business ownership.
/// </summary>
public sealed partial class NCBusinessRegistrySystem : EntitySystem
{
    public const string AggregateType = "living-city.business";
    private const int SnapshotFormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPartyDirectorySystem _directory = default!;
    [Dependency] private NCPartyResolverSystem _parties = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;
    [Dependency] private NCEconomicAssetRegistrySystem _assets = default!;

    private readonly ConcurrentDictionary<NCBusinessId, NCBusinessRecord> _cache = [];
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public int CachedBusinessCount => _cache.Count;

    public async Task<NCBusinessRecord?> GetAsync(NCBusinessId id, bool forceReload = false)
    {
        if (!id.IsValid)
            return null;
        if (!forceReload && _cache.TryGetValue(id, out var cached))
            return cached;
        var stored = await _database.GetNCAggregateSnapshotAsync(AggregateType, id.ToString());
        var record = Deserialize(stored, id);
        return record == null ? null : _cache.AddOrUpdate(id, record,
            (_, current) => current.AggregateVersion > record.AggregateVersion ? current : record);
    }

    /// <summary>
    /// Reads one bounded durable page. Periodic business services use this instead of scanning ECS entities.
    /// </summary>
    public async Task<NCBusinessPage> GetPageAsync(string? afterAggregateId, int limit)
    {
        if (limit is <= 0 or > 100 || afterAggregateId?.Length > 64)
            return new([], afterAggregateId, "The business page request is invalid.");
        var page = await _database.GetNCAggregateSnapshotPageAsync(
            AggregateType,
            string.IsNullOrWhiteSpace(afterAggregateId) ? null : afterAggregateId.Trim(),
            limit);
        var businesses = new List<NCBusinessRecord>(page.Count);
        foreach (var stored in page)
        {
            if (!Guid.TryParse(stored.AggregateId, out var raw))
                continue;
            var business = await GetAsync(new NCBusinessId(raw), true);
            if (business != null)
                businesses.Add(business);
        }
        return new(businesses, page.Count == 0 ? afterAggregateId : page[^1].AggregateId, null);
    }

    public async Task<NCBusinessMutationResult> CreateAsync(NCCreateBusinessRequest request)
    {
        if (!Valid(request) || !_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject(_clock.IsReady ? NCMarketMutationStatus.Rejected : NCMarketMutationStatus.ClockUnavailable,
                "The business request or active-world clock is invalid.");
        var businessParty = NCPersistentIdentity.Business(request.StableKey);
        if (businessParty.PersistentId != request.BusinessId.Value ||
            !(await _parties.ResolveAsync(request.Owner, true)).IsResolved || request.Initiator != request.Owner)
            return Reject(NCMarketMutationStatus.PartyUnavailable,
                "The business identity or active owner is invalid.");
        foreach (var party in request.Operators.Append(request.Owner).Distinct())
        {
            if (!(await _parties.ResolveAsync(party, true)).IsResolved)
                return Reject(NCMarketMutationStatus.PartyUnavailable, "A business operator is unavailable.");
        }

        var gate = Gate(request.BusinessId.Value);
        await gate.WaitAsync();
        try
        {
            var existing = await GetAsync(request.BusinessId, true);
            if (existing != null)
                return Same(existing.Snapshot, request)
                    ? new(NCMarketMutationStatus.Duplicate, existing, null)
                    : Reject(NCMarketMutationStatus.Rejected,
                        "The business identity belongs to different immutable data.");

            var registered = await _directory.RegisterAsync(new NCPartyDirectoryRegisterRequest(
                NCPersistentIdentity.Command("business", "register-party", request.StableKey),
                businessParty,
                request.DisplayName.Trim(),
                NCPersistentIdentity.Correlation("business", "register-party", request.StableKey),
                request.CausationId,
                request.AuditSource));
            if (registered.Status is not (NCPartyDirectoryMutationStatus.Applied or
                NCPartyDirectoryMutationStatus.Duplicate) && await _directory.GetAsync(businessParty, true) == null)
                return Reject(NCMarketMutationStatus.StorageFailure,
                    registered.Error ?? "The business party could not be registered.");
            var account = await _finance.EnsurePrimaryAccountAsync(businessParty, request.AuditSource);
            if (!account.IsResolved)
                return Reject(NCMarketMutationStatus.StorageFailure,
                    account.Error ?? "The business account could not be created.");

            var inputNodeId = NCPersistentIdentity.StockNode(businessParty, "business-input");
            var outputNodeId = NCPersistentIdentity.StockNode(businessParty, "business-output");
            var input = await EnsureStockAsync(
                inputNodeId, "business-input", NCStockNodeKind.BusinessInventory, businessParty, request);
            var output = await EnsureStockAsync(
                outputNodeId, "business-output", NCStockNodeKind.BusinessInventory, businessParty, request);
            if (input == null || output == null)
                return Reject(NCMarketMutationStatus.StorageFailure,
                    "The business inventory stock nodes could not be created.");

            var eventId = Guid.NewGuid();
            var snapshot = new NCBusinessSnapshot(
                request.BusinessId,
                request.StableKey.Trim().ToLowerInvariant(),
                request.DisplayName.Trim(),
                request.Kind,
                request.OperatingZone,
                businessParty,
                request.Owner,
                request.Operators.Distinct().OrderBy(party => party.ToString()).ToArray(),
                account.AccountId,
                inputNodeId,
                outputNodeId,
                request.Policy,
                NCBusinessStatus.Active,
                [],
                [],
                now,
                now,
                1,
                eventId);
            var persisted = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(
                request.CommandId,
                "living-city.business.create",
                "business",
                request.BusinessId.ToString(),
                eventId,
                "living-city.business.created",
                AggregateType,
                request.BusinessId.ToString(),
                0,
                SnapshotFormatVersion,
                JsonSerializer.Serialize(snapshot, JsonOptions),
                JsonSerializer.Serialize(new BusinessEvent(request.BusinessId, request.AuditSource), JsonOptions),
                JsonSerializer.Serialize(new Receipt(request.BusinessId, 1), JsonOptions),
                request.CorrelationId,
                request.CausationId,
                epoch,
                now,
                []));
            if (persisted.Status is not (NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted))
                return Reject(Map(persisted.Status), persisted.Error ?? "The business was not committed.");
            var loaded = await GetAsync(request.BusinessId, true);
            return loaded == null
                ? Reject(NCMarketMutationStatus.StorageFailure, "The committed business could not be reloaded.")
                : new(persisted.Status == NCPersistentMutationStatus.Applied
                    ? NCMarketMutationStatus.Applied
                    : NCMarketMutationStatus.Duplicate, loaded, null);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to create business {request.BusinessId}: {exception}");
            return Reject(NCMarketMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<NCBusinessMutationResult> UpdatePolicyAsync(NCUpdateBusinessPolicyRequest request) =>
        MutateAsync(request.CommandId, request.BusinessId, request.ExpectedVersion, request.Initiator,
            request.CorrelationId, request.CausationId, request.AuditSource,
            "living-city.business.policy-updated",
            current => Valid(request.Policy) ? current with { Policy = request.Policy } : null);

    public Task<NCBusinessMutationResult> ChangeStatusAsync(NCChangeBusinessStatusRequest request) =>
        MutateAsync(request.CommandId, request.BusinessId, request.ExpectedVersion, request.Initiator,
            request.CorrelationId, request.CausationId, request.AuditSource,
            "living-city.business.status-changed",
            current => Enum.IsDefined(request.Status) &&
                       !(current.Status == NCBusinessStatus.Dissolved && request.Status != NCBusinessStatus.Dissolved)
                ? current with { Status = request.Status }
                : null);

    public Task<NCBusinessMutationResult> AddProductionJobAsync(
        Guid commandId,
        NCBusinessId businessId,
        long expectedVersion,
        NCProductionJobId jobId,
        NCPartyRef initiator,
        Guid correlationId,
        Guid? causationId,
        string auditSource) => MutateAsync(
        commandId, businessId, expectedVersion, initiator, correlationId, causationId, auditSource,
        "living-city.business.production-job-added",
        current => jobId.IsValid && !current.ProductionQueue.Contains(jobId) &&
                   current.ProductionQueue.Count < current.Policy.MaximumConcurrentProduction
            ? current with { ProductionQueue = current.ProductionQueue.Append(jobId).ToArray() }
            : null);

    private async Task<NCBusinessMutationResult> MutateAsync(
        Guid commandId,
        NCBusinessId id,
        long expectedVersion,
        NCPartyRef initiator,
        Guid correlationId,
        Guid? causationId,
        string auditSource,
        string eventType,
        Func<NCBusinessSnapshot, NCBusinessSnapshot?> transition)
    {
        if (commandId == Guid.Empty || !id.IsValid || expectedVersion <= 0 || !initiator.IsValid ||
            correlationId == Guid.Empty || !ValidAudit(auditSource) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject(_clock.IsReady ? NCMarketMutationStatus.Rejected : NCMarketMutationStatus.ClockUnavailable,
                "The business mutation metadata is invalid.");
        var gate = Gate(id.Value);
        await gate.WaitAsync();
        try
        {
            var current = await GetAsync(id, true);
            if (current == null)
                return Reject(NCMarketMutationStatus.NotFound, "The business does not exist.");
            if (current.AggregateVersion != expectedVersion)
                return Reject(NCMarketMutationStatus.VersionConflict, "The business version changed.");
            if (initiator != current.Snapshot.BusinessParty && initiator != current.Snapshot.Owner &&
                !current.Snapshot.Operators.Contains(initiator))
                return Reject(NCMarketMutationStatus.Rejected, "The initiator cannot operate this business.");
            var transitioned = transition(current.Snapshot);
            if (transitioned == null)
                return Reject(NCMarketMutationStatus.Rejected, "The business transition is not allowed.");
            var eventId = Guid.NewGuid();
            var next = transitioned with
            {
                UpdatedAt = now,
                DataVersion = checked(current.Snapshot.DataVersion + 1),
                LastAppliedEventId = eventId,
            };
            var persisted = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(
                commandId, eventType, "business", id.ToString(), eventId, eventType, AggregateType, id.ToString(),
                current.AggregateVersion, SnapshotFormatVersion, JsonSerializer.Serialize(next, JsonOptions),
                JsonSerializer.Serialize(new BusinessEvent(id, auditSource), JsonOptions),
                JsonSerializer.Serialize(new Receipt(id, current.AggregateVersion + 1), JsonOptions),
                correlationId, causationId, epoch, now, []));
            if (persisted.Status is not (NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted))
                return Reject(Map(persisted.Status), persisted.Error ?? "The business mutation was not committed.");
            var loaded = await GetAsync(id, true);
            return loaded == null
                ? Reject(NCMarketMutationStatus.StorageFailure, "The business mutation could not be reloaded.")
                : new(persisted.Status == NCPersistentMutationStatus.Applied
                    ? NCMarketMutationStatus.Applied
                    : NCMarketMutationStatus.Duplicate, loaded, null);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<NCStockNodeRecord?> EnsureStockAsync(
        NCStockNodeId id,
        string suffix,
        NCStockNodeKind kind,
        NCPartyRef owner,
        NCCreateBusinessRequest request)
    {
        var existing = await _assets.GetStockNodeAsync(id, true);
        if (existing != null)
            return existing.Snapshot.Owner == owner && existing.Snapshot.Kind == kind ? existing : null;
        var created = await _assets.CreateStockNodeAsync(new NCCreateStockNodeRequest(
            NCPersistentIdentity.Command("business", $"stock-{suffix}", request.StableKey),
            id,
            $"business.{request.StableKey}.{suffix}",
            kind,
            owner,
            $"business.{request.StableKey}.{suffix}",
            true,
            true,
            owner,
            NCPersistentIdentity.Correlation("business", $"stock-{suffix}", request.StableKey),
            request.CausationId,
            request.AuditSource));
        return created.StockNode;
    }

    private static bool Valid(NCCreateBusinessRequest value) => value.CommandId != Guid.Empty &&
        value.BusinessId.IsValid && Stable(value.StableKey, 128) && !string.IsNullOrWhiteSpace(value.DisplayName) &&
        value.DisplayName.Trim().Length <= 128 && Enum.IsDefined(value.Kind) && value.OperatingZone.IsValid &&
        value.Owner.IsValid &&
        value.Operators.Count <= 32 && value.Operators.All(party => party.IsValid) &&
        value.Operators.Distinct().Count() == value.Operators.Count && Valid(value.Policy) &&
        value.Initiator == value.Owner && value.CorrelationId != Guid.Empty && ValidAudit(value.AuditSource);

    private static bool Valid(NCBusinessOperationPolicy value) => value.PriceMarkupBasisPoints is >= -9000 and <= 100000 &&
        value.RestockThresholdBasisPoints is >= 0 and <= 10000 && value.MinimumCashReserve >= 0 &&
        value.MaximumConcurrentProduction is > 0 and <= 64 && value.MaximumConcurrentShipments is > 0 and <= 64;

    private static bool Same(NCBusinessSnapshot value, NCCreateBusinessRequest request) =>
        value.BusinessId == request.BusinessId && value.StableKey == request.StableKey &&
        value.DisplayName == request.DisplayName.Trim() && value.Kind == request.Kind &&
        value.OperatingZone == request.OperatingZone && value.Owner == request.Owner &&
        value.Operators.SequenceEqual(request.Operators.Distinct().OrderBy(party => party.ToString())) &&
        value.Policy == request.Policy;

    private static NCBusinessRecord? Deserialize(NCAggregateSnapshotData? stored, NCBusinessId id)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCBusinessSnapshot>(stored.Payload, JsonOptions);
            return snapshot?.BusinessId == id
                ? new(snapshot, stored.AggregateVersion, stored.SessionEpoch, stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private SemaphoreSlim Gate(Guid id) => _gates[(id.GetHashCode() & int.MaxValue) % _gates.Length];
    private static bool Stable(string value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max &&
        value == value.Trim().ToLowerInvariant();
    private static bool ValidAudit(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
    private static NCMarketMutationStatus Map(NCPersistentMutationStatus status) => status switch
    {
        NCPersistentMutationStatus.VersionConflict => NCMarketMutationStatus.VersionConflict,
        NCPersistentMutationStatus.ClockRejected => NCMarketMutationStatus.ClockUnavailable,
        _ => NCMarketMutationStatus.StorageFailure,
    };
    private static NCBusinessMutationResult Reject(NCMarketMutationStatus status, string error) =>
        new(status, null, error);
    private sealed record BusinessEvent(NCBusinessId BusinessId, string AuditSource);
    private sealed record Receipt(NCBusinessId BusinessId, long AggregateVersion);
}
