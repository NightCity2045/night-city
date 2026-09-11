// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Vehicle;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Shared.IoC;
using Robust.Shared.Timing;

namespace Content.Client._NC.Vehicle;

/// <summary>Animated gated shifter display for manual and automatic vehicle layouts.</summary>
public sealed class NCVehicleGearGate : Control
{
    private readonly Font _font;
    private Vector2 _display;
    private Vector2 _target;
    private bool _initialized;
    private bool _automatic;
    private bool _reverseArmed;
    private int _selector;

    public NCVehicleGearGate()
    {
        var cache = IoCManager.Resolve<IResourceCache>();
        _font = new VectorFont(cache.GetResource<FontResource>("/EngineFonts/NotoSans/NotoSans-Regular.ttf"), 14);
        SetSize = new Vector2(250, 130);
        MouseFilter = MouseFilterMode.Ignore;
    }

    public void SetState(bool automatic, int column, int row, NCVehicleSelector selector, bool reverseArmed)
    {
        _automatic = automatic;
        _selector = (int) selector;
        _reverseArmed = reverseArmed;
        _target = automatic
            ? new Vector2(0, Math.Clamp(_selector, 0, 4) / 4f * 2 - 1)
            : new Vector2(Math.Clamp(column, 0, 2) - 1, Math.Clamp(row, -1, 1));
        if (_initialized)
            return;
        _display = _target;
        _initialized = true;
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        // Exponential smoothing makes every accepted lever move visible without delaying gameplay state.
        var amount = 1 - MathF.Exp(-12 * args.DeltaSeconds);
        _display = Vector2.Lerp(_display, _target, amount);
        if (Vector2.DistanceSquared(_display, _target) < 0.0001f)
            _display = _target;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        var size = (Vector2) PixelSize;
        handle.DrawRect(new UIBox2(Vector2.Zero, size), Color.FromHex("#4d565d"));
        handle.DrawRect(new UIBox2(new Vector2(4 * UIScale), size - new Vector2(4 * UIScale)), Color.FromHex("#20262b"));

        if (_automatic)
            DrawAutomatic(handle, size);
        else
            DrawManual(handle, size);
    }

    private void DrawManual(DrawingHandleScreen handle, Vector2 size)
    {
        var left = 48 * UIScale;
        var right = size.X - 48 * UIScale;
        var top = 31 * UIScale;
        var bottom = size.Y - 31 * UIScale;
        var middle = size.Y / 2;
        var step = (right - left) / 2;
        var groove = Color.FromHex("#080b0d");
        var rim = Color.FromHex("#90999e");
        for (var i = 0; i < 3; i++)
        {
            var x = left + step * i;
            DrawVerticalSlot(handle, x, top, bottom, 12 * UIScale, rim);
            DrawVerticalSlot(handle, x, top, bottom, 7 * UIScale, groove);
            DrawCentered(handle, new Vector2(x, 10 * UIScale), (i * 2 + 1).ToString(), Color.White);
            DrawCentered(handle, new Vector2(x, size.Y - 22 * UIScale), i == 2 ? "6/R" : (i * 2 + 2).ToString(),
                i == 2 && _reverseArmed ? Color.FromHex("#efb75b") : Color.White);
        }
        DrawHorizontalSlot(handle, left, right, middle, 12 * UIScale, rim);
        DrawHorizontalSlot(handle, left, right, middle, 7 * UIScale, groove);

        var knob = new Vector2(left + (_display.X + 1) * step, middle + _display.Y * (bottom - middle));
        handle.DrawLine(new Vector2(size.X / 2, middle), knob, Color.FromHex("#b7bec2"));
        DrawKnob(handle, knob);
    }

    private void DrawAutomatic(DrawingHandleScreen handle, Vector2 size)
    {
        var x = size.X / 2;
        var top = 20 * UIScale;
        var bottom = size.Y - 20 * UIScale;
        var groove = Color.FromHex("#080b0d");
        DrawVerticalSlot(handle, x, top, bottom, 13 * UIScale, Color.FromHex("#90999e"));
        DrawVerticalSlot(handle, x, top, bottom, 8 * UIScale, groove);
        var names = new[] { "P", "R", "N", "D", "L" };
        for (var i = 0; i < names.Length; i++)
        {
            var y = top + (bottom - top) * i / 4;
            DrawCentered(handle, new Vector2(x - 35 * UIScale, y - 8 * UIScale), names[i],
                i == _selector ? Color.FromHex("#efb75b") : Color.White);
        }
        var knob = new Vector2(x, top + (_display.Y + 1) / 2 * (bottom - top));
        DrawKnob(handle, knob);
    }

    private void DrawKnob(DrawingHandleScreen handle, Vector2 position)
    {
        handle.DrawCircle(position + new Vector2(2 * UIScale), 15 * UIScale, Color.FromHex("#07090a"));
        handle.DrawCircle(position, 14 * UIScale, Color.FromHex("#c6c9ca"));
        handle.DrawCircle(position, 10 * UIScale, Color.FromHex("#2b3338"));
        handle.DrawCircle(position - new Vector2(3 * UIScale), 3 * UIScale, Color.FromHex("#65727a"));
    }

    private static void DrawVerticalSlot(DrawingHandleScreen handle, float x, float top, float bottom, float width, Color color) =>
        handle.DrawRect(new UIBox2(new Vector2(x - width / 2, top), new Vector2(x + width / 2, bottom)), color);

    private static void DrawHorizontalSlot(DrawingHandleScreen handle, float left, float right, float y, float width, Color color) =>
        handle.DrawRect(new UIBox2(new Vector2(left, y - width / 2), new Vector2(right, y + width / 2)), color);

    private void DrawCentered(DrawingHandleScreen handle, Vector2 position, string text, Color color)
    {
        var dimensions = handle.GetDimensions(_font, text, UIScale);
        handle.DrawString(_font, position - new Vector2(dimensions.X / 2, 0), text, UIScale, color);
    }
}
