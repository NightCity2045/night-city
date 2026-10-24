// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._NC.LivingCity.Economy;

namespace Content.Server._NC.LivingCity.Economy;

/// <summary>
/// Atomic custody and ownership transfers for unique items and fungible commodity batches.
/// </summary>
public sealed partial class NCEconomicAssetRegistrySystem
{
    public const string AssetTransferAggregateType = "living-city.asset-transfer";

    private const string TransferItemCommand = "living-city.asset-transfer.item";
    private const string TransferCommodityCommand = "living-city.asset-transfer.commodity";

    private readonly ConcurrentDictionary<NCEconomicTransferId, NCEconomicAssetTransferRecord> _transfers = [];

    public int CachedTransferCount => _transfers.Count;

    public bool Unload(NCEconomicTransferId id) => _transfers.TryRemove(id, out _);

    public async Task<NCEconomicAssetTransferRecord?> GetTransferAsync(
        NCEconomicTransferId id,
        bool forceReload = false)
    {
        if (!id.IsValid)
            return null;
        if (!forceReload && _transfers.TryGetValue(id, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(AssetTransferAggregateType, Key(id));
        var record = DeserializeTransfer(stored, id);
        if (record == null)
            return null;

        return _transfers.AddOrUpdate(id, record,
            (_, current) => current.AggregateVersion > record.AggregateVersion ? current : record);
    }

    public async Task<NCEconomicAssetTransferResult> TransferItemAsync(NCTransferItemRequest request)
    {
        if (!Valid(request) || !TryGetPolicy(out var policy) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return RejectTransfer(_clock.IsReady
                ? NCEconomicAssetMutationStatus.Rejected
                : NCEconomicAssetMutationStatus.ClockUnavailable,
                "The item-transfer request, policy, or active-world clock is invalid.");
        }

        try
        {
            if (!await ResolveParties(request.ExpectedOwner, request.NewOwner, request.Initiator))
                return RejectTransfer(NCEconomicAssetMutationStatus.PartyUnavailable,
                    "The previous owner, new owner, or initiator is unavailable.");

            var command = await InspectCommand(request.CommandId, TransferItemCommand, "asset-transfer",
                Key(request.TransferId));
            if (command == InitialCommandState.Collision)
                return RejectTransfer(NCEconomicAssetMutationStatus.Rejected,
                    "The command ID belongs to another operation.");
            if (command == InitialCommandState.InProgress)
                return RejectTransfer(NCEconomicAssetMutationStatus.StorageFailure,
                    "The item-transfer command is still in progress.");

            var gates = await AcquireGates(
                request.TransferId.Value,
                request.ItemId.Value,
                request.SourceStockNodeId.Value,
                request.DestinationStockNodeId.Value);
            try
            {
                var existing = await GetTransferAsync(request.TransferId, true);
                if (existing != null)
                {
                    if (!Same(existing.Snapshot, request))
                        return RejectTransfer(NCEconomicAssetMutationStatus.Rejected,
                            "The transfer identity is bound to different immutable data.");
                    await ReconcileOwnershipAsync(await LoadOwnershipContextAsync(request.TransferId.Value,
                        Key(request.ItemId), request.ExpectedOwner, request.NewOwner, epoch, now));
                    return await LoadItemTransferResult(request, existing, NCEconomicAssetMutationStatus.Duplicate);
                }
                if (command == InitialCommandState.Completed)
                    return RejectTransfer(NCEconomicAssetMutationStatus.StorageFailure,
                        "The completed command is missing its transfer aggregate.");

                var item = await GetItemAsync(request.ItemId, true);
                var source = await GetStockNodeAsync(request.SourceStockNodeId, true);
                var destination = await GetStockNodeAsync(request.DestinationStockNodeId, true);
                var stateError = ValidateItemTransferState(request, item, source, destination, policy);
                if (stateError != null)
                    return stateError;

                var installedParts = await LoadAssemblyPartsAsync(item!);
                if (installedParts == null)
                    return RejectTransfer(NCEconomicAssetMutationStatus.Rejected,
                        "The item assembly contains a missing or inconsistent installed part.");

                var itemEventId = Guid.NewGuid();
                var nextItem = item!.Snapshot with
                {
                    Owner = request.NewOwner,
                    Location = StockLocation(request.DestinationStockNodeId),
                    UpdatedAt = now,
                    DataVersion = checked(item.Snapshot.DataVersion + 1),
                    LastAppliedEventId = itemEventId,
                };
                var sourceEventId = Guid.NewGuid();
                var nextSource = source!.Snapshot with
                {
                    ItemIds = source.Snapshot.ItemIds.Where(id => id != request.ItemId).ToArray(),
                    UpdatedAt = now,
                    DataVersion = checked(source.Snapshot.DataVersion + 1),
                    LastAppliedEventId = sourceEventId,
                };
                var destinationEventId = Guid.NewGuid();
                var nextDestination = destination!.Snapshot with
                {
                    ItemIds = destination.Snapshot.ItemIds.Append(request.ItemId)
                        .OrderBy(id => id.Value).ToArray(),
                    UpdatedAt = now,
                    DataVersion = checked(destination.Snapshot.DataVersion + 1),
                    LastAppliedEventId = destinationEventId,
                };
                var transferEventId = Guid.NewGuid();
                var transfer = new NCEconomicAssetTransferSnapshot(
                    request.TransferId,
                    null,
                    null,
                    NCEconomicTransferAssetKind.ItemInstance,
                    Key(request.ItemId),
                    Key(request.ItemId),
                    1,
                    request.SourceStockNodeId,
                    request.DestinationStockNodeId,
                    request.ExpectedOwner,
                    request.NewOwner,
                    request.Initiator,
                    now,
                    1,
                    transferEventId);

                var writes = new List<NCPersistentAggregateWrite>
                {
                    ItemMutationWrite(item, nextItem, itemEventId,
                        "living-city.item-instance.transferred", request.AuditSource),
                    StockNodeWrite(source, nextSource, sourceEventId,
                        "living-city.stock-node.item-withdrawn", request.AuditSource),
                    StockNodeWrite(destination, nextDestination, destinationEventId,
                        "living-city.stock-node.item-deposited", request.AuditSource),
                    TransferWrite(transfer, transferEventId, request.AuditSource),
                };
                foreach (var part in installedParts)
                {
                    var partEventId = Guid.NewGuid();
                    var nextPart = part.Snapshot with
                    {
                        Owner = request.NewOwner,
                        UpdatedAt = now,
                        DataVersion = checked(part.Snapshot.DataVersion + 1),
                        LastAppliedEventId = partEventId,
                    };
                    writes.Add(ItemMutationWrite(part, nextPart, partEventId,
                        "living-city.item-instance.transferred-with-assembly", request.AuditSource));
                }

                await using var ownership = await PrepareOwnershipAsync(new(request.TransferId.Value, item,
                    installedParts, request.ExpectedOwner, request.NewOwner, epoch, now), writes);
                if (!await ownership.TryBeginCommitAsync())
                    return RejectTransfer(NCEconomicAssetMutationStatus.Rejected, ownership.Error!);
                var persisted = await Persist(
                    command == InitialCommandState.Available ? request.CommandId : Guid.NewGuid(),
                    TransferItemCommand,
                    "asset-transfer",
                    Key(request.TransferId),
                    writes,
                    new AssetReceipt(Key(request.TransferId), 1),
                    request.CorrelationId,
                    request.CausationId,
                    epoch,
                    now);
                if (persisted.Status is not (NCPersistentMutationStatus.Applied or
                    NCPersistentMutationStatus.DuplicateCompleted))
                    return FromPersistenceTransfer(persisted);

                var loaded = await GetTransferAsync(request.TransferId, true);
                return loaded == null
                    ? RejectTransfer(NCEconomicAssetMutationStatus.StorageFailure,
                        "The committed item transfer could not be reloaded.")
                    : await LoadItemTransferResult(request, loaded,
                        persisted.Status == NCPersistentMutationStatus.Applied
                            ? NCEconomicAssetMutationStatus.Applied
                            : NCEconomicAssetMutationStatus.Duplicate);
            }
            finally
            {
                ReleaseGates(gates);
            }
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to transfer item {request.ItemId}: {exception}");
            return RejectTransfer(NCEconomicAssetMutationStatus.StorageFailure, exception.Message);
        }
    }

    public async Task<NCEconomicAssetTransferResult> TransferCommodityAsync(NCTransferCommodityRequest request)
    {
        if (!Valid(request) || !TryGetPolicy(out var policy) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return RejectTransfer(_clock.IsReady
                ? NCEconomicAssetMutationStatus.Rejected
                : NCEconomicAssetMutationStatus.ClockUnavailable,
                "The commodity-transfer request, policy, or active-world clock is invalid.");
        }

        try
        {
            if (!await ResolveParties(request.ExpectedOwner, request.NewOwner, request.Initiator))
                return RejectTransfer(NCEconomicAssetMutationStatus.PartyUnavailable,
                    "The previous owner, new owner, or initiator is unavailable.");

            var command = await InspectCommand(request.CommandId, TransferCommodityCommand, "asset-transfer",
                Key(request.TransferId));
            if (command == InitialCommandState.Collision)
                return RejectTransfer(NCEconomicAssetMutationStatus.Rejected,
                    "The command ID belongs to another operation.");
            if (command == InitialCommandState.InProgress)
                return RejectTransfer(NCEconomicAssetMutationStatus.StorageFailure,
                    "The commodity-transfer command is still in progress.");

            var gateKeys = new List<Guid>
            {
                request.TransferId.Value,
                request.SourceBatchId.Value,
                request.SourceStockNodeId.Value,
                request.DestinationStockNodeId.Value,
            };
            if (request.SplitBatchId is { } splitBatchId)
                gateKeys.Add(splitBatchId.Value);
            var gates = await AcquireGates(gateKeys.ToArray());
            try
            {
                var existing = await GetTransferAsync(request.TransferId, true);
                if (existing != null)
                {
                    return Same(existing.Snapshot, request)
                        ? await LoadCommodityTransferResult(request, existing,
                            NCEconomicAssetMutationStatus.Duplicate)
                        : RejectTransfer(NCEconomicAssetMutationStatus.Rejected,
                            "The transfer identity is bound to different immutable data.");
                }
                if (command == InitialCommandState.Completed)
                    return RejectTransfer(NCEconomicAssetMutationStatus.StorageFailure,
                        "The completed command is missing its transfer aggregate.");

                var batch = await GetCommodityBatchAsync(request.SourceBatchId, true);
                var source = await GetStockNodeAsync(request.SourceStockNodeId, true);
                var destination = await GetStockNodeAsync(request.DestinationStockNodeId, true);
                var stateError = await ValidateCommodityTransferState(request, batch, source, destination, policy);
                if (stateError != null)
                    return stateError;

                var split = request.Quantity < batch!.Snapshot.Quantity;
                var destinationBatchId = split ? request.SplitBatchId!.Value : request.SourceBatchId;
                var sourceBatchEventId = Guid.NewGuid();
                var nextSourceBatch = batch.Snapshot with
                {
                    Quantity = split ? batch.Snapshot.Quantity - request.Quantity : batch.Snapshot.Quantity,
                    Owner = split ? batch.Snapshot.Owner : request.NewOwner,
                    Location = split ? batch.Snapshot.Location : StockLocation(request.DestinationStockNodeId),
                    UpdatedAt = now,
                    DataVersion = checked(batch.Snapshot.DataVersion + 1),
                    LastAppliedEventId = sourceBatchEventId,
                };
                NCCommodityBatchSnapshot? splitBatch = null;
                if (split)
                {
                    splitBatch = batch.Snapshot with
                    {
                        BatchId = destinationBatchId,
                        Quantity = request.Quantity,
                        ReservedQuantity = 0,
                        Owner = request.NewOwner,
                        Location = StockLocation(request.DestinationStockNodeId),
                        CreatedAt = now,
                        UpdatedAt = now,
                        DataVersion = 1,
                        LastAppliedEventId = Guid.NewGuid(),
                    };
                }

                var sourceEventId = Guid.NewGuid();
                var nextSource = source!.Snapshot with
                {
                    CommodityBatchIds = split
                        ? source.Snapshot.CommodityBatchIds
                        : source.Snapshot.CommodityBatchIds.Where(id => id != request.SourceBatchId).ToArray(),
                    UpdatedAt = now,
                    DataVersion = checked(source.Snapshot.DataVersion + 1),
                    LastAppliedEventId = sourceEventId,
                };
                var destinationEventId = Guid.NewGuid();
                var nextDestination = destination!.Snapshot with
                {
                    CommodityBatchIds = destination.Snapshot.CommodityBatchIds.Append(destinationBatchId)
                        .OrderBy(id => id.Value).ToArray(),
                    UpdatedAt = now,
                    DataVersion = checked(destination.Snapshot.DataVersion + 1),
                    LastAppliedEventId = destinationEventId,
                };
                var transferEventId = Guid.NewGuid();
                var transfer = new NCEconomicAssetTransferSnapshot(
                    request.TransferId,
                    null,
                    null,
                    NCEconomicTransferAssetKind.CommodityBatch,
                    Key(request.SourceBatchId),
                    Key(destinationBatchId),
                    request.Quantity,
                    request.SourceStockNodeId,
                    request.DestinationStockNodeId,
                    request.ExpectedOwner,
                    request.NewOwner,
                    request.Initiator,
                    now,
                    1,
                    transferEventId);

                var writes = new List<NCPersistentAggregateWrite>
                {
                    CommodityMutationWrite(batch, nextSourceBatch, sourceBatchEventId,
                        split ? "living-city.commodity-batch.split" : "living-city.commodity-batch.transferred",
                        request.AuditSource),
                    StockNodeWrite(source, nextSource, sourceEventId,
                        split ? "living-city.stock-node.batch-split" : "living-city.stock-node.batch-withdrawn",
                        request.AuditSource),
                    StockNodeWrite(destination, nextDestination, destinationEventId,
                        "living-city.stock-node.batch-deposited", request.AuditSource),
                    TransferWrite(transfer, transferEventId, request.AuditSource),
                };
                if (splitBatch != null)
                {
                    writes.Insert(1, CommodityMutationWrite(null, splitBatch, splitBatch.LastAppliedEventId,
                        "living-city.commodity-batch.split-created", request.AuditSource));
                }

                var persisted = await Persist(
                    command == InitialCommandState.Available ? request.CommandId : Guid.NewGuid(),
                    TransferCommodityCommand,
                    "asset-transfer",
                    Key(request.TransferId),
                    writes,
                    new AssetReceipt(Key(request.TransferId), 1),
                    request.CorrelationId,
                    request.CausationId,
                    epoch,
                    now);
                if (persisted.Status is not (NCPersistentMutationStatus.Applied or
                    NCPersistentMutationStatus.DuplicateCompleted))
                    return FromPersistenceTransfer(persisted);

                var loaded = await GetTransferAsync(request.TransferId, true);
                return loaded == null
                    ? RejectTransfer(NCEconomicAssetMutationStatus.StorageFailure,
                        "The committed commodity transfer could not be reloaded.")
                    : await LoadCommodityTransferResult(request, loaded,
                        persisted.Status == NCPersistentMutationStatus.Applied
                            ? NCEconomicAssetMutationStatus.Applied
                            : NCEconomicAssetMutationStatus.Duplicate);
            }
            finally
            {
                ReleaseGates(gates);
            }
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to transfer commodity batch {request.SourceBatchId}: {exception}");
            return RejectTransfer(NCEconomicAssetMutationStatus.StorageFailure, exception.Message);
        }
    }

    private NCEconomicAssetTransferResult? ValidateItemTransferState(
        NCTransferItemRequest request,
        NCItemInstanceRecord? item,
        NCStockNodeRecord? source,
        NCStockNodeRecord? destination,
        NCEconomicAssetPolicyPrototype policy)
    {
        if (item == null || source == null || destination == null)
            return RejectTransfer(NCEconomicAssetMutationStatus.NotFound,
                "The item, source stock node, or destination stock node does not exist.");
        if (_materializationLeases.IsLeased(request.SourceStockNodeId) ||
            _materializationLeases.IsLeased(request.DestinationStockNodeId))
        {
            return RejectTransfer(NCEconomicAssetMutationStatus.Rejected,
                "Abstract item transfer cannot mutate a materialized stock node.");
        }
        if (item.AggregateVersion != request.ExpectedItemVersion ||
            source.AggregateVersion != request.ExpectedSourceStockNodeVersion ||
            destination.AggregateVersion != request.ExpectedDestinationStockNodeVersion)
        {
            return RejectTransfer(NCEconomicAssetMutationStatus.VersionConflict,
                "The item or stock-node version changed.");
        }
        if (item.Snapshot.Owner != request.ExpectedOwner || source.Snapshot.Owner != request.ExpectedOwner ||
            destination.Snapshot.Owner != request.NewOwner || request.Initiator != request.ExpectedOwner)
        {
            return RejectTransfer(NCEconomicAssetMutationStatus.Rejected,
                "The item and stock-node ownership chain does not match the request.");
        }
        if (item.Snapshot.Location != StockLocation(request.SourceStockNodeId) ||
            !source.Snapshot.ItemIds.Contains(request.ItemId) || destination.Snapshot.ItemIds.Contains(request.ItemId))
        {
            return RejectTransfer(NCEconomicAssetMutationStatus.Rejected,
                "The stock indexes and item location are inconsistent.");
        }
        if (item.Snapshot.ReservationId.HasValue)
            return RejectTransfer(NCEconomicAssetMutationStatus.Rejected, "The item is reserved for another operation.");
        if (item.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active)
            return RejectTransfer(NCEconomicAssetMutationStatus.Rejected, "Only an active item can be transferred.");
        if (!source.Snapshot.AllowsWithdrawals || !destination.Snapshot.AcceptsDeposits)
            return RejectTransfer(NCEconomicAssetMutationStatus.Rejected,
                "The source forbids withdrawals or the destination forbids deposits.");
        return destination.Snapshot.ItemIds.Count >= policy.MaximumItemsPerStockNode
            ? RejectTransfer(NCEconomicAssetMutationStatus.CapacityExceeded,
                "The destination item capacity is exhausted.")
            : null;
    }

    private async Task<NCEconomicAssetTransferResult?> ValidateCommodityTransferState(
        NCTransferCommodityRequest request,
        NCCommodityBatchRecord? batch,
        NCStockNodeRecord? source,
        NCStockNodeRecord? destination,
        NCEconomicAssetPolicyPrototype policy)
    {
        if (batch == null || source == null || destination == null)
            return RejectTransfer(NCEconomicAssetMutationStatus.NotFound,
                "The commodity batch, source stock node, or destination stock node does not exist.");
        if (_materializationLeases.IsLeased(request.SourceStockNodeId) ||
            _materializationLeases.IsLeased(request.DestinationStockNodeId))
        {
            return RejectTransfer(NCEconomicAssetMutationStatus.Rejected,
                "Abstract commodity transfer cannot mutate a materialized stock node.");
        }
        if (batch.AggregateVersion != request.ExpectedSourceBatchVersion ||
            source.AggregateVersion != request.ExpectedSourceStockNodeVersion ||
            destination.AggregateVersion != request.ExpectedDestinationStockNodeVersion)
        {
            return RejectTransfer(NCEconomicAssetMutationStatus.VersionConflict,
                "The commodity batch or stock-node version changed.");
        }
        if (batch.Snapshot.Owner != request.ExpectedOwner || source.Snapshot.Owner != request.ExpectedOwner ||
            destination.Snapshot.Owner != request.NewOwner || request.Initiator != request.ExpectedOwner)
        {
            return RejectTransfer(NCEconomicAssetMutationStatus.Rejected,
                "The batch and stock-node ownership chain does not match the request.");
        }
        if (batch.Snapshot.Location != StockLocation(request.SourceStockNodeId) ||
            !source.Snapshot.CommodityBatchIds.Contains(request.SourceBatchId))
        {
            return RejectTransfer(NCEconomicAssetMutationStatus.Rejected,
                "The source stock index and batch location are inconsistent.");
        }
        if (!source.Snapshot.AllowsWithdrawals || !destination.Snapshot.AcceptsDeposits)
            return RejectTransfer(NCEconomicAssetMutationStatus.Rejected,
                "The source forbids withdrawals or the destination forbids deposits.");
        if (batch.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active)
            return RejectTransfer(NCEconomicAssetMutationStatus.Rejected,
                "Only an active commodity batch can be transferred.");
        if (request.Quantity > batch.Snapshot.Quantity - batch.Snapshot.ReservedQuantity)
            return RejectTransfer(NCEconomicAssetMutationStatus.Rejected,
                "The requested transfer exceeds the unreserved source quantity.");

        var split = request.Quantity < batch.Snapshot.Quantity;
        if (split != request.SplitBatchId.HasValue)
            return RejectTransfer(NCEconomicAssetMutationStatus.Rejected,
                "A partial transfer must name one new split-batch identity; a whole transfer must not.");
        var destinationBatchId = split ? request.SplitBatchId!.Value : request.SourceBatchId;
        if (destination.Snapshot.CommodityBatchIds.Contains(destinationBatchId))
            return RejectTransfer(NCEconomicAssetMutationStatus.Rejected,
                "The destination already indexes the transferred batch identity.");
        if (split && await GetCommodityBatchAsync(destinationBatchId, true) != null)
            return RejectTransfer(NCEconomicAssetMutationStatus.Rejected,
                "The split-batch identity already exists.");
        return destination.Snapshot.CommodityBatchIds.Count >= policy.MaximumBatchesPerStockNode
            ? RejectTransfer(NCEconomicAssetMutationStatus.CapacityExceeded,
                "The destination batch capacity is exhausted.")
            : null;
    }

    private async Task<NCEconomicAssetTransferResult> LoadItemTransferResult(
        NCTransferItemRequest request,
        NCEconomicAssetTransferRecord transfer,
        NCEconomicAssetMutationStatus status)
    {
        var sourceTask = GetStockNodeAsync(request.SourceStockNodeId, true);
        var destinationTask = GetStockNodeAsync(request.DestinationStockNodeId, true);
        var itemTask = GetItemAsync(request.ItemId, true);
        await Task.WhenAll(sourceTask, destinationTask, itemTask);
        var source = await sourceTask;
        var destination = await destinationTask;
        var item = await itemTask;
        return source == null || destination == null || item == null
            ? RejectTransfer(NCEconomicAssetMutationStatus.StorageFailure,
                "The committed item-transfer state could not be reloaded.")
            : new NCEconomicAssetTransferResult(status, transfer, source, destination, null, null, item, null);
    }

    private async Task<NCEconomicAssetTransferResult> LoadCommodityTransferResult(
        NCTransferCommodityRequest request,
        NCEconomicAssetTransferRecord transfer,
        NCEconomicAssetMutationStatus status)
    {
        var destinationBatchId = new NCCommodityBatchId(Guid.Parse(transfer.Snapshot.DestinationAssetId));
        var sourceTask = GetStockNodeAsync(request.SourceStockNodeId, true);
        var destinationTask = GetStockNodeAsync(request.DestinationStockNodeId, true);
        var sourceBatchTask = GetCommodityBatchAsync(request.SourceBatchId, true);
        var destinationBatchTask = destinationBatchId == request.SourceBatchId
            ? sourceBatchTask
            : GetCommodityBatchAsync(destinationBatchId, true);
        await Task.WhenAll(sourceTask, destinationTask, sourceBatchTask, destinationBatchTask);
        var source = await sourceTask;
        var destination = await destinationTask;
        var sourceBatch = await sourceBatchTask;
        var destinationBatch = await destinationBatchTask;
        return source == null || destination == null || sourceBatch == null || destinationBatch == null
            ? RejectTransfer(NCEconomicAssetMutationStatus.StorageFailure,
                "The committed commodity-transfer state could not be reloaded.")
            : new NCEconomicAssetTransferResult(
                status, transfer, source, destination, sourceBatch, destinationBatch, null, null);
    }

    private NCPersistentAggregateWrite ItemMutationWrite(
        NCItemInstanceRecord current,
        NCItemInstanceSnapshot next,
        Guid eventId,
        string eventType,
        string auditSource) => new(
        eventId,
        eventType,
        ItemInstanceAggregateType,
        Key(next.ItemId),
        current.AggregateVersion,
        SnapshotFormatVersion,
        JsonSerializer.Serialize(next, JsonOptions),
        JsonSerializer.Serialize(new AssetEvent(Key(next.ItemId), auditSource.Trim()), JsonOptions));

    private NCPersistentAggregateWrite CommodityMutationWrite(
        NCCommodityBatchRecord? current,
        NCCommodityBatchSnapshot next,
        Guid eventId,
        string eventType,
        string auditSource) => new(
        eventId,
        eventType,
        CommodityBatchAggregateType,
        Key(next.BatchId),
        current?.AggregateVersion ?? 0,
        SnapshotFormatVersion,
        JsonSerializer.Serialize(next, JsonOptions),
        JsonSerializer.Serialize(new AssetEvent(Key(next.BatchId), auditSource.Trim()), JsonOptions));

    private NCPersistentAggregateWrite TransferWrite(
        NCEconomicAssetTransferSnapshot transfer,
        Guid eventId,
        string auditSource) => new(
        eventId,
        "living-city.asset-transfer.completed",
        AssetTransferAggregateType,
        Key(transfer.TransferId),
        0,
        SnapshotFormatVersion,
        JsonSerializer.Serialize(transfer, JsonOptions),
        JsonSerializer.Serialize(new AssetEvent(Key(transfer.TransferId), auditSource.Trim()), JsonOptions));

    private static bool Valid(NCTransferItemRequest value) => value.CommandId != Guid.Empty &&
        value.TransferId.IsValid && value.ItemId.IsValid && value.ExpectedItemVersion > 0 &&
        value.SourceStockNodeId.IsValid && value.ExpectedSourceStockNodeVersion > 0 &&
        value.DestinationStockNodeId.IsValid && value.ExpectedDestinationStockNodeVersion > 0 &&
        value.SourceStockNodeId != value.DestinationStockNodeId && value.ExpectedOwner.IsValid &&
        value.NewOwner.IsValid && value.Initiator.IsValid && value.CorrelationId != Guid.Empty &&
        ValidAudit(value.AuditSource);

    private static bool Valid(NCTransferCommodityRequest value) => value.CommandId != Guid.Empty &&
        value.TransferId.IsValid && value.SourceBatchId.IsValid &&
        (!value.SplitBatchId.HasValue || value.SplitBatchId.Value.IsValid &&
            value.SplitBatchId.Value != value.SourceBatchId) && value.Quantity > 0 &&
        value.ExpectedSourceBatchVersion > 0 && value.SourceStockNodeId.IsValid &&
        value.ExpectedSourceStockNodeVersion > 0 && value.DestinationStockNodeId.IsValid &&
        value.ExpectedDestinationStockNodeVersion > 0 && value.SourceStockNodeId != value.DestinationStockNodeId &&
        value.ExpectedOwner.IsValid && value.NewOwner.IsValid && value.Initiator.IsValid &&
        value.CorrelationId != Guid.Empty && ValidAudit(value.AuditSource);

    private static bool Valid(NCEconomicAssetTransferSnapshot? value, NCEconomicTransferId expected) =>
        value != null && value.TransferId == expected && expected.IsValid && Enum.IsDefined(value.AssetKind) &&
        (!value.QuoteId.HasValue || value.QuoteId.Value.IsValid) &&
        (!value.ReservationId.HasValue || value.ReservationId.Value.IsValid) &&
        value.QuoteId.HasValue == value.ReservationId.HasValue &&
        Guid.TryParse(value.SourceAssetId, out var sourceAssetId) && sourceAssetId != Guid.Empty &&
        Guid.TryParse(value.DestinationAssetId, out var destinationAssetId) && destinationAssetId != Guid.Empty &&
        value.SourceAssetId == sourceAssetId.ToString("D") &&
        value.DestinationAssetId == destinationAssetId.ToString("D") && value.Quantity > 0 &&
        value.SourceStockNodeId.IsValid && value.DestinationStockNodeId.IsValid &&
        value.SourceStockNodeId != value.DestinationStockNodeId && value.PreviousOwner.IsValid &&
        value.NewOwner.IsValid && value.Initiator.IsValid && value.OccurredAt.IsValid && value.DataVersion == 1 &&
        value.LastAppliedEventId != Guid.Empty &&
        (value.AssetKind != NCEconomicTransferAssetKind.ItemInstance ||
            value.Quantity == 1 && value.SourceAssetId == value.DestinationAssetId);

    private static bool Same(NCEconomicAssetTransferSnapshot value, NCTransferItemRequest request) =>
        value.AssetKind == NCEconomicTransferAssetKind.ItemInstance && value.SourceAssetId == Key(request.ItemId) &&
        value.DestinationAssetId == Key(request.ItemId) && value.Quantity == 1 &&
        value.SourceStockNodeId == request.SourceStockNodeId &&
        value.DestinationStockNodeId == request.DestinationStockNodeId &&
        value.PreviousOwner == request.ExpectedOwner && value.NewOwner == request.NewOwner &&
        value.Initiator == request.Initiator;

    private static bool Same(NCEconomicAssetTransferSnapshot value, NCTransferCommodityRequest request) =>
        value.AssetKind == NCEconomicTransferAssetKind.CommodityBatch &&
        value.SourceAssetId == Key(request.SourceBatchId) &&
        value.DestinationAssetId == Key(request.SplitBatchId ?? request.SourceBatchId) &&
        value.Quantity == request.Quantity && value.SourceStockNodeId == request.SourceStockNodeId &&
        value.DestinationStockNodeId == request.DestinationStockNodeId &&
        value.PreviousOwner == request.ExpectedOwner && value.NewOwner == request.NewOwner &&
        value.Initiator == request.Initiator;

    private static NCEconomicAssetTransferRecord? DeserializeTransfer(
        NCAggregateSnapshotData? stored,
        NCEconomicTransferId expected)
    {
        if (!StoredShape(stored))
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCEconomicAssetTransferSnapshot>(stored!.Payload, JsonOptions);
            return Valid(snapshot, expected) && snapshot!.DataVersion == stored.AggregateVersion
                ? new NCEconomicAssetTransferRecord(snapshot, stored.AggregateVersion, stored.SessionEpoch,
                    stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static NCEconomicAssetTransferResult RejectTransfer(
        NCEconomicAssetMutationStatus status,
        string error) => new(status, null, null, null, null, null, null, error);

    private static NCEconomicAssetTransferResult FromPersistenceTransfer(NCPersistentBatchMutationResult result) =>
        RejectTransfer(result.Status switch
        {
            NCPersistentMutationStatus.VersionConflict => NCEconomicAssetMutationStatus.VersionConflict,
            NCPersistentMutationStatus.ClockRejected => NCEconomicAssetMutationStatus.ClockUnavailable,
            _ => NCEconomicAssetMutationStatus.StorageFailure,
        }, result.Error ?? "The economic asset transfer was not committed.");

    private static string Key(NCEconomicTransferId id) => id.Value.ToString("D");
}
