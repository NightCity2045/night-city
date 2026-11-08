// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Runtime;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Projects;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Optics;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.UI;
using Content.Shared.Verbs;

namespace Content.Server._NC.Medical.Cyberware.Optics;

public sealed partial class NCCyberOpticsSystem : INCMetaDeviceAdapter
{
    [Dependency] private NCEntityPartyResolverSystem _parties = default!;
    [Dependency] private NCMetaWorkspaceSystem _workspace = default!;
    [Dependency] private NCMetaHardwareSystem _hardware = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    private readonly Dictionary<EntityUid, NCMetaRuntimeInstance> _controllers = [];
    public IEnumerable<string> Members => ["meta.implants.CyberEye.sample", "meta.implants.CyberEye.setMode", "meta.implants.CyberEye.heartbeat"];

    private void InitializeMeta()
    {
        _runtime.RegisterAdapter(this);
        _runtime.RuntimeStopped += OnRuntimeStopped;
        _hardware.RegisterFirmwarePolicy("optic-controller", (host, user) =>
            !HasComp<NCOpticControllerComponent>(host) ? null : new NCMetaFirmwareEligibility(
                TryGetServiceBench(host, out var bench) && _cyberware.CanOperateWorkbench(bench, user) && _cells.HasCharge(bench, 1),
                bench.ToString()));
        SubscribeLocalEvent<NCOpticControllerComponent, GetVerbsEvent<AlternativeVerb>>(OnControllerVerbs);
        SubscribeLocalEvent<NCCyberwareWorkbenchComponent, GetVerbsEvent<AlternativeVerb>>(OnBenchControllerVerbs);
    }

    public override void Shutdown()
    {
        _runtime.RuntimeStopped -= OnRuntimeStopped;
        base.Shutdown();
    }

    public bool TryGetServiceBench(EntityUid board, out EntityUid bench)
    {
        bench = default;
        if (!_containers.TryGetContainingContainer((board, null, null), out var container)) return false;
        var parent = container.Owner;
        if (HasComp<NCCyberEyeComponent>(parent))
        {
            if (_cyberware.TryGetOwningBody(parent, out _) || !_containers.TryGetContainingContainer((parent, null, null), out container)) return false;
            parent = container.Owner;
        }
        bench = parent;
        return HasComp<NCCyberwareWorkbenchComponent>(bench);
    }

    public bool CanBind(EntityUid device, string capability) => capability == "meta.implants.CyberEye" &&
        HasComp<NCOpticControllerComponent>(device) && _devices.TryGetEntry(device, out var entry) &&
        (TryGetServiceBench(device, out _) || TryResolveController(device, out _, out var body) &&
            _parties.TryResolve(body, out var patient) && patient.ToString() == entry.Owner);

    private bool CanControl(NCMetaDeviceInvocation invocation, out EntityUid eye)
    {
        eye = default;
        return TryResolveController(invocation.Device, out eye, out var body) && IsReady(eye, out _) &&
            _parties.TryResolve(body, out var patient) && patient.ToString() == invocation.Runtime.Owner;
    }

    public MetaValue Read(NCMetaDeviceInvocation invocation)
    {
        var process = invocation.Runtime.Process;
        var type = invocation.Runtime.Artifact.GetImport(invocation.Request.ImportId).ReturnType!.Value;
        return CanControl(invocation, out var eye) ? process.CreateResult(type, Sample(process, eye)) : process.CreateErrorResult(type, "Unavailable");
    }

