// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Containers.ItemSlots;

namespace Content.Shared._NC.Bank.Budget;

/// <summary>
/// Owns the organization console cash slot lifecycle on both client and server.
/// </summary>
public sealed partial class SharedNCOrganizationBudgetConsoleSystem : EntitySystem
{
    [Dependency] private ItemSlotsSystem _itemSlots = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCOrganizationBudgetConsoleComponent, ComponentInit>(OnComponentInit);
        SubscribeLocalEvent<NCOrganizationBudgetConsoleComponent, ComponentRemove>(OnComponentRemove);
    }

    private void OnComponentInit(EntityUid uid, NCOrganizationBudgetConsoleComponent component, ComponentInit args)
    {
        _itemSlots.AddItemSlot(uid, NCOrganizationBudgetConsoleComponent.CashSlotId, component.CashSlot);
    }

    private void OnComponentRemove(EntityUid uid, NCOrganizationBudgetConsoleComponent component,
        ComponentRemove args)
    {
        _itemSlots.RemoveItemSlot(uid, component.CashSlot);
    }
}
