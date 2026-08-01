// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Editor;
using Content.Shared._NC.City.Zones.Prototypes;
using Robust.Client.Graphics;
using Robust.Shared.Enums;

namespace Content.Client._NC.City.Zones.Overlays;

/// <summary>
/// Visualizes server-authorized zone geometry on the mapper's current logical floor.
/// </summary>
public sealed class NCZoneEditorOverlay(NCZoneEditorClientSystem editor) : Overlay
{
    public override OverlaySpace Space => OverlaySpace.WorldSpace;

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (editor.Snapshot is not { } snapshot || !editor.TryGetCurrentDepth(out var z))
            return;

        foreach (var zone in snapshot.Zones)
        {
            var selected = editor.SelectedZone == zone.Id;
            var color = selected
                ? Color.White
                : new Color(zone.ColorR, zone.ColorG, zone.ColorB, (byte) 210);

            foreach (var geometry in zone.Geometry)
                DrawGeometry(args, geometry, z, color, selected);
        }

        DrawActiveTool(args, editor, z);
    }

    private static void DrawGeometry(
        in OverlayDrawArgs args,
        NCZoneEditorGeometry geometry,
        int z,
        Color color,
        bool selected)
    {
        switch (geometry.Kind)
        {
            case NCZoneGeometryKind.Polygon when geometry.Global || geometry.Z == z:
                DrawPolygon(args, geometry.Vertices, color, selected);
                break;
            case NCZoneGeometryKind.Volume when
                geometry.Global || z >= geometry.MinZ && z <= geometry.MaxZ:
                DrawPolygon(args, geometry.Vertices, color, selected);
                break;
            case NCZoneGeometryKind.TileMask:
                DrawTileMask(args, geometry.Chunks, z, color);
                break;
        }
    }

    private static void DrawPolygon(
        in OverlayDrawArgs args,
        IReadOnlyList<Vector2> vertices,
        Color color,
        bool selected)
    {
        if (vertices.Count < 2)
            return;

        var minimum = vertices[0];
        var maximum = vertices[0];
        for (var index = 1; index < vertices.Count; index++)
        {
            minimum = Vector2.Min(minimum, vertices[index]);
            maximum = Vector2.Max(maximum, vertices[index]);
        }

        // Large city drafts may contain thousands of shapes. Do not submit lines that cannot
        // affect the current viewport to the renderer.
        if (!new Box2(minimum, maximum).Intersects(args.WorldBounds.CalcBoundingBox()))
            return;

        for (var index = 0; index < vertices.Count; index++)
        {
            var start = vertices[index];
            var end = vertices[(index + 1) % vertices.Count];
            args.WorldHandle.DrawLine(start, end, color);

            if (selected)
            {
                var marker = Box2.FromDimensions(start - new Vector2(0.08f), new Vector2(0.16f));
                args.WorldHandle.DrawRect(marker, color);
            }
        }
    }

    private static void DrawTileMask(
        in OverlayDrawArgs args,
        IReadOnlyList<NCZoneEditorTileChunk> chunks,
        int z,
        Color color)
    {
        foreach (var chunk in chunks)
        {
            if (chunk.Z != z)
                continue;

            var chunkBounds = Box2.FromDimensions(
                new Vector2(chunk.Origin.X, chunk.Origin.Y),
                new Vector2(32f, 32f));
            if (!chunkBounds.Intersects(args.WorldBounds.CalcBoundingBox()))
                continue;

            for (var y = 0; y < chunk.Rows.Length; y++)
            {
                var row = chunk.Rows[y];
                while (row != 0)
                {
                    var x = BitOperations.TrailingZeroCount(row);
                    var bounds = Box2.FromDimensions(
                        new Vector2(chunk.Origin.X + x, chunk.Origin.Y + y),
                        Vector2.One);
                    args.WorldHandle.DrawRect(bounds, color.WithAlpha(40));
                    args.WorldHandle.DrawRect(bounds, color, filled: false);
                    row &= row - 1;
                }
            }
        }
    }

    private static void DrawActiveTool(
        in OverlayDrawArgs args,
        NCZoneEditorClientSystem editor,
        int z)
    {
        if (editor.CursorCoordinates is not { } cursor || cursor.Z != z)
            return;

        var color = Color.Lime;
        if (editor.Tool is NCZoneEditorTool.DrawPolygon or NCZoneEditorTool.DrawVolume)
        {
            var vertices = editor.PendingVertices;
            for (var index = 1; index < vertices.Count; index++)
                args.WorldHandle.DrawLine(vertices[index - 1], vertices[index], color);

            if (vertices.Count != 0)
                args.WorldHandle.DrawLine(vertices[^1], cursor.Position, color);

            var marker = Box2.FromDimensions(cursor.Position - new Vector2(0.08f), new Vector2(0.16f));
            args.WorldHandle.DrawRect(marker, color);
            return;
        }

        if (editor.Tool is NCZoneEditorTool.PaintTiles or NCZoneEditorTool.EraseTiles)
        {
            var tile = new Vector2(
                MathF.Floor(cursor.Position.X),
                MathF.Floor(cursor.Position.Y));
            args.WorldHandle.DrawRect(
                Box2.FromDimensions(tile, Vector2.One),
                editor.Tool == NCZoneEditorTool.PaintTiles ? Color.Lime : Color.Red,
                filled: false);
            return;
        }

    }
}
