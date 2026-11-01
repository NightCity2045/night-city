// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Runtime;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;
using Content.Server._NC.Meta.Devices;
using Content.Shared._NC.Meta.Domains;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared._NC.Vehicle.Prototypes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Meta.Domains;

/// <summary>Certified NC vehicle telemetry and an expiring native torque governor; never writes physical engine condition.</summary>
public sealed partial class NCMetaEcuSystem : EntitySystem, INCMetaDeviceAdapter
{
    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCMetaPersistenceSystem _persistence = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    private readonly Dictionary<EntityUid, NCMetaEcuComponent> _ecus = [];
    private TimeSpan _nextSample;
    public IEnumerable<string> Members => ["meta.vehicle.EngineController.snapshot", "meta.vehicle.EngineController.setMode", "meta.vehicle.VehicleDashboard.snapshot"];

    public override void Initialize()
    {
        base.Initialize(); _runtime.RegisterAdapter(this); _runtime.RuntimeStopped += OnRuntimeStopped;
        SubscribeLocalEvent<NCMetaEcuComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<NCMetaEcuComponent, ComponentShutdown>(OnShutdown);
    }
    public override void Shutdown() { _runtime.RuntimeStopped -= OnRuntimeStopped; base.Shutdown(); }
    private void OnStartup(Entity<NCMetaEcuComponent> ent, ref ComponentStartup args) => _ecus[ent.Owner] = ent.Comp;
    private void OnShutdown(Entity<NCMetaEcuComponent> ent, ref ComponentShutdown args) { Release(ent.Owner, ent.Comp); _ecus.Remove(ent.Owner); }
    private void OnRuntimeStopped(NCMetaRuntimeInstance instance)
    {
        foreach (var (uid, ecu) in _ecus) if (ecu.Controller == instance) Release(uid, ecu);
    }
    public bool CanBind(EntityUid device, string capability) => capability is "meta.vehicle.EngineController" or "meta.vehicle.VehicleDashboard" &&
        HasComp<NCMetaEcuComponent>(device) && HasComp<NCVehicleDriveComponent>(device) && HasComp<NCVehicleConditionComponent>(device) &&
        (capability != "meta.vehicle.VehicleDashboard" || HasComp<PhysicsComponent>(device));

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_persistence.TryCaptureTime(out _, out _) || _timing.CurTime < _nextSample) return;
        _nextSample = _timing.CurTime + TimeSpan.FromMilliseconds(200);
        foreach (var (uid, ecu) in _ecus)
        {
            if (!TryComp<NCVehicleDriveComponent>(uid, out var drive) || !TryComp<NCVehicleConditionComponent>(uid, out var state)) { Release(uid, ecu); ecu.SampleValid = false; continue; }
            var powered = _devices.IsPowered(uid);
            if (ecu.PowerAvailable && !powered) { Release(uid, ecu); _devices.TryInvalidate(uid); }
            ecu.PowerAvailable = powered;
            if (!powered) { ecu.SampleValid = false; continue; }
            if (ecu.Controller != null && drive.Driver != ecu.Driver) { ecu.ManualOverride = true; Release(uid, ecu); }
            if (ecu.Controller != null && (_timing.CurTime >= ecu.LeaseUntil || ecu.ManualOverride)) Release(uid, ecu);
            if (!Sample(ecu, drive, state)) continue;
            _runtime.TryPublishEvent(uid, "meta.vehicle.EngineController.telemetryChanged", process => State(process, ecu));
            ecu.DashboardValid = SampleDashboard(uid, ecu, drive, state);
            if (ecu.DashboardValid)
                _runtime.TryPublishEvent(uid, "meta.vehicle.VehicleDashboard.telemetryChanged", process => Dashboard(process, ecu));
        }
    }

    private bool Sample(NCMetaEcuComponent ecu, NCVehicleDriveComponent drive, NCVehicleConditionComponent condition)
    {
        // The stock NC thermal prototype starts at 293.15 K and overheats at 388.15 K (vehicles.yml).
        // Its oil-pressure instrument explicitly labels this same physical OilPressure value kPa (service-fluids.ftl).
        // EngineRpm is converted to angular velocity with rpm * tau / 60 in NCVehicleDriveSystem.
        ecu.SampleValid = ecu.SensorsOperational && float.IsFinite(condition.EngineTemperature) && condition.EngineTemperature >= 0 &&
            float.IsFinite(drive.EngineRpm) && drive.EngineRpm >= 0 && drive.EngineRpm < 9223372036854775808.0 &&
            float.IsFinite(condition.OilPressure) && condition.OilPressure >= 0 && float.IsFinite(condition.CoolantFraction) && condition.CoolantFraction is >= 0 and <= 1;
        if (!ecu.SampleValid) return false;
        ecu.TemperatureC = condition.EngineTemperature - 273.15; ecu.Rpm = checked((long) Math.Round(drive.EngineRpm, MidpointRounding.ToEven));
        ecu.OilPressureKPa = condition.OilPressure; ecu.CoolantFraction = condition.CoolantFraction;
        var faults = new List<string>(4);
        if (condition.EngineTemperature >= condition.OverheatTemperature) faults.Add("Overheated");
        if (condition.OilFraction < condition.MinimumOil) faults.Add("LowOil");
        if (condition.CoolantFraction < 0.25) faults.Add("LowCoolant");
        if (drive.Overturned) faults.Add("Overturned");
        ecu.Faults = faults.ToArray(); ecu.LastSample = _timing.CurTime; ecu.SampleTick = _timing.CurTick.Value;
        if (ecu.SampleSequence < long.MaxValue) ecu.SampleSequence++; return true;
    }

    public MetaValue Read(NCMetaDeviceInvocation invocation)
    {
        var process = invocation.Runtime.Process;
        if (!TryComp<NCMetaEcuComponent>(invocation.Device, out var ecu)) return Error(invocation, "Unsupported");
        if (!ecu.SampleValid || !ecu.SensorsOperational) return Error(invocation, "Unsupported");
        if (_timing.CurTime - ecu.LastSample > TimeSpan.FromSeconds(1)) return Error(invocation, "StaleState");
        if (invocation.Member.Owner == "meta.vehicle.VehicleDashboard")
            return ecu.DashboardValid ? process.CreateResult(ReturnType(invocation), Dashboard(process, ecu)) : Error(invocation, "Unsupported");
        return process.CreateResult(ReturnType(invocation), State(process, ecu));
    }

    private bool SampleDashboard(EntityUid uid, NCMetaEcuComponent ecu, NCVehicleDriveComponent drive, NCVehicleConditionComponent state)
    {
        if (!TryComp<PhysicsComponent>(uid, out var physics) || !_prototypes.TryIndex(drive.Handling, out var tuning) ||
            !float.IsFinite(drive.Fuel) || drive.Fuel < 0 || !float.IsFinite(state.BatteryCharge) || state.BatteryCharge < 0 ||
            !float.IsFinite(state.BatteryCapacity) || state.BatteryCapacity <= 0 || state.BatteryCharge > state.BatteryCapacity) return false;
        var speed = physics.LinearVelocity.Length();
        if (!float.IsFinite(speed)) return false;
        // NCVehicleFuelSystem converts physical fuel solution through its prototype's UnitsPerLitre into this drive field.
        ecu.SpeedMps = speed; ecu.FuelLitres = drive.Fuel; ecu.BatteryFraction = state.BatteryCharge / state.BatteryCapacity;
        ecu.Gear = drive.Gear; ecu.Automatic = tuning.Automatic; ecu.EngineRunning = drive.EngineRunning; return true;
    }
    private MetaValue Dashboard(MetaProcess process, NCMetaEcuComponent ecu) => process.CreateRecord(process.GetTypeId("meta.vehicle.DashboardSample"),
        [MetaValue.Float(ecu.SpeedMps), MetaValue.Float(ecu.FuelLitres), MetaValue.Float(ecu.BatteryFraction), MetaValue.Int(ecu.Gear),
            MetaValue.Bool(ecu.Automatic), MetaValue.Bool(ecu.EngineRunning), SampleStamp(process, ecu)]);

    private MetaValue SampleStamp(MetaProcess process, NCMetaEcuComponent ecu)
    {
        var stamp = _runtime.CreateStamp(process);
        return process.CreateRecord((int) stamp.TypeId, [process.GetField(stamp, 0), MetaValue.Int(ecu.SampleSequence), MetaValue.Int(ecu.SampleTick)]);
    }

    public MetaHostResult Commit(NCMetaDeviceInvocation invocation)
    {
        if (!CanSetMode(invocation, out var ecu, out var mode, out var error)) return MetaHostResult.Completed(Error(invocation, error));
        var power = mode == "Safe" ? ecu.SafePowerLimit : mode == "Economy" ? ecu.EconomyPowerLimit : 1f;
        var governor = EnsureComp<NCMetaVehicleGovernorComponent>(invocation.Device);
        if (governor.PowerLimit != power) { governor.PowerLimit = power; Dirty(invocation.Device, governor); }
        ecu.Controller = invocation.Runtime; ecu.Mode = mode; ecu.LeaseUntil = _timing.CurTime + TimeSpan.FromSeconds(1.5);
        ecu.NextCommand = _timing.CurTime + TimeSpan.FromMilliseconds(500);
        ecu.Driver = Comp<NCVehicleDriveComponent>(invocation.Device).Driver;
        return MetaHostResult.Completed(invocation.Runtime.Process.CreateResult(ReturnType(invocation), State(invocation.Runtime.Process, ecu)));
    }

    private bool CanSetMode(NCMetaDeviceInvocation invocation, out NCMetaEcuComponent ecu, out string mode, out string error)
    {
        mode = "Normal"; error = "Unsupported";
        if (!TryComp(invocation.Device, out ecu!) || !TryComp<NCVehicleDriveComponent>(invocation.Device, out var drive) || !TryComp<NCVehicleConditionComponent>(invocation.Device, out var state)) return false;
        if (ecu.Controller != null && drive.Driver != ecu.Driver) { error = "ControlLost"; return false; }
        if (ecu.ManualOverride) { error = "ControlLost"; return false; }
        if (!ecu.SampleValid) { error = "StaleState"; return false; }
        if (_timing.CurTime < ecu.NextCommand) { error = "RateLimited"; return false; }
        if (ecu.Controller != null && ecu.Controller != invocation.Runtime && ecu.LeaseUntil > _timing.CurTime) { error = "Conflict"; return false; }
        var process = invocation.Runtime.Process; var type = invocation.Runtime.Artifact.GetType((int) invocation.Request.Arguments[0].TypeId);
        mode = type.Variants.Single(v => v.Tag == process.GetVariantTag(invocation.Request.Arguments[0])).Name;
        if (mode == "Economy" && !ecu.SupportsEconomy) return false;
        if (!float.IsFinite(ecu.SafePowerLimit) || ecu.SafePowerLimit is < 0 or > 1 || !float.IsFinite(ecu.EconomyPowerLimit) || ecu.EconomyPowerLimit is < 0 or > 1) return false;
        if (mode != "Safe" && state.EnginePower < 0.999f) { error = "Interlock"; return false; }
        error = ""; return true;
    }

    /// <summary>Called by the existing physical driver control flow; ownership/grants never manufacture a driver.</summary>
    public bool TrySetManualControl(Entity<NCMetaEcuComponent?> ent, EntityUid driver, bool manual)
    {
        if (!Resolve(ent, ref ent.Comp) || !TryComp<NCVehicleDriveComponent>(ent.Owner, out var drive) || drive.Driver != driver) return false;
        ent.Comp.ManualOverride = manual; Release(ent.Owner, ent.Comp); return true;
    }
    private void Release(EntityUid uid, NCMetaEcuComponent ecu)
    {
        ecu.Controller = null; ecu.Mode = "Normal"; ecu.LeaseUntil = TimeSpan.Zero;
        if (TryComp<NCMetaVehicleGovernorComponent>(uid, out var governor) && governor.PowerLimit != 1) { governor.PowerLimit = 1; Dirty(uid, governor); }
    }
    private MetaValue State(MetaProcess process, NCMetaEcuComponent ecu)
    {
        var stamp = _runtime.CreateStamp(process);
        stamp = process.CreateRecord((int) stamp.TypeId, [process.GetField(stamp, 0), MetaValue.Int(ecu.SampleSequence), MetaValue.Int(ecu.SampleTick)]);
        var modeType = process.GetTypeId("meta.vehicle.EngineMode");
        var faults = process.CreateArray(process.GetTypeId("array<string>"), ecu.Faults.Select(process.CreateString).ToArray());
        return process.CreateRecord(process.GetTypeId("meta.vehicle.EngineState"), [MetaValue.Float(ecu.TemperatureC), MetaValue.Int(ecu.Rpm), MetaValue.Float(ecu.OilPressureKPa), MetaValue.Float(ecu.CoolantFraction), faults, process.CreateEnum(modeType, ecu.Mode switch { "Economy" => 1, "Safe" => 2, _ => 0 }), stamp]);
    }
    private static int ReturnType(NCMetaDeviceInvocation invocation) => invocation.Runtime.Artifact.GetImport(invocation.Request.ImportId).ReturnType!.Value;
    private static MetaValue Error(NCMetaDeviceInvocation invocation, string code) => invocation.Runtime.Process.CreateErrorResult(ReturnType(invocation), code);
}
