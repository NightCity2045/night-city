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
using Content.Shared._NC.Persistence.Time;
using Content.Shared.ActionBlocker;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Timing;

namespace Content.Server._NC.Meta.Domains;

/// <summary>Physical cargo and equipped-gun operations. Durable admission never substitutes for native authority.</summary>
public sealed partial class NCMetaDroneOperationsSystem : EntitySystem, INCMetaDeviceAdapter
{
    private sealed record Outcome(MetaOperationStatus Status, int Completed, string Error = "");
    private sealed class Job(NCMetaDeviceInvocation invocation, Guid id, string kind, string fingerprint,
        EntityUid target, EntityUid? item, ulong controlEpoch, NCSessionEpoch epoch, NCActiveWorldTime time)
    {
        public readonly NCMetaDeviceInvocation Invocation = invocation;
        public readonly Guid Id = id;
        public readonly string Kind = kind;
        public readonly string Fingerprint = fingerprint;
        public readonly EntityUid Target = target;
        public readonly EntityUid? Item = item;
        public readonly ulong ControlEpoch = controlEpoch;
        public readonly NCSessionEpoch Epoch = epoch;
        public readonly NCActiveWorldTime Time = time;
        public readonly TaskCompletionSource<Outcome> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan Deadline;
        public bool Aiming;
        public bool OutputCommitting;
        public bool ShotConfirmed;
        public bool Cancelled;
    }
    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCMetaPersistenceSystem _persistence = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCMetaDroneSystem _navigation = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedStorageSystem _storage = default!;
    [Dependency] private SharedGunSystem _guns = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private RotateToFaceSystem _rotation = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    private readonly Dictionary<Guid, Job> _jobs = [];
    private readonly ConcurrentQueue<Action> _callbacks = new();
    private int _statusReads;
    public IEnumerable<string> Members => ["meta.drone.Perception.observe", "meta.drone.Manipulator.holding", "meta.drone.Manipulator.pickUp",
        "meta.drone.Manipulator.dropAt", "meta.drone.Manipulator.storeIn", "meta.drone.Manipulator.operationStatus", "meta.drone.Manipulator.cancel",
        "meta.drone.WeaponController.snapshot", "meta.drone.WeaponController.fire", "meta.drone.WeaponController.operationStatus", "meta.drone.WeaponController.cancel"];
    public override void Initialize()
    {
        base.Initialize(); _runtime.RegisterAdapter(this);
        _runtime.RuntimeStopped += OnRuntimeStopped; _navigation.ControlEpochChanged += OnControlChanged;
        SubscribeLocalEvent<NCMetaDroneWeaponComponent, AmmoShotEvent>(OnShot);
        SubscribeLocalEvent<NCMetaDroneSensorComponent, ComponentShutdown>(OnSensorShutdown);
    }
    public override void Shutdown()
    {
        _runtime.RuntimeStopped -= OnRuntimeStopped; _navigation.ControlEpochChanged -= OnControlChanged;
        foreach (var (token, contact) in _contacts.ToArray()) ReleaseContact(token, contact);
        foreach (var job in _jobs.Values) Cancel(job, "Cancelled");
        base.Shutdown();
    }
    private void OnSensorShutdown(Entity<NCMetaDroneSensorComponent> ent, ref ComponentShutdown args) => OnControlChanged(ent.Owner);
    private void OnControlChanged(EntityUid uid)
    {
        foreach (var (token, contact) in _contacts.Where(pair => pair.Value.Sensor == uid).ToArray()) ReleaseContact(token, contact);
        foreach (var job in _jobs.Values.Where(job => job.Invocation.Device == uid)) Cancel(job, "ControlLost");
    }
    private void OnRuntimeStopped(NCMetaRuntimeInstance runtime)
    {
        foreach (var (token, contact) in _contacts.Where(pair => pair.Value.Runtime == runtime.Generation).ToArray()) ReleaseContact(token, contact);
        foreach (var job in _jobs.Values.Where(job => job.Invocation.Runtime == runtime)) Cancel(job, "PermissionRevoked");
    }
    public bool CanBind(EntityUid uid, string capability) => HasComp<NCMetaDroneComponent>(uid) && (capability switch
    {
        "meta.drone.Perception" => HasComp<NCMetaDroneSensorComponent>(uid) && HasComp<Content.Shared.SurveillanceCamera.Components.SurveillanceCameraComponent>(uid),
        "meta.drone.Manipulator" => HasComp<NCMetaDroneManipulatorComponent>(uid) && HasComp<HandsComponent>(uid),
        "meta.drone.WeaponController" => HasComp<NCMetaDroneWeaponComponent>(uid) && HasComp<GunComponent>(uid) && HasComp<BallisticAmmoProviderComponent>(uid),
        _ => false
    });
    public MetaValue Read(NCMetaDeviceInvocation invocation)
    {
        if (!CanBind(invocation.Device, invocation.Member.Owner)) return Error(invocation, "Unsupported");
        if (invocation.Member.Owner == "meta.drone.Perception") return Observe(invocation);
        var process = invocation.Runtime.Process;
        if (invocation.Member.Name == "holding") return process.CreateResult(ReturnType(invocation), MetaValue.Bool(
            TryComp<NCMetaDroneManipulatorComponent>(invocation.Device, out var manipulator) && _hands.GetHeldItem(invocation.Device, manipulator.Hand) != null));
        var equipped = HasComp<GunComponent>(invocation.Device) && HasComp<BallisticAmmoProviderComponent>(invocation.Device);
        return process.CreateResult(ReturnType(invocation), process.CreateRecord(process.GetTypeId("meta.drone.WeaponState"),
            [MetaValue.Bool(equipped), MetaValue.Bool(equipped && _guns.CanFireControlled(invocation.Device)),
                MetaValue.Int(equipped ? _guns.GetAmmoCount(invocation.Device) : 0), _runtime.CreateStamp(process)]));
    }
    public MetaHostResult Commit(NCMetaDeviceInvocation invocation)
    {
        if (!CanBind(invocation.Device, invocation.Member.Owner) || _jobs.Count >= 64 || !_persistence.TryCaptureTime(out var epoch, out var time)) return CompletedError(invocation, "Busy");
        var process = invocation.Runtime.Process; var key = process.GetString(invocation.Request.Arguments[^1]);
        if (!MetaOperationReceipt.IsValidKey(key)) return CompletedError(invocation, "InvalidArgument");
        var kind = invocation.Member.Owner == "meta.drone.Manipulator" ? "cargo" : "weapon";
        var id = NCMetaPersistenceSystem.StableGuid(MetaOperationReceipt.HashFields("meta.drone", invocation.Runtime.Owner, invocation.DeviceId, kind, key));
        if (invocation.Member.Name is "operationStatus" or "cancel")
        {
            if (_statusReads >= 64) return CompletedError(invocation, "Busy");
            if (invocation.Member.Name == "cancel" && _jobs.TryGetValue(id, out var current)) Cancel(current, "Cancelled");
            _statusReads++; _ = ReadStatusAsync(invocation, id, kind); return MetaHostResult.Awaiting(invocation.OperationId);
        }
        if (!_navigation.CanUseActuators(invocation.Device, invocation.Actor) || !_devices.IsPowered(invocation.Device)) return CompletedError(invocation, "ControlLost");
        if (_jobs.Values.Any(job => job.Invocation.Device == invocation.Device)) return CompletedError(invocation, "Busy");
        var drone = Comp<NCMetaDroneComponent>(invocation.Device);
        if (_timing.CurTime < drone.NextActuatorCommand) return CompletedError(invocation, "RateLimited");
        EntityUid target;
        if (invocation.Member.Name == "dropAt")
        { if (!_navigation.TryResolveWaypointReference(invocation.Device, invocation.Runtime, invocation.Request.Arguments[0], out target)) return CompletedError(invocation, "NotFound"); }
        else if (!TryResolveContact(invocation, invocation.Request.Arguments[0], out target)) return CompletedError(invocation, "StaleState");
        var item = TryComp<NCMetaDroneManipulatorComponent>(invocation.Device, out var manipulator) ? _hands.GetHeldItem(invocation.Device, manipulator.Hand) : null;
        // UIDs are an active-session fingerprint only. No saved UID is ever resolved into a world entity.
        var fingerprint = MetaOperationReceipt.HashFields(invocation.Member.Name, epoch.ToString(), target.ToString(), item?.ToString() ?? "");
        var lease = _persistence.TryBeginNativeMutation(); if (lease == null) return CompletedError(invocation, "Busy");
        var job = new Job(invocation, id, kind, fingerprint, target, item, drone.ControlEpoch, epoch, time);
        drone.NextActuatorCommand = _timing.CurTime + TimeSpan.FromMilliseconds(250);
        drone.PendingPhysicalOperations++;
        _jobs.Add(id, job); _ = ExecuteAsync(job, lease); return MetaHostResult.Awaiting(invocation.OperationId);
    }
    private bool CanCommit(Job job) => !job.Cancelled && _runtime.CanContinue(job.Invocation) && _devices.IsPowered(job.Invocation.Device) &&
        _navigation.CanUseActuators(job.Invocation.Device, job.Invocation.Actor) && TryComp<NCMetaDroneComponent>(job.Invocation.Device, out var drone) &&
        drone.ControlEpoch == job.ControlEpoch && Exists(job.Target) && (!job.Aiming || _timing.CurTime < job.Deadline);
    private bool CanTarget(Job job) => CanCommit(job) && (job.Invocation.Member.Name == "dropAt"
        ? _navigation.TryResolveWaypointReference(job.Invocation.Device, job.Invocation.Runtime, job.Invocation.Request.Arguments[0], out var marker) && marker == job.Target
        : TryResolveContact(job.Invocation, job.Invocation.Request.Arguments[0], out var target) && target == job.Target);
    private void BeginNative(Job job)
    {
        if (!CanTarget(job)) { Cancel(job, "StaleState"); return; }
        if (job.Kind == "weapon")
        {
            if (!TryComp<NCMetaDroneWeaponComponent>(job.Invocation.Device, out var weapon) || !ValidWeapon(weapon) || !_guns.CanFireControlled(job.Invocation.Device) ||
                _guns.GetAmmoCount(job.Invocation.Device) <= 0) { Cancel(job, "Interlock"); return; }
            job.Aiming = true; job.Deadline = _timing.CurTime + TimeSpan.FromSeconds(1.5); return;
        }
        job.OutputCommitting = true;
        try
        {
            var success = job.Invocation.Member.Name switch
            {
                "pickUp" => TryPickUp(job.Invocation.Device, job.Target, () => CanTarget(job), job.Invocation.Actor),
                "dropAt" => job.Item is { } held && TryDropAt(job.Invocation.Device, held, job.Target, () => CanTarget(job), job.Invocation.Actor),
                "storeIn" => job.Item is { } held && TryStoreIn(job.Invocation.Device, held, job.Target, () => CanTarget(job), job.Invocation.Actor),
                _ => false
            };
            job.Completion.TrySetResult(new(success ? MetaOperationStatus.Applied : job.Cancelled ? MetaOperationStatus.OutcomeUnknown : MetaOperationStatus.Rejected,
                success ? 1 : 0, success ? "" : job.Cancelled ? "OutcomeUnknown" : "ActionRejected"));
        }
        finally { job.OutputCommitting = false; }
    }
    private void Cancel(Job job, string error)
    {
        job.Cancelled = true;
        if (!job.OutputCommitting) job.Completion.TrySetResult(new(MetaOperationStatus.Rejected, 0, error));
    }
    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        for (var work = 0; work < 32 && _callbacks.TryDequeue(out var callback); work++) callback();
        PruneContacts();
        foreach (var job in _jobs.Values.ToArray())
        {
            if (job.Completion.Task.IsCompleted) continue;
            if (!CanCommit(job)) { Cancel(job, "ControlLost"); continue; }
            if (job.Aiming) AdvanceAim(job, frameTime);
        }
    }
    private Task<T> OnMain<T>(Func<T> callback)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _callbacks.Enqueue(() => { try { completion.TrySetResult(callback()); } catch (Exception error) { completion.TrySetException(error); } });
        return completion.Task;
    }
    private static int ReturnType(NCMetaDeviceInvocation invocation) => invocation.Runtime.Artifact.GetImport(invocation.Request.ImportId).ReturnType!.Value;
    private static MetaValue Error(NCMetaDeviceInvocation invocation, string code) => invocation.Runtime.Process.CreateErrorResult(ReturnType(invocation), code);
    private static MetaHostResult CompletedError(NCMetaDeviceInvocation invocation, string code) => MetaHostResult.Completed(Error(invocation, code));
}
