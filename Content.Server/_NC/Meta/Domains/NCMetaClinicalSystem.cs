// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Meta.Language;
using Content.Meta.Runtime;
using Content.Server._NC.Medical.Diagnostics.Detail;
using Content.Server._NC.Medical.Surgery;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;
using Content.Server.Database;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Diagnostics.Detail;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Events;
using Content.Shared.Chemistry;
using Content.Shared.FixedPoint;
using Content.Shared.Interaction;
using Content.Shared.ActionBlocker;
using Robust.Shared.Timing;

namespace Content.Server._NC.Meta.Domains;

/// <summary>Native filtered observation, physical reservoir dosing and existing timed surgical interactions.</summary>
public sealed partial class NCMetaClinicalSystem : EntitySystem, INCMetaDeviceAdapter
{
    private sealed record PatientReference(EntityUid Device, EntityUid Patient, Guid Epoch);
    private sealed class Operation(NCMetaDeviceInvocation invocation, NCMetaClinicalComponent context,
        EntityUid patient, EntityUid user, string key, Guid id, string fingerprint, NCSessionEpoch epoch, NCActiveWorldTime time)
    {
        public readonly NCMetaDeviceInvocation Invocation = invocation;
        public readonly Guid PatientEpoch = context.PatientEpoch;
        public readonly EntityUid Patient = patient;
        public readonly EntityUid User = user;
        public readonly string Key = key;
        public readonly Guid Id = id;
        public readonly string Fingerprint = fingerprint;
        public readonly float Dose = context.ConfiguredDose;
        public readonly float MaximumDose = context.MaximumDose;
        public readonly string Reservoir = context.ReservoirSolution;
        public readonly NCSessionEpoch Epoch = epoch;
        public readonly NCActiveWorldTime Time = time;
        public readonly string Kind = invocation.Member.Owner.EndsWith("DosingController", StringComparison.Ordinal) ? "dose" : "procedure";
        public readonly TaskCompletionSource<(MetaOperationStatus Status, string Error)> NativeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MetaOperationReceipt? Receipt;
        public TimeSpan Deadline;
        public bool Started;
    }

    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCMetaPersistenceSystem _persistence = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCDiagnosticsSystem _diagnostics = default!;
    [Dependency] private NCSurgeryInteractionSystem _surgery = default!;
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private ReactiveSystem _reactive = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private IGameTiming _timing = default!;
    private readonly Dictionary<EntityUid, NCMetaClinicalComponent> _contexts = [];
    private readonly Dictionary<(Guid Runtime, ulong Token), PatientReference> _references = [];
    private readonly Dictionary<Guid, Operation> _operations = [];
    private readonly ConcurrentQueue<Action> _admissions = new();
    private ulong _nextReference;
    public IEnumerable<string> Members => ["meta.medical.MedicalMonitor.patient", "meta.medical.MedicalMonitor.snapshot",
        "meta.medical.DosingController.dose", "meta.medical.DosingController.operationStatus",
        "meta.medical.ProcedureController.startSelectedStep", "meta.medical.ProcedureController.operationStatus"];

    public override void Initialize()
    {
        base.Initialize(); _runtime.RegisterAdapter(this); _runtime.RuntimeStopped += OnRuntimeStopped;
        SubscribeLocalEvent<NCMetaClinicalComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<NCMetaClinicalComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<NCMedicalBodyComponent, NCSurgeryStepExecutionChangedEvent>(OnStepChanged);
    }
    public override void Shutdown() { _runtime.RuntimeStopped -= OnRuntimeStopped; base.Shutdown(); }
    private void OnStartup(Entity<NCMetaClinicalComponent> ent, ref ComponentStartup args)
    {
        ent.Comp.LastPatient = ent.Comp.Patient; ent.Comp.LastOperator = ent.Comp.Operator; _contexts[ent.Owner] = ent.Comp;
    }
    private void OnShutdown(Entity<NCMetaClinicalComponent> ent, ref ComponentShutdown args)
    {
        Invalidate(ent.Owner, ent.Comp); _contexts.Remove(ent.Owner);
    }
    private void OnRuntimeStopped(NCMetaRuntimeInstance runtime)
    {
        foreach (var key in _references.Keys.Where(key => key.Runtime == runtime.Generation).ToArray()) _references.Remove(key);
        foreach (var operation in _operations.Values)
            if (operation.Invocation.Runtime == runtime && operation.Started)
                operation.NativeCompletion.TrySetResult((MetaOperationStatus.OutcomeUnknown, "OutcomeUnknown"));
    }

