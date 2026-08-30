// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.NCUI;

[Serializable, NetSerializable]
public readonly record struct NcUiCompressedPoint(short X, short Y);

/// <summary>
/// Quantizes continuous surface paths relative to the first point, bounding packet size and precision.
/// </summary>
public static class NcUiPointCompression
{
    public const float DefaultQuantum = 0.125f;

    public static NcUiCompressedPoint[] Compress(ReadOnlySpan<Vector2> points, Vector2 origin, float quantum)
    {
        if (!float.IsFinite(quantum) || quantum <= 0f)
            throw new ArgumentOutOfRangeException(nameof(quantum));
        var result = new NcUiCompressedPoint[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            var local = (points[i] - origin) / quantum;
            result[i] = new NcUiCompressedPoint(
                (short)Math.Clamp(MathF.Round(local.X), short.MinValue, short.MaxValue),
                (short)Math.Clamp(MathF.Round(local.Y), short.MinValue, short.MaxValue));
        }
        return result;
    }

    public static Vector2[] Decompress(ReadOnlySpan<NcUiCompressedPoint> points, Vector2 origin, float quantum)
    {
        if (!float.IsFinite(quantum) || quantum <= 0f)
            throw new ArgumentOutOfRangeException(nameof(quantum));
        var result = new Vector2[points.Length];
        for (var i = 0; i < points.Length; i++)
            result[i] = origin + new Vector2(points[i].X, points[i].Y) * quantum;
        return result;
    }
}
