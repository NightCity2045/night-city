// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.IoC;

namespace Content.Client._NC.Vehicle;

/// <summary>Compact pedal art with live pressure illumination for clutch, brake and throttle.</summary>
public sealed class NCVehiclePedalDisplay : Control
{
    private readonly Texture _pedals;

    public float Clutch { get; set; }
    public float Brake { get; set; }
    public float Throttle { get; set; }

    public NCVehiclePedalDisplay()
    {
        var cache = IoCManager.Resolve<IResourceCache>();
        _pedals = cache.GetResource<TextureResource>("/Textures/_NC/Vehicle/Hud/pedals.png").Texture;
        SetSize = new Vector2(115, 52);
        MouseFilter = MouseFilterMode.Ignore;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        var size = (Vector2) PixelSize;
        handle.DrawTextureRect(_pedals, UIBox2.FromDimensions(Vector2.Zero, size));
        DrawPressure(handle, new UIBox2(size.X * 0.05f, size.Y * 0.08f, size.X * 0.31f, size.Y * 0.94f), Clutch, Color.FromHex("#17d4dd"));
        DrawPressure(handle, new UIBox2(size.X * 0.37f, size.Y * 0.08f, size.X * 0.63f, size.Y * 0.94f), Brake, Color.FromHex("#ff5a45"));
        DrawPressure(handle, new UIBox2(size.X * 0.72f, size.Y * 0.08f, size.X * 0.94f, size.Y * 0.94f), Throttle, Color.FromHex("#efb75b"));
    }

    private static void DrawPressure(DrawingHandleScreen handle, UIBox2 box, float value, Color color)
    {
        value = Math.Clamp(value, 0, 1);
        if (value <= 0)
            return;
        var top = MathHelper.Lerp(box.Bottom, box.Top, value);
        handle.DrawRect(new UIBox2(box.Left, top, box.Right, box.Bottom), color.WithAlpha(0.32f));
    }
}
