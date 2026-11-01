// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Meta.UI;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Meta.UI;

public sealed class NCMetaApplicationBoundUserInterface : BoundUserInterface
{
    private NCMetaApplicationWindow? _window;

    public NCMetaApplicationBoundUserInterface(EntityUid owner, Enum key) : base(owner, key)
    {
    }

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<NCMetaApplicationWindow>();
        _window.Requested += SendMessage;
        // The first private update may precede replication of the open BUI subscription.
        SendMessage(new NCMetaAppResyncMessage(Guid.Empty, -1));
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        base.ReceiveMessage(message);
        if (message is NCMetaAppUpdateMessage update)
        {
            _window?.ApplyState(update.State);
        }
        else if (message is NCMetaAppActionResultMessage result)
            _window?.ApplyResult(result);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _window?.SessionId is { } session && session != Guid.Empty)
            SendMessage(new NCMetaAppCloseMessage(session));
        base.Dispose(disposing);
    }
}
