// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Content.Meta.Language;
using Content.Meta.Runtime;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;
using Content.Server.Database;
using Content.Server.Lathe;
using Content.Shared.ActionBlocker;
using Content.Shared.Interaction;
using Content.Shared.Lathe;
using Content.Shared.Research.Prototypes;
using Content.Shared.Verbs;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Meta.Domains;

[RegisterComponent]
public sealed partial class NCMetaMachineComponent : Component
{
    [DataField] public bool Fabricator;
    [DataField] public bool Grinder;
    [DataField] public bool ChemMaster;
    public bool AutomationEnabled;
    public EntityUid? Operator;
    public Guid ControlEpoch = Guid.NewGuid();
    public TimeSpan NextCommand;
}

/// <summary>Durable admission surrounds the real machine debit and output boundaries.</summary>
public sealed partial class NCMetaMachineSystem : EntitySystem, INCMetaDeviceAdapter
{
    private sealed record Outcome(MetaOperationStatus Status, int Completed, string Error = "");
    private sealed class Job(NCMetaDeviceInvocation invocation, Guid id, string kind, string fingerprint,
        Guid controlEpoch, int requested, string recipe, int program, int amount, string label, NCSessionEpoch epoch, NCActiveWorldTime time)
    {
        public readonly NCMetaDeviceInvocation Invocation = invocation;
        public readonly Guid Id = id;
        public readonly string Kind = kind;
        public readonly string Fingerprint = fingerprint;
        public readonly Guid ControlEpoch = controlEpoch;
        public readonly int Requested = requested;
        public readonly string Recipe = recipe;
        public readonly int Program = program;
        public readonly int Amount = amount;
        public readonly string Label = label;
        public readonly NCSessionEpoch Epoch = epoch;
        public readonly NCActiveWorldTime Time = time;
        public readonly TaskCompletionSource<Outcome> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Outcome? NativeOutcome;
        public bool Started;
        public bool CancelRequested;
        public TimeSpan Deadline;
    }
    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCMetaPersistenceSystem _persistence = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private LatheSystem _lathes = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    private readonly Dictionary<Guid, Job> _jobs = [];
    private readonly ConcurrentQueue<Action> _callbacks = new();
    private int _statusReads;
    public IEnumerable<string> Members => ["meta.industrial.Fabricator.recipes", "meta.industrial.Fabricator.start",
        "meta.industrial.Fabricator.operationStatus", "meta.industrial.Fabricator.cancel", "meta.lab.Grinder.start",
        "meta.lab.Grinder.operationStatus", "meta.lab.Grinder.cancel", "meta.lab.ChemMaster.bottle", "meta.lab.ChemMaster.pills", "meta.lab.ChemMaster.operationStatus"];

