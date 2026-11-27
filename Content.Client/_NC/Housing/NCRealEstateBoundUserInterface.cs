// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared._NC.Housing;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Housing;

public sealed class NCRealEstateBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private NCRealEstateWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<NCRealEstateWindow>();
        _window.OnRent += (property, hours) => SendMessage(new NCRealEstateRentMessage(property, hours));
        _window.OnExtend += (property, hours) => SendMessage(new NCRealEstateExtendMessage(property, hours));
        _window.OnCancel += property => SendMessage(new NCRealEstateCancelMessage(property));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is NCRealEstateUiState realEstate)
            _window?.UpdateState(realEstate);
    }
}
