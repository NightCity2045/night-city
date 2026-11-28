// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared._NC.Medical.Cyberware.AutoImplanter;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Medical.Cyberware.AutoImplanter;

public sealed class NCAutoImplanterBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private NCAutoImplanterWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<NCAutoImplanterWindow>();
        _window.OnInstall += item => SendMessage(new NCAutoImplanterInstallMessage(item));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is NCAutoImplanterUiState implanter)
            _window?.UpdateState(implanter);
    }
}
