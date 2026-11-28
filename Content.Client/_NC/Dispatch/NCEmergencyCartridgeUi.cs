// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Client.UserInterface.Fragments;
using Content.Shared._NC.Dispatch;
using Content.Shared.CartridgeLoader;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Dispatch;

public sealed partial class NCEmergencyCartridgeUi : UIFragment
{
    private NCEmergencyCartridgeUiFragment? _fragment;

    public override Control GetUIFragmentRoot()
    {
        return _fragment!;
    }

    public override void Setup(BoundUserInterface userInterface, EntityUid? fragmentOwner)
    {
        _fragment = new NCEmergencyCartridgeUiFragment();
        _fragment.OnCall += (service, text) =>
            userInterface.SendMessage(new CartridgeUiMessage(new NCEmergencyCallMessage(service, text)));
        _fragment.OnCancel += service =>
            userInterface.SendMessage(new CartridgeUiMessage(new NCEmergencyCancelMessage(service)));
        _fragment.OnSubscribe += plan =>
            userInterface.SendMessage(new CartridgeUiMessage(new NCTraumaSubscribeMessage(plan)));
        _fragment.OnUnsubscribe += () =>
            userInterface.SendMessage(new CartridgeUiMessage(new NCTraumaUnsubscribeMessage()));
    }

    public override void UpdateState(BoundUserInterfaceState state)
    {
        if (state is NCEmergencyCartridgeUiState emergency)
            _fragment?.UpdateState(emergency);
    }
}
