// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using Content.Meta.Runtime;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Medical.Cyberware.Abilities;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Abilities;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.UI;
using Content.Shared.Verbs;
using Robust.Server.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Meta.Domains;

[RegisterComponent]
public sealed partial class NCMetaFunctionalImplantComponent : Component
{
    public bool ManualOverride = true;
    public Guid ControlEpoch = Guid.NewGuid();
    public Guid ModuleIdentity;
    public EntityUid? Body;
    public string WearerParty = "";
    public NCMetaRuntimeInstance? Controller;
    public NCMetaDeviceInvocation? Invocation;
    public TimeSpan LeaseUntil;
    public TimeSpan NextCommand;
    public bool Clearing;
}

[RegisterComponent]
public sealed partial class NCMetaFunctionalWearerComponent : Component;

/// <summary>Programs select supported native modes. Physical ability controllers retain all actuation and safety authority.</summary>
public sealed partial class NCMetaFunctionalImplantSystem : EntitySystem, INCMetaDeviceAdapter
{
    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCMetaPersistenceSystem _persistence = default!;
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCEntityPartyResolverSystem _parties = default!;
    [Dependency] private NCCyberwareSystem _cyberware = default!;
    [Dependency] private NCCyberwareAbilitySystem _abilities = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IGameTiming _timing = default!;
    private readonly Dictionary<EntityUid, NCMetaFunctionalImplantComponent> _modules = [];
    private TimeSpan _nextSample;
    public IEnumerable<string> Members => ["meta.implants.FunctionalController.snapshot", "meta.implants.FunctionalController.setEnabled", "meta.implants.FunctionalController.pulse"];

    public override void Initialize()
    {
        base.Initialize();
        _runtime.RegisterAdapter(this);
        _devices.RegisterClaimPolicy(ClaimPolicy);
        _runtime.RuntimeStopped += OnRuntimeStopped;
        _abilities.ManualControlRequested += OnManualControl;
        SubscribeLocalEvent<NCMetaFunctionalImplantComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<NCMetaFunctionalImplantComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<NCMetaFunctionalWearerComponent, NCCyberwareStateChangedEvent>(OnCyberwareChanged);
        SubscribeLocalEvent<NCMetaFunctionalWearerComponent, GetVerbsEvent<AlternativeVerb>>(OnWearerVerbs);
    }
    public override void Shutdown()
    {
        _runtime.RuntimeStopped -= OnRuntimeStopped;
        _devices.UnregisterClaimPolicy(ClaimPolicy);
        _abilities.ManualControlRequested -= OnManualControl;
        base.Shutdown();
    }
    private void OnStartup(Entity<NCMetaFunctionalImplantComponent> ent, ref ComponentStartup args)
    {
        if (_modules.Count >= 256) return;
        _modules.Add(ent.Owner, ent.Comp);
        Synchronize(ent.Owner, ent.Comp);
    }
    private void OnShutdown(Entity<NCMetaFunctionalImplantComponent> ent, ref ComponentShutdown args)
    {
        ClearGuest(ent.Owner, ent.Comp);
        _cyberware.TrySetComputeLoad(ent.Owner, 0);
        _modules.Remove(ent.Owner);
    }
    private void OnRuntimeStopped(NCMetaRuntimeInstance runtime)
    {
        foreach (var (uid, control) in _modules)
            if (control.Controller == runtime) ClearGuest(uid, control);
    }
    private void OnManualControl(EntityUid uid)
    {
        if (!_modules.TryGetValue(uid, out var control)) return;
        // Do not stop before the native toggle: that would turn an intended manual off into on.
        control.Controller = null; control.Invocation = null; control.LeaseUntil = TimeSpan.Zero;
        control.ManualOverride = true; control.ControlEpoch = Guid.NewGuid();
        _devices.TryInvalidate(uid);
    }
    private void OnCyberwareChanged(Entity<NCMetaFunctionalWearerComponent> ent, ref NCCyberwareStateChangedEvent args)
    {
        if (!_modules.TryGetValue(args.Module, out var control) || control.Clearing) return;
        if (args.Kind is not (NCCyberwareStateChangeKind.Removed or NCCyberwareStateChangeKind.Installed or NCCyberwareStateChangeKind.Emp or NCCyberwareStateChangeKind.NeuralInterface) &&
            _abilities.CanOperate(ent.Owner, args.Module)) return;
        ClearGuest(args.Module, control); _cyberware.TrySetComputeLoad(args.Module, 0);
        control.ManualOverride = true; control.ControlEpoch = Guid.NewGuid();
        _devices.TryInvalidate(args.Module);
        Synchronize(args.Module, control);
    }
    private bool TryResolve(EntityUid uid, out EntityUid body, out NCCyberwareComponent hardware)
    {
        body = default; hardware = default!;
        return _modules.ContainsKey(uid) && TryComp(uid, out hardware!) && hardware.StableInstanceId != Guid.Empty &&
            HasComp<NCCyberwareAbilityComponent>(uid) && _cyberware.TryGetOwningBody(uid, out body);
    }
    private bool? ClaimPolicy(EntityUid uid, EntityUid user) => !HasComp<NCMetaFunctionalImplantComponent>(uid) ? null : CanClaimInstalledModule(uid, user);
    /// <summary>Native wearer proof only. The registry additionally checks durable ownership, power, range and claim eligibility.</summary>
    public bool CanClaimInstalledModule(EntityUid uid, EntityUid user) => TryResolve(uid, out var body, out _) && body == user;
    private void Synchronize(EntityUid uid, NCMetaFunctionalImplantComponent control)
    {
        if (!TryResolve(uid, out var body, out var hardware))
        {
            if (control.Body != null) { ClearGuest(uid, control); control.Body = null; control.ManualOverride = true; control.ControlEpoch = Guid.NewGuid(); _devices.TryInvalidate(uid); }
            return;
        }
        EnsureComp<NCMetaFunctionalWearerComponent>(body);
        var wearer = _parties.TryResolve(body, out var party) ? party.ToString() : "";
        if (control.Body == body && control.ModuleIdentity == hardware.StableInstanceId && control.WearerParty == wearer) return;
        ClearGuest(uid, control); control.Body = body; control.ModuleIdentity = hardware.StableInstanceId;
        control.WearerParty = wearer;
        control.ManualOverride = true; control.ControlEpoch = Guid.NewGuid(); _devices.TryInvalidate(uid);
    }
    public bool CanBind(EntityUid device, string capability) => capability == "meta.implants.FunctionalController" &&
        TryResolve(device, out var body, out _) && _parties.TryResolve(body, out var owner) &&
        _devices.TryGetEntry(device, out var entry) && entry.Owner == owner.ToString();

