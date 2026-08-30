// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Economy;

/// <summary>
/// Performs bounded, read-only reconciliation of canonical asset locations against StockNode indexes. It never scans
/// ECS entities and never repairs state implicitly; an administrator can inspect deterministic issue codes in F7.
/// </summary>
public sealed partial class NCEconomicReconciliationSystem : EntitySystem
{
    private const int PageSize = 32;
    private const int MaximumIssues = 64;
    private const int MaximumReferenceChecks = 256;

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCEconomicAssetRegistrySystem _assets = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;

    public async Task<NCEconomicReconciliationReport> AuditAsync(NCEconomicReconciliationCursor cursor)
    {
        var stockPageTask = _database.GetNCAggregateSnapshotPageAsync(
            NCEconomicAssetRegistrySystem.StockNodeAggregateType, CleanCursor(cursor.StockNodeAfter), PageSize);
        var batchPageTask = _database.GetNCAggregateSnapshotPageAsync(
            NCEconomicAssetRegistrySystem.CommodityBatchAggregateType,
            CleanCursor(cursor.CommodityBatchAfter), PageSize);
        var itemPageTask = _database.GetNCAggregateSnapshotPageAsync(
            NCEconomicAssetRegistrySystem.ItemInstanceAggregateType, CleanCursor(cursor.ItemAfter), PageSize);
        await Task.WhenAll(stockPageTask, batchPageTask, itemPageTask);

        var stockPage = await stockPageTask;
        var batchPage = await batchPageTask;
        var itemPage = await itemPageTask;
        var issues = new List<NCEconomicReconciliationIssue>(MaximumIssues);
        var referenceChecks = 0;
        var referencesTruncated = false;

        foreach (var stored in stockPage)
        {
            if (!TryId(stored.AggregateId, out var raw))
            {
                AddIssue(issues, stored.AggregateType, stored.AggregateId, "invalid-aggregate-id", "none");
                continue;
            }
            var node = await _assets.GetStockNodeAsync(new NCStockNodeId(raw), true);
            if (node == null)
            {
                AddIssue(issues, stored.AggregateType, stored.AggregateId, "invalid-snapshot", "none");
                continue;
            }
            foreach (var batchId in node.Snapshot.CommodityBatchIds)
            {
                if (!TakeReference(ref referenceChecks, ref referencesTruncated))
                    break;
                var batch = await _assets.GetCommodityBatchAsync(batchId, true);
                if (batch == null)
                    AddIssue(issues, stored.AggregateType, stored.AggregateId, "missing-indexed-batch", batchId.ToString());
                else if (batch.Snapshot.Location != StockLocation(node.Snapshot.StockNodeId) ||
                         batch.Snapshot.Owner != node.Snapshot.Owner ||
                         batch.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active)
                {
                    AddIssue(issues, stored.AggregateType, stored.AggregateId,
                        "indexed-batch-location-mismatch", batchId.ToString());
                }
            }
            foreach (var itemId in node.Snapshot.ItemIds)
            {
                if (!TakeReference(ref referenceChecks, ref referencesTruncated))
                    break;
                var item = await _assets.GetItemAsync(itemId, true);
                if (item == null)
                    AddIssue(issues, stored.AggregateType, stored.AggregateId, "missing-indexed-item", itemId.ToString());
                else if (item.Snapshot.Location != StockLocation(node.Snapshot.StockNodeId) ||
                         item.Snapshot.Owner != node.Snapshot.Owner ||
                         item.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active)
                {
                    AddIssue(issues, stored.AggregateType, stored.AggregateId,
                        "indexed-item-location-mismatch", itemId.ToString());
                }
            }
        }

        foreach (var stored in batchPage)
        {
            if (!TryId(stored.AggregateId, out var raw))
            {
                AddIssue(issues, stored.AggregateType, stored.AggregateId, "invalid-aggregate-id", "none");
                continue;
            }
            var batch = await _assets.GetCommodityBatchAsync(new NCCommodityBatchId(raw), true);
            if (batch == null)
            {
                AddIssue(issues, stored.AggregateType, stored.AggregateId, "invalid-snapshot", "none");
                continue;
            }
            if (batch.Snapshot.Quantity < 0 || batch.Snapshot.ReservedQuantity < 0 ||
                batch.Snapshot.ReservedQuantity > batch.Snapshot.Quantity)
            {
                AddIssue(issues, stored.AggregateType, stored.AggregateId, "invalid-quantity", "none");
            }
            if (_clock.TryGetCurrentTime(out _, out var now) &&
                batch.Snapshot.Lifecycle == NCEconomicAssetLifecycle.Active &&
                batch.Snapshot.ExpiresAt is { } expiry && now >= expiry)
            {
                AddIssue(issues, stored.AggregateType, stored.AggregateId, "expired-active-batch", "none");
            }
            if (batch.Snapshot.Location.Kind == NCEconomicAssetLocationKind.StockNode)
            {
                if (TakeReference(ref referenceChecks, ref referencesTruncated))
                    await CheckBatchLocation(batch, issues);
            }
            else if (batch.Snapshot.Lifecycle == NCEconomicAssetLifecycle.Active &&
                     batch.Snapshot.Location.Kind is NCEconomicAssetLocationKind.Consumed or
                         NCEconomicAssetLocationKind.Destroyed)
            {
                AddIssue(issues, stored.AggregateType, stored.AggregateId, "active-terminal-location", "none");
            }
        }

        foreach (var stored in itemPage)
        {
            if (!TryId(stored.AggregateId, out var raw))
            {
                AddIssue(issues, stored.AggregateType, stored.AggregateId, "invalid-aggregate-id", "none");
                continue;
            }
            var item = await _assets.GetItemAsync(new NCItemId(raw), true);
            if (item == null)
            {
                AddIssue(issues, stored.AggregateType, stored.AggregateId, "invalid-snapshot", "none");
                continue;
            }
            if (item.Snapshot.Location.Kind == NCEconomicAssetLocationKind.StockNode)
            {
                if (TakeReference(ref referenceChecks, ref referencesTruncated))
                    await CheckItemLocation(item, issues);
            }
            else if (item.Snapshot.Location.Kind == NCEconomicAssetLocationKind.InstalledInItem)
            {
                if (TakeReference(ref referenceChecks, ref referencesTruncated))
                    await CheckInstalledItemLocation(item, issues);
            }
            else if (item.Snapshot.Location.Kind == NCEconomicAssetLocationKind.InstalledInActor &&
                     (!Guid.TryParse(item.Snapshot.Location.StableId, out var actorId) || actorId == Guid.Empty ||
                      item.Snapshot.Owner.Kind != NCPartyKind.Actor ||
                      item.Snapshot.Owner.PersistentId != actorId))
            {
                AddIssue(issues, stored.AggregateType, stored.AggregateId,
                    "installed-actor-owner-mismatch", item.Snapshot.Location.StableId);
            }
            else if (item.Snapshot.Lifecycle == NCEconomicAssetLifecycle.Active &&
                     item.Snapshot.Location.Kind is NCEconomicAssetLocationKind.Consumed or
                         NCEconomicAssetLocationKind.Destroyed)
            {
                AddIssue(issues, stored.AggregateType, stored.AggregateId, "active-terminal-location", "none");
            }
            foreach (var partId in item.Snapshot.InstalledPartIds)
            {
                if (!TakeReference(ref referenceChecks, ref referencesTruncated))
                    break;
                var part = await _assets.GetItemAsync(partId, true);
                if (part == null || part.Snapshot.ParentItemId != item.Snapshot.ItemId ||
                    part.Snapshot.Owner != item.Snapshot.Owner ||
                    part.Snapshot.Location != new NCEconomicAssetLocation(
                        NCEconomicAssetLocationKind.InstalledInItem, item.Snapshot.ItemId.ToString()) ||
                    part.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active)
                {
                    AddIssue(issues, stored.AggregateType, stored.AggregateId,
                        "assembly-part-mismatch", partId.ToString());
                }
            }
        }

        var continuation = new NCEconomicReconciliationCursor(
            LastId(stockPage, cursor.StockNodeAfter),
            LastId(batchPage, cursor.CommodityBatchAfter),
            LastId(itemPage, cursor.ItemAfter));
        return new NCEconomicReconciliationReport(
            stockPage.Count,
            batchPage.Count,
            itemPage.Count,
            referenceChecks,
            referencesTruncated,
            issues,
            continuation,
            stockPage.Count == PageSize || batchPage.Count == PageSize || itemPage.Count == PageSize);
    }

