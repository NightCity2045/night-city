// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.City.Zones.Prototypes;
using Content.Shared._NC.Coordinates;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.City.Zones.Editor;

[Serializable, NetSerializable]
public sealed class NCZoneEditorSnapshotRequest(
    string? zoneSet = null,
    bool discardDraft = false) : EntityEventArgs
{
    public string? ZoneSet { get; } = zoneSet;
    public bool DiscardDraft { get; } = discardDraft;
}

[Serializable, NetSerializable]
public sealed class NCZoneEditorSetVertexRequest : EntityEventArgs
{
    public NCZoneId ZoneId { get; }
    public int GeometryIndex { get; }
    public int VertexIndex { get; }
    public Vector2 Position { get; }

    public NCZoneEditorSetVertexRequest(
        NCZoneId zoneId,
        int geometryIndex,
        int vertexIndex,
        Vector2 position)
    {
        ZoneId = zoneId;
        GeometryIndex = geometryIndex;
        VertexIndex = vertexIndex;
        Position = position;
    }
}

[Serializable, NetSerializable]
public sealed class NCZoneEditorInsertVertexRequest(
    NCZoneId zoneId,
    int geometryIndex,
    int afterVertexIndex,
    Vector2 position) : EntityEventArgs
{
    public NCZoneId ZoneId { get; } = zoneId;
    public int GeometryIndex { get; } = geometryIndex;
    public int AfterVertexIndex { get; } = afterVertexIndex;
    public Vector2 Position { get; } = position;
}

[Serializable, NetSerializable]
public sealed class NCZoneEditorCreateZoneRequest(
    string kind,
    string name,
    NCZoneId parent) : EntityEventArgs
{
    public string Kind { get; } = kind;
    public string Name { get; } = name;
    public NCZoneId Parent { get; } = parent;
}

[Serializable, NetSerializable]
public sealed class NCZoneEditorUpdateZoneRequest(
    NCZoneId zoneId,
    string name,
    NCZoneId parent,
    int priority,
    NCZoneActivityMode activityMode) : EntityEventArgs
{
    public NCZoneId ZoneId { get; } = zoneId;
    public string Name { get; } = name;
    public NCZoneId Parent { get; } = parent;
    public int Priority { get; } = priority;
    public NCZoneActivityMode ActivityMode { get; } = activityMode;
}

[Serializable, NetSerializable]
public sealed class NCZoneEditorDeleteZoneRequest(NCZoneId zoneId) : EntityEventArgs
{
    public NCZoneId ZoneId { get; } = zoneId;
}

[Serializable, NetSerializable]
public sealed class NCZoneEditorAddGeometryRequest(
    NCZoneId zoneId,
    NCZoneGeometryKind kind,
    int z,
    int minZ,
    int maxZ,
    Vector2[] vertices,
    Vector2i[] tiles,
    bool global = false) : EntityEventArgs
{
    public NCZoneId ZoneId { get; } = zoneId;
    public NCZoneGeometryKind Kind { get; } = kind;
    public int Z { get; } = z;
    public int MinZ { get; } = minZ;
    public int MaxZ { get; } = maxZ;
    public Vector2[] Vertices { get; } = vertices;
    public Vector2i[] Tiles { get; } = tiles;
    public bool Global { get; } = global;
}

[Serializable, NetSerializable]
public sealed class NCZoneEditorSetGeometryScopeRequest(
    NCZoneId zoneId,
    int geometryIndex,
    bool global,
    int minZ,
    int maxZ) : EntityEventArgs
{
    public NCZoneId ZoneId { get; } = zoneId;
    public int GeometryIndex { get; } = geometryIndex;
    public bool Global { get; } = global;
    public int MinZ { get; } = minZ;
    public int MaxZ { get; } = maxZ;
}

[Serializable, NetSerializable]
public sealed class NCZoneEditorDeleteGeometryRequest(
    NCZoneId zoneId,
    int geometryIndex) : EntityEventArgs
{
    public NCZoneId ZoneId { get; } = zoneId;
    public int GeometryIndex { get; } = geometryIndex;
}

[Serializable, NetSerializable]
public sealed class NCZoneEditorRemoveVertexRequest(
    NCZoneId zoneId,
    int geometryIndex,
    int vertexIndex) : EntityEventArgs
{
    public NCZoneId ZoneId { get; } = zoneId;
    public int GeometryIndex { get; } = geometryIndex;
    public int VertexIndex { get; } = vertexIndex;
}

[Serializable, NetSerializable]
public sealed class NCZoneEditorTilePatchRequest(
    NCZoneId zoneId,
    int geometryIndex,
    int z,
    Vector2i[] tiles,
    bool add) : EntityEventArgs
{
    public NCZoneId ZoneId { get; } = zoneId;
    public int GeometryIndex { get; } = geometryIndex;
    public int Z { get; } = z;
    public Vector2i[] Tiles { get; } = tiles;
    public bool Add { get; } = add;
}

