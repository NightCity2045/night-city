// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Runtime;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.Meta;

namespace Content.Server._NC.Meta.Applications;

/// <summary>
/// The two native CitiNet capabilities META programs use. The browser program opens an address in a window; a site
/// reads which device its visitor browses from. Everything else in a browser or a registrar is written in META.
/// </summary>
public sealed class NCMetaCitiNetAdapterSystem : EntitySystem, INCMetaDeviceAdapter
{
    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCMetaApplicationSystem _applications = default!;
    [Dependency] private NCMetaCitiNetSystem _citiNet = default!;

    public IEnumerable<string> Members => ["meta.citinet.Browser.open", "meta.citinet.Gateway.visitorDevice"];

    public bool CanBind(EntityUid device, string capability) => capability switch
    {
        "meta.citinet.Browser" => HasComp<NCMetaTerminalComponent>(device) && HasComp<NCMetaHostComponent>(device),
        "meta.citinet.Gateway" => HasComp<NCMetaHostComponent>(device),
        _ => false,
    };

    public override void Initialize()
    {
        base.Initialize();
        _runtime.RegisterAdapter(this);
    }

    public MetaValue Read(NCMetaDeviceInvocation invocation)
    {
        var process = invocation.Runtime.Process;
        var type = process.GetImport(invocation.Request.ImportId).ReturnType!.Value;
        if (invocation.Member.Name != "visitorDevice" ||
            !_applications.TryGetVisitorDevice(invocation.Runtime, invocation.Request.Arguments[0], out var device))
            return process.CreateErrorResult(type, "NotFound");
        return process.CreateResult(type, process.CreateString(device));
    }

    public MetaHostResult Commit(NCMetaDeviceInvocation invocation)
    {
        var process = invocation.Runtime.Process;
        var type = process.GetImport(invocation.Request.ImportId).ReturnType!.Value;
        // A button press carries its user; a page opened because an answer arrived goes to the one user who has the browser open.
        EntityUid actor;
        if (invocation.Member.Name != "open") return MetaHostResult.Completed(process.CreateErrorResult(type, "AccessDenied"));
        if (invocation.Actor is { } pressed) actor = pressed;
        else if (!_applications.TryGetSessionUser(invocation.Runtime, out actor))
            return MetaHostResult.Completed(process.CreateErrorResult(type, "AccessDenied"));
        var address = process.GetString(invocation.Request.Arguments[0]);
        _ = OpenAsync(invocation, actor, address, type);
        return MetaHostResult.Awaiting(invocation.OperationId);
    }

    private async Task OpenAsync(NCMetaDeviceInvocation invocation, EntityUid actor, string address, int type)
    {
        var opened = await _citiNet.TryOpenFromTerminalAsync(invocation.Device, actor, address);
        _runtime.TryComplete(invocation.Runtime, invocation.OperationId, process => opened
            ? process.CreateResult(type, MetaValue.Unit) : process.CreateErrorResult(type, "NotFound"));
    }
}
