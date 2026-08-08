// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Bank.Components;
using Content.Shared.Containers.ItemSlots;

namespace Content.Shared._NC.Bank.ATM;

/// <summary>
/// Owns the lifecycle of the ATM cash slot on both client and server.
/// </summary>
public sealed partial class SharedNCAtmSystem : EntitySystem
{
    [Dependency] private ItemSlotsSystem _itemSlots = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCAtmComponent, ComponentInit>(OnComponentInit);
        SubscribeLocalEvent<NCAtmComponent, ComponentRemove>(OnComponentRemove);
    }

    private void OnComponentInit(EntityUid uid, NCAtmComponent component, ComponentInit args)
    {
        _itemSlots.AddItemSlot(uid, NCAtmComponent.CashSlotId, component.CashSlot);
    }

    private void OnComponentRemove(EntityUid uid, NCAtmComponent component, ComponentRemove args)
    {
        _itemSlots.RemoveItemSlot(uid, component.CashSlot);
    }
}
