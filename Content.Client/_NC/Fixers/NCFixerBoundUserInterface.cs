// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared._NC.Fixers;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Fixers;

public sealed class NCFixerBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private NCFixerWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<NCFixerWindow>();
        _window.OnAccept += slot => SendMessage(new NCFixerAcceptMessage(slot));
        _window.OnAbandon += () => SendMessage(new NCFixerAbandonMessage());
        _window.OnCollect += () => SendMessage(new NCFixerCollectPaymentMessage());
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is NCFixerUiState fixerState)
            _window?.UpdateState(fixerState);
    }
}
