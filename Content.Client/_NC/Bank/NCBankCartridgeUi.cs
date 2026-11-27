// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Client.UserInterface.Fragments;
using Content.Shared._NC.Bank;
using Content.Shared.CartridgeLoader;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Bank;

public sealed partial class NCBankCartridgeUi : UIFragment
{
    private NCBankCartridgeUiFragment? _fragment;

    public override Control GetUIFragmentRoot()
    {
        return _fragment!;
    }

    public override void Setup(BoundUserInterface userInterface, EntityUid? fragmentOwner)
    {
        _fragment = new NCBankCartridgeUiFragment();
        _fragment.OnTransfer += (target, amount) =>
            userInterface.SendMessage(new CartridgeUiMessage(new NCBankCartridgeTransferMessage(target, amount)));
    }

    public override void UpdateState(BoundUserInterfaceState state)
    {
        if (state is NCBankCartridgeUiState bankState)
            _fragment?.UpdateState(bankState);
    }
}
