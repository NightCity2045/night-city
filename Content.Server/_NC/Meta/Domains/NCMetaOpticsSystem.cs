// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Runtime;
using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Runtime;
using Content.Server._NC.LivingCity.Identity;
using Content.Server.GameTicking;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared.Body;
using Content.Shared.Interaction;
using Content.Shared.Light.Components;
using Content.Shared.Light.EntitySystems;
using Content.Shared.NightVision;
using Content.Shared.Overlays;
using Robust.Server.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Meta.Domains;

/// <summary>Native compatible optics only. No organ mutation, healing, hidden-entity observation or simulated offline time.</summary>
public sealed partial class NCMetaOpticsSystem : EntitySystem, INCMetaDeviceAdapter
{
    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCMetaPersistenceSystem _persistence = default!;
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCEntityPartyResolverSystem _parties = default!;
    [Dependency] private NCCyberwareSystem _cyberware = default!;
    [Dependency] private SharedNightVisionSystem _nightVision = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private MetaDataSystem _metadata = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    private readonly Dictionary<EntityUid, NCMetaOpticsComponent> _optics = [];
    private readonly Dictionary<EntityUid, (EntityUid Module, NightVisionComponent Vision)> _visionProviders = [];
    private readonly HashSet<Entity<PointLightComponent>> _lights = [];
    private TimeSpan _nextSample;
    public IEnumerable<string> Members => ["meta.implants.Optics.setFilter"];

    public override void Initialize()
    {
        base.Initialize(); _runtime.RegisterAdapter(this); _runtime.RuntimeStopped += OnRuntimeStopped;
        SubscribeLocalEvent<NCMetaOpticsComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<NCMetaOpticsComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<NCMedicalBodyComponent, NCCyberwareStateChangedEvent>(OnCyberwareChanged);
    }
    public override void Shutdown() { _runtime.RuntimeStopped -= OnRuntimeStopped; base.Shutdown(); }
    private void OnStartup(Entity<NCMetaOpticsComponent> ent, ref ComponentStartup args)
    {
        _optics[ent.Owner] = ent.Comp;
        if (CanOperate(ent.Owner, out var body, out _)) EnsureComp<NCMetaOpticsWearerComponent>(body);
    }
    private void OnShutdown(Entity<NCMetaOpticsComponent> ent, ref ComponentShutdown args) { ent.Comp.RuntimeLoad = 0; Clear(ent.Owner, ent.Comp); _optics.Remove(ent.Owner); }
    private void OnRuntimeStopped(NCMetaRuntimeInstance instance)
    {
        foreach (var (uid, optics) in _optics) if (optics.Controller == instance && !optics.ManualOverride) Clear(uid, optics);
    }
    private void OnCyberwareChanged(Entity<NCMedicalBodyComponent> ent, ref NCCyberwareStateChangedEvent args)
    {
        if (!TryComp<NCMetaOpticsComponent>(args.Module, out var optics)) return;
        if (args.Kind == NCCyberwareStateChangeKind.Installed) EnsureComp<NCMetaOpticsWearerComponent>(ent.Owner);
        if (optics.Clearing) return;
        if (args.Kind is not (NCCyberwareStateChangeKind.Emp or NCCyberwareStateChangeKind.Removed or NCCyberwareStateChangeKind.Installed or NCCyberwareStateChangeKind.NeuralInterface) && CanOperate(args.Module, out _, out _)) return;
        Clear(args.Module, optics); _devices.TryInvalidate(args.Module);
    }
    public bool CanBind(EntityUid device, string capability) => capability == "meta.implants.Optics" && HasComp<NCMetaOpticsComponent>(device) &&
        CanOperate(device, out var body, out _) && _parties.TryResolve(body, out var patient) && _devices.TryGetEntry(device, out var entry) && entry.Owner == patient.ToString();

