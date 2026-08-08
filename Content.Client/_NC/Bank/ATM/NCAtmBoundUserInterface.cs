// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Bank.ATM;
using Robust.Client.GameObjects;

namespace Content.Client._NC.Bank.ATM;

public sealed class NCAtmBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private NCAtmWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = new NCAtmWindow();
        _window.OnWithdraw += amount => SendMessage(new NCAtmWithdrawMessage(amount));
        _window.OnDeposit += () => SendMessage(new NCAtmDepositMessage());
        _window.OnLogin += (account, pin) => SendMessage(new NCAtmLoginMessage(account, pin));
        _window.OnLogout += () => SendMessage(new NCAtmLogoutMessage());
        _window.OnPayFine += fineId => SendMessage(new NCAtmPayFineMessage(fineId));
        _window.OpenCentered();
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is NCAtmBoundUserInterfaceState atmState)
            _window?.UpdateState(atmState);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _window?.Dispose();
    }
}
