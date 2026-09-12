// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.Prototype.Array;

namespace Content.Shared._NC.Vehicle.Prototypes;

/// <summary>Physical tuning in world units, seconds, kilograms and radians; no per-model code branches.</summary>
[Prototype("ncVehicleHandling")]
public sealed partial class NCVehicleHandlingPrototype : IPrototype, IInheritingPrototype
{
    [ParentDataField(typeof(AbstractPrototypeIdArraySerializer<NCVehicleHandlingPrototype>))]
    public string[]? Parents { get; private set; }
    [AbstractDataField, NeverPushInheritance]
    public bool Abstract { get; private set; }
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public List<NCVehicleWheelDefinition> Wheels = new();
    [DataField(required: true)] public List<Vector2> TorqueCurve = new();
    [DataField(required: true)] public float[] GearRatios = [];
    [DataField] public bool Automatic;
    [DataField] public bool Sequential;
    [DataField] public float AutomaticUpshiftRpm = 5500;
    [DataField] public float AutomaticDownshiftRpm = 1500;
    [DataField] public float AutomaticShiftSeconds = 0.4f;
    [DataField] public float SelectorMaximumSpeed = 0.5f;
    [DataField] public float ConverterStallRatio = 1.8f;
    [DataField] public float ConverterCoupling = 1.5f;
    [DataField] public float ParkingTorque = 5000;
    [DataField] public float DifferentialCoupling;
    [DataField] public float DifferentialTorqueLimit = 300;
    [DataField] public float CenterDifferentialCoupling;
    [DataField] public float CenterDifferentialTorqueLimit = 300;
    [DataField] public bool StabilityControl;
    [DataField] public float StabilityGain = 400;
    [DataField] public float StabilityBrakeLimit = 800;
    [DataField] public float Wheelbase = 2.2f;
    [DataField] public bool AntiLockBrakes;
    [DataField] public bool TractionControl;
    [DataField] public float AntiLockSlip = 0.15f;
    [DataField] public float TractionSlip = 0.15f;
    [DataField] public float TyreWearPerJoule = 0.000000002f;
    [DataField] public float SuspensionWearPerMeter = 0.0000002f;
    [DataField] public float DamagedBodyDrag = 1;
    [DataField(required: true)] public int[] ManualPattern = [];
    [DataField(required: true)] public float ShiftClutchThreshold;
    [DataField(required: true)] public float ShiftRpmTolerance;
    [DataField(required: true)] public float MisshiftWear;
    [DataField(required: true)] public float FinalDrive;
    [DataField(required: true)] public float DriveEfficiency;
    [DataField(required: true)] public float IdleRpm;
    [DataField] public float StarterTorque = 80;
    [DataField] public float StarterSeconds = 0.7f;
    [DataField] public float StarterTimeout = 3;
    [DataField] public float StartingRpm = 350;
    [DataField(required: true)] public float RedlineRpm;
    /// <summary>Fraction of redline where sustained high-RPM wear begins.</summary>
    [DataField] public float HighRpmWearStart = 0.9f;
    /// <summary>Engine condition lost per second at redline before part-specific wear sharing.</summary>
    [DataField] public float HighRpmWearPerSecond = 0.00025f;
    [DataField] public float MaximumHighRpmWearMultiplier = 12;
    [DataField(required: true)] public float StallRpm;
    /// <summary>Time below stall RPM before the running engine stops.</summary>
    [DataField] public float StallGraceSeconds = 1;
    [DataField(required: true)] public float EngineInertia;
    [DataField(required: true)] public float EngineDrag;
    [DataField(required: true)] public float ClutchTorque;
    [DataField(required: true)] public float ClutchCoupling;
    [DataField(required: true)] public float PedalRate;
    [DataField(required: true)] public float SteeringRate;
    [DataField(required: true)] public float SteeringAngle;
    [DataField(required: true)] public float SteeringReturn;
    [DataField(required: true)] public float SteeringWheelTravel;
    [DataField(required: true)] public float Gravity;
    [DataField] public float CenterOfGravityHeight = 0.45f;
    [DataField(required: true)] public float Friction;
    [DataField(required: true)] public float LongitudinalStiffness;
    [DataField(required: true)] public float LateralStiffness;
    [DataField(required: true)] public float LowSpeed;
    [DataField(required: true)] public float BrakeTorque;
    [DataField(required: true)] public float HandbrakeTorque;
    [DataField(required: true)] public float AerodynamicDrag;
    [DataField(required: true)] public float RollingResistance;
    [DataField(required: true)] public float FuelCapacity;
    [DataField(required: true)] public float FuelIdleRate;
    [DataField(required: true)] public float FuelLoadRate;
    [DataField(required: true)] public float InputTimeout;
    [DataField(required: true)] public float ExitSpeed;
    [DataField(required: true)] public Vector2 ExitOffset;
    [DataField(required: true)] public float ExitClearance;
    [DataField(required: true)] public float InputInterval;
    [DataField(required: true)] public float KeyboardSteeringRate;
    [DataField] public float CameraLookAheadStartSpeed = 4;
    [DataField] public float CameraLookAheadMaximumSpeed = 18;
    [DataField] public float CameraLookAheadMaximum = 2.5f;
    [DataField] public float ShiftAdviceUpFraction = 0.78f;
    [DataField] public float ShiftAdviceDownRpmFactor = 1.4f;
    [DataField] public float SlipWarning = 0.35f;
    [DataField(required: true)] public float MaximumStep;
    [DataField(required: true)] public int MaximumSubsteps;
}

[DataDefinition]
public sealed partial class NCVehicleWheelDefinition
{
    [DataField] public int Axle;
    [DataField(required: true)] public Vector2 Position;
    [DataField(required: true)] public float Radius;
    [DataField(required: true)] public float Inertia;
    [DataField(required: true)] public float DriveWeight;
    [DataField(required: true)] public float BrakeWeight;
    [DataField] public bool Steered;
    [DataField] public bool Handbrake;
}