    private bool CanOperate(EntityUid uid, out EntityUid body, out NCCyberwareComponent cyberware)
    {
        body = default;
        if (!TryComp(uid, out cyberware!) || !cyberware.Operational || !cyberware.PowerGranted || cyberware.ThermalShutdown || cyberware.EmpDisabledUntil > _timing.CurTime || cyberware.StableInstanceId == Guid.Empty) return false;
        if (TryComp<OrganComponent>(uid, out var organ) && organ.Body is { } direct) body = direct;
        else if (TryComp<ChildOrganComponent>(uid, out var child) && child.Parent is { } parent && TryComp<OrganComponent>(parent, out var host) && host.Body is { } parentBody) body = parentBody;
        else return false;
        return _cyberware.TryResolveSemanticPlacement(body, uid, out _, out _);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_persistence.TryCaptureTime(out _, out _) || _timing.CurTime < _nextSample) return;
        _nextSample = _timing.CurTime + TimeSpan.FromMilliseconds(500);
        foreach (var (uid, optics) in _optics)
        {
            if (!CanOperate(uid, out var body, out _))
            {
                if (optics.Body != null) { Clear(uid, optics); _devices.TryInvalidate(uid); }
                continue;
            }
            if (optics.LastInstalledBody != null && optics.LastInstalledBody != body) { Clear(uid, optics); optics.ManualOverride = true; _devices.TryInvalidate(uid); }
            optics.LastInstalledBody = body;
            optics.Body = body;
            EnsureComp<NCMetaOpticsWearerComponent>(body);
            if (optics.Controller != null && !optics.ManualOverride && _timing.CurTime >= optics.LeaseUntil) Clear(uid, optics);
            if (!TryMeasure(body, optics, out var normalized)) continue;
            _runtime.TryPublishEvent(uid, "meta.implants.Optics.lightSampled", process => process.CreateRecord(process.GetTypeId("meta.implants.LightSample"), [MetaValue.Float(normalized), _runtime.CreateStamp(process)]));
        }
    }

    private bool TryMeasure(EntityUid body, NCMetaOpticsComponent optics, out double normalized)
    {
        normalized = 0;
        if (!float.IsFinite(optics.SensorRadius) || optics.SensorRadius is <= 0 or > 8 || Transform(body).MapUid is not { } map || !TryComp<MapLightComponent>(map, out var ambient)) return false;
        var color = ambient.AmbientLightColor;
        if (TryComp<LightCycleComponent>(map, out var cycle)) color = SharedLightCycleSystem.GetColor((map, cycle), cycle.OriginalColor,
            (float) (_timing.CurTime + cycle.Offset - _ticker.RoundStartTimeSpan - _metadata.GetPauseTime(map)).TotalSeconds);
        var light = 0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B;
        _lights.Clear(); _lookup.GetEntitiesInRange(Transform(body).Coordinates, optics.SensorRadius, _lights, LookupFlags.All);
        if (_lights.Count > 32) return false;
        foreach (var candidate in _lights)
        {
            var lamp = candidate.Comp;
            if (!lamp.Enabled || lamp.ContainerOccluded || !float.IsFinite(lamp.Energy) || !float.IsFinite(lamp.Radius) || lamp.Radius <= 0 ||
                !Transform(body).Coordinates.TryDistance(EntityManager, Transform(candidate.Owner).Coordinates, out var distance) || distance > lamp.Radius ||
                !_interaction.InRangeUnobstructed(body, candidate.Owner, optics.SensorRadius)) continue;
            // A bounded photodiode exposure, based on actual native lamp energy/radius and occlusion; it is not a renderer/lux claim.
            light += Math.Max(0, lamp.Energy) * Math.Max(0, 1 - distance / lamp.Radius) * (0.2126 * lamp.Color.R + 0.7152 * lamp.Color.G + 0.0722 * lamp.Color.B);
        }
        if (!double.IsFinite(light)) return false;
        normalized = Math.Clamp(light, 0, 1); return true;
    }

    public MetaValue Read(NCMetaDeviceInvocation invocation) => Error(invocation, "Unsupported");
    public MetaHostResult Commit(NCMetaDeviceInvocation invocation)
    {
        if (!TryComp<NCMetaOpticsComponent>(invocation.Device, out var optics) || !CanOperate(invocation.Device, out var body, out _)) return MetaHostResult.Completed(Error(invocation, "Unsupported"));
        if (!_parties.TryResolve(body, out var patient) || patient.ToString() != invocation.Runtime.Owner) return MetaHostResult.Completed(Error(invocation, "AccessDenied"));
        if (optics.ManualOverride || optics.Body != null && optics.Body != body) return MetaHostResult.Completed(Error(invocation, "ControlLost"));
        if (optics.Controller != null && optics.Controller != invocation.Runtime && optics.LeaseUntil > _timing.CurTime) return MetaHostResult.Completed(Error(invocation, "Conflict"));
        if (_timing.CurTime < optics.NextCommand) return MetaHostResult.Completed(Error(invocation, "RateLimited"));
        var process = invocation.Runtime.Process; var lowLight = process.GetVariantTag(invocation.Request.Arguments[0]) == 1;
        if (lowLight && !optics.SupportsLowLight) return MetaHostResult.Completed(Error(invocation, "Unsupported"));
        if (!Apply(invocation.Device, optics, body, lowLight)) return MetaHostResult.Completed(Error(invocation, "Conflict"));
        optics.Controller = invocation.Runtime; optics.LeaseUntil = _timing.CurTime + TimeSpan.FromSeconds(1.5); optics.NextCommand = _timing.CurTime + TimeSpan.FromMilliseconds(500);
        return MetaHostResult.Completed(process.CreateResult(ReturnType(invocation), process.CreateRecord(process.GetTypeId("meta.implants.OpticState"),
            [process.CreateEnum(process.GetTypeId("meta.implants.OpticFilter"), lowLight ? 1 : 0), _runtime.CreateStamp(process)])));
    }

    private bool Apply(EntityUid module, NCMetaOpticsComponent optics, EntityUid body, bool lowLight)
    {
        if (!float.IsFinite(optics.LowLightLoad) || optics.LowLightLoad is < 0 or > 1) return false;
        if (lowLight)
        {
            var hasVision = TryComp<NightVisionComponent>(body, out var existing);
            if (_visionProviders.TryGetValue(body, out var provider) && (!hasVision || !ReferenceEquals(provider.Vision, existing)))
                _visionProviders.Remove(body);
            if (_visionProviders.TryGetValue(body, out provider) && provider.Module != module) return false;
            if (!_visionProviders.ContainsKey(body) && hasVision) return false;
            if (!_cyberware.TrySetLoad(module, Math.Clamp(optics.LowLightLoad + optics.RuntimeLoad, 0, 1))) return false;
            var vision = EnsureComp<NightVisionComponent>(body);
            _nightVision.SetEnabled((body, vision), true, body); _visionProviders[body] = (module, vision); optics.AddedVision = true;
        }
        else
        {
            if (_visionProviders.TryGetValue(body, out var provider) && provider.Module == module)
            {
                _visionProviders.Remove(body);
                if (TryComp<NightVisionComponent>(body, out var current) && ReferenceEquals(provider.Vision, current))
                {
                    _nightVision.SetEnabled((body, current), false, body);
                    if (optics.AddedVision && TryComp<NightVisionComponent>(body, out current) && ReferenceEquals(provider.Vision, current))
                        RemComp(body, current);
                }
                optics.AddedVision = false;
            }
            _cyberware.TrySetLoad(module, optics.RuntimeLoad);
        }
        optics.Body = body; optics.RequestedLowLight = lowLight; return true;
    }

    /// <summary>Independent compute load composes with the native low-light mode instead of clearing its power/heat cost.</summary>
    public bool TrySetRuntimeLoad(EntityUid module, float load)
    {
        if (!float.IsFinite(load) || load is < 0 or > 1 || !TryComp<NCMetaOpticsComponent>(module, out var optics)) return false;
        var total = Math.Clamp(load + (optics.RequestedLowLight ? optics.LowLightLoad : 0), 0, 1);
        if (!_cyberware.TrySetLoad(module, total)) return false;
        optics.RuntimeLoad = load; return true;
    }

    public bool TrySetRuntimeWatts(EntityUid module, float watts)
    {
        if (!float.IsFinite(watts) || watts < 0 || !TryComp<NCCyberwareComponent>(module, out var cyberware) ||
            !_prototypes.TryIndex(cyberware.Profile, out var profile) || !float.IsFinite(profile.LoadedPowerPerSecond) ||
            profile.LoadedPowerPerSecond <= 0) return watts == 0 && TrySetRuntimeLoad(module, 0);
        return TrySetRuntimeLoad(module, Math.Clamp(watts / profile.LoadedPowerPerSecond, 0, 1));
    }

    public bool TrySetManualFilter(Entity<NCMetaOpticsComponent?> ent, EntityUid user, bool lowLight)
    {
        if (!Resolve(ent, ref ent.Comp) || !CanOperate(ent.Owner, out var body, out _) || body != user || lowLight && !ent.Comp.SupportsLowLight) return false;
        if (!Apply(ent.Owner, ent.Comp, body, lowLight)) return false;
        ent.Comp.ManualOverride = true; ent.Comp.Controller = null; return true;
    }
    public bool TryEnableAutomation(Entity<NCMetaOpticsComponent?> ent, EntityUid user)
    {
        if (!Resolve(ent, ref ent.Comp) || !CanOperate(ent.Owner, out var body, out _) || body != user) return false;
        ent.Comp.ManualOverride = false; ent.Comp.Controller = null; return true;
    }
    public IEnumerable<EntityUid> GetInstalledModules(EntityUid body) => _optics.Keys.Where(module => CanOperate(module, out var installed, out _) && installed == body);
    private void Clear(EntityUid uid, NCMetaOpticsComponent optics)
    {
        if (optics.Clearing) return;
        optics.Clearing = true;
        try
        {
            if (optics.Body is { } body && Exists(body)) Apply(uid, optics, body, false);
            optics.Controller = null; optics.LeaseUntil = TimeSpan.Zero; optics.Body = null; optics.RequestedLowLight = false;
        }
        finally { optics.Clearing = false; }
    }
    private static int ReturnType(NCMetaDeviceInvocation invocation) => invocation.Runtime.Artifact.GetImport(invocation.Request.ImportId).ReturnType!.Value;
    private static MetaValue Error(NCMetaDeviceInvocation invocation, string code) => invocation.Runtime.Process.CreateErrorResult(ReturnType(invocation), code);
}
