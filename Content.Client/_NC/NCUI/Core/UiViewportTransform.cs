// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Robust.Shared.Maths;

namespace Content.Client._NC.NCUI.Core;

/// <summary>
/// Converts persistent virtual surface units to physical control pixels while preserving the point under the cursor.
/// </summary>
public sealed class UiViewportTransform
{
    private float _zoom = 1f;

    public Vector2 Center { get; private set; }
    public float MinZoom { get; set; } = 0.1f;
    public float MaxZoom { get; set; } = 8f;
    public float UiScale { get; private set; } = 1f;
    public Vector2 PixelSize { get; private set; } = Vector2.One;

    public float Zoom
    {
        get => _zoom;
        set => _zoom = Math.Clamp(value, Math.Max(0.001f, MinZoom), Math.Max(MinZoom, MaxZoom));
    }

    public UIBox2 ViewportBounds
    {
        get
        {
            var half = PixelSize / (2f * Zoom * UiScale);
            return new UIBox2(Center - half, Center + half);
        }
    }

    public void Configure(Vector2 pixelSize, float uiScale)
    {
        PixelSize = Vector2.Max(pixelSize, Vector2.One);
        UiScale = Math.Max(0.001f, uiScale);
    }

    public void SetCenter(Vector2 center)
    {
        if (!float.IsFinite(center.X) || !float.IsFinite(center.Y))
            return;
        Center = center;
    }

    public Vector2 SurfaceToPixel(Vector2 surface) =>
        (surface - Center) * Zoom * UiScale + PixelSize * 0.5f;

    public Vector2 PixelToSurface(Vector2 pixel) =>
        (pixel - PixelSize * 0.5f) / (Zoom * UiScale) + Center;

    public UIBox2 SurfaceToPixel(UIBox2 surface) =>
        new(SurfaceToPixel(surface.TopLeft), SurfaceToPixel(surface.BottomRight));

    public Vector2 SurfaceToScreen(Vector2 surface) => SurfaceToPixel(surface);
    public Vector2 ScreenToSurface(Vector2 screen) => PixelToSurface(screen);
    public Vector2 WorldToLocal(Vector2 surface) => SurfaceToPixel(surface) / UiScale;
    public Vector2 LocalToWorld(Vector2 local) => PixelToSurface(local * UiScale);

    public Vector2 SurfaceToPixelSnapped(Vector2 surface)
    {
        var pixel = SurfaceToPixel(surface);
        return new Vector2(MathF.Round(pixel.X), MathF.Round(pixel.Y));
    }

    public void PanPixels(Vector2 pixelDelta)
    {
        Center -= pixelDelta / (Zoom * UiScale);
    }

    public void ZoomAt(Vector2 pixelAnchor, float factor)
    {
        if (!float.IsFinite(factor) || factor <= 0f)
            return;

        var surfaceAnchor = PixelToSurface(pixelAnchor);
        Zoom *= factor;
        Center += surfaceAnchor - PixelToSurface(pixelAnchor);
    }
}
