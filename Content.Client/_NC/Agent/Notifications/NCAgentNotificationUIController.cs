// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client.Gameplay;
using Content.Shared._NC.Agent;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controllers;
using Robust.Client.UserInterface.Controllers.Implementations;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Client._NC.Agent.Notifications;

/// <summary>
/// Keeps a bounded, non-modal notification stack above the game screen.
/// </summary>
[UsedImplicitly]
public sealed partial class NCAgentNotificationUIController : UIController,
    IOnStateEntered<GameplayState>,
    IOnStateExited<GameplayState>
{
    private static readonly ProtoId<NCAgentConfigurationPrototype> DefaultConfiguration = "Default";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [UISystemDependency] private readonly NCAgentNotificationSystem _system = default!;

    private NCAgentNotificationOverlay? _overlay;

    public void OnStateEntered(GameplayState state)
    {
        _overlay = new NCAgentNotificationOverlay();
        UIManager.RootControl.AddChild(_overlay);
        LayoutContainer.SetAnchorPreset(_overlay, LayoutContainer.LayoutPreset.Wide);
    }

    public void OnStateExited(GameplayState state)
    {
        _overlay?.Orphan();
        _overlay = null;
    }

    public void Push(NCAgentNotificationEvent message)
    {
        if (_overlay == null)
            return;

        var config = _prototypes.Index(DefaultConfiguration);
        while (_overlay.ToastContainer.ChildCount >= config.NotificationOverlayLimit)
            _overlay.ToastContainer.Children[0].Orphan();

        var toast = new NCAgentNotificationToast(message);
        toast.Opened += () =>
        {
            _system.SendAction(message.NotificationId, NCAgentNotificationAction.Open);
            toast.BeginDismiss();
        };
        toast.Accepted += () =>
        {
            _system.SendAction(message.NotificationId, NCAgentNotificationAction.Accept);
            toast.BeginDismiss();
        };
        toast.Declined += () =>
        {
            _system.SendAction(message.NotificationId, NCAgentNotificationAction.Decline);
            toast.BeginDismiss();
        };
        toast.Dismissed += toast.Orphan;
        _overlay.ToastContainer.AddChild(toast);
        toast.PlayEnter();

        var fadeDelay = config.NotificationOverlayDuration - TimeSpan.FromSeconds(0.2);
        Timer.Spawn(fadeDelay > TimeSpan.Zero ? fadeDelay : TimeSpan.Zero, toast.BeginDismiss);
    }
}
