// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using System.Numerics;
using Content.Shared.GameTicking;
using Content.Shared._NC.Meta.UI;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Input;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.IoC;
using Robust.Shared.Maths;

namespace Content.Client._NC.Meta.UI;

/// <summary>
/// The computer's desktop: wallpaper, application icons, a system card, a taskbar with a start menu and a clock.
/// It only dresses the existing operations; opening an application is the same request as before.
/// </summary>
public sealed partial class NCMetaWorkspaceWindow
{
    [Dependency] private IEntitySystemManager _desktopSystems = default!;

    private static readonly Color IconIdle = Color.FromHex("#00000000");
    private static readonly Color IconHover = Color.FromHex("#31E6FF30");
    private static readonly Color IconPressed = Color.FromHex("#31E6FF58");

    private bool _desktopAvailable;
    private Popup? _startMenu;
    private float _clockElapsed = 1;
    private Texture[]? _rainFrames;
    private float _rainElapsed;
    private int _rainFrame;
    private const int RainFrameCount = 8;
    private const float RainFrameSeconds = 1f / 14f;

    private void InitializeDesktop()
    {
        WireIcon(DesktopStudio, DesktopStudioFrame);
        WireIcon(DesktopNetwork, DesktopNetworkFrame);
        WireIcon(DesktopCitiNet, DesktopCitiNetFrame);
        WireIcon(StartButton, StartButtonFrame);
        DesktopStudio.OnPressed += _ => OpenStudioApplication();
        DesktopNetwork.OnPressed += _ => OpenLocalNetworkApplication();
        // The CitiNet browser is not available on computers yet; its icon is shown but does nothing.
        DesktopCitiNet.ToolTip = Loc.GetString("nc-meta-desktop-citinet-soon");
        DesktopCitiNet.Modulate = Color.FromHex("#FFFFFF90");
        TaskDesktop.OnPressed += _ => { SetDesktop(true); UpdateTaskbar(); };
        TaskStudio.OnPressed += _ => OpenStudioApplication();
        TaskNetwork.OnPressed += _ => OpenLocalNetworkApplication();
        StartButton.OnPressed += _ => ToggleStartMenu();
        var cache = IoCManager.Resolve<IResourceCache>();
        _rainFrames = Enumerable.Range(0, RainFrameCount)
            .Select(index => cache.GetResource<TextureResource>("/Textures/_NC/Interface/Desktop/rain-" + index + ".png").Texture).ToArray();
    }

    /// <summary>The rain on the wallpaper: eight frames that loop without a seam, only while the desktop is on screen.</summary>
    private void TickDesktopRain(float seconds)
    {
        if (_rainFrames == null || !DesktopPanel.Visible || !DesktopPanel.VisibleInTree) return;
        _rainElapsed += seconds;
        if (_rainElapsed < RainFrameSeconds) return;
        _rainElapsed -= RainFrameSeconds;
        _rainFrame = (_rainFrame + 1) % RainFrameCount;
        DesktopRain.Texture = _rainFrames[_rainFrame];
    }

    private static void WireIcon(ContainerButton button, PanelContainer frame)
    {
        void Paint(Color color) => frame.PanelOverride = new StyleBoxFlat
        {
            BackgroundColor = color,
            BorderColor = color.A > 0.1f ? Color.FromHex("#31E6FF") : Color.Transparent,
            BorderThickness = new Thickness(color.A > 0.1f ? 1 : 0),
        };
        Paint(IconIdle);
        button.OnMouseEntered += _ => Paint(IconHover);
        button.OnMouseExited += _ => Paint(IconIdle);
        button.OnKeyBindDown += args =>
        {
            if (args.Function == EngineKeyFunctions.UIClick) Paint(IconPressed);
        };
        button.OnKeyBindUp += _ => Paint(button.IsHovered ? IconHover : IconIdle);
    }

    private void OpenStudioApplication()
    {
        CloseStartMenu();
        SetDesktop(false);
        Request(NCMetaWorkspaceOperation.Open);
        UpdateTaskbar();
    }

    private void OpenLocalNetworkApplication()
    {
        CloseStartMenu();
        SetDesktop(false);
        Request(NCMetaWorkspaceOperation.OpenNativeTools, "network");
        UpdateTaskbar();
    }

    /// <summary>A computer has a desktop and a taskbar; a server shows its service panel directly.</summary>
    private void SetDesktopAvailable(bool available)
    {
        _desktopAvailable = available;
        Taskbar.Visible = available;
        UpdateTaskbar();
    }

