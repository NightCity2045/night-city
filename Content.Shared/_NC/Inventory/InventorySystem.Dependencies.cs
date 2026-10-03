// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Armor;

namespace Content.Shared.Inventory;

public abstract partial class InventorySystem
{
    /// <summary>
    /// Checks the legacy single dependency or any configured alternative dependency.
    /// </summary>
    private bool AreSlotDependenciesSatisfied(
        EntityUid target,
        EntityUid item,
        SlotDefinition slotDefinition,
        InventoryComponent inventory)
    {
        if (slotDefinition.DependsOn is { } dependency)
            return IsSlotDependencySatisfied(target, item, dependency, slotDefinition, inventory);

        if (slotDefinition.DependsOnAny.Length == 0)
            return true;

        foreach (var alternative in slotDefinition.DependsOnAny)
        {
            if (IsSlotDependencySatisfied(target, item, alternative, slotDefinition, inventory))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Validates one equipped provider, including the provider-specific suit-storage whitelist.
    /// </summary>
    private bool IsSlotDependencySatisfied(
        EntityUid target,
        EntityUid item,
        string dependency,
        SlotDefinition slotDefinition,
        InventoryComponent inventory)
    {
        if (!TryGetSlotEntity(target, dependency, out EntityUid? provider, inventory) || provider is null)
            return false;

        if (slotDefinition.DependsOnComponents is not { } componentRegistry)
            return true;

        foreach (var (_, entry) in componentRegistry)
        {
            if (!HasComp(provider.Value, entry.Component.GetType()))
                return false;
        }

        // AllowSuitStorage owns the per-provider whitelist, so alternatives may accept different items.
        return !TryComp<AllowSuitStorageComponent>(provider.Value, out var suitStorage) ||
               !_whitelistSystem.IsWhitelistFailOrNull(suitStorage.Whitelist, item);
    }
}
