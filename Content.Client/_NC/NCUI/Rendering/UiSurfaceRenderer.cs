// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Diagnostics;
using System.Numerics;
using Content.Client._NC.NCUI.Core;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Graphics;
using Robust.Shared.Maths;

namespace Content.Client._NC.NCUI.Rendering;

/// <summary>
/// Draws the culled lightweight scene with a reused queue and point buffer.
/// </summary>
public sealed class UiSurfaceRenderer
{
    private Vector2[] _points = new Vector2[128];

    public UiRenderQueue Queue { get; }
    public int LastDrawCommands { get; private set; }
    public double LastBuildMilliseconds { get; private set; }
    public double LastDrawMilliseconds { get; private set; }

    public UiSurfaceRenderer(UiNodeStore store)
    {
        Queue = new UiRenderQueue(store);
    }

    public void Draw(
        DrawingHandleScreen handle,
        UiSurfaceDocument document,
        UiViewportTransform viewport,
        UiSurfacePalette palette)
    {
        var started = Stopwatch.GetTimestamp();
        Queue.Build(document, viewport.ViewportBounds);
        var built = Stopwatch.GetTimestamp();
        LastDrawCommands = 0;

        foreach (var id in Queue.Span)
        {
            if (!document.Nodes.TryGet(id, out var node) ||
                !document.Visuals.TryGet(node.Visual, out var visual))
            {
                continue;
            }

            DrawVisual(handle, viewport, palette, node, visual);
            LastDrawCommands++;
        }

        var finished = Stopwatch.GetTimestamp();
        LastBuildMilliseconds = Stopwatch.GetElapsedTime(started, built).TotalMilliseconds;
        LastDrawMilliseconds = Stopwatch.GetElapsedTime(built, finished).TotalMilliseconds;
    }

    private void DrawVisual(
        DrawingHandleScreen handle,
        UiViewportTransform viewport,
        UiSurfacePalette palette,
        in UiSurfaceNode node,
        UiVisualDefinition visual)
    {
        var box = viewport.SurfaceToPixel(node.Bounds);
        if (!MathHelper.CloseTo(node.Scale, 1f))
        {
            var size = box.Size * Math.Max(0f, node.Scale);
            box = UIBox2.FromDimensions(box.Center - size * 0.5f, size);
        }

        var tint = visual.Color * palette.Resolve(node.VisualState) * node.Tint;
        if (node.Highlight > 0f)
            tint = Color.InterpolateBetween(tint, Color.White, Math.Clamp(node.Highlight, 0f, 1f));
        tint = tint.WithAlpha(tint.A * Math.Clamp(node.Opacity, 0f, 1f));

        switch (visual.Kind)
        {
            case UiVisualKind.Rectangle:
                if (visual.Filled && node.Fill < 1f)
                    box = UIBox2.FromDimensions(box.TopLeft, new Vector2(box.Width * Math.Clamp(node.Fill, 0f, 1f), box.Height));
                DrawRectangle(handle, box, tint, visual.Filled, node.Rotation);
                break;
            case UiVisualKind.Polygon:
                DrawPoints(handle, viewport, node, visual, DrawPrimitiveTopology.TriangleFan, tint);
                break;
            case UiVisualKind.Line:
                DrawLine(handle, viewport, node, visual, tint,
                    Math.Max(1f, visual.Thickness * node.ThicknessScale * viewport.UiScale));
                break;
            case UiVisualKind.Polyline:
            case UiVisualKind.Arc:
                DrawPoints(handle, viewport, node, visual, DrawPrimitiveTopology.LineStrip, tint,
                    Math.Max(1f, visual.Thickness * node.ThicknessScale * viewport.UiScale));
                break;
            case UiVisualKind.Sprite:
            case UiVisualKind.Icon:
                DrawTexture(handle, box, visual, tint, node.Rotation);
                break;
            case UiVisualKind.NinePatch:
                visual.StyleBox?.Draw(handle, box, viewport.UiScale);
                break;
            case UiVisualKind.Text:
                if (visual.Font != null)
                    handle.DrawString(visual.Font, box.TopLeft, visual.Text, tint);
                break;
            case UiVisualKind.EntityPreview:
                if (visual.Entity is { Valid: true } entity)
                    handle.DrawEntity(entity, box.Center, visual.EntityScale * viewport.Zoom * node.Scale, null);
                break;
        }
    }

    private static void DrawRectangle(
        DrawingHandleScreen handle,
        UIBox2 box,
        Color color,
        bool filled,
        float rotation)
    {
        if (MathHelper.CloseTo(rotation, 0f))
        {
            handle.DrawRect(box, color, filled);
            return;
        }

        Span<Vector2> points = stackalloc Vector2[5];
        points[0] = box.TopLeft;
        points[1] = new Vector2(box.Right, box.Top);
        points[2] = box.BottomRight;
        points[3] = new Vector2(box.Left, box.Bottom);
        points[4] = points[0];
        Rotate(points, box.Center, rotation);
        handle.DrawPrimitives(filled ? DrawPrimitiveTopology.TriangleFan : DrawPrimitiveTopology.LineStrip,
            filled ? points[..4] : points, color);
    }

