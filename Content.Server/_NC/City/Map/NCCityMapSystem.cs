// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.City.Atlas;
using Content.Server.Administration.Managers;
using Content.Shared._NC.City.Map;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared.Administration;
using Robust.Shared.Enums;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._NC.City.Map;

/// <summary>
/// NC: The player's city map. It sends the districts and the detected buildings of the player's current floor,
/// keeps the player's own position live while the window is open, and applies admin markup edits.
/// </summary>
public sealed class NCCityMapSystem : EntitySystem
{
    private static readonly TimeSpan TrackingInterval = TimeSpan.FromSeconds(0.5);

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IAdminManager _admins = default!;
    [Dependency] private NCMapCoordinatesSystem _mapCoordinates = default!;
    [Dependency] private NCCityAtlasSystem _atlas = default!;

    private readonly HashSet<ICommonSession> _viewers = new();
    private TimeSpan _nextTracking;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<NCCityMapOpenRequestEvent>(OnOpen);
        SubscribeNetworkEvent<NCCityMapCloseRequestEvent>(OnClose);
        SubscribeNetworkEvent<NCCityMapNameBuildingRequestEvent>(OnNameBuilding);
        SubscribeNetworkEvent<NCCityMapSaveDistrictRequestEvent>(OnSaveDistrict);
        SubscribeNetworkEvent<NCCityMapDeleteDistrictRequestEvent>(OnDeleteDistrict);
    }

    private void OnOpen(NCCityMapOpenRequestEvent args, EntitySessionEventArgs session)
    {
        if (session.SenderSession.AttachedEntity is not { } user)
            return;

        _viewers.Add(session.SenderSession);
        RaiseNetworkEvent(BuildSnapshot(session.SenderSession, user), session.SenderSession);
    }

    private void OnClose(NCCityMapCloseRequestEvent args, EntitySessionEventArgs session)
    {
        _viewers.Remove(session.SenderSession);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_viewers.Count == 0 || _timing.CurTime < _nextTracking)
            return;

        _nextTracking = _timing.CurTime + TrackingInterval;
        foreach (var viewer in _viewers.ToArray())
        {
            if (viewer.Status is SessionStatus.Disconnected or SessionStatus.Zombie)
            {
                _viewers.Remove(viewer);
                continue;
            }

            if (viewer.AttachedEntity is not { } user ||
                !_mapCoordinates.TryGetCoordinates(user, out var coordinates))
            {
                continue;
            }

            RaiseNetworkEvent(new NCCityMapTrackingEvent(
                coordinates.NetworkId.ToString(),
                coordinates.Z,
                coordinates.Position,
                CollectMarkers(user)), viewer);
        }
    }

    #region Admin markup

    private void OnNameBuilding(NCCityMapNameBuildingRequestEvent args, EntitySessionEventArgs session)
    {
        if (!TryGetEditor(session.SenderSession, out var user, out var coordinates) ||
            Transform(user).MapUid is not { } map)
        {
            return;
        }

        var target = new NCMapCoordinates(coordinates.NetworkId, args.Position, coordinates.Z);
        var success = _atlas.TryNameBuilding(target, map, args.Name, out var message);
        FinishEdit(session.SenderSession, coordinates.NetworkId, success, message);
    }

    private void OnSaveDistrict(NCCityMapSaveDistrictRequestEvent args, EntitySessionEventArgs session)
    {
        if (!TryGetEditor(session.SenderSession, out _, out var coordinates))
            return;

        var success = _atlas.TrySaveDistrict(coordinates.NetworkId, args.Id, args.Name, args.Vertices, out var message);
        FinishEdit(session.SenderSession, coordinates.NetworkId, success, message);
    }

    private void OnDeleteDistrict(NCCityMapDeleteDistrictRequestEvent args, EntitySessionEventArgs session)
    {
        if (!TryGetEditor(session.SenderSession, out _, out var coordinates))
            return;

        var success = _atlas.TryDeleteDistrict(coordinates.NetworkId, args.Id, out var message);
        FinishEdit(session.SenderSession, coordinates.NetworkId, success, message);
    }

    private bool CanEdit(ICommonSession session) => _admins.HasAdminFlag(session, AdminFlags.Mapping);

    private bool TryGetEditor(ICommonSession session, out EntityUid user, out NCMapCoordinates coordinates)
    {
        user = default;
        coordinates = default;
        if (!CanEdit(session) || session.AttachedEntity is not { } attached ||
            !_mapCoordinates.TryGetCoordinates(attached, out coordinates))
        {
            return false;
        }

        user = attached;
        return true;
    }

    private void FinishEdit(ICommonSession editor, NCZNetworkId networkId, bool success, string message)
    {
        RaiseNetworkEvent(new NCCityMapEditResultEvent(success, message), editor);
        if (!success)
            return;

        // Everyone looking at the same city sees the new names and outlines immediately.
        foreach (var viewer in _viewers)
        {
            if (viewer.AttachedEntity is { } user &&
                _mapCoordinates.TryGetCoordinates(user, out var coordinates) &&
                coordinates.NetworkId == networkId)
            {
                RaiseNetworkEvent(BuildSnapshot(viewer, user), viewer);
            }
        }
    }

    #endregion

    private NCCityMapSnapshotEvent BuildSnapshot(ICommonSession session, EntityUid user)
    {
        if (!_mapCoordinates.TryGetCoordinates(user, out var coordinates) || Transform(user).MapUid is not { } map)
            return new NCCityMapSnapshotEvent(string.Empty, 0, default, [], [], Loc.GetString("nc-city-map-no-signal"));

        var features = new List<NCCityMapFeature>();
        _atlas.BuildFeatures(coordinates.NetworkId, coordinates.Z, map, features);

        return new NCCityMapSnapshotEvent(
            coordinates.NetworkId.ToString(),
            coordinates.Z,
            coordinates.Position,
            features,
            CollectMarkers(user),
            features.Count == 0 ? Loc.GetString("nc-city-map-grid-only") : string.Empty,
            CanEdit(session));
    }

    private List<NCCityMapMarker> CollectMarkers(EntityUid viewer)
    {
        var markers = new List<NCCityMapMarker>();
        var ev = new NCCityMapCollectMarkersEvent(markers);
        RaiseLocalEvent(viewer, ref ev);
        return markers;
    }
}
