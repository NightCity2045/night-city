// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Linq;
using Content.Server.Database;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Economy;

public sealed partial class NCEconomicAssetRegistrySystem
{
    private const int MaximumActorDematerializationParticipantWrites = 29;

    /// <summary>
    /// Builds economy participant writes for the actor's already captured physical graph. The actor registry commits
    /// these writes together with root, health, and inventory projections; this method never writes independently.
    /// </summary>
    public bool TryBuildActorDematerializationWrites(
        NCActorInventoryMaterializationPlan plan,
        NCCityActorInventorySummary captured,
        IReadOnlySet<NCItemId> destroyedItems,
        NCActiveWorldTime now,
        string auditSource,
        out IReadOnlyList<NCPersistentAggregateWrite> writes,
        out string? error)
    {
        var result = new List<NCPersistentAggregateWrite>();
        writes = result;
        error = null;
        if (!plan.ActorId.IsValid || captured == null || destroyedItems == null || !now.IsValid ||
            !TryGetPolicy(out var policy) ||
            !ValidAudit(auditSource))
        {
            error = "The actor economy dematerialization capture is invalid.";
            return false;
        }

        var expectedItems = plan.Items.ToDictionary(entry => entry.Item.Snapshot.ItemId);
        var capturedItems = captured.UniqueItems.ToHashSet();
        var capturedInstalledCyberware = captured.InstalledCyberware.ToHashSet();
        if (capturedItems.Any(id => !expectedItems.ContainsKey(id)) ||
            capturedInstalledCyberware.Any(id => !capturedItems.Contains(id) || !expectedItems.ContainsKey(id)) ||
            destroyedItems.Any(id => !expectedItems.ContainsKey(id)) ||
            expectedItems.Keys.Any(id => !capturedItems.Contains(id) && !destroyedItems.Contains(id)))
        {
            error = "The captured unique-item set contains an unleased, dropped, or ambiguous item.";
            return false;
        }

        var nextItemIds = plan.StockNode.Snapshot.ItemIds.ToHashSet();
        foreach (var itemId in destroyedItems.OrderBy(id => id.Value))
        {
            var itemPlan = expectedItems[itemId];
            var current = itemPlan.Item;
            if (current.Snapshot.ReservationId.HasValue || current.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active)
            {
                error = $"Destroyed item {itemId} is reserved or no longer active.";
                return false;
            }
            // Assembly children have no independent ECS entity. Destroying their physical parent therefore closes
            // every child aggregate in the same atomic actor hand-off, preventing orphaned persistent weapon parts.
            foreach (var installedPart in itemPlan.InstalledParts.OrderBy(part => part.Snapshot.ItemId.Value))
            {
                if (installedPart.Snapshot.ParentItemId != itemId ||
                    installedPart.Snapshot.ReservationId.HasValue ||
                    installedPart.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active)
                {
                    error = $"Installed part {installedPart.Snapshot.ItemId} cannot follow destroyed parent {itemId}.";
                    return false;
                }
                var partEventId = Guid.NewGuid();
                var nextPart = installedPart.Snapshot with
                {
                    ParentItemId = null,
                    InstalledPartIds = [],
                    Location = TerminalLocation(NCEconomicAssetLocationKind.Destroyed),
                    Lifecycle = NCEconomicAssetLifecycle.Destroyed,
                    UpdatedAt = now,
                    DataVersion = checked(installedPart.Snapshot.DataVersion + 1),
                    LastAppliedEventId = partEventId,
                };
                result.Add(ItemMutationWrite(installedPart, nextPart, partEventId,
                    "living-city.item-instance.destroyed-with-assembly-parent", auditSource));
            }

            var eventId = Guid.NewGuid();
            var next = current.Snapshot with
            {
                ReservationId = null,
                InstalledPartIds = [],
                Location = TerminalLocation(NCEconomicAssetLocationKind.Destroyed),
                Lifecycle = NCEconomicAssetLifecycle.Destroyed,
                UpdatedAt = now,
                DataVersion = checked(current.Snapshot.DataVersion + 1),
                LastAppliedEventId = eventId,
            };
            result.Add(ItemMutationWrite(current, next, eventId,
                "living-city.item-instance.destroyed-while-materialized", auditSource));
            nextItemIds.Remove(itemId);
        }

        foreach (var (itemId, itemPlan) in expectedItems.OrderBy(pair => pair.Key.Value))
        {
            if (destroyedItems.Contains(itemId))
                continue;
            var current = itemPlan.Item;
            var wasInstalled = current.Snapshot.Location.Kind == NCEconomicAssetLocationKind.InstalledInActor;
            var isInstalled = capturedInstalledCyberware.Contains(itemId);
            if (wasInstalled == isInstalled)
                continue;
            if (itemPlan.Domain != NCEconomicAssetDomain.Cyberware || current.Snapshot.ReservationId.HasValue ||
                current.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active)
            {
                error = $"Item {itemId} has an invalid persistent cyberware installation transition.";
                return false;
            }

            var eventId = Guid.NewGuid();
            var next = current.Snapshot with
            {
                Location = isInstalled
                    ? new NCEconomicAssetLocation(NCEconomicAssetLocationKind.InstalledInActor,
                        plan.ActorId.ToString())
                    : StockLocation(plan.StockNode.Snapshot.StockNodeId),
                UpdatedAt = now,
                DataVersion = checked(current.Snapshot.DataVersion + 1),
                LastAppliedEventId = eventId,
            };
            if (isInstalled)
                nextItemIds.Remove(itemId);
            else
            {
                if (nextItemIds.Count >= policy.MaximumItemsPerStockNode)
                {
                    error = "Removed cyberware would exceed the actor stock-node capacity.";
                    return false;
                }
                nextItemIds.Add(itemId);
            }
            result.Add(ItemMutationWrite(current, next, eventId,
                isInstalled
                    ? "living-city.item-instance.installed-in-actor"
                    : "living-city.item-instance.removed-from-actor",
                auditSource));
        }

        var expectedBatches = plan.Commodities.ToDictionary(entry => entry.Batch.Snapshot.BatchId);
        var capturedBatches = captured.Commodities.ToDictionary(bundle => bundle.BatchId);
        if (capturedBatches.Keys.Any(id => !expectedBatches.ContainsKey(id)))
        {
            error = "The captured commodity set contains a batch outside the materialization lease.";
            return false;
        }

        var nextBatchIds = plan.StockNode.Snapshot.CommodityBatchIds.ToHashSet();
        foreach (var (batchId, entry) in expectedBatches.OrderBy(pair => pair.Key.Value))
        {
            var current = entry.Batch;
            var quantity = capturedBatches.GetValueOrDefault(batchId)?.Units ?? 0;
            if (quantity < 0 || quantity > current.Snapshot.Quantity || current.Snapshot.ReservedQuantity != 0 ||
                current.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active)
            {
                error = $"Captured commodity batch {batchId} has an invalid remaining quantity or state.";
                return false;
            }
            if (quantity == current.Snapshot.Quantity)
                continue;

            var eventId = Guid.NewGuid();
            var consumed = quantity == 0;
            var next = current.Snapshot with
            {
                Quantity = quantity,
                ReservedQuantity = 0,
                Location = consumed
                    ? TerminalLocation(NCEconomicAssetLocationKind.Consumed)
                    : current.Snapshot.Location,
                Lifecycle = consumed ? NCEconomicAssetLifecycle.Consumed : NCEconomicAssetLifecycle.Active,
                UpdatedAt = now,
                DataVersion = checked(current.Snapshot.DataVersion + 1),
                LastAppliedEventId = eventId,
            };
            result.Add(CommodityMutationWrite(current, next, eventId,
                consumed
                    ? "living-city.commodity-batch.consumed-while-materialized"
                    : "living-city.commodity-batch.quantity-captured",
                auditSource));
            if (consumed)
                nextBatchIds.Remove(batchId);
        }

        if (!nextItemIds.SetEquals(plan.StockNode.Snapshot.ItemIds) ||
            !nextBatchIds.SetEquals(plan.StockNode.Snapshot.CommodityBatchIds))
        {
            var eventId = Guid.NewGuid();
            var nextNode = plan.StockNode.Snapshot with
            {
                ItemIds = nextItemIds.OrderBy(id => id.Value).ToArray(),
                CommodityBatchIds = nextBatchIds.OrderBy(id => id.Value).ToArray(),
                UpdatedAt = now,
                DataVersion = checked(plan.StockNode.Snapshot.DataVersion + 1),
                LastAppliedEventId = eventId,
            };
            result.Add(StockNodeWrite(plan.StockNode, nextNode, eventId,
                "living-city.stock-node.materialized-assets-removed", auditSource));
        }

        if (result.Count > MaximumActorDematerializationParticipantWrites)
        {
            result.Clear();
            error = "The physical actor changed too many persistent assets for one bounded atomic hand-off.";
            return false;
        }
        return true;
    }

    private static NCEconomicAssetLocation TerminalLocation(NCEconomicAssetLocationKind kind) =>
        new(kind, kind == NCEconomicAssetLocationKind.Consumed ? "consumed" : "destroyed");
}
