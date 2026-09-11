// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;

namespace Content.Shared._NC.Vehicle;

/// <summary>Pure driveline/tyre math shared by prediction and server authority.</summary>
public static class NCVehicleDriveMath
{
    // Scalar validation avoids compiler-generated inline-array helpers rejected by the client sandbox.
    public static bool Positive(float value) => float.IsFinite(value) && value > 0;
    public static bool Nonnegative(float value) => float.IsFinite(value) && value >= 0;

    /// <summary>Signed angular drag crosses the atan2 seam without jumping a full turn.</summary>
    public static float WheelDragAngle(Vector2 previous, Vector2 next)
    {
        var cross = previous.X * next.Y - previous.Y * next.X;
        var dot = Vector2.Dot(previous, next);
        return float.IsFinite(cross) && float.IsFinite(dot) ? MathF.Atan2(cross, dot) : 0;
    }

    /// <summary>Rows cannot cross neutral in one input; columns move only in the neutral gate.</summary>
    public static (int Column, int Row) MoveLever(int column, int row, int horizontal, int vertical, int columns)
    {
        if (horizontal != 0 && row == 0)
            column = Math.Clamp(column + Math.Sign(horizontal), 0, columns - 1);
        if (vertical != 0)
            row = row == 0 ? Math.Sign(vertical) : row == Math.Sign(vertical) ? row : 0;
        return (column, row);
    }

    public static float Approach(float current, float target, float maximumDelta) =>
        current + Math.Clamp(target - current, -maximumDelta, maximumDelta);

    /// <summary>High RPM causes progressive quadratic wear; invalid tuning safely disables it.</summary>
    public static float HighRpmWearMultiplier(float rpm, float redline, float startFraction, float maximum)
    {
        if (!Positive(redline) || !float.IsFinite(rpm) || startFraction is < 0 or >= 1 || !Nonnegative(maximum))
            return 0;
        var excess = Math.Max(0, rpm / redline - startFraction) / (1 - startFraction);
        return Math.Min(maximum, excess * excess);
    }

    public static float Curve(IReadOnlyList<Vector2> points, float rpm)
    {
        if (points.Count == 0)
            return 0;
        if (rpm <= points[0].X)
            return points[0].Y;
        for (var i = 1; i < points.Count; i++)
        {
            var previous = points[i - 1];
            var next = points[i];
            if (rpm > next.X)
                continue;
            var span = next.X - previous.X;
            return span > 0 ? previous.Y + (next.Y - previous.Y) * (rpm - previous.X) / span : 0;
        }
        return points[^1].Y;
    }

    public static Vector2 LimitGrip(Vector2 force, float maximum)
    {
        var length = force.Length();
        return length > maximum && length > 0 ? force * (maximum / length) : force;
    }

    /// <summary>Support-plane load distribution balances weight and longitudinal/lateral moments.</summary>
    public static float NormalLoad(Vector2 offset, float xx, float yy, float xy, float totalWeight,
        Vector2 moment, int count)
    {
        var determinant = xx * yy - xy * xy;
        if (determinant <= 0)
            return totalWeight / count;
        var gradientX = (moment.X * yy - moment.Y * xy) / determinant;
        var gradientY = (moment.Y * xx - moment.X * xy) / determinant;
        return Math.Max(0, totalWeight / count + gradientX * offset.X + gradientY * offset.Y);
    }

    /// <summary>Implicit longitudinal coupling avoids exploding free-wheel speed at a low-speed stop.</summary>
    public static float WheelSpeed(float omega, float roadSpeed, float radius, float inertia,
        float driveTorque, float brakeTorque, float stiffness, float lowSpeed, float dt)
    {
        var coupling = stiffness / Math.Max(Math.Abs(roadSpeed), lowSpeed);
        var denominator = inertia + dt * coupling * radius * radius;
        var next = (inertia * omega + dt * (driveTorque + coupling * radius * roadSpeed)) / denominator;
        return Approach(next, 0, brakeTorque * dt / denominator);
    }
}
