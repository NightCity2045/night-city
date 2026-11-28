// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared._NC.Police.UI;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Police;

public sealed class NCPoliceMonitorBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private NCPoliceMonitorWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<NCPoliceMonitorWindow>();
        _window.SetSource(EntMan.GetComponentOrNull<TransformComponent>(Owner)?.GridUid);
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        base.ReceiveMessage(message);
        switch (message)
        {
            case NCPoliceMonitorMapMessage map:
                _window?.SetSource(EntMan.GetComponentOrNull<TransformComponent>(Owner)?.GridUid);
                _window?.ApplyMap(map);
                break;
            case NCPoliceMonitorContactsMessage contacts:
                _window?.ApplyContacts(contacts);
                break;
        }
    }
}
