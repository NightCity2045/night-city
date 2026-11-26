// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.City.Zones;
using Content.Shared._NC.City.Map;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Prototypes;
using Content.Shared._NC.Coordinates.Systems;
using Robust.Shared.Enums;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._NC.City.Map;

/// <summary>
/// NC: The player's city map. It sends public cartography (districts, sectors, streets and buildings) for the
/// player's current city network and floor, then keeps the player's own position live while the window is open.
/// </summary>
public sealed class NCCityMapSystem : EntitySystem
{
    private static readonly TimeSpan TrackingInterval = TimeSpan.FromSeconds(0.5);

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private NCMapCoordinatesSystem _mapCoordinates = default!;
    [Dependency] private NCZoneSystem _zones = default!;

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

        var zoneInfos = new List<NCZoneInfo>();
        var geometryInfos = new List<NCZoneGeometryInfo>();
        var features = new List<NCCityMapFeature>();
        _zones.GetZones(coordinates.NetworkId, zoneInfos);

        foreach (var zone in zoneInfos)
        {
            if (!TryGetPublicMapKind(zone.ContextSlot, out var featureKind) ||
                !_zones.GetGeometry(zone.Id, geometryInfos))
            {
                continue;
            }

            foreach (var geometry in geometryInfos)
            {
                switch (geometry.Kind)
                {
                    case NCZoneGeometryKind.Polygon when geometry.Global || geometry.Z == coordinates.Z:
                    case NCZoneGeometryKind.Volume when geometry.Global ||
                                                        coordinates.Z >= geometry.MinZ &&
                                                        coordinates.Z <= geometry.MaxZ:
                        if (geometry.Vertices.Count >= 3)
                        {
                            features.Add(new NCCityMapFeature(
                                featureKind,
                                NCCityMapGeometryKind.Polygon,
                                zone.Name,
                                geometry.Vertices.ToList(),
                                []));
                        }
                        break;
                    case NCZoneGeometryKind.TileMask:
                    {
                        var chunks = geometry.Chunks
                            .Where(chunk => chunk.Z == coordinates.Z)
                            .Select(chunk => new NCCityMapTileChunk(
                                chunk.Origin.X,
                                chunk.Origin.Y,
                                chunk.Rows.ToList()))
                            .ToList();
                        if (chunks.Count > 0)
                        {
                            features.Add(new NCCityMapFeature(
                                featureKind,
                                NCCityMapGeometryKind.TileMask,
                                zone.Name,
                                [],
                                chunks));
                        }
                        break;
                    }
                }
            }
        }

        return new NCCityMapSnapshotEvent(
            coordinates.NetworkId.ToString(),
            coordinates.Z,
            coordinates.Position,
            features,
            [],
            features.Count == 0 ? Loc.GetString("nc-city-map-grid-only") : string.Empty);
    }

    private static bool TryGetPublicMapKind(NCZoneContextSlot contextSlot, out NCCityMapFeatureKind kind)
    {
        kind = contextSlot switch
        {
            NCZoneContextSlot.District => NCCityMapFeatureKind.District,
            NCZoneContextSlot.Sector => NCCityMapFeatureKind.Sector,
            NCZoneContextSlot.Street => NCCityMapFeatureKind.Street,
            NCZoneContextSlot.Building => NCCityMapFeatureKind.Building,
            _ => default,
        };
        return contextSlot is NCZoneContextSlot.District or
            NCZoneContextSlot.Sector or
            NCZoneContextSlot.Street or
            NCZoneContextSlot.Building;
    }
}
