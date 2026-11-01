// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Numerics;
using Content.Meta.Runtime;
using Content.Server.Atmos.EntitySystems;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;
using Content.Shared.Audio;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Components;
using Content.Shared.Power;
using Content.Shared.Stealth;
using Content.Shared.Stealth.Components;
using Content.Shared.SurveillanceCamera.Components;

namespace Content.Server._NC.Meta.Devices;

[RegisterComponent]
public sealed partial class NCMetaTemperatureSensorComponent : Component;
[RegisterComponent]
public sealed partial class NCMetaCameraComponent : Component
{
    [DataField] public float Range = 6;
    public readonly Dictionary<EntityUid, Vector2> Positions = [];
}
[RegisterComponent]
public sealed partial class NCMetaAlarmComponent : Component
{
    public NCMetaRuntimeInstance? Controller;
}

/// <summary>Physical sampling only. Optical events contain no hidden entity identity, inventory or mind data.</summary>
public sealed partial class NCMetaSensorSystem : EntitySystem, INCMetaDeviceAdapter
{
    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCMetaPersistenceSystem _persistence = default!;
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private AtmosphereSystem _atmos = default!;
    [Dependency] private SharedAmbientSoundSystem _ambient = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedStealthSystem _stealth = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    private readonly Dictionary<EntityUid, long> _sensors = [];
    private readonly Queue<EntityUid> _ready = [];
    private readonly HashSet<Entity<MobStateComponent>> _nearby = [];
    private readonly Dictionary<EntityUid, NCMetaAlarmComponent> _alarms = [];
    public IEnumerable<string> Members => ["meta.security.Alarm.setActive"];
    public bool CanBind(EntityUid device, string capability) => capability switch
    {
        "meta.security.Alarm" => HasComp<NCMetaAlarmComponent>(device) && HasComp<AmbientSoundComponent>(device),
        "meta.security.Camera" => _sensors.ContainsKey(device) && HasComp<NCMetaCameraComponent>(device) && HasComp<SurveillanceCameraComponent>(device),
        "meta.sensors.TemperatureSensor" => _sensors.ContainsKey(device) && HasComp<NCMetaTemperatureSensorComponent>(device),
        _ => false,
    };
    public override void Initialize()
    {
        base.Initialize();
        _runtime.RegisterAdapter(this); _runtime.RuntimeStopped += OnStopped;
        SubscribeLocalEvent<NCMetaTemperatureSensorComponent, ComponentStartup>(OnTemperatureStartup);
        SubscribeLocalEvent<NCMetaCameraComponent, ComponentStartup>(OnCameraStartup);
        SubscribeLocalEvent<NCMetaTemperatureSensorComponent, ComponentShutdown>(OnTemperatureShutdown);
        SubscribeLocalEvent<NCMetaCameraComponent, ComponentShutdown>(OnCameraShutdown);
        SubscribeLocalEvent<NCMetaAlarmComponent, ComponentStartup>(OnAlarmStartup);
        SubscribeLocalEvent<NCMetaAlarmComponent, ComponentShutdown>(OnAlarmShutdown);
        SubscribeLocalEvent<NCMetaAlarmComponent, PowerChangedEvent>(OnAlarmPower);
    }
    public override void Shutdown() { _runtime.RuntimeStopped -= OnStopped; base.Shutdown(); }
    private void OnTemperatureStartup(Entity<NCMetaTemperatureSensorComponent> ent, ref ComponentStartup args) => RegisterSensor(ent.Owner);
    private void OnCameraStartup(Entity<NCMetaCameraComponent> ent, ref ComponentStartup args) => RegisterSensor(ent.Owner);
    private void OnTemperatureShutdown(Entity<NCMetaTemperatureSensorComponent> ent, ref ComponentShutdown args) => RemoveSensor(ent.Owner);
    private void OnCameraShutdown(Entity<NCMetaCameraComponent> ent, ref ComponentShutdown args) => RemoveSensor(ent.Owner);
    private void RegisterSensor(EntityUid uid)
    {
        if (_sensors.Count >= 128 || !_sensors.TryAdd(uid, 0)) return;
        _ready.Enqueue(uid);
    }
    private void RemoveSensor(EntityUid uid)
    {
        _sensors.Remove(uid);
        var count = _ready.Count;
        for (var index = 0; index < count; index++) { var other = _ready.Dequeue(); if (other != uid) _ready.Enqueue(other); }
    }
    private void OnAlarmStartup(Entity<NCMetaAlarmComponent> ent, ref ComponentStartup args)
    { _alarms[ent.Owner] = ent.Comp; _ambient.SetAmbience(ent.Owner, false); }
    private void OnAlarmShutdown(Entity<NCMetaAlarmComponent> ent, ref ComponentShutdown args)
    { _alarms.Remove(ent.Owner); _ambient.SetAmbience(ent.Owner, false); }
    private void OnAlarmPower(Entity<NCMetaAlarmComponent> ent, ref PowerChangedEvent args)
    { if (!args.Powered) { ent.Comp.Controller = null; _ambient.SetAmbience(ent.Owner, false); } }
    private void OnStopped(NCMetaRuntimeInstance runtime)
    {
        foreach (var (uid, alarm) in _alarms)
            if (alarm.Controller == runtime) { alarm.Controller = null; _ambient.SetAmbience(uid, false); }
    }
    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_persistence.TryCaptureTime(out _, out var time)) return;
        var now = time.Ticks / TimeSpan.TicksPerMillisecond;
        var count = Math.Min(4, _ready.Count);
        for (var index = 0; index < count; index++)
        {
            var uid = _ready.Dequeue(); _ready.Enqueue(uid);
            if (!_sensors.TryGetValue(uid, out var due) || due > now) continue;
            _sensors[uid] = now + 500;
            if (!_devices.IsPowered(uid))
            { if (TryComp<NCMetaCameraComponent>(uid, out var offline)) offline.Positions.Clear(); continue; }
            if (HasComp<NCMetaTemperatureSensorComponent>(uid)) SampleTemperature(uid);
            if (TryComp<NCMetaCameraComponent>(uid, out var camera)) SampleCamera(uid, camera);
        }
    }
    private void SampleTemperature(EntityUid uid)
    {
        var mixture = _atmos.GetContainingMixture((uid, (TransformComponent?) Transform(uid)));
        if (mixture == null || mixture.Volume <= 0 || mixture.TotalMoles <= 0 || !float.IsFinite(mixture.Temperature)) return;
        var celsius = mixture.Temperature - 273.15;
        _runtime.TryPublishEvent(uid, "meta.sensors.TemperatureSensor.temperatureChanged", process =>
            process.CreateRecord(process.GetTypeId("meta.sensors.TemperatureSample"), [MetaValue.Float(celsius), _runtime.CreateStamp(process)]));
    }
    private void SampleCamera(EntityUid uid, NCMetaCameraComponent camera)
    {
        if (!TryComp<SurveillanceCameraComponent>(uid, out var native) || !native.Active || !float.IsFinite(camera.Range))
        { camera.Positions.Clear(); return; }
        var range = Math.Clamp(camera.Range, 0, 8);
        _nearby.Clear();
        _lookup.GetEntitiesInRange(Transform(uid).Coordinates, range, _nearby);
        if (_nearby.Count > 128) { camera.Positions.Clear(); return; }
        var observed = new Dictionary<EntityUid, Vector2>();
        var confidence = 0f;
        foreach (var body in _nearby)
        {
            if (body.Owner == uid || observed.Count >= 64 || !_interaction.InRangeUnobstructed(uid, body.Owner, range: range)) continue;
            var visible = TryComp<StealthComponent>(body.Owner, out var stealth)
                ? Math.Clamp(_stealth.GetVisibility(body.Owner, stealth), 0, 1) : 1;
            if (visible < 0.5f) continue;
            var position = _transform.GetWorldPosition(body.Owner);
            observed.Add(body.Owner, position);
            if (camera.Positions.TryGetValue(body.Owner, out var previous) && Vector2.DistanceSquared(previous, position) > 0.0025f)
                confidence = Math.Max(confidence, visible);
        }
        camera.Positions.Clear();
        foreach (var pair in observed) camera.Positions.Add(pair.Key, pair.Value);
        if (confidence < 0.5f) return;
        _runtime.TryPublishEvent(uid, "meta.security.Camera.motionDetected", process => process.CreateRecord(
            process.GetTypeId("meta.security.Detection"), [MetaValue.Float(confidence), _runtime.CreateStamp(process)]));
    }
    public MetaValue Read(NCMetaDeviceInvocation invocation) => Error(invocation, "Unsupported");
    public MetaHostResult Commit(NCMetaDeviceInvocation invocation)
    {
        var process = invocation.Runtime.Process;
        if (!TryComp<NCMetaAlarmComponent>(invocation.Device, out var alarm) || !HasComp<AmbientSoundComponent>(invocation.Device))
            return MetaHostResult.Completed(Error(invocation, "Incompatible"));
        var active = invocation.Request.Arguments[0].AsBool();
        alarm.Controller = active ? invocation.Runtime : null;
        _ambient.SetAmbience(invocation.Device, active);
        return MetaHostResult.Completed(process.CreateResult(process.GetImport(invocation.Request.ImportId).ReturnType!.Value,
            process.CreateRecord(process.GetTypeId("meta.security.AlarmState"), [MetaValue.Bool(active), _runtime.CreateStamp(process)])));
    }
    private static MetaValue Error(NCMetaDeviceInvocation invocation, string code) => invocation.Runtime.Process.CreateErrorResult(
        invocation.Runtime.Process.GetImport(invocation.Request.ImportId).ReturnType!.Value, code);
}
