// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared.Containers.ItemSlots;
using Content.Shared.Whitelist;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Bank;

/// <summary>A cash slot for bank machines; cash put in it is deposited from the machine UI.</summary>
public interface INCBankCashMachine
{
    ItemSlot CashSlot { get; }
}

/// <summary>An ATM: deposits and withdraws cash on the user's own account.</summary>
[RegisterComponent]
public sealed partial class NCAtmComponent : Component, INCBankCashMachine
{
    public const string CashSlotId = "nc_bank_cash";

    [DataField]
    public ItemSlot CashSlot { get; set; } = new()
    {
        Name = "nc-bank-cash-slot-name",
        Whitelist = new EntityWhitelist { Components = ["Cash"] },
    };
}

/// <summary>A department terminal: the department manager handles the department account and salaries.</summary>
[RegisterComponent]
public sealed partial class NCBankTerminalComponent : Component, INCBankCashMachine
{
    [DataField(required: true)]
    public ProtoId<NCBankDepartmentPrototype> Department;

    [DataField]
    public ItemSlot CashSlot { get; set; } = new()
    {
        Name = "nc-bank-cash-slot-name",
        Whitelist = new EntityWhitelist { Components = ["Cash"] },
    };
}

/// <summary>A PDA bank program: balance, history and transfers from the holder's own account.</summary>
[RegisterComponent]
public sealed partial class NCBankCartridgeComponent : Component;
