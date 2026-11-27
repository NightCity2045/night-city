// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Client.Eui;
using Content.Shared._NC.Bank;
using Content.Shared.Eui;

namespace Content.Client._NC.Bank;

public sealed class NCBankAdminEui : BaseEui
{
    private readonly NCBankAdminWindow _window;

    public NCBankAdminEui()
    {
        _window = new NCBankAdminWindow();
        _window.OnClose += () => SendMessage(new CloseEuiMessage());
        _window.OnLookup += number => SendMessage(new NCBankAdminEuiMsg.Lookup(number));
        _window.OnAdjust += (number, amount, reason) => SendMessage(new NCBankAdminEuiMsg.Adjust(number, amount, reason));
        _window.OnSetFee += (category, basisPoints) => SendMessage(new NCBankAdminEuiMsg.SetFee(category, basisPoints));
    }

    public override void Opened()
    {
        _window.OpenCentered();
    }

    public override void Closed()
    {
        _window.Close();
    }

    public override void HandleState(EuiStateBase state)
    {
        if (state is NCBankAdminEuiState bankState)
            _window.UpdateState(bankState);
    }
}
