// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.Medical.Cyberware;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.FixedPoint;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Actors.Domains;

/// <summary>
/// Reads only an actor's equipped/held containment graph and translates economy adapter keys to stable IDs.
/// Untagged physical items fail closed because inventing persistent ownership from an EntityUid would duplicate it.
/// </summary>
public sealed partial class NCCityActorInventoryAdapterSystem : EntitySystem
{
    public const string EconomicInventoryContainerId = "nc-economic-inventory";
    private static readonly ProtoId<NCCityActorSummaryPolicyPrototype> DefaultPolicy =
        new("NCDefaultCityActorSummaryPolicy");
    private const int MaximumPersistentStringLength = 128;

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private NCCyberwareSystem _cyberware = default!;

    public bool TryCapture(
        EntityUid actorEntity,
        NCActorId actorId,
        NCActiveWorldTime now,
        Guid eventId,
        out NCCityActorInventorySnapshot result,
        out string? error)
    {
        result = default!;
        error = null;
        if (!actorId.IsValid || !now.IsValid || eventId == Guid.Empty ||
            !TryComp<NCCityActorInventoryAdapterComponent>(actorEntity, out var authority) ||
            authority.AuthorityVersion < 0 || authority.EquippedLoadoutId.Length > MaximumPersistentStringLength)
        {
            error = "The actor inventory identity, time, event, or authority adapter is invalid.";
            return false;
        }

        var policy = _prototypes.Index(DefaultPolicy);
        var pending = new Stack<(EntityUid Entity, int Depth)>();
        var installedCyberwareEntities = _cyberware.GetInstalledModuleEntities(
            actorEntity,
            policy.MaximumInventoryItems).ToHashSet();
        foreach (var module in installedCyberwareEntities)
            pending.Push((module, 0));
        var slots = _inventory.GetSlotEnumerator(actorEntity);
        while (slots.NextItem(out var item, out _))
            pending.Push((item, 0));

        if (TryComp<HandsComponent>(actorEntity, out var hands))
        {
            foreach (var held in _hands.EnumerateHeld((actorEntity, hands)))
                pending.Push((held, 0));
        }
        if (_containers.TryGetContainer(actorEntity, EconomicInventoryContainerId, out var economicInventory))
        {
            foreach (var contained in economicInventory.ContainedEntities)
                pending.Push((contained, 0));
        }

        var visitedEntities = new HashSet<EntityUid>();
        var stableItems = new HashSet<NCItemId>();
        var unique = new HashSet<NCItemId>();
        var weapons = new HashSet<NCItemId>();
        var documents = new HashSet<NCItemId>();
        var credentials = new HashSet<NCItemId>();
        var installedCyberware = new HashSet<NCItemId>();
        var commodities = new Dictionary<NCCommodityBatchId, (string CommodityId, long Units)>();

        while (pending.TryPop(out var entry))
        {
            if (!visitedEntities.Add(entry.Entity))
                continue;
            if (visitedEntities.Count > policy.MaximumInventoryItems || entry.Depth > policy.MaximumContainerDepth)
            {
                error = "The materialized inventory exceeds its bounded traversal policy.";
                return false;
            }

            if (!TryComp<NCCityPersistentItemAdapterComponent>(entry.Entity, out var adapter) ||
                !TryReadAdapter(entry.Entity, adapter, stableItems, unique, weapons, documents, credentials,
                    commodities, policy, out error))
            {
                error ??= $"Materialized inventory item {ToPrettyString(entry.Entity)} has no valid persistent adapter.";
                return false;
            }
            if (installedCyberwareEntities.Contains(entry.Entity))
                installedCyberware.Add(adapter.ItemId);

            if (!TryComp<ContainerManagerComponent>(entry.Entity, out var containerManager))
                continue;

            foreach (var container in _containers.GetAllContainers(entry.Entity, containerManager))
            {
                foreach (var contained in container.ContainedEntities)
                    pending.Push((contained, entry.Depth + 1));
            }
        }

        var summary = new NCCityActorInventorySummary(
            Sort(unique),
            Sort(weapons),
            Sort(documents),
            Sort(credentials),
            commodities.OrderBy(static entry => entry.Key.Value)
                .Select(static entry => new NCCityCommodityBundle(
                    entry.Key,
                    entry.Value.CommodityId,
                    entry.Value.Units))
                .ToArray(),
            Sort(installedCyberware),
            authority.EquippedLoadoutId.Trim());
        result = new NCCityActorInventorySnapshot(
            actorId,
            summary,
            authority.AuthorityVersion,
            now,
            1,
            eventId);
        return true;
    }

