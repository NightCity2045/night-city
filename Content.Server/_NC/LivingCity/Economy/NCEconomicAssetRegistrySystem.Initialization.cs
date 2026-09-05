// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text.Json;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Identity;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Economy;

public sealed partial class NCEconomicAssetRegistrySystem
{
    public static NCStockNodeId ActorInventoryStockNode(NCActorId actorId)
    {
        if (!actorId.IsValid)
            throw new ArgumentOutOfRangeException(nameof(actorId));
        return NCPersistentIdentity.StockNode(NCPartyRef.ForActor(actorId), "actor-inventory");
    }

    /// <summary>
    /// Initial stock authority is committed in the same transaction as a new persistent actor. This prevents an actor
    /// from ever becoming schedulable with a missing inventory ownership root.
    /// </summary>
    public static NCPersistentAggregateWrite BuildInitialActorStockNodeWrite(
        NCActorId actorId,
        NCActiveWorldTime now,
        string auditSource)
    {
        if (!actorId.IsValid || !now.IsValid || string.IsNullOrWhiteSpace(auditSource))
            throw new ArgumentException("The actor stock-node initialization input is invalid.");

        var owner = NCPartyRef.ForActor(actorId);
        var stockNodeId = ActorInventoryStockNode(actorId);
        var eventId = Guid.NewGuid();
        var snapshot = new NCStockNodeSnapshot(
            stockNodeId,
            "actor-inventory",
            NCStockNodeKind.ActorInventory,
            owner,
            $"actor.{actorId.Value:D}.inventory",
            [],
            [],
            true,
            true,
            now,
            now,
            1,
            eventId);
        return new NCPersistentAggregateWrite(
            eventId,
            "living-city.stock-node.actor-inventory-initialized",
            StockNodeAggregateType,
            stockNodeId.Value.ToString("D"),
            0,
            SnapshotFormatVersion,
            JsonSerializer.Serialize(snapshot, JsonOptions),
            JsonSerializer.Serialize(new AssetEvent(stockNodeId.Value.ToString("D"), auditSource.Trim()), JsonOptions));
    }

    /// <summary>Repairs residents created before the inventory authority was restored to actor creation.</summary>
    public async Task<NCStockNodeRecord?> EnsureActorInventoryStockNodeAsync(
        NCActorId actorId,
        string auditSource)
    {
        if (!actorId.IsValid || string.IsNullOrWhiteSpace(auditSource))
            return null;

        var owner = NCPartyRef.ForActor(actorId);
        var nodeId = ActorInventoryStockNode(actorId);
        var existing = await GetStockNodeAsync(nodeId, true);
        if (existing != null)
        {
            return existing.Snapshot.Owner == owner && existing.Snapshot.Kind == NCStockNodeKind.ActorInventory
                ? existing
                : null;
        }

        var stable = actorId.Value.ToString("D");
        var created = await CreateStockNodeAsync(new NCCreateStockNodeRequest(
            NCPersistentIdentity.Command("living-city-actor", "repair-inventory-stock", stable),
            nodeId,
            "actor-inventory",
            NCStockNodeKind.ActorInventory,
            owner,
            $"actor.{stable}.inventory",
            true,
            true,
            owner,
            NCPersistentIdentity.Correlation("living-city-actor", "repair-inventory-stock", stable),
            AuditSource: auditSource));
        return created.Status is NCEconomicAssetMutationStatus.Applied or NCEconomicAssetMutationStatus.Duplicate
            ? created.StockNode
            : null;
    }
}
