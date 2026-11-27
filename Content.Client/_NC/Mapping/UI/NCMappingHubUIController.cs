// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Client.Administration.Managers;
using Content.Client.Gameplay;
using Content.Client.Sandbox;
using Content.Client.UserInterface.Systems.DecalPlacer;
using Content.Shared._NC.Coordinates.Components;
using Content.Shared._NC.ZLevels.Core.Components;
using JetBrains.Annotations;
using Robust.Client.Player;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controllers;
using Robust.Client.UserInterface.Controllers.Implementations;

namespace Content.Client._NC.Mapping.UI;

/// <summary>
/// Coordinates existing mapping tools and Z-floor navigation from one admin-facing window.
/// </summary>
[UsedImplicitly]
public sealed partial class NCMappingHubUIController : UIController, IOnStateChanged<GameplayState>
{
    [Dependency] private IClientAdminManager _admins = default!;
    [Dependency] private IPlayerManager _players = default!;

    [UISystemDependency] private readonly SandboxSystem _sandbox = default!;
    [UISystemDependency] private readonly NCMappingHubClientSystem _mapping = default!;

    private readonly List<int> _availableDepths = [];
    private NCMappingHubWindow? _window;
    private int? _selectedDepth;
    private NCMappingHubClientSystem? _subscribedMapping;

    private EntitySpawningUIController EntitySpawningController =>
        UIManager.GetUIController<EntitySpawningUIController>();

    private TileSpawningUIController TileSpawningController =>
        UIManager.GetUIController<TileSpawningUIController>();

    private DecalPlacerUIController DecalPlacerController =>
        UIManager.GetUIController<DecalPlacerUIController>();

    public void OnStateEntered(GameplayState state)
    {
        if (_subscribedMapping != null)
            return;

        EnsureWindow();
        _mapping.FloorChangeCompleted += OnFloorChangeCompleted;
        // UI-system dependencies may be cleared before OnStateExited during a disconnect. Retain the
        // exact system instances used for subscription so teardown can always remove these delegates.
        _subscribedMapping = _mapping;
    }

    public void OnStateExited(GameplayState state)
    {
        // A disconnected integration client may leave GameplayState before UI-system dependencies were
        // injected and OnStateEntered ran. Only touch those systems when this controller subscribed.
        if (_subscribedMapping is { } mapping)
        {
            mapping.FloorChangeCompleted -= OnFloorChangeCompleted;
            _subscribedMapping = null;
        }

        _window?.Close();
        _window = null;
        _availableDepths.Clear();
        _selectedDepth = null;
    }

    public void ToggleWindow()
    {
        if (!_admins.CanAdminPlace())
            return;

        EnsureWindow();
        if (_window!.IsOpen)
        {
            _window.Close();
            return;
        }

        UIManager.ClickSound();
        RefreshFloors();
        _window.OpenCentered();
    }

    private void EnsureWindow()
    {
        if (_window is { Disposed: false })
            return;

        _window = UIManager.CreateWindow<NCMappingHubWindow>();
        BindToolButtons();
        BindFloorButtons();
    }

    private void BindToolButtons()
    {
        _window!.EntitiesButton.OnPressed += _ => EntitySpawningController.ToggleWindow();
        _window.TilesButton.OnPressed += _ => TileSpawningController.ToggleWindow();
        _window.DecalsButton.OnPressed += _ => DecalPlacerController.ToggleWindow();
        _window.SubfloorButton.OnPressed += _ => _sandbox.ToggleSubFloor();
        _window.MarkersButton.OnPressed += _ => _sandbox.ShowMarkers();
        _window.BoundsButton.OnPressed += _ => _sandbox.ShowBb();
        _window.LightButton.OnPressed += _ => _sandbox.ToggleLight();
        _window.FovButton.OnPressed += _ => _sandbox.ToggleFov();
        _window.ShadowsButton.OnPressed += _ => _sandbox.ToggleShadows();
        _window.ThermalButton.OnPressed += _ => _sandbox.ToggleThermalVision();
        _window.AghostButton.OnPressed += _ => _sandbox.GiveAGhost();
        _window.AccessButton.OnPressed += _ => _sandbox.GiveAdminAccess();
        _window.RespawnButton.OnPressed += _ => _sandbox.Respawn();
        _window.SuicideButton.OnPressed += _ => _sandbox.Suicide();
    }

