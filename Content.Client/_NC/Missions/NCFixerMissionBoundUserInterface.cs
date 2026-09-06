// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Missions.UI;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Missions;

public sealed class NCFixerMissionBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private NCFixerMissionWindow? _window;
    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<NCFixerMissionWindow>();
        _window.WorkControl.OnRequest += SendMessage;
        _window.OpenCentered();
    }
    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        base.ReceiveMessage(message);
        if (message is NCFixerMissionUpdateMessage update) _window?.WorkControl.SetData(update);
    }
}
