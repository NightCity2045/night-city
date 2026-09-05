// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Police.NCPD;
using Robust.Client.GameObjects;

namespace Content.Client._NC.LivingCity.Police.NCPD;

public sealed partial class NCNcpdOperationsConsoleBoundUserInterface(EntityUid owner, Enum uiKey)
    : BoundUserInterface(owner, uiKey)
{
    private NCNcpdOperationsConsoleWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = new NCNcpdOperationsConsoleWindow();
        _window.OnIssue += (unit, kind, zone, priority, reason) =>
            SendMessage(new NCNcpdOperationsIssueMessage(unit, kind, zone, priority, reason));
        _window.OnCancel += (command, reason) => SendMessage(new NCNcpdOperationsCancelMessage(command, reason));
        _window.OnRefresh += () => SendMessage(new NCNcpdOperationsRefreshMessage());
        _window.OpenCentered();
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        base.ReceiveMessage(message);
        if (message is NCNcpdOperationsUpdateMessage update)
            _window?.UpdateState(update);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _window?.Dispose();
    }
}
