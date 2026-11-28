// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Client.UserInterface.Fragments;
using Content.Shared._NC.Dispatch;
using Content.Shared.CartridgeLoader;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Dispatch;

public sealed partial class NCDispatchCartridgeUi : UIFragment
{
    private NCDispatchCartridgeUiFragment? _fragment;

    public override Control GetUIFragmentRoot()
    {
        return _fragment!;
    }

    public override void Setup(BoundUserInterface userInterface, EntityUid? fragmentOwner)
    {
        _fragment = new NCDispatchCartridgeUiFragment();
        _fragment.OnAccept += id =>
            userInterface.SendMessage(new CartridgeUiMessage(new NCDispatchCartridgeAcceptMessage(id)));
        _fragment.OnClose += id =>
            userInterface.SendMessage(new CartridgeUiMessage(new NCDispatchCartridgeCloseMessage(id)));
    }

    public override void UpdateState(BoundUserInterfaceState state)
    {
        if (state is NCDispatchCartridgeUiState dispatch)
            _fragment?.UpdateState(dispatch);
    }
}
