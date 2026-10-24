// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using System.Text.Json.Nodes;
using Content.Meta.Mui;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;

namespace Content.Client._NC.Meta.Mui;

/// <summary>Bounded chart and projected marker canvas. It cannot inspect entities, maps, grids, rooms or PVS.</summary>
public sealed class NCMetaMuiPlot(MuiControlKind kind) : Control
{
    private readonly List<Vector2> _points = [];
    private readonly MuiControlKind _kind = kind;

    public void SetData(JsonArray? data, double minimum = 0, double maximum = 1)
    {
        MinHeight = Math.Max(MinHeight, 120);
        RectClipContent = true;
        _points.Clear();
        if (data is null || !double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum)
            return;
        var count = Math.Min(data.Count, MuiLimits.ChartPoints);
        for (var i = 0; i < count; i++)
        {
            if (_kind == MuiControlKind.Chart && data[i] is JsonValue scalar && scalar.TryGetValue<double>(out var y) && double.IsFinite(y))
                _points.Add(new(count <= 1 ? 0 : (float) i / (count - 1), Math.Clamp((float) ((y - minimum) / (maximum - minimum)), 0, 1)));
            else if (data[i] is JsonObject obj && obj["x"] is JsonValue xValue && obj["y"] is JsonValue yValue &&
                xValue.TryGetValue<double>(out var x) && yValue.TryGetValue<double>(out y) && double.IsFinite(x) && double.IsFinite(y))
                _points.Add(new(Math.Clamp((float) x, 0, 1), Math.Clamp((float) y, 0, 1)));
        }
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        handle.DrawRect(PixelSizeBox, Color.FromHex("#08151E"));
        Vector2? previous = null;
        foreach (var value in _points)
        {
            var point = new Vector2(value.X * PixelSize.X, (1 - value.Y) * PixelSize.Y);
            if (previous.HasValue && _kind is MuiControlKind.Chart or MuiControlKind.Schematic)
                handle.DrawLine(previous.Value, point, Color.Cyan);
            if (_kind != MuiControlKind.Chart)
                handle.DrawCircle(point, 3, Color.Cyan);
            previous = point;
        }
    }
}
