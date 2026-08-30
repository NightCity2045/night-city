// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Client._NC.NCUI.Core;
using Robust.Shared.Maths;

namespace Content.Client._NC.NCUI.Spatial;

/// <summary>
/// Hit geometry kinds evaluated in node-local surface units.
/// </summary>
public enum UiHitShapeKind : byte
{
    Rectangle,
    Ellipse,
    Polygon,
    Polyline,
}

/// <summary>
/// Immutable hit geometry. Polygon and polyline points are relative to the node's top-left corner.
/// </summary>
public sealed class UiHitShapeDefinition
{
    public required UiHitShapeKind Kind { get; init; }
    public Vector2[] Points { get; init; } = [];
    public float Tolerance { get; init; } = 4f;
}

/// <summary>
/// Stores reusable hit shapes and performs exact checks after spatial broad-phase filtering.
/// </summary>
public sealed class UiHitShapeRegistry
{
    private readonly List<Entry> _definitions = [];
    private readonly Stack<int> _free = [];
    private int _activeCount;

    public int Count => _activeCount;

    public UiHitShapeHandle Register(UiHitShapeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!float.IsFinite(definition.Tolerance) || definition.Tolerance < 0f)
            throw new ArgumentOutOfRangeException(nameof(definition), "NCUI hit tolerance must be finite and non-negative.");
        foreach (var point in definition.Points)
        {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
                throw new ArgumentException("NCUI hit-shape points must be finite.", nameof(definition));
        }
        if (_free.TryPop(out var free))
        {
            _definitions[free] = new Entry(definition, 0);
            _activeCount++;
            return new UiHitShapeHandle((uint) free + 1);
        }
        _definitions.Add(new Entry(definition, 0));
        _activeCount++;
        return new UiHitShapeHandle((uint)_definitions.Count);
    }

    public bool Contains(UiHitShapeHandle handle, UIBox2 bounds, Vector2 point)
    {
        if (!bounds.Contains(point))
            return false;
        if (!handle.IsValid)
            return true;

        var index = (int)handle.Value - 1;
        if (index < 0 || index >= _definitions.Count || _definitions[index].Definition == null)
            return false;

        var shape = _definitions[index].Definition!;
        var local = point - bounds.TopLeft;
        return shape.Kind switch
        {
            UiHitShapeKind.Rectangle => true,
            UiHitShapeKind.Ellipse => ContainsEllipse(bounds.Size, local),
            UiHitShapeKind.Polygon => ContainsPolygon(shape.Points, local),
            UiHitShapeKind.Polyline => ContainsPolyline(shape.Points, local, shape.Tolerance),
            _ => false,
        };
    }

    public bool Retain(UiHitShapeHandle handle)
    {
        if (!handle.IsValid)
            return true;
        var index = (int) handle.Value - 1;
        if (index < 0 || index >= _definitions.Count || _definitions[index].Definition == null)
            return false;
        var entry = _definitions[index];
        entry.References++;
        _definitions[index] = entry;
        return true;
    }

    public void Release(UiHitShapeHandle handle)
    {
        if (!handle.IsValid)
            return;
        var index = (int) handle.Value - 1;
        if (index < 0 || index >= _definitions.Count || _definitions[index].Definition == null)
            return;
        var entry = _definitions[index];
        if (entry.References > 0)
            entry.References--;
        if (entry.References != 0)
        {
            _definitions[index] = entry;
            return;
        }
        _definitions[index] = default;
        _free.Push(index);
        _activeCount--;
    }

    public void Clear()
    {
        _definitions.Clear();
        _free.Clear();
        _activeCount = 0;
    }

    private struct Entry
    {
        public UiHitShapeDefinition? Definition;
        public int References;

        public Entry(UiHitShapeDefinition definition, int references)
        {
            Definition = definition;
            References = references;
        }
    }

    private static bool ContainsEllipse(Vector2 size, Vector2 point)
    {
        if (size.X <= 0f || size.Y <= 0f)
            return false;
        var normalized = (point - size * 0.5f) / (size * 0.5f);
        return normalized.LengthSquared() <= 1f;
    }

    private static bool ContainsPolygon(ReadOnlySpan<Vector2> points, Vector2 point)
    {
        if (points.Length < 3)
            return false;

        var inside = false;
        for (var i = 0; i < points.Length; i++)
        {
            var j = i == 0 ? points.Length - 1 : i - 1;
            var a = points[i];
            var b = points[j];
            if ((a.Y > point.Y) == (b.Y > point.Y))
                continue;
            var crossing = (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X;
            if (point.X < crossing)
                inside = !inside;
        }
        return inside;
    }

    private static bool ContainsPolyline(ReadOnlySpan<Vector2> points, Vector2 point, float tolerance)
    {
        var threshold = tolerance * tolerance;
        for (var i = 1; i < points.Length; i++)
        {
            var start = points[i - 1];
            var end = points[i];
            var segment = end - start;
            var lengthSquared = segment.LengthSquared();
            var t = lengthSquared <= float.Epsilon
                ? 0f
                : Math.Clamp(Vector2.Dot(point - start, segment) / lengthSquared, 0f, 1f);
            if (Vector2.DistanceSquared(point, start + segment * t) <= threshold)
                return true;
        }
        return false;
    }
}
