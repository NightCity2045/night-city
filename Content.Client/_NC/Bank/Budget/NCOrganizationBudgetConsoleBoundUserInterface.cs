// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Bank.Budget;
using Robust.Client.GameObjects;

namespace Content.Client._NC.Bank.Budget;

public sealed class NCOrganizationBudgetConsoleBoundUserInterface(EntityUid owner, Enum uiKey)
    : BoundUserInterface(owner, uiKey)
{
    private NCOrganizationBudgetConsoleWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = new NCOrganizationBudgetConsoleWindow();
        _window.OnDeposit += reason => SendMessage(new NCOrganizationBudgetDepositMessage(reason));
        _window.OnWithdraw += (amount, reason) =>
            SendMessage(new NCOrganizationBudgetWithdrawMessage(amount, reason));
        _window.OpenCentered();
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is NCOrganizationBudgetConsoleState budgetState)
            _window?.UpdateState(budgetState);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _window?.Dispose();
    }
}
