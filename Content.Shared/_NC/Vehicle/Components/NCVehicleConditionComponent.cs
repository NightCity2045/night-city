// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.GameStates;

namespace Content.Shared._NC.Vehicle.Components;

/// <summary>Thermal reservoirs and electrical storage; all rates are prototype calibration.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(raiseAfterAutoHandleState: true)]
public sealed partial class NCVehicleConditionComponent : Component
{
    [DataField, AutoNetworkedField] public float EngineTemperature;
    [DataField, AutoNetworkedField] public float BrakeTemperature;
    [DataField, AutoNetworkedField] public float BatteryCharge;
    [AutoNetworkedField] public float OilFraction;
    [AutoNetworkedField] public float CoolantFraction;
    [AutoNetworkedField] public float BrakeFluidFraction;
    [AutoNetworkedField] public float TransmissionFraction = 1;
    [AutoNetworkedField] public float SteeringFluidFraction = 1;
    [AutoNetworkedField] public float WasherFraction;
    [AutoNetworkedField] public float OilPressure;
    [DataField] public float NominalOilPressure = 300;
    [DataField] public float UnassistedSteering = 0.25f;
    [DataField] public float DryTransmissionWear = 0.002f;
    [AutoNetworkedField] public float BrakeHeat;
    [AutoNetworkedField] public float EnginePower = 1;
    [AutoNetworkedField] public float BrakePower = 1;
    [AutoNetworkedField] public float EngineWear;
    [AutoNetworkedField] public float WearElapsed;
    [AutoNetworkedField] public float Elapsed;
    [DataField(required: true)] public float Interval;
    [DataField(required: true)] public float AmbientTemperature;
    [DataField(required: true)] public float EngineHeatCapacity;
    [DataField(required: true)] public float BrakeHeatCapacity;
    [DataField(required: true)] public float IdleHeat;
    [DataField(required: true)] public float LoadHeat;
    [DataField(required: true)] public float PassiveCooling;
    [DataField(required: true)] public float RadiatorCooling;
    [DataField(required: true)] public float AirCooling;
    [DataField(required: true)] public float BrakeCooling;
    [DataField(required: true)] public float OverheatTemperature;
    [DataField(required: true)] public float ShutdownTemperature;
    [DataField(required: true)] public float BrakeFadeTemperature;
    [DataField(required: true)] public float BrakeFailureTemperature;
    [DataField(required: true)] public float MinimumOil;
    [DataField(required: true)] public float NormalWear;
    [DataField(required: true)] public float DryWear;
    [DataField(required: true)] public float HotWear;
    [DataField(required: true)] public float WearInterval;
    [DataField(required: true)] public float BatteryCapacity;
    [DataField(required: true)] public float StarterCharge;
    [DataField(required: true)] public float IgnitionAmps;
    [DataField(required: true)] public float AlternatorAmps;
}
