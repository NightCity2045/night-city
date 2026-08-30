// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Database;
using Content.Shared._NC.LivingCity.Economy;

namespace Content.Server._NC.LivingCity.Economy;

/// <summary>
/// Applies non-physical commodity sinks. Physical consumption is captured by the actor materialization lease instead,
/// so the same persistent quantity can never be consumed concurrently through both boundaries.
/// </summary>
public sealed partial class NCEconomicAssetRegistrySystem
{
    private const string ConsumeCommodityCommand = "living-city.commodity-batch.consume";

    public async Task<NCEconomicAssetMutationResult> ConsumeCommodityAsync(
        NCConsumeEconomicCommodityRequest request)
    {
        if (!Valid(request) || !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(_clock.IsReady
                ? NCEconomicAssetMutationStatus.Rejected
                : NCEconomicAssetMutationStatus.ClockUnavailable,
                "The commodity consumption request or active-world clock is invalid.");
        }
        if (!await ResolveParties(request.Owner, request.Initiator) || request.Initiator != request.Owner)
        {
            return Reject(NCEconomicAssetMutationStatus.PartyUnavailable,
                "Only the resolved stock owner can consume an abstract commodity batch.");
        }

        var command = await InspectCommand(request.CommandId, ConsumeCommodityCommand,
            "commodity-batch", Key(request.BatchId));
        if (command == InitialCommandState.Collision)
            return Reject(NCEconomicAssetMutationStatus.Rejected, "The command ID belongs to another operation.");

        var gates = await AcquireGates(request.BatchId.Value, request.StockNodeId.Value);
        try
        {
            var batch = await GetCommodityBatchAsync(request.BatchId, true);
            var node = await GetStockNodeAsync(request.StockNodeId, true);
            if (batch == null || node == null)
                return Reject(NCEconomicAssetMutationStatus.NotFound, "The commodity batch or stock node is missing.");
            if (command == InitialCommandState.Completed)
            {
                return new NCEconomicAssetMutationResult(
                    NCEconomicAssetMutationStatus.Duplicate, node, batch, null, null);
            }
            if (command == InitialCommandState.InProgress)
                return Reject(NCEconomicAssetMutationStatus.StorageFailure,
                    "The commodity consumption command is still in progress.");
            if (_materializationLeases.IsLeased(request.StockNodeId))
            {
                return Reject(NCEconomicAssetMutationStatus.Rejected,
                    "A materialized commodity must be consumed through its physical gameplay entity.");
            }
            if (batch.AggregateVersion != request.ExpectedBatchVersion ||
                node.AggregateVersion != request.ExpectedStockNodeVersion)
            {
                return Reject(NCEconomicAssetMutationStatus.VersionConflict,
                    "The commodity batch or stock-node version changed.");
            }
            if (batch.Snapshot.Owner != request.Owner || node.Snapshot.Owner != request.Owner ||
                batch.Snapshot.Location != StockLocation(request.StockNodeId) ||
                !node.Snapshot.CommodityBatchIds.Contains(request.BatchId) ||
                batch.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active ||
                batch.Snapshot.ExpiresAt is { } expiresAt && now >= expiresAt ||
                request.Quantity > batch.Snapshot.Quantity - batch.Snapshot.ReservedQuantity)
            {
                return Reject(NCEconomicAssetMutationStatus.Rejected,
                    "The commodity custody, expiry, reservation, or available quantity is invalid.");
            }

            var remaining = checked(batch.Snapshot.Quantity - request.Quantity);
            var consumed = remaining == 0;
            var batchEventId = Guid.NewGuid();
            var nextBatch = batch.Snapshot with
            {
                Quantity = remaining,
                Location = consumed
                    ? TerminalLocation(NCEconomicAssetLocationKind.Consumed)
                    : batch.Snapshot.Location,
                Lifecycle = consumed ? NCEconomicAssetLifecycle.Consumed : NCEconomicAssetLifecycle.Active,
                UpdatedAt = now,
                DataVersion = checked(batch.Snapshot.DataVersion + 1),
                LastAppliedEventId = batchEventId,
            };
            var nodeEventId = Guid.NewGuid();
            var nextNode = node.Snapshot with
            {
                CommodityBatchIds = consumed
                    ? node.Snapshot.CommodityBatchIds.Where(id => id != request.BatchId).ToArray()
                    : node.Snapshot.CommodityBatchIds,
                UpdatedAt = now,
                DataVersion = checked(node.Snapshot.DataVersion + 1),
                LastAppliedEventId = nodeEventId,
            };
            var persisted = await Persist(
                request.CommandId,
                ConsumeCommodityCommand,
                "commodity-batch",
                Key(request.BatchId),
                [
                    CommodityMutationWrite(batch, nextBatch, batchEventId,
                        consumed
                            ? "living-city.commodity-batch.consumed"
                            : "living-city.commodity-batch.partially-consumed",
                        request.AuditSource),
                    StockNodeWrite(node, nextNode, nodeEventId,
                        consumed
                            ? "living-city.stock-node.batch-consumed"
                            : "living-city.stock-node.batch-consumption-recorded",
                        request.AuditSource),
                ],
                new AssetReceipt(Key(request.BatchId), batch.AggregateVersion + 1),
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
                    "The committed consumption state could not be reloaded.")
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
            Log.Error($"Failed to consume commodity batch {request.BatchId}: {exception}");
            return Reject(NCEconomicAssetMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            ReleaseGates(gates);
        }
    }

    private static bool Valid(NCConsumeEconomicCommodityRequest value) =>
        value.CommandId != Guid.Empty && value.BatchId.IsValid && value.ExpectedBatchVersion > 0 &&
        value.StockNodeId.IsValid && value.ExpectedStockNodeVersion > 0 && value.Quantity > 0 &&
        value.Owner.IsValid && value.Initiator.IsValid && value.CorrelationId != Guid.Empty &&
        ValidAudit(value.AuditSource);
}
