// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle.Prototypes;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using System.Numerics;

namespace Content.Shared._NC.Vehicle.Components;

/// <summary>Replayable control and driveline state. PhysicsComponent alone owns pose and body velocity.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(raiseAfterAutoHandleState: true)]
public sealed partial class NCVehicleDriveComponent : Component
{
    [DataField(required: true), AutoNetworkedField] public ProtoId<NCVehicleHandlingPrototype> Handling;
    [DataField, AutoNetworkedField] public ProtoId<NCVehicleTuningPrototype>? Calibration;
    [AutoNetworkedField] public EntityUid? Driver;
    [AutoNetworkedField] public uint Authority;
    [AutoNetworkedField] public uint LastInput;
    [AutoNetworkedField] public TimeSpan LastInputTime;
    [AutoNetworkedField] public float SteeringTarget;
    [AutoNetworkedField] public float Steering;
    [AutoNetworkedField] public bool SteeringHeld;
    [AutoNetworkedField] public bool GasHeld;
    [AutoNetworkedField] public bool BrakeHeld;
    [AutoNetworkedField] public bool ClutchHeld;
    [AutoNetworkedField] public bool HandbrakeHeld;
    [AutoNetworkedField] public float Throttle;
    [AutoNetworkedField] public float Brake;
    [AutoNetworkedField] public float Clutch;
    [AutoNetworkedField] public bool EngineRunning;
    [AutoNetworkedField] public bool Cranking;
    [AutoNetworkedField] public bool Overturned;
    [AutoNetworkedField] public float CrankElapsed;
    [AutoNetworkedField] public float EngineRpm;
    [AutoNetworkedField] public float StallElapsed;
    [AutoNetworkedField] public int Gear;
    [AutoNetworkedField] public NCVehicleDriverAction? PendingShift;
    [AutoNetworkedField] public NCVehicleShiftStatus ShiftStatus;
    [AutoNetworkedField] public NCVehicleSelector Selector;
    [AutoNetworkedField] public TimeSpan NextAutomaticShift;
    [AutoNetworkedField] public int LeverColumn = 1;
    [AutoNetworkedField] public int LeverRow;
    [AutoNetworkedField] public bool ReverseArmed;
    [AutoNetworkedField] public float GearboxWear;
    [DataField, AutoNetworkedField] public float Fuel;
    [AutoNetworkedField] public float[] WheelSpeeds = [];
    [AutoNetworkedField] public bool AssemblyReady;
    [AutoNetworkedField] public float AssemblyPower = 1;
    [AutoNetworkedField] public float[] AssemblyFunctions = [];
    [AutoNetworkedField] public float[] WheelWear = [];
    [AutoNetworkedField] public float LongitudinalSlip;
    [AutoNetworkedField] public float LateralSlip;
    [AutoNetworkedField] public Vector2 AssemblyCenter;
    [AutoNetworkedField] public float AssemblyInertia;
    [AutoNetworkedField] public Vector2 PreviousVelocity;
}
