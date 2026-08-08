// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Containers.ItemSlots;
using Content.Shared.Whitelist;
using Robust.Shared.GameStates;

namespace Content.Shared._NC.Bank.Components;

/// <summary>
/// Data for a powered terminal that exchanges physical credits with a character-owned account.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class NCAtmComponent : Component
{
    public const string CashSlotId = "nc_atm_cash_slot";

    [DataField]
    public float TaxRate = 0.1f;

    [DataField]
    public int MaximumTransaction = 100000;

    [DataField]
    public ItemSlot CashSlot = new()
    {
        Name = "nc-atm-cash-slot-name",
        Whitelist = new EntityWhitelist
        {
            Components = new[] { "Stack" },
        },
    };
}
