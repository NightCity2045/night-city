// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client._NC.NCUI.Rendering;
using Content.Client._NC.NCUI.Spatial;
using Robust.Shared.Maths;

namespace Content.Client._NC.NCUI.Core;

/// <summary>
/// Owns lightweight nodes and keeps the spatial index coherent through one mutation boundary.
/// </summary>
public sealed class UiSurfaceDocument
{
    private readonly List<UiNodeId> _removeBuffer = [];

    public UiNodeStore Nodes { get; } = new();
    public UiVisualRegistry Visuals { get; } = new();
    public UiHitShapeRegistry HitShapes { get; } = new();
    public IUiSpatialIndex SpatialIndex { get; }
    public uint Version { get; private set; }
    public uint RenderOrderVersion { get; private set; }

    public event Action<UiNodeId>? NodeAdded;
    public event Action<UiNodeId>? NodeChanged;
    public event Action<UiNodeId>? NodeRemoved;
    public event Action? Cleared;

    public UiSurfaceDocument(IUiSpatialIndex? spatialIndex = null)
    {
        SpatialIndex = spatialIndex ?? new UniformGridSpatialIndex();
    }

    public UiNodeId AddNode(UiSurfaceNode node)
    {
        ValidateNode(node);
        if (node.Parent is { } parent && !Nodes.Contains(parent))
            throw new ArgumentException($"Parent node {parent} does not exist.", nameof(node));

        if (!Visuals.Retain(node.Visual))
            throw new ArgumentException("The NCUI node references an unknown visual handle.", nameof(node));
        if (!HitShapes.Retain(node.HitShape))
        {
            Visuals.Release(node.Visual);
            throw new ArgumentException("The NCUI node references an unknown hit-shape handle.", nameof(node));
        }
        var id = Nodes.Add(node);
        SpatialIndex.Insert(id, node.Bounds);
        Version++;
        RenderOrderVersion++;
        NodeAdded?.Invoke(id);
        return id;
    }

    public bool TryGetNode(UiNodeId id, out UiSurfaceNode node) => Nodes.TryGet(id, out node);

    public void UpdateNode(UiNodeId id, in UiSurfaceNode node)
    {
        ValidateNode(node);
        if (!Nodes.TryGet(id, out var old))
            throw new KeyNotFoundException($"Unknown NCUI node {id}.");
        if (node.Parent is { } parent && !Nodes.Contains(parent))
            throw new ArgumentException($"Parent node {parent} does not exist.", nameof(node));

        var visualChanged = old.Visual != node.Visual;
        var hitShapeChanged = old.HitShape != node.HitShape;
        if (visualChanged && !Visuals.Retain(node.Visual))
            throw new ArgumentException("The NCUI node references an unknown visual handle.", nameof(node));
        if (hitShapeChanged && !HitShapes.Retain(node.HitShape))
        {
            if (visualChanged)
                Visuals.Release(node.Visual);
            throw new ArgumentException("The NCUI node references an unknown hit-shape handle.", nameof(node));
        }
        if (visualChanged)
            Visuals.Release(old.Visual);
        if (hitShapeChanged)
            HitShapes.Release(old.HitShape);
        Nodes.Set(id, node);
        if (old.Bounds != node.Bounds)
            SpatialIndex.Update(id, old.Bounds, node.Bounds);
        Version++;
        if (old.Bounds != node.Bounds || old.ZIndex != node.ZIndex ||
            (old.Flags & UiNodeFlags.Visible) != (node.Flags & UiNodeFlags.Visible))
        {
            RenderOrderVersion++;
        }
        NodeChanged?.Invoke(id);
    }

    public bool SetBounds(UiNodeId id, UIBox2 bounds)
    {
        if (!Nodes.TryGet(id, out var node))
            return false;
        node.Bounds = bounds;
        UpdateNode(id, node);
        return true;
    }

    public bool SetVisualState(UiNodeId id, UiNodeVisualState state)
    {
        if (!Nodes.TryGet(id, out var node) || node.VisualState == state)
            return false;
        node.VisualState = state;
        Nodes.Set(id, node);
        Version++;
        NodeChanged?.Invoke(id);
        return true;
    }

    public bool RemoveNode(UiNodeId id, bool removeDescendants = true)
    {
        if (!Nodes.Contains(id))
            return false;

        _removeBuffer.Clear();
        _removeBuffer.Add(id);
        if (removeDescendants)
        {
            for (var cursor = 0; cursor < _removeBuffer.Count; cursor++)
            {
                var parent = _removeBuffer[cursor];
                var nodes = Nodes.Span;
                for (var i = 0; i < nodes.Length; i++)
                {
                    if (nodes[i].Parent == parent)
                        _removeBuffer.Add(nodes[i].Id);
                }
            }
        }

        for (var i = _removeBuffer.Count - 1; i >= 0; i--)
        {
            var remove = _removeBuffer[i];
            if (!Nodes.Remove(remove, out var removed))
                continue;
            SpatialIndex.Remove(remove, removed.Bounds);
            Visuals.Release(removed.Visual);
            HitShapes.Release(removed.HitShape);
            NodeRemoved?.Invoke(remove);
        }

        Version++;
        RenderOrderVersion++;
        return true;
    }

    public void Clear()
    {
        Nodes.Clear();
        Visuals.Clear();
        HitShapes.Clear();
        SpatialIndex.Clear();
        Version++;
        RenderOrderVersion++;
        Cleared?.Invoke();
    }

    private static void ValidateNode(in UiSurfaceNode node)
    {
        var bounds = node.Bounds;
        if (!float.IsFinite(bounds.Left) || !float.IsFinite(bounds.Top) ||
            !float.IsFinite(bounds.Right) || !float.IsFinite(bounds.Bottom) ||
            bounds.Right < bounds.Left || bounds.Bottom < bounds.Top)
        {
            throw new ArgumentException("NCUI node bounds must be finite and normalized.", nameof(node));
        }
        if (!float.IsFinite(node.Opacity) || !float.IsFinite(node.Rotation) ||
            !float.IsFinite(node.Scale) || node.Scale < 0f ||
            !float.IsFinite(node.Highlight) || !float.IsFinite(node.ThicknessScale) ||
            node.ThicknessScale < 0f || !float.IsFinite(node.Fill) ||
            !float.IsFinite(node.Tint.R) || !float.IsFinite(node.Tint.G) ||
            !float.IsFinite(node.Tint.B) || !float.IsFinite(node.Tint.A))
        {
            throw new ArgumentException("NCUI node visual values must be finite and non-negative where required.", nameof(node));
        }
    }
}
