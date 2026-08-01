// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Prototypes;
using Content.Shared._NC.Coordinates;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.City.Zones;

/// <summary>
/// Validates city-zone definitions and provides indexed coordinate queries.
/// It never performs entity-wide scans or per-frame zone updates.
/// </summary>
public sealed partial class NCZoneSystem : EntitySystem
{
    private const float IndexCellSize = 16f;
    private const int MaxVolumeDepthSpan = 256;

    [Dependency] private IPrototypeManager _prototypes = default!;

    private readonly Dictionary<ZoneCell, List<RuntimeZone>> _spatialIndex = new();
    // Global geometry is indexed without Z so newly added floors require no index rebuild.
    private readonly Dictionary<GlobalZoneCell, List<RuntimeZone>> _globalSpatialIndex = new();
    private readonly Dictionary<NCZoneId, RuntimeZone> _zonesById = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        RebuildIndex();
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<NCZoneSetPrototype>() || args.WasModified<NCZoneKindPrototype>())
            RebuildIndex();
    }

    /// <summary>
    /// Returns all zones containing a coordinate, ordered from broadest hierarchy rank to most specific.
    /// </summary>
    public void GetZones(NCMapCoordinates coordinates, List<NCZoneInfo> result)
    {
        result.Clear();

        var added = new HashSet<NCZoneId>();
        if (_spatialIndex.TryGetValue(GetCell(coordinates), out var floorCandidates))
            AddMatchingZones(floorCandidates, coordinates, added, result);
        if (_globalSpatialIndex.TryGetValue(GetGlobalCell(coordinates), out var globalCandidates))
            AddMatchingZones(globalCandidates, coordinates, added, result);

        result.Sort(static (left, right) =>
        {
            var rank = left.HierarchyRank.CompareTo(right.HierarchyRank);
            if (rank != 0)
                return rank;

            var priority = right.Priority.CompareTo(left.Priority);
            return priority != 0
                ? priority
                : left.Id.Value.CompareTo(right.Id.Value);
        });
    }

    private static void AddMatchingZones(
        IEnumerable<RuntimeZone> candidates,
        NCMapCoordinates coordinates,
        HashSet<NCZoneId> added,
        List<NCZoneInfo> result)
    {
        foreach (var candidate in candidates)
        {
            if (!added.Add(candidate.Definition.Id) ||
                !Contains(candidate.Definition, coordinates))
            {
                continue;
            }

            result.Add(ToInfo(candidate));
        }
    }

    public bool TryGetZone(NCZoneId zoneId, out NCZoneInfo zone)
    {
        if (_zonesById.TryGetValue(zoneId, out var runtime))
        {
            zone = ToInfo(runtime);
            return true;
        }

        zone = default;
        return false;
    }

    /// <summary>
    /// Resolves a generic zone identity as a district and rejects identities belonging to another city level.
    /// </summary>
    public bool TryGetDistrictId(NCZoneId zoneId, out NCDistrictId districtId)
    {
        if (HasContextSlot(zoneId, NCZoneContextSlot.District))
        {
            districtId = new NCDistrictId(zoneId);
            return true;
        }

        districtId = default;
        return false;
    }

    public bool TryGetSectorId(NCZoneId zoneId, out NCSectorId sectorId)
    {
        if (HasContextSlot(zoneId, NCZoneContextSlot.Sector))
        {
            sectorId = new NCSectorId(zoneId);
            return true;
        }

        sectorId = default;
        return false;
    }

    public bool TryGetStreetId(NCZoneId zoneId, out NCStreetId streetId)
    {
        if (HasContextSlot(zoneId, NCZoneContextSlot.Street))
        {
            streetId = new NCStreetId(zoneId);
            return true;
        }

        streetId = default;
        return false;
    }

    public bool TryGetBuildingId(NCZoneId zoneId, out NCBuildingId buildingId)
    {
        if (HasContextSlot(zoneId, NCZoneContextSlot.Building))
        {
            buildingId = new NCBuildingId(zoneId);
            return true;
        }

        buildingId = default;
        return false;
    }

    public bool TryGetApartmentId(NCZoneId zoneId, out NCApartmentId apartmentId)
    {
        if (HasContextSlot(zoneId, NCZoneContextSlot.Apartment))
        {
            apartmentId = new NCApartmentId(zoneId);
            return true;
        }

        apartmentId = default;
        return false;
    }

    public bool TryGetDistrict(NCDistrictId id, out NCZoneInfo district) =>
        TryGetTypedZone(id.ZoneId, NCZoneContextSlot.District, out district);

    public bool TryGetSector(NCSectorId id, out NCZoneInfo sector) =>
        TryGetTypedZone(id.ZoneId, NCZoneContextSlot.Sector, out sector);

    public bool TryGetStreet(NCStreetId id, out NCZoneInfo street) =>
        TryGetTypedZone(id.ZoneId, NCZoneContextSlot.Street, out street);

    public bool TryGetBuilding(NCBuildingId id, out NCZoneInfo building) =>
        TryGetTypedZone(id.ZoneId, NCZoneContextSlot.Building, out building);

    public bool TryGetApartment(NCApartmentId id, out NCZoneInfo apartment) =>
        TryGetTypedZone(id.ZoneId, NCZoneContextSlot.Apartment, out apartment);

    private bool HasContextSlot(NCZoneId zoneId, NCZoneContextSlot slot)
    {
        return _zonesById.TryGetValue(zoneId, out var zone) && zone.Kind.ContextSlot == slot;
    }

    private bool TryGetTypedZone(NCZoneId zoneId, NCZoneContextSlot slot, out NCZoneInfo zone)
    {
        if (_zonesById.TryGetValue(zoneId, out var runtime) && runtime.Kind.ContextSlot == slot)
        {
            zone = ToInfo(runtime);
            return true;
        }

        zone = default;
        return false;
    }

    /// <summary>
    /// Resolves a stable coordinate to one primary zone per semantic hierarchy slot.
    /// Higher-priority overlapping zones win because GetZones returns them first within each rank.
    /// </summary>
    public NCCityLocationContext GetLocationContext(NCMapCoordinates coordinates)
    {
        var zones = new List<NCZoneInfo>();
        GetZones(coordinates, zones);

        RuntimeZone? leaf = null;
        foreach (var candidate in zones)
        {
            if (leaf != null && candidate.HierarchyRank <= leaf.Kind.HierarchyRank)
                continue;

            if (_zonesById.TryGetValue(candidate.Id, out var runtime))
                leaf = runtime;
        }

        var district = default(NCDistrictId);
        var sector = default(NCSectorId);
        var street = default(NCStreetId);
        var building = default(NCBuildingId);
        var apartment = default(NCApartmentId);
        var visited = new HashSet<NCZoneId>();
        while (leaf != null && visited.Add(leaf.Definition.Id))
        {
            switch (leaf.Kind.ContextSlot)
            {
                case NCZoneContextSlot.District:
                    district = new NCDistrictId(leaf.Definition.Id);
                    break;
                case NCZoneContextSlot.Sector:
                    sector = new NCSectorId(leaf.Definition.Id);
                    break;
                case NCZoneContextSlot.Street:
                    street = new NCStreetId(leaf.Definition.Id);
                    break;
                case NCZoneContextSlot.Building:
                    building = new NCBuildingId(leaf.Definition.Id);
                    break;
                case NCZoneContextSlot.Apartment:
                    apartment = new NCApartmentId(leaf.Definition.Id);
                    break;
            }

            leaf = leaf.Definition.Parent.IsValid &&
                   _zonesById.TryGetValue(leaf.Definition.Parent, out var parent)
                ? parent
                : null;
        }

        return new NCCityLocationContext(
            coordinates.NetworkId,
            coordinates.Z,
            district,
            sector,
            street,
            building,
            apartment);
    }

    public bool TryGetActivityMode(NCZoneId zoneId, out NCZoneActivityMode mode)
    {
        if (_zonesById.TryGetValue(zoneId, out var zone) && zone.Kind.SupportsActivityMode)
        {
            mode = zone.ActivityMode;
            return true;
        }

        mode = default;
        return false;
    }

    /// <summary>
    /// Returns the most restrictive activity mode inherited from the current district and sector.
    /// Consumers can use this without retaining zone runtime objects.
    /// </summary>
    public NCZoneActivityMode GetEffectiveActivityMode(NCCityLocationContext context)
    {
        var mode = NCZoneActivityMode.Active;
        if (TryGetActivityMode(context.DistrictId.ZoneId, out var district))
            mode = (NCZoneActivityMode) Math.Max((byte) mode, (byte) district);
        if (TryGetActivityMode(context.SectorId.ZoneId, out var sector))
            mode = (NCZoneActivityMode) Math.Max((byte) mode, (byte) sector);
        return mode;
    }

    /// <summary>
    /// Changes the runtime simulation mode of a district or sector.
    /// Persistence policy can store this stable zone ID without retaining prototype or component references.
    /// </summary>
    public bool SetActivityMode(NCZoneId zoneId, NCZoneActivityMode mode)
    {
        if (!_zonesById.TryGetValue(zoneId, out var zone) ||
            !zone.Kind.SupportsActivityMode ||
            zone.ActivityMode == mode)
        {
            return false;
        }

        var oldMode = zone.ActivityMode;
        zone.ActivityMode = mode;
        var ev = new NCZoneActivityChangedEvent(zoneId, oldMode, mode);
        RaiseLocalEvent(ref ev);
        return true;
    }

    /// <summary>
    /// Validates all loaded zone sets without mutating runtime state.
    /// </summary>
    public bool ValidateAll(List<NCZoneValidationError> errors)
    {
        errors.Clear();
        var globalOwners = new Dictionary<NCZoneId, string>();
        var networkOwners = new Dictionary<NCZNetworkId, string>();

        foreach (var zoneSet in _prototypes.EnumeratePrototypes<NCZoneSetPrototype>())
        {
            if (zoneSet.NetworkId.IsValid && !networkOwners.TryAdd(zoneSet.NetworkId, zoneSet.ID))
            {
                errors.Add(new NCZoneValidationError(
                    zoneSet.ID,
                    default,
                    $"NCZNetworkId is already assigned to zone set {networkOwners[zoneSet.NetworkId]}."));
            }

            ValidateZoneSet(zoneSet, globalOwners, errors);
        }

        return errors.Count == 0;
    }

    private void RebuildIndex()
    {
        var previousActivity = _zonesById.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ActivityMode);
        _spatialIndex.Clear();
        _globalSpatialIndex.Clear();
        _zonesById.Clear();

        var errors = new List<NCZoneValidationError>();
        if (!ValidateAll(errors))
        {
            foreach (var error in errors)
                Log.Error($"Zone set {error.ZoneSet}, zone {error.Zone}: {error.Message}");
        }

        // One malformed zone set must not disable unrelated city networks.
        var invalidSets = errors.Select(error => error.ZoneSet).ToHashSet();
        foreach (var zoneSet in _prototypes.EnumeratePrototypes<NCZoneSetPrototype>())
        {
            if (invalidSets.Contains(zoneSet.ID))
                continue;

            foreach (var definition in zoneSet.Zones)
            {
                var kind = _prototypes.Index(definition.Kind);
                var runtime = new RuntimeZone(zoneSet.NetworkId, definition, kind);
                if (kind.SupportsActivityMode &&
                    previousActivity.TryGetValue(definition.Id, out var activity))
                {
                    runtime.ActivityMode = activity;
                }

                _zonesById.Add(definition.Id, runtime);

                foreach (var geometry in definition.Geometry)
                    AddGeometryToIndex(runtime, geometry);
            }
        }

        var ev = new NCZoneIndexRebuiltEvent();
        RaiseLocalEvent(ref ev);
    }

    private void ValidateZoneSet(
        NCZoneSetPrototype zoneSet,
        Dictionary<NCZoneId, string> globalOwners,
        List<NCZoneValidationError> errors)
    {
        if (!zoneSet.NetworkId.IsValid)
        {
            errors.Add(new NCZoneValidationError(zoneSet.ID, default, "Zone set has an empty NCZNetworkId."));
            return;
        }

        var localZones = new Dictionary<NCZoneId, NCZoneDefinition>();
        foreach (var zone in zoneSet.Zones)
        {
            if (!zone.Id.IsValid)
            {
                errors.Add(new NCZoneValidationError(zoneSet.ID, zone.Id, "Zone has an empty NCZoneId."));
                continue;
            }

            if (!localZones.TryAdd(zone.Id, zone))
                errors.Add(new NCZoneValidationError(zoneSet.ID, zone.Id, "NCZoneId is duplicated inside this zone set."));

            if (!globalOwners.TryAdd(zone.Id, zoneSet.ID))
            {
                errors.Add(new NCZoneValidationError(
                    zoneSet.ID,
                    zone.Id,
                    $"NCZoneId is already used by zone set {globalOwners[zone.Id]}."));
            }

            if (!_prototypes.TryIndex(zone.Kind, out var kind))
            {
                errors.Add(new NCZoneValidationError(zoneSet.ID, zone.Id, $"Unknown zone kind {zone.Kind}."));
                continue;
            }

            if (zone.Geometry.Count == 0)
                errors.Add(new NCZoneValidationError(zoneSet.ID, zone.Id, "Zone has no geometry."));

            foreach (var geometry in zone.Geometry)
                ValidateGeometry(zoneSet.ID, zone, kind, geometry, errors);
        }

        foreach (var zone in localZones.Values)
        {
            var kind = _prototypes.Index(zone.Kind);
            if (!zone.Parent.IsValid)
            {
                if (kind.RequiresParent)
                    errors.Add(new NCZoneValidationError(zoneSet.ID, zone.Id, $"{zone.Kind} requires a parent zone."));

                continue;
            }

            if (!localZones.TryGetValue(zone.Parent, out var parent))
            {
                errors.Add(new NCZoneValidationError(zoneSet.ID, zone.Id, $"Parent zone {zone.Parent} does not exist."));
                continue;
            }

            if (!kind.AllowedParents.Contains(parent.Kind))
            {
                errors.Add(new NCZoneValidationError(
                    zoneSet.ID,
                    zone.Id,
                    $"{zone.Kind} cannot be a child of {parent.Kind}."));
            }

            ValidateParentCycle(zoneSet.ID, zone, localZones, errors);
        }
    }

    private static void ValidateParentCycle(
        string zoneSet,
        NCZoneDefinition zone,
        Dictionary<NCZoneId, NCZoneDefinition> zones,
        List<NCZoneValidationError> errors)
    {
        var visited = new HashSet<NCZoneId> { zone.Id };
        var parentId = zone.Parent;

        while (parentId.IsValid && zones.TryGetValue(parentId, out var parent))
        {
            if (!visited.Add(parentId))
            {
                errors.Add(new NCZoneValidationError(zoneSet, zone.Id, "Zone hierarchy contains a cycle."));
                return;
            }

            parentId = parent.Parent;
        }
    }

    private static void ValidateGeometry(
        string zoneSet,
        NCZoneDefinition zone,
        NCZoneKindPrototype kind,
        NCZoneGeometry geometry,
        List<NCZoneValidationError> errors)
    {
        if (!kind.AllowedGeometry.Contains(geometry.Kind))
        {
            errors.Add(new NCZoneValidationError(
                zoneSet,
                zone.Id,
                $"{zone.Kind} does not allow {geometry.Kind} geometry."));
            return;
        }

        switch (geometry.Kind)
        {
            case NCZoneGeometryKind.Polygon:
                ValidatePolygon(zoneSet, zone.Id, geometry.Vertices, errors);
                break;
            case NCZoneGeometryKind.Volume:
                ValidatePolygon(zoneSet, zone.Id, geometry.Vertices, errors);
                if (!geometry.Global && geometry.MinZ > geometry.MaxZ)
                    errors.Add(new NCZoneValidationError(zoneSet, zone.Id, "Volume MinZ is greater than MaxZ."));
                if (!geometry.Global &&
                    (long) geometry.MaxZ - geometry.MinZ > MaxVolumeDepthSpan)
                    errors.Add(new NCZoneValidationError(zoneSet, zone.Id, "Volume spans too many Z-levels."));
                break;
            case NCZoneGeometryKind.TileMask:
                if (geometry.Global)
                    errors.Add(new NCZoneValidationError(zoneSet, zone.Id, "Tile-mask geometry cannot be global."));
                if (geometry.Chunks.Count == 0)
                    errors.Add(new NCZoneValidationError(zoneSet, zone.Id, "Tile-mask has no chunks."));

                var chunks = new HashSet<(int Z, Vector2i Origin)>();
                foreach (var chunk in geometry.Chunks)
                {
                    if (!chunks.Add((chunk.Z, chunk.Origin)))
                        errors.Add(new NCZoneValidationError(zoneSet, zone.Id, $"Duplicate tile-mask chunk at {chunk.Origin}, Z={chunk.Z}."));
                    if (chunk.Rows.Count is 0 or > NCTileMaskChunk.Size)
                        errors.Add(new NCZoneValidationError(zoneSet, zone.Id, "Tile-mask chunk must contain 1 to 32 rows."));
                }
                break;
        }
    }

    private static void ValidatePolygon(
        string zoneSet,
        NCZoneId zoneId,
        List<Vector2> vertices,
        List<NCZoneValidationError> errors)
    {
        if (vertices.Count < 3)
        {
            errors.Add(new NCZoneValidationError(zoneSet, zoneId, "Polygon requires at least three vertices."));
            return;
        }

        for (var i = 0; i < vertices.Count; i++)
        {
            if (!float.IsFinite(vertices[i].X) || !float.IsFinite(vertices[i].Y))
            {
                errors.Add(new NCZoneValidationError(zoneSet, zoneId, "Polygon contains a non-finite vertex."));
                return;
            }

            var nextI = (i + 1) % vertices.Count;
            if (vertices[i] == vertices[nextI])
            {
                errors.Add(new NCZoneValidationError(zoneSet, zoneId, "Polygon contains a zero-length edge."));
                return;
            }

            for (var j = i + 1; j < vertices.Count; j++)
            {
                var nextJ = (j + 1) % vertices.Count;
                if (i == j || nextI == j || nextJ == i)
                    continue;

                if (SegmentsIntersect(vertices[i], vertices[nextI], vertices[j], vertices[nextJ]))
                {
                    errors.Add(new NCZoneValidationError(zoneSet, zoneId, "Polygon intersects itself."));
                    return;
                }
            }
        }

        var twiceArea = 0f;
        for (var i = 0; i < vertices.Count; i++)
        {
            var next = vertices[(i + 1) % vertices.Count];
            twiceArea += vertices[i].X * next.Y - next.X * vertices[i].Y;
        }

        if (MathF.Abs(twiceArea) < 0.0001f)
            errors.Add(new NCZoneValidationError(zoneSet, zoneId, "Polygon has zero area."));
    }

    private void AddGeometryToIndex(RuntimeZone zone, NCZoneGeometry geometry)
    {
        if (geometry.Global)
        {
            if (geometry.Kind is NCZoneGeometryKind.Polygon or NCZoneGeometryKind.Volume)
                AddGlobalBounds(zone, GetPolygonBounds(geometry.Vertices));
            return;
        }

        switch (geometry.Kind)
        {
            case NCZoneGeometryKind.Polygon:
                AddBounds(zone, geometry.Z, geometry.Z, GetPolygonBounds(geometry.Vertices));
                break;
            case NCZoneGeometryKind.Volume:
                AddBounds(zone, geometry.MinZ, geometry.MaxZ, GetPolygonBounds(geometry.Vertices));
                break;
            case NCZoneGeometryKind.TileMask:
                foreach (var chunk in geometry.Chunks)
                {
                    AddBounds(
                        zone,
                        chunk.Z,
                        chunk.Z,
                        new Box2(chunk.Origin.X, chunk.Origin.Y,
                            chunk.Origin.X + NCTileMaskChunk.Size,
                            chunk.Origin.Y + NCTileMaskChunk.Size));
                }
                break;
        }
    }

    private void AddGlobalBounds(RuntimeZone zone, Box2 bounds)
    {
        var minX = (int) MathF.Floor(bounds.Left / IndexCellSize);
        var maxX = (int) MathF.Floor(bounds.Right / IndexCellSize);
        var minY = (int) MathF.Floor(bounds.Bottom / IndexCellSize);
        var maxY = (int) MathF.Floor(bounds.Top / IndexCellSize);

        for (var x = minX; x <= maxX; x++)
        {
            for (var y = minY; y <= maxY; y++)
            {
                var cell = new GlobalZoneCell(zone.NetworkId, x, y);
                if (!_globalSpatialIndex.TryGetValue(cell, out var zones))
                    _globalSpatialIndex[cell] = zones = [];

                if (!zones.Contains(zone))
                    zones.Add(zone);
            }
        }
    }

    private void AddBounds(RuntimeZone zone, int minZ, int maxZ, Box2 bounds)
    {
        var minX = (int) MathF.Floor(bounds.Left / IndexCellSize);
        var maxX = (int) MathF.Floor(bounds.Right / IndexCellSize);
        var minY = (int) MathF.Floor(bounds.Bottom / IndexCellSize);
        var maxY = (int) MathF.Floor(bounds.Top / IndexCellSize);

        for (var z = minZ; z <= maxZ; z++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                for (var y = minY; y <= maxY; y++)
                {
                    var cell = new ZoneCell(zone.NetworkId, z, x, y);
                    if (!_spatialIndex.TryGetValue(cell, out var zones))
                        _spatialIndex[cell] = zones = [];

                    if (!zones.Contains(zone))
                        zones.Add(zone);
                }
            }
        }
    }

    private static bool Contains(NCZoneDefinition zone, NCMapCoordinates coordinates)
    {
        foreach (var geometry in zone.Geometry)
        {
            switch (geometry.Kind)
            {
                case NCZoneGeometryKind.Polygon when
                    (geometry.Global || geometry.Z == coordinates.Z) &&
                    PointInPolygon(coordinates.Position, geometry.Vertices):
                case NCZoneGeometryKind.Volume when
                    (geometry.Global ||
                     coordinates.Z >= geometry.MinZ &&
                     coordinates.Z <= geometry.MaxZ) &&
                    PointInPolygon(coordinates.Position, geometry.Vertices):
                    return true;
                case NCZoneGeometryKind.TileMask when TileMaskContains(coordinates, geometry.Chunks):
                    return true;
            }
        }

        return false;
    }

    private static bool TileMaskContains(NCMapCoordinates coordinates, List<NCTileMaskChunk> chunks)
    {
        var tileX = (int) MathF.Floor(coordinates.Position.X);
        var tileY = (int) MathF.Floor(coordinates.Position.Y);

        foreach (var chunk in chunks)
        {
            if (chunk.Z != coordinates.Z)
                continue;

            var localX = tileX - chunk.Origin.X;
            var localY = tileY - chunk.Origin.Y;
            if (localX < 0 || localX >= NCTileMaskChunk.Size ||
                localY < 0 || localY >= chunk.Rows.Count)
            {
                continue;
            }

            if ((chunk.Rows[localY] & (1UL << localX)) != 0)
                return true;
        }

        return false;
    }

    private static bool PointInPolygon(Vector2 point, List<Vector2> vertices)
    {
        var inside = false;
        for (var i = 0; i < vertices.Count; i++)
        {
            var j = i == 0 ? vertices.Count - 1 : i - 1;
            var a = vertices[j];
            var b = vertices[i];

            if (PointOnSegment(point, a, b))
                return true;

            if ((a.Y > point.Y) == (b.Y > point.Y))
                continue;

            var intersectionX = (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X;
            if (point.X < intersectionX)
                inside = !inside;
        }

        return inside;
    }

    private static bool PointOnSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        const float epsilon = 0.0001f;
        var cross = (point.Y - start.Y) * (end.X - start.X) -
                    (point.X - start.X) * (end.Y - start.Y);
        if (MathF.Abs(cross) > epsilon)
            return false;

        var dot = Vector2.Dot(point - start, end - start);
        return dot >= -epsilon && dot <= Vector2.DistanceSquared(start, end) + epsilon;
    }

    private static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        const float epsilon = 0.0001f;

        static float Cross(Vector2 p, Vector2 q, Vector2 r)
        {
            return (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
        }

        var o1 = Cross(a, b, c);
        var o2 = Cross(a, b, d);
        var o3 = Cross(c, d, a);
        var o4 = Cross(c, d, b);

        if (MathF.Sign(o1) != MathF.Sign(o2) && MathF.Sign(o3) != MathF.Sign(o4))
            return true;

        return MathF.Abs(o1) <= epsilon && PointOnSegment(c, a, b) ||
               MathF.Abs(o2) <= epsilon && PointOnSegment(d, a, b) ||
               MathF.Abs(o3) <= epsilon && PointOnSegment(a, c, d) ||
               MathF.Abs(o4) <= epsilon && PointOnSegment(b, c, d);
    }

    private static Box2 GetPolygonBounds(List<Vector2> vertices)
    {
        var min = vertices[0];
        var max = vertices[0];
        foreach (var vertex in vertices)
        {
            min = Vector2.Min(min, vertex);
            max = Vector2.Max(max, vertex);
        }

        return new Box2(min, max);
    }

    private static ZoneCell GetCell(NCMapCoordinates coordinates)
    {
        return new ZoneCell(
            coordinates.NetworkId,
            coordinates.Z,
            (int) MathF.Floor(coordinates.Position.X / IndexCellSize),
            (int) MathF.Floor(coordinates.Position.Y / IndexCellSize));
    }

    private static GlobalZoneCell GetGlobalCell(NCMapCoordinates coordinates)
    {
        return new GlobalZoneCell(
            coordinates.NetworkId,
            (int) MathF.Floor(coordinates.Position.X / IndexCellSize),
            (int) MathF.Floor(coordinates.Position.Y / IndexCellSize));
    }

    private static NCZoneInfo ToInfo(RuntimeZone zone)
    {
        return new NCZoneInfo(
            zone.Definition.Id,
            zone.Definition.Kind,
            zone.Definition.Parent,
            zone.Definition.Name,
            zone.Definition.Priority,
            zone.Kind.HierarchyRank,
            zone.Kind.ContextSlot,
            zone.ActivityMode);
    }

    private sealed class RuntimeZone
    {
        public readonly NCZNetworkId NetworkId;
        public readonly NCZoneDefinition Definition;
        public readonly NCZoneKindPrototype Kind;
        public NCZoneActivityMode ActivityMode;

        public RuntimeZone(
            NCZNetworkId networkId,
            NCZoneDefinition definition,
            NCZoneKindPrototype kind)
        {
            NetworkId = networkId;
            Definition = definition;
            Kind = kind;
            ActivityMode = definition.ActivityMode;
        }
    }

    private readonly record struct ZoneCell(NCZNetworkId NetworkId, int Z, int X, int Y);
    private readonly record struct GlobalZoneCell(NCZNetworkId NetworkId, int X, int Y);
}

/// <summary>
/// Notifies opt-in semantic-location caches after zone prototypes are rebuilt.
/// </summary>
[ByRefEvent]
public readonly record struct NCZoneIndexRebuiltEvent;
