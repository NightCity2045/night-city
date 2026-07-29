// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.Shared._NC.Economy.Components;
using Robust.Client.GameObjects;

namespace Content.Client._NC.Economy;

public sealed class NCAtmBoundUserInterface : BoundUserInterface
{
    private NCAtmWindow? _window;

    public NCAtmBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();
        _window = new NCAtmWindow(amount =>
            SendMessage(new NCAtmWithdrawMessage(amount, Guid.NewGuid())));
        _window.OpenCentered();
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is NCAtmUiState atmState)
            _window?.UpdateState(atmState);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _window?.Dispose();
    }
}
