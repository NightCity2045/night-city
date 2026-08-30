// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Client._NC.NCUI.Core;
using Content.Client._NC.NCUI.Rendering;
using Content.Client._NC.NCUI.Spatial;
using Content.Client._NC.NCUI.State;
using Robust.Shared.Maths;

namespace Content.Client._NC.NCUI.Controls;

public sealed class PanZoomSurfaceControl : InteractiveSurfaceControl
{
    public PanZoomSurfaceControl(float spatialCellSize = 64f) : base(spatialCellSize)
    {
    }

    public void ResetCamera() => Viewport.SetCenter(Vector2.Zero);
}

public readonly record struct UiGraphPortId(uint Value)
{
    public bool IsValid => Value != 0;
}

public readonly record struct UiGraphConnectionId(uint Value)
{
    public bool IsValid => Value != 0;
}

/// <summary>
/// Reusable graph surface with lightweight nodes, ports and routed polyline connections.
/// </summary>
[Virtual]
public class NodeGraphControl : InteractiveSurfaceControl
{
    private readonly Dictionary<UiNodeId, List<UiGraphPortId>> _nodePorts = [];
    private readonly Dictionary<UiGraphPortId, (UiNodeId Owner, UiNodeId Visual)> _ports = [];
    private readonly Dictionary<UiGraphConnectionId, (UiGraphPortId Start, UiGraphPortId End, UiNodeId Visual)> _connections = [];
    private uint _nextPort = 1;
    private uint _nextConnection = 1;

    public NodeGraphControl() : base(96f)
    {
    }

    public UiNodeId AddGraphNode(UIBox2 bounds, Color color)
    {
        var visual = Document.Visuals.Register(new UiVisualDefinition
        {
            Kind = UiVisualKind.Rectangle,
            Color = color,
        });
        var node = Document.AddNode(new UiSurfaceNode(bounds, visual,
            UiNodeFlags.Visible | UiNodeFlags.Enabled | UiNodeFlags.HitTest | UiNodeFlags.Draggable |
            UiNodeFlags.Focusable | UiNodeFlags.Selectable));
        _nodePorts.Add(node, []);
        return node;
    }

    public UiGraphPortId AddPort(UiNodeId owner, Vector2 localPosition, float radius, Color color)
    {
        if (!Document.TryGetNode(owner, out var parent))
            throw new ArgumentException("The graph node does not exist.", nameof(owner));
        var id = new UiGraphPortId(_nextPort++);
        var visual = Document.Visuals.Register(new UiVisualDefinition
        {
            Kind = UiVisualKind.Rectangle,
            Color = color,
        });
        var bounds = UIBox2.FromDimensions(parent.Bounds.TopLeft + localPosition - new Vector2(radius),
            new Vector2(radius * 2f));
        var hit = Document.HitShapes.Register(new UiHitShapeDefinition { Kind = UiHitShapeKind.Ellipse });
        var portNode = new UiSurfaceNode(bounds, visual,
            UiNodeFlags.Visible | UiNodeFlags.Enabled | UiNodeFlags.HitTest | UiNodeFlags.Selectable,
            parent.ZIndex + 1,
            owner)
        {
            HitShape = hit,
        };
        var nodeId = Document.AddNode(portNode);
        _ports.Add(id, (owner, nodeId));
        _nodePorts[owner].Add(id);
        return id;
    }

    public UiGraphConnectionId Connect(UiGraphPortId start, UiGraphPortId end, Color color)
    {
        if (!_ports.TryGetValue(start, out var from) || !_ports.TryGetValue(end, out var to))
            throw new ArgumentException("Both graph ports must exist.");
        var startPoint = GetCenter(from.Visual);
        var endPoint = GetCenter(to.Visual);
        var topLeft = Vector2.Min(startPoint, endPoint);
        var bottomRight = Vector2.Max(startPoint, endPoint);
        var visual = Document.Visuals.Register(new UiVisualDefinition
        {
            Kind = UiVisualKind.Polyline,
            Color = color,
            Filled = false,
            Points = BuildConnectionPoints(startPoint - topLeft, endPoint - topLeft),
        });
        var wire = Document.AddNode(new UiSurfaceNode(new UIBox2(topLeft, bottomRight), visual,
            UiNodeFlags.Visible | UiNodeFlags.Enabled, -1));
        var id = new UiGraphConnectionId(_nextConnection++);
        _connections.Add(id, (start, end, wire));
        return id;
    }