    private async Task CheckBatchLocation(
        NCCommodityBatchRecord batch,
        List<NCEconomicReconciliationIssue> issues)
    {
        if (!TryId(batch.Snapshot.Location.StableId, out var nodeRaw))
        {
            AddIssue(issues, NCEconomicAssetRegistrySystem.CommodityBatchAggregateType,
                batch.Snapshot.BatchId.ToString(), "invalid-stock-location", batch.Snapshot.Location.StableId);
            return;
        }
        var node = await _assets.GetStockNodeAsync(new NCStockNodeId(nodeRaw), true);
        if (node == null)
        {
            AddIssue(issues, NCEconomicAssetRegistrySystem.CommodityBatchAggregateType,
                batch.Snapshot.BatchId.ToString(), "missing-location-stock-node", batch.Snapshot.Location.StableId);
            return;
        }
        if (!node.Snapshot.CommodityBatchIds.Contains(batch.Snapshot.BatchId) ||
            node.Snapshot.Owner != batch.Snapshot.Owner)
        {
            AddIssue(issues, NCEconomicAssetRegistrySystem.CommodityBatchAggregateType,
                batch.Snapshot.BatchId.ToString(), "location-stock-index-mismatch", node.Snapshot.StockNodeId.ToString());
        }
    }

    private async Task CheckItemLocation(
        NCItemInstanceRecord item,
        List<NCEconomicReconciliationIssue> issues)
    {
        if (!TryId(item.Snapshot.Location.StableId, out var nodeRaw))
        {
            AddIssue(issues, NCEconomicAssetRegistrySystem.ItemInstanceAggregateType,
                item.Snapshot.ItemId.ToString(), "invalid-stock-location", item.Snapshot.Location.StableId);
            return;
        }
        var node = await _assets.GetStockNodeAsync(new NCStockNodeId(nodeRaw), true);
        if (node == null)
        {
            AddIssue(issues, NCEconomicAssetRegistrySystem.ItemInstanceAggregateType,
                item.Snapshot.ItemId.ToString(), "missing-location-stock-node", item.Snapshot.Location.StableId);
            return;
        }
        if (!node.Snapshot.ItemIds.Contains(item.Snapshot.ItemId) || node.Snapshot.Owner != item.Snapshot.Owner)
        {
            AddIssue(issues, NCEconomicAssetRegistrySystem.ItemInstanceAggregateType,
                item.Snapshot.ItemId.ToString(), "location-stock-index-mismatch", node.Snapshot.StockNodeId.ToString());
        }
    }

