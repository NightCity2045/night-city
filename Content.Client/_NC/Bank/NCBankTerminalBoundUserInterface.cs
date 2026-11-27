// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared._NC.Bank;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Bank;

public sealed class NCBankTerminalBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private NCBankTerminalWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<NCBankTerminalWindow>();
        _window.OnDeposit += () => SendMessage(new NCBankDepositMessage());
        _window.OnWithdraw += amount => SendMessage(new NCBankWithdrawMessage(amount));
        _window.OnSetSalary += (job, amount) => SendMessage(new NCBankSetSalaryMessage(job, amount));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is NCBankTerminalUiState terminalState)
            _window?.UpdateState(terminalState);
    }
}
