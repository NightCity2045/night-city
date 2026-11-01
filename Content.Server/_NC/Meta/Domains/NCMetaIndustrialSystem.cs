// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Runtime;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;
using Content.Shared.Conveyor;
using Content.Shared.DeviceLinking.Events;
using Content.Shared.Power.EntitySystems;
using Robust.Shared.Physics.Components;
using Robust.Shared.Timing;

namespace Content.Server._NC.Meta.Domains;

/// <summary>Load cells sample real local physics mass; conveyor commands pass the existing signal/reclaimer system.</summary>
public sealed partial class NCMetaIndustrialSystem : EntitySystem, INCMetaDeviceAdapter
{
    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCMetaPersistenceSystem _persistence = default!;
    [Dependency] private SharedPowerReceiverSystem _power = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private IGameTiming _timing = default!;
    private readonly Dictionary<EntityUid, NCMetaLoadSensorComponent> _sensors = [];
    private readonly Dictionary<EntityUid, NCMetaConveyorComponent> _belts = [];
    private readonly HashSet<Entity<PhysicsComponent>> _massQuery = [];
    private TimeSpan _nextSample;
    public IEnumerable<string> Members => ["meta.industrial.Conveyor.setEnabled"];

    public override void Initialize()
    {
        base.Initialize(); _runtime.RegisterAdapter(this); _runtime.RuntimeStopped += OnRuntimeStopped;
        SubscribeLocalEvent<NCMetaLoadSensorComponent, ComponentStartup>(OnSensorStartup);
        SubscribeLocalEvent<NCMetaLoadSensorComponent, ComponentShutdown>(OnSensorShutdown);
        SubscribeLocalEvent<NCMetaConveyorComponent, ComponentStartup>(OnBeltStartup);
        SubscribeLocalEvent<NCMetaConveyorComponent, ComponentShutdown>(OnBeltShutdown);
        SubscribeLocalEvent<NCMetaConveyorComponent, SignalReceivedEvent>(OnExternalSignal);
    }
    public override void Shutdown() { _runtime.RuntimeStopped -= OnRuntimeStopped; base.Shutdown(); }
    private void OnSensorStartup(Entity<NCMetaLoadSensorComponent> ent, ref ComponentStartup args) => _sensors[ent.Owner] = ent.Comp;
    private void OnSensorShutdown(Entity<NCMetaLoadSensorComponent> ent, ref ComponentShutdown args) => _sensors.Remove(ent.Owner);
    private void OnBeltStartup(Entity<NCMetaConveyorComponent> ent, ref ComponentStartup args)
    {
        _belts[ent.Owner] = ent.Comp;
        if (ent.Comp.LoadSensor == null && HasComp<NCMetaLoadSensorComponent>(ent.Owner)) ent.Comp.LoadSensor = ent.Owner;
    }
    private void OnBeltShutdown(Entity<NCMetaConveyorComponent> ent, ref ComponentShutdown args) { Release(ent.Owner, ent.Comp); _belts.Remove(ent.Owner); }
    private void OnRuntimeStopped(NCMetaRuntimeInstance instance)
    {
        foreach (var (uid, belt) in _belts) if (belt.Controller == instance) Release(uid, belt);
    }
    private void OnExternalSignal(Entity<NCMetaConveyorComponent> ent, ref SignalReceivedEvent args)
    {
        if (ent.Comp.ApplyingCommand || !TryComp<ConveyorComponent>(ent.Owner, out var conveyor) || args.Port != conveyor.ForwardPort && args.Port != conveyor.ReversePort && args.Port != conveyor.OffPort) return;
        ent.Comp.ManualOverride = true; ent.Comp.Controller = null;
        if (ent.Comp.ServiceRevision != ulong.MaxValue) ent.Comp.ServiceRevision++;
    }
    public bool CanBind(EntityUid device, string capability) => capability switch
    {
        "meta.industrial.LoadSensor" => HasComp<NCMetaLoadSensorComponent>(device),
        "meta.industrial.Conveyor" => HasComp<NCMetaConveyorComponent>(device) && HasComp<ConveyorComponent>(device),
        _ => false
    };

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_persistence.TryCaptureTime(out _, out _) || _timing.CurTime < _nextSample) return;
        _nextSample = _timing.CurTime + TimeSpan.FromMilliseconds(500);
        foreach (var (uid, sensor) in _sensors)
        {
            sensor.SampleValid = sensor.Operational && _power.IsPowered(uid) && float.IsFinite(sensor.Radius) && sensor.Radius is > 0 and <= 2;
            if (!sensor.SampleValid) continue;
            _massQuery.Clear(); _lookup.GetEntitiesInRange(Transform(uid).Coordinates, sensor.Radius, _massQuery, LookupFlags.Dynamic | LookupFlags.Sundries);
            if (_massQuery.Count > 128) { sensor.SampleValid = false; continue; }
            double kilograms = 0;
            foreach (var body in _massQuery)
            {
                if (body.Owner == uid) continue;
                if (!float.IsFinite(body.Comp.Mass) || body.Comp.Mass < 0) { sensor.SampleValid = false; break; }
                kilograms += body.Comp.Mass;
            }
            if (!sensor.SampleValid || !double.IsFinite(kilograms)) continue;
            sensor.Kilograms = kilograms; sensor.LastSample = _timing.CurTime;
            _runtime.TryPublishEvent(uid, "meta.industrial.LoadSensor.loadSampled", process => process.CreateRecord(process.GetTypeId("meta.industrial.LoadSample"), [MetaValue.Float(kilograms), _runtime.CreateStamp(process)]));
        }
        foreach (var (uid, belt) in _belts)
            if (belt.Controller != null && (_timing.CurTime >= belt.LeaseUntil || belt.Interlocked || !FreshLoad(belt))) Release(uid, belt);
    }

    private bool FreshLoad(NCMetaConveyorComponent belt) => belt.LoadSensor is { } uid && TryComp<NCMetaLoadSensorComponent>(uid, out var sensor) &&
        sensor.SampleValid && sensor.Operational && _power.IsPowered(uid) && _timing.CurTime - sensor.LastSample <= TimeSpan.FromSeconds(1.5);
    public MetaValue Read(NCMetaDeviceInvocation invocation) => Error(invocation, "Unsupported");
    public MetaHostResult Commit(NCMetaDeviceInvocation invocation)
    {
        if (!TryComp<NCMetaConveyorComponent>(invocation.Device, out var belt) || !TryComp<ConveyorComponent>(invocation.Device, out var conveyor)) return MetaHostResult.Completed(Error(invocation, "Unsupported"));
        var enabled = invocation.Request.Arguments[0].AsBool();
        if (belt.ManualOverride) return MetaHostResult.Completed(Error(invocation, "ControlLost"));
        if (enabled && (!conveyor.Powered || !_power.IsPowered(invocation.Device))) return MetaHostResult.Completed(Error(invocation, "PowerOff"));
        if (enabled && (belt.Interlocked || !FreshLoad(belt))) return MetaHostResult.Completed(Error(invocation, "Interlock"));
        if (_timing.CurTime < belt.NextCommand) return MetaHostResult.Completed(Error(invocation, "RateLimited"));
        if (belt.Controller != null && belt.Controller != invocation.Runtime && belt.LeaseUntil > _timing.CurTime) return MetaHostResult.Completed(Error(invocation, "Conflict"));
        if (!Apply(invocation.Device, belt, conveyor, enabled)) return MetaHostResult.Completed(Error(invocation, "Interlock"));
        belt.Controller = invocation.Runtime; belt.LeaseUntil = _timing.CurTime + TimeSpan.FromSeconds(1.5); belt.NextCommand = _timing.CurTime + TimeSpan.FromMilliseconds(500);
        var process = invocation.Runtime.Process;
        return MetaHostResult.Completed(process.CreateResult(ReturnType(invocation), process.CreateRecord(process.GetTypeId("meta.industrial.ConveyorState"), [MetaValue.Bool(enabled), _runtime.CreateStamp(process)])));
    }

    public bool TrySetManualControl(Entity<NCMetaConveyorComponent?> ent, EntityUid user, bool manual)
    {
        if (!Resolve(ent, ref ent.Comp) || !_devices.CanManage(ent.Owner, user)) return false;
        Release(ent.Owner, ent.Comp); ent.Comp.ManualOverride = manual;
        if (ent.Comp.ServiceRevision != ulong.MaxValue) ent.Comp.ServiceRevision++;
        return true;
    }
    public bool CanServiceFirmware(EntityUid uid, out ulong revision)
    {
        revision = 0;
        if (!TryComp<NCMetaConveyorComponent>(uid, out var belt) || !belt.ManualOverride || belt.Controller != null ||
            belt.ServiceRevision == ulong.MaxValue || !TryComp<ConveyorComponent>(uid, out var conveyor) || conveyor.State != ConveyorState.Off) return false;
        revision = belt.ServiceRevision; return true;
    }
    public bool TryBindLoadSensor(Entity<NCMetaConveyorComponent?> ent, EntityUid user, EntityUid sensor)
    {
        if (!Resolve(ent, ref ent.Comp) || !_devices.CanManage(ent.Owner, user) || !_devices.CanManage(sensor, user) || !HasComp<NCMetaLoadSensorComponent>(sensor)) return false;
        Release(ent.Owner, ent.Comp); ent.Comp.LoadSensor = sensor;
        if (ent.Comp.ServiceRevision != ulong.MaxValue) ent.Comp.ServiceRevision++;
        _devices.TryInvalidate(ent.Owner); return true;
    }
    private bool Apply(EntityUid uid, NCMetaConveyorComponent belt, ConveyorComponent conveyor, bool enabled)
    {
        belt.ApplyingCommand = true;
        try
        {
            var signal = new SignalReceivedEvent(enabled ? conveyor.ForwardPort.Id : conveyor.OffPort.Id);
            RaiseLocalEvent(uid, ref signal);
        }
        finally { belt.ApplyingCommand = false; }
        return conveyor.State == (enabled ? ConveyorState.Forward : ConveyorState.Off);
    }
    private void Release(EntityUid uid, NCMetaConveyorComponent belt)
    {
        belt.Controller = null; belt.LeaseUntil = TimeSpan.Zero;
        if (TryComp<ConveyorComponent>(uid, out var conveyor)) Apply(uid, belt, conveyor, false);
    }
    private static int ReturnType(NCMetaDeviceInvocation invocation) => invocation.Runtime.Artifact.GetImport(invocation.Request.ImportId).ReturnType!.Value;
    private static MetaValue Error(NCMetaDeviceInvocation invocation, string code) => invocation.Runtime.Process.CreateErrorResult(ReturnType(invocation), code);
}
