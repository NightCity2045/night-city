// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.AstraGraph;
using Robust.Shared.GameObjects;

namespace Content.Client._NC.AstraGraph;

/// <summary>
/// Bound user interface backed by an HTML document from Studio.
/// </summary>
public sealed class AstraHtmlBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private AstraHtmlGameWindow? _window;

    protected override void Open()
    {
        base.Open();
        if (!EntMan.TryGetComponent(Owner, out AstraHtmlUiComponent? ui))
            return;

        if (!AstraHtmlDocuments.TryLoad(ui.Document, out var page, out var title, out var width, out var height))
            return;

        _window = new AstraHtmlGameWindow();
        _window.Open(title, page, width, height, (action, payload) =>
            SendMessage(AstraHtmlBui.Action(action, payload)));
        if (State != null && AstraHtmlBui.TryReadValues(State, out var values))
            _window.ApplyState(values);
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (_window != null && AstraHtmlBui.TryReadValues(state, out var values))
            _window.ApplyState(values);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _window?.Close();
        _window = null;
    }
}
