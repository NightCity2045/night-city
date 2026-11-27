// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace Content.Server._NC.Housing;

/// <summary>What one grid tile is to the property flood fill.</summary>
public enum NCPropertyTileKind : byte
{
    /// <summary>No floor: open space or the map edge. A fill that reaches it is not enclosed.</summary>
    Empty,
    Floor,
    Wall,
    Door,
}

/// <summary>
/// Flood fill over grid tiles for rentable properties. A property is the floor reachable from its markers up to
/// walls and doors, plus back rooms whose doors all open into it (bathrooms, store rooms); the doors left on its
/// edge are the property's own doors.
/// </summary>
public sealed class NCPropertyIndex
{
    /// <summary>A marker whose fill grows past this is standing in the street or a corridor.</summary>
    public const int MaximumTiles = 2000;

    /// <summary>A back room bigger than this is not a back room.</summary>
    public const int MaximumBackRoomTiles = 400;

    private static readonly Vector2i[] Neighbours = [new(1, 0), new(-1, 0), new(0, 1), new(0, -1)];

    private readonly Func<Vector2i, (NCPropertyTileKind Kind, EntityUid? Door)> _classify;
    private readonly Dictionary<Vector2i, (NCPropertyTileKind Kind, EntityUid? Door)> _cache = new();

    public NCPropertyIndex(Func<Vector2i, (NCPropertyTileKind Kind, EntityUid? Door)> classify)
    {
        _classify = classify;
    }

    public (NCPropertyTileKind Kind, EntityUid? Door) Classify(Vector2i tile)
    {
        if (!_cache.TryGetValue(tile, out var result))
        {
            result = _classify(tile);
            _cache[tile] = result;
        }

        return result;
    }

    /// <summary>The floor reachable from a tile without crossing walls or doors, and the doors on its edge.</summary>
    public bool TryFill(Vector2i start, int limit, out HashSet<Vector2i> tiles, out Dictionary<Vector2i, EntityUid> doors)
    {
        tiles = new HashSet<Vector2i>();
        doors = new Dictionary<Vector2i, EntityUid>();
        if (Classify(start).Kind != NCPropertyTileKind.Floor)
            return false;

        var queue = new Queue<Vector2i>();
        queue.Enqueue(start);
        tiles.Add(start);
        while (queue.TryDequeue(out var tile))
        {
            foreach (var step in Neighbours)
            {
                var next = tile + step;
                if (tiles.Contains(next))
                    continue;

                var (kind, door) = Classify(next);
                switch (kind)
                {
                    case NCPropertyTileKind.Empty:
                        return false;
                    case NCPropertyTileKind.Door when door is { } uid:
                        doors[next] = uid;
                        break;
                    case NCPropertyTileKind.Floor:
                        if (tiles.Count >= limit)
                            return false;
                        tiles.Add(next);
                        queue.Enqueue(next);
                        break;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Grows a property by the back rooms behind its doors. Returns the doors left on the edge of the property,
    /// keyed by tile: those are the ones that lock.
    /// </summary>
    public Dictionary<Vector2i, EntityUid> AddBackRooms(HashSet<Vector2i> tiles, Dictionary<Vector2i, EntityUid> doors)
    {
        var rejected = new HashSet<Vector2i>();
        bool changed;
        do
        {
            changed = false;
            foreach (var doorTile in doors.Keys.ToList())
            {
                foreach (var step in Neighbours)
                {
                    var beyond = doorTile + step;
                    if (tiles.Contains(beyond) || rejected.Contains(beyond) || Classify(beyond).Kind != NCPropertyTileKind.Floor)
                        continue;

                    if (!TryFill(beyond, MaximumBackRoomTiles, out var room, out var roomDoors) ||
                        roomDoors.Keys.Any(door => !doors.ContainsKey(door)))
                    {
                        rejected.Add(beyond);
                        continue;
                    }

                    tiles.UnionWith(room);
                    changed = true;
                }
            }
        }
        while (changed);

        var outer = new Dictionary<Vector2i, EntityUid>();
        foreach (var (tile, door) in doors)
        {
            foreach (var step in Neighbours)
            {
                var next = tile + step;
                var kind = Classify(next).Kind;
                if (kind == NCPropertyTileKind.Empty || kind == NCPropertyTileKind.Floor && !tiles.Contains(next))
                {
                    outer[tile] = door;
                    break;
                }
            }
        }

        return outer;
    }
}
