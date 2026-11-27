// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared._NC.Corporations;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Corporations;

public sealed class NCCorpSalesBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private NCCorpSalesWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<NCCorpSalesWindow>();
        _window.OnFulfill += slot => SendMessage(new NCCorpSalesFulfillMessage(slot));
        _window.OnRefresh += () => SendMessage(new NCCorpSalesRefreshMessage());
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is NCCorpSalesUiState salesState)
            _window?.UpdateState(salesState);
    }
}