    private void UpdateTaskbar()
    {
        if (!_desktopAvailable) return;
        TaskDesktop.Pressed = DesktopPanel.Visible;
        TaskStudio.Pressed = !DesktopPanel.Visible && !_nativeToolsMode;
        TaskNetwork.Pressed = !DesktopPanel.Visible && _nativeToolsMode;
        Title = Loc.GetString(DesktopPanel.Visible ? "nc-meta-start-title" : _nativeToolsMode ? "nc-meta-tools-title" : "nc-meta-workspace-title");
    }

    private void ToggleStartMenu()
    {
        if (_startMenu is { Visible: true })
        {
            CloseStartMenu();
            return;
        }

        _startMenu ??= BuildStartMenu();
        if (Root == null) return;
        _startMenu.Measure(Window?.Size ?? Vector2Helpers.Infinity);
        var size = _startMenu.DesiredSize;
        var origin = StartButton.GlobalPosition;
        origin.Y -= size.Y + 2;
        Root.ModalRoot.AddChild(_startMenu);
        _startMenu.Open(UIBox2.FromDimensions(origin, size));
    }

    private void CloseStartMenu() => _startMenu?.Close();

    private Popup BuildStartMenu()
    {
        var popup = new Popup();
        popup.OnPopupHide += () => popup.Orphan();
        var panel = new PanelContainer
        {
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = Color.FromHex("#0E1524F4"),
                BorderColor = Color.FromHex("#A3171E"),
                BorderThickness = new Thickness(1, 1, 1, 2),
                ContentMarginLeftOverride = 8,
                ContentMarginRightOverride = 8,
                ContentMarginTopOverride = 8,
                ContentMarginBottomOverride = 8,
            },
        };
        var box = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = 3, MinWidth = 248 };
        box.AddChild(new Label { Text = Loc.GetString("nc-meta-start-title"), FontColorOverride = Color.FromHex("#FF4A4A") });
        box.AddChild(new PanelContainer { MinHeight = 2, PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex("#1F4A6B") } });
        box.AddChild(MenuItem("nc-meta-desktop-studio", OpenStudioApplication));
        box.AddChild(MenuItem("nc-meta-desktop-network", OpenLocalNetworkApplication));
        var citinet = MenuItem("nc-meta-desktop-citinet", () => { });
        citinet.Disabled = true;
        citinet.ToolTip = Loc.GetString("nc-meta-desktop-citinet-soon");
        box.AddChild(citinet);
        box.AddChild(new PanelContainer { MinHeight = 2, PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex("#1F4A6B") } });
        box.AddChild(MenuItem("nc-meta-start-show-desktop", () => { CloseStartMenu(); SetDesktop(true); UpdateTaskbar(); }));
        box.AddChild(MenuItem("nc-meta-start-shutdown", () => { CloseStartMenu(); Close(); }));
        panel.AddChild(box);
        popup.AddChild(panel);
        return popup;
    }

    private static Button MenuItem(string key, Action action)
    {
        var button = new Button { Text = Loc.GetString(key), TextAlign = Label.AlignMode.Left, HorizontalExpand = true };
        button.OnPressed += _ => action();
        return button;
    }

    /// <summary>The system card and the taskbar clock. The world has its own time, so the clock follows the round.</summary>
    private void UpdateDesktop()
    {
        var device = _state.Devices.Take(64).FirstOrDefault(candidate => candidate.DeviceId == _selectedDeviceId);
        DesktopDeviceName.Text = Loc.GetString("nc-meta-desktop-device", ("name", device?.Name ?? "-"));
        DesktopDeviceId.Text = Loc.GetString("nc-meta-desktop-device-id", ("id", ShortDeviceId(_selectedDeviceId)));
        DesktopNetworkInfo.Text = Loc.GetString("nc-meta-desktop-net-info", ("count", Math.Max(0, _state.Devices.Count - 1)));
        DesktopProgramsInfo.Text = Loc.GetString("nc-meta-desktop-programs-info",
            ("count", _state.Installations.Count(installation => installation.DeviceId == _selectedDeviceId)));
        TickDesktopClock(1);
    }

    private void TickDesktopClock(float seconds)
    {
        if (!_desktopAvailable) return;
        _clockElapsed += seconds;
        if (_clockElapsed < 1) return;
        _clockElapsed = 0;
        var round = _desktopSystems.GetEntitySystem<SharedGameTicker>().RoundDuration();
        var clock = TimeSpan.FromHours(8) + round;
        ClockTime.Text = $"{clock.Hours:00}:{clock.Minutes:00}";
        ClockDate.Text = "2045.11.13";
        DesktopUptime.Text = Loc.GetString("nc-meta-desktop-uptime-info", ("hours", (int) round.TotalHours), ("minutes", round.Minutes));
    }
}
