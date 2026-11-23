// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Meta.UI;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Meta.UI;

/// <summary>
/// The windows of programs that run on a computer. A program gets a window of its own; a page of a website that a browser
/// program opened is shown inside that browser's window.
/// </summary>
public sealed class NCMetaApplicationBoundUserInterface : BoundUserInterface
{
    private NCMetaApplicationSessionManager? _sessions;

    public NCMetaApplicationBoundUserInterface(EntityUid owner, Enum key) : base(owner, key)
    {
    }

    protected override void Open()
    {
        base.Open();
        _sessions = new NCMetaApplicationSessionManager();
        _sessions.Requested += SendMessage;
        _sessions.Emptied += Close;
        // The first private update may precede replication of the open BUI subscription.
        SendMessage(new NCMetaAppResyncMessage(Guid.Empty, -1));
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        base.ReceiveMessage(message);
        if (message is NCMetaAppUpdateMessage update)
            _sessions?.TryApplyState(update.State);
        else if (message is NCMetaAppActionResultMessage result)
            _sessions?.ApplyResult(result);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _sessions?.Dispose();
        _sessions = null;
        base.Dispose(disposing);
    }
}
