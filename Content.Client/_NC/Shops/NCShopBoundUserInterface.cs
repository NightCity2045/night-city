// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared._NC.Shops;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Shops;

public sealed class NCShopBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private NCShopWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<NCShopWindow>();
        _window.OnBuy += index => SendMessage(new NCShopBuyMessage(index));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is NCShopUiState shop)
            _window?.UpdateState(shop);
    }
}
