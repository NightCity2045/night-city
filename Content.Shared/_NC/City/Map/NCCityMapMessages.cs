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
/// Public cartography fragment: an authored district outline or a building detected from the map.
/// <see cref="Id"/> is stable for districts and only valid within one snapshot for buildings.
/// An empty <see cref="Name"/> means the building has not been named yet.
/// </summary>
[Serializable, NetSerializable]
public sealed record NCCityMapFeature(
    string Id,
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
/// Public cartography for the player's current city network and floor. <see cref="CanEdit"/> tells the client to
/// offer the admin markup tools; the server checks permissions again on every request.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCCityMapSnapshotEvent(
    string networkId,
    int z,
    Vector2 userPosition,
    List<NCCityMapFeature> features,
    List<NCCityMapMarker> markers,
    string notice,
    bool canEdit = false) : EntityEventArgs
{
    public readonly bool CanEdit = canEdit;
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

/// <summary>Admin: names the detected building under <see cref="Position"/> on the admin's floor. Empty clears it.</summary>
[Serializable, NetSerializable]
public sealed class NCCityMapNameBuildingRequestEvent(Vector2 position, string name) : EntityEventArgs
{
    public readonly Vector2 Position = position;
    public readonly string Name = name;
}

/// <summary>Admin: creates a district (empty <see cref="Id"/>) or replaces the outline and name of one.</summary>
[Serializable, NetSerializable]
public sealed class NCCityMapSaveDistrictRequestEvent(string id, string name, List<Vector2> vertices) : EntityEventArgs
{
    public readonly string Id = id;
    public readonly string Name = name;
    public readonly List<Vector2> Vertices = vertices;
}

/// <summary>Admin: deletes a district.</summary>
[Serializable, NetSerializable]
public sealed class NCCityMapDeleteDistrictRequestEvent(string id) : EntityEventArgs
{
    public readonly string Id = id;
}

/// <summary>Result of an admin markup request, shown in the map window's status line.</summary>
[Serializable, NetSerializable]
public sealed class NCCityMapEditResultEvent(bool success, string message) : EntityEventArgs
{
    public readonly bool Success = success;
    public readonly string Message = message;
}
