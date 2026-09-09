// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Vehicle;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Input;

namespace Content.Client._NC.Vehicle;

/// <summary>Circular UI drag supports multiple turns within the panel, using only public Robust UI APIs.</summary>
public sealed class NCVehicleSteeringWheel : BaseButton
{
    public float Value { get; set; }
    public float MaximumAngle { get; set; } = MathF.Tau;
    public bool Held { get; private set; }
    private Vector2? _lastDirection;

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
        // Passing through the hub releases the angular reference; it must not cause a half-turn jump.
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
        var center = (Vector2) PixelSize / 2;
        var radius = Math.Min(PixelSize.X, PixelSize.Y) * 0.43f;
        handle.DrawCircle(center, radius, Held ? Color.FromHex("#ddad55") : Color.FromHex("#8796a2"));
        handle.DrawCircle(center, radius - 7 * UIScale, Color.FromHex("#172027"));
        var angle = -Value * MaximumAngle;
        for (var i = 0; i < 3; i++)
        {
            var spoke = angle + i * MathF.Tau / 3 - MathF.PI / 2;
            handle.DrawLine(center, center + new Vector2(MathF.Cos(spoke), MathF.Sin(spoke)) * radius,
                i == 0 ? Color.FromHex("#f3c66b") : Color.FromHex("#8796a2"));
        }
        handle.DrawCircle(center, 12 * UIScale, Color.FromHex("#394c5c"));
    }
}
