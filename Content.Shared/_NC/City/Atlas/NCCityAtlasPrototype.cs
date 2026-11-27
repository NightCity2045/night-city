// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Numerics;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Serialization;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.City.Atlas;

/// <summary>
/// Authored city cartography for one Z-network: district outlines and the names of buildings.
/// Building outlines are never authored; the server detects them from the map's walls and doors.
/// </summary>
[Prototype("ncCityAtlas")]
public sealed partial class NCCityAtlasPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true, customTypeSerializer: typeof(NCZNetworkIdSerializer))]
    public NCZNetworkId NetworkId { get; private set; }

    [DataField]
    public List<NCCityDistrictDefinition> Districts { get; private set; } = [];

    [DataField]
    public List<NCCityBuildingLabel> Buildings { get; private set; } = [];
}

/// <summary>A named district outline in map space. It applies to every floor of the network.</summary>
[DataDefinition]
public sealed partial class NCCityDistrictDefinition
{
    /// <summary>Stable identity used by the admin editor; never shown to players.</summary>
    [DataField(required: true)]
    public string Id = string.Empty;

    [DataField(required: true)]
    public string Name = string.Empty;

    [DataField]
    public List<Vector2> Vertices = [];
}

/// <summary>
/// Names the detected building that contains <see cref="Position"/>. Without <see cref="Z"/> the name also
/// covers the same building column on every other floor that has no name of its own.
/// </summary>
[DataDefinition]
public sealed partial class NCCityBuildingLabel
{
    [DataField(required: true)]
    public string Name = string.Empty;

    [DataField(required: true)]
    public Vector2 Position;

    [DataField]
    public int? Z;
}

/// <summary>Editable runtime copy of an atlas, also the payload of the server's saved atlas file.</summary>
[DataDefinition]
public sealed partial class NCCityAtlasData
{
    [DataField(required: true, customTypeSerializer: typeof(NCZNetworkIdSerializer))]
    public NCZNetworkId NetworkId;

    [DataField]
    public List<NCCityDistrictDefinition> Districts = [];

    [DataField]
    public List<NCCityBuildingLabel> Buildings = [];
}
