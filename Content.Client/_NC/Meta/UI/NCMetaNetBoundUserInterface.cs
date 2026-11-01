// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Meta.Network;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Meta.UI;

public sealed class NCMetaNetBoundUserInterface(EntityUid owner, Enum key) : BoundUserInterface(owner, key)
{
    private NCMetaNetWindow? _window;
    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<NCMetaNetWindow>();
        _window.Requested += SendMessage;
        SendMessage(new NCMetaNetRequest(NCMetaNetOperation.Refresh, Guid.Empty, 0));
    }
    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        base.ReceiveMessage(message);
        if (message is NCMetaNetUpdate update) _window?.ApplyState(update.State);
    }
}