    public override void Initialize()
    {
        base.Initialize(); _runtime.RegisterAdapter(this);
        _runtime.RuntimeStopped += OnRuntimeStopped; _lathes.AutomationCompleted += OnLatheCompleted;
        SubscribeLocalEvent<NCMetaMachineComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<NCMetaMachineComponent, GetVerbsEvent<AlternativeVerb>>(OnVerbs);
        InitializeLaboratory();
    }
    public override void Shutdown()
    {
        _runtime.RuntimeStopped -= OnRuntimeStopped; _lathes.AutomationCompleted -= OnLatheCompleted;
        ShutdownLaboratory(); base.Shutdown();
    }
    private void OnVerbs(Entity<NCMetaMachineComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !_devices.CanManage(ent.Owner, args.User)) return;
        var user = args.User; var enabled = ent.Comp.AutomationEnabled;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString(enabled ? "nc-meta-domain-manual" : "nc-meta-domain-automation"),
            Act = () => TrySetAutomation(ent.Owner, user, !enabled) });
    }
    public bool TrySetAutomation(Entity<NCMetaMachineComponent?> ent, EntityUid user, bool enabled)
    {
        if (!Resolve(ent, ref ent.Comp) || !_devices.CanManage(ent.Owner, user) || !_blocker.CanConsciouslyPerformAction(user)) return false;
        foreach (var job in _jobs.Values.Where(job => job.Invocation.Device == ent.Owner).ToArray()) CancelNative(job, "ControlLost");
        ent.Comp.AutomationEnabled = enabled; ent.Comp.Operator = user; ent.Comp.ControlEpoch = Guid.NewGuid();
        _devices.TryInvalidate(ent.Owner); return true;
    }
    public bool CanServiceFirmware(EntityUid uid, out Guid controlEpoch)
    {
        controlEpoch = default;
        if (!TryComp<NCMetaMachineComponent>(uid, out var machine) || machine.AutomationEnabled ||
            _jobs.Values.Any(job => job.Invocation.Device == uid) ||
            TryComp<LatheComponent>(uid, out var lathe) && (lathe.CurrentRecipe != null || lathe.Queue.Count != 0) || IsLaboratoryBusy(uid)) return false;
        controlEpoch = machine.ControlEpoch; return true;
    }
    private void OnShutdown(Entity<NCMetaMachineComponent> ent, ref ComponentShutdown args)
    { foreach (var job in _jobs.Values.Where(job => job.Invocation.Device == ent.Owner).ToArray()) CancelNative(job, "DeviceDestroyed"); }
    private void OnRuntimeStopped(NCMetaRuntimeInstance runtime)
    { foreach (var job in _jobs.Values.Where(job => job.Invocation.Runtime == runtime).ToArray()) CancelNative(job, "PermissionRevoked"); }
    private void OnLatheCompleted(NCLatheAutomationOutcome result)
    {
        if (_jobs.TryGetValue(result.OperationId, out var job) && job.Invocation.Device == result.Device && job.Kind == "fabricator" && job.NativeOutcome == null)
        {
            var outcome = new Outcome(result.Applied ? MetaOperationStatus.Applied : result.Error == "OutcomeUnknown" ? MetaOperationStatus.OutcomeUnknown : MetaOperationStatus.Rejected,
                result.Completed, result.Error);
            job.NativeOutcome = outcome;
            job.Completion.TrySetResult(outcome);
        }
    }
    public bool CanBind(EntityUid device, string capability) => TryComp<NCMetaMachineComponent>(device, out var machine) && (capability switch
    {
        "meta.industrial.Fabricator" => machine.Fabricator && HasComp<LatheComponent>(device),
        "meta.lab.Grinder" => machine.Grinder && HasGrinder(device),
        "meta.lab.ChemMaster" => machine.ChemMaster && HasChemMaster(device),
        _ => false
    });
    public MetaValue Read(NCMetaDeviceInvocation invocation)
    {
        if (invocation.Member.Name != "recipes" || !CanBind(invocation.Device, invocation.Member.Owner) ||
            !_lathes.TryGetAvailableRecipes(invocation.Device, out var recipes)) return Error(invocation, "Unsupported");
        if (recipes.Count > 128) return Error(invocation, "QuotaExceeded");
        var process = invocation.Runtime.Process;
        return process.CreateResult(ReturnType(invocation), process.CreateArray(process.GetTypeId("array<string>"),
            recipes.OrderBy(recipe => recipe.Id, StringComparer.Ordinal).Select(recipe => process.CreateString(recipe.Id)).ToArray()));
    }
    public MetaHostResult Commit(NCMetaDeviceInvocation invocation)
    {
        var process = invocation.Runtime.Process;
        if (!CanBind(invocation.Device, invocation.Member.Owner) || !_persistence.TryCaptureTime(out var epoch, out var time) || _jobs.Count >= 64) return CompletedError(invocation, "Busy");
        var key = process.GetString(invocation.Request.Arguments[^1]);
        if (!MetaOperationReceipt.IsValidKey(key)) return CompletedError(invocation, "InvalidArgument");
        var kind = invocation.Member.Owner switch { "meta.industrial.Fabricator" => "fabricator", "meta.lab.Grinder" => "grinder", _ => "chemmaster" };
        var id = NCMetaPersistenceSystem.StableGuid(MetaOperationReceipt.HashFields("meta.machine", invocation.Runtime.Owner, invocation.DeviceId, kind, key));
        if (invocation.Member.Name is "operationStatus" or "cancel")
        {
            if (_statusReads >= 64) return CompletedError(invocation, "Busy");
            if (invocation.Member.Name == "cancel" && _jobs.TryGetValue(id, out var current)) CancelNative(current, "Cancelled");
            _statusReads++; _ = ReadStatusAsync(invocation, id, kind); return MetaHostResult.Awaiting(invocation.OperationId);
        }
        if (!TryComp<NCMetaMachineComponent>(invocation.Device, out var machine) || !machine.AutomationEnabled) return CompletedError(invocation, "ControlLost");
        if (_jobs.Values.Any(job => job.Invocation.Device == invocation.Device)) return CompletedError(invocation, "Busy");
        if (_timing.CurTime < machine.NextCommand) return CompletedError(invocation, "RateLimited");
        var requested = 1; var recipe = ""; var program = 0; var amount = 0; var label = "";
        if (kind == "fabricator")
        {
            recipe = process.GetString(invocation.Request.Arguments[0]); var count = invocation.Request.Arguments[1].AsInt();
            if (recipe.Length is 0 or > 128 || count is < 1 or > 16) return CompletedError(invocation, "InvalidArgument");
            requested = (int) count;
        }
        else if (!TryLaboratoryArguments(invocation, kind, out requested, out program, out amount, out label)) return CompletedError(invocation, "InvalidArgument");
        var fingerprint = MetaOperationReceipt.HashFields(kind, invocation.Member.Name, recipe,
            requested.ToString(), program.ToString(), amount.ToString(), label);
        var lease = _persistence.TryBeginNativeMutation(); if (lease == null) return CompletedError(invocation, "Busy");
        var job = new Job(invocation, id, kind, fingerprint, machine.ControlEpoch, requested, recipe, program, amount, label, epoch, time);
        _jobs.Add(id, job); machine.NextCommand = _timing.CurTime + TimeSpan.FromSeconds(1);
        _ = ExecuteAsync(job, lease); return MetaHostResult.Awaiting(invocation.OperationId);
    }
    private bool CanCommit(Job job) => !job.CancelRequested && _runtime.CanContinue(job.Invocation, requireWaiting: !job.Started) &&
        TryComp<NCMetaMachineComponent>(job.Invocation.Device, out var machine) && machine.AutomationEnabled && machine.ControlEpoch == job.ControlEpoch &&
        (!job.Started || _timing.CurTime < job.Deadline);
    private Outcome ApplyNative(Job job)
    {
        if (!CanCommit(job)) return new(MetaOperationStatus.Rejected, 0, "PermissionRevoked");
        job.Deadline = _timing.CurTime + TimeSpan.FromSeconds(305);
        if (job.Kind != "fabricator") return ApplyLaboratory(job);
        if (!_prototypes.TryIndex<LatheRecipePrototype>(job.Recipe, out var recipe) ||
            !_lathes.CanStartAutomationJob(job.Invocation.Device, recipe, job.Requested)) return new(MetaOperationStatus.Rejected, 0, "Interlock");
        if (!_lathes.TryStartAutomationJob(job.Invocation.Device, job.Id, recipe, job.Requested, () => CanCommit(job)))
            return job.NativeOutcome ?? new(MetaOperationStatus.Rejected, 0, "ActionRejected");
        job.Started = true;
        return job.NativeOutcome ?? new(MetaOperationStatus.Running, 0);
    }
    private void CancelNative(Job job, string error)
    {
        job.CancelRequested = true;
        if (!job.Started) return;
        if (job.Kind == "fabricator") _lathes.TryCancelAutomationJob(job.Invocation.Device, job.Id, error);
        else CancelLaboratory(job, error);
    }
    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        for (var work = 0; work < 32 && _callbacks.TryDequeue(out var callback); work++) callback();
        foreach (var job in _jobs.Values.ToArray())
            if (job.Started && !job.Completion.Task.IsCompleted && !CanCommit(job)) CancelNative(job, "PermissionRevoked");
    }
    private Task<T> OnMain<T>(Func<T> callback)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _callbacks.Enqueue(() => { try { completion.TrySetResult(callback()); } catch (Exception error) { completion.TrySetException(error); } });
        return completion.Task;
    }
    private static int ReturnType(NCMetaDeviceInvocation invocation) => invocation.Runtime.Artifact.GetImport(invocation.Request.ImportId).ReturnType!.Value;
    private static MetaValue Error(NCMetaDeviceInvocation invocation, string error) => invocation.Runtime.Process.CreateErrorResult(ReturnType(invocation), error);
    private static MetaHostResult CompletedError(NCMetaDeviceInvocation invocation, string error) => MetaHostResult.Completed(Error(invocation, error));
}
