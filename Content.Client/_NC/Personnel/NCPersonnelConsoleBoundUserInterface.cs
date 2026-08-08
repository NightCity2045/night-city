// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Personnel.UI;
using Robust.Client.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.Personnel;

public sealed partial class NCPersonnelConsoleBoundUserInterface(EntityUid owner, Enum uiKey)
    : BoundUserInterface(owner, uiKey)
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    private NCPersonnelConsoleWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = new NCPersonnelConsoleWindow(_prototypes);
        _window.OnSearch += query => SendMessage(new NCPersonnelSearchMessage(query));
        _window.OnSelectCharacter += characterId => SendMessage(new NCPersonnelSelectCharacterMessage(characterId));
        _window.OnHire += (characterId, jobId, reason) =>
            SendMessage(new NCPersonnelHireMessage(characterId, jobId, reason));
        _window.OnTerminate += (characterId, reason) =>
            SendMessage(new NCPersonnelTerminateMessage(characterId, reason));
        _window.OnChangePosition += (characterId, jobId, reason) =>
            SendMessage(new NCPersonnelChangePositionMessage(characterId, jobId, reason));
        _window.OpenCentered();
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        base.ReceiveMessage(message);
        if (message is NCPersonnelConsoleUpdateMessage update)
            _window?.UpdateState(update);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _window?.Dispose();
    }
}
