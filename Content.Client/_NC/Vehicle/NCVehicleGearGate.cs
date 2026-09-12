// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Vehicle;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Input;
using Robust.Shared.IoC;
using Robust.Shared.Timing;

namespace Content.Client._NC.Vehicle;

/// <summary>Clickable gated shifter whose lever smoothly follows authoritative transmission state.</summary>
public sealed class NCVehicleGearGate : BaseButton
{
    private readonly Font _font;
    private readonly Texture _console;
    private Vector2 _display;
    private Vector2 _target;
    private bool _initialized;
    private bool _automatic;
    private bool _reverseArmed;
    private int _selector;
    private int _column;
    private int _row;

    public event Action<NCVehicleDriverAction>? DriverAction;

    public NCVehicleGearGate()
    {
        var cache = IoCManager.Resolve<IResourceCache>();
        _font = new VectorFont(cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"), 12);
        _console = cache.GetResource<TextureResource>("/Textures/_NC/Vehicle/Hud/shifter-console.png").Texture;
        SetSize = new Vector2(150, 150);
    }

    public void SetState(bool automatic, int column, int row, NCVehicleSelector selector, bool reverseArmed)
    {
        _automatic = automatic;
        _selector = (int) selector;
        _reverseArmed = reverseArmed;
        _column = Math.Clamp(column, 0, 2);
        _row = Math.Clamp(row, -1, 1);
        _target = automatic
            ? new Vector2(0, Math.Clamp(_selector, 0, 4) / 4f * 2 - 1)
            : new Vector2(_column - 1, _row);
        if (_initialized)
            return;
        _display = _target;
        _initialized = true;
    }

    protected override void KeyBindDown(GUIBoundKeyEventArgs args)
    {
        base.KeyBindDown(args);
        if (args.Function != EngineKeyFunctions.UIClick)
            return;

        TrySelect(args.RelativePosition);
        args.Handle();
    }

    private void TrySelect(Vector2 position)
    {
        if (Size.X <= 0 || Size.Y <= 0)
            return;

        if (_automatic)
        {
            var target = Math.Clamp((int) MathF.Round(position.Y / Size.Y * 4), 0, 4);
            var action = target < _selector ? NCVehicleDriverAction.GearUp : NCVehicleDriverAction.GearDown;
            for (var i = 0; i < Math.Abs(target - _selector); i++)
                DriverAction?.Invoke(action);
            return;
        }

        var targetColumn = Math.Clamp((int) MathF.Round(position.X / Size.X * 2), 0, 2);
        var targetRow = position.Y < Size.Y * 0.38f ? -1 : position.Y > Size.Y * 0.62f ? 1 : 0;
        if (targetColumn == _column && targetRow == _row)
            return;
        if (_row != 0)
            DriverAction?.Invoke(_row < 0 ? NCVehicleDriverAction.GearDown : NCVehicleDriverAction.GearUp);
        var horizontal = targetColumn < _column ? NCVehicleDriverAction.GearLeft : NCVehicleDriverAction.GearRight;
        for (var i = 0; i < Math.Abs(targetColumn - _column); i++)
            DriverAction?.Invoke(horizontal);
        if (targetRow != 0)
            DriverAction?.Invoke(targetRow < 0 ? NCVehicleDriverAction.GearUp : NCVehicleDriverAction.GearDown);
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        var amount = 1 - MathF.Exp(-12 * args.DeltaSeconds);
        _display = Vector2.Lerp(_display, _target, amount);
        if (Vector2.DistanceSquared(_display, _target) < 0.0001f)
            _display = _target;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        var size = (Vector2) PixelSize;
        if (_automatic)
            DrawAutomatic(handle, size);
        else
            DrawManual(handle, size);
    }

    private void DrawManual(DrawingHandleScreen handle, Vector2 size)
    {
        handle.DrawTextureRect(_console, UIBox2.FromDimensions(Vector2.Zero, size));
        var left = size.X * 0.23f;
        var right = size.X * 0.77f;
        var top = size.Y * 0.25f;
        var bottom = size.Y * 0.77f;
        var middle = size.Y * 0.51f;
        var step = (right - left) / 2;
        for (var i = 0; i < 3; i++)
        {
            var x = left + step * i;
            DrawCentered(handle, new Vector2(x, size.Y * 0.08f), (i * 2 + 1).ToString(), Color.White);
            DrawCentered(handle, new Vector2(x, size.Y * 0.84f), i == 2 ? "6/R" : (i * 2 + 2).ToString(),
                i == 2 && _reverseArmed ? Color.FromHex("#efb75b") : Color.White);
        }

        var knob = new Vector2(left + (_display.X + 1) * step, middle + _display.Y * (bottom - middle));
        handle.DrawLine(new Vector2(size.X / 2, middle), knob, Color.FromHex("#b7bec2"));
        DrawKnob(handle, knob);
    }

    private void DrawAutomatic(DrawingHandleScreen handle, Vector2 size)
    {
        handle.DrawRect(new UIBox2(Vector2.Zero, size), Color.FromHex("#11171b"));
        var x = size.X * 0.62f;
        var top = size.Y * 0.13f;
        var bottom = size.Y * 0.87f;
        handle.DrawRect(new UIBox2(x - 7 * UIScale, top, x + 7 * UIScale, bottom), Color.FromHex("#050708"));
        var names = new[] { "P", "R", "N", "D", "L" };
        for (var i = 0; i < names.Length; i++)
        {
            var y = top + (bottom - top) * i / 4;
            DrawCentered(handle, new Vector2(size.X * 0.25f, y - 8 * UIScale), names[i],
                i == _selector ? Color.FromHex("#efb75b") : Color.White);
        }
        DrawKnob(handle, new Vector2(x, top + (_display.Y + 1) / 2 * (bottom - top)));
    }

    private void DrawKnob(DrawingHandleScreen handle, Vector2 position)
    {
        handle.DrawCircle(position + new Vector2(2 * UIScale), 11 * UIScale, Color.FromHex("#07090a"));
        handle.DrawCircle(position, 10 * UIScale, Color.FromHex("#c6c9ca"));
        handle.DrawCircle(position, 7 * UIScale, Color.FromHex("#2b3338"));
        handle.DrawCircle(position - new Vector2(2 * UIScale), 2 * UIScale, Color.FromHex("#efb75b"));
    }

    private void DrawCentered(DrawingHandleScreen handle, Vector2 position, string text, Color color)
    {
        var dimensions = handle.GetDimensions(_font, text, UIScale);
        handle.DrawString(_font, position - new Vector2(dimensions.X / 2, 0), text, UIScale, color);
    }
}
