// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Runtime;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Projects;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Neural;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.UI;
using Content.Shared.Verbs;

namespace Content.Server._NC.Medical.Cyberware.Neural;

public sealed partial class NCNeuralHardwareSystem : INCMetaDeviceAdapter
{
    [Dependency] private NCEntityPartyResolverSystem _parties = default!;
    [Dependency] private Content.Shared._NC.Medical.Anatomy.NCMedicalBodySystem _medical = default!;
    [Dependency] private NCMetaWorkspaceSystem _workspace = default!;
    [Dependency] private NCMetaHardwareSystem _hardware = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    public IEnumerable<string> Members => ["meta.implants.NeuralController.sample", "meta.implants.NeuralController.setEnabled"];

    private void InitializeMeta()
    {
        _runtime.RegisterAdapter(this);
        _runtime.RuntimeStopped += OnRuntimeStopped;
        _hardware.RegisterFirmwarePolicy("neural-controller", (host, user) =>
            !HasComp<NCNeuralControllerComponent>(host) ? null : new NCMetaFirmwareEligibility(
                TryGetServiceBench(host, out var bench) && _cyberware.CanOperateWorkbench(bench, user) && _cells.HasCharge(bench, 1),
                bench.ToString()));
        SubscribeLocalEvent<NCNeuralControllerComponent, GetVerbsEvent<AlternativeVerb>>(OnControllerVerbs);
        SubscribeLocalEvent<NCNeuralBenchComponent, GetVerbsEvent<AlternativeVerb>>(OnBenchControllerVerbs);
    }

    public override void Shutdown()
    {
        _runtime.RuntimeStopped -= OnRuntimeStopped;
        _devices.UnregisterClaimPolicy(ClaimPolicy);
        base.Shutdown();
    }

    private bool? ClaimPolicy(EntityUid board, EntityUid user)
    {
        if (!HasComp<NCNeuralControllerComponent>(board)) return null;
        if (TryResolveController(board, out _, out var body)) return body == user;
        return TryGetServiceBench(board, out var bench) && _cyberware.CanOperateWorkbench(bench, user);
    }

    public bool CanBind(EntityUid device, string capability) => capability == "meta.implants.NeuralController" &&
        HasComp<NCNeuralControllerComponent>(device) && _devices.TryGetEntry(device, out var entry) &&
        (TryGetServiceBench(device, out _) || TryResolveController(device, out _, out var body) &&
            _parties.TryResolve(body, out var patient) && patient.ToString() == entry.Owner);

    private bool CanControl(NCMetaDeviceInvocation invocation, out EntityUid module)
    {
        module = default;
        return TryResolveController(invocation.Device, out module, out var body) && IsReady(module) &&
            _parties.TryResolve(body, out var patient) && patient.ToString() == invocation.Runtime.Owner;
    }

    public MetaValue Read(NCMetaDeviceInvocation invocation)
    {
        var process = invocation.Runtime.Process;
        var type = invocation.Runtime.Artifact.GetImport(invocation.Request.ImportId).ReturnType!.Value;
        return CanControl(invocation, out var module) ? process.CreateResult(type, Sample(process, module)) : process.CreateErrorResult(type, "Unavailable");
    }

    public MetaHostResult Commit(NCMetaDeviceInvocation invocation)
    {
        var process = invocation.Runtime.Process;
        var type = invocation.Runtime.Artifact.GetImport(invocation.Request.ImportId).ReturnType!.Value;
        if (!CanControl(invocation, out var module)) return MetaHostResult.Completed(process.CreateErrorResult(type, "Unavailable"));
        var enabled = invocation.Request.Arguments[0].AsBool();
        var accepted = TrySetEnabled(module, enabled);
        return MetaHostResult.Completed(process.CreateResult(type, MetaValue.Bool(accepted)));
    }

    private MetaValue Sample(MetaProcess process, EntityUid module)
    {
        var neural = Comp<NCNeuralHardwareComponent>(module);
        var hardware = Comp<NCCyberwareComponent>(module);
        var profile = _prototypes.Index(hardware.Profile);
        var heat = profile.HeatShutdownThreshold > 0 ? hardware.Heat / profile.HeatShutdownThreshold : 0;
        long cooldown = 0;
        if (_abilities.TryGetFunctionState(module, out var state)) cooldown = state.CooldownMs;
        return process.CreateRecord(process.GetTypeId("meta.implants.NeuralSample"),
            [MetaValue.Int((int) neural.Kind), MetaValue.Bool(neural.Enabled), MetaValue.Float(heat),
                MetaValue.Int(cooldown), _runtime.CreateStamp(process)]);
    }

    public bool TrySetEnabled(EntityUid module, bool enabled)
    {
        if (!TryComp<NCNeuralHardwareComponent>(module, out var neural) ||
            !_cyberware.TryGetOwningBody(module, out var body) || enabled && !IsReady(module)) return false;
        if (neural.Kind == NCNeuralKind.Sandevistan)
            return _abilities.TrySetEnabled(body, module, enabled);
        if (neural.Kind is NCNeuralKind.Link or NCNeuralKind.ChipSocket) return enabled;
        neural.Enabled = enabled;
        _medical.TryMarkDirty(body, Content.Shared._NC.Medical.Anatomy.NCMedicalDirtyDomains.Persistence);
        return true;
    }

    private void OnRuntimeStopped(NCMetaRuntimeInstance instance)
    {
        if (TryResolveController(instance.Host, out var module, out _)) _cyberware.TrySetComputeLoad(module, 0);
    }

    private void OnControllerVerbs(Entity<NCNeuralControllerComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (args.CanAccess && args.CanInteract) AddControllerVerbs(ent, ent, args.User, args.Verbs);
    }

    private void OnBenchControllerVerbs(Entity<NCNeuralBenchComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess || Comp<NCCyberwareWorkbenchComponent>(ent).Workpiece?.ContainedEntity is not { } piece) return;
        var board = piece;
        if (!HasComp<NCNeuralControllerComponent>(board) && !TryGetController(piece, out board)) return;
        var user = args.User;
        if (_devices.CanClaim(board, user)) args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-neural-claim", ("module", Name(board))), Act = () => _devices.TryClaim(board, user) });
        AddControllerVerbs(piece, board, user, args.Verbs);
    }

    private void AddControllerVerbs(EntityUid module, EntityUid board, EntityUid user, SortedSet<AlternativeVerb> verbs)
    {
        if (!_devices.CanManage(board, user)) return;
        verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-neural-workspace"), Act = () => _ui.TryOpenUi(board, NCMetaUiKey.Workspace, user) });
        if (TryGetServiceBench(board, out _) && Comp<NCNeuralControllerComponent>(board).FactoryProgram)
            verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-neural-commission"), Act = () =>
            {
                if (_ui.TryOpenUi(board, NCMetaUiKey.Workspace, user)) _ = CommissionAsync(board, user);
            } });
    }
    private async Task CommissionAsync(EntityUid board, EntityUid user)
    {
        var success = await _workspace.TryCommissionNeuralAsync(board, user);
        _tasks.RunOnMainThread(() =>
        {
            if (Exists(board) && Exists(user)) _popup.PopupEntity(Loc.GetString(success ? "nc-neural-commissioned" : "nc-neural-commission-failed"), board, user);
        });
    }
    [Dependency] private Robust.Shared.Asynchronous.ITaskManager _tasks = default!;

}
