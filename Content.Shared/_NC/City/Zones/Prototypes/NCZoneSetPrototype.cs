// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.City.Zones.Serialization;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Serialization;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.City.Zones.Prototypes;

/// <summary>
/// All logical zones and geometry belonging to one persistent Z-network.
/// Geometry is server-only; mapping clients request editor snapshots explicitly.
/// </summary>
[Prototype("ncZoneSet")]
public sealed partial class NCZoneSetPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true, customTypeSerializer: typeof(NCZNetworkIdSerializer))]
    public NCZNetworkId NetworkId { get; private set; }

    [DataField(serverOnly: true)]
    public List<NCZoneDefinition> Zones { get; private set; } = [];
}

[DataDefinition]
public sealed partial class NCZoneDefinition
{
    [DataField(required: true, customTypeSerializer: typeof(NCZoneIdSerializer))]
    public NCZoneId Id { get; private set; }

    [DataField(required: true)]
    public ProtoId<NCZoneKindPrototype> Kind { get; private set; }

    /// <summary>
    /// Empty for a root zone.
    /// </summary>
    [DataField(customTypeSerializer: typeof(NCZoneIdSerializer))]
    public NCZoneId Parent { get; private set; }

    [DataField(required: true)]
    public string Name { get; private set; } = string.Empty;

    [DataField]
    public int Priority { get; private set; }

    [DataField]
    public NCZoneActivityMode ActivityMode { get; private set; } = NCZoneActivityMode.Active;

    [DataField]
    public List<NCZoneGeometry> Geometry { get; private set; } = [];
}

public enum NCZoneGeometryKind : byte
{
    Polygon,
    Volume,
    TileMask,
}

[DataDefinition]
public sealed partial class NCZoneGeometry
{
    [DataField(required: true)]
    public NCZoneGeometryKind Kind { get; private set; }

    /// <summary>
    /// Makes polygonal geometry apply to every current and future floor in the Z-network.
    /// Tile masks remain explicitly floor-bound.
    /// </summary>
    [DataField]
    public bool Global { get; private set; }

    /// <summary>
    /// Depth used by polygons.
    /// </summary>
    [DataField]
    public int Z { get; private set; }

    /// <summary>
    /// Inclusive depth range used by vertical volumes.
    /// </summary>
    [DataField]
    public int MinZ { get; private set; }

    [DataField]
    public int MaxZ { get; private set; }

    [DataField]
    public List<Vector2> Vertices { get; private set; } = [];

    [DataField]
    public List<NCTileMaskChunk> Chunks { get; private set; } = [];
}

/// <summary>
/// A 32x32 tile-mask chunk. Each ulong stores the occupied bits of one row.
/// </summary>
[DataDefinition]
public sealed partial class NCTileMaskChunk
{
    public const int Size = 32;

    [DataField]
    public int Z { get; private set; }

    [DataField]
    public Vector2i Origin { get; private set; }

    [DataField]
    public List<ulong> Rows { get; private set; } = [];
}