    private bool TryReadAdapter(
        EntityUid entity,
        NCCityPersistentItemAdapterComponent adapter,
        HashSet<NCItemId> stableItems,
        HashSet<NCItemId> unique,
        HashSet<NCItemId> weapons,
        HashSet<NCItemId> documents,
        HashSet<NCItemId> credentials,
        Dictionary<NCCommodityBatchId, (string CommodityId, long Units)> commodities,
        NCCityActorSummaryPolicyPrototype policy,
        out string? error)
    {
        error = null;
        const NCCityInventoryItemCategory all =
            NCCityInventoryItemCategory.Unique |
            NCCityInventoryItemCategory.Weapon |
            NCCityInventoryItemCategory.Document |
            NCCityInventoryItemCategory.Credential |
            NCCityInventoryItemCategory.Commodity;
        if (adapter.Categories == NCCityInventoryItemCategory.None || (adapter.Categories & ~all) != 0)
        {
            error = "A persistent item has invalid or empty projection categories.";
            return false;
        }

        var needsStableId = (adapter.Categories & ~NCCityInventoryItemCategory.Commodity) != 0;
        if (needsStableId && (!adapter.ItemId.IsValid || !stableItems.Add(adapter.ItemId)))
        {
            error = "A persistent unique item ID is missing or duplicated in the materialized inventory.";
            return false;
        }

        if ((adapter.Categories & NCCityInventoryItemCategory.Unique) != 0)
            unique.Add(adapter.ItemId);
        if ((adapter.Categories & NCCityInventoryItemCategory.Weapon) != 0)
            weapons.Add(adapter.ItemId);
        if ((adapter.Categories & NCCityInventoryItemCategory.Document) != 0)
            documents.Add(adapter.ItemId);
        if ((adapter.Categories & NCCityInventoryItemCategory.Credential) != 0)
            credentials.Add(adapter.ItemId);

        if ((adapter.Categories & NCCityInventoryItemCategory.Commodity) == 0)
            return true;

        var commodityId = adapter.CommodityId.Trim();
        if (!adapter.CommodityBatchId.IsValid || commodityId.Length is < 1 or > MaximumPersistentStringLength ||
            adapter.CommodityUnits <= 0 ||
            !_prototypes.TryIndex(new ProtoId<NCCommodityPrototype>(commodityId), out var commodity))
        {
            error = "A commodity adapter has an invalid identifier or unit count.";
            return false;
        }

        var physicalUnits = adapter.CommodityUnits;
        if (commodity.MaterializedReagent is { } reagent)
        {
            if (string.IsNullOrWhiteSpace(commodity.MaterializedSolutionId) ||
                !_solutions.TryGetSolution(entity, commodity.MaterializedSolutionId, out _, out var solution))
            {
                error = "A materialized commodity has no readable authored solution.";
                return false;
            }
            var quantity = solution.GetTotalPrototypeQuantity(new ProtoId<ReagentPrototype>(reagent.Id));
            if (quantity != FixedPoint2.New(quantity.Int()) || quantity.Int() < 0 ||
                quantity.Int() > adapter.CommodityUnits || solution.Volume != quantity)
            {
                error = "A materialized commodity solution is fractional, contaminated, or exceeds its adapter units.";
                return false;
            }
            physicalUnits = quantity.Int();
        }
        if (physicalUnits == 0)
            return true;

        if (!commodities.ContainsKey(adapter.CommodityBatchId) && commodities.Count >= policy.MaximumCommodityBundles)
        {
            error = "The materialized inventory exceeds its commodity-bundle bound.";
            return false;
        }

        try
        {
            if (commodities.TryGetValue(adapter.CommodityBatchId, out var current) &&
                current.CommodityId != commodityId)
            {
                error = "One persistent commodity batch is projected with conflicting commodity definitions.";
                return false;
            }
            commodities[adapter.CommodityBatchId] = (
                commodityId,
                checked(current.Units + physicalUnits));
        }
        catch (OverflowException)
        {
            error = "A commodity quantity overflowed its persistent representation.";
            return false;
        }

        return true;
    }

    private static NCItemId[] Sort(HashSet<NCItemId> values)
    {
        return values.OrderBy(static value => value.Value).ToArray();
    }
}
