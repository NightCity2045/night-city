// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using Content.Meta.Runtime;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.Meta.Domains;
using Content.Shared._NC.Vehicle.Assembly;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared._NC.Vehicle.Prototypes;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Meta.Domains;

[RegisterComponent]
public sealed partial class NCMetaDriverAssistanceComponent : Component
{
    [DataField] public bool AllowDisable;
    public bool ManualOverride;
    public NCMetaRuntimeInstance? Controller;
    public uint DriverAuthority;
    public TimeSpan NextCommand;
}

/// <summary>Diagnostics and expiring requests to actual native ABS, stability and traction controllers.</summary>
public sealed partial class NCMetaDriverAssistanceSystem : EntitySystem, INCMetaDeviceAdapter
{
    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCMetaPersistenceSystem _persistence = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IGameTiming _timing = default!;
    private readonly Dictionary<EntityUid, NCMetaDriverAssistanceComponent> _controllers = [];
    private TimeSpan _nextSample;
    public IEnumerable<string> Members => ["meta.vehicle.DriverAssistance.snapshot", "meta.vehicle.DriverAssistance.setEnabled"];
    public override void Initialize()
    {
        base.Initialize(); _runtime.RegisterAdapter(this); _runtime.RuntimeStopped += OnStopped;
        SubscribeLocalEvent<NCMetaDriverAssistanceComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<NCMetaDriverAssistanceComponent, ComponentShutdown>(OnShutdown);
    }
    public override void Shutdown() { _runtime.RuntimeStopped -= OnStopped; base.Shutdown(); }
    private void OnStartup(Entity<NCMetaDriverAssistanceComponent> ent, ref ComponentStartup args) => _controllers[ent.Owner] = ent.Comp;
    private void OnShutdown(Entity<NCMetaDriverAssistanceComponent> ent, ref ComponentShutdown args) { Release(ent.Owner, ent.Comp); _controllers.Remove(ent.Owner); }
    private void OnStopped(NCMetaRuntimeInstance runtime)
    { foreach (var (uid, controller) in _controllers) if (controller.Controller == runtime) Release(uid, controller); }
    private bool TryHardware(EntityUid uid, out NCMetaDriverAssistanceComponent controller, out NCVehicleDriveComponent drive, out NCVehicleHandlingPrototype tuning)
    {
        tuning = default!; drive = default!;
        return TryComp(uid, out controller!) && TryComp(uid, out drive!) && drive.AssemblyReady &&
            NCVehiclePerformanceCompiler.Get(drive.AssemblyFunctions, NCVehicleFunction.Electronics) > 0 &&
            _prototypes.TryIndex(drive.Handling, out tuning!) && (tuning.AntiLockBrakes || tuning.StabilityControl || tuning.TractionControl) &&
            drive.WheelSpeeds.Length is > 0 and <= 16 && drive.WheelSpeeds.All(float.IsFinite) &&
            float.IsFinite(drive.LongitudinalSlip) && float.IsFinite(drive.LateralSlip);
    }
    public bool CanBind(EntityUid device, string capability) => capability == "meta.vehicle.DriverAssistance" && TryHardware(device, out _, out _, out _);
    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_persistence.TryCaptureTime(out _, out _) || _timing.CurTime < _nextSample) return;
        _nextSample = _timing.CurTime + TimeSpan.FromMilliseconds(200);
        foreach (var (uid, controller) in _controllers)
        {
            if (!TryHardware(uid, out _, out var drive, out var tuning) || !_devices.IsPowered(uid)) { Release(uid, controller); continue; }
            if (controller.Controller != null && (controller.ManualOverride || drive.Authority != controller.DriverAuthority ||
                !TryComp<NCMetaVehicleGovernorComponent>(uid, out var policy) || policy.AssistanceUntil <= _timing.CurTime)) Release(uid, controller);
            _runtime.TryPublishEvent(uid, "meta.vehicle.DriverAssistance.stateChanged", process => State(uid, process, drive, tuning));
        }
    }
    public MetaValue Read(NCMetaDeviceInvocation invocation) => TryHardware(invocation.Device, out _, out var drive, out var tuning)
        ? invocation.Runtime.Process.CreateResult(ReturnType(invocation), State(invocation.Device, invocation.Runtime.Process, drive, tuning)) : Error(invocation, "Unsupported");
    public MetaHostResult Commit(NCMetaDeviceInvocation invocation)
    {
        if (!TryHardware(invocation.Device, out var controller, out var drive, out var tuning)) return MetaHostResult.Completed(Error(invocation, "Unsupported"));
        if (controller.ManualOverride || drive.Driver is not { } driver || !_devices.CanManage(invocation.Device, driver, requireRange: false) ||
            invocation.Actor is { } actor && actor != driver) return MetaHostResult.Completed(Error(invocation, "ControlLost"));
        if (controller.Controller != null && controller.Controller != invocation.Runtime) return MetaHostResult.Completed(Error(invocation, "Conflict"));
        if (_timing.CurTime < controller.NextCommand) return MetaHostResult.Completed(Error(invocation, "RateLimited"));
        var channel = invocation.Runtime.Process.GetVariantTag(invocation.Request.Arguments[0]);
        var enabled = invocation.Request.Arguments[1].AsBool();
        if (!(channel switch { 0 => tuning.AntiLockBrakes, 1 => tuning.StabilityControl, 2 => tuning.TractionControl, _ => false }) ||
            !enabled && !controller.AllowDisable) return MetaHostResult.Completed(Error(invocation, "Unsupported"));
        var policy = EnsureComp<NCMetaVehicleGovernorComponent>(invocation.Device);
        if (policy.AssistanceUntil <= _timing.CurTime || policy.AssistanceAuthority != drive.Authority)
            policy.AbsDisabled = policy.StabilityDisabled = policy.TractionDisabled = false;
        switch (channel) { case 0: policy.AbsDisabled = !enabled; break; case 1: policy.StabilityDisabled = !enabled; break; case 2: policy.TractionDisabled = !enabled; break; }
        policy.AssistanceAuthority = drive.Authority; policy.AssistanceUntil = _timing.CurTime + TimeSpan.FromSeconds(1.5);
        Dirty(invocation.Device, policy); controller.Controller = invocation.Runtime; controller.DriverAuthority = drive.Authority;
        controller.NextCommand = _timing.CurTime + TimeSpan.FromMilliseconds(500);
        return MetaHostResult.Completed(invocation.Runtime.Process.CreateResult(ReturnType(invocation), State(invocation.Device, invocation.Runtime.Process, drive, tuning)));
    }
    public bool TrySetManualControl(Entity<NCMetaDriverAssistanceComponent?> ent, EntityUid user, bool manual)
    {
        if (!Resolve(ent, ref ent.Comp) || !TryComp<NCVehicleDriveComponent>(ent.Owner, out var drive) || drive.Driver != user ||
            !_devices.CanManage(ent.Owner, user, requireRange: false)) return false;
        Release(ent.Owner, ent.Comp); ent.Comp.ManualOverride = manual; return true;
    }
    private void Release(EntityUid uid, NCMetaDriverAssistanceComponent controller)
    {
        controller.Controller = null;
        if (!TryComp<NCMetaVehicleGovernorComponent>(uid, out var policy) || policy.AssistanceUntil == TimeSpan.Zero) return;
        policy.AssistanceUntil = TimeSpan.Zero; policy.AbsDisabled = policy.StabilityDisabled = policy.TractionDisabled = false; Dirty(uid, policy);
    }
    private MetaValue State(EntityUid uid, MetaProcess process, NCVehicleDriveComponent drive, NCVehicleHandlingPrototype tuning)
    {
        var active = TryComp<NCMetaVehicleGovernorComponent>(uid, out var policy) && drive.Driver != null && policy.AssistanceUntil > _timing.CurTime && policy.AssistanceAuthority == drive.Authority;
        return process.CreateRecord(process.GetTypeId("meta.vehicle.AssistanceState"),
            [MetaValue.Bool(tuning.AntiLockBrakes), MetaValue.Bool(tuning.AntiLockBrakes && (!active || !policy!.AbsDisabled)),
             MetaValue.Bool(tuning.StabilityControl), MetaValue.Bool(tuning.StabilityControl && (!active || !policy!.StabilityDisabled)),
             MetaValue.Bool(tuning.TractionControl), MetaValue.Bool(tuning.TractionControl && (!active || !policy!.TractionDisabled)),
             MetaValue.Float(drive.LongitudinalSlip), MetaValue.Float(drive.LateralSlip),
             process.CreateArray(process.GetTypeId("array<float>"), drive.WheelSpeeds.Select(speed => MetaValue.Float(speed)).ToArray()), _runtime.CreateStamp(process)]);
    }
    private static int ReturnType(NCMetaDeviceInvocation invocation) => invocation.Runtime.Artifact.GetImport(invocation.Request.ImportId).ReturnType!.Value;
    private static MetaValue Error(NCMetaDeviceInvocation invocation, string code) => invocation.Runtime.Process.CreateErrorResult(ReturnType(invocation), code);
}
