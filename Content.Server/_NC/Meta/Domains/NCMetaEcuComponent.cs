// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server._NC.Meta.Runtime;

namespace Content.Server._NC.Meta.Domains;

/// <summary>Physical ECU hardware attached to an NCVehicleDrive chassis. Runtime leases are never persisted.</summary>
[RegisterComponent]
public sealed partial class NCMetaEcuComponent : Component
{
    [DataField] public bool SensorsOperational = true;
    [DataField] public bool SupportsEconomy = true;
    [DataField] public float SafePowerLimit = 0.35f;
    [DataField] public float EconomyPowerLimit = 0.7f;
    [ViewVariables] public bool ManualOverride;
    [ViewVariables] public string Mode = "Normal";
    public NCMetaRuntimeInstance? Controller;
    public EntityUid? Driver;
    public TimeSpan LeaseUntil;
    public TimeSpan NextCommand;
    public TimeSpan LastSample;
    public long SampleTick;
    public long SampleSequence;
    public bool SampleValid;
    public double TemperatureC;
    public long Rpm;
    public double OilPressureKPa;
    public double CoolantFraction;
    public string[] Faults = [];
    public bool PowerAvailable = true;
    public bool DashboardValid;
    public double SpeedMps;
    public double FuelLitres;
    public double BatteryFraction;
    public int Gear;
    public bool Automatic;
    public bool EngineRunning;
}
