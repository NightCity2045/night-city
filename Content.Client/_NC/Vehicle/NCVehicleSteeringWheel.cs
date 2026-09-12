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
using Robust.Shared.Graphics.RSI;
using Robust.Shared.IoC;

namespace Content.Client._NC.Vehicle;

/// <summary>Circular mouse steering with a rendered wheel that follows the actual steering angle.</summary>
public sealed class NCVehicleSteeringWheel : BaseButton
{
    private const int WheelFrames = 17;
    private readonly RSI.State _wheel;
    private Vector2? _lastDirection;

    public float Value { get; set; }
    public float MaximumAngle { get; set; } = MathF.Tau;
    public bool Held { get; private set; }

    public NCVehicleSteeringWheel()
    {
        var cache = IoCManager.Resolve<IResourceCache>();
        var rsi = cache.GetResource<RSIResource>("/Textures/_NC/Vehicle/Hud/steering-wheel.rsi").RSI;
        _wheel = rsi["wheel"];
    }

    protected override void KeyBindDown(GUIBoundKeyEventArgs args)
    {
        if (args.Function == EngineKeyFunctions.CloseModals || args.Function == EngineKeyFunctions.EscapeMenu)
        {
            ReleaseGrip();
            return;
        }

        base.KeyBindDown(args);
        if (args.Function != EngineKeyFunctions.UIClick)
            return;

        Held = true;
        _lastDirection = Direction(args.RelativePosition);
        args.Handle();
    }

    protected override void KeyBindUp(GUIBoundKeyEventArgs args)
    {
        base.KeyBindUp(args);
        if (args.Function == EngineKeyFunctions.UIClick)
            ReleaseGrip();
    }

    protected override void ControlFocusExited()
    {
        base.ControlFocusExited();
        Held = false;
        _lastDirection = null;
    }

    public void ReleaseGrip()
    {
        Held = false;
        _lastDirection = null;
        if (UserInterfaceManager.ControlFocused == this)
            UserInterfaceManager.ControlFocused = null;
    }

    protected override void MouseMove(GUIMouseMoveEventArgs args)
    {
        base.MouseMove(args);
        if (!Held)
            return;

        var next = Direction(args.RelativePosition);
        if (_lastDirection is { } previous && next is { } direction && MaximumAngle > 0)
            Value = Math.Clamp(Value - NCVehicleDriveMath.WheelDragAngle(previous, direction) / MaximumAngle, -1, 1);

        // Crossing the hub releases the angular reference and avoids a half-turn jump.
        _lastDirection = next;
        args.Handle();
    }

    private Vector2? Direction(Vector2 point)
    {
        var offset = point - Size / 2;
        return offset.LengthSquared() < 100 ? null : Vector2.Normalize(offset);
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        var size = (Vector2) PixelSize;
        var side = Math.Min(size.X, size.Y);
        var origin = (size - new Vector2(side)) / 2;
        var frame = Math.Clamp((int) MathF.Round((Value + 1) * 0.5f * (WheelFrames - 1)), 0, WheelFrames - 1);
        handle.DrawTextureRect(_wheel.GetFrame(RsiDirection.South, frame),
            UIBox2.FromDimensions(origin, new Vector2(side)), Held ? Color.FromHex("#fff0c7") : Color.White);

        var center = size / 2;
        var radius = side * 0.31f;
        var angle = -Value * MaximumAngle;
        var spokeColor = Held ? Color.FromHex("#e9b958") : Color.FromHex("#7f8f98");
        for (var i = 0; i < 3; i++)
        {
            var spoke = angle + i * MathF.Tau / 3 - MathF.PI / 2;
            var direction = new Vector2(MathF.Cos(spoke), MathF.Sin(spoke));
            var perpendicular = new Vector2(-direction.Y, direction.X) * 2.5f * UIScale;
            var end = center + direction * radius;
            handle.DrawLine(center - perpendicular, end - perpendicular, Color.FromHex("#172027"));
            handle.DrawLine(center + perpendicular, end + perpendicular, Color.FromHex("#172027"));
            handle.DrawLine(center, end, spokeColor);
        }
        handle.DrawCircle(center, side * 0.09f, Color.FromHex("#12191d"));
        handle.DrawCircle(center, side * 0.055f, Color.FromHex("#15bfca"));
    }
}
