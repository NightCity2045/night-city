// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Runtime;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.Meta.Devices;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Robust.Shared.Timing;

namespace Content.Server._NC.Meta.Devices;

/// <summary>
/// Tells programs who stands in a doorway. It asks the door itself which bodies overlap it, the same question the door
/// asks before it closes, so "in the doorway" means exactly "would be crushed". Only doors a program is bound to are sampled.
/// </summary>
public sealed class NCMetaDoorOccupancySystem : EntitySystem
{
    private const float PeriodSeconds = 0.2f;

    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private SharedDoorSystem _doors = default!;
    [Dependency] private IGameTiming _timing = default!;

    private readonly Dictionary<EntityUid, int> _last = [];
    private TimeSpan _next;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_timing.CurTime < _next) return;
        _next = _timing.CurTime + TimeSpan.FromSeconds(PeriodSeconds);
        var seen = new HashSet<EntityUid>();
        var query = EntityQueryEnumerator<NCMetaDoorAccessComponent, DoorComponent>();
        while (query.MoveNext(out var uid, out _, out var door))
        {
            if (!_runtime.HasDependants(uid)) continue;
            seen.Add(uid);
            var count = _doors.GetColliding(uid, null, null, door.CheckFixtureCollision, door.AllowMachineLayer).Count();
            if (_last.TryGetValue(uid, out var previous) && previous == count) continue;
            _last[uid] = count;
            _runtime.TryPublishEvent(uid, "meta.doors.Door.occupancyChanged", process => process.CreateRecord(
                process.GetTypeId("meta.doors.DoorOccupancy"),
                [MetaValue.Bool(count > 0), MetaValue.Int(count), _runtime.CreateStamp(process)]));
        }

        foreach (var stale in _last.Keys.Where(key => !seen.Contains(key)).ToArray())
            _last.Remove(stale);
    }
}
