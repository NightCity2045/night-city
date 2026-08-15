// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Shared._NC.LivingCity.Actors;
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
    private static readonly ProtoId<NCCityActorSummaryPolicyPrototype> DefaultPolicy =
        new("NCDefaultCityActorSummaryPolicy");
    private const int MaximumPersistentStringLength = 128;

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedContainerSystem _containers = default!;

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
        var slots = _inventory.GetSlotEnumerator(actorEntity);
        while (slots.NextItem(out var item, out _))
            pending.Push((item, 0));

        if (TryComp<HandsComponent>(actorEntity, out var hands))
        {
            foreach (var held in _hands.EnumerateHeld((actorEntity, hands)))
                pending.Push((held, 0));
        }

        var visitedEntities = new HashSet<EntityUid>();
        var stableItems = new HashSet<NCItemId>();
        var unique = new HashSet<NCItemId>();
        var weapons = new HashSet<NCItemId>();
        var documents = new HashSet<NCItemId>();
        var credentials = new HashSet<NCItemId>();
        var commodities = new Dictionary<string, long>(StringComparer.Ordinal);

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
                !TryReadAdapter(adapter, stableItems, unique, weapons, documents, credentials,
                    commodities, policy, out error))
            {
                error ??= $"Materialized inventory item {ToPrettyString(entry.Entity)} has no valid persistent adapter.";
                return false;
            }

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
            commodities.OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                .Select(static entry => new NCCityCommodityBundle(entry.Key, entry.Value))
                .ToArray(),
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

    private static bool TryReadAdapter(
        NCCityPersistentItemAdapterComponent adapter,
        HashSet<NCItemId> stableItems,
        HashSet<NCItemId> unique,
        HashSet<NCItemId> weapons,
        HashSet<NCItemId> documents,
        HashSet<NCItemId> credentials,
        Dictionary<string, long> commodities,
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
        if (commodityId.Length is < 1 or > MaximumPersistentStringLength || adapter.CommodityUnits <= 0)
        {
            error = "A commodity adapter has an invalid identifier or unit count.";
            return false;
        }

        if (!commodities.ContainsKey(commodityId) && commodities.Count >= policy.MaximumCommodityBundles)
        {
            error = "The materialized inventory exceeds its commodity-bundle bound.";
            return false;
        }

        try
        {
            commodities[commodityId] = checked(commodities.GetValueOrDefault(commodityId) + adapter.CommodityUnits);
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
