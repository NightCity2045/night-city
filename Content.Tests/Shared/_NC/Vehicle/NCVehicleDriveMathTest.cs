// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Numerics;
using Content.Shared._NC.Vehicle;
using NUnit.Framework;

namespace Content.Tests.Shared._NC.Vehicle;

[TestFixture]
[TestOf(typeof(NCVehicleDriveMath))]
public sealed class NCVehicleDriveMathTest
{
    [Test]
    public void CircularSteeringCrossesTheAngleSeamAndCanAccumulateSeveralTurns()
    {
        var total = 0f;
        var previous = Vector2.UnitX;
        for (var i = 1; i <= 144; i++)
        {
            var angle = i * MathF.Tau / 48;
            var next = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            total += NCVehicleDriveMath.WheelDragAngle(previous, next);
            previous = next;
        }
        Assert.That(total, Is.EqualTo(3 * MathF.Tau).Within(0.0001f));
        Assert.That(NCVehicleDriveMath.WheelDragAngle(Vector2.UnitY, Vector2.UnitX), Is.EqualTo(-MathF.PI / 2));
        Assert.That(NCVehicleDriveMath.WheelDragAngle(Vector2.Zero, Vector2.UnitX), Is.Zero);
    }

    [Test]
    public void ManualLeverMustCrossNeutralAndCannotMoveSidewaysInGear()
    {
        Assert.That(NCVehicleDriveMath.MoveLever(0, -1, 1, 0, 3), Is.EqualTo((0, -1)));
        Assert.That(NCVehicleDriveMath.MoveLever(0, -1, 0, 1, 3), Is.EqualTo((0, 0)));
        Assert.That(NCVehicleDriveMath.MoveLever(0, 0, 0, 1, 3), Is.EqualTo((0, 1)));
        Assert.That(NCVehicleDriveMath.MoveLever(0, 0, -1, 0, 3), Is.EqualTo((0, 0)));
    }

    [Test]
    public void AccelerationTransfersLoadRearwardWithoutCreatingWeight()
    {
        var moment = new Vector2(0, -2000);
        var front = NCVehicleDriveMath.NormalLoad(new Vector2(1, 1), 4, 4, 0, 10000, moment, 4);
        var rear = NCVehicleDriveMath.NormalLoad(new Vector2(1, -1), 4, 4, 0, 10000, moment, 4);
        Assert.That(front, Is.EqualTo(2000));
        Assert.That(rear, Is.EqualTo(3000));
        Assert.That(2 * (front + rear), Is.EqualTo(10000));
    }

    [TestCase(0f)]
    [TestCase(0.01f)]
    [TestCase(-0.01f)]
    [TestCase(30f)]
    [TestCase(-30f)]
    public void FreeWheelConvergesToRoadSpeed(float roadSpeed)
    {
        var omega = 0f;
        for (var i = 0; i < 2000; i++)
            omega = NCVehicleDriveMath.WheelSpeed(omega, roadSpeed, 0.31f, 1.4f, 0, 0, 5500, 2, 0.005f);
        Assert.That(omega * 0.31f, Is.EqualTo(roadSpeed).Within(0.001f));
    }

    [TestCase(100f)]
    [TestCase(-100f)]
    public void StationaryBrakingDoesNotReverseWheel(float initial)
    {
        var omega = initial;
        for (var i = 0; i < 100; i++)
        {
            var next = NCVehicleDriveMath.WheelSpeed(omega, 0, 0.31f, 1.4f, 0, 2000, 5500, 2, 0.005f);
            Assert.That(Math.Abs(next), Is.LessThanOrEqualTo(Math.Abs(omega)));
            Assert.That(next * initial, Is.GreaterThanOrEqualTo(0));
            omega = next;
        }
        Assert.That(omega, Is.Zero);
    }

    [Test]
    public void CombinedGripPreservesDirectionWithinFrictionCircle()
    {
        var force = NCVehicleDriveMath.LimitGrip(new Vector2(3000, 4000), 1000);
        Assert.That(force.X, Is.EqualTo(600).Within(0.001));
        Assert.That(force.Y, Is.EqualTo(800).Within(0.001));
        Assert.That(NCVehicleDriveMath.LimitGrip(Vector2.Zero, 0), Is.EqualTo(Vector2.Zero));
    }

    [Test]
    public void TorqueCurveInterpolatesAndClampsEndpoints()
    {
        Vector2[] points = [new(0, 100), new(1000, 200), new(2000, 0)];
        Assert.That(NCVehicleDriveMath.Curve(points, 500), Is.EqualTo(150));
        Assert.That(NCVehicleDriveMath.Curve(points, 1500), Is.EqualTo(100));
        Assert.That(NCVehicleDriveMath.Curve(points, -1), Is.EqualTo(100));
        Assert.That(NCVehicleDriveMath.Curve(points, 3000), Is.Zero);
    }

    [Test]
    public void ControlSequenceRejectsDuplicatesAndAcceptsWraparound()
    {
        Assert.That(NCVehicleControlSystem.Newer(1, 1), Is.False);
        Assert.That(NCVehicleControlSystem.Newer(9, 10), Is.False);
        Assert.That(NCVehicleControlSystem.Newer(0, uint.MaxValue), Is.True);
        Assert.That(NCVehicleControlSystem.Newer(uint.MaxValue, 0), Is.False);
    }

    [Test]
    public void HighRpmWearStartsGraduallyAndIsCapped()
    {
        Assert.That(NCVehicleDriveMath.HighRpmWearMultiplier(6000, 7000, 0.9f, 12), Is.Zero);
        Assert.That(NCVehicleDriveMath.HighRpmWearMultiplier(6650, 7000, 0.9f, 12), Is.EqualTo(0.25f).Within(0.001f));
        Assert.That(NCVehicleDriveMath.HighRpmWearMultiplier(7000, 7000, 0.9f, 12), Is.EqualTo(1).Within(0.001f));
        Assert.That(NCVehicleDriveMath.HighRpmWearMultiplier(14000, 7000, 0.9f, 12), Is.EqualTo(12));
    }
}
