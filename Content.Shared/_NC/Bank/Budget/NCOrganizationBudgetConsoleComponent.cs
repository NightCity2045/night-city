// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Containers.ItemSlots;
using Content.Shared.Roles;
using Content.Shared.Whitelist;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Bank.Budget;

/// <summary>
/// Data-only configuration for a reusable persistent organization budget console.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class NCOrganizationBudgetConsoleComponent : Component
{
    public const string CashSlotId = "nc_organization_budget_cash_slot";

    [DataField(required: true)]
    public ProtoId<DepartmentPrototype> Department;

    [DataField]
    public int MaximumTransaction = 100000;

    [DataField]
    public int HistoryLimit = 100;

    [DataField]
    public uint MaximumReasonLength = 512;

    [DataField]
    public ItemSlot CashSlot = new()
    {
        Name = "nc-budget-cash-slot-name",
        Whitelist = new EntityWhitelist
        {
            Components = new[] { "Stack" },
        },
    };
}
