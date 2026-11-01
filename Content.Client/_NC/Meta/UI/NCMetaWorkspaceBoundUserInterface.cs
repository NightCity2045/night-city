// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Meta.UI;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Meta.UI;

public sealed class NCMetaWorkspaceBoundUserInterface(EntityUid owner, Enum key) : BoundUserInterface(owner, key)
{
    private NCMetaWorkspaceWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<NCMetaWorkspaceWindow>();
        _window.Requested += SendMessage;
        _window.AuthoringRequested += SendMessage;
        SendMessage(new NCMetaWorkspaceRequestMessage(NCMetaWorkspaceOperation.Open, Guid.NewGuid()));
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        base.ReceiveMessage(message);
        if (message is NCMetaWorkspaceUpdateMessage update)
            _window?.ApplyState(update.State);
        if (message is NCMetaAuthoringReplyMessage authoring)
            _window?.ApplyAuthoringReply(authoring);
    }
}
