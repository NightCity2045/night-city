// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Agent;
using Robust.Client.Animations;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Animations;
using Robust.Shared.Timing;

namespace Content.Client._NC.Agent.Notifications;

/// <summary>
/// Full-screen pass-through layer positioning compact Agent cards at the top center of the HUD.
/// </summary>
public sealed class NCAgentNotificationOverlay : Control
{
    public readonly BoxContainer ToastContainer;

    public NCAgentNotificationOverlay()
    {
        MouseFilter = MouseFilterMode.Ignore;

        ToastContainer = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            MinWidth = 360,
            MaxWidth = 440,
            HorizontalAlignment = HAlignment.Center,
            MouseFilter = MouseFilterMode.Ignore,
        };

        var center = new CenterContainer
        {
            HorizontalExpand = true,
            Margin = new Thickness(0, 18, 0, 0),
            MouseFilter = MouseFilterMode.Ignore,
        };
        center.AddChild(ToastContainer);
        AddChild(center);
        LayoutContainer.SetAnchorPreset(center, LayoutContainer.LayoutPreset.TopWide);
    }
}

/// <summary>
/// Cyberpunk-styled notification card with a short signal-acquisition entrance and fade-out.
/// </summary>
public sealed class NCAgentNotificationToast : PanelContainer
{
    private static readonly Animation EnterAnimation = new()
    {
        Length = TimeSpan.FromSeconds(0.22),
        AnimationTracks =
        {
            new AnimationTrackControlProperty
            {
                Property = nameof(Modulate),
                InterpolationMode = AnimationInterpolationMode.Linear,
                KeyFrames =
                {
                    new AnimationTrackProperty.KeyFrame(Color.Transparent, 0f),
                    new AnimationTrackProperty.KeyFrame(Color.FromHex("#63FFF0AA"), 0.09f),
                    new AnimationTrackProperty.KeyFrame(Color.White, 0.22f),
                },
            },
        },
    };

    private static readonly Animation ExitAnimation = new()
    {
        Length = TimeSpan.FromSeconds(0.18),
        AnimationTracks =
        {
            new AnimationTrackControlProperty
            {
                Property = nameof(Modulate),
                InterpolationMode = AnimationInterpolationMode.Linear,
                KeyFrames =
                {
                    new AnimationTrackProperty.KeyFrame(Color.White, 0f),
                    new AnimationTrackProperty.KeyFrame(Color.Transparent, 0.18f),
                },
            },
        },
    };

    private bool _dismissing;

    public event Action? Opened;
    public event Action? Accepted;
    public event Action? Declined;
    public event Action? Dismissed;

    public NCAgentNotificationToast(NCAgentNotificationEvent message)
    {
        HorizontalExpand = true;
        Margin = new Thickness(0, 0, 0, 6);
        MouseFilter = MouseFilterMode.Stop;
        PanelOverride = new StyleBoxFlat
        {
            BackgroundColor = Color.FromHex("#08171EEE"),
            BorderColor = Accent(message.Kind),
            BorderThickness = new Thickness(2, 1, 1, 1),
            ContentMarginLeftOverride = 12,
            ContentMarginRightOverride = 10,
            ContentMarginTopOverride = 8,
            ContentMarginBottomOverride = 8,
        };

        var content = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
        };
        var header = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
        };
        header.AddChild(new Label
        {
            Text = $"AGENT // {message.Title}",
            FontColorOverride = Accent(message.Kind),
            HorizontalExpand = true,
        });
        header.AddChild(new Label { Text = "● LIVE", FontColorOverride = Color.FromHex("#76FF95") });
        content.AddChild(header);
        content.AddChild(new Label
        {
            Text = message.Detail,
            FontColorOverride = Color.FromHex("#C9F5F0"),
            HorizontalExpand = true,
        });

        var actions = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalAlignment = HAlignment.Right,
        };
        if (message.Actionable)
        {
            var decline = CreateButton("DECLINE", "#FF7897");
            decline.OnPressed += _ => Declined?.Invoke();
            var accept = CreateButton("ACCEPT", "#76FF95");
            accept.OnPressed += _ => Accepted?.Invoke();
            actions.AddChild(decline);
            actions.AddChild(accept);
        }
        else
        {
            var open = CreateButton("OPEN", "#6BFFF1");
            open.OnPressed += _ => Opened?.Invoke();
            actions.AddChild(open);
        }

        content.AddChild(actions);
        AddChild(content);
    }

    public void PlayEnter()
    {
        PlayAnimation(EnterAnimation, "nc-agent-toast-enter");
    }

    public void BeginDismiss()
    {
        if (_dismissing || Parent == null)
            return;

        _dismissing = true;
        PlayAnimation(ExitAnimation, "nc-agent-toast-exit");
        Timer.Spawn(ExitAnimation.Length, () => Dismissed?.Invoke());
    }

    private static Button CreateButton(string text, string color)
    {
        var button = new Button
        {
            Text = text,
            MinWidth = 76,
            Margin = new Thickness(4, 5, 0, 0),
        };
        button.Label.FontColorOverride = Color.FromHex(color);
        return button;
    }

    private static Color Accent(NCAgentNotificationKind kind)
    {
        return kind switch
        {
            NCAgentNotificationKind.IncomingCall or NCAgentNotificationKind.MissedCall => Color.FromHex("#FFE28D"),
            NCAgentNotificationKind.GroupMessage or NCAgentNotificationKind.GroupInvite => Color.FromHex("#B79BFF"),
            NCAgentNotificationKind.ContactRequest => Color.FromHex("#76FF95"),
            NCAgentNotificationKind.CitiNetComment or NCAgentNotificationKind.CitiNetMention => Color.FromHex("#E05CFF"),
            NCAgentNotificationKind.System => Color.FromHex("#FF7897"),
            _ => Color.FromHex("#6BFFF1"),
        };
    }
}
