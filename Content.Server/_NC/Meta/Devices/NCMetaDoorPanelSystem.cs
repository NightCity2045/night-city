// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Runtime;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.Meta;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.Interaction;
using Content.Shared.Power.EntitySystems;
using Content.Shared.Verbs;
using Content.Server._NC.Meta.Network;
using Robust.Shared.Timing;

namespace Content.Server._NC.Meta.Devices;

/// <summary>A physical panel is the only producer of DoorRequest authority. Its native fallback outlives the VM.</summary>
public sealed partial class NCMetaDoorPanelSystem : EntitySystem
{
    private sealed record Pending(EntityUid Panel, EntityUid Door, EntityUid Actor, bool Open,
        ulong DeviceEpoch, ulong AuthorityEpoch, TimeSpan Deadline);
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private SharedDoorSystem _doors = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private NCMetaNetworkSystem _network = default!;
    private readonly Dictionary<(EntityUid, EntityUid, bool), Pending> _pending = [];
    private readonly PriorityQueue<(EntityUid, EntityUid, bool), TimeSpan> _deadlines = new();

    public override void Initialize()
    {
        base.Initialize();
        _runtime.DoorCommandAccepted += OnDoorCommand;
        SubscribeLocalEvent<NCMetaDoorPanelComponent, InteractHandEvent>(OnInteract);
        SubscribeLocalEvent<NCMetaDoorPanelComponent, GetVerbsEvent<AlternativeVerb>>(OnVerbs);
    }
    public override void Shutdown()
    {
        _runtime.DoorCommandAccepted -= OnDoorCommand;
        _pending.Clear(); _deadlines.Clear();
        base.Shutdown();
    }
    private void OnInteract(Entity<NCMetaDoorPanelComponent> ent, ref InteractHandEvent args)
    {
        if (!args.Handled && TryRequest(ent.AsNullable(), args.User)) args.Handled = true;
    }
    private void OnVerbs(Entity<NCMetaDoorPanelComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !_devices.CanManage(ent.Owner, args.User)) return;
        var user = args.User;
        foreach (var target in _network.GetReachablePorts(ent.Owner, 16))
        {
            if (!HasComp<DoorComponent>(target) || !_devices.TryGetEntry(target, out var door)) continue;
            var id = door.Id;
            args.Verbs.Add(new AlternativeVerb
            {
                Text = Loc.GetString("nc-meta-panel-bind", ("door", target)),
                Act = () => TryBindDoor(ent.AsNullable(), user, id),
            });
        }
    }
    public bool TryBindDoor(Entity<NCMetaDoorPanelComponent?> panel, EntityUid user, string doorId)
    {
        if (!Resolve(panel.Owner, ref panel.Comp) || !_devices.CanManage(panel.Owner, user) ||
            !_devices.TryGetEntry(panel.Owner, out var source) || !_devices.TryResolve(doorId, out var target) ||
            source.Owner != target.Owner || !HasComp<DoorComponent>(target.Entity) ||
            !_network.CanRoute(panel.Owner, target.Entity)) return false;
        panel.Comp.DoorId = doorId;
        _devices.TryInvalidate(panel.Owner);
        return true;
    }
    public bool TryRequest(Entity<NCMetaDoorPanelComponent?> panel, EntityUid user)
    {
        if (!Resolve(panel.Owner, ref panel.Comp) || !_devices.IsPowered(panel.Owner) ||
            !_interaction.InRangeUnobstructed(user, panel.Owner, range: 2) ||
            !_devices.TryResolve(panel.Comp.DoorId, out var target) ||
            !_runtime.CanReach(panel.Owner, target.Entity) || !TryComp<DoorComponent>(target.Entity, out var door) ||
            !_doors.HasAccess(target.Entity, user, door)) return false;
        var open = door.State is not (DoorState.Open or DoorState.Opening);
        var key = (target.Entity, user, open);
        if (_pending.ContainsKey(key) || _pending.Count >= 256 || _deadlines.Count >= 512) return false;
        if (!_devices.TryGetEntry(panel.Owner, out var source)) return false;
        var id = Guid.NewGuid().ToString("D");
        var authority = new PanelDeferredAuthority(this, panel.Owner, target.Entity, user, source.Id, target.Id,
            source.DeviceEpoch, target.DeviceEpoch, source.AuthorityEpoch, target.AuthorityEpoch,
            _timing.CurTime + TimeSpan.FromSeconds(2));
        var accepted = _runtime.TryPublishEvent(panel.Owner, "meta.doors.DoorPanel.requested", process =>
            process.CreateRecord(process.GetTypeId("meta.doors.DoorRequest"),
                [MetaValue.Bool(open), process.CreateString(id), _runtime.CreateStamp(process)]), user, deferredAuthority: authority);
        if (accepted == 0) return TryNative(target.Entity, user, open);
        var pending = new Pending(panel.Owner, target.Entity, user, open, target.DeviceEpoch, target.AuthorityEpoch,
            _timing.CurTime + TimeSpan.FromSeconds(2));
        _pending.Add(key, pending); _deadlines.Enqueue(key, pending.Deadline);
        return true;
    }
    private void OnDoorCommand(EntityUid door, EntityUid? user, bool open)
    {
        if (user != null) _pending.Remove((door, user.Value, open));
    }
    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        for (var work = 0; work < 32 && _deadlines.TryPeek(out _, out var deadline) && deadline <= _timing.CurTime; work++)
        {
            var key = _deadlines.Dequeue();
            if (!_pending.TryGetValue(key, out var pending) || pending.Deadline != deadline) continue;
            _pending.Remove(key);
            if (!Exists(pending.Panel) || !Exists(pending.Actor) || !_devices.IsPowered(pending.Panel) ||
                !_devices.TryGetEntry(pending.Door, out var current) || current.DeviceEpoch != pending.DeviceEpoch ||
                current.AuthorityEpoch != pending.AuthorityEpoch || !_runtime.CanReach(pending.Panel, pending.Door) ||
                !_interaction.InRangeUnobstructed(pending.Actor, pending.Panel, range: 2)) continue;
            TryNative(pending.Door, pending.Actor, pending.Open);
        }
    }
    private bool TryNative(EntityUid door, EntityUid user, bool open) => _doors.HasAccess(door, user) &&
        (open ? _doors.TryOpen(door, user: user, quiet: true) : _doors.TryClose(door, user: user));
}
