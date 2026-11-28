// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.IO;
using System.Linq;
using System.Numerics;
using Content.Shared._NC.City.Atlas;
using Content.Shared._NC.City.Map;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared.GameTicking;
using Content.Shared.Pinpointer;
using Robust.Shared.ContentPack;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Utility;

namespace Content.Server._NC.City.Atlas;

/// <summary>
/// Owns city districts and building names. Districts are authored outlines; buildings are detected from the map's
/// walls and doors per floor and only carry a name. Admin edits apply live and are saved to the server's user data
/// in prototype form, so the file can be copied into <c>Resources/Prototypes/_NC/City/</c> as-is.
/// </summary>
public sealed partial class NCCityAtlasSystem : EntitySystem
{
    public const int MaximumNameLength = 64;
    public const int MaximumDistrictVertices = 128;
    public static readonly ResPath SaveDirectory = new("/CityAtlas");

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IResourceManager _resources = default!;
    [Dependency] private ISerializationManager _serialization = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private NCMapCoordinatesSystem _mapCoordinates = default!;

    private readonly Dictionary<NCZNetworkId, Atlas> _atlases = new();
    private readonly Dictionary<EntityUid, NCCityBuildingIndex> _buildings = new();

    private sealed class Atlas(string prototypeId, NCCityAtlasData data)
    {
        public readonly string PrototypeId = prototypeId;
        public readonly NCCityAtlasData Data = data;
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => _buildings.Clear());
        LoadAtlases();
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<NCCityAtlasPrototype>())
            LoadAtlases();
    }

    #region Loading and saving

    private void LoadAtlases()
    {
        _atlases.Clear();
        foreach (var prototype in _prototypes.EnumeratePrototypes<NCCityAtlasPrototype>())
        {
            var data = new NCCityAtlasData
            {
                NetworkId = prototype.NetworkId,
                Districts = prototype.Districts.Select(Copy).ToList(),
                Buildings = prototype.Buildings.Select(Copy).ToList(),
            };
            _atlases[prototype.NetworkId] = new Atlas(prototype.ID, data);
        }

        // Edits saved by admins on this server win over the shipped prototype until they are copied into it.
        if (!_resources.UserData.IsDir(SaveDirectory))
            return;

        foreach (var file in _resources.UserData.DirectoryEntries(SaveDirectory))
        {
            var path = SaveDirectory / file;
            if (path.Extension != "yml" || !TryReadSaved(path, out var prototypeId, out var data))
                continue;

            _atlases[data.NetworkId] = new Atlas(prototypeId, data);
        }
    }

    private bool TryReadSaved(ResPath path, out string prototypeId, out NCCityAtlasData data)
    {
        prototypeId = string.Empty;
        data = default!;
        try
        {
            using var reader = _resources.UserData.OpenText(path);
            var document = DataNodeParser.ParseYamlStream(reader).FirstOrDefault();
            if (document?.Root is not SequenceDataNode { Count: 1 } sequence ||
                sequence[0] is not MappingDataNode mapping ||
                !mapping.TryGet<ValueDataNode>("id", out var id))
            {
                Log.Error($"City atlas {path} is not a single ncCityAtlas prototype.");
                return false;
            }

            prototypeId = id.Value;
            var payload = mapping.Copy();
            payload.Remove("type");
            payload.Remove("id");
            data = _serialization.Read<NCCityAtlasData>(payload, notNullableOverride: true);
            return data.NetworkId.IsValid;
        }
        catch (Exception exception)
        {
            Log.Error($"Unable to read city atlas {path}: {exception.Message}");
            return false;
        }
    }

    private bool TrySave(Atlas atlas, out string error)
    {
        error = string.Empty;
        try
        {
            var payload = (MappingDataNode) _serialization.WriteValue(atlas.Data, notNullableOverride: true);
            var prototype = new MappingDataNode();
            prototype.Add("type", "ncCityAtlas");
            prototype.Add("id", atlas.PrototypeId);
            foreach (var (key, value) in payload)
                prototype.Add(key, value);

            var document = new SequenceDataNode();
            document.Add(prototype);

            _resources.UserData.CreateDir(SaveDirectory);
            using var writer = _resources.UserData.OpenWriteText(SavePath(atlas));
            writer.WriteLine("# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0");
            writer.WriteLine();
            writer.WriteLine("# Saved by the city map editor. Copy into Resources/Prototypes/_NC/City/ to ship it.");
            writer.Write(document.ToString());
            return true;
        }
        catch (Exception exception)
        {
            Log.Error($"Unable to save city atlas {atlas.PrototypeId}: {exception}");
            error = Loc.GetString("nc-city-map-edit-save-failed");
            return false;
        }
    }

    public static ResPath SavePathFor(string prototypeId) => SaveDirectory / $"{prototypeId}.yml";

    private static ResPath SavePath(Atlas atlas) => SavePathFor(atlas.PrototypeId);

    private Atlas GetOrCreateAtlas(NCZNetworkId networkId)
    {
        if (_atlases.TryGetValue(networkId, out var atlas))
            return atlas;

        atlas = new Atlas($"NCCityAtlas{networkId.Value:N}", new NCCityAtlasData { NetworkId = networkId });
        _atlases[networkId] = atlas;
        return atlas;
    }

    private static NCCityDistrictDefinition Copy(NCCityDistrictDefinition source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Vertices = source.Vertices.ToList(),
    };

    private static NCCityBuildingLabel Copy(NCCityBuildingLabel source) => new()
    {
        Name = source.Name,
        Position = source.Position,
        Z = source.Z,
    };

    #endregion

    #region Queries

    /// <summary>Finds the district whose outline contains the position. Districts apply to every floor.</summary>
    public bool TryGetDistrict(NCMapCoordinates coordinates, out NCCityDistrictDefinition district)
    {
        district = default!;
        if (!_atlases.TryGetValue(coordinates.NetworkId, out var atlas))
            return false;

        foreach (var candidate in atlas.Data.Districts)
        {
            if (candidate.Vertices.Count < 3 || !Contains(candidate.Vertices, coordinates.Position))
                continue;

            district = candidate;
            return true;
        }

        return false;
    }

    /// <summary>Names the district and building an entity is in. Either can be null.</summary>
    public void GetLocation(EntityUid uid, out string? district, out string? building)
    {
        district = null;
        building = null;
        if (!_mapCoordinates.TryGetCoordinates(uid, out var coordinates))
            return;

        if (TryGetDistrict(coordinates, out var found))
            district = found.Name;

        if (Transform(uid).MapUid is not { } map)
            return;

        var index = GetBuildings(map);
        if (!index.TryGetBuilding(ToTile(coordinates.Position), out var id))
            return;

        var names = ResolveBuildingNames(coordinates.NetworkId, coordinates.Z, index);
        if (names.TryGetValue(id, out var name))
            building = name;
    }

    /// <summary>Detected buildings of one floor map, rebuilt whenever its walls or floors change.</summary>
    public NCCityBuildingIndex GetBuildings(EntityUid mapUid)
    {
        var grids = new List<(EntityUid Uid, NavMapComponent NavMap)>();
        var query = EntityQueryEnumerator<NavMapComponent, MapGridComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var navMap, out _, out var xform))
        {
            if (xform.MapUid == mapUid)
                grids.Add((uid, navMap));
        }

        var hash = new HashCode();
        foreach (var (uid, navMap) in grids)
        {
            hash.Add(uid);
            hash.Add(NCCityBuildingIndex.ComputeSignature(navMap));
        }

        var signature = hash.ToHashCode();
        if (_buildings.TryGetValue(mapUid, out var cached) && cached.Signature == signature)
            return cached;

        var index = new NCCityBuildingIndex { Signature = signature };
        foreach (var (uid, navMap) in grids)
        {
            var (position, rotation) = _transform.GetWorldPositionRotation(uid);
            // Building tiles are reported in world tiles; a rotated grid has no such alignment.
            if (Math.Abs(rotation.Reduced().Theta) > 0.001)
                continue;

            index.AddGrid(navMap, new Vector2i((int) MathF.Round(position.X), (int) MathF.Round(position.Y)));
        }

        _buildings[mapUid] = index;
        return index;
    }

    /// <summary>
    /// Resolves the name of each detected building on a floor. A label for this floor wins; otherwise a label
    /// that covers all floors names the building whose footprint contains it here as well.
    /// </summary>
    public Dictionary<int, string> ResolveBuildingNames(NCZNetworkId networkId, int z, NCCityBuildingIndex index)
    {
        var names = new Dictionary<int, string>();
        if (!_atlases.TryGetValue(networkId, out var atlas))
            return names;

        foreach (var label in atlas.Data.Buildings.Where(label => label.Z == z))
        {
            if (TryFindBuilding(index, label.Position, out var id))
                names.TryAdd(id, label.Name);
        }

        foreach (var label in atlas.Data.Buildings.Where(label => label.Z == null))
        {
            if (TryFindBuilding(index, label.Position, out var id))
                names.TryAdd(id, label.Name);
        }

        return names;
    }

    /// <summary>Map features of one floor: every district outline plus the floor's buildings.</summary>
    public void BuildFeatures(NCZNetworkId networkId, int z, EntityUid mapUid, List<NCCityMapFeature> features)
    {
        if (_atlases.TryGetValue(networkId, out var atlas))
        {
            foreach (var district in atlas.Data.Districts)
            {
                if (district.Vertices.Count < 3)
                    continue;

                features.Add(new NCCityMapFeature(
                    district.Id,
                    NCCityMapFeatureKind.District,
                    NCCityMapGeometryKind.Polygon,
                    district.Name,
                    district.Vertices.ToList(),
                    []));
            }
        }

        var index = GetBuildings(mapUid);
        var names = ResolveBuildingNames(networkId, z, index);
        for (var id = 0; id < index.Buildings.Count; id++)
        {
            features.Add(new NCCityMapFeature(
                $"{z}:{id}",
                NCCityMapFeatureKind.Building,
                NCCityMapGeometryKind.TileMask,
                names.GetValueOrDefault(id, string.Empty),
                [],
                ToChunks(index.Buildings[id].Tiles)));
        }
    }

    #endregion

    #region Admin edits

    /// <summary>Names the building under the position on this floor and every floor of the same column.</summary>
    public bool TryNameBuilding(NCMapCoordinates coordinates, EntityUid mapUid, string name, out string message)
    {
        name = name.Trim();
        if (name.Length > MaximumNameLength)
        {
            message = Loc.GetString("nc-city-map-edit-name-too-long", ("max", MaximumNameLength));
            return false;
        }

        var index = GetBuildings(mapUid);
        if (!TryFindBuilding(index, coordinates.Position, out var id))
        {
            message = Loc.GetString("nc-city-map-edit-no-building");
            return false;
        }

        var atlas = GetOrCreateAtlas(coordinates.NetworkId);
        // Replace every label that currently names this building here, including labels covering all floors.
        atlas.Data.Buildings.RemoveAll(label =>
            (label.Z == null || label.Z == coordinates.Z) &&
            TryFindBuilding(index, label.Position, out var labelled) && labelled == id);

        if (name.Length > 0)
            atlas.Data.Buildings.Add(new NCCityBuildingLabel { Name = name, Position = coordinates.Position });

        if (!TrySave(atlas, out message))
            return false;

        message = name.Length > 0
            ? Loc.GetString("nc-city-map-edit-building-named", ("name", name), ("path", SavePath(atlas).ToString()))
            : Loc.GetString("nc-city-map-edit-building-cleared", ("path", SavePath(atlas).ToString()));
        return true;
    }

    /// <summary>Creates a district (empty id) or replaces an existing one's name and outline.</summary>
    public bool TrySaveDistrict(NCZNetworkId networkId, string id, string name, List<Vector2> vertices, out string message)
    {
        name = name.Trim();
        if (name.Length is 0 or > MaximumNameLength)
        {
            message = Loc.GetString("nc-city-map-edit-name-too-long", ("max", MaximumNameLength));
            return false;
        }

        if (vertices.Count is < 3 or > MaximumDistrictVertices || vertices.Any(v => !float.IsFinite(v.X) || !float.IsFinite(v.Y)))
        {
            message = Loc.GetString("nc-city-map-edit-district-shape", ("max", MaximumDistrictVertices));
            return false;
        }

        var atlas = GetOrCreateAtlas(networkId);
        var district = id.Length == 0 ? null : atlas.Data.Districts.FirstOrDefault(d => d.Id == id);
        if (id.Length != 0 && district == null)
        {
            message = Loc.GetString("nc-city-map-edit-district-missing");
            return false;
        }

        if (district == null)
        {
            district = new NCCityDistrictDefinition { Id = Guid.NewGuid().ToString("D") };
            atlas.Data.Districts.Add(district);
        }

        district.Name = name;
        district.Vertices = vertices.ToList();
        if (!TrySave(atlas, out message))
            return false;

        message = Loc.GetString("nc-city-map-edit-district-saved", ("name", name), ("path", SavePath(atlas).ToString()));
        return true;
    }

    public bool TryDeleteDistrict(NCZNetworkId networkId, string id, out string message)
    {
        if (!_atlases.TryGetValue(networkId, out var atlas) || atlas.Data.Districts.RemoveAll(d => d.Id == id) == 0)
        {
            message = Loc.GetString("nc-city-map-edit-district-missing");
            return false;
        }

        if (!TrySave(atlas, out message))
            return false;

        message = Loc.GetString("nc-city-map-edit-district-deleted", ("path", SavePath(atlas).ToString()));
        return true;
    }

    #endregion

    #region Geometry

    private static Vector2i ToTile(Vector2 position) =>
        new((int) MathF.Floor(position.X), (int) MathF.Floor(position.Y));

    /// <summary>A label point may sit on a wall or doorway; the nearest tile of a building around it counts.</summary>
    private static bool TryFindBuilding(NCCityBuildingIndex index, Vector2 position, out int building)
    {
        var tile = ToTile(position);
        if (index.TryGetBuilding(tile, out building))
            return true;

        for (var radius = 1; radius <= 2; radius++)
        {
            for (var x = -radius; x <= radius; x++)
            {
                for (var y = -radius; y <= radius; y++)
                {
                    if (Math.Max(Math.Abs(x), Math.Abs(y)) == radius && index.TryGetBuilding(tile + new Vector2i(x, y), out building))
                        return true;
                }
            }
        }

        building = default;
        return false;
    }

    public static bool Contains(IReadOnlyList<Vector2> polygon, Vector2 point)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            if (a.Y > point.Y != b.Y > point.Y &&
                point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static List<NCCityMapTileChunk> ToChunks(List<Vector2i> tiles)
    {
        const int size = 32;
        var chunks = new Dictionary<Vector2i, ulong[]>();
        foreach (var tile in tiles)
        {
            var origin = new Vector2i(FloorDiv(tile.X, size) * size, FloorDiv(tile.Y, size) * size);
            if (!chunks.TryGetValue(origin, out var rows))
                chunks[origin] = rows = new ulong[size];

            rows[tile.Y - origin.Y] |= 1UL << (tile.X - origin.X);
        }

        return chunks.Select(pair => new NCCityMapTileChunk(pair.Key.X, pair.Key.Y, pair.Value.ToList())).ToList();
    }

    private static int FloorDiv(int value, int divisor) =>
        value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);

    #endregion
}