    public MetaValue Read(NCMetaDeviceInvocation invocation)
    {
        var process = invocation.Runtime.Process;
        return CanBind(invocation.Device, invocation.Member.Owner) && _abilities.TryGetFunctionState(invocation.Device, out var state)
            ? process.CreateResult(ReturnType(invocation), StateValue(process, state)) : Error(invocation, "Incompatible");
    }
    public MetaHostResult Commit(NCMetaDeviceInvocation invocation)
    {
        if (!_modules.TryGetValue(invocation.Device, out var control) || !CanBind(invocation.Device, invocation.Member.Owner) ||
            !TryResolve(invocation.Device, out var body, out var hardware)) return CompletedError(invocation, "Incompatible");
        if (control.ManualOverride || control.Body != body || control.ModuleIdentity != hardware.StableInstanceId ||
            invocation.Actor is { } actor && actor != body) return CompletedError(invocation, "ControlLost");
        if (control.Controller != null && control.Controller != invocation.Runtime) return CompletedError(invocation, "Conflict");
        var pulse = invocation.Member.Name == "pulse";
        var enabled = !pulse && invocation.Request.Arguments[0].AsBool();
        if (!_abilities.TryGetFunctionState(invocation.Device, out var currentState)) return CompletedError(invocation, "StaleState");
        if (pulse != (currentState.Kind == NCCyberwareAbilityKind.Biomonitor)) return CompletedError(invocation, "Unsupported");
        if ((pulse || enabled) && _timing.CurTime < control.NextCommand) return CompletedError(invocation, "RateLimited");
        var epoch = control.ControlEpoch; var identity = hardware.StableInstanceId;
        bool Guard() => _runtime.CanContinue(invocation) && !control.ManualOverride && control.ControlEpoch == epoch &&
            TryResolve(invocation.Device, out var currentBody, out var current) && currentBody == body && current.StableInstanceId == identity &&
            CanBind(invocation.Device, invocation.Member.Owner);
        var accepted = pulse ? _abilities.TryPulse(body, invocation.Device, Guard) : _abilities.TrySetEnabled(body, invocation.Device, enabled, Guard);
        if (!accepted) return CompletedError(invocation, "ActionRejected");
        control.NextCommand = _timing.CurTime + TimeSpan.FromMilliseconds(pulse ? 1000 : 250);
        if (!pulse && enabled)
        {
            control.Controller = invocation.Runtime; control.Invocation = invocation;
            control.LeaseUntil = _timing.CurTime + TimeSpan.FromSeconds(3);
        }
        else if (!pulse) { control.Controller = null; control.Invocation = null; }
        return _abilities.TryGetFunctionState(invocation.Device, out var state)
            ? MetaHostResult.Completed(invocation.Runtime.Process.CreateResult(ReturnType(invocation), StateValue(invocation.Runtime.Process, state)))
            : CompletedError(invocation, "StaleState");
    }
    private void ClearGuest(EntityUid uid, NCMetaFunctionalImplantComponent control)
    {
        if (control.Clearing) return;
        var owned = control.Controller != null; var body = control.Body;
        control.Controller = null; control.Invocation = null; control.LeaseUntil = TimeSpan.Zero;
        if (!owned || body == null) return;
        control.Clearing = true;
        try { _abilities.TrySetEnabled(body.Value, uid, false); }
        finally { control.Clearing = false; }
    }
    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_persistence.TryCaptureTime(out _, out _) || _timing.CurTime < _nextSample) return;
        _nextSample = _timing.CurTime + TimeSpan.FromMilliseconds(250);
        foreach (var (uid, control) in _modules)
        {
            Synchronize(uid, control);
            if (control.Controller != null && (_timing.CurTime >= control.LeaseUntil || control.Invocation is not { } invocation ||
                !_runtime.CanContinue(invocation, requireWaiting: false))) ClearGuest(uid, control);
            if (_abilities.TryGetFunctionState(uid, out var state))
                _runtime.TryPublishEvent(uid, "meta.implants.FunctionalController.stateChanged", process => StateValue(process, state));
        }
    }
    public bool TrySetAutomation(Entity<NCMetaFunctionalImplantComponent?> ent, EntityUid user, bool enabled)
    {
        if (!Resolve(ent, ref ent.Comp) || !TryResolve(ent.Owner, out var body, out _) || body != user ||
            !_abilities.CanOperate(body, ent.Owner) || !_devices.CanManage(ent.Owner, user)) return false;
        ClearGuest(ent.Owner, ent.Comp); ent.Comp.ManualOverride = !enabled; ent.Comp.ControlEpoch = Guid.NewGuid();
        _devices.TryInvalidate(ent.Owner); return true;
    }
    public bool TrySetRuntimeWatts(EntityUid uid, float watts)
    {
        if (!float.IsFinite(watts) || watts < 0 || !TryComp<NCMetaFunctionalImplantComponent>(uid, out _)) return false;
        if (watts == 0) return _cyberware.TrySetComputeLoad(uid, 0);
        if (!TryComp<NCCyberwareComponent>(uid, out var hardware) || !_prototypes.TryIndex(hardware.Profile, out var profile) ||
            !float.IsFinite(profile.LoadedPowerPerSecond) || profile.LoadedPowerPerSecond <= 0) return false;
        return _cyberware.TrySetComputeLoad(uid, Math.Clamp(watts / profile.LoadedPowerPerSecond, 0, 1));
    }
    private void OnWearerVerbs(Entity<NCMetaFunctionalWearerComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (args.User != ent.Owner || !args.CanInteract) return;
        var user = args.User;
        foreach (var uid in _modules.Keys.Where(uid => TryResolve(uid, out var body, out _) && body == user).Take(8))
        {
            var name = Name(uid);
            if (_devices.CanClaim(uid, user)) args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-meta-function-claim", ("module", name)), Act = () => _devices.TryClaim(uid, user) });
            if (!_devices.CanManage(uid, user)) continue;
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-meta-function-workspace", ("module", name)), Act = () => TryOpenWorkspace(uid, user) });
            var enabled = _modules[uid].ManualOverride;
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString(enabled ? "nc-meta-function-automation" : "nc-meta-function-manual", ("module", name)), Act = () => TrySetAutomation(uid, user, enabled) });
        }
    }
    private bool TryOpenWorkspace(EntityUid uid, EntityUid user) => TryResolve(uid, out var body, out _) && body == user &&
        _devices.CanManage(uid, user) && _ui.TryOpenUi(uid, NCMetaUiKey.Workspace, user);
    private MetaValue StateValue(MetaProcess process, NCCyberwareFunctionState state) => process.CreateRecord(process.GetTypeId("meta.implants.FunctionState"),
        [process.CreateEnum(process.GetTypeId("meta.implants.FunctionKind"), (int) state.Kind), MetaValue.Bool(state.Active), MetaValue.Bool(state.Operational),
            MetaValue.Int(state.CooldownMs), MetaValue.Int(state.ActiveRemainingMs), _runtime.CreateStamp(process)]);
    private static int ReturnType(NCMetaDeviceInvocation invocation) => invocation.Runtime.Artifact.GetImport(invocation.Request.ImportId).ReturnType!.Value;
    private static MetaValue Error(NCMetaDeviceInvocation invocation, string error) => invocation.Runtime.Process.CreateErrorResult(ReturnType(invocation), error);
    private static MetaHostResult CompletedError(NCMetaDeviceInvocation invocation, string error) => MetaHostResult.Completed(Error(invocation, error));
}
