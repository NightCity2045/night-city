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
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Economy;

/// <summary>
/// Persistent authority for stock locations, commodity batches and unique item instances. Stock indexes and assets
/// commit together; no EntityUid, generated prose or client price enters these aggregates.
/// </summary>
public sealed partial class NCEconomicAssetRegistrySystem : EntitySystem
{
    public const string StockNodeAggregateType = "living-city.stock-node";
    public const string CommodityBatchAggregateType = "living-city.commodity-batch";
    public const string ItemInstanceAggregateType = "living-city.item-instance";
    public const int SnapshotFormatVersion = 1;

    private static readonly ProtoId<NCEconomicAssetPolicyPrototype> DefaultPolicy = "Default";
    private const string CreateStockNodeCommand = "living-city.stock-node.create";
    private const string CreateCommodityBatchCommand = "living-city.commodity-batch.create";
    private const string CreateItemInstanceCommand = "living-city.item-instance.create";
    private const int GateStripeCount = 128;
    private const int MaximumAuditSourceLength = 128;
    private const int AbsoluteMaximumStockEntries = 4_096;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPartyResolverSystem _parties = default!;
    [Dependency] private NCEconomicMaterializationLeaseIndexSystem _materializationLeases = default!;

    private readonly ConcurrentDictionary<NCStockNodeId, NCStockNodeRecord> _stockNodes = [];
    private readonly ConcurrentDictionary<NCCommodityBatchId, NCCommodityBatchRecord> _batches = [];
    private readonly ConcurrentDictionary<NCItemId, NCItemInstanceRecord> _items = [];
    private readonly SemaphoreSlim[] _gates = CreateGates();

    public int CachedStockNodeCount => _stockNodes.Count;
    public int CachedCommodityBatchCount => _batches.Count;
    public int CachedItemInstanceCount => _items.Count;

    public bool Unload(NCStockNodeId id) => _stockNodes.TryRemove(id, out _);
    public bool Unload(NCCommodityBatchId id) => _batches.TryRemove(id, out _);
    public bool Unload(NCItemId id) => _items.TryRemove(id, out _);

    public async Task<NCStockNodeRecord?> GetStockNodeAsync(NCStockNodeId id, bool forceReload = false)
    {
        if (!id.IsValid)
            return null;
        if (!forceReload && _stockNodes.TryGetValue(id, out var cached))
            return cached;
        var stored = await _database.GetNCAggregateSnapshotAsync(StockNodeAggregateType, Key(id));
        var record = DeserializeStockNode(stored, id);
        return record == null ? null : CacheNewest(record);
    }

    public async Task<NCCommodityBatchRecord?> GetCommodityBatchAsync(
        NCCommodityBatchId id,
        bool forceReload = false)
    {
        if (!id.IsValid)
            return null;
        if (!forceReload && _batches.TryGetValue(id, out var cached))
            return cached;
        var stored = await _database.GetNCAggregateSnapshotAsync(CommodityBatchAggregateType, Key(id));
        var record = DeserializeCommodityBatch(stored, id);
        return record == null ? null : CacheNewest(record);
    }

    public async Task<NCItemInstanceRecord?> GetItemAsync(NCItemId id, bool forceReload = false)
    {
        if (!id.IsValid)
            return null;
        if (!forceReload && _items.TryGetValue(id, out var cached))
            return cached;
        var stored = await _database.GetNCAggregateSnapshotAsync(ItemInstanceAggregateType, Key(id));
        var record = DeserializeItem(stored, id);
        return record == null ? null : CacheNewest(record);
    }

