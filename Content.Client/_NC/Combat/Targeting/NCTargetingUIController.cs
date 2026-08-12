// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client.Gameplay;
using Content.Client.UserInterface.Systems.Gameplay;
using Content.Shared._NC.Combat.Targeting;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controllers;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._NC.Combat.Targeting;

/// <summary>
/// Connects the reusable doll presenter to predicted client intent and the private server acknowledgement stream.
/// </summary>
[UsedImplicitly]
public sealed partial class NCTargetingUIController : UIController,
    IOnStateEntered<GameplayState>,
    IOnStateExited<GameplayState>,
    IOnSystemChanged<NCTargetingClientSystem>
{
    [UISystemDependency] private readonly NCTargetingClientSystem? _targeting = default;

    private NCTargetingHudOverlay? _overlay;

    public override void Initialize()
    {
        base.Initialize();
        var gameplayScreen = UIManager.GetUIController<GameplayStateLoadController>();
        gameplayScreen.OnScreenLoad += AttachToViewport;
        gameplayScreen.OnScreenUnload += DetachFromScreen;
    }

    public void OnSystemLoaded(NCTargetingClientSystem system)
    {
        system.TargetingChanged += OnTargetingChanged;
    }

    public void OnSystemUnloaded(NCTargetingClientSystem system)
    {
        system.TargetingChanged -= OnTargetingChanged;
    }

    public void OnStateEntered(GameplayState state)
    {
        _overlay = new NCTargetingHudOverlay();
        _overlay.Doll.TargetingChanged += OnDollChanged;
        AttachToViewport();

        _targeting?.RefreshLocalPlayer();
        SynchronizePresenter();
    }

    public void OnStateExited(GameplayState state)
    {
        if (_overlay == null)
            return;

        _overlay.Doll.TargetingChanged -= OnDollChanged;
        _overlay.Orphan();
        _overlay = null;
    }

    private void AttachToViewport()
    {
        if (_overlay == null)
            return;

        // The separated-chat screen gives the chat its own sibling panel. Parenting the targeting HUD to
        // the gameplay viewport keeps BottomRight relative to the playable area at every chat width.
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
        SynchronizePresenter();
    }

    private void DetachFromScreen()
    {
        _overlay?.Orphan();
    }

    private void OnDollChanged(NCTargetingState requested)
    {
        if (_targeting?.RequestSelection(requested) == false)
            SynchronizePresenter();
    }

    private void OnTargetingChanged()
    {
        SynchronizePresenter();
    }

    private void SynchronizePresenter()
    {
        if (_overlay == null || _targeting?.ActiveProfile is not { } profile)
        {
            if (_overlay != null)
                _overlay.Visible = false;
            return;
        }

        _overlay.Visible = true;
        if (_overlay.Doll.ActiveProfile != profile && !_overlay.Doll.SetProfile(profile))
        {
            _overlay.Visible = false;
            return;
        }

        if (!_overlay.Doll.SetState(_targeting.State))
            _overlay.Doll.SetState(new NCTargetingState(NCTargetingMode.Automatic, null));

        _overlay.Doll.SetStatusLevels(_targeting.StatusLevels);
    }
}
