// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using Content.Meta.Runtime;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;
using Content.Server.NPC.Components;
using Content.Server.NPC.Systems;
using Content.Shared.Movement.Components;
using Content.Shared.NPC;
using Content.Shared.Interaction;
using Content.Shared.ActionBlocker;
using Robust.Shared.Timing;
using Robust.Shared.Player;
using Robust.Shared.Physics.Components;

namespace Content.Server._NC.Meta.Domains;

/// <summary>Issues bounded native steering requests for physical drones. A waypoint reference has one process/device generation.</summary>
public sealed partial class NCMetaDroneSystem : EntitySystem, INCMetaDeviceAdapter
{
    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCMetaPersistenceSystem _persistence = default!;
    [Dependency] private NPCSteeringSystem _steering = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    private readonly Dictionary<EntityUid, NCMetaDroneComponent> _drones = [];
    private ulong _nextReference;
    private TimeSpan _nextUpdate;
    public event Action<EntityUid>? ControlEpochChanged;
    public IEnumerable<string> Members => ["meta.drone.DroneController.resolveWaypoint", "meta.drone.DroneController.setDestination",
        "meta.drone.NavigationSupervisor.snapshot", "meta.drone.NavigationSupervisor.requestMode", "meta.drone.NavigationSupervisor.cancel"];

    public override void Initialize()
    {
        base.Initialize(); _runtime.RegisterAdapter(this); _runtime.RuntimeStopped += OnRuntimeStopped;
        SubscribeLocalEvent<NCMetaDroneComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<NCMetaDroneComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<PlayerAttachedEvent>(OnPlayerAttached);
    }
    public override void Shutdown() { _runtime.RuntimeStopped -= OnRuntimeStopped; base.Shutdown(); }
    private void OnStartup(Entity<NCMetaDroneComponent> ent, ref ComponentStartup args) => _drones[ent.Owner] = ent.Comp;
    private void OnShutdown(Entity<NCMetaDroneComponent> ent, ref ComponentShutdown args) { Cancel(ent.Owner, ent.Comp, false); _drones.Remove(ent.Owner); }
    private void OnRuntimeStopped(NCMetaRuntimeInstance instance)
    {
        foreach (var (uid, drone) in _drones)
        {
            if (drone.Controller == instance) Cancel(uid, drone, false);
            foreach (var key in drone.References.Keys.Where(k => k.Runtime == instance.Generation).ToArray()) drone.References.Remove(key);
        }
    }
    public bool CanBind(EntityUid device, string capability) => capability is "meta.drone.DroneController" or "meta.drone.NavigationSupervisor" &&
        HasComp<NCMetaDroneComponent>(device) && HasComp<InputMoverComponent>(device);

    public MetaValue Read(NCMetaDeviceInvocation invocation)
    {
        if (!TryComp<NCMetaDroneComponent>(invocation.Device, out var drone)) return Error(invocation, "Unsupported");
        if (invocation.Member.Owner == "meta.drone.NavigationSupervisor")
            return invocation.Runtime.Process.CreateResult(ReturnType(invocation), State(invocation.Runtime.Process, drone));
        var process = invocation.Runtime.Process; var name = process.GetString(invocation.Request.Arguments[0]);
        if (name.Length > 96 || !drone.Waypoints.TryGetValue(name, out var target) || !Exists(target)) return Error(invocation, "NotFound");
        if (Transform(target).MapUid != Transform(invocation.Device).MapUid) return Error(invocation, "PathUnavailable");
        var previous = drone.References.FirstOrDefault(pair => pair.Key.Runtime == invocation.Runtime.Generation && pair.Value == (name, target));
        var token = previous.Key.Token;
        if (token == 0)
        {
            if (drone.References.Count >= 64 || _nextReference == ulong.MaxValue) return Error(invocation, "QuotaExceeded");
            token = ++_nextReference; drone.References.Add((invocation.Runtime.Generation, token), (name, target));
        }
        return process.CreateResult(ReturnType(invocation), process.CreateOpaque(process.GetTypeId("meta.drone.WaypointRef"), token));
    }

