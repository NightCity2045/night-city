// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Map;
using Content.Shared.Input;
using Robust.Client.Player;
using Robust.Shared.Input.Binding;

namespace Content.Client._NC.City.Map;

/// <summary>
/// NC: Opens the city map window from its keybind, feeds it the server's cartography and live position, and
/// forwards admin markup edits to the server.
/// </summary>
public sealed class NCCityMapClientSystem : EntitySystem
{
    [Dependency] private IPlayerManager _player = default!;

    private NCCityMapWindow? _window;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<NCCityMapSnapshotEvent>(OnSnapshot);
        SubscribeNetworkEvent<NCCityMapTrackingEvent>(OnTracking);
        SubscribeNetworkEvent<NCCityMapEditResultEvent>(OnEditResult);

        CommandBinds.Builder
            .Bind(ContentKeyFunctions.NCOpenCityMap, InputCmdHandler.FromDelegate(_ => ToggleWindow(), handle: false))
            .Register<NCCityMapClientSystem>();
    }

    public override void Shutdown()
    {
        base.Shutdown();
        CommandBinds.Unregister<NCCityMapClientSystem>();
        _window?.Close();
        _window = null;
    }

    public void ToggleWindow()
    {
        if (_window is { IsOpen: true })
        {
            _window.Close();
            return;
        }

        if (_player.LocalEntity is not { } user)
            return;

        _window = new NCCityMapWindow();
        _window.OnClose += OnWindowClosed;
        _window.OnNameBuilding += (position, name) => RaiseNetworkEvent(new NCCityMapNameBuildingRequestEvent(position, name));
        _window.OnSaveDistrict += (id, name, vertices) => RaiseNetworkEvent(new NCCityMapSaveDistrictRequestEvent(id, name, vertices));
        _window.OnDeleteDistrict += id => RaiseNetworkEvent(new NCCityMapDeleteDistrictRequestEvent(id));
        _window.SetSource(CompOrNull<TransformComponent>(user)?.GridUid);
        _window.OpenCentered();
        RaiseNetworkEvent(new NCCityMapOpenRequestEvent());
    }

    private void OnWindowClosed()
    {
        _window = null;
        RaiseNetworkEvent(new NCCityMapCloseRequestEvent());
    }

    private void OnSnapshot(NCCityMapSnapshotEvent args)
    {
        if (_window == null)
            return;

        _window.SetSource(_player.LocalEntity is { } user ? CompOrNull<TransformComponent>(user)?.GridUid : null);
        _window.ApplySnapshot(args);
    }

    private void OnEditResult(NCCityMapEditResultEvent args)
    {
        _window?.ShowEditResult(args);
    }

    private void OnTracking(NCCityMapTrackingEvent args)
    {
        if (_window == null)
            return;

        _window.SetSource(_player.LocalEntity is { } user ? CompOrNull<TransformComponent>(user)?.GridUid : null);
        _window.ApplyTracking(args);
    }
}
