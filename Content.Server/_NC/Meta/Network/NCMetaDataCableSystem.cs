// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Meta.Network;
using Content.Shared.Wires;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._NC.Meta.Network;

/// <summary>
/// Physical META data cable. Anchored pieces on neighbouring tiles form a bus; a device port attaches to the pieces on
/// its own tile and on the four tiles beside it. The graph is rebuilt lazily after the pieces change.
/// </summary>
public sealed class NCMetaDataCableSystem : EntitySystem
{
    private const int MaximumPieces = 8192;
    private const int MaximumSearch = 2048;
    private static readonly Vector2i[] Cardinals = [new(0, 1), new(0, -1), new(1, 0), new(-1, 0)];
    private static readonly Vector2i[] Attachments = [new(0, 0), new(0, 1), new(0, -1), new(1, 0), new(-1, 0)];

    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;

    private readonly Dictionary<(EntityUid Grid, Vector2i Tile), EntityUid> _pieces = [];
    private readonly Dictionary<EntityUid, (EntityUid Grid, Vector2i Tile)> _locations = [];
    private readonly Dictionary<EntityUid, int> _busOf = [];
    private bool _dirty;

    /// <summary>Raised after a piece is laid, cut or moved. Subscribers queue bounded work instead of re-querying routes.</summary>
    public event Action? Changed;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCMetaDataCableComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<NCMetaDataCableComponent, AnchorStateChangedEvent>(OnAnchorStateChanged);
        SubscribeLocalEvent<NCMetaDataCableComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnStartup(Entity<NCMetaDataCableComponent> piece, ref ComponentStartup args) => TryAddPiece(piece.Owner);

    private void OnAnchorStateChanged(Entity<NCMetaDataCableComponent> piece, ref AnchorStateChangedEvent args)
    {
        if (args.Anchored)
            TryAddPiece(piece.Owner);
        else
            RemovePiece(piece.Owner);
    }

    private void OnShutdown(Entity<NCMetaDataCableComponent> piece, ref ComponentShutdown args) => RemovePiece(piece.Owner);

    private void TryAddPiece(EntityUid piece)
    {
        var xform = Transform(piece);
        if (!xform.Anchored || xform.GridUid is not { } grid || !TryComp<MapGridComponent>(grid, out var gridComponent) ||
            _locations.ContainsKey(piece) || _pieces.Count >= MaximumPieces)
            return;

        var tile = _map.TileIndicesFor((grid, gridComponent), xform.Coordinates);
        if (!_pieces.TryAdd((grid, tile), piece))
            return;

        _locations[piece] = (grid, tile);
        OnPiecesChanged(grid, tile);
    }

    private void RemovePiece(EntityUid piece)
    {
        if (!_locations.Remove(piece, out var location))
            return;

        _pieces.Remove(location);
        OnPiecesChanged(location.Grid, location.Tile);
    }

    private void OnPiecesChanged(EntityUid grid, Vector2i tile)
    {
        _dirty = true;
        UpdateAppearance(grid, tile);
        foreach (var offset in Cardinals)
            UpdateAppearance(grid, tile + offset);
        Changed?.Invoke();
    }

    private void UpdateAppearance(EntityUid grid, Vector2i tile)
    {
        if (!_pieces.TryGetValue((grid, tile), out var piece) || !Exists(piece))
            return;

        var mask = WireVisDirFlags.None;
        if (_pieces.ContainsKey((grid, tile + new Vector2i(0, 1)))) mask |= WireVisDirFlags.North;
        if (_pieces.ContainsKey((grid, tile + new Vector2i(0, -1)))) mask |= WireVisDirFlags.South;
        if (_pieces.ContainsKey((grid, tile + new Vector2i(1, 0)))) mask |= WireVisDirFlags.East;
        if (_pieces.ContainsKey((grid, tile + new Vector2i(-1, 0)))) mask |= WireVisDirFlags.West;
        _appearance.SetData(piece, WireVisVisuals.ConnectedMask, mask);
    }

    private void EnsureGraph()
    {
        if (!_dirty)
            return;

        _dirty = false;
        _busOf.Clear();
        var next = 0;
        var pending = new Stack<EntityUid>();
        foreach (var piece in _pieces.Values)
        {
            if (!_busOf.TryAdd(piece, next))
                continue;

            pending.Push(piece);
            while (pending.TryPop(out var current))
            {
                var (grid, tile) = _locations[current];
                foreach (var offset in Cardinals)
                {
                    if (_pieces.TryGetValue((grid, tile + offset), out var neighbor) && _busOf.TryAdd(neighbor, next))
                        pending.Push(neighbor);
                }
            }

            next++;
        }
    }

    private List<EntityUid> AttachedPieces(EntityUid port)
    {
        var result = new List<EntityUid>();
        var xform = Transform(port);
        if (xform.GridUid is not { } grid || !TryComp<MapGridComponent>(grid, out var gridComponent))
            return result;

        var tile = _map.TileIndicesFor((grid, gridComponent), xform.Coordinates);
        foreach (var offset in Attachments)
        {
            if (_pieces.TryGetValue((grid, tile + offset), out var piece))
                result.Add(piece);
        }

        return result;
    }

    /// <summary>True when both ports attach to the same physical data bus.</summary>
    public bool AreConnected(EntityUid a, EntityUid b)
    {
        EnsureGraph();
        var left = AttachedPieces(a);
        if (left.Count == 0)
            return false;

        var right = AttachedPieces(b);
        foreach (var first in left)
        {
            if (!_busOf.TryGetValue(first, out var bus))
                continue;

            foreach (var second in right)
            {
                if (_busOf.TryGetValue(second, out var other) && other == bus)
                    return true;
            }
        }

        return false;
    }

    /// <summary>Number of cable tiles on the shortest path between two ports, bounded by a search cap.</summary>
    public bool TryGetDistance(EntityUid a, EntityUid b, out int tiles)
    {
        tiles = 0;
        EnsureGraph();
        var starts = AttachedPieces(a);
        var goals = new HashSet<EntityUid>(AttachedPieces(b));
        if (starts.Count == 0 || goals.Count == 0)
            return false;

        var distance = new Dictionary<EntityUid, int>();
        var queue = new Queue<EntityUid>();
        foreach (var start in starts)
        {
            distance[start] = 1;
            queue.Enqueue(start);
        }

        while (queue.TryDequeue(out var current))
        {
            if (goals.Contains(current))
            {
                tiles = distance[current];
                return true;
            }

            if (distance.Count > MaximumSearch)
                return false;

            var (grid, tile) = _locations[current];
            foreach (var offset in Cardinals)
            {
                if (_pieces.TryGetValue((grid, tile + offset), out var neighbor) && distance.TryAdd(neighbor, distance[current] + 1))
                    queue.Enqueue(neighbor);
            }
        }

        return false;
    }
}
