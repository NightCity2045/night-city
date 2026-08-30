// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Database;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Economy;

/// <summary>
/// Canonical sink for explicitly destroyed abstract or leased gameplay assets. Assemblies close with all installed
/// children, and the stock-node membership changes in the same persistent batch.
/// </summary>
public sealed partial class NCEconomicAssetRegistrySystem
{
    private const string DestroyAssetCommand = "living-city.economic-asset.destroy";

    public async Task<NCEconomicAssetMutationResult> DestroyAssetAsync(NCDestroyEconomicAssetRequest request)
    {
        if (!Valid(request) || !_clock.TryGetCurrentTime(out var epoch, out var now) ||
            !Guid.TryParse(request.AssetId, out var assetGuid))
            return Reject(_clock.IsReady ? NCEconomicAssetMutationStatus.Rejected :
                NCEconomicAssetMutationStatus.ClockUnavailable, "The asset destruction request is invalid.");
        if (request.Initiator != request.Owner || !await ResolveParties(request.Owner, request.Initiator))
            return Reject(NCEconomicAssetMutationStatus.PartyUnavailable,
                "Only the resolved asset owner can confirm destruction.");
        var scopeId = $"{request.AssetKind}:{assetGuid:D}";
        var command = await InspectCommand(request.CommandId, DestroyAssetCommand, "economic-asset", scopeId);
        if (command == InitialCommandState.Collision)
            return Reject(NCEconomicAssetMutationStatus.Rejected, "The command ID belongs to another operation.");

        var lockIds = new List<Guid> { assetGuid, request.StockNodeId.Value };
        if (request.AssetKind == NCEconomicTransferAssetKind.ItemInstance)
        {
            var initial = await GetItemAsync(new NCItemId(assetGuid), true);
            if (initial != null)
                lockIds.AddRange(initial.Snapshot.InstalledPartIds.Select(id => id.Value));
        }
        var gates = await AcquireGates(lockIds.ToArray());
        try
        {
            var node = await GetStockNodeAsync(request.StockNodeId, true);
            if (node == null)
                return Reject(NCEconomicAssetMutationStatus.NotFound, "The destruction stock node is missing.");
            if (command == InitialCommandState.Completed)
                return await DuplicateDestructionResultAsync(request, assetGuid, node);
            if (command == InitialCommandState.InProgress)
                return Reject(NCEconomicAssetMutationStatus.StorageFailure,
                    "The asset destruction command is still in progress.");
            if (_materializationLeases.IsLeased(request.StockNodeId))
                return Reject(NCEconomicAssetMutationStatus.Rejected,
                    "Release the physical stock lease before committing its destruction capture.");
            if (node.AggregateVersion != request.ExpectedStockNodeVersion || node.Snapshot.Owner != request.Owner)
                return Reject(NCEconomicAssetMutationStatus.VersionConflict,
                    "The destruction stock-node authority changed.");

            if (request.AssetKind == NCEconomicTransferAssetKind.CommodityBatch)
                return await DestroyCommodityAsync(request, assetGuid, node, epoch, now, scopeId);
            return await DestroyItemAsync(request, assetGuid, node, epoch, now, scopeId, lockIds);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to destroy economic asset {scopeId}: {exception}");
            return Reject(NCEconomicAssetMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            ReleaseGates(gates);
        }
    }

    private async Task<NCEconomicAssetMutationResult> DestroyCommodityAsync(
        NCDestroyEconomicAssetRequest request,
        Guid raw,
        NCStockNodeRecord node,
        Content.Shared._NC.Persistence.Time.NCSessionEpoch epoch,
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime now,
        string scopeId)
    {
        var id = new NCCommodityBatchId(raw);
        var batch = await GetCommodityBatchAsync(id, true);
        if (batch == null)
            return Reject(NCEconomicAssetMutationStatus.NotFound, "The commodity batch is missing.");
        if (batch.AggregateVersion != request.ExpectedAssetVersion || batch.Snapshot.Owner != request.Owner ||
            batch.Snapshot.Location != StockLocation(request.StockNodeId) ||
            batch.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active || batch.Snapshot.ReservedQuantity != 0 ||
            !node.Snapshot.CommodityBatchIds.Contains(id))
            return Reject(NCEconomicAssetMutationStatus.Rejected,
                "The commodity destruction authority, custody, or reservation is invalid.");
        var assetEvent = Guid.NewGuid();
        var nextBatch = batch.Snapshot with
        {
            Quantity = 0,
            ReservedQuantity = 0,
            Location = TerminalLocation(NCEconomicAssetLocationKind.Destroyed),
            Lifecycle = NCEconomicAssetLifecycle.Destroyed,
            UpdatedAt = now,
            DataVersion = checked(batch.Snapshot.DataVersion + 1),
            LastAppliedEventId = assetEvent,
        };
        var nodeEvent = Guid.NewGuid();
        var nextNode = node.Snapshot with
        {
            CommodityBatchIds = node.Snapshot.CommodityBatchIds.Where(value => value != id).ToArray(),
            UpdatedAt = now,
            DataVersion = checked(node.Snapshot.DataVersion + 1),
            LastAppliedEventId = nodeEvent,
        };
        var persisted = await Persist(request.CommandId, DestroyAssetCommand, "economic-asset", scopeId,
            [
                CommodityMutationWrite(batch, nextBatch, assetEvent, "living-city.commodity-batch.destroyed",
                    request.AuditSource),
                StockNodeWrite(node, nextNode, nodeEvent, "living-city.stock-node.batch-destroyed",
                    request.AuditSource),
            ], new AssetReceipt(scopeId, batch.AggregateVersion + 1), request.CorrelationId, request.CausationId,
            epoch, now);
        if (persisted.Status is not (NCPersistentMutationStatus.Applied or
            NCPersistentMutationStatus.DuplicateCompleted))
            return FromPersistence(persisted);
        var loadedBatch = await GetCommodityBatchAsync(id, true);
        var loadedNode = await GetStockNodeAsync(request.StockNodeId, true);
        return loadedBatch == null || loadedNode == null
            ? Reject(NCEconomicAssetMutationStatus.StorageFailure, "Destroyed commodity state could not be reloaded.")
            : new(persisted.Status == NCPersistentMutationStatus.Applied ? NCEconomicAssetMutationStatus.Applied :
                NCEconomicAssetMutationStatus.Duplicate, loadedNode, loadedBatch, null, null);
    }

    private async Task<NCEconomicAssetMutationResult> DestroyItemAsync(
        NCDestroyEconomicAssetRequest request,
        Guid raw,
        NCStockNodeRecord node,
        Content.Shared._NC.Persistence.Time.NCSessionEpoch epoch,
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime now,
        string scopeId,
        IReadOnlyCollection<Guid> lockedIds)
    {
        var id = new NCItemId(raw);
        var item = await GetItemAsync(id, true);
        if (item == null)
            return Reject(NCEconomicAssetMutationStatus.NotFound, "The item instance is missing.");
        if (item.AggregateVersion != request.ExpectedAssetVersion || item.Snapshot.Owner != request.Owner ||
            item.Snapshot.Location != StockLocation(request.StockNodeId) || item.Snapshot.ReservationId.HasValue ||
            item.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active || !node.Snapshot.ItemIds.Contains(id) ||
            item.Snapshot.InstalledPartIds.Any(part => !lockedIds.Contains(part.Value)))
            return Reject(NCEconomicAssetMutationStatus.Rejected,
                "The item destruction authority, custody, assembly, or reservation is invalid.");
        var writes = new List<NCPersistentAggregateWrite>();
        foreach (var partId in item.Snapshot.InstalledPartIds.OrderBy(value => value.Value))
        {
            var part = await GetItemAsync(partId, true);
            if (part == null || part.Snapshot.Owner != request.Owner || part.Snapshot.ParentItemId != id ||
                part.Snapshot.Location != new NCEconomicAssetLocation(
                    NCEconomicAssetLocationKind.InstalledInItem, id.ToString()) ||
                part.Snapshot.ReservationId.HasValue || part.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active)
                return Reject(NCEconomicAssetMutationStatus.Rejected,
                    "An installed part cannot follow the destroyed assembly parent.");
            var partEvent = Guid.NewGuid();
            var nextPart = part.Snapshot with
            {
                ParentItemId = null,
                InstalledPartIds = [],
                Location = TerminalLocation(NCEconomicAssetLocationKind.Destroyed),
                Lifecycle = NCEconomicAssetLifecycle.Destroyed,
                UpdatedAt = now,
                DataVersion = checked(part.Snapshot.DataVersion + 1),
                LastAppliedEventId = partEvent,
            };
            writes.Add(ItemMutationWrite(part, nextPart, partEvent,
                "living-city.item-instance.destroyed-with-cargo-assembly", request.AuditSource));
        }
        var assetEvent = Guid.NewGuid();
        var nextItem = item.Snapshot with
        {
            ReservationId = null,
            ParentItemId = null,
            InstalledPartIds = [],
            Location = TerminalLocation(NCEconomicAssetLocationKind.Destroyed),
            Lifecycle = NCEconomicAssetLifecycle.Destroyed,
            UpdatedAt = now,
            DataVersion = checked(item.Snapshot.DataVersion + 1),
            LastAppliedEventId = assetEvent,
        };
        writes.Add(ItemMutationWrite(item, nextItem, assetEvent, "living-city.item-instance.destroyed",
            request.AuditSource));
        var nodeEvent = Guid.NewGuid();
        var nextNode = node.Snapshot with
        {
            ItemIds = node.Snapshot.ItemIds.Where(value => value != id).ToArray(),
            UpdatedAt = now,
            DataVersion = checked(node.Snapshot.DataVersion + 1),
            LastAppliedEventId = nodeEvent,
        };
        writes.Add(StockNodeWrite(node, nextNode, nodeEvent, "living-city.stock-node.item-destroyed",
            request.AuditSource));
        var persisted = await Persist(request.CommandId, DestroyAssetCommand, "economic-asset", scopeId, writes,
            new AssetReceipt(scopeId, item.AggregateVersion + 1), request.CorrelationId, request.CausationId,
            epoch, now);
        if (persisted.Status is not (NCPersistentMutationStatus.Applied or
            NCPersistentMutationStatus.DuplicateCompleted))
            return FromPersistence(persisted);
        var loadedItem = await GetItemAsync(id, true);
        var loadedNode = await GetStockNodeAsync(request.StockNodeId, true);
        return loadedItem == null || loadedNode == null
            ? Reject(NCEconomicAssetMutationStatus.StorageFailure, "Destroyed item state could not be reloaded.")
            : new(persisted.Status == NCPersistentMutationStatus.Applied ? NCEconomicAssetMutationStatus.Applied :
                NCEconomicAssetMutationStatus.Duplicate, loadedNode, null, loadedItem, null);
    }

    private async Task<NCEconomicAssetMutationResult> DuplicateDestructionResultAsync(
        NCDestroyEconomicAssetRequest request,
        Guid raw,
        NCStockNodeRecord node)
    {
        if (request.AssetKind == NCEconomicTransferAssetKind.CommodityBatch)
            return new(NCEconomicAssetMutationStatus.Duplicate, node,
                await GetCommodityBatchAsync(new NCCommodityBatchId(raw), true), null, null);
        return new(NCEconomicAssetMutationStatus.Duplicate, node, null,
            await GetItemAsync(new NCItemId(raw), true), null);
    }

    private static bool Valid(NCDestroyEconomicAssetRequest value) =>
        value.CommandId != Guid.Empty && Enum.IsDefined(value.AssetKind) &&
        Guid.TryParse(value.AssetId, out var id) && id != Guid.Empty && value.AssetId == id.ToString("D") &&
        value.ExpectedAssetVersion > 0 && value.StockNodeId.IsValid && value.ExpectedStockNodeVersion > 0 &&
        value.Owner.IsValid && value.Initiator.IsValid && value.CorrelationId != Guid.Empty &&
        ValidAudit(value.AuditSource);
}
