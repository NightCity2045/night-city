// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Housing.UI;
using Robust.Client.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.Housing;

public sealed class NCPropertyConsoleBoundUserInterface(EntityUid owner, Enum uiKey)
    : BoundUserInterface(owner, uiKey)
{
    [Dependency] private IPrototypeManager _prototypes = default!;

    private NCPropertyConsoleWindow? _window;

    protected override void Open()
    {
        base.Open();

        _window = new NCPropertyConsoleWindow(_prototypes);
        _window.OnSearch += query => SendMessage(new NCPropertyConsoleSearchMessage(query));
        _window.OnPage += page => SendMessage(new NCPropertyConsolePageMessage(page));
        _window.OnSelect += listing => SendMessage(new NCPropertyConsoleSelectMessage(listing));
        _window.OnRent += (listing, template) =>
            SendMessage(new NCPropertyConsoleRentMessage(listing, template));
        _window.OnEndLease += listing => SendMessage(new NCPropertyConsoleEndLeaseMessage(listing));
        _window.OnInvokeInterior += listing =>
            SendMessage(new NCPropertyConsoleInvokeInteriorMessage(listing));
        _window.OpenCentered();
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        base.ReceiveMessage(message);
        if (message is NCPropertyConsoleUpdateMessage update)
            _window?.UpdateState(update);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _window?.Dispose();
    }
}
