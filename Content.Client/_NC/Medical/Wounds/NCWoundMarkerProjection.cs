// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Medical.Wounds;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Maths;

namespace Content.Client._NC.Medical.Wounds;

/// <summary>
/// Converts stable normalized wound coordinates into a pixel cell inside one target-zone rectangle.
/// Keeping this math independent from the overlay makes edge coordinates deterministic and testable.
/// </summary>
public static class NCWoundMarkerProjection
{
    /// <summary>
    /// Returns whether a body-local wound surface is exposed by the directional frame currently drawn for the
    /// humanoid. Diagonal frames expose their two adjacent surfaces; a wound never leaks through to its opposite.
    /// </summary>
    public static bool IsSurfaceVisible(NCWoundSurface surface, RsiDirection direction)
    {
        return direction switch
        {
            RsiDirection.South => surface == NCWoundSurface.Front,
            RsiDirection.North => surface == NCWoundSurface.Rear,
            RsiDirection.East => surface == NCWoundSurface.Right,
            RsiDirection.West => surface == NCWoundSurface.Left,
            RsiDirection.SouthEast => surface is NCWoundSurface.Front or NCWoundSurface.Right,
            RsiDirection.SouthWest => surface is NCWoundSurface.Front or NCWoundSurface.Left,
            RsiDirection.NorthEast => surface is NCWoundSurface.Rear or NCWoundSurface.Right,
            RsiDirection.NorthWest => surface is NCWoundSurface.Rear or NCWoundSurface.Left,
            _ => false,
        };
    }

    public static Vector2 ResolveBasePoint(NCVisibleWoundMarker marker, Box2 bounds)
    {
        var width = Math.Max(1, (int) MathF.Floor(bounds.Width));
        var height = Math.Max(1, (int) MathF.Floor(bounds.Height));
        var x = Math.Clamp((int) MathF.Floor(marker.VisualX / 256f * width), 0, width - 1);
        var y = Math.Clamp((int) MathF.Floor(marker.VisualY / 256f * height), 0, height - 1);
        return new Vector2(bounds.Left + x, bounds.Bottom + y);
    }

    /// <summary>
    /// Projects a stable wound coordinate onto the alpha mask of the layer frame that is actually being rendered.
    /// Directional humanoid frames do not share identical silhouettes, so using the south-facing doll rectangle
    /// directly makes a wound appear stationary while the body turns. Selecting a row and a pixel by normalized
    /// rank keeps the mark attached to the same relative area of every directional silhouette.
    /// </summary>
    public static bool TryResolveMaskedPoint(
        NCVisibleWoundMarker marker,
        HashSet<Vector2i> mask,
        out Vector2i point)
    {
        point = default;
        if (mask.Count == 0)
            return false;

        var minimumY = int.MaxValue;
        var maximumY = int.MinValue;
        foreach (var cell in mask)
        {
            minimumY = Math.Min(minimumY, cell.Y);
            maximumY = Math.Max(maximumY, cell.Y);
        }

        var desiredY = ResolveCoordinate(marker.VisualY, minimumY, maximumY);
        var selectedY = minimumY;
        var rowDistance = int.MaxValue;
        foreach (var cell in mask)
        {
            var distance = Math.Abs(cell.Y - desiredY);
            if (distance > rowDistance || distance == rowDistance && cell.Y >= selectedY)
                continue;

            selectedY = cell.Y;
            rowDistance = distance;
        }

        var minimumX = int.MaxValue;
        var maximumX = int.MinValue;
        foreach (var cell in mask)
        {
            if (cell.Y != selectedY)
                continue;

            minimumX = Math.Min(minimumX, cell.X);
            maximumX = Math.Max(maximumX, cell.X);
        }

        if (minimumX == int.MaxValue)
            return false;

        var desiredX = ResolveCoordinate(marker.VisualX, minimumX, maximumX);
        var columnDistance = int.MaxValue;
        foreach (var cell in mask)
        {
            if (cell.Y != selectedY)
                continue;

            var distance = Math.Abs(cell.X - desiredX);
            if (distance > columnDistance || distance == columnDistance && cell.X >= point.X)
                continue;

            point = cell;
            columnDistance = distance;
        }

        return columnDistance != int.MaxValue;
    }

    private static int ResolveCoordinate(byte normalized, int minimum, int maximum)
    {
        if (minimum >= maximum)
            return minimum;

        var range = maximum - minimum;
        return minimum + (int) MathF.Round(normalized / (float) byte.MaxValue * range);
    }
}
