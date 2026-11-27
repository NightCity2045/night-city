// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.City.Map;
using Content.Shared._NC.Coordinates.Systems;
using Robust.Shared.Enums;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._NC.City.Map;

/// <summary>
/// NC: The player's city map. It sends the player's city network and floor, then keeps the player's own position
/// live while the window is open. District and building cartography is not authored yet, so no features are sent.
/// </summary>
public sealed class NCCityMapSystem : EntitySystem
{
    private static readonly TimeSpan TrackingInterval = TimeSpan.FromSeconds(0.5);

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private NCMapCoordinatesSystem _mapCoordinates = default!;

    private readonly HashSet<ICommonSession> _viewers = new();
    private TimeSpan _nextTracking;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<NCCityMapOpenRequestEvent>(OnOpen);
        SubscribeNetworkEvent<NCCityMapCloseRequestEvent>(OnClose);
    }

    private void OnOpen(NCCityMapOpenRequestEvent args, EntitySessionEventArgs session)
    {
        if (session.SenderSession.AttachedEntity is not { } user)
            return;

        _viewers.Add(session.SenderSession);
        RaiseNetworkEvent(BuildSnapshot(user), session.SenderSession);
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
                []), viewer);
        }
    }

    private NCCityMapSnapshotEvent BuildSnapshot(EntityUid user)
    {
        if (!_mapCoordinates.TryGetCoordinates(user, out var coordinates))
            return new NCCityMapSnapshotEvent(string.Empty, 0, default, [], [], Loc.GetString("nc-city-map-no-signal"));

        var features = new List<NCCityMapFeature>();

        return new NCCityMapSnapshotEvent(
            coordinates.NetworkId.ToString(),
            coordinates.Z,
            coordinates.Position,
            features,
            [],
            features.Count == 0 ? Loc.GetString("nc-city-map-grid-only") : string.Empty);
    }
}
