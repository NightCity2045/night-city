// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared.Containers.ItemSlots;

namespace Content.Shared._NC.Bank;

/// <summary>Registers the cash slot of bank machines on both sides so inserting cash is predicted.</summary>
public sealed class NCBankMachineSystem : EntitySystem
{
    [Dependency] private ItemSlotsSystem _itemSlots = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCAtmComponent, ComponentInit>(OnInit);
        SubscribeLocalEvent<NCAtmComponent, ComponentRemove>(OnRemove);
        SubscribeLocalEvent<NCBankTerminalComponent, ComponentInit>(OnInit);
        SubscribeLocalEvent<NCBankTerminalComponent, ComponentRemove>(OnRemove);
    }

    private void OnInit<T>(Entity<T> ent, ref ComponentInit args) where T : IComponent, INCBankCashMachine
    {
        _itemSlots.AddItemSlot(ent, NCAtmComponent.CashSlotId, ent.Comp.CashSlot);
    }

    private void OnRemove<T>(Entity<T> ent, ref ComponentRemove args) where T : IComponent, INCBankCashMachine
    {
        _itemSlots.RemoveItemSlot(ent, ent.Comp.CashSlot);
    }
}
