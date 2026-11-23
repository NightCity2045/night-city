// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Meta.UI;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Meta.UI;

public sealed class NCMetaWorkspaceBoundUserInterface(EntityUid owner, Enum key) : BoundUserInterface(owner, key)
{
    private NCMetaWorkspaceWindow? _window;
    private bool _shown;

    protected override void Open()
    {
        base.Open();
        // Created but not shown: an empty frame would flash until the server answers.
        _window = this.CreateDisposableControl<NCMetaWorkspaceWindow>();
        _window.OnClose += Close;
        _window.Requested += SendMessage;
        _window.AuthoringRequested += SendMessage;
        SendMessage(new NCMetaWorkspaceRequestMessage(NCMetaWorkspaceOperation.Open, Guid.NewGuid()));
        // If the server stays silent the window appears anyway, so the player is never left without feedback.
        Robust.Shared.Timing.Timer.Spawn(TimeSpan.FromSeconds(2), ShowWindow);
    }

    private void ShowWindow()
    {
        if (_shown || _window is not { Disposed: false }) return;
        _shown = true;
        if (EntMan.System<UserInterfaceSystem>().TryGetPosition(Owner, UiKey, out var position))
            _window.Open(position);
        else
            _window.OpenCentered();
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        base.ReceiveMessage(message);
        if (message is NCMetaWorkspaceUpdateMessage update)
        {
            _window?.ApplyState(update.State);
            ShowWindow();
        }
        if (message is NCMetaAuthoringReplyMessage authoring)
            _window?.ApplyAuthoringReply(authoring);
    }
}
