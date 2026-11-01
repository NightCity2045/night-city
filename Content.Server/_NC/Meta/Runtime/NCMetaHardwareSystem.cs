// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using Content.Meta.Runtime;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Domains;
using Content.Server._NC.Meta.Network;
using Content.Server._NC.Vehicle;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Power.Components;
using Content.Shared.Power.EntitySystems;
using Content.Shared.PowerCell;
using Content.Shared.PowerCell.Components;
using Content.Shared.Temperature.Components;
using Content.Shared.Verbs;
using Robust.Shared.Prototypes;
using System.Security.Cryptography;
using System.Text;

namespace Content.Server._NC.Meta.Runtime;

public enum NCMetaPowerState { Off, Booting, On, Brownout, ShuttingDown }
public enum NCMetaHealthState { Operational, Degraded, HardwareFault, FirmwareFault, Maintenance }
public sealed record NCMetaFirmwareEligibility(bool Allowed, string NativeState);
/// <summary>Transient physical service proof. Never serialized as persistent authority.</summary>
public sealed record NCMetaFirmwareServiceStamp(EntityUid Actor, string DeviceId, ulong DeviceEpoch,
    ulong AuthorityEpoch, string ProfileId, int ProfileRevision, string NativeStateHash);

/// <summary>Server observations only; this marker owns unique native event subscriptions.</summary>
[RegisterComponent]
public sealed partial class NCMetaHardwareStateComponent : Component
{
    public NCMetaPowerState Power;
    public NCMetaHealthState Health;
    public bool Maintenance;
    public bool FirmwareFault;
    public bool HardwareFault;
    public long BootAt;
    public long LastSample;
    public long PreviousGas;
    public float AppliedWatts;
    public float LoadFactor;
    public float? TemperatureKelvin;
    public string DeviceId = string.Empty;
    public string ProfileId = string.Empty;
    public int ProfileRevision;
    public bool LowPowerOff;
    public int TransmittedBytes;
    public bool Observing;
    public int ThrottlePermille = 1000;
}

/// <summary>Physical supervision remains independent of guest execution and never changes native safety controls.</summary>
public sealed partial class NCMetaHardwareSystem : EntitySystem
{
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCMetaFunctionalImplantSystem _functions = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedPowerReceiverSystem _receivers = default!;
    [Dependency] private PowerCellSystem _cells = default!;
    [Dependency] private SharedBatterySystem _batteries = default!;
    [Dependency] private DamageableSystem _damage = default!;
    [Dependency] private NCVehicleConditionSystem _vehicles = default!;
    [Dependency] private NCMetaOpticsSystem _optics = default!;
    [Dependency] private NCMetaNetworkSystem _network = default!;
    private readonly Dictionary<EntityUid, NCMetaHardwareStateComponent> _hosts = [];
    private readonly Dictionary<string, Func<EntityUid, EntityUid, NCMetaFirmwareEligibility?>> _firmwarePolicies = new(StringComparer.Ordinal);
    private long _now;
    private long _nextSample;
    private long _nextEnergySample;
    public event Action<EntityUid>? HostReady;

    public override void Initialize()
    {
        base.Initialize();
        _devices.Changed += OnDeviceChanged;
        _runtime.RuntimeBootHealthChanged += OnBootHealth;
        _network.TransmissionAdmitted += OnTransmissionAdmitted;
        SubscribeLocalEvent<NCMetaHardwareStateComponent, DamageChangedEvent>(OnDamageChanged);
        SubscribeLocalEvent<NCMetaHardwareStateComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<NCMetaHardwareStateComponent, GetVerbsEvent<AlternativeVerb>>(OnVerbs);
        SubscribeLocalEvent<NCMetaHardwareStateComponent, NCVehicleMaintenanceEvent>(OnVehicleMaintenance,
            after: [typeof(NCVehicleAccessorySystem), typeof(NCVehicleRecoverySystem)]);
        SubscribeLocalEvent<NCMetaHardwareStateComponent, NCVehicleDriverActionMessage>(OnVehicleAction,
            after: [typeof(NCVehicleControlSystem)]);
        SubscribeLocalEvent<NCMetaHardwareStateComponent, NCVehicleAssemblyChangedEvent>(OnVehicleAssemblyChanged);
        SubscribeLocalEvent<NCMetaHardwareStateComponent, NCVehicleElectricalPowerChangedEvent>(OnElectricalPowerChanged);
    }

