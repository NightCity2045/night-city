// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Actors.Domains;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Medical.Cyberware;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.FixedPoint;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared._NC.Medical.Cyberware;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Economy;

/// <summary>
/// Owns the round-scoped one-to-one projection between persistent stock and ECS items. Persistent stock remains the
/// authority; a lease prevents the same actor inventory from being materialized twice in one active round.
/// </summary>
public sealed partial class NCEconomicPhysicalizationSystem : EntitySystem
{
    private const int MaximumPhysicalEntitiesPerActor = 512;
    private const int MaximumAssemblyPartsPerActorLease = 24;

    [Dependency] private NCEconomicAssetRegistrySystem _assets = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private NCEconomicMaterializationLeaseIndexSystem _leaseIndex = default!;
    [Dependency] private NCCyberwareSystem _cyberware = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;

    private readonly Dictionary<NCActorId, MaterializationLease> _leases = [];

    public int ActiveLeaseCount => _leases.Count;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(OnSessionClosed);
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(OnClockFailed);
    }

    public bool IsStockNodeMaterialized(NCStockNodeId stockNodeId) => _leaseIndex.IsLeased(stockNodeId);

    public IReadOnlyList<NCEconomicMaterializationLeaseView> GetLeaseViews(int maximum)
    {
        if (maximum <= 0)
            return [];
        return _leases.Values.OrderBy(value => value.ActorId.Value).Take(maximum).Select(value =>
            new NCEconomicMaterializationLeaseView(
                value.ActorId,
                value.StockNodeId,
                value.ActorEntity,
                value.ItemEntities.Count,
                value.CommodityEntities.Values.Sum(entities => entities.Count))).ToArray();
    }

    public async Task<NCEconomicMaterializationPlanResult> BuildActorInventoryPlanAsync(
        NCActorId actorId,
        NCCityActorInventorySnapshot inventory)
    {
        if (!actorId.IsValid || inventory.ActorId != actorId || inventory.Summary == null ||
            !_clock.TryGetCurrentTime(out _, out var now))
            return new(null, "The actor inventory materialization request is invalid.");

        var owner = NCPartyRef.ForActor(actorId);
        var nodeId = NCEconomicAssetRegistrySystem.ActorInventoryStockNode(actorId);
        var node = await _assets.GetStockNodeAsync(nodeId, true);
        if (node == null || node.Snapshot.Owner != owner || node.Snapshot.Kind != NCStockNodeKind.ActorInventory)
            return new(null, "The actor inventory stock authority is missing or incompatible.");

        // The actor-domain inventory is a strategic projection. StockNode and its item/batch aggregates are the
        // canonical ownership source, so a newer economic mutation is allowed to supersede a stale summary here.
        var installedItemIds = inventory.Summary.InstalledCyberware.ToHashSet();
        var summaryItemIds = node.Snapshot.ItemIds.Concat(installedItemIds)
            .Distinct().OrderBy(id => id.Value).ToArray();
        var summaryBatchIds = node.Snapshot.CommodityBatchIds.Distinct().OrderBy(id => id.Value).ToArray();

        var itemPlans = new List<NCEconomicPhysicalItemPlan>(summaryItemIds.Length);
        var assemblyPartCount = 0;
        foreach (var itemId in summaryItemIds)
        {
            var item = await _assets.GetItemAsync(itemId, true);
            var installed = installedItemIds.Contains(itemId);
            var expectedLocation = installed
                ? new NCEconomicAssetLocation(NCEconomicAssetLocationKind.InstalledInActor, actorId.ToString())
                : StockLocation(nodeId);
            if (item == null || item.Snapshot.Owner != owner ||
                item.Snapshot.Location != expectedLocation || item.Snapshot.ReservationId.HasValue ||
                item.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active ||
                !_prototypes.TryIndex(new ProtoId<NCItemDefinitionPrototype>(item.Snapshot.DefinitionId),
                    out var definition) || definition.MaterializedPrototype is not { } entityPrototype)
            {
                return new(null, $"Item {itemId} cannot be materialized from this actor stock node.");
            }

            var categories = NCCityInventoryItemCategory.Unique;
            switch (definition.Domain)
            {
                case NCEconomicAssetDomain.WeaponAssembly:
                    categories |= NCCityInventoryItemCategory.Weapon;
                    break;
                case NCEconomicAssetDomain.Document:
                    categories |= NCCityInventoryItemCategory.Document;
                    break;
                case NCEconomicAssetDomain.Credential:
                    categories |= NCCityInventoryItemCategory.Credential;
                    break;
            }
            if (installed && definition.Domain != NCEconomicAssetDomain.Cyberware)
                return new(null, $"Non-cyberware item {itemId} is marked installed in the actor.");

            // Installed parts remain distinct persistent assets, but the parent is their only physical projection.
            // Loading them here makes the lease an immutable, bounded snapshot of the complete assembly graph.
            var installedParts = new List<NCItemInstanceRecord>(item.Snapshot.InstalledPartIds.Count);
            foreach (var partId in item.Snapshot.InstalledPartIds.OrderBy(id => id.Value))
            {
                var part = await _assets.GetItemAsync(partId, true);
                if (part == null || part.Snapshot.Owner != owner ||
                    part.Snapshot.ParentItemId != itemId ||
                    part.Snapshot.Location != new NCEconomicAssetLocation(
                        NCEconomicAssetLocationKind.InstalledInItem, itemId.ToString()) ||
                    part.Snapshot.ReservationId.HasValue ||
                    part.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active ||
                    !_prototypes.TryIndex(new ProtoId<NCItemDefinitionPrototype>(part.Snapshot.DefinitionId),
                        out var partDefinition) ||
                    definition.Domain != NCEconomicAssetDomain.WeaponAssembly ||
                    partDefinition.Domain != NCEconomicAssetDomain.WeaponAssembly ||
                    !definition.AllowedInstalledPartDefinitions.Contains(
                        new ProtoId<NCItemDefinitionPrototype>(partDefinition.ID)))
                {
                    return new(null, $"Installed part {partId} is inconsistent with parent item {itemId}.");
                }
                installedParts.Add(part);
            }
            assemblyPartCount = checked(assemblyPartCount + installedParts.Count);
            if (assemblyPartCount > MaximumAssemblyPartsPerActorLease)
                return new(null, "The actor item assemblies exceed the bounded persistent-part budget.");
            itemPlans.Add(new NCEconomicPhysicalItemPlan(
                item, entityPrototype, categories, definition.Domain, installed, installedParts));
        }

        var commodityPlans = new List<NCEconomicPhysicalCommodityPlan>(summaryBatchIds.Length);
        var physicalCount = itemPlans.Count(item => !item.InstalledInActor);
        foreach (var batchId in summaryBatchIds)
        {
            var batch = await _assets.GetCommodityBatchAsync(batchId, true);
            if (batch == null || batch.Snapshot.Owner != owner || batch.Snapshot.Location != StockLocation(nodeId) ||
                batch.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active || batch.Snapshot.ReservedQuantity != 0 ||
                batch.Snapshot.ExpiresAt is { } expiresAt && now >= expiresAt ||
                !_prototypes.TryIndex(new ProtoId<NCCommodityPrototype>(batch.Snapshot.CommodityId),
                    out var commodity) || commodity.MaterializedPrototype is not { } entityPrototype)
            {
                return new(null, $"Commodity batch {batchId} cannot be materialized from this actor stock node.");
            }
            var entities = checked((batch.Snapshot.Quantity + commodity.MaterializedUnitsPerEntity - 1) /
                commodity.MaterializedUnitsPerEntity);
            if (entities > MaximumPhysicalEntitiesPerActor || physicalCount + entities > MaximumPhysicalEntitiesPerActor)
                return new(null, "The actor inventory exceeds the bounded physical entity budget.");
            physicalCount += checked((int) entities);
            commodityPlans.Add(new NCEconomicPhysicalCommodityPlan(
                batch,
                entityPrototype,
                commodity.MaterializedUnitsPerEntity,
                commodity.MaterializedSolutionId,
                commodity.MaterializedReagent?.Id));
        }

        return new(new NCActorInventoryMaterializationPlan(actorId, node, itemPlans, commodityPlans, physicalCount), null);
    }

    public bool TryMaterialize(
        EntityUid actorEntity,
        NCActorInventoryMaterializationPlan plan,
        out string? error)
    {
        error = null;
        if (!Exists(actorEntity) || _leases.ContainsKey(plan.ActorId) ||
            !_leaseIndex.TryAcquire(plan.ActorId, plan.StockNode.Snapshot.StockNodeId, actorEntity))
        {
            error = "The actor inventory already has a physicalization lease or its ECS entity is invalid.";
            return false;
        }

        var spawned = new List<EntityUid>(plan.PhysicalEntityCount);
        var itemEntities = new Dictionary<NCItemId, EntityUid>();
        var commodityEntities = new Dictionary<NCCommodityBatchId, List<EntityUid>>();
        var container = _containers.EnsureContainer<Container>(
            actorEntity,
            NCCityActorInventoryAdapterSystem.EconomicInventoryContainerId);
        try
        {
            foreach (var itemPlan in plan.Items)
            {
                EntityUid entity;
                if (itemPlan.InstalledInActor)
                {
                    if (!_cyberware.TryGetInstalledModuleEntity(
                            actorEntity,
                            itemPlan.Item.Snapshot.ItemId.Value,
                            out entity))
                    {
                        throw new InvalidOperationException(
                            "The medical restore did not provide the installed economic cyberware instance.");
                    }
                }
                else
                {
                    entity = Spawn(itemPlan.EntityPrototype, Transform(actorEntity).Coordinates);
                    spawned.Add(entity);
                }
                var adapter = EnsureComp<NCCityPersistentItemAdapterComponent>(entity);
                adapter.ItemId = itemPlan.Item.Snapshot.ItemId;
                adapter.Categories = itemPlan.Categories;
                if (itemPlan.Domain == NCEconomicAssetDomain.Cyberware)
                {
                    if (!TryComp<NCCyberwareComponent>(entity, out var cyberware))
                        throw new InvalidOperationException("The authored cyberware item has no cyberware component.");
                    cyberware.StableInstanceId = itemPlan.Item.Snapshot.ItemId.Value;
                }
                if (itemPlan.Domain == NCEconomicAssetDomain.WeaponAssembly)
                {
                    var assembly = EnsureComp<NCEconomicAssemblyAdapterComponent>(entity);
                    assembly.InstalledPartIds = itemPlan.InstalledParts
                        .Select(part => part.Snapshot.ItemId)
                        .ToList();
                }
                if (!itemPlan.InstalledInActor &&
                    !_containers.Insert(entity, container, containerXform: Transform(actorEntity)))
                    throw new InvalidOperationException("A unique item could not enter the actor economy container.");
                itemEntities.Add(adapter.ItemId, entity);
            }

            foreach (var commodityPlan in plan.Commodities)
            {
                var remaining = commodityPlan.Batch.Snapshot.Quantity;
                var entities = new List<EntityUid>();
                while (remaining > 0)
                {
                    var units = Math.Min(remaining, commodityPlan.UnitsPerEntity);
                    var entity = Spawn(commodityPlan.EntityPrototype, Transform(actorEntity).Coordinates);
                    spawned.Add(entity);
                    if (!TryApplyCommodityContents(entity, commodityPlan, units, out error))
                        throw new InvalidOperationException(error);
                    var adapter = EnsureComp<NCCityPersistentItemAdapterComponent>(entity);
                    adapter.Categories = NCCityInventoryItemCategory.Commodity;
                    adapter.CommodityBatchId = commodityPlan.Batch.Snapshot.BatchId;
                    adapter.CommodityId = commodityPlan.Batch.Snapshot.CommodityId;
                    adapter.CommodityUnits = units;
                    if (!_containers.Insert(entity, container, containerXform: Transform(actorEntity)))
                        throw new InvalidOperationException("A commodity item could not enter the actor economy container.");
                    entities.Add(entity);
                    remaining -= units;
                }
                commodityEntities.Add(commodityPlan.Batch.Snapshot.BatchId, entities);
            }
        }
        catch (Exception exception)
        {
            foreach (var entity in spawned)
            {
                if (Exists(entity))
                    QueueDel(entity);
            }
            _leaseIndex.TryRelease(plan.ActorId, actorEntity);
            error = exception.Message;
            return false;
        }

        var lease = new MaterializationLease(
            plan.ActorId,
            plan.StockNode.Snapshot.StockNodeId,
            actorEntity,
            plan,
            itemEntities,
            commodityEntities);
        _leases.Add(plan.ActorId, lease);
        return true;
    }

    private bool TryApplyCommodityContents(
        EntityUid entity,
        NCEconomicPhysicalCommodityPlan plan,
        long units,
        out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(plan.SolutionId) && plan.ReagentId == null)
            return true;
        if (string.IsNullOrWhiteSpace(plan.SolutionId) || plan.ReagentId == null || units > int.MaxValue ||
            !_prototypes.HasIndex(new ProtoId<ReagentPrototype>(plan.ReagentId)) ||
            !_solutions.TryGetSolution(entity, plan.SolutionId, out var solutionEntity, out _))
        {
            error = "The commodity entity has no compatible authored solution adapter.";
            return false;
        }
        _solutions.RemoveAllSolution(solutionEntity.Value);
        if (!_solutions.TryAddReagent(
                solutionEntity.Value,
                new ProtoId<ReagentPrototype>(plan.ReagentId),
                FixedPoint2.New((int) units)))
        {
            error = "The materialized commodity contents exceed the physical solution capacity.";
            return false;
        }
        return true;
    }

    private static NCEconomicAssetLocation StockLocation(NCStockNodeId id) =>
        new(NCEconomicAssetLocationKind.StockNode, id.Value.ToString("D"));

    private void OnSessionClosed(NCActiveWorldSessionClosedEvent args) => ClearLeases();
    private void OnClockFailed(NCActiveWorldClockFailedEvent args) => ClearLeases();

    private void ClearLeases()
    {
        _leases.Clear();
    }

    private sealed record MaterializationLease(
        NCActorId ActorId,
        NCStockNodeId StockNodeId,
        EntityUid ActorEntity,
        NCActorInventoryMaterializationPlan Plan,
        IReadOnlyDictionary<NCItemId, EntityUid> ItemEntities,
        IReadOnlyDictionary<NCCommodityBatchId, List<EntityUid>> CommodityEntities);
}
