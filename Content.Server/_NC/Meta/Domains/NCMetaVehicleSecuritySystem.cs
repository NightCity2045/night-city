// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Runtime;
using Content.Server._NC.Meta.Runtime;
using Content.Server._NC.Vehicle;
using Content.Shared.Lock;

namespace Content.Server._NC.Meta.Domains;

public sealed partial class NCMetaVehicleSecuritySystem : EntitySystem, INCMetaDeviceAdapter
{
    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCVehicleSecuritySystem _security = default!;
    public IEnumerable<string> Members => ["meta.vehicle.VehicleSecurity.snapshot", "meta.vehicle.VehicleSecurity.setArmed"];
    public override void Initialize()
    { base.Initialize(); _runtime.RegisterAdapter(this); _security.StateChanged += OnChanged; }
    public override void Shutdown() { _security.StateChanged -= OnChanged; base.Shutdown(); }
    public bool CanBind(EntityUid device, string capability) => capability == "meta.vehicle.VehicleSecurity" && _security.HasHardware(device);
    private void OnChanged(EntityUid uid) => _runtime.TryPublishEvent(uid, "meta.vehicle.VehicleSecurity.stateChanged", process => State(uid, process));
    private MetaValue State(EntityUid uid, MetaProcess process)
    {
        var security = Comp<NCVehicleSecurityComponent>(uid); var physicalLock = Comp<LockComponent>(uid);
        return process.CreateRecord(process.GetTypeId("meta.vehicle.SecurityState"),
            [MetaValue.Bool(security.Armed), MetaValue.Bool(physicalLock.Locked), MetaValue.Bool(security.AlarmActive),
             MetaValue.Bool(security.PendingArmed != null), _runtime.CreateStamp(process)]);
    }
    public MetaValue Read(NCMetaDeviceInvocation invocation) => _security.HasHardware(invocation.Device)
        ? invocation.Runtime.Process.CreateResult(ReturnType(invocation), State(invocation.Device, invocation.Runtime.Process)) : Error(invocation, "Unsupported");
    public MetaHostResult Commit(NCMetaDeviceInvocation invocation)
    {
        if (invocation.Actor is not { } actor) return MetaHostResult.Completed(Error(invocation, "AccessDenied"));
        return MetaHostResult.Completed(_security.TrySetArmed(invocation.Device, actor, invocation.Request.Arguments[0].AsBool())
            ? invocation.Runtime.Process.CreateResult(ReturnType(invocation), State(invocation.Device, invocation.Runtime.Process)) : Error(invocation, "Interlock"));
    }
    private static int ReturnType(NCMetaDeviceInvocation invocation) => invocation.Runtime.Artifact.GetImport(invocation.Request.ImportId).ReturnType!.Value;
    private static MetaValue Error(NCMetaDeviceInvocation invocation, string code) => invocation.Runtime.Process.CreateErrorResult(ReturnType(invocation), code);
}