    public MetaHostResult Commit(NCMetaDeviceInvocation invocation)
    {
        var process = invocation.Runtime.Process;
        if (!TryComp<NCMetaDroneComponent>(invocation.Device, out var drone) || !HasComp<InputMoverComponent>(invocation.Device)) return CompletedError(invocation, "Unsupported");
        if (invocation.Member.Owner == "meta.drone.NavigationSupervisor") return CommitSupervisor(invocation, drone);
        if (!CanNavigate(invocation.Device, drone) || drone.Mode == NCMetaDroneMode.Assistant && invocation.Actor != drone.Operator) return CompletedError(invocation, "ControlLost");
        if (drone.Controller != null && drone.Controller != invocation.Runtime) return CompletedError(invocation, "Conflict");
        if (!process.TryGetOpaqueToken(invocation.Request.Arguments[0], out var token) || !drone.References.TryGetValue((invocation.Runtime.Generation, token), out var point) ||
            !drone.Waypoints.TryGetValue(point.Name, out var target) || target != point.Target || !Exists(target)) return CompletedError(invocation, "NotFound");
        if (Transform(target).MapUid != Transform(invocation.Device).MapUid) return CompletedError(invocation, "PathUnavailable");
        if (drone.Controller == invocation.Runtime && drone.Target == target) return Receipt(invocation, drone);
        if (_timing.CurTime < drone.NextCommand) return CompletedError(invocation, "RateLimited");
        if (!float.IsFinite(drone.MaximumNavigationSeconds) || drone.MaximumNavigationSeconds is <= 0 or > 300 || drone.RequestId == long.MaxValue ||
            drone.MaximumReplans is < 0 or > 4 || !float.IsFinite(drone.ReplanCooldownSeconds) || drone.ReplanCooldownSeconds is < 0.5f or > 10) return CompletedError(invocation, "QuotaExceeded");
        if (drone.Controller != null) Cancel(invocation.Device, drone, true);
        drone.Controller = invocation.Runtime; drone.Target = target; drone.RequestId++; drone.IssuedTick = _timing.CurTick.Value;
        drone.TaskControlEpoch = drone.ControlEpoch; drone.Outcome = 1; drone.Replans = 0;
        drone.NextReplan = _timing.CurTime + TimeSpan.FromSeconds(drone.ReplanCooldownSeconds);
        drone.Deadline = _timing.CurTime + TimeSpan.FromSeconds(drone.MaximumNavigationSeconds); drone.NextCommand = _timing.CurTime + TimeSpan.FromSeconds(1);
        EnsureComp<ActiveNPCComponent>(invocation.Device);
        _steering.Register(invocation.Device, Transform(target).Coordinates);
        return Receipt(invocation, drone);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_persistence.TryCaptureTime(out _, out _) || _timing.CurTime < _nextUpdate) return;
        _nextUpdate = _timing.CurTime + TimeSpan.FromMilliseconds(200);
        foreach (var (uid, drone) in _drones)
        {
            _runtime.TryPublishEvent(uid, "meta.drone.NavigationSupervisor.stateChanged", process => State(process, drone));
            if (drone.Controller == null || drone.IssuedTick >= _timing.CurTick.Value) continue;
            if (!CanNavigate(uid, drone) || drone.TaskControlEpoch != drone.ControlEpoch) { Cancel(uid, drone, true); continue; }
            if (drone.Target is not { } target || !Exists(target) || !TryComp<NPCSteeringComponent>(uid, out var steering)) { Finish(uid, drone, 1); continue; }
            if (Transform(target).MapUid != Transform(uid).MapUid) { Finish(uid, drone, 3); continue; }
            if (_timing.CurTime > drone.Deadline) { Finish(uid, drone, 1); continue; }
            if (steering.Status == SteeringStatus.NoPath)
            {
                if (drone.Replans >= drone.MaximumReplans) { Finish(uid, drone, 3); continue; }
                if (_timing.CurTime >= drone.NextReplan)
                {
                    _steering.Unregister(uid); _steering.Register(uid, Transform(target).Coordinates);
                    drone.Replans++; drone.IssuedTick = _timing.CurTick.Value;
                    drone.NextReplan = _timing.CurTime + TimeSpan.FromSeconds(drone.ReplanCooldownSeconds);
                }
                continue;
            }
            if (steering.Status == SteeringStatus.InRange) Finish(uid, drone, 0);
        }
    }

    public bool TrySetManualControl(Entity<NCMetaDroneComponent?> ent, EntityUid user, bool manual)
    {
        return TrySetMode(ent, user, manual ? NCMetaDroneMode.Manual : NCMetaDroneMode.Autonomous);
    }
    public bool CanServiceFirmware(EntityUid uid, out ulong epoch)
    {
        epoch = 0;
        if (!TryComp<NCMetaDroneComponent>(uid, out var drone) || drone.Mode != NCMetaDroneMode.Maintenance || drone.Controller != null || drone.PendingPhysicalOperations != 0 ||
            drone.ControlEpoch == ulong.MaxValue || HasComp<NPCSteeringComponent>(uid) || !HasComp<InputMoverComponent>(uid) ||
            !TryComp<PhysicsComponent>(uid, out var body) || !float.IsFinite(body.LinearVelocity.LengthSquared()) || body.LinearVelocity.LengthSquared() > 0.01f) return false;
        epoch = drone.ControlEpoch; return true;
    }
    public bool TrySetMode(Entity<NCMetaDroneComponent?> ent, EntityUid user, NCMetaDroneMode mode)
    {
        if (!Resolve(ent, ref ent.Comp) || !Enum.IsDefined(mode) || !_devices.CanManage(ent.Owner, user) ||
            !_blocker.CanConsciouslyPerformAction(user) || mode == NCMetaDroneMode.Assistant && !HasComp<ActorComponent>(user) || ent.Comp.ControlEpoch == ulong.MaxValue) return false;
        Cancel(ent.Owner, ent.Comp, true); ent.Comp.Mode = mode;
        ent.Comp.ManualOverride = mode is NCMetaDroneMode.Manual or NCMetaDroneMode.Maintenance or NCMetaDroneMode.Emergency;
        ent.Comp.Operator = mode == NCMetaDroneMode.Assistant ? user : null;
        ent.Comp.AssistantUntil = _timing.CurTime + TimeSpan.FromSeconds(30);
        ent.Comp.ControlEpoch++; ent.Comp.References.Clear(); ControlEpochChanged?.Invoke(ent.Owner); _devices.TryInvalidate(ent.Owner); return true;
    }

    public bool TryRenewAssistant(Entity<NCMetaDroneComponent?> ent, EntityUid user)
    {
        if (!Resolve(ent, ref ent.Comp) || ent.Comp.Mode != NCMetaDroneMode.Assistant || ent.Comp.Operator != user ||
            !_devices.CanManage(ent.Owner, user) || !HasComp<ActorComponent>(user) || !_blocker.CanConsciouslyPerformAction(user)) return false;
        ent.Comp.AssistantUntil = _timing.CurTime + TimeSpan.FromSeconds(30); return true;
    }

    private bool CanNavigate(EntityUid uid, NCMetaDroneComponent drone) => !HasComp<ActorComponent>(uid) && drone.AutonomousEnabled && !drone.ManualOverride &&
        (drone.Mode == NCMetaDroneMode.Autonomous || drone.Mode == NCMetaDroneMode.Assistant &&
            drone.Operator is { } user && _timing.CurTime < drone.AssistantUntil && Exists(user) &&
            HasComp<ActorComponent>(user) && _blocker.CanConsciouslyPerformAction(user) && _devices.CanManage(uid, user, requireRange: false));

    private MetaHostResult CommitSupervisor(NCMetaDeviceInvocation invocation, NCMetaDroneComponent drone)
    {
        if (invocation.Member.Name == "cancel")
        {
            if (invocation.Request.Arguments[0].AsInt() != drone.RequestId) return CompletedError(invocation, "StaleState");
            if (drone.Controller != null && drone.Controller != invocation.Runtime) return CompletedError(invocation, "Conflict");
            Cancel(invocation.Device, drone, true);
        }
        else
        {
            var mode = (NCMetaDroneMode) invocation.Runtime.Process.GetVariantTag(invocation.Request.Arguments[0]);
            if (mode != NCMetaDroneMode.Emergency) return CompletedError(invocation, "AccessDenied");
            Cancel(invocation.Device, drone, true); drone.Mode = mode; drone.ManualOverride = true;
            if (drone.ControlEpoch != ulong.MaxValue) drone.ControlEpoch++;
            drone.References.Clear();
            ControlEpochChanged?.Invoke(invocation.Device); _devices.TryInvalidate(invocation.Device);
        }
        return MetaHostResult.Completed(invocation.Runtime.Process.CreateResult(ReturnType(invocation), State(invocation.Runtime.Process, drone)));
    }

    private MetaValue State(MetaProcess process, NCMetaDroneComponent drone) => process.CreateRecord(process.GetTypeId("meta.drone.DroneState"),
        [process.CreateEnum(process.GetTypeId("meta.drone.DroneMode"), (int) drone.Mode), MetaValue.Int(drone.RequestId),
         process.CreateEnum(process.GetTypeId("meta.drone.NavigationOutcome"), drone.Outcome), _runtime.CreateStamp(process)]);
    private MetaHostResult Receipt(NCMetaDeviceInvocation invocation, NCMetaDroneComponent drone) => MetaHostResult.Completed(
        invocation.Runtime.Process.CreateResult(ReturnType(invocation), invocation.Runtime.Process.CreateRecord(invocation.Runtime.Process.GetTypeId("meta.drone.NavigationReceipt"),
            [MetaValue.Int(drone.RequestId), _runtime.CreateStamp(invocation.Runtime.Process)])));
    public bool TryRegisterWaypoint(Entity<NCMetaDroneComponent?> ent, EntityUid user, EntityUid marker)
    {
        if (!Resolve(ent, ref ent.Comp) || !_devices.CanManage(ent.Owner, user, requireRange: false) || !_interaction.InRangeUnobstructed(user, marker) || !TryComp<NCMetaWaypointComponent>(marker, out var waypoint) ||
            !Exists(marker) || Transform(marker).MapUid != Transform(ent.Owner).MapUid || waypoint.Name.Length is 0 or > 64 ||
            !waypoint.Name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') || ent.Comp.Waypoints.Count >= 32 && !ent.Comp.Waypoints.ContainsKey(waypoint.Name) ||
            ent.Comp.ControlEpoch == ulong.MaxValue) return false;
        Cancel(ent.Owner, ent.Comp, true); ent.Comp.ControlEpoch++; ent.Comp.Waypoints[waypoint.Name] = marker; ent.Comp.References.Clear(); ControlEpochChanged?.Invoke(ent.Owner); _devices.TryInvalidate(ent.Owner); return true;
    }
    public bool CanUseActuators(EntityUid uid, EntityUid? actor) => TryComp<NCMetaDroneComponent>(uid, out var drone) &&
        CanNavigate(uid, drone) && (drone.Mode != NCMetaDroneMode.Assistant || actor == drone.Operator);
    public bool TryResolveWaypointReference(EntityUid uid, NCMetaRuntimeInstance runtime, MetaValue reference, out EntityUid marker)
    {
        marker = default;
        if (!TryComp<NCMetaDroneComponent>(uid, out var drone) || !runtime.Process.TryGetOpaqueToken(reference, out var token) ||
            !drone.References.TryGetValue((runtime.Generation, token), out var point) || !drone.Waypoints.TryGetValue(point.Name, out marker) ||
            marker != point.Target || !Exists(marker) || Transform(marker).MapUid != Transform(uid).MapUid) return false;
        return true;
    }
    private void OnPlayerAttached(PlayerAttachedEvent ev)
    {
        if (ev.Player.AttachedEntity is not { } uid || !TryComp<NCMetaDroneComponent>(uid, out var drone)) return;
        Cancel(uid, drone, true); drone.Mode = NCMetaDroneMode.Manual; drone.ManualOverride = true;
        if (drone.ControlEpoch != ulong.MaxValue) drone.ControlEpoch++;
        drone.References.Clear(); ControlEpochChanged?.Invoke(uid); _devices.TryInvalidate(uid);
    }
    private void Cancel(EntityUid uid, NCMetaDroneComponent drone, bool publish)
    {
        if (publish && drone.Controller != null) Finish(uid, drone, 2);
        else { _steering.Unregister(uid); if (drone.Controller != null) drone.Outcome = 4; drone.Controller = null; drone.Target = null; }
    }
    private void Finish(EntityUid uid, NCMetaDroneComponent drone, int status)
    {
        var requestId = drone.RequestId; _steering.Unregister(uid); drone.Controller = null; drone.Target = null;
        drone.Outcome = status switch { 0 => 2, 1 => 3, 2 => 4, _ => 5 };
        _runtime.TryPublishEvent(uid, "meta.drone.DroneController.navigationFinished", process => process.CreateRecord(process.GetTypeId("meta.drone.NavigationEvent"),
            [MetaValue.Int(requestId), process.CreateEnum(process.GetTypeId("meta.drone.NavigationStatus"), status == 3 ? 1 : status), _runtime.CreateStamp(process)]));
        _runtime.TryPublishEvent(uid, "meta.drone.NavigationSupervisor.navigationOutcome", process => process.CreateRecord(process.GetTypeId("meta.drone.NavigationOutcomeEvent"),
            [MetaValue.Int(requestId), process.CreateEnum(process.GetTypeId("meta.drone.NavigationOutcome"), drone.Outcome), _runtime.CreateStamp(process)]));
    }
    private static int ReturnType(NCMetaDeviceInvocation invocation) => invocation.Runtime.Artifact.GetImport(invocation.Request.ImportId).ReturnType!.Value;
    private static MetaValue Error(NCMetaDeviceInvocation invocation, string code) => invocation.Runtime.Process.CreateErrorResult(ReturnType(invocation), code);
    private static MetaHostResult CompletedError(NCMetaDeviceInvocation invocation, string code) => MetaHostResult.Completed(Error(invocation, code));
}
