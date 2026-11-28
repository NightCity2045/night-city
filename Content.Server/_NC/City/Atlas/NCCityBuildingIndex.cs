// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared.Pinpointer;

namespace Content.Server._NC.City.Atlas;

/// <summary>What the detection saw on one floor, for mappers checking why buildings are missing.</summary>
public sealed class NCCityBuildingStats
{
    public int Grids;
    public int FloorTiles;
    public int WallTiles;
    public int DoorTiles;
    public int StreetSeeds;
    public int StreetTiles;
    public int Regions;
    public int LargestRegion;
    public int DroppedSmall;
    public int DroppedLarge;
}

/// <summary>One detected building on one floor, in world-tile coordinates.</summary>
public sealed class NCCityBuildingFootprint
{
    public readonly List<Vector2i> Tiles = [];
}

/// <summary>
/// Buildings of one floor, detected from NavMap tile data. Open floor reachable from the edge of the paved area
/// is street; walls and doors stop that fill. Everything left enclosed is indoors, and each connected indoor
/// area, joined through its interior doors, is one building.
/// </summary>
public sealed class NCCityBuildingIndex
{
    /// <summary>Enclosed pockets smaller than this are wall cavities or closets, not buildings.</summary>
    public const int MinimumFloorTiles = 4;

    /// <summary>A sealed map edge must not turn the whole street grid into one giant building.</summary>
    public const int MaximumTiles = 6000;

    public readonly Dictionary<Vector2i, int> TileToBuilding = new();
    public readonly List<NCCityBuildingFootprint> Buildings = [];
    public readonly NCCityBuildingStats Stats = new();
    public int Signature;

    private enum TileKind : byte
    {
        Floor,
        Wall,
        Door,
    }

    private static readonly Vector2i[] Neighbours =
    [
        new(1, 0),
        new(-1, 0),
        new(0, 1),
        new(0, -1),
    ];

    public bool TryGetBuilding(Vector2i worldTile, out int building) =>
        TileToBuilding.TryGetValue(worldTile, out building);

    /// <summary>Signature of the NavMap contents; a different value means walls or floors changed.</summary>
    public static int ComputeSignature(NavMapComponent navMap)
    {
        var hash = new HashCode();
        hash.Add(navMap.Chunks.Count);
        foreach (var (origin, chunk) in navMap.Chunks)
        {
            hash.Add(origin);
            hash.Add(chunk.LastUpdate.Value);
        }

        return hash.ToHashCode();
    }

    /// <summary>Adds the buildings of one grid. <paramref name="offset"/> converts grid tiles to world tiles.</summary>
    public void AddGrid(NavMapComponent navMap, Vector2i offset)
    {
        var tiles = new Dictionary<Vector2i, TileKind>();
        foreach (var (origin, chunk) in navMap.Chunks)
        {
            for (var index = 0; index < SharedNavMapSystem.ArraySize; index++)
            {
                var data = chunk.TileData[index];
                if (data == 0)
                    continue;

                var tile = origin * SharedNavMapSystem.ChunkSize + SharedNavMapSystem.GetTileFromIndex(index) + offset;
                tiles[tile] = (data & SharedNavMapSystem.AirlockMask) != 0 ? TileKind.Door :
                    (data & SharedNavMapSystem.WallMask) != 0 ? TileKind.Wall :
                    TileKind.Floor;
            }
        }

        Stats.Grids++;
        foreach (var kind in tiles.Values)
        {
            switch (kind)
            {
                case TileKind.Floor:
                    Stats.FloorTiles++;
                    break;
                case TileKind.Wall:
                    Stats.WallTiles++;
                    break;
                case TileKind.Door:
                    Stats.DoorTiles++;
                    break;
            }
        }

        // Street: open floor flood-filled from every tile that touches the void around the paved area.
        var outdoor = new HashSet<Vector2i>();
        var queue = new Queue<Vector2i>();
        foreach (var (tile, kind) in tiles)
        {
            if (kind != TileKind.Floor)
                continue;

            foreach (var step in Neighbours)
            {
                if (tiles.ContainsKey(tile + step))
                    continue;

                outdoor.Add(tile);
                queue.Enqueue(tile);
                break;
            }
        }

        Stats.StreetSeeds += outdoor.Count;
        while (queue.TryDequeue(out var tile))
        {
            foreach (var step in Neighbours)
            {
                var next = tile + step;
                if (tiles.TryGetValue(next, out var kind) && kind == TileKind.Floor && outdoor.Add(next))
                    queue.Enqueue(next);
            }
        }

        Stats.StreetTiles += outdoor.Count;

        // Buildings: connected enclosed floor, joined through doors. Walls separate buildings.
        var visited = new HashSet<Vector2i>();
        var region = new List<Vector2i>();
        foreach (var (start, startKind) in tiles)
        {
            if (startKind == TileKind.Wall || outdoor.Contains(start) || !visited.Add(start))
                continue;

            region.Clear();
            queue.Enqueue(start);
            var floorTiles = 0;
            while (queue.TryDequeue(out var tile))
            {
                region.Add(tile);
                if (tiles[tile] == TileKind.Floor)
                    floorTiles++;

                foreach (var step in Neighbours)
                {
                    var next = tile + step;
                    if (!tiles.TryGetValue(next, out var kind) || kind == TileKind.Wall ||
                        outdoor.Contains(next) || !visited.Add(next))
                    {
                        continue;
                    }

                    queue.Enqueue(next);
                }
            }

            Stats.Regions++;
            Stats.LargestRegion = Math.Max(Stats.LargestRegion, region.Count);
            if (floorTiles < MinimumFloorTiles)
            {
                Stats.DroppedSmall++;
                continue;
            }

            if (region.Count > MaximumTiles)
            {
                Stats.DroppedLarge++;
                continue;
            }

            var building = new NCCityBuildingFootprint();
            building.Tiles.AddRange(region);
            var id = Buildings.Count;
            Buildings.Add(building);
            foreach (var tile in region)
                TileToBuilding[tile] = id;
        }
    }
}
