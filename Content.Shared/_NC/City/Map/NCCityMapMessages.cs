// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.City.Map;

[Serializable, NetSerializable]
public enum NCCityMapFeatureKind : byte
{
    District,
    Sector,
    Street,
    Building,
}

[Serializable, NetSerializable]
public enum NCCityMapGeometryKind : byte
{
    Polygon,
    TileMask,
}

/// <summary>
/// One compressed 32x32 tile-mask fragment. Each row stores occupied tiles as bits.
/// </summary>
[Serializable, NetSerializable]
public sealed record NCCityMapTileChunk(int OriginX, int OriginY, List<ulong> Rows);

/// <summary>
/// Public cartography fragment derived from server-only city-zone geometry.
/// </summary>
[Serializable, NetSerializable]
public sealed record NCCityMapFeature(
    NCCityMapFeatureKind Kind,
    NCCityMapGeometryKind GeometryKind,
    string Name,
    List<Vector2> Vertices,
    List<NCCityMapTileChunk> Chunks);

/// <summary>
/// A short-lived marker rendered over the static city geometry.
/// </summary>
[Serializable, NetSerializable]
public sealed record NCCityMapMarker(
    string Label,
    string NetworkId,
    int Z,
    Vector2 Position,
    bool Live);

/// <summary>Client asks for the city map; the server answers with one snapshot and then live tracking.</summary>
[Serializable, NetSerializable]
public sealed class NCCityMapOpenRequestEvent : EntityEventArgs;

/// <summary>Client closed its city map window; the server stops sending tracking updates.</summary>
[Serializable, NetSerializable]
public sealed class NCCityMapCloseRequestEvent : EntityEventArgs;

/// <summary>
/// Sanitized public cartography for the player's current city network and floor.
/// Server-only zone prototypes and private zone kinds never cross the network boundary.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCCityMapSnapshotEvent(
    string networkId,
    int z,
    Vector2 userPosition,
    List<NCCityMapFeature> features,
    List<NCCityMapMarker> markers,
    string notice) : EntityEventArgs
{
    public readonly string NetworkId = networkId;
    public readonly int Z = z;
    public readonly Vector2 UserPosition = userPosition;
    public readonly List<NCCityMapFeature> Features = features;
    public readonly List<NCCityMapMarker> Markers = markers;
    public readonly string Notice = notice;
}

/// <summary>
/// Lightweight live position sent while the city map is open; static geometry stays client-side.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCCityMapTrackingEvent(
    string networkId,
    int z,
    Vector2 userPosition,
    List<NCCityMapMarker> markers) : EntityEventArgs
{
    public readonly string NetworkId = networkId;
    public readonly int Z = z;
    public readonly Vector2 UserPosition = userPosition;
    public readonly List<NCCityMapMarker> Markers = markers;
}
