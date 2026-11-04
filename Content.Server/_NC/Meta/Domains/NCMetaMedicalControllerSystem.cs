// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Runtime;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware.Internal;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Content.Shared.Interaction;

namespace Content.Server._NC.Meta.Domains;

/// <summary>Programs detached physical boards. Installed assemblies must be serviced on the native bench.</summary>
public sealed partial class NCMetaMedicalControllerSystem : EntitySystem, INCMetaDeviceAdapter
{
    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    public IEnumerable<string> Members => ["meta.implants.MedicalProgrammer.flash", "meta.implants.MedicalProgrammer.selfTest"];
    public override void Initialize()
    {
        base.Initialize();
        _runtime.RegisterAdapter(this);
    }
    public bool CanBind(EntityUid device, string capability) => capability == "meta.implants.MedicalProgrammer" &&
        HasComp<NCMedicalControllerComponent>(device);

    public MetaValue Read(NCMetaDeviceInvocation invocation)
    {
        if (!CanBind(invocation.Device, invocation.Member.Owner)) return Error(invocation, "Incompatible");
        var passed = Comp<NCMedicalControllerComponent>(invocation.Device).Firmware != null &&
            TryComp<NCOrganIntegrityComponent>(invocation.Device, out var integrity) && integrity.CurrentIntegrity > 0;
        return invocation.Runtime.Process.CreateResult(ReturnType(invocation), MetaValue.Bool(passed));
    }

    public MetaHostResult Commit(NCMetaDeviceInvocation invocation)
    {
        if (!_runtime.CanContinue(invocation) ||
            !_devices.TryGetEntry(invocation.Device, out var entry) || entry.Owner != invocation.Runtime.Owner ||
            !_devices.IsPowered(invocation.Device) ||
            !_interaction.InRangeUnobstructed(invocation.Runtime.Host, invocation.Device, range: 2) ||
            invocation.Actor is { } user && !_devices.CanManage(invocation.Device, user) ||
            !CanBind(invocation.Device, invocation.Member.Owner) ||
            !TryComp<NCMedicalControllerComponent>(invocation.Device, out var board) ||
            !TryComp<NCOrganIntegrityComponent>(invocation.Device, out var integrity) || integrity.CurrentIntegrity <= 0 ||
            _containers.TryGetContainingContainer((invocation.Device, null, null), out var container) &&
            HasComp<NCCyberwareAssemblyComponent>(container.Owner))
            return MetaHostResult.Completed(Error(invocation, "ActionRejected"));
        var mode = invocation.Request.Arguments[0].AsInt();
        ProtoId<NCCyberwareFirmwarePrototype>? firmware = mode switch
        {
            0 => "NCMedicalFirmwareBasic",
            1 => "NCMedicalFirmwareBiomonitor",
            2 => "NCMedicalFirmwareInjector",
            _ => null,
        };
        if (firmware == null) return MetaHostResult.Completed(Error(invocation, "InvalidArgument"));
        board.Firmware = firmware;
        return MetaHostResult.Completed(invocation.Runtime.Process.CreateResult(ReturnType(invocation), MetaValue.Bool(true)));
    }
    private static int ReturnType(NCMetaDeviceInvocation invocation) => invocation.Runtime.Artifact.GetImport(invocation.Request.ImportId).ReturnType!.Value;
    private static MetaValue Error(NCMetaDeviceInvocation invocation, string error) => invocation.Runtime.Process.CreateErrorResult(ReturnType(invocation), error);
}