    private void BindFloorButtons()
    {
        _window!.FloorSelector.OnItemSelected += args =>
        {
            args.Button.SelectId(args.Id);
            _selectedDepth = args.Button.SelectedMetadata as int?;
        };
        _window.FloorDownButton.OnPressed += _ => RequestAdjacentFloor(-1);
        _window.FloorUpButton.OnPressed += _ => RequestAdjacentFloor(1);
        _window.GoToFloorButton.OnPressed += _ => RequestSelectedFloor();
        _window.RefreshFloorsButton.OnPressed += _ => RefreshFloors();
    }

    private void RefreshFloors()
    {
        if (_window == null)
            return;

        _availableDepths.Clear();
        _selectedDepth = null;
        _window.FloorSelector.Clear();

        if (_players.LocalEntity is not { } player ||
            EntityManager.GetComponent<TransformComponent>(player).MapUid is not { } map ||
            !EntityManager.TryGetComponent<NCZMapComponent>(map, out var zMap) ||
            !EntityManager.TryGetComponent<NCZMapNetworkComponent>(zMap.NetworkUid, out var network))
        {
            _window.NetworkLabel.Text = Loc.GetString("nc-mapping-hub-network-none");
            _window.CurrentFloorLabel.Text = Loc.GetString("nc-mapping-hub-floor-none");
            SetFloorButtons(false);
            return;
        }

        var networkId = EntityManager.TryGetComponent<NCZNetworkIdentityComponent>(zMap.NetworkUid, out var identity)
            ? identity.NetworkId.ToString()
            : Loc.GetString("nc-mapping-hub-network-runtime");

        _window.NetworkLabel.Text = Loc.GetString(
            "nc-mapping-hub-network",
            ("network", networkId));
        _window.CurrentFloorLabel.Text = Loc.GetString(
            "nc-mapping-hub-floor-current",
            ("floor", zMap.Depth));

        _availableDepths.AddRange(network.ZLevels
            .Where(pair => pair.Value is { } uid && uid.IsValid())
            .Select(pair => pair.Key)
            .Order());

        for (var index = 0; index < _availableDepths.Count; index++)
        {
            var depth = _availableDepths[index];
            _window.FloorSelector.AddItem(
                Loc.GetString("nc-mapping-hub-floor-option", ("floor", depth)),
                index);
            _window.FloorSelector.SetItemMetadata(index, depth);
        }

        var currentIndex = _availableDepths.IndexOf(zMap.Depth);
        if (currentIndex >= 0)
        {
            _window.FloorSelector.SelectId(currentIndex);
            _selectedDepth = zMap.Depth;
        }

        SetFloorButtons(_availableDepths.Count > 0);
    }

    private void SetFloorButtons(bool enabled)
    {
        if (_window == null)
            return;

        _window.FloorSelector.Disabled = !enabled;
        _window.FloorDownButton.Disabled = !enabled;
        _window.FloorUpButton.Disabled = !enabled;
        _window.GoToFloorButton.Disabled = !enabled;
    }

    private void RequestAdjacentFloor(int direction)
    {
        if (!TryGetCurrentDepth(out var current))
            return;

        var target = direction < 0
            ? _availableDepths.LastOrDefault(depth => depth < current, int.MinValue)
            : _availableDepths.FirstOrDefault(depth => depth > current, int.MaxValue);

        if (target is int.MinValue or int.MaxValue)
        {
            SetFloorStatus(Loc.GetString("nc-mapping-hub-floor-edge"));
            return;
        }

        _mapping.RequestFloorChange(target);
    }

    private void RequestSelectedFloor()
    {
        if (_selectedDepth is not { } depth)
            return;

        _mapping.RequestFloorChange(depth);
    }

    private bool TryGetCurrentDepth(out int depth)
    {
        depth = default;
        if (_players.LocalEntity is not { } player ||
            EntityManager.GetComponent<TransformComponent>(player).MapUid is not { } map ||
            !EntityManager.TryGetComponent<NCZMapComponent>(map, out var zMap))
        {
            SetFloorStatus(Loc.GetString("nc-mapping-hub-floor-none"));
            return false;
        }

        depth = zMap.Depth;
        return true;
    }

    private void OnFloorChangeCompleted(Content.Shared._NC.Mapping.NCMappingFloorChangeResult result)
    {
        SetFloorStatus(result.Message);
        RefreshFloors();
    }

    private void SetFloorStatus(string message)
    {
        if (_window != null)
            _window.FloorStatusLabel.Text = message;
    }
}