[Serializable, NetSerializable]
public sealed class NCZoneEditorHistoryRequest(bool redo) : EntityEventArgs
{
    public bool Redo { get; } = redo;
}

[Serializable, NetSerializable]
public sealed class NCZoneEditorValidationRequest : EntityEventArgs;

[Serializable, NetSerializable]
public sealed class NCZoneEditorExportRequest(string fileName) : EntityEventArgs
{
    public string FileName { get; } = fileName;
}

[Serializable, NetSerializable]
public sealed class NCZoneEditorOperationResult(
    bool success,
    string message) : EntityEventArgs
{
    public bool Success { get; } = success;
    public string Message { get; } = message;
}

[Serializable, NetSerializable]
public sealed class NCZoneEditorValidationResult(
    NCZoneEditorValidationError[] errors) : EntityEventArgs
{
    public NCZoneEditorValidationError[] Errors { get; } = errors;
}

[Serializable, NetSerializable]
public sealed class NCZoneEditorValidationError(
    NCZoneId zoneId,
    int geometryIndex,
    string message)
{
    public NCZoneId ZoneId { get; } = zoneId;
    public int GeometryIndex { get; } = geometryIndex;
    public string Message { get; } = message;
}

[Serializable, NetSerializable]
public sealed class NCZoneEditorSnapshot : EntityEventArgs
{
    public string ZoneSet { get; }
    public NCZNetworkId NetworkId { get; }
    public NCZoneEditorZone[] Zones { get; }
    public int Revision { get; }
    public bool Dirty { get; }

    public NCZoneEditorSnapshot(
        string zoneSet,
        NCZNetworkId networkId,
        NCZoneEditorZone[] zones,
        int revision = 0,
        bool dirty = false)
    {
        ZoneSet = zoneSet;
        NetworkId = networkId;
        Zones = zones;
        Revision = revision;
        Dirty = dirty;
    }
}

[Serializable, NetSerializable]
public sealed class NCZoneEditorPatch(
    int baseRevision,
    int revision,
    bool dirty,
    NCZoneEditorZone[] upserted,
    NCZoneId[] removed) : EntityEventArgs
{
    public int BaseRevision { get; } = baseRevision;
    public int Revision { get; } = revision;
    public bool Dirty { get; } = dirty;
    public NCZoneEditorZone[] Upserted { get; } = upserted;
    public NCZoneId[] Removed { get; } = removed;
}

[Serializable, NetSerializable]
public sealed class NCZoneEditorZone
{
    public NCZoneId Id { get; }
    public NCZoneId Parent { get; }
    public string Kind { get; }
    public string Name { get; }
    public int Priority { get; }
    public NCZoneActivityMode ActivityMode { get; }
    public byte ColorR { get; }
    public byte ColorG { get; }
    public byte ColorB { get; }
    public NCZoneEditorGeometry[] Geometry { get; }

    public NCZoneEditorZone(
        NCZoneId id,
        NCZoneId parent,
        string kind,
        string name,
        int priority,
        NCZoneActivityMode activityMode,
        byte colorR,
        byte colorG,
        byte colorB,
        NCZoneEditorGeometry[] geometry)
    {
        Id = id;
        Parent = parent;
        Kind = kind;
        Name = name;
        Priority = priority;
        ActivityMode = activityMode;
        ColorR = colorR;
        ColorG = colorG;
        ColorB = colorB;
        Geometry = geometry;
    }
}

[Serializable, NetSerializable]
public sealed class NCZoneEditorGeometry
{
    public NCZoneGeometryKind Kind { get; }
    public bool Global { get; }
    public int Z { get; }
    public int MinZ { get; }
    public int MaxZ { get; }
    public Vector2[] Vertices { get; }
    public NCZoneEditorTileChunk[] Chunks { get; }

    public NCZoneEditorGeometry(
        NCZoneGeometryKind kind,
        bool global,
        int z,
        int minZ,
        int maxZ,
        Vector2[] vertices,
        NCZoneEditorTileChunk[] chunks)
    {
        Kind = kind;
        Global = global;
        Z = z;
        MinZ = minZ;
        MaxZ = maxZ;
        Vertices = vertices;
        Chunks = chunks;
    }
}

[Serializable, NetSerializable]
public sealed class NCZoneEditorTileChunk
{
    public int Z { get; }
    public Vector2i Origin { get; }
    public ulong[] Rows { get; }

    public NCZoneEditorTileChunk(int z, Vector2i origin, ulong[] rows)
    {
        Z = z;
        Origin = origin;
        Rows = rows;
    }
}