    public async Task<NCEconomicAssetMutationResult> CreateStockNodeAsync(NCCreateStockNodeRequest request)
    {
        if (!TryGetPolicy(out var policy) || !Valid(request, policy) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(_clock.IsReady
                ? NCEconomicAssetMutationStatus.Rejected
                : NCEconomicAssetMutationStatus.ClockUnavailable,
                "The stock-node request, policy, or active-world clock is invalid.");
        }
        if (!await ResolveParties(request.Owner, request.Initiator))
            return Reject(NCEconomicAssetMutationStatus.PartyUnavailable, "The stock owner or initiator is unavailable.");

        var scopeId = Key(request.StockNodeId);
        var command = await InspectCommand(request.CommandId, CreateStockNodeCommand, "stock-node", scopeId);
        if (command == InitialCommandState.Collision)
            return Reject(NCEconomicAssetMutationStatus.Rejected, "The command ID belongs to another operation.");
        if (command == InitialCommandState.InProgress)
            return Reject(NCEconomicAssetMutationStatus.StorageFailure, "The stock-node command is still in progress.");

        var gate = Gate(request.StockNodeId.Value);
        await gate.WaitAsync();
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var current = await GetStockNodeAsync(request.StockNodeId, true);
                if (current != null)
                {
                    return Same(current.Snapshot, request)
                        ? new NCEconomicAssetMutationResult(
                            NCEconomicAssetMutationStatus.Duplicate, current, null, null, null)
                        : Reject(NCEconomicAssetMutationStatus.Rejected,
                            "The stock-node identity is bound to different immutable data.");
                }
                if (attempt == 0 && command == InitialCommandState.Completed)
                    return Reject(NCEconomicAssetMutationStatus.StorageFailure,
                        "The completed command is missing its stock-node aggregate.");

                var eventId = Guid.NewGuid();
                var snapshot = new NCStockNodeSnapshot(
                    request.StockNodeId,
                    Normalize(request.StableKey),
                    request.Kind,
                    request.Owner,
                    Normalize(request.SemanticLocationKey),
                    [],
                    [],
                    request.AcceptsDeposits,
                    request.AllowsWithdrawals,
                    now,
                    now,
                    1,
                    eventId);
                var persisted = await Persist(
                    attempt == 0 && command == InitialCommandState.Available ? request.CommandId : Guid.NewGuid(),
                    CreateStockNodeCommand,
                    "stock-node",
                    scopeId,
                    [StockNodeWrite(null, snapshot, eventId, "living-city.stock-node.created", request.AuditSource)],
                    new AssetReceipt(scopeId, 1),
                    request.CorrelationId,
                    request.CausationId,
                    epoch,
                    now);
                if (persisted.Status is NCPersistentMutationStatus.Applied or
                    NCPersistentMutationStatus.DuplicateCompleted)
                {
                    var loaded = await GetStockNodeAsync(request.StockNodeId, true);
                    return loaded == null
                        ? Reject(NCEconomicAssetMutationStatus.StorageFailure,
                            "The committed stock node could not be reloaded.")
                        : new NCEconomicAssetMutationResult(
                            persisted.Status == NCPersistentMutationStatus.Applied
                                ? NCEconomicAssetMutationStatus.Applied
                                : NCEconomicAssetMutationStatus.Duplicate,
                            loaded,
                            null,
                            null,
                            null);
                }
                if (persisted.Status is NCPersistentMutationStatus.VersionConflict or
                    NCPersistentMutationStatus.DuplicateFailed)
                {
                    command = InitialCommandState.Failed;
                    continue;
                }
                return FromPersistence(persisted);
            }
            return Reject(NCEconomicAssetMutationStatus.VersionConflict,
                "Stock-node creation exceeded its optimistic retry limit.");
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to create stock node {request.StockNodeId}: {exception}");
            return Reject(NCEconomicAssetMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NCEconomicAssetMutationResult> CreateCommodityBatchAsync(
        NCCreateCommodityBatchRequest request)
    {
        if (!TryGetPolicy(out var policy) || !Valid(request, policy, out var commodity) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(_clock.IsReady
                ? NCEconomicAssetMutationStatus.Rejected
                : NCEconomicAssetMutationStatus.ClockUnavailable,
                "The commodity-batch request, prototype, policy, or active-world clock is invalid.");
        }
        if (!await ResolveParties(request.Owner, request.Initiator, request.Manufacturer))
            return Reject(NCEconomicAssetMutationStatus.PartyUnavailable,
                "The batch owner, manufacturer, or initiator is unavailable.");

        var scopeId = Key(request.BatchId);
        var command = await InspectCommand(request.CommandId, CreateCommodityBatchCommand,
            "commodity-batch", scopeId);
        if (command == InitialCommandState.Collision)
            return Reject(NCEconomicAssetMutationStatus.Rejected, "The command ID belongs to another operation.");
        if (command == InitialCommandState.InProgress)
            return Reject(NCEconomicAssetMutationStatus.StorageFailure, "The batch command is still in progress.");

        var gates = await AcquireGates(request.BatchId.Value, request.StockNodeId.Value);
        try
        {
            var existing = await GetCommodityBatchAsync(request.BatchId, true);
            if (existing != null)
            {
                var node = await GetStockNodeAsync(request.StockNodeId, true);
                return Same(existing.Snapshot, request, commodity) &&
                       node?.Snapshot.CommodityBatchIds.Contains(request.BatchId) == true
                    ? new NCEconomicAssetMutationResult(
                        NCEconomicAssetMutationStatus.Duplicate, node, existing, null, null)
                    : Reject(NCEconomicAssetMutationStatus.Rejected,
                        "The batch identity is bound to different immutable data.");
            }
            if (command == InitialCommandState.Completed)
                return Reject(NCEconomicAssetMutationStatus.StorageFailure,
                    "The completed command is missing its commodity batch.");

            var stock = await GetStockNodeAsync(request.StockNodeId, true);
            if (stock == null)
                return Reject(NCEconomicAssetMutationStatus.NotFound, "The destination stock node does not exist.");
            if (_materializationLeases.IsLeased(request.StockNodeId))
                return Reject(NCEconomicAssetMutationStatus.Rejected,
                    "Abstract commodity creation cannot target a materialized stock node.");
            if (stock.AggregateVersion != request.ExpectedStockNodeVersion)
                return Reject(NCEconomicAssetMutationStatus.VersionConflict, "The destination stock version changed.");
            if (stock.Snapshot.Owner != request.Owner || !stock.Snapshot.AcceptsDeposits)
                return Reject(NCEconomicAssetMutationStatus.Rejected,
                    "The destination stock node does not accept this owner's batch.");
            if (stock.Snapshot.CommodityBatchIds.Count >= policy.MaximumBatchesPerStockNode)
                return Reject(NCEconomicAssetMutationStatus.CapacityExceeded,
                    "The destination stock-node batch capacity is exhausted.");

            var eventId = Guid.NewGuid();
            var quality = request.Quality ?? commodity.DefaultQuality;
            var batch = new NCCommodityBatchSnapshot(
                request.BatchId,
                commodity.ID,
                request.Quantity,
                0,
                quality,
                request.Owner,
                StockLocation(request.StockNodeId),
                commodity.LegalStatus,
                commodity.TradeStatus,
                NCEconomicAssetLifecycle.Active,
                Provenance(request, now),
                commodity.ShelfLife == TimeSpan.Zero ? null : now.Advance(commodity.ShelfLife),
                now,
                now,
                1,
                eventId);
            var nodeEventId = Guid.NewGuid();
            var nextNode = stock.Snapshot with
            {
                CommodityBatchIds = stock.Snapshot.CommodityBatchIds.Append(request.BatchId)
                    .OrderBy(value => value.Value).ToArray(),
                UpdatedAt = now,
                DataVersion = checked(stock.Snapshot.DataVersion + 1),
                LastAppliedEventId = nodeEventId,
            };
            var persisted = await Persist(
                command == InitialCommandState.Available ? request.CommandId : Guid.NewGuid(),
                CreateCommodityBatchCommand,
                "commodity-batch",
                scopeId,
                [
                    CommodityBatchWrite(batch, eventId, request.AuditSource),
                    StockNodeWrite(stock, nextNode, nodeEventId, "living-city.stock-node.batch-added",
                        request.AuditSource),
                ],
                new AssetReceipt(scopeId, 1),
                request.CorrelationId,
                request.CausationId,
                epoch,
                now);
            if (persisted.Status is not (NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted))
                return FromPersistence(persisted);
            var loadedBatch = await GetCommodityBatchAsync(request.BatchId, true);
            var loadedNode = await GetStockNodeAsync(request.StockNodeId, true);
            return loadedBatch == null || loadedNode == null
                ? Reject(NCEconomicAssetMutationStatus.StorageFailure,
                    "The committed commodity batch or stock node could not be reloaded.")
                : new NCEconomicAssetMutationResult(
                    persisted.Status == NCPersistentMutationStatus.Applied
                        ? NCEconomicAssetMutationStatus.Applied
                        : NCEconomicAssetMutationStatus.Duplicate,
                    loadedNode,
                    loadedBatch,
                    null,
                    null);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to create commodity batch {request.BatchId}: {exception}");
            return Reject(NCEconomicAssetMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            ReleaseGates(gates);
        }
    }

    public async Task<NCEconomicAssetMutationResult> CreateItemInstanceAsync(NCCreateItemInstanceRequest request)
    {
        if (!TryGetPolicy(out var policy) || !Valid(request, policy, out var definition) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(_clock.IsReady
                ? NCEconomicAssetMutationStatus.Rejected
                : NCEconomicAssetMutationStatus.ClockUnavailable,
                "The item request, definition, policy, or active-world clock is invalid.");
        }
        if (!await ResolveParties(request.Owner, request.Initiator, request.Manufacturer))
            return Reject(NCEconomicAssetMutationStatus.PartyUnavailable,
                "The item owner, manufacturer, or initiator is unavailable.");

        var scopeId = Key(request.ItemId);
        var command = await InspectCommand(request.CommandId, CreateItemInstanceCommand, "item-instance", scopeId);
        if (command == InitialCommandState.Collision)
            return Reject(NCEconomicAssetMutationStatus.Rejected, "The command ID belongs to another operation.");
        if (command == InitialCommandState.InProgress)
            return Reject(NCEconomicAssetMutationStatus.StorageFailure, "The item command is still in progress.");

        var gates = await AcquireGates(request.ItemId.Value, request.StockNodeId.Value);
        try
        {
            var existing = await GetItemAsync(request.ItemId, true);
            if (existing != null)
            {
                var node = await GetStockNodeAsync(request.StockNodeId, true);
                return Same(existing.Snapshot, request, definition) &&
                       node?.Snapshot.ItemIds.Contains(request.ItemId) == true
                    ? new NCEconomicAssetMutationResult(
                        NCEconomicAssetMutationStatus.Duplicate, node, null, existing, null)
                    : Reject(NCEconomicAssetMutationStatus.Rejected,
                        "The item identity is bound to different immutable data.");
            }
            if (command == InitialCommandState.Completed)
                return Reject(NCEconomicAssetMutationStatus.StorageFailure,
                    "The completed command is missing its item instance.");

            var stock = await GetStockNodeAsync(request.StockNodeId, true);
            if (stock == null)
                return Reject(NCEconomicAssetMutationStatus.NotFound, "The destination stock node does not exist.");
            if (_materializationLeases.IsLeased(request.StockNodeId))
                return Reject(NCEconomicAssetMutationStatus.Rejected,
                    "Abstract item creation cannot target a materialized stock node.");
            if (stock.AggregateVersion != request.ExpectedStockNodeVersion)
                return Reject(NCEconomicAssetMutationStatus.VersionConflict, "The destination stock version changed.");
            if (stock.Snapshot.Owner != request.Owner || !stock.Snapshot.AcceptsDeposits)
                return Reject(NCEconomicAssetMutationStatus.Rejected,
                    "The destination stock node does not accept this owner's item.");
            if (stock.Snapshot.ItemIds.Count >= policy.MaximumItemsPerStockNode)
                return Reject(NCEconomicAssetMutationStatus.CapacityExceeded,
                    "The destination stock-node item capacity is exhausted.");

            var eventId = Guid.NewGuid();
            var item = new NCItemInstanceSnapshot(
                request.ItemId,
                definition.ID,
                request.SerialNumber.Trim(),
                request.Condition ?? definition.DefaultCondition,
                null,
                request.Owner,
                StockLocation(request.StockNodeId),
                definition.LegalStatus,
                definition.TradeStatus,
                NCEconomicAssetLifecycle.Active,
                Provenance(request, now),
                null,
                [],
                now,
                now,
                1,
                eventId);
            var nodeEventId = Guid.NewGuid();
            var nextNode = stock.Snapshot with
            {
                ItemIds = stock.Snapshot.ItemIds.Append(request.ItemId).OrderBy(value => value.Value).ToArray(),
                UpdatedAt = now,
                DataVersion = checked(stock.Snapshot.DataVersion + 1),
                LastAppliedEventId = nodeEventId,
            };
            var persisted = await Persist(
                command == InitialCommandState.Available ? request.CommandId : Guid.NewGuid(),
                CreateItemInstanceCommand,
                "item-instance",
                scopeId,
                [
                    ItemWrite(item, eventId, request.AuditSource),
                    StockNodeWrite(stock, nextNode, nodeEventId, "living-city.stock-node.item-added",
                        request.AuditSource),
                ],
                new AssetReceipt(scopeId, 1),
                request.CorrelationId,
                request.CausationId,
                epoch,
                now);
            if (persisted.Status is not (NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted))
                return FromPersistence(persisted);
            var loadedItem = await GetItemAsync(request.ItemId, true);
            var loadedNode = await GetStockNodeAsync(request.StockNodeId, true);
            return loadedItem == null || loadedNode == null
                ? Reject(NCEconomicAssetMutationStatus.StorageFailure,
                    "The committed item or stock node could not be reloaded.")
                : new NCEconomicAssetMutationResult(
                    persisted.Status == NCPersistentMutationStatus.Applied
                        ? NCEconomicAssetMutationStatus.Applied
                        : NCEconomicAssetMutationStatus.Duplicate,
                    loadedNode,
                    null,
                    loadedItem,
                    null);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to create item instance {request.ItemId}: {exception}");
            return Reject(NCEconomicAssetMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            ReleaseGates(gates);
        }
    }

    public bool ValidatePrototypes(List<string> errors)
    {
        var initial = errors.Count;
        foreach (var commodity in _prototypes.EnumeratePrototypes<NCCommodityPrototype>())
        {
            if (!Valid(commodity))
                errors.Add($"Commodity {commodity.ID} is invalid.");
        }
        foreach (var item in _prototypes.EnumeratePrototypes<NCItemDefinitionPrototype>())
        {
            if (!Valid(item))
                errors.Add($"Item definition {item.ID} is invalid.");
        }
        foreach (var policy in _prototypes.EnumeratePrototypes<NCEconomicAssetPolicyPrototype>())
        {
            if (!Valid(policy))
                errors.Add($"Economic asset policy {policy.ID} is invalid.");
        }
        foreach (var policy in _prototypes.EnumeratePrototypes<NCEconomicPricingPolicyPrototype>())
        {
            if (!Valid(policy))
                errors.Add($"Economic pricing policy {policy.ID} is invalid.");
        }
        foreach (var service in _prototypes.EnumeratePrototypes<NCEconomicServicePrototype>())
        {
            if (!Valid(service))
                errors.Add($"Economic service {service.ID} is invalid.");
        }
        foreach (var access in _prototypes.EnumeratePrototypes<NCEconomicInformationAccessPrototype>())
        {
            if (!Valid(access))
                errors.Add($"Economic information/access definition {access.ID} is invalid.");
        }
        if (!TryGetPolicy(out _))
            errors.Add("The Default economic asset policy is missing or invalid.");
        if (!TryGetPricingPolicy(out _))
            errors.Add("The Default economic pricing policy is missing or invalid.");
        return errors.Count == initial;
    }

    private NCPersistentAggregateWrite StockNodeWrite(
        NCStockNodeRecord? current,
        NCStockNodeSnapshot next,
        Guid eventId,
        string eventType,
        string auditSource) => new(
        eventId,
        eventType,
        StockNodeAggregateType,
        Key(next.StockNodeId),
        current?.AggregateVersion ?? 0,
        SnapshotFormatVersion,
        JsonSerializer.Serialize(next, JsonOptions),
        JsonSerializer.Serialize(new AssetEvent(Key(next.StockNodeId), auditSource.Trim()), JsonOptions));

    private NCPersistentAggregateWrite CommodityBatchWrite(
        NCCommodityBatchSnapshot batch,
        Guid eventId,
        string auditSource) => new(
        eventId,
        "living-city.commodity-batch.created",
        CommodityBatchAggregateType,
        Key(batch.BatchId),
        0,
        SnapshotFormatVersion,
        JsonSerializer.Serialize(batch, JsonOptions),
        JsonSerializer.Serialize(new AssetEvent(Key(batch.BatchId), auditSource.Trim()), JsonOptions));

    private NCPersistentAggregateWrite ItemWrite(
        NCItemInstanceSnapshot item,
        Guid eventId,
        string auditSource) => new(
        eventId,
        "living-city.item-instance.created",
        ItemInstanceAggregateType,
        Key(item.ItemId),
        0,
        SnapshotFormatVersion,
        JsonSerializer.Serialize(item, JsonOptions),
        JsonSerializer.Serialize(new AssetEvent(Key(item.ItemId), auditSource.Trim()), JsonOptions));

    private Task<NCPersistentBatchMutationResult> Persist(
        Guid commandId,
        string commandType,
        string scopeType,
        string scopeId,
        IReadOnlyList<NCPersistentAggregateWrite> writes,
        AssetReceipt receipt,
        Guid correlationId,
        Guid? causationId,
        NCSessionEpoch epoch,
        NCActiveWorldTime now) => _database.ApplyNCPersistentBatchMutationAsync(
        new NCPersistentBatchMutationRequest(
            commandId,
            commandType,
            scopeType,
            scopeId,
            writes,
            JsonSerializer.Serialize(receipt, JsonOptions),
            correlationId,
            causationId,
            epoch,
            now,
            []));

    private async Task<InitialCommandState> InspectCommand(
        Guid commandId,
        string commandType,
        string scopeType,
        string scopeId)
    {
        var command = await _database.GetNCPersistentCommandResultAsync(commandId);
        if (command == null)
            return InitialCommandState.Available;
        if (command.CommandType != commandType || command.ScopeType != scopeType || command.ScopeId != scopeId)
            return InitialCommandState.Collision;
        return command.Status switch
        {
            NCPersistentMutationStatus.InProgress => InitialCommandState.InProgress,
            NCPersistentMutationStatus.DuplicateCompleted => InitialCommandState.Completed,
            _ => InitialCommandState.Failed,
        };
    }

    private async Task<bool> ResolveParties(NCPartyRef first, NCPartyRef second, NCPartyRef optional = default)
    {
        var firstTask = _parties.ResolveAsync(first, true);
        var secondTask = _parties.ResolveAsync(second, true);
        Task<NCPartyResolution>? optionalTask = optional.IsValid ? _parties.ResolveAsync(optional, true) : null;
        if (optionalTask == null)
            await Task.WhenAll(firstTask, secondTask);
        else
            await Task.WhenAll(firstTask, secondTask, optionalTask);
        return (await firstTask).IsResolved && (await secondTask).IsResolved &&
               (optionalTask == null || (await optionalTask).IsResolved);
    }

    private bool TryGetPolicy(out NCEconomicAssetPolicyPrototype policy) =>
        _prototypes.TryIndex(DefaultPolicy, out policy!) && Valid(policy);

    private bool Valid(NCCommodityPrototype value) => IsStableKey(value.StableKey, 128) &&
        Enum.IsDefined(value.Unit) && Enum.IsDefined(value.Domain) && value.MaximumBatchQuantity > 0 &&
        value.DefaultQuality is >= 0 and <= 1_000 && value.ShelfLife >= TimeSpan.Zero &&
        value.BaseUnitPrice is null or >= 0 && Enum.IsDefined(value.LegalStatus) &&
        Enum.IsDefined(value.TradeStatus) &&
        value.MaterializedUnitsPerEntity > 0 &&
        (value.MaterializedPrototype == null || _prototypes.HasIndex<EntityPrototype>(value.MaterializedPrototype)) &&
        (value.MaterializedReagent == null || _prototypes.HasIndex(value.MaterializedReagent.Value)) &&
        (value.MaterializedReagent == null && string.IsNullOrWhiteSpace(value.MaterializedSolutionId) ||
            value.MaterializedReagent != null && IsPrototypeKey(value.MaterializedSolutionId, 64));

    private bool Valid(NCItemDefinitionPrototype value)
    {
        if (!IsStableKey(value.StableKey, 128) || !Enum.IsDefined(value.Domain) || value.BasePrice is < 0 ||
            value.DefaultCondition is < 0 or > 1_000 || !Enum.IsDefined(value.LegalStatus) ||
            !Enum.IsDefined(value.TradeStatus) || value.MaximumInstalledParts is < 0 or > 18 ||
            value.AllowedInstalledPartDefinitions.Count > value.MaximumInstalledParts ||
            value.AllowedInstalledPartDefinitions.Distinct().Count() !=
            value.AllowedInstalledPartDefinitions.Count ||
            value.AllowedInstalledPartDefinitions.Any(id => !_prototypes.HasIndex(id)) ||
            value.Domain != NCEconomicAssetDomain.WeaponAssembly &&
            value.AllowedInstalledPartDefinitions.Count > 0 ||
            value.MaterializedPrototype != null &&
            !_prototypes.HasIndex<EntityPrototype>(value.MaterializedPrototype))
        {
            return false;
        }
        if (value.Domain == NCEconomicAssetDomain.Generic)
            return string.IsNullOrWhiteSpace(value.DomainPrototypeId);
        if (!IsPrototypeKey(value.DomainPrototypeId, 128))
            return false;
        return value.Domain != NCEconomicAssetDomain.Cyberware ||
               _prototypes.HasIndex(new ProtoId<NCCyberwarePrototype>(value.DomainPrototypeId));
    }

    private static bool Valid(NCEconomicAssetPolicyPrototype value) =>
        value.MaximumBatchesPerStockNode is > 0 and <= AbsoluteMaximumStockEntries &&
        value.MaximumItemsPerStockNode is > 0 and <= AbsoluteMaximumStockEntries &&
        value.MaximumSourceEvents is > 0 and <= 64 && value.MaximumStableKeyLength is > 0 and <= 256 &&
        value.MaximumSerialLength is > 0 and <= 256 && value.MaximumQueryCount is > 0 and <= 500;

    private static bool Valid(NCCreateStockNodeRequest value, NCEconomicAssetPolicyPrototype policy) =>
        value.CommandId != Guid.Empty && value.StockNodeId.IsValid &&
        IsStableKey(value.StableKey, policy.MaximumStableKeyLength) && Enum.IsDefined(value.Kind) &&
        value.Owner.IsValid && IsStableKey(value.SemanticLocationKey, policy.MaximumStableKeyLength) &&
        value.Initiator.IsValid && value.CorrelationId != Guid.Empty && ValidAudit(value.AuditSource);

    private bool Valid(
        NCCreateCommodityBatchRequest value,
        NCEconomicAssetPolicyPrototype policy,
        out NCCommodityPrototype commodity)
    {
        commodity = default!;
        return value.CommandId != Guid.Empty && value.BatchId.IsValid && value.Quantity > 0 &&
            value.Owner.IsValid && value.StockNodeId.IsValid && value.ExpectedStockNodeVersion >= 1 &&
            IsStableKey(value.OriginKey, policy.MaximumStableKeyLength) && value.SourceWorldEventIds.Count is > 0 &&
            value.SourceWorldEventIds.Count <= policy.MaximumSourceEvents &&
            value.SourceWorldEventIds.All(id => id != Guid.Empty) &&
            value.SourceWorldEventIds.Distinct().Count() == value.SourceWorldEventIds.Count &&
            value.Initiator.IsValid && value.CorrelationId != Guid.Empty && ValidAudit(value.AuditSource) &&
            _prototypes.TryIndex(value.Commodity, out commodity!) && Valid(commodity) &&
            value.Quantity <= commodity.MaximumBatchQuantity && value.Quality is null or >= 0 and <= 1_000;
    }

    private bool Valid(
        NCCreateItemInstanceRequest value,
        NCEconomicAssetPolicyPrototype policy,
        out NCItemDefinitionPrototype definition)
    {
        definition = default!;
        return value.CommandId != Guid.Empty && value.ItemId.IsValid && value.Owner.IsValid &&
            value.StockNodeId.IsValid && value.ExpectedStockNodeVersion >= 1 &&
            value.SerialNumber != null && value.SerialNumber == value.SerialNumber.Trim() &&
            value.SerialNumber.Length <= policy.MaximumSerialLength &&
            IsStableKey(value.OriginKey, policy.MaximumStableKeyLength) && value.SourceWorldEventIds.Count is > 0 &&
            value.SourceWorldEventIds.Count <= policy.MaximumSourceEvents &&
            value.SourceWorldEventIds.All(id => id != Guid.Empty) &&
            value.SourceWorldEventIds.Distinct().Count() == value.SourceWorldEventIds.Count &&
            value.Initiator.IsValid && value.CorrelationId != Guid.Empty && ValidAudit(value.AuditSource) &&
            _prototypes.TryIndex(value.Definition, out definition!) && Valid(definition) &&
            (!definition.SerialRequired || value.SerialNumber.Length > 0) && value.Condition is null or >= 0 and <= 1_000;
    }

    private static bool Valid(NCStockNodeSnapshot? value, NCStockNodeId expected) => value != null &&
        value.StockNodeId == expected && expected.IsValid && IsStableKey(value.StableKey, 256) &&
        Enum.IsDefined(value.Kind) && value.Owner.IsValid && IsStableKey(value.SemanticLocationKey, 256) &&
        value.CommodityBatchIds.Count <= AbsoluteMaximumStockEntries && value.ItemIds.Count <= AbsoluteMaximumStockEntries &&
        value.CommodityBatchIds.All(id => id.IsValid) && value.ItemIds.All(id => id.IsValid) &&
        value.CommodityBatchIds.Distinct().Count() == value.CommodityBatchIds.Count &&
        value.ItemIds.Distinct().Count() == value.ItemIds.Count && value.CreatedAt.IsValid &&
        value.UpdatedAt >= value.CreatedAt && value.DataVersion > 0 && value.LastAppliedEventId != Guid.Empty;

    private bool Valid(NCCommodityBatchSnapshot? value, NCCommodityBatchId expected) => value != null &&
        value.BatchId == expected && expected.IsValid &&
        _prototypes.TryIndex(new ProtoId<NCCommodityPrototype>(value.CommodityId), out var commodity) &&
        Valid(commodity) && value.Quantity >= 0 && value.Quantity <= commodity.MaximumBatchQuantity &&
        value.ReservedQuantity >= 0 && value.ReservedQuantity <= value.Quantity &&
        value.Quality is >= 0 and <= 1_000 && value.Owner.IsValid && Valid(value.Location) &&
        Enum.IsDefined(value.LegalStatus) && Enum.IsDefined(value.TradeStatus) &&
        ValidLifecycle(value) && Valid(value.Provenance, 64) &&
        (value.ExpiresAt == null || value.ExpiresAt > value.CreatedAt) && value.CreatedAt.IsValid &&
        value.UpdatedAt >= value.CreatedAt && value.DataVersion > 0 && value.LastAppliedEventId != Guid.Empty;

    private bool Valid(NCItemInstanceSnapshot? value, NCItemId expected) => value != null && value.ItemId == expected &&
        expected.IsValid && _prototypes.TryIndex(new ProtoId<NCItemDefinitionPrototype>(value.DefinitionId),
            out var definition) && Valid(definition) && value.SerialNumber.Length <= 256 &&
        (!definition.SerialRequired || value.SerialNumber.Length > 0) && value.Condition is >= 0 and <= 1_000 &&
        (!value.ReservationId.HasValue || value.ReservationId.Value.IsValid) &&
        value.Owner.IsValid && Valid(value.Location) && Enum.IsDefined(value.LegalStatus) &&
        Enum.IsDefined(value.TradeStatus) && ValidLifecycle(value) &&
        Valid(value.Provenance, 64) &&
        value.InstalledPartIds.Count <= definition.MaximumInstalledParts &&
        value.InstalledPartIds.All(id => id.IsValid && id != value.ItemId) &&
        value.InstalledPartIds.Distinct().Count() == value.InstalledPartIds.Count &&
        (!value.ParentItemId.HasValue && value.Location.Kind != NCEconomicAssetLocationKind.InstalledInItem ||
            value.ParentItemId is { IsValid: true } parentId && parentId != value.ItemId &&
            value.Location.Kind == NCEconomicAssetLocationKind.InstalledInItem &&
            value.Location.StableId == parentId.ToString()) &&
        (value.Location.Kind != NCEconomicAssetLocationKind.InstalledInActor ||
            definition.Domain == NCEconomicAssetDomain.Cyberware &&
            Guid.TryParse(value.Location.StableId, out var installedActor) && installedActor != Guid.Empty) &&
        value.CreatedAt.IsValid && value.UpdatedAt >= value.CreatedAt && value.DataVersion > 0 &&
        value.LastAppliedEventId != Guid.Empty;

    private static bool Valid(NCEconomicAssetProvenance value, int maximumSourceEvents) => value != null &&
        IsStableKey(value.OriginKey, 256) && value.SourceWorldEventIds.Count is > 0 &&
        value.SourceWorldEventIds.Count <= maximumSourceEvents && value.SourceWorldEventIds.All(id => id != Guid.Empty) &&
        value.SourceWorldEventIds.Distinct().Count() == value.SourceWorldEventIds.Count && value.ProducedAt.IsValid;

    private static bool Valid(NCEconomicAssetLocation value) => Enum.IsDefined(value.Kind) &&
        !string.IsNullOrWhiteSpace(value.StableId) && value.StableId.Length <= 256 &&
        value.StableId == value.StableId.Trim().ToLowerInvariant();

    private static bool ValidLifecycle(NCCommodityBatchSnapshot value) => value.Lifecycle switch
    {
        NCEconomicAssetLifecycle.Active => value.Quantity > 0 &&
            value.Location.Kind is not (NCEconomicAssetLocationKind.Consumed or NCEconomicAssetLocationKind.Destroyed),
        NCEconomicAssetLifecycle.Consumed => value.Quantity == 0 && value.ReservedQuantity == 0 &&
            value.Location.Kind == NCEconomicAssetLocationKind.Consumed,
        NCEconomicAssetLifecycle.Destroyed => value.Quantity == 0 && value.ReservedQuantity == 0 &&
            value.Location.Kind == NCEconomicAssetLocationKind.Destroyed,
        _ => false,
    };

    private static bool ValidLifecycle(NCItemInstanceSnapshot value) => value.Lifecycle switch
    {
        NCEconomicAssetLifecycle.Active =>
            value.Location.Kind is not (NCEconomicAssetLocationKind.Consumed or NCEconomicAssetLocationKind.Destroyed),
        NCEconomicAssetLifecycle.Consumed => !value.ReservationId.HasValue && !value.ParentItemId.HasValue &&
            value.InstalledPartIds.Count == 0 &&
            value.Location.Kind == NCEconomicAssetLocationKind.Consumed,
        NCEconomicAssetLifecycle.Destroyed => !value.ReservationId.HasValue && !value.ParentItemId.HasValue &&
            value.InstalledPartIds.Count == 0 &&
            value.Location.Kind == NCEconomicAssetLocationKind.Destroyed,
        _ => false,
    };

    private static bool Same(NCStockNodeSnapshot value, NCCreateStockNodeRequest request) =>
        value.StockNodeId == request.StockNodeId && value.StableKey == Normalize(request.StableKey) &&
        value.Kind == request.Kind && value.Owner == request.Owner &&
        value.SemanticLocationKey == Normalize(request.SemanticLocationKey) &&
        value.AcceptsDeposits == request.AcceptsDeposits && value.AllowsWithdrawals == request.AllowsWithdrawals;

    private static bool Same(
        NCCommodityBatchSnapshot value,
        NCCreateCommodityBatchRequest request,
        NCCommodityPrototype commodity) => value.BatchId == request.BatchId && value.CommodityId == commodity.ID &&
        value.Quantity == request.Quantity && value.Quality == (request.Quality ?? commodity.DefaultQuality) &&
        value.Owner == request.Owner && value.Location == StockLocation(request.StockNodeId) &&
        value.Provenance.OriginKey == Normalize(request.OriginKey) &&
        value.Provenance.Manufacturer == request.Manufacturer &&
        value.Provenance.SourceWorldEventIds.SequenceEqual(request.SourceWorldEventIds.Order()) &&
        (commodity.ShelfLife == TimeSpan.Zero || value.ExpiresAt == value.CreatedAt.Advance(commodity.ShelfLife));

    private static bool Same(
        NCItemInstanceSnapshot value,
        NCCreateItemInstanceRequest request,
        NCItemDefinitionPrototype definition) => value.ItemId == request.ItemId && value.DefinitionId == definition.ID &&
        value.SerialNumber == request.SerialNumber.Trim() &&
        value.Condition == (request.Condition ?? definition.DefaultCondition) && value.Owner == request.Owner &&
        value.Location == StockLocation(request.StockNodeId) &&
        value.Provenance.OriginKey == Normalize(request.OriginKey) &&
        value.Provenance.Manufacturer == request.Manufacturer &&
        value.Provenance.SourceWorldEventIds.SequenceEqual(request.SourceWorldEventIds.Order());

    private static NCEconomicAssetProvenance Provenance(NCCreateCommodityBatchRequest request, NCActiveWorldTime now) =>
        new(Normalize(request.OriginKey), request.Manufacturer, request.SourceWorldEventIds.Order().ToArray(), now);

    private static NCEconomicAssetProvenance Provenance(NCCreateItemInstanceRequest request, NCActiveWorldTime now) =>
        new(Normalize(request.OriginKey), request.Manufacturer, request.SourceWorldEventIds.Order().ToArray(), now);

    private static NCEconomicAssetLocation StockLocation(NCStockNodeId id) =>
        new(NCEconomicAssetLocationKind.StockNode, Key(id));

    private static bool ValidAudit(string value) => !string.IsNullOrWhiteSpace(value) &&
        value.Trim().Length <= MaximumAuditSourceLength;
    private static bool IsPrototypeKey(string value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && value == value.Trim();
    private static bool IsStableKey(string value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && value == Normalize(value) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private static NCStockNodeRecord? DeserializeStockNode(NCAggregateSnapshotData? stored, NCStockNodeId expected)
    {
        if (!StoredShape(stored))
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCStockNodeSnapshot>(stored!.Payload, JsonOptions);
            return Valid(snapshot, expected) && snapshot!.DataVersion == stored.AggregateVersion
                ? new NCStockNodeRecord(snapshot, stored.AggregateVersion, stored.SessionEpoch, stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private NCCommodityBatchRecord? DeserializeCommodityBatch(
        NCAggregateSnapshotData? stored,
        NCCommodityBatchId expected)
    {
        if (!StoredShape(stored))
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCCommodityBatchSnapshot>(stored!.Payload, JsonOptions);
            return Valid(snapshot, expected) && snapshot!.DataVersion == stored.AggregateVersion
                ? new NCCommodityBatchRecord(snapshot, stored.AggregateVersion, stored.SessionEpoch,
                    stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private NCItemInstanceRecord? DeserializeItem(NCAggregateSnapshotData? stored, NCItemId expected)
    {
        if (!StoredShape(stored))
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCItemInstanceSnapshot>(stored!.Payload, JsonOptions);
            return Valid(snapshot, expected) && snapshot!.DataVersion == stored.AggregateVersion
                ? new NCItemInstanceRecord(snapshot, stored.AggregateVersion, stored.SessionEpoch,
                    stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool StoredShape(NCAggregateSnapshotData? stored) => stored != null &&
        stored.FormatVersion == SnapshotFormatVersion && stored.SessionEpoch.IsValid && stored.ActiveWorldTime.IsValid;

    private NCStockNodeRecord CacheNewest(NCStockNodeRecord candidate) =>
        _stockNodes.AddOrUpdate(candidate.Snapshot.StockNodeId, candidate,
            (_, current) => current.AggregateVersion > candidate.AggregateVersion ? current : candidate);
    private NCCommodityBatchRecord CacheNewest(NCCommodityBatchRecord candidate) =>
        _batches.AddOrUpdate(candidate.Snapshot.BatchId, candidate,
            (_, current) => current.AggregateVersion > candidate.AggregateVersion ? current : candidate);
    private NCItemInstanceRecord CacheNewest(NCItemInstanceRecord candidate) =>
        _items.AddOrUpdate(candidate.Snapshot.ItemId, candidate,
            (_, current) => current.AggregateVersion > candidate.AggregateVersion ? current : candidate);

    private SemaphoreSlim Gate(Guid key) => _gates[(key.GetHashCode() & int.MaxValue) % _gates.Length];

    private async Task<SemaphoreSlim[]> AcquireGates(params Guid[] keys)
    {
        var gates = keys.Select(Gate).Distinct().OrderBy(gate => Array.IndexOf(_gates, gate)).ToArray();
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

    private static string Key(NCStockNodeId id) => id.Value.ToString("D");
    private static string Key(NCCommodityBatchId id) => id.Value.ToString("D");
    private static string Key(NCItemId id) => id.Value.ToString("D");

    private static NCEconomicAssetLocation TerminalLocation(NCEconomicAssetLocationKind kind) =>
        new(kind, kind == NCEconomicAssetLocationKind.Consumed ? "consumed" : "destroyed");

    private static NCEconomicAssetMutationResult Reject(NCEconomicAssetMutationStatus status, string error) =>
        new(status, null, null, null, error);

    private static NCEconomicAssetMutationResult FromPersistence(NCPersistentBatchMutationResult result) =>
        Reject(result.Status switch
        {
            NCPersistentMutationStatus.VersionConflict => NCEconomicAssetMutationStatus.VersionConflict,
            NCPersistentMutationStatus.ClockRejected => NCEconomicAssetMutationStatus.ClockUnavailable,
            _ => NCEconomicAssetMutationStatus.StorageFailure,
        }, result.Error ?? "The economic asset mutation was not committed.");

    private sealed record AssetReceipt(string AggregateId, long AggregateVersion);
    private sealed record AssetEvent(string AggregateId, string AuditSource);

    private enum InitialCommandState : byte
    {
        Available,
        Completed,
        Failed,
        InProgress,
        Collision,
    }
}
