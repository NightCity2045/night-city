// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Client._NC.NCUI.Core;
using Robust.Shared.Maths;

namespace Content.Client._NC.NCUI.Spatial;

/// <summary>
/// Incremental uniform grid optimized for frequently moved workbench, map and graph objects.
/// </summary>
public sealed class UniformGridSpatialIndex : IUiSpatialIndex
{
    private readonly Dictionary<long, List<UiNodeId>> _buckets = [];
    private readonly Dictionary<UiNodeId, uint> _queryStamps = [];
    private uint _queryStamp = 1;

    public int Count { get; private set; }
    public float CellSize { get; }
    public int LastBucketsQueried { get; private set; }

    public UniformGridSpatialIndex(float cellSize = 64f)
    {
        if (!float.IsFinite(cellSize) || cellSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(cellSize));
        CellSize = cellSize;
    }

    public void Insert(UiNodeId id, UIBox2 bounds)
    {
        if (!id.IsValid)
            throw new ArgumentException("A spatial entry requires a valid node ID.", nameof(id));

        GetCellRange(bounds, out var minX, out var minY, out var maxX, out var maxY);
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var key = Key(x, y);
                if (!_buckets.TryGetValue(key, out var bucket))
                    _buckets[key] = bucket = [];
                bucket.Add(id);
            }
        }
        Count++;
    }

    public void Remove(UiNodeId id, UIBox2 bounds)
    {
        GetCellRange(bounds, out var minX, out var minY, out var maxX, out var maxY);
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var key = Key(x, y);
                if (!_buckets.TryGetValue(key, out var bucket))
                    continue;
                bucket.Remove(id);
                if (bucket.Count == 0)
                    _buckets.Remove(key);
            }
        }
        _queryStamps.Remove(id);
        Count = Math.Max(0, Count - 1);
    }

    public void Update(UiNodeId id, UIBox2 oldBounds, UIBox2 newBounds)
    {
        var oldMinX = Cell(oldBounds.Left);
        var oldMinY = Cell(oldBounds.Top);
        var oldMaxX = Cell(oldBounds.Right);
        var oldMaxY = Cell(oldBounds.Bottom);
        var newMinX = Cell(newBounds.Left);
        var newMinY = Cell(newBounds.Top);
        var newMaxX = Cell(newBounds.Right);
        var newMaxY = Cell(newBounds.Bottom);
        if (oldMinX == newMinX && oldMinY == newMinY && oldMaxX == newMaxX && oldMaxY == newMaxY)
            return;

        Remove(id, oldBounds);
        Insert(id, newBounds);
    }

    public int QueryPoint(Vector2 point, Span<UiNodeId> results, out bool truncated)
    {
        LastBucketsQueried = 1;
        truncated = false;
        if (!_buckets.TryGetValue(Key(Cell(point.X), Cell(point.Y)), out var bucket))
            return 0;

        var count = Math.Min(bucket.Count, results.Length);
        for (var i = 0; i < count; i++)
            results[i] = bucket[i];
        truncated = bucket.Count > results.Length;
        return count;
    }

    public int QueryArea(UIBox2 area, Span<UiNodeId> results, out bool truncated)
    {
        var stamp = NextQueryStamp();
        var count = 0;
        truncated = false;
        GetCellRange(area, out var minX, out var minY, out var maxX, out var maxY);
        LastBucketsQueried = Math.Max(0, maxX - minX + 1) * Math.Max(0, maxY - minY + 1);
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                if (!_buckets.TryGetValue(Key(x, y), out var bucket))
                    continue;
                foreach (var id in bucket)
                {
                    if (_queryStamps.TryGetValue(id, out var seen) && seen == stamp)
                        continue;
                    _queryStamps[id] = stamp;
                    if (count >= results.Length)
                    {
                        truncated = true;
                        continue;
                    }
                    results[count++] = id;
                }
            }
        }
        return count;
    }

    public void Clear()
    {
        _buckets.Clear();
        _queryStamps.Clear();
        Count = 0;
        _queryStamp = 1;
        LastBucketsQueried = 0;
    }

    /// <summary>
    /// Enumerates occupied cells for diagnostics without exposing mutable buckets.
    /// </summary>
    public IEnumerable<(int X, int Y, int Count)> EnumerateBuckets()
    {
        foreach (var (key, bucket) in _buckets)
            yield return ((int)(key >> 32), unchecked((int)key), bucket.Count);
    }

    private int Cell(float coordinate) => (int)MathF.Floor(coordinate / CellSize);

    private static long Key(int x, int y) => ((long)x << 32) | (uint)y;

    private uint NextQueryStamp()
    {
        _queryStamp = unchecked(_queryStamp + 1);
        if (_queryStamp != 0)
            return _queryStamp;

        _queryStamps.Clear();
        _queryStamp = 1;
        return _queryStamp;
    }

    private void GetCellRange(
        UIBox2 bounds,
        out int minX,
        out int minY,
        out int maxX,
        out int maxY)
    {
        minX = Cell(bounds.Left);
        minY = Cell(bounds.Top);
        maxX = Cell(bounds.Right);
        maxY = Cell(bounds.Bottom);
    }
}
