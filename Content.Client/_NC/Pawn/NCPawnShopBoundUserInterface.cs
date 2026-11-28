// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared._NC.Pawn;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Pawn;

public sealed class NCPawnShopBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private NCPawnShopWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<NCPawnShopWindow>();
        _window.OnSell += item => SendMessage(new NCPawnSellMessage(item));
        _window.OnSellAll += () => SendMessage(new NCPawnSellAllMessage());
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        base.ReceiveMessage(message);
        if (message is NCPawnUpdateMessage update)
            _window?.Update(update);
    }
}