    public bool MoveGraphNode(UiNodeId id, Vector2 topLeft)
    {
        if (!Document.TryGetNode(id, out var node) || !_nodePorts.TryGetValue(id, out var ports))
            return false;
        var delta = topLeft - node.Bounds.TopLeft;
        Document.SetBounds(id, UIBox2.FromDimensions(topLeft, node.Bounds.Size));
        foreach (var port in ports)
        {
            var visual = _ports[port].Visual;
            if (Document.TryGetNode(visual, out var portNode))
                Document.SetBounds(visual, UIBox2.FromDimensions(portNode.Bounds.TopLeft + delta, portNode.Bounds.Size));
        }
        RebuildConnectionsFor(ports);
        return true;
    }

    public bool Disconnect(UiGraphConnectionId id)
    {
        if (!_connections.Remove(id, out var connection))
            return false;
        return Document.RemoveNode(connection.Visual);
    }

    protected override void MoveNodeDuringDrag(UiNodeId source, Vector2 topLeft)
    {
        if (!MoveGraphNode(source, topLeft))
            base.MoveNodeDuringDrag(source, topLeft);
    }

    private void RebuildConnectionsFor(List<UiGraphPortId> ports)
    {
        foreach (var connection in _connections.Values)
        {
            if (!ports.Contains(connection.Start) && !ports.Contains(connection.End))
                continue;
            var start = GetCenter(_ports[connection.Start].Visual);
            var end = GetCenter(_ports[connection.End].Visual);
            var topLeft = Vector2.Min(start, end);
            var bottomRight = Vector2.Max(start, end);
            if (!Document.TryGetNode(connection.Visual, out var wire) ||
                !Document.Visuals.TryGet(wire.Visual, out var oldVisual))
            {
                continue;
            }
            var updatedVisual = new UiVisualDefinition
            {
                Kind = UiVisualKind.Polyline,
                Color = oldVisual.Color,
                Filled = false,
                Points = BuildConnectionPoints(start - topLeft, end - topLeft),
            };
            Document.Visuals.Update(wire.Visual, updatedVisual);
            wire.Bounds = new UIBox2(topLeft, bottomRight);
            Document.UpdateNode(connection.Visual, wire);
        }
    }

    private Vector2 GetCenter(UiNodeId id) => Document.TryGetNode(id, out var node) ? node.Bounds.Center : Vector2.Zero;

    private static Vector2[] BuildConnectionPoints(Vector2 start, Vector2 end)
    {
        var middle = (start.X + end.X) * 0.5f;
        return [start, new Vector2(middle, start.Y), new Vector2(middle, end.Y), end];
    }
}

/// <summary>
/// Specialized graph surface exposing only wire operations for circuit-like interfaces.
/// </summary>
public sealed class WireConnectionControl : NodeGraphControl
{
}

/// <summary>
/// Slot-oriented surface. Domain code registers typed drop targets through the inherited DragDrop service.
/// </summary>
public sealed class SlotBoardControl : InteractiveSurfaceControl
{
    public UiNodeId AddSlot(UIBox2 bounds, Color color)
    {
        var visual = Document.Visuals.Register(new UiVisualDefinition
        {
            Kind = UiVisualKind.Rectangle,
            Color = color,
            Filled = false,
        });
        return Document.AddNode(new UiSurfaceNode(bounds, visual,
            UiNodeFlags.Visible | UiNodeFlags.Enabled | UiNodeFlags.HitTest | UiNodeFlags.DropTarget |
            UiNodeFlags.Selectable));
    }
}

/// <summary>
/// Interactive surface facade for continuous paths. Network envelopes remain domain-owned and typed.
/// </summary>
public sealed class StrokeInputControl : InteractiveSurfaceControl
{
    public UiStrokeBatcher Batcher { get; } = new();

    public void BeginStroke(Vector2 surfacePoint, TimeSpan now) => Batcher.Begin(surfacePoint, now);
    public bool AddStrokePoint(Vector2 surfacePoint, TimeSpan now) => Batcher.Add(surfacePoint, now);
    public void CommitStroke(TimeSpan now) => Batcher.Commit(now);
    public void CancelStroke() => Batcher.Cancel();
}

