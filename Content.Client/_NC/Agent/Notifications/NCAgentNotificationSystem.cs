// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Agent;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Agent.Notifications;

/// <summary>
/// Receives only server-authorized Agent notifications and forwards opaque actions back to the server.
/// </summary>
public sealed partial class NCAgentNotificationSystem : EntitySystem
{
    [Dependency] private IUserInterfaceManager _ui = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<NCAgentNotificationEvent>(OnNotification);
    }

    public void SendAction(Guid notificationId, NCAgentNotificationAction action)
    {
        RaiseNetworkEvent(new NCAgentNotificationActionEvent(notificationId, action));
    }

    private void OnNotification(NCAgentNotificationEvent message)
    {
        _ui.GetUIController<NCAgentNotificationUIController>().Push(message);
    }
}