    public override void Shutdown()
    {
        _devices.Changed -= OnDeviceChanged;
        _runtime.RuntimeBootHealthChanged -= OnBootHealth;
        _network.TransmissionAdmitted -= OnTransmissionAdmitted;
        foreach (var (host, state) in _hosts) ApplyLoad(host, state, 0, 0);
        _hosts.Clear(); base.Shutdown();
    }

    private void OnDeviceChanged(NCMetaDeviceEntry entry)
    {
        if (!HasComp<NCMetaHostComponent>(entry.Entity)) return;
        if (!_hosts.TryGetValue(entry.Entity, out var state))
        {
            if (_hosts.Count >= 4096 || !entry.Available) return;
            state = EnsureComp<NCMetaHardwareStateComponent>(entry.Entity);
            _hosts.Add(entry.Entity, state);
        }
        Observe(entry.Entity, state);
    }

    private void OnDamageChanged(Entity<NCMetaHardwareStateComponent> ent, ref DamageChangedEvent args) => Observe(ent.Owner, ent.Comp);
    private void OnVehicleMaintenance(Entity<NCMetaHardwareStateComponent> ent, ref NCVehicleMaintenanceEvent args) => Observe(ent.Owner, ent.Comp);
    private void OnVehicleAction(Entity<NCMetaHardwareStateComponent> ent, ref NCVehicleDriverActionMessage args) => Observe(ent.Owner, ent.Comp);
    private void OnVehicleAssemblyChanged(Entity<NCMetaHardwareStateComponent> ent, ref NCVehicleAssemblyChangedEvent args) => Observe(ent.Owner, ent.Comp);
    private void OnElectricalPowerChanged(Entity<NCMetaHardwareStateComponent> ent, ref NCVehicleElectricalPowerChangedEvent args) => Observe(ent.Owner, ent.Comp);
    private void OnVerbs(Entity<NCMetaHardwareStateComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !_devices.CanManage(ent.Owner, args.User)) return;
        var user = args.User;
        var maintenance = ent.Comp.Maintenance;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString(maintenance ? "nc-meta-hardware-maintenance-leave" : "nc-meta-hardware-maintenance-enter"),
            Act = () => TrySetMaintenance(ent.Owner, user, !maintenance) });
        if (maintenance && ent.Comp.FirmwareFault)
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-meta-hardware-clear-firmware-fault"),
                Act = () => TryClearFirmwareFault(ent.Owner, user) });
    }
    private void OnTransmissionAdmitted(EntityUid host, int bytes)
    {
        if (bytes > 0 && _hosts.TryGetValue(host, out var state))
            state.TransmittedBytes = (int) Math.Min(16L * 1024 * 1024, state.TransmittedBytes + (long) bytes);
    }
    private void OnShutdown(Entity<NCMetaHardwareStateComponent> ent, ref ComponentShutdown args)
    {
        ent.Comp.Power = NCMetaPowerState.ShuttingDown;
        _runtime.StopHost(ent.Owner, MetaStopReason.PowerLost);
        _runtime.ReleaseHardwareQuota(ent.Comp.DeviceId);
        ApplyLoad(ent.Owner, ent.Comp, 0, 0);
        _hosts.Remove(ent.Owner);
    }

    public bool CanRun(EntityUid host, NCMetaHardwareProfilePrototype profile) => IsValid(profile) &&
        _hosts.TryGetValue(host, out var state) && state.Power is NCMetaPowerState.On or NCMetaPowerState.Brownout &&
        state.Health is NCMetaHealthState.Operational or NCMetaHealthState.Degraded or NCMetaHealthState.FirmwareFault &&
        _devices.IsPowered(host);

    public bool CanUseDevice(EntityUid device) => !_hosts.TryGetValue(device, out var state)
        ? !HasComp<NCMetaHostComponent>(device)
        : state.Power is NCMetaPowerState.On or NCMetaPowerState.Brownout &&
          state.Health is not (NCMetaHealthState.HardwareFault or NCMetaHealthState.Maintenance);

    public bool CanAutoStart(EntityUid host) => TryComp<NCMetaHostComponent>(host, out var hardware) &&
        _prototypes.TryIndex(hardware.Profile, out var profile) && profile.AllowAutoStart && CanRun(host, profile);

    public bool IsMaintenance(EntityUid host) => _hosts.TryGetValue(host, out var state) && state.Maintenance;
    public bool HasHardwareFault(EntityUid host) => !_hosts.TryGetValue(host, out var state) || state.HardwareFault ||
        !TryProfile(host, out var profile) || !IsValid(profile) ||
        TryComp<DamageableComponent>(host, out var damage) && (float) _damage.GetTotalDamage((host, damage)) >= profile.HardwareFaultDamage ||
        TryComp<TemperatureComponent>(host, out var temperature) && (!float.IsFinite(temperature.Temperature) || temperature.Temperature >= profile.ShutdownTemperatureKelvin);
    public int GetThrottlePermille(EntityUid host) => _hosts.TryGetValue(host, out var state) ? state.ThrottlePermille : 0;

    public void RegisterFirmwarePolicy(string name, Func<EntityUid, EntityUid, NCMetaFirmwareEligibility?> policy)
    {
        if (name.Length is 0 or > 64 || _firmwarePolicies.Count >= 16 || !_firmwarePolicies.TryAdd(name, policy))
            throw new InvalidOperationException("Duplicate or excessive META native firmware policy.");
    }

    public bool CanStageFirmware(EntityUid host, EntityUid user) => TryCaptureFirmwareService(host, user, out _);

    public bool TryCaptureFirmwareService(EntityUid host, EntityUid user, out NCMetaFirmwareServiceStamp stamp)
    {
        stamp = default!;
        if (!_devices.CanManage(host, user) || !_devices.TryGetEntry(host, out var device) || HasHardwareFault(host) ||
            !TryComp<NCMetaHostComponent>(host, out var hardware) || hardware.Disabled || !HasPhysicalPowerSource(host) ||
            !TryProfile(host, out var profile) || !IsValid(profile) || !profile.AllowFirmwareUpdates ||
            profile.FirmwareRequiresMaintenance && !IsMaintenance(host)) return false;
        var native = new List<string>();
        foreach (var (name, policy) in _firmwarePolicies.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var eligibility = policy(host, user);
            if (eligibility == null) continue;
            if (!eligibility.Allowed || eligibility.NativeState.Length > 256) return false;
            native.Add(name + ":" + eligibility.NativeState);
        }
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', native))));
        stamp = new(user, device.Id, device.DeviceEpoch, device.AuthorityEpoch, profile.ID, profile.Revision, hash); return true;
    }

    public bool CanActivateFirmware(EntityUid host, EntityUid user, NCMetaFirmwareServiceStamp expected) =>
        TryCaptureFirmwareService(host, user, out var current) && current == expected;

    public bool TrySetMaintenance(EntityUid host, EntityUid user, bool maintenance)
    {
        if (!_devices.CanManage(host, user) || !_hosts.TryGetValue(host, out var state)) return false;
        state.Maintenance = maintenance;
        if (maintenance) _runtime.StopHost(host);
        else state.Power = NCMetaPowerState.Off;
        Observe(host, state); return true;
    }

    public bool TryClearFirmwareFault(EntityUid host, EntityUid user)
    {
        if (!_devices.CanManage(host, user) || !_hosts.TryGetValue(host, out var state) || !state.Maintenance) return false;
        state.FirmwareFault = false; Observe(host, state); return true;
    }

    private void OnBootHealth(NCMetaRuntimeInstance runtime, bool healthy, string reason)
    {
        if (runtime.Installation.SlotKind != "Firmware" || !_hosts.TryGetValue(runtime.Host, out var state)) return;
        state.FirmwareFault = !healthy; Observe(runtime.Host, state);
    }

    public void BeforeRuntime(long now)
    {
        if (now < _now)
        {
            foreach (var state in _hosts.Values) { state.Power = NCMetaPowerState.Off; state.LastSample = now; state.PreviousGas = 0; }
            _nextSample = now; _nextEnergySample = now;
        }
        _now = now;
        if (now < _nextSample) return;
        _nextSample = now + 250;
        foreach (var (host, state) in _hosts.ToArray()) Observe(host, state);
    }

    public void AfterRuntime(long now)
    {
        if (now < _nextEnergySample) return;
        _nextEnergySample = now + 500;
        foreach (var (host, state) in _hosts.ToArray())
        {
            if (now - state.LastSample < 500 || !TryProfile(host, out var profile)) continue;
            var seconds = Math.Max(0, now - state.LastSample) / 1000f;
            state.LastSample = now;
            long gas = 0;
            if (_devices.TryGetEntry(host, out var entry) && _runtime.TryGetHardwareUsage(entry.Id, out var usage))
            { gas = Math.Max(0, usage.TotalGas - state.PreviousGas); state.PreviousGas = usage.TotalGas; }
            var load = Math.Clamp(gas / Math.Max(1, profile.GasPerWindow * seconds), 0, 1);
            state.LoadFactor = (state.LoadFactor + load) / 2;
            var transmit = Math.Clamp(state.TransmittedBytes / Math.Max(1, profile.RadioBytesPerSecond * seconds), 0, 1);
            state.TransmittedBytes = 0;
            var watts = state.Power is NCMetaPowerState.On or NCMetaPowerState.Brownout or NCMetaPowerState.Booting
                ? profile.IdleWatts + profile.ActiveWatts * state.LoadFactor + profile.RadioWatts * transmit : 0;
            ApplyLoad(host, state, watts, seconds);
            Observe(host, state);
        }
    }

    private bool TryProfile(EntityUid host, out NCMetaHardwareProfilePrototype profile)
    {
        profile = default!;
        if (!TryComp<NCMetaHostComponent>(host, out var hardware) || !_prototypes.TryIndex(hardware.Profile, out var found)) return false;
        profile = found; return true;
    }

    private void Observe(EntityUid host, NCMetaHardwareStateComponent state)
    {
        if (state.Observing) return;
        state.Observing = true;
        try { ObserveCore(host, state); }
        finally { state.Observing = false; }
    }

    private void ObserveCore(EntityUid host, NCMetaHardwareStateComponent state)
    {
        if (!TryProfile(host, out var profile) || !IsValid(profile))
        { state.HardwareFault = true; state.Health = NCMetaHealthState.HardwareFault; state.Power = NCMetaPowerState.Off; _runtime.StopHost(host); return; }
        if (state.ProfileId != profile.ID || state.ProfileRevision != profile.Revision)
        {
            _runtime.StopHost(host);
            state.ProfileId = profile.ID; state.ProfileRevision = profile.Revision;
            state.Power = NCMetaPowerState.Off;
        }
        var damage = TryComp<DamageableComponent>(host, out var damaged) ? (float) _damage.GetTotalDamage((host, damaged)) : 0;
        state.TemperatureKelvin = TryComp<TemperatureComponent>(host, out var temperature) ? temperature.Temperature : null;
        var thermalFault = state.TemperatureKelvin is { } kelvin && (!float.IsFinite(kelvin) || kelvin >= profile.ShutdownTemperatureKelvin);
        state.HardwareFault = damage >= profile.HardwareFaultDamage || thermalFault;
        state.Health = state.Maintenance ? NCMetaHealthState.Maintenance : state.HardwareFault
            ? NCMetaHealthState.HardwareFault : state.FirmwareFault ? NCMetaHealthState.FirmwareFault
            : damage >= profile.DegradedDamage ? NCMetaHealthState.Degraded : NCMetaHealthState.Operational;
        if (!_devices.TryGetEntry(host, out var entry) || !entry.Ready || !HasPhysicalPowerSource(host) || !_devices.IsPowered(host) ||
            !TryComp<NCMetaHostComponent>(host, out var hardware) || hardware.Disabled ||
            state.Health is NCMetaHealthState.HardwareFault or NCMetaHealthState.Maintenance)
        {
            var wasActive = state.Power is NCMetaPowerState.On or NCMetaPowerState.Brownout or NCMetaPowerState.Booting;
            state.Power = NCMetaPowerState.ShuttingDown;
            _runtime.StopHost(host, _devices.IsPowered(host) ? MetaStopReason.Stop : MetaStopReason.PowerLost);
            if (wasActive) _devices.TryInvalidate(host);
            ApplyLoad(host, state, 0, 0);
            state.Power = NCMetaPowerState.Off; return;
        }
        state.DeviceId = entry.Id;
        if (state.LowPowerOff)
        {
            if (PowerFraction(host) is { } reserve && reserve <= profile.BrownoutFraction) return;
            state.LowPowerOff = false;
        }
        if (state.Power is NCMetaPowerState.Off or NCMetaPowerState.ShuttingDown)
        { state.Power = NCMetaPowerState.Booting; state.BootAt = _now + profile.BootMilliseconds; state.LastSample = _now; return; }
        if (state.Power == NCMetaPowerState.Booting)
        {
            if (_now < state.BootAt) return;
            state.Power = NCMetaPowerState.On;
            if (HostReady != null) foreach (Action<EntityUid> callback in HostReady.GetInvocationList())
                try { callback(host); } catch (Exception exception) { Log.Error($"META autostart callback failed: {exception}"); }
        }
        var fraction = PowerFraction(host);
        var brownout = fraction is { } level && level <= profile.BrownoutFraction;
        if (brownout && profile.BrownoutBehavior != "Throttle")
        {
            _runtime.StopHost(host, MetaStopReason.PowerLost);
            _devices.TryInvalidate(host);
            state.Power = profile.BrownoutBehavior == "Reset" ? NCMetaPowerState.Booting : NCMetaPowerState.Off;
            state.LowPowerOff = profile.BrownoutBehavior == "Off";
            state.BootAt = _now + profile.BootMilliseconds; return;
        }
        state.Power = brownout ? NCMetaPowerState.Brownout : NCMetaPowerState.On;
        var throttle = brownout || state.Health == NCMetaHealthState.Degraded ||
            state.TemperatureKelvin >= profile.ThrottleTemperatureKelvin ? profile.BrownoutThrottlePermille : 1000;
        state.ThrottlePermille = throttle;
        _runtime.TrySetHardwareThrottle(entry.Id, throttle);
    }

    private float? PowerFraction(EntityUid host)
    {
        if (_cells.TryGetBatteryFromSlot(host, out var battery)) return _batteries.GetChargeLevel(battery.Value.AsNullable());
        if (TryComp<NCVehicleConditionComponent>(host, out var vehicle) && vehicle.BatteryCapacity > 0)
            return Math.Clamp(vehicle.BatteryCharge / vehicle.BatteryCapacity, 0, 1);
        return null;
    }

    private bool HasPhysicalPowerSource(EntityUid host)
    {
        SharedApcPowerReceiverComponent? receiver = null;
        return _receivers.ResolveApc(host, ref receiver) || HasComp<PowerCellSlotComponent>(host) ||
            HasComp<NCVehicleConditionComponent>(host) || HasComp<NCMetaOpticsComponent>(host) || HasComp<NCMetaFunctionalImplantComponent>(host);
    }

    private void ApplyLoad(EntityUid host, NCMetaHardwareStateComponent state, float watts, float seconds)
    {
        if (!float.IsFinite(watts) || watts < 0 || !float.IsFinite(seconds) || seconds < 0) return;
        SharedApcPowerReceiverComponent? receiver = null;
        if (_receivers.ResolveApc(host, ref receiver))
            _receivers.SetLoad((host, receiver), Math.Max(0, receiver.Load - state.AppliedWatts + watts));
        else if (HasComp<PowerCellSlotComponent>(host) && seconds > 0)
        {
            if (!_cells.TryUseCharge(host, watts * seconds))
            { if (_cells.TryGetBatteryFromSlot(host, out var battery)) _batteries.UseCharge(battery.Value.AsNullable(), watts * seconds); }
        }
        else if (HasComp<NCVehicleConditionComponent>(host) && seconds > 0) _vehicles.TryUseElectricalEnergy(host, watts * seconds);
        else if (HasComp<NCMetaOpticsComponent>(host)) _optics.TrySetRuntimeWatts(host, watts);
        else if (HasComp<NCMetaFunctionalImplantComponent>(host)) _functions.TrySetRuntimeWatts(host, watts);
        state.AppliedWatts = watts;
    }

    public static bool IsValid(NCMetaHardwareProfilePrototype profile) => profile.Revision > 0 && profile.MemoryBytes >= 32768 &&
        profile.MaxProcesses is >= 1 and <= 256 && profile.MaxFrames is >= 1 and <= 64 && profile.CpuWeight is >= 1 and <= 16 &&
        profile.StorageBytes > 0 && profile.MaxFileBytes > 0 && profile.MaxFileBytes <= profile.StorageBytes &&
        profile.GasPerWindow is > 0 and <= 500000 && profile.MaxBurstGas is > 0 and <= 262144 && profile.BootMilliseconds is >= 0 and <= 60000 &&
        profile.BrownoutThrottlePermille is >= 1 and <= 1000 && profile.BrownoutBehavior is "Throttle" or "Reset" or "Off" &&
        float.IsFinite(profile.IdleWatts) && profile.IdleWatts >= 0 && float.IsFinite(profile.ActiveWatts) && profile.ActiveWatts >= 0 &&
        float.IsFinite(profile.RadioWatts) && profile.RadioWatts >= 0 && profile.RadioBytesPerSecond > 0 &&
        float.IsFinite(profile.BrownoutFraction) && profile.BrownoutFraction is >= 0 and <= 1 &&
        float.IsFinite(profile.DegradedDamage) && profile.DegradedDamage >= 0 && float.IsFinite(profile.HardwareFaultDamage) && profile.HardwareFaultDamage > profile.DegradedDamage &&
        float.IsFinite(profile.ThrottleTemperatureKelvin) && profile.ThrottleTemperatureKelvin > 0 &&
        float.IsFinite(profile.ShutdownTemperatureKelvin) && profile.ShutdownTemperatureKelvin > profile.ThrottleTemperatureKelvin;
}