    private async Task CheckInstalledItemLocation(
        NCItemInstanceRecord part,
        List<NCEconomicReconciliationIssue> issues)
    {
        if (part.Snapshot.ParentItemId is not { } parentId ||
            part.Snapshot.Location.StableId != parentId.ToString())
        {
            AddIssue(issues, NCEconomicAssetRegistrySystem.ItemInstanceAggregateType,
                part.Snapshot.ItemId.ToString(), "invalid-assembly-parent", part.Snapshot.Location.StableId);
            return;
        }
        var parent = await _assets.GetItemAsync(parentId, true);
        if (parent == null || !parent.Snapshot.InstalledPartIds.Contains(part.Snapshot.ItemId) ||
            parent.Snapshot.Owner != part.Snapshot.Owner || parent.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active)
        {
            AddIssue(issues, NCEconomicAssetRegistrySystem.ItemInstanceAggregateType,
                part.Snapshot.ItemId.ToString(), "assembly-parent-mismatch", parentId.ToString());
        }
    }

    private static NCEconomicAssetLocation StockLocation(NCStockNodeId id) =>
        new(NCEconomicAssetLocationKind.StockNode, id.ToString());

    private static bool TakeReference(ref int count, ref bool truncated)
    {
        if (count >= MaximumReferenceChecks)
        {
            truncated = true;
            return false;
        }
        count++;
        return true;
    }

    private static void AddIssue(
        ICollection<NCEconomicReconciliationIssue> issues,
        string aggregateType,
        string aggregateId,
        string code,
        string relatedId)
    {
        if (issues.Count < MaximumIssues)
            issues.Add(new NCEconomicReconciliationIssue(aggregateType, aggregateId, code, relatedId));
    }

    private static bool TryId(string value, out Guid id) =>
        Guid.TryParse(value, out id) && id != Guid.Empty && value == id.ToString("D");

    private static string? CleanCursor(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? LastId(
        IReadOnlyList<NCAggregateSnapshotData> page,
        string? previous) => page.Count == 0 ? CleanCursor(previous) : page[^1].AggregateId;
}
