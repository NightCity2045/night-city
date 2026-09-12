// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.IoC;
using Robust.Shared.Timing;

namespace Content.Client._NC.Vehicle;

/// <summary>Client-smoothed analog instruments backed by the replicated vehicle state.</summary>
public sealed class NCVehicleInstrumentCluster : Control
{
    private readonly Texture _background;
    private readonly Texture _warningStrip;
    private readonly Font _smallFont;
    private readonly Font _largeFont;
    private float _displaySpeed;
    private float _displayRpm;

    public float Speed { get; set; }
    public float Rpm { get; set; }
    public float RedlineRpm { get; set; } = 7000;
    public float FuelFraction { get; set; }
    public float TemperatureCelsius { get; set; } = 20;
    public string Gear { get; set; } = "N";
    public bool EngineWarning { get; set; }
    public bool OilWarning { get; set; }
    public bool BatteryWarning { get; set; }
    public bool HandbrakeWarning { get; set; }
    public bool BeltWarning { get; set; }
    public bool GripWarning { get; set; }

    public NCVehicleInstrumentCluster()
    {
        var cache = IoCManager.Resolve<IResourceCache>();
        _background = cache.GetResource<TextureResource>("/Textures/_NC/Vehicle/Hud/instrument-cluster.png").Texture;
        _warningStrip = cache.GetResource<TextureResource>("/Textures/_NC/Vehicle/Hud/warning-lights.png").Texture;
        var regular = cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf");
        var bold = cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Bold.ttf");
        _smallFont = new VectorFont(regular, 10);
        _largeFont = new VectorFont(bold, 24);
        SetSize = new Vector2(340, 150);
        MouseFilter = MouseFilterMode.Ignore;
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        // Visual interpolation is client-only; it never feeds back into drivetrain simulation.
        var amount = 1 - MathF.Exp(-10 * args.DeltaSeconds);
        _displaySpeed = MathHelper.Lerp(_displaySpeed, Speed, amount);
        _displayRpm = MathHelper.Lerp(_displayRpm, Rpm, amount);
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        var size = (Vector2) PixelSize;
        handle.DrawTextureRect(_background, UIBox2.FromDimensions(Vector2.Zero, size));

        var rpmCenter = new Vector2(size.X * 0.225f, size.Y * 0.53f);
        var speedCenter = new Vector2(size.X * 0.765f, size.Y * 0.53f);
        DrawNeedle(handle, rpmCenter, Math.Clamp(_displayRpm / Math.Max(1, RedlineRpm), 0, 1), Color.FromHex("#efb75b"), size.Y * 0.27f);
        DrawNeedle(handle, speedCenter, Math.Clamp(_displaySpeed / 240f, 0, 1), Color.FromHex("#16d3dc"), size.Y * 0.27f);

        DrawCentered(handle, new Vector2(size.X * 0.5f, size.Y * 0.30f), Gear, _largeFont, Color.FromHex("#f1c15e"));
        DrawCentered(handle, new Vector2(size.X * 0.5f, size.Y * 0.57f),
            Loc.GetString("nc-vehicle-hud-speed-value", ("speed", MathF.Round(_displaySpeed))), _smallFont, Color.FromHex("#9eebef"));
        DrawCentered(handle, new Vector2(size.X * 0.225f, size.Y * 0.77f),
            Loc.GetString("nc-vehicle-hud-rpm-value", ("rpm", MathF.Round(_displayRpm))), _smallFont, Color.White);
        DrawCentered(handle, new Vector2(size.X * 0.765f, size.Y * 0.77f),
            Loc.GetString("nc-vehicle-hud-fuel-value", ("fuel", MathF.Round(FuelFraction * 100))), _smallFont, Color.White);

        var stripSize = new Vector2(size.X * 0.46f, size.Y * 0.14f);
        var stripOrigin = new Vector2((size.X - stripSize.X) / 2, size.Y * 0.82f);
        handle.DrawTextureRect(_warningStrip, UIBox2.FromDimensions(stripOrigin, stripSize), Color.FromHex("#677178"));
        var warnings = new[] { EngineWarning, OilWarning, BatteryWarning, HandbrakeWarning, BeltWarning, GripWarning };
        for (var i = 0; i < warnings.Length; i++)
        {
            var center = stripOrigin + new Vector2(stripSize.X * (i + 0.5f) / warnings.Length, stripSize.Y / 2);
            handle.DrawCircle(center, 3.3f * UIScale,
                warnings[i] ? Color.FromHex(i == 5 ? "#17d4dd" : "#ff5a45") : Color.FromHex("#322b20"));
        }

        DrawBar(handle, new Vector2(size.X * 0.41f, size.Y * 0.73f), new Vector2(size.X * 0.18f, 3 * UIScale),
            FuelFraction, Color.FromHex("#17d4dd"));
        var temperature = Math.Clamp((TemperatureCelsius - 40) / 90, 0, 1);
        DrawBar(handle, new Vector2(size.X * 0.41f, size.Y * 0.78f), new Vector2(size.X * 0.18f, 3 * UIScale),
            temperature, temperature > 0.8f ? Color.FromHex("#ff5a45") : Color.FromHex("#efb75b"));
    }

    private static void DrawNeedle(DrawingHandleScreen handle, Vector2 center, float value, Color color, float length)
    {
        var angle = 2.45f + value * 4.55f;
        var end = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * length;
        handle.DrawLine(center, end, Color.FromHex("#150b08"));
        handle.DrawLine(center + Vector2.One, end + Vector2.One, color);
        handle.DrawCircle(center, 3, color);
    }

    private static void DrawBar(DrawingHandleScreen handle, Vector2 origin, Vector2 size, float value, Color color)
    {
        handle.DrawRect(UIBox2.FromDimensions(origin, size), Color.FromHex("#070a0c"));
        handle.DrawRect(UIBox2.FromDimensions(origin, new Vector2(size.X * Math.Clamp(value, 0, 1), size.Y)), color);
    }

    private void DrawCentered(DrawingHandleScreen handle, Vector2 position, string text, Font font, Color color)
    {
        var dimensions = handle.GetDimensions(font, text, UIScale);
        handle.DrawString(font, position - new Vector2(dimensions.X / 2, 0), text, UIScale, color);
    }
}
