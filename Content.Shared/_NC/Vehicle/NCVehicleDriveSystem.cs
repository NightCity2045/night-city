// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Vehicle.Assembly;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared._NC.Vehicle.Prototypes;
using Content.Shared.Friction;
using Content.Shared.Gravity;
using Content.Shared.Movement.Events;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Shared._NC.Vehicle;

/// <summary>Applies virtual tyre forces before Robust integrates the single chassis body.</summary>
public sealed partial class NCVehicleDriveSystem : EntitySystem
{
    // Fixed scratch capacity bounds memory independently of content balance.
    private const int MaximumWheelCount = 64;
    // The physics event is serial. These buffers contain no replay state and are overwritten before use.
    // Managed arrays keep this path compatible with the unmodified client IL verifier.
    private readonly float[] _surfaceFriction = new float[MaximumWheelCount];
    private readonly float[] _initialWheelSpeeds = new float[MaximumWheelCount];
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private NCVehicleConditionSystem _condition = default!;
    [Dependency] private NCVehicleControlSystem _controls = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedGravitySystem _gravity = default!;
    [Dependency] private ITileDefinitionManager _tiles = default!;
    private readonly HashSet<EntityUid> _vehicles = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCVehicleDriveComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<NCVehicleDriveComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<NCVehicleDriveComponent, TileFrictionEvent>(OnTileFriction);
        SubscribeLocalEvent<PhysicsUpdateBeforeSolveEvent>(OnPhysicsStep);
    }

    private void OnStartup(Entity<NCVehicleDriveComponent> ent, ref ComponentStartup args) => _vehicles.Add(ent);
    private void OnTileFriction(Entity<NCVehicleDriveComponent> ent, ref TileFrictionEvent args)
    {
        // Tyre forces already account for road friction. Generic sliding-body damping would brake the car twice.
        if (!ent.Comp.Overturned)
            args.Modifier = 0;
    }
    private void OnShutdown(Entity<NCVehicleDriveComponent> ent, ref ComponentShutdown args)
    {
        var ev = new NCVehicleDriveShutdownEvent();
        RaiseLocalEvent(ent.Owner, ref ev);
        _vehicles.Remove(ent);
    }

    private void OnPhysicsStep(ref PhysicsUpdateBeforeSolveEvent args)
    {
        var surfaces = _surfaceFriction;
        // Only registered vehicles are considered. Parked sleeping bodies do no tyre/engine work.
        foreach (var uid in _vehicles)
        {
            if (IsPaused(uid) || !TryComp<NCVehicleDriveComponent>(uid, out var drive) ||
                !TryComp<PhysicsComponent>(uid, out var body) || !body.CanCollide ||
                (args.Prediction && !body.Predict) ||
                !_prototypes.TryIndex(drive.Handling, out var tuning))
                continue;

            if (!Valid(tuning) || drive.WheelSpeeds.Length != tuning.Wheels.Count)
                continue;

            if (_timing.CurTime - drive.LastInputTime > TimeSpan.FromSeconds(tuning.InputTimeout) || drive.Driver == null)
            {
                drive.GasHeld = false;
                drive.BrakeHeld = false;
                drive.ClutchHeld = false;
                drive.SteeringHeld = false;
            }

            var dt = args.DeltaTime;
            if (!float.IsFinite(dt) || dt <= 0)
                continue;
            _condition.Advance(uid, body.LinearVelocity.Length(), dt);
            if (!body.Awake && !drive.EngineRunning && !drive.Cranking && drive.Driver == null && drive.PendingShift == null)
                continue;
            if (tuning.Automatic && drive.Selector == NCVehicleSelector.Drive && _timing.CurTime >= drive.NextAutomaticShift)
            {
                var previous = drive.Gear;
                if (drive.EngineRpm > tuning.AutomaticUpshiftRpm)
                    drive.Gear = Math.Min(tuning.GearRatios.Length - 2, drive.Gear + 1);
                else if (drive.EngineRpm < tuning.AutomaticDownshiftRpm)
                    drive.Gear = Math.Max(1, drive.Gear - 1);
                if (drive.Gear != previous)
                    drive.NextAutomaticShift = _timing.CurTime + TimeSpan.FromSeconds(tuning.AutomaticShiftSeconds);
            }
            if (drive.AssemblyInertia > 0)
            {
                // These derived physics values are not networked by PhysicsComponent itself.
                _physics.SetLocalCenter(uid, body, drive.AssemblyCenter);
                _physics.SetInertia(uid, body, drive.AssemblyInertia + body.Mass * drive.AssemblyCenter.LengthSquared());
            }
            drive.Throttle = NCVehicleDriveMath.Approach(drive.Throttle, drive.GasHeld ? 1 : 0, tuning.PedalRate * dt);
            drive.Brake = NCVehicleDriveMath.Approach(drive.Brake, drive.BrakeHeld ? 1 : 0, tuning.PedalRate * dt);
            drive.Clutch = NCVehicleDriveMath.Approach(drive.Clutch, drive.ClutchHeld ? 1 : 0, tuning.PedalRate * dt);
            _controls.TryCompleteShift(uid);
            // An unheld rack returns with road speed; it does not snap to center at a stop.
            if (!drive.SteeringHeld)
                drive.SteeringTarget = NCVehicleDriveMath.Approach(drive.Steering, 0,
                    tuning.SteeringReturn * body.LinearVelocity.Length() * dt);
            var assist = TryComp<NCVehicleConditionComponent>(uid, out var conditionState) ?
                conditionState.UnassistedSteering + (1 - conditionState.UnassistedSteering) * conditionState.SteeringFluidFraction *
                    NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.SteeringAssist) : 1;
            drive.Steering = NCVehicleDriveMath.Approach(drive.Steering, drive.SteeringTarget, tuning.SteeringRate * assist * dt);

            // A delayed tick cannot create an unbounded catch-up loop.
            var steps = (int) Math.Clamp(MathF.Ceiling(dt / tuning.MaximumStep), 1, tuning.MaximumSubsteps);
            var step = dt / steps;
            var rotation = _transform.GetWorldRotation(uid);
            for (var i = 0; i < tuning.Wheels.Count; i++)
                surfaces[i] = SurfaceFriction(uid, tuning.Wheels[i].Position, body);
            var fuelBeforeStep = drive.Fuel;
            var acceleration = (-rotation).RotateVec((body.LinearVelocity - drive.PreviousVelocity) / dt);
            var rollover = new NCVehicleRolloverRiskEvent(acceleration.X, body.LinearVelocity.Length());
            RaiseLocalEvent(uid, ref rollover);
            drive.PreviousVelocity = body.LinearVelocity;
            for (var substep = 0; substep < steps; substep++)
                Step(uid, drive, body, tuning, rotation, acceleration, surfaces, step, 1f / steps);
            if (drive.Fuel < fuelBeforeStep)
            {
                var consumed = new NCVehicleFuelConsumedEvent(fuelBeforeStep - drive.Fuel);
                RaiseLocalEvent(uid, ref consumed);
            }
            Dirty(uid, drive);
        }
    }

    private void Step(EntityUid uid, NCVehicleDriveComponent drive, PhysicsComponent body,
        NCVehicleHandlingPrototype tuning, Angle rotation, Vector2 acceleration, float[] surfaces, float dt, float forceWeight)
    {
        var initialWheelSpeeds = _initialWheelSpeeds;
        Array.Copy(drive.WheelSpeeds, initialWheelSpeeds, drive.WheelSpeeds.Length);
        var ratio = drive.Gear >= 0 && drive.Gear < tuning.GearRatios.Length ? tuning.GearRatios[drive.Gear] : 0;
        var driveWeight = 0f;
        var wheelOmega = 0f;
        for (var i = 0; i < tuning.Wheels.Count; i++)
        {
            driveWeight += tuning.Wheels[i].DriveWeight;
            wheelOmega += drive.WheelSpeeds[i] * tuning.Wheels[i].DriveWeight;
        }
        if (driveWeight > 0)
            wheelOmega /= driveWeight;

        var calibration = drive.Calibration is { } configured && _prototypes.TryIndex(configured, out var selected) &&
            NCVehicleCalibrationValidator.IsValid(selected) ? selected : null;
        var engineOmega = drive.EngineRpm * MathF.Tau / 60;
        var shaftOmega = wheelOmega * ratio * tuning.FinalDrive;
        var clutchTorque = ratio == 0 ? 0 : Math.Clamp((engineOmega - shaftOmega) * tuning.ClutchCoupling,
            -tuning.ClutchTorque, tuning.ClutchTorque) * (1 - drive.Clutch);
        if (tuning.Automatic)
            clutchTorque = ratio == 0 || !drive.EngineRunning ? 0 :
                Math.Clamp((engineOmega - shaftOmega) * tuning.ConverterCoupling, 0, tuning.ClutchTorque);
        clutchTorque *= NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Transmission);
        var engineTorque = 0f;
        TryComp<NCVehicleConditionComponent>(uid, out var condition);
        if (drive.EngineRunning && drive.Fuel > 0 && drive.AssemblyReady)
        {
            var idle = Math.Clamp((tuning.IdleRpm - drive.EngineRpm) / tuning.IdleRpm, 0, 1);
            engineTorque = NCVehicleDriveMath.Curve(tuning.TorqueCurve, drive.EngineRpm) * Math.Max(drive.Throttle, idle);
            engineTorque *= drive.AssemblyPower * (condition?.EnginePower ?? 1) * (calibration?.EngineMultiplier ?? 1);
            if (drive.EngineRpm >= tuning.RedlineRpm)
                engineTorque = 0;
            drive.Fuel = Math.Max(0, drive.Fuel - dt * (tuning.FuelIdleRate + tuning.FuelLoadRate * drive.Throttle) * (calibration?.FuelMultiplier ?? 1));
        }
        else
            drive.EngineRunning = false;

        if (drive.Cranking)
        {
            drive.CrankElapsed += dt;
            if (!drive.AssemblyReady || drive.Fuel <= 0 || drive.CrankElapsed > tuning.StarterTimeout ||
                (condition != null && condition.BatteryCharge <= 0) ||
                NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Starting) <= 0 ||
                NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Ignition) <= 0)
                drive.Cranking = false;
            else
                engineTorque += tuning.StarterTorque * NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Starting);
        }

        engineOmega = Math.Max(0, engineOmega + dt * (engineTorque - tuning.EngineDrag * engineOmega - clutchTorque) / tuning.EngineInertia);
        drive.EngineRpm = engineOmega * 60 / MathF.Tau;
        if (drive.Cranking && drive.CrankElapsed >= tuning.StarterSeconds && drive.EngineRpm >= tuning.StartingRpm)
        {
            drive.Cranking = false;
            drive.EngineRunning = true;
        }
        if (drive.EngineRunning && drive.EngineRpm < tuning.StallRpm)
        {
            drive.StallElapsed += dt;
            if (drive.StallElapsed >= tuning.StallGraceSeconds)
                drive.EngineRunning = false;
        }
        else
            drive.StallElapsed = 0;

        var wheelTorque = clutchTorque * ratio * tuning.FinalDrive * tuning.DriveEfficiency;
        if (tuning.Automatic && engineOmega > 0)
            wheelTorque *= 1 + (tuning.ConverterStallRatio - 1) * (1 - Math.Clamp(shaftOmega / engineOmega, 0, 1));
        var centroid = Vector2.Zero;
        foreach (var wheel in tuning.Wheels)
            centroid += wheel.Position;
        centroid /= tuning.Wheels.Count;
        var xx = 0f;
        var yy = 0f;
        var xy = 0f;
        foreach (var wheel in tuning.Wheels)
        {
            var contact = wheel.Position - centroid;
            xx += contact.X * contact.X;
            yy += contact.Y * contact.Y;
            xy += contact.X * contact.Y;
        }
        var weight = body.Mass * tuning.Gravity;
        var moment = weight * (drive.AssemblyCenter - centroid) - body.Mass * tuning.CenterOfGravityHeight * acceleration;
        var totalLoad = 0f;
        foreach (var wheel in tuning.Wheels)
            totalLoad += NCVehicleDriveMath.NormalLoad(wheel.Position - centroid, xx, yy, xy, weight, moment, tuning.Wheels.Count);
        for (var i = 0; i < tuning.Wheels.Count; i++)
        {
            var wheel = tuning.Wheels[i];
            var normalLoad = NCVehicleDriveMath.NormalLoad(wheel.Position - centroid, xx, yy, xy, weight, moment, tuning.Wheels.Count);
            var grip = totalLoad > 0 ? normalLoad * weight / totalLoad * tuning.Friction * surfaces[i] : 0;
            if (drive.Overturned)
                grip = 0;
            grip *= NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Grip, i) *
                NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Suspension, i);
            var offset = rotation.RotateVec(wheel.Position - drive.AssemblyCenter);
            var velocity = body.LinearVelocity + new Vector2(-offset.Y, offset.X) * body.AngularVelocity;
            var heading = rotation + new Angle(wheel.Steered ? drive.Steering * tuning.SteeringAngle * (calibration?.SteeringMultiplier ?? 1) *
                NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Steering) : 0);
            var forward = heading.RotateVec(Vector2.UnitY);
            var right = heading.RotateVec(Vector2.UnitX);
            var longitudinal = Vector2.Dot(velocity, forward);
            var lateral = Vector2.Dot(velocity, right);
            var torque = driveWeight > 0 ? wheelTorque * wheel.DriveWeight / driveWeight : 0;
            if (wheel.DriveWeight > 0 && driveWeight > 0)
            {
                // Equal/opposite pair torques conserve axle torque; coupling tunes open/LSD/locked response.
                for (var other = 0; other < tuning.Wheels.Count; other++)
                {
                    if (other == i || tuning.Wheels[other].DriveWeight <= 0)
                        continue;
                    var sameAxle = wheel.Axle == tuning.Wheels[other].Axle;
                    var coupling = (sameAxle ? tuning.DifferentialCoupling : tuning.CenterDifferentialCoupling) * (calibration?.DifferentialMultiplier ?? 1);
                    var limit = sameAxle ? tuning.DifferentialTorqueLimit : tuning.CenterDifferentialTorqueLimit;
                    torque += Math.Clamp((initialWheelSpeeds[other] - initialWheelSpeeds[i]) * coupling, -limit, limit);
                }
            }
            torque *= NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Drive, i);
            var brakeCalibration = wheel.Steered ? calibration?.FrontBrakeMultiplier ?? 1 : calibration?.RearBrakeMultiplier ?? 1;
            var braking = drive.Brake * tuning.BrakeTorque * wheel.BrakeWeight * brakeCalibration * (condition?.BrakePower ?? 1) *
                NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Braking, i);
            var previousOmega = drive.WheelSpeeds[i];
            var electronics = (condition == null || condition.BatteryCharge > 0) &&
                NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Electronics) > 0;
            var tyreSpeed = Math.Abs(previousOmega * wheel.Radius);
            var roadSpeed = Math.Abs(longitudinal);
            if (electronics && tuning.StabilityControl && roadSpeed > tuning.LowSpeed)
            {
                // ESC requests selective tyre braking; available grip still bounds its yaw correction.
                var targetYaw = longitudinal * MathF.Tan(drive.Steering * tuning.SteeringAngle * (calibration?.SteeringMultiplier ?? 1)) / tuning.Wheelbase;
                var yawError = targetYaw - body.AngularVelocity;
                if (wheel.Steered && wheel.Position.X * yawError < 0)
                    braking += Math.Min(tuning.StabilityBrakeLimit, Math.Abs(yawError) * tuning.StabilityGain) *
                        (condition?.BrakePower ?? 1) * NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Braking, i);
            }
            if (electronics && tuning.TractionControl && tyreSpeed > roadSpeed + tuning.LowSpeed * tuning.TractionSlip &&
                torque * previousOmega > 0)
                torque *= Math.Clamp((roadSpeed + tuning.LowSpeed * tuning.TractionSlip) / tyreSpeed, 0, 1);
            if (electronics && tuning.AntiLockBrakes && roadSpeed > tuning.LowSpeed && drive.Brake > 0)
                braking *= Math.Clamp(tyreSpeed / (roadSpeed * (1 - tuning.AntiLockSlip)), 0, 1);
            // ABS releases hydraulic pressure only; the mechanical handbrake and parking pawl remain engaged.
            if (wheel.Handbrake && drive.HandbrakeHeld)
                braking += tuning.HandbrakeTorque * NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Braking, i);
            if (tuning.Automatic && drive.Selector == NCVehicleSelector.Park && wheel.DriveWeight > 0)
                braking += tuning.ParkingTorque;
            var coupledOmega = NCVehicleDriveMath.WheelSpeed(previousOmega, longitudinal,
                wheel.Radius, wheel.Inertia, torque, braking, tuning.LongitudinalStiffness, tuning.LowSpeed, dt);
            var slip = (coupledOmega * wheel.Radius - longitudinal) / Math.Max(Math.Abs(longitudinal), tuning.LowSpeed);
            var force = NCVehicleDriveMath.LimitGrip(new Vector2(
                -lateral * tuning.LateralStiffness,
                slip * tuning.LongitudinalStiffness), grip);
            if (condition != null && braking > 0)
                condition.BrakeHeat += Math.Abs(braking * coupledOmega) * dt;
            if (i < drive.WheelWear.Length)
            {
                // Slip work wears this corner only; distance adds the slower fatigue of the suspension.
                drive.WheelWear[i] += dt * (Math.Abs(force.Y * (coupledOmega * wheel.Radius - longitudinal)) *
                    tuning.TyreWearPerJoule + velocity.Length() * tuning.SuspensionWearPerMeter);
            }
            // Integrate the actual limited contact reaction. Static brakes cannot drive a locked wheel backwards.
            drive.WheelSpeeds[i] = NCVehicleDriveMath.Approach(
                previousOmega + (torque - force.Y * wheel.Radius) * dt / wheel.Inertia,
                0, braking * dt / wheel.Inertia);
            var worldForce = (right * force.X + forward * force.Y) * forceWeight;
            _physics.ApplyForce(uid, worldForce, body: body);
            _physics.ApplyTorque(uid, offset.X * worldForce.Y - offset.Y * worldForce.X, body: body);
        }

        // Resistances dissipate energy and never substitute for a requested service brake.
        var speed = body.LinearVelocity.Length();
        var bodyDrag = 1 + tuning.DamagedBodyDrag * (1 - Math.Clamp(
            NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Aerodynamics), 0, 1));
        var drag = body.LinearVelocity * (tuning.AerodynamicDrag * bodyDrag * speed + tuning.RollingResistance);
        _physics.ApplyForce(uid, -drag * forceWeight, body: body);
    }

    public static bool Valid(NCVehicleHandlingPrototype value)
    {
        if (!NCVehicleDriveMath.Positive(value.MaximumStep) ||
            !NCVehicleDriveMath.Positive(value.LowSpeed) ||
            !NCVehicleDriveMath.Positive(value.EngineInertia) ||
            !NCVehicleDriveMath.Positive(value.IdleRpm) ||
            !NCVehicleDriveMath.Positive(value.RedlineRpm) ||
            !NCVehicleDriveMath.Positive(value.FinalDrive) ||
            !NCVehicleDriveMath.Positive(value.PedalRate) ||
            !NCVehicleDriveMath.Positive(value.SteeringRate) ||
            !NCVehicleDriveMath.Positive(value.Gravity) ||
            !NCVehicleDriveMath.Positive(value.InputInterval) ||
            !NCVehicleDriveMath.Positive(value.InputTimeout) ||
            !NCVehicleDriveMath.Positive(value.FuelCapacity) ||
            !NCVehicleDriveMath.Positive(value.ExitClearance) ||
            !NCVehicleDriveMath.Positive(value.SteeringWheelTravel))
            return false;
        if (!NCVehicleDriveMath.Nonnegative(value.DriveEfficiency) ||
            !NCVehicleDriveMath.Positive(value.StarterSeconds) ||
            !NCVehicleDriveMath.Positive(value.StarterTorque) ||
            !NCVehicleDriveMath.Positive(value.StartingRpm) ||
            !NCVehicleDriveMath.Positive(value.StarterTimeout) || value.StarterTimeout < value.StarterSeconds ||
            !NCVehicleDriveMath.Positive(value.Wheelbase) ||
            !NCVehicleDriveMath.Nonnegative(value.CenterDifferentialCoupling) ||
            !NCVehicleDriveMath.Nonnegative(value.CenterDifferentialTorqueLimit) ||
            !NCVehicleDriveMath.Nonnegative(value.StabilityGain) ||
            !NCVehicleDriveMath.Nonnegative(value.StabilityBrakeLimit) ||
            !NCVehicleDriveMath.Nonnegative(value.TyreWearPerJoule) ||
            !NCVehicleDriveMath.Nonnegative(value.SuspensionWearPerMeter) ||
            !NCVehicleDriveMath.Nonnegative(value.DamagedBodyDrag) ||
            !NCVehicleDriveMath.Nonnegative(value.HighRpmWearPerSecond) ||
            !NCVehicleDriveMath.Nonnegative(value.MaximumHighRpmWearMultiplier) ||
            !NCVehicleDriveMath.Nonnegative(value.StallRpm) ||
            !NCVehicleDriveMath.Nonnegative(value.StallGraceSeconds) ||
            !NCVehicleDriveMath.Nonnegative(value.EngineDrag) ||
            !NCVehicleDriveMath.Nonnegative(value.ClutchTorque) ||
            !NCVehicleDriveMath.Nonnegative(value.ClutchCoupling) ||
            !NCVehicleDriveMath.Nonnegative(value.SteeringAngle) ||
            !NCVehicleDriveMath.Nonnegative(value.SteeringReturn) ||
            !NCVehicleDriveMath.Nonnegative(value.Friction) ||
            !NCVehicleDriveMath.Nonnegative(value.LongitudinalStiffness) ||
            !NCVehicleDriveMath.Nonnegative(value.LateralStiffness) ||
            !NCVehicleDriveMath.Nonnegative(value.BrakeTorque) ||
            !NCVehicleDriveMath.Nonnegative(value.HandbrakeTorque) ||
            !NCVehicleDriveMath.Nonnegative(value.AerodynamicDrag) ||
            !NCVehicleDriveMath.Nonnegative(value.RollingResistance) ||
            !NCVehicleDriveMath.Nonnegative(value.FuelIdleRate) ||
            !NCVehicleDriveMath.Nonnegative(value.FuelLoadRate) ||
            !NCVehicleDriveMath.Nonnegative(value.ExitSpeed) ||
            !NCVehicleDriveMath.Nonnegative(value.KeyboardSteeringRate) ||
            !NCVehicleDriveMath.Nonnegative(value.ShiftClutchThreshold) ||
            !NCVehicleDriveMath.Nonnegative(value.ShiftRpmTolerance) ||
            !NCVehicleDriveMath.Nonnegative(value.MisshiftWear) ||
            !NCVehicleDriveMath.Nonnegative(value.CenterOfGravityHeight) ||
            !NCVehicleDriveMath.Nonnegative(value.AutomaticUpshiftRpm) ||
            !NCVehicleDriveMath.Nonnegative(value.AutomaticDownshiftRpm) ||
            !NCVehicleDriveMath.Nonnegative(value.AutomaticShiftSeconds) ||
            !NCVehicleDriveMath.Nonnegative(value.SelectorMaximumSpeed) ||
            !NCVehicleDriveMath.Nonnegative(value.ConverterStallRatio) ||
            !NCVehicleDriveMath.Nonnegative(value.ConverterCoupling) ||
            !NCVehicleDriveMath.Nonnegative(value.ParkingTorque) ||
            !NCVehicleDriveMath.Nonnegative(value.DifferentialCoupling) ||
            !NCVehicleDriveMath.Nonnegative(value.DifferentialTorqueLimit) ||
            !NCVehicleDriveMath.Nonnegative(value.TractionSlip) ||
            !NCVehicleDriveMath.Nonnegative(value.AntiLockSlip))
            return false;
        if (value.AntiLockSlip >= 1 || value.HighRpmWearStart is < 0 or >= 1 || (value.Automatic && value.Sequential) || value.AutomaticUpshiftRpm <= value.AutomaticDownshiftRpm ||
            value.ConverterStallRatio < 1 || value.ManualPattern.Length < 2 || value.ManualPattern.Length % 2 != 0 || value.ShiftClutchThreshold > 1 ||
            value.Wheels.Count is 0 or > MaximumWheelCount || value.MaximumSubsteps is <= 0 or > 64 || value.TorqueCurve.Count == 0 ||
            value.GearRatios.Length < 3 || value.GearRatios[0] != 0 || value.GearRatios[^1] >= 0 ||
            value.StallRpm >= value.IdleRpm || value.IdleRpm >= value.RedlineRpm || value.DriveEfficiency > 1 ||
            !float.IsFinite(value.ExitOffset.X) || !float.IsFinite(value.ExitOffset.Y))
            return false;
        foreach (var gear in value.ManualPattern)
            if (gear < 0 || gear >= value.GearRatios.Length - 1)
                return false;
        for (var i = 0; i < value.GearRatios.Length; i++)
            if (!float.IsFinite(value.GearRatios[i]) ||
                (i > 0 && i < value.GearRatios.Length - 1 && value.GearRatios[i] <= 0))
                return false;
        var previousRpm = -1f;
        foreach (var point in value.TorqueCurve)
        {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || point.X < 0 || point.X <= previousRpm || point.Y < 0)
                return false;
            previousRpm = point.X;
        }
        foreach (var wheel in value.Wheels)
            if (!float.IsFinite(wheel.Radius) || !float.IsFinite(wheel.Inertia) ||
                !float.IsFinite(wheel.DriveWeight) || !float.IsFinite(wheel.BrakeWeight) ||
                !float.IsFinite(wheel.Position.X) || !float.IsFinite(wheel.Position.Y) ||
                wheel.Radius <= 0 || wheel.Inertia <= 0 || wheel.DriveWeight < 0 || wheel.BrakeWeight < 0)
                return false;
        return true;
    }

    private float SurfaceFriction(EntityUid uid, Vector2 position, PhysicsComponent body)
    {
        if (body.BodyStatus != BodyStatus.OnGround || _gravity.IsWeightless(uid))
            return 0;
        var coordinates = _transform.ToMapCoordinates(new EntityCoordinates(uid, position));
        if (!_maps.TryFindGridAt(coordinates, out var gridUid, out var grid))
        {
            return TryComp<TileFrictionModifierComponent>(Transform(uid).MapUid, out var mapFriction)
                ? float.IsFinite(mapFriction.Modifier) ? Math.Max(0, mapFriction.Modifier) : 0 : 1;
        }
        var tile = _maps.GetTileRef(gridUid, grid, coordinates);
        if (tile.Tile.IsEmpty && !HasComp<MapComponent>(gridUid))
            return 0;
        var friction = tile.Tile.IsEmpty ? 1 : _tiles[tile.Tile.TypeId].Friction;
        // Existing puddles modify their anchored tile; sample each wheel, not the chassis center.
        var anchored = _maps.GetAnchoredEntitiesEnumerator(gridUid, grid, tile.GridIndices);
        while (anchored.MoveNext(out var entity))
            if (TryComp<TileFrictionModifierComponent>(entity, out var modifier))
                friction *= modifier.Modifier;
        return float.IsFinite(friction) ? Math.Max(0, friction) : 0;
    }
}