public sealed class TimelineControl : InteractiveSurfaceControl
{
    public float UnitsPerSecond { get; set; } = 100f;

    public float TimeToSurfaceX(TimeSpan time) => (float) time.TotalSeconds * UnitsPerSecond;
    public TimeSpan SurfaceXToTime(float x) => TimeSpan.FromSeconds(x / Math.Max(0.001f, UnitsPerSecond));

    public UiNodeId AddClip(int track, TimeSpan start, TimeSpan duration, float trackHeight, Color color)
    {
        var visual = Document.Visuals.Register(new UiVisualDefinition
        {
            Kind = UiVisualKind.Rectangle,
            Color = color,
        });
        return Document.AddNode(new UiSurfaceNode(UIBox2.FromDimensions(
                new Vector2(TimeToSurfaceX(start), track * trackHeight),
                new Vector2(Math.Max(1f, TimeToSurfaceX(duration)), trackHeight - 2f)),
            visual,
            UiNodeFlags.Visible | UiNodeFlags.Enabled | UiNodeFlags.HitTest | UiNodeFlags.Draggable |
            UiNodeFlags.Selectable));
    }
}

public sealed class LayeredBodyControl : InteractiveSurfaceControl
{
    public UiNodeId AddRegion(Vector2 origin, Vector2[] polygon, int layer, Color color)
    {
        if (polygon.Length < 3)
            throw new ArgumentException("An anatomy region needs at least three points.", nameof(polygon));
        var min = polygon[0];
        var max = polygon[0];
        foreach (var point in polygon)
        {
            min = Vector2.Min(min, point);
            max = Vector2.Max(max, point);
        }
        var local = new Vector2[polygon.Length];
        for (var i = 0; i < polygon.Length; i++)
            local[i] = polygon[i] - min;
        var visual = Document.Visuals.Register(new UiVisualDefinition
        {
            Kind = UiVisualKind.Polygon,
            Color = color,
            Points = local,
        });
        var hit = Document.HitShapes.Register(new UiHitShapeDefinition
        {
            Kind = UiHitShapeKind.Polygon,
            Points = local,
        });
        var node = new UiSurfaceNode(new UIBox2(origin + min, origin + max), visual,
            UiNodeFlags.Visible | UiNodeFlags.Enabled | UiNodeFlags.HitTest | UiNodeFlags.Selectable,
            layer)
        {
            HitShape = hit,
        };
        return Document.AddNode(node);
    }
}

public sealed class MapSurfaceControl : InteractiveSurfaceControl
{
    public UiNodeId AddMarker(Vector2 position, float radius, Color color)
    {
        var visual = Document.Visuals.Register(new UiVisualDefinition
        {
            Kind = UiVisualKind.Rectangle,
            Color = color,
        });
        var hit = Document.HitShapes.Register(new UiHitShapeDefinition { Kind = UiHitShapeKind.Ellipse });
        var node = new UiSurfaceNode(UIBox2.FromDimensions(position - new Vector2(radius), new Vector2(radius * 2f)),
            visual,
            UiNodeFlags.Visible | UiNodeFlags.Enabled | UiNodeFlags.HitTest | UiNodeFlags.Selectable);
        node.HitShape = hit;
        return Document.AddNode(node);
    }
}

public sealed class CurveEditorControl : InteractiveSurfaceControl
{
    private readonly List<UiNodeId> _handles = [];

    public IReadOnlyList<UiNodeId> Handles => _handles;

    public UiNodeId AddHandle(Vector2 position, float radius, Color color)
    {
        var visual = Document.Visuals.Register(new UiVisualDefinition
        {
            Kind = UiVisualKind.Rectangle,
            Color = color,
        });
        var handle = Document.AddNode(new UiSurfaceNode(
            UIBox2.FromDimensions(position - new Vector2(radius), new Vector2(radius * 2f)), visual,
            UiNodeFlags.Visible | UiNodeFlags.Enabled | UiNodeFlags.HitTest | UiNodeFlags.Draggable |
            UiNodeFlags.Selectable));
        _handles.Add(handle);
        return handle;
    }
}
