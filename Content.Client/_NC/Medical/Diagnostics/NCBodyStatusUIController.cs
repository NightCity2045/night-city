// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client.Gameplay;
using Content.Client.UserInterface.Systems.Gameplay;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controllers;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._NC.Medical.Diagnostics;

/// <summary>
/// Mounts the owner-status doll to the gameplay viewport and keeps it synchronized with directed snapshots.
/// </summary>
[UsedImplicitly]
public sealed partial class NCBodyStatusUIController : UIController,
    IOnStateEntered<GameplayState>,
    IOnStateExited<GameplayState>,
    IOnSystemChanged<NCBodyStatusClientSystem>
{
    [UISystemDependency] private readonly NCBodyStatusClientSystem? _status = default;

    private NCBodyStatusHudOverlay? _overlay;

    public override void Initialize()
    {
        base.Initialize();
        var gameplayScreen = UIManager.GetUIController<GameplayStateLoadController>();
        gameplayScreen.OnScreenLoad += AttachToViewport;
        gameplayScreen.OnScreenUnload += DetachFromScreen;
    }

    public void OnSystemLoaded(NCBodyStatusClientSystem system)
    {
        system.StatusChanged += Synchronize;
    }

    public void OnSystemUnloaded(NCBodyStatusClientSystem system)
    {
        system.StatusChanged -= Synchronize;
    }

    public void OnStateEntered(GameplayState state)
    {
        _overlay = new NCBodyStatusHudOverlay();
        AttachToViewport();
        _status?.RefreshLocalPlayer();
        Synchronize();
    }

    public void OnStateExited(GameplayState state)
    {
        _overlay?.Orphan();
        _overlay = null;
    }

    private void AttachToViewport()
    {
        if (_overlay == null)
            return;

        var viewport = UIManager.ActiveScreen?.FindControl<LayoutContainer>("ViewportContainer");
        if (viewport == null)
        {
            _overlay.Orphan();
            _overlay.Visible = false;
            return;
        }

        _overlay.Orphan();
        viewport.AddChild(_overlay);
        LayoutContainer.SetAnchorPreset(_overlay, LayoutContainer.LayoutPreset.Wide);
        Synchronize();
    }

    private void DetachFromScreen()
    {
        _overlay?.Orphan();
    }

    private void Synchronize()
    {
        if (_overlay == null || _status?.ActiveProfile is not { } profile)
        {
            if (_overlay != null)
                _overlay.Visible = false;
            return;
        }

        _overlay.Visible = _overlay.Doll.ActiveProfile == profile || _overlay.Doll.SetProfile(profile);
        if (_overlay.Visible)
            _overlay.Doll.SetZoneStatuses(_status.Statuses);
    }
}
