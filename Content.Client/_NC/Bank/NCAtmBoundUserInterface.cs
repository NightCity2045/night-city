// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared._NC.Bank;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Bank;

public sealed class NCAtmBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private NCAtmWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<NCAtmWindow>();
        _window.OnDeposit += () => SendMessage(new NCBankDepositMessage());
        _window.OnWithdraw += amount => SendMessage(new NCBankWithdrawMessage(amount));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is NCAtmUiState atmState)
            _window?.UpdateState(atmState);
    }
}