    private static void DrawTexture(
        DrawingHandleScreen handle,
        UIBox2 box,
        UiVisualDefinition visual,
        Color tint,
        float rotation)
    {
        if (visual.Texture == null)
            return;
        if (MathHelper.CloseTo(rotation, 0f))
        {
            handle.DrawTextureRectRegion(visual.Texture, box, visual.TextureRegion, tint);
            return;
        }

        var region = visual.TextureRegion ?? UIBox2.FromDimensions(Vector2.Zero, visual.Texture.Size);
        var textureSize = (Vector2)visual.Texture.Size;
        Span<DrawVertexUV2D> vertices = stackalloc DrawVertexUV2D[4];
        vertices[0] = new DrawVertexUV2D(box.TopLeft, region.TopLeft / textureSize);
        vertices[1] = new DrawVertexUV2D(new Vector2(box.Right, box.Top),
            new Vector2(region.Right, region.Top) / textureSize);
        vertices[2] = new DrawVertexUV2D(box.BottomRight, region.BottomRight / textureSize);
        vertices[3] = new DrawVertexUV2D(new Vector2(box.Left, box.Bottom),
            new Vector2(region.Left, region.Bottom) / textureSize);
        Span<Vector2> positions = stackalloc Vector2[4];
        for (var i = 0; i < vertices.Length; i++)
            positions[i] = vertices[i].Position;
        Rotate(positions, box.Center, rotation);
        for (var i = 0; i < vertices.Length; i++)
            vertices[i].Position = positions[i];
        handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, visual.Texture, vertices, tint);
    }

    private void DrawLine(
        DrawingHandleScreen handle,
        UiViewportTransform viewport,
        in UiSurfaceNode node,
        UiVisualDefinition visual,
        Color tint,
        float thickness)
    {
        var start = visual.Points.Length > 0
            ? viewport.SurfaceToPixel(node.Bounds.TopLeft + visual.Points[0])
            : viewport.SurfaceToPixel(node.Bounds.TopLeft);
        var end = visual.Points.Length > 1
            ? viewport.SurfaceToPixel(node.Bounds.TopLeft + visual.Points[1])
            : viewport.SurfaceToPixel(node.Bounds.BottomRight);
        var center = viewport.SurfaceToPixel(node.Bounds.Center);
        if (!MathHelper.CloseTo(node.Scale, 1f))
        {
            start = center + (start - center) * node.Scale;
            end = center + (end - center) * node.Scale;
        }
        if (!MathHelper.CloseTo(node.Rotation, 0f))
        {
            Span<Vector2> points = stackalloc Vector2[2] { start, end };
            Rotate(points, center, node.Rotation);
            start = points[0];
            end = points[1];
        }
        end = Vector2.Lerp(start, end, Math.Clamp(node.Fill, 0f, 1f));
        DrawThickLine(handle, start, end, tint, thickness);
    }

    private void DrawPoints(
        DrawingHandleScreen handle,
        UiViewportTransform viewport,
        in UiSurfaceNode node,
        UiVisualDefinition visual,
        DrawPrimitiveTopology topology,
        Color tint,
        float thickness = 1f)
    {
        if (visual.Points.Length == 0)
            return;
        var pointCount = visual.Points.Length;
        if (topology == DrawPrimitiveTopology.LineStrip)
        {
            if (pointCount < 2)
                return;
            pointCount = Math.Clamp((int) MathF.Ceiling(pointCount * node.Fill), 2, pointCount);
        }
        EnsurePointCapacity(pointCount);
        var center = viewport.SurfaceToPixel(node.Bounds.Center);
        for (var i = 0; i < pointCount; i++)
        {
            _points[i] = viewport.SurfaceToPixel(node.Bounds.TopLeft + visual.Points[i]);
            if (!MathHelper.CloseTo(node.Scale, 1f))
                _points[i] = center + (_points[i] - center) * node.Scale;
        }
        if (!MathHelper.CloseTo(node.Rotation, 0f))
            Rotate(_points.AsSpan(0, pointCount), center, node.Rotation);

        if (topology == DrawPrimitiveTopology.LineStrip && thickness > 1f)
        {
            for (var i = 1; i < pointCount; i++)
                DrawThickLine(handle, _points[i - 1], _points[i], tint, thickness);
            return;
        }

        handle.DrawPrimitives(topology, _points.AsSpan(0, pointCount), tint);
    }

    private static void DrawThickLine(
        DrawingHandleScreen handle,
        Vector2 start,
        Vector2 end,
        Color color,
        float thickness)
    {
        var delta = end - start;
        var lengthSquared = delta.LengthSquared();
        if (thickness <= 1f || lengthSquared <= float.Epsilon)
        {
            handle.DrawLine(start, end, color);
            return;
        }

        var normal = new Vector2(-delta.Y, delta.X) / MathF.Sqrt(lengthSquared) * (thickness * 0.5f);
        Span<Vector2> vertices = stackalloc Vector2[4]
        {
            start + normal,
            end + normal,
            end - normal,
            start - normal,
        };
        handle.DrawPrimitives(DrawPrimitiveTopology.TriangleFan, vertices, color);
    }

    private void EnsurePointCapacity(int count)
    {
        if (count <= _points.Length)
            return;
        Array.Resize(ref _points, Math.Max(count, _points.Length * 2));
    }

    private static void Rotate(Span<Vector2> points, Vector2 center, float radians)
    {
        var sine = MathF.Sin(radians);
        var cosine = MathF.Cos(radians);
        for (var i = 0; i < points.Length; i++)
        {
            var local = points[i] - center;
            points[i] = center + new Vector2(
                local.X * cosine - local.Y * sine,
                local.X * sine + local.Y * cosine);
        }
    }
}