    public bool CanBind(EntityUid device, string capability) => TryComp<NCMetaClinicalComponent>(device, out var context) && (capability switch
    {
        "meta.medical.MedicalMonitor" => context.DiagnosticTier != NCDiagnosticTier.AdminExact,
        "meta.medical.DosingController" => context.DosingHardware,
        "meta.medical.ProcedureController" => context.ProcedureHardware,
        _ => false
    });

    public bool CanServiceFirmware(EntityUid uid, out Guid patientEpoch)
    {
        patientEpoch = default;
        if (!TryComp<NCMetaClinicalComponent>(uid, out var context) || context.AutomationEnabled || context.Patient != null ||
            _operations.Values.Any(operation => operation.Invocation.Device == uid)) return false;
        patientEpoch = context.PatientEpoch; return true;
    }

    /// <summary>Called by the physical bedside commissioning flow, never from a guest entity ID.</summary>
    public bool TryBindPatient(Entity<NCMetaClinicalComponent?> ent, EntityUid user, EntityUid? patient)
    {
        if (!Resolve(ent, ref ent.Comp) || !_devices.CanManage(ent.Owner, user) ||
            patient is { } target && (!HasComp<NCMedicalBodyComponent>(target) || !_interaction.InRangeUnobstructed(ent.Owner, target))) return false;
        Invalidate(ent.Owner, ent.Comp); ent.Comp.Patient = patient; ent.Comp.Operator = user; ent.Comp.StationaryScannerId = null;
        if (ent.Comp.DiagnosticTool == null && HasComp<NCDiagnosticCapabilityComponent>(ent.Owner)) ent.Comp.DiagnosticTool = ent.Owner;
        ent.Comp.LastPatient = patient; ent.Comp.LastOperator = user; return true;
    }
    public bool TrySetAutomation(Entity<NCMetaClinicalComponent?> ent, EntityUid user, bool enabled)
    {
        if (!Resolve(ent, ref ent.Comp) || !_devices.CanManage(ent.Owner, user) || ent.Comp.Operator != user) return false;
        ent.Comp.AutomationEnabled = enabled; if (!enabled) Invalidate(ent.Owner, ent.Comp); return true;
    }
    private void Invalidate(EntityUid uid, NCMetaClinicalComponent context)
    {
        context.PatientEpoch = Guid.NewGuid();
        foreach (var key in _references.Where(pair => pair.Value.Device == uid).Select(pair => pair.Key).ToArray()) _references.Remove(key);
        foreach (var operation in _operations.Values)
            if (operation.Invocation.Device == uid && operation.Started)
                operation.NativeCompletion.TrySetResult((MetaOperationStatus.OutcomeUnknown, "OutcomeUnknown"));
        _devices.TryInvalidate(uid);
    }
    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        // Drain while world ingress is quiescing, so an admitted durable mutation can settle its lease.
        for (var count = 0; count < 32 && _admissions.TryDequeue(out var action); count++) action();
        foreach (var (uid, context) in _contexts)
            if (context.Patient != context.LastPatient || context.Operator != context.LastOperator)
            { Invalidate(uid, context); context.LastPatient = context.Patient; context.LastOperator = context.Operator; }
        if (!_persistence.TryCaptureTime(out _, out _)) return;
        foreach (var operation in _operations.Values)
            if (operation.Started && _timing.CurTime >= operation.Deadline)
                operation.NativeCompletion.TrySetResult((MetaOperationStatus.OutcomeUnknown, "OutcomeUnknown"));
    }

    private bool CanObserve(EntityUid device, out NCMetaClinicalComponent context, out EntityUid patient, out EntityUid user)
    {
        patient = default; user = default;
        if (!TryComp(device, out context!) || context.Interlocked || context.Patient is not { } target || context.Operator is not { } actor ||
            !Exists(actor) || !_blocker.CanConsciouslyPerformAction(actor) || !HasComp<NCMedicalBodyComponent>(target) || !_devices.CanManage(device, actor, requireRange: false) ||
            !_interaction.InRangeUnobstructed(actor, device) || !_interaction.InRangeUnobstructed(device, target)) return false;
        patient = target; user = actor; return true;
    }
    private bool TryResolvePatient(NCMetaDeviceInvocation invocation, MetaValue value, EntityUid expected)
    {
        return invocation.Runtime.Process.TryGetOpaqueToken(value, out var token) &&
            _references.TryGetValue((invocation.Runtime.Generation, token), out var reference) && reference.Patient == expected &&
            TryComp<NCMetaClinicalComponent>(reference.Device, out var source) && source.Patient == expected &&
            source.PatientEpoch == reference.Epoch && source.LastPatient == expected;
    }
    public MetaValue Read(NCMetaDeviceInvocation invocation)
    {
        var stationary = CanObserveStationary(invocation.Device, out var context, out var patient);
        EntityUid user = default;
        if (!stationary && (context is { StationaryScannerId: not null } ||
            !CanObserve(invocation.Device, out context, out patient, out user))) return Error(invocation, "Interlock");
        var process = invocation.Runtime.Process;
        if (invocation.Member.Name == "patient")
        {
            var previous = _references.FirstOrDefault(pair => pair.Key.Runtime == invocation.Runtime.Generation &&
                pair.Value.Device == invocation.Device && pair.Value.Patient == patient && pair.Value.Epoch == context.PatientEpoch);
            var token = previous.Key.Token;
            if (token == 0)
            {
                if (_references.Count >= 1024 || _references.Keys.Count(key => key.Runtime == invocation.Runtime.Generation) >= 32 || _nextReference == ulong.MaxValue) return Error(invocation, "QuotaExceeded");
                token = ++_nextReference; _references.Add((invocation.Runtime.Generation, token), new(invocation.Device, patient, context.PatientEpoch));
            }
            return process.CreateResult(ReturnType(invocation), process.CreateOpaque(process.GetTypeId("meta.medical.PatientRef"), token));
        }
        if (!TryResolvePatient(invocation, invocation.Request.Arguments[0], patient)) return Error(invocation, "StaleState");
        NCMedicalSnapshot snapshot;
        if (context.DiagnosticTier == NCDiagnosticTier.AdminExact || !(stationary
                ? _diagnostics.TryBuildForStationaryScanner(context.DiagnosticTool!.Value, patient, context.DiagnosticTier, out snapshot, out _)
                : _diagnostics.TryBuildForActor(user, patient, context.DiagnosticTool, context.DiagnosticTier, out snapshot, out _))) return Error(invocation, "AccessDenied");
        var vitals = snapshot.Vitals;
        MetaValue Band(NCDiagnosticBand band) => process.CreateEnum(process.GetTypeId("meta.medical.DiagnosticBand"), (int) band);
        var oxygenType = process.GetTypeId("option<meta.medical.NumericRange>");
        var oxygen = vitals.OxygenationPermille is { } range
            ? process.CreateVariant(oxygenType, 1, process.CreateRecord(process.GetTypeId("meta.medical.NumericRange"), [MetaValue.Int(range.Minimum), MetaValue.Int(range.Maximum)]))
            : process.CreateVariant(oxygenType, 0, null);
        var report = process.CreateRecord(process.GetTypeId("meta.medical.MedicalReport"), [Band(vitals.Pain), Band(vitals.Shock),
            process.CreateEnum(process.GetTypeId("meta.medical.Consciousness"), (int) vitals.Consciousness),
            process.CreateEnum(process.GetTypeId("meta.medical.Condition"), (int) vitals.Condition), oxygen,
            Band(vitals.ExternalBleeding), Band(vitals.InternalBleeding), _runtime.CreateStamp(process)]);
        return process.CreateResult(ReturnType(invocation), report);
    }

    public MetaHostResult Commit(NCMetaDeviceInvocation invocation)
    {
        if (!_persistence.TryCaptureTime(out var epoch, out var time) || _operations.Count >= 64) return CompletedError(invocation, "Busy");
        var process = invocation.Runtime.Process;
        var key = process.GetString(invocation.Request.Arguments[^1]);
        if (!MetaOperationReceipt.IsValidKey(key)) return CompletedError(invocation, "InvalidArgument");
        var kind = invocation.Member.Owner.EndsWith("DosingController", StringComparison.Ordinal) ? "dose" : "procedure";
        var id = NCMetaPersistenceSystem.StableGuid(MetaOperationReceipt.HashFields("meta.clinical", invocation.Runtime.Owner, invocation.DeviceId, kind, key));
        if (invocation.Member.Name == "operationStatus")
        {
            if (_statusReads >= 64) return CompletedError(invocation, "Busy");
            _statusReads++; _ = ReadStatusAsync(invocation, id, kind, epoch, time); return MetaHostResult.Awaiting(invocation.OperationId);
        }
        if (!CanObserve(invocation.Device, out var context, out var patient, out var user) || !context.AutomationEnabled) return CompletedError(invocation, "Interlock");
        if (!TryResolvePatient(invocation, invocation.Request.Arguments[0], patient)) return CompletedError(invocation, "StaleState");
        if (_operations.ContainsKey(id)) return CompletedError(invocation, "Busy");
        if (_timing.CurTime < context.NextCommand) return CompletedError(invocation, "RateLimited");
        if (kind == "dose" && !context.DosingHardware || kind == "procedure" && !context.ProcedureHardware) return CompletedError(invocation, "Unsupported");
        var lease = _persistence.TryBeginNativeMutation(); if (lease == null) return CompletedError(invocation, "Busy");
        var fingerprint = MetaOperationReceipt.HashFields(invocation.Runtime.Owner, invocation.DeviceId, kind, key,
            context.PatientEpoch.ToString("N"), context.ReservoirSolution, context.ConfiguredDose.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            context.MaximumDose.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        var operation = new Operation(invocation, context, patient, user, key, id, fingerprint, epoch, time);
        _operations.Add(id, operation); context.NextCommand = _timing.CurTime + TimeSpan.FromSeconds(1);
        _ = ExecuteAsync(operation, lease); return MetaHostResult.Awaiting(invocation.OperationId);
    }

    private bool CanCommit(Operation operation) => _runtime.CanContinue(operation.Invocation, requireWaiting: !operation.Started) &&
        (!operation.Started || _timing.CurTime < operation.Deadline) &&
        CanObserve(operation.Invocation.Device, out var context, out var patient, out var user) && context.AutomationEnabled &&
        context.PatientEpoch == operation.PatientEpoch && patient == operation.Patient && user == operation.User &&
        context.ConfiguredDose == operation.Dose && context.MaximumDose == operation.MaximumDose && context.ReservoirSolution == operation.Reservoir;

    private (MetaOperationStatus, string) ApplyNative(Operation operation)
    {
        if (!CanCommit(operation)) return (MetaOperationStatus.Rejected, "PermissionRevoked");
        var context = Comp<NCMetaClinicalComponent>(operation.Invocation.Device);
        if (operation.Kind == "procedure")
        {
            if (!context.ProcedureHardware || !float.IsFinite(context.MaximumStepSeconds) || context.MaximumStepSeconds is <= 0 or > 300) return (MetaOperationStatus.Rejected, "Unsupported");
            var result = _surgery.TryStartCurrentStep(operation.User, operation.Patient, out _, () => CanCommit(operation));
            if (result != NCSurgeryInteractionNotice.StepStarted) return (MetaOperationStatus.Rejected, "ActionRejected");
            operation.Started = true; operation.Deadline = _timing.CurTime + TimeSpan.FromSeconds(context.MaximumStepSeconds);
            return (MetaOperationStatus.Running, "");
        }
        if (!context.DosingHardware || !float.IsFinite(context.ConfiguredDose) || !float.IsFinite(context.MaximumDose) ||
            context.ConfiguredDose <= 0 || context.ConfiguredDose > context.MaximumDose || context.MaximumDose > 50 ||
            context.ReservoirSolution.Length is 0 or > 64) return (MetaOperationStatus.Rejected, "Interlock");
        var amount = FixedPoint2.New(context.ConfiguredDose);
        if (amount <= 0 || !_solutions.TryGetSolution(operation.Invocation.Device, context.ReservoirSolution, out var source, out var solution) ||
            solution.Volume < amount || !_solutions.TryGetInjectableSolution(operation.Patient, out var destination, out var target) ||
            target.AvailableVolume < amount) return (MetaOperationStatus.Rejected, "Interlock");
        var self = new SelfBeforeInjectEvent(operation.User, operation.Invocation.Device, operation.Patient); RaiseLocalEvent(operation.User, self);
        if (self.Cancelled || self.TargetGettingInjected != operation.Patient) return (MetaOperationStatus.Rejected, "ActionRejected");
        var before = new TargetBeforeInjectEvent(operation.User, operation.Invocation.Device, operation.Patient); RaiseLocalEvent(operation.Patient, ref before);
        // Events may mutate inventory/context. Resolve exact physical reservoirs again before consuming anything.
        if (before.Cancelled || before.TargetGettingInjected != operation.Patient || !CanCommit(operation) ||
            !_solutions.TryGetSolution(operation.Invocation.Device, context.ReservoirSolution, out source, out solution) || solution.Volume < amount ||
            !_solutions.TryGetInjectableSolution(operation.Patient, out destination, out target) || target.AvailableVolume < amount) return (MetaOperationStatus.Rejected, "Interlock");
        var dose = _solutions.SplitSolution(source.Value, amount);
        _reactive.DoEntityReaction(operation.Patient, dose, ReactionMethod.Injection);
        _solutions.Inject(operation.Patient, destination.Value, dose);
        return (MetaOperationStatus.Applied, "");
    }

    private void OnStepChanged(Entity<NCMedicalBodyComponent> ent, ref NCSurgeryStepExecutionChangedEvent args)
    {
        if (args.Running) return;
        foreach (var operation in _operations.Values)
            if (operation.Started && operation.Kind == "procedure" && operation.Patient == ent.Owner && operation.User == args.Practitioner)
                operation.NativeCompletion.TrySetResult(ResolveProcedureOutcome(args));
    }

    /// <summary>The native result is authoritative; an ambiguous notification never becomes an applied receipt.</summary>
    public static (MetaOperationStatus Status, string Error) ResolveProcedureOutcome(in NCSurgeryStepExecutionChangedEvent result) =>
        result.Cancelled ? (MetaOperationStatus.Rejected, "Cancelled") : result.StepApplied == true ? (MetaOperationStatus.Applied, "") :
        result.StepApplied == false ? (MetaOperationStatus.Rejected, "ActionRejected") : (MetaOperationStatus.OutcomeUnknown, "OutcomeUnknown");

    private Task<T> OnMainThread<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _admissions.Enqueue(() => { try { completion.TrySetResult(action()); } catch (Exception exception) { completion.TrySetException(exception); } });
        return completion.Task;
    }
    private static int ReturnType(NCMetaDeviceInvocation invocation) => invocation.Runtime.Artifact.GetImport(invocation.Request.ImportId).ReturnType!.Value;
    private static MetaValue Error(NCMetaDeviceInvocation invocation, string code) => invocation.Runtime.Process.CreateErrorResult(ReturnType(invocation), code);
    private static MetaHostResult CompletedError(NCMetaDeviceInvocation invocation, string code) => MetaHostResult.Completed(Error(invocation, code));
}
