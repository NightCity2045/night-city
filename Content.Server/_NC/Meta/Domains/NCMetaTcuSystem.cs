// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Runtime;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.Meta.Domains;
using Content.Shared._NC.Vehicle;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared._NC.Vehicle.Prototypes;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Meta.Domains;

/// <summary>Expiring requests to the native automatic gearbox. Mechanical transmissions never acquire an actuator from software.</summary>
public sealed partial class NCMetaTcuSystem : EntitySystem, INCMetaDeviceAdapter
{
    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCMetaPersistenceSystem _persistence = default!;
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IGameTiming _timing = default!;
    private readonly Dictionary<EntityUid, NCMetaTcuComponent> _controllers = [];
    private TimeSpan _nextSample;
    public IEnumerable<string> Members => ["meta.vehicle.TransmissionController.snapshot", "meta.vehicle.TransmissionController.setStrategy", "meta.vehicle.TransmissionController.requestGear"];
    public override void Initialize()
    {
        base.Initialize(); _runtime.RegisterAdapter(this); _runtime.RuntimeStopped += OnStopped;
        SubscribeLocalEvent<NCMetaTcuComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<NCMetaTcuComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<NCMetaTcuComponent, NCVehicleDriverActionMessage>(OnDriverAction);
    }
    public override void Shutdown() { _runtime.RuntimeStopped -= OnStopped; base.Shutdown(); }
    private void OnStartup(Entity<NCMetaTcuComponent> ent, ref ComponentStartup args) => _controllers[ent.Owner] = ent.Comp;
    private void OnShutdown(Entity<NCMetaTcuComponent> ent, ref ComponentShutdown args) { Release(ent.Owner, ent.Comp); _controllers.Remove(ent.Owner); }
    private void OnStopped(NCMetaRuntimeInstance runtime) { foreach (var (uid, controller) in _controllers) if (controller.Controller == runtime) Release(uid, controller); }
    private void OnDriverAction(Entity<NCMetaTcuComponent> ent, ref NCVehicleDriverActionMessage args)
    {
        if (args.Action is not (NCVehicleDriverAction.GearUp or NCVehicleDriverAction.GearDown or NCVehicleDriverAction.Neutral or NCVehicleDriverAction.Reverse) ||
            !TryComp<NCVehicleDriveComponent>(ent.Owner, out var drive) || drive.Driver != args.Actor || drive.Authority != args.Authority) return;
        ent.Comp.ManualOverride = true; Release(ent.Owner, ent.Comp);
    }
    private bool HasActuator(EntityUid uid, out NCMetaTcuComponent controller, out NCVehicleDriveComponent drive, out NCVehicleHandlingPrototype tuning)
    {
        tuning = default!; drive = default!;
        return TryComp(uid, out controller!) && controller.ElectronicActuator && TryComp(uid, out drive!) &&
            _prototypes.TryIndex(drive.Handling, out tuning!) && tuning.Automatic;
    }
    public bool CanBind(EntityUid device, string capability) => capability == "meta.vehicle.TransmissionController" && HasActuator(device, out _, out _, out _);
    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_persistence.TryCaptureTime(out _, out _) || _timing.CurTime < _nextSample) return;
        _nextSample = _timing.CurTime + TimeSpan.FromMilliseconds(200);
        foreach (var (uid, controller) in _controllers)
        {
            if (!HasActuator(uid, out _, out var drive, out _) || !_devices.IsPowered(uid)) { Release(uid, controller); continue; }
            if (controller.Controller != null && (drive.Driver != controller.Driver || drive.Authority != controller.DriverAuthority ||
                controller.ManualOverride || _timing.CurTime >= controller.LeaseUntil)) Release(uid, controller);
            _runtime.TryPublishEvent(uid, "meta.vehicle.TransmissionController.stateChanged", process => State(process, controller, drive));
        }
    }
    public MetaValue Read(NCMetaDeviceInvocation invocation) => HasActuator(invocation.Device, out var controller, out var drive, out _)
        ? invocation.Runtime.Process.CreateResult(ReturnType(invocation), State(invocation.Runtime.Process, controller, drive)) : Error(invocation, "Unsupported");
    public MetaHostResult Commit(NCMetaDeviceInvocation invocation)
    {
        if (!HasActuator(invocation.Device, out var controller, out var drive, out var tuning)) return CompletedError(invocation, "Unsupported");
        if (controller.ManualOverride || drive.Driver is not { } driver || !_devices.CanManage(invocation.Device, driver, requireRange: false)) return CompletedError(invocation, "ControlLost");
        if (controller.Controller != null && controller.Controller != invocation.Runtime && _timing.CurTime < controller.LeaseUntil) return CompletedError(invocation, "Conflict");
        if (_timing.CurTime < controller.NextCommand) return CompletedError(invocation, "RateLimited");
        if (drive.Selector != NCVehicleSelector.Drive || !drive.AssemblyReady || drive.PendingShift != null || drive.Overturned ||
            !float.IsFinite(drive.EngineRpm) || drive.EngineRpm < 0) return CompletedError(invocation, "Interlock");
        var requested = 0; var strategy = controller.Strategy;
        if (invocation.Member.Name == "requestGear")
        {
            var gear = invocation.Request.Arguments[0].AsInt();
            if (gear < 1 || gear >= tuning.GearRatios.Length - 1) return CompletedError(invocation, "OutOfRange");
            requested = (int) gear;
            if (drive.Gear < 1 || drive.Gear >= tuning.GearRatios.Length - 1 ||
                drive.EngineRpm * tuning.GearRatios[requested] / tuning.GearRatios[drive.Gear] > tuning.RedlineRpm * 0.95f) return CompletedError(invocation, "Interlock");
        }
        else strategy = invocation.Runtime.Process.GetVariantTag(invocation.Request.Arguments[0]);
        if (strategy == 1 && !controller.SupportsEconomy || controller.LowMaximumGear < 1 || controller.LowMaximumGear >= tuning.GearRatios.Length - 1) return CompletedError(invocation, "Unsupported");
        if (requested > 0 && strategy == 2 && requested > controller.LowMaximumGear) return CompletedError(invocation, "Interlock");
        var governor = EnsureComp<NCMetaVehicleGovernorComponent>(invocation.Device);
        governor.TransmissionUntil = _timing.CurTime + TimeSpan.FromSeconds(1.5);
        governor.UpshiftMultiplier = strategy == 1 ? 0.8f : 1f;
        governor.MaximumForwardGear = strategy == 2 ? controller.LowMaximumGear : tuning.GearRatios.Length - 2;
        governor.RequestedForwardGear = requested;
        governor.TransmissionAuthority = drive.Authority; Dirty(invocation.Device, governor);
        controller.Strategy = strategy; controller.Controller = invocation.Runtime; controller.Driver = driver; controller.DriverAuthority = drive.Authority;
        controller.InputSequence = drive.LastInput; controller.LeaseUntil = governor.TransmissionUntil; controller.NextCommand = _timing.CurTime + TimeSpan.FromMilliseconds(500);
        controller.RequestedGear = requested;
        return MetaHostResult.Completed(invocation.Runtime.Process.CreateResult(ReturnType(invocation), State(invocation.Runtime.Process, controller, drive)));
    }
    public bool TrySetManualControl(Entity<NCMetaTcuComponent?> ent, EntityUid user, bool manual)
    {
        if (!Resolve(ent, ref ent.Comp) || !TryComp<NCVehicleDriveComponent>(ent.Owner, out var drive) || drive.Driver != user || !_devices.CanManage(ent.Owner, user, requireRange: false)) return false;
        Release(ent.Owner, ent.Comp); ent.Comp.ManualOverride = manual; return true;
    }
    private void Release(EntityUid uid, NCMetaTcuComponent controller)
    {
        controller.Controller = null; controller.Strategy = 0; controller.RequestedGear = 0; controller.LeaseUntil = TimeSpan.Zero;
        if (!TryComp<NCMetaVehicleGovernorComponent>(uid, out var governor) || governor.TransmissionUntil == TimeSpan.Zero) return;
        governor.TransmissionUntil = TimeSpan.Zero; governor.RequestedForwardGear = 0; governor.MaximumForwardGear = 0; governor.UpshiftMultiplier = 1; Dirty(uid, governor);
    }
    private MetaValue State(MetaProcess process, NCMetaTcuComponent controller, NCVehicleDriveComponent drive) => process.CreateRecord(process.GetTypeId("meta.vehicle.TransmissionState"),
        [MetaValue.Int(drive.Gear), MetaValue.Bool(true), process.CreateEnum(process.GetTypeId("meta.vehicle.TransmissionStrategy"), controller.Strategy), MetaValue.Bool(drive.PendingShift != null || _timing.CurTime < drive.NextAutomaticShift || controller.RequestedGear > 0 && controller.RequestedGear != drive.Gear), _runtime.CreateStamp(process)]);
    private static int ReturnType(NCMetaDeviceInvocation invocation) => invocation.Runtime.Artifact.GetImport(invocation.Request.ImportId).ReturnType!.Value;
    private static MetaValue Error(NCMetaDeviceInvocation invocation, string code) => invocation.Runtime.Process.CreateErrorResult(ReturnType(invocation), code);
    private static MetaHostResult CompletedError(NCMetaDeviceInvocation invocation, string code) => MetaHostResult.Completed(Error(invocation, code));
}