    public MetaHostResult Commit(NCMetaDeviceInvocation invocation)
    {
        var process = invocation.Runtime.Process;
        var type = invocation.Runtime.Artifact.GetImport(invocation.Request.ImportId).ReturnType!.Value;
        if (!CanControl(invocation, out var eye)) return MetaHostResult.Completed(process.CreateErrorResult(type, "Unavailable"));
        var optics = Comp<NCCyberEyeComponent>(eye);
        if (_controllers.TryGetValue(invocation.Device, out var current) && current != invocation.Runtime && optics.CommandLease > _timing.CurTime)
            return MetaHostResult.Completed(process.CreateErrorResult(type, "Conflict"));
        var accepted = true;
        if (invocation.Request.Arguments.Count != 0)
        {
            var mode = invocation.Request.Arguments[0].AsInt();
            accepted = mode is >= 0 and <= 7 && TrySetMode(eye, (NCOpticMode) mode, invocation.Request.Arguments[1].AsBool());
        }
        if (accepted)
        {
            _controllers[invocation.Device] = invocation.Runtime;
            optics.CommandLease = _timing.CurTime + TimeSpan.FromSeconds(2);
        }
        return MetaHostResult.Completed(process.CreateResult(type, MetaValue.Bool(accepted)));
    }

    private MetaValue Sample(MetaProcess process, EntityUid eye)
    {
        double light = 0;
        if (TryResolveControllerForEye(eye, out var body) && TryComp<Content.Server._NC.Meta.Domains.NCMetaOpticsComponent>(eye, out var sensor))
            _lowLight.TryMeasure(body, sensor, out light);
        var modules = 0;
        foreach (var mode in Enum.GetValues<NCOpticMode>()) if (TryModule(eye, mode, out _)) modules |= 1 << (int) mode;
        return process.CreateRecord(process.GetTypeId("meta.implants.EyeSample"), [MetaValue.Float(light), MetaValue.Int(modules), _runtime.CreateStamp(process)]);
    }

    private bool TryResolveControllerForEye(EntityUid eye, out EntityUid body) => _cyberware.TryGetOwningBody(eye, out body);

    private bool PublishCommand(EntityUid controller, int mode, bool enabled, EntityUid user) =>
        _devices.CanManage(controller, user) && _runtime.TryPublishEvent(controller, "meta.implants.CyberEye.commanded", process =>
            process.CreateRecord(process.GetTypeId("meta.implants.EyeCommand"), [MetaValue.Int(mode), MetaValue.Bool(enabled), _runtime.CreateStamp(process)]), user) > 0;

    private void OnRuntimeStopped(NCMetaRuntimeInstance instance)
    {
        if (_controllers.TryGetValue(instance.Host, out var current) && current == instance)
        { ClearController(instance.Host); _controllers.Remove(instance.Host); }
    }

    private void OnControllerVerbs(Entity<NCOpticControllerComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (args.CanAccess && args.CanInteract) AddControllerVerbs(ent, ent, args.User, args.Verbs);
    }

    private void OnBenchControllerVerbs(Entity<NCCyberwareWorkbenchComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess || ent.Comp.Workpiece?.ContainedEntity is not { } piece) return;
        var board = piece;
        if (!HasComp<NCOpticControllerComponent>(board) && !TryGetController(piece, out board)) return;
        var user = args.User;
        if (_devices.CanClaim(board, user)) args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-optics-claim", ("eye", Name(board))), Act = () => _devices.TryClaim(board, user) });
        AddControllerVerbs(piece, board, user, args.Verbs);
    }

    private void AddControllerVerbs(EntityUid eye, EntityUid board, EntityUid user, SortedSet<AlternativeVerb> verbs)
    {
        if (!_devices.CanManage(board, user)) return;
        verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-optics-workspace"), Act = () => _ui.TryOpenUi(board, NCMetaUiKey.Workspace, user) });
        if (TryGetServiceBench(board, out _) && Comp<NCOpticControllerComponent>(board).FactoryProgram)
            verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-optics-commission"), Act = () =>
            {
                if (_ui.TryOpenUi(board, NCMetaUiKey.Workspace, user)) _ = CommissionAsync(board, user);
            } });
    }
    private async Task CommissionAsync(EntityUid board, EntityUid user)
    {
        var success = await _workspace.TryCommissionOpticAsync(board, user);
        _tasks.RunOnMainThread(() =>
        {
            if (Exists(board) && Exists(user)) _popup.PopupEntity(Loc.GetString(success ? "nc-optics-commissioned" : "nc-optics-commission-failed"), board, user);
        });
    }
    [Dependency] private Robust.Shared.Asynchronous.ITaskManager _tasks = default!;

}
