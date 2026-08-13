// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Medical.Wounds;
using Robust.Shared.Maths;

namespace Content.Client._NC.Medical.Wounds;

/// <summary>
/// Converts stable normalized wound coordinates into a pixel cell inside one target-zone rectangle.
/// Keeping this math independent from the overlay makes edge coordinates deterministic and testable.
/// </summary>
public static class NCWoundMarkerProjection
{
    public static Vector2 ResolveBasePoint(NCVisibleWoundMarker marker, Box2 bounds)
    {
        var width = Math.Max(1, (int) MathF.Floor(bounds.Width));
        var height = Math.Max(1, (int) MathF.Floor(bounds.Height));
        var x = Math.Clamp((int) MathF.Floor(marker.VisualX / 256f * width), 0, width - 1);
        var y = Math.Clamp((int) MathF.Floor(marker.VisualY / 256f * height), 0, height - 1);
        return new Vector2(bounds.Left + x, bounds.Bottom + y);
    }
}
