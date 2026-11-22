// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Atmos.EntitySystems;
using Content.Shared._NC.ZLevels.Core.Components;
using Content.Shared._NC.ZLevels.Core.EntitySystems;
using Content.Shared.Atmos.Components;
using Content.Shared.Light.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.ZLevels.Core;

/// <summary>
/// Owns the temporary map used by one grid during a timed Z-level move.
/// The caller retains its own obstacle checks and decides whether to arrive or return.
/// </summary>
public sealed partial class NCZGridMotionSystem : EntitySystem
{
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private NCSharedZLevelsSystem _levels = default!;
    [Dependency] private NCZGridConnectorSystem _connectors = default!;
    [Dependency] private AtmosphereSystem _atmos = default!;
    [Dependency] private ViewSubscriberSystem _views = default!;
    [Dependency] private SharedEyeSystem _eyes = default!;
    [Dependency] private IGameTiming _timing = default!;

    private static readonly EntProtoId ViewEye = "NCZLevelEye";
    private readonly Dictionary<EntityUid, Transit> _transits = [];
    private readonly List<EntityUid> _scratch = [];
    private readonly List<EntityUid> _children = [];
    private readonly Stack<EntityUid> _descendants = [];
    private readonly HashSet<EntityUid> _viewers = [];
    private readonly List<EntityUid> _obsoleteObservers = [];
    private TimeSpan _nextViewUpdate;

    private sealed class Transit(EntityUid map, NCZGridMotionComponent motion)
    {
        public readonly EntityUid Map = map;
        public readonly NCZGridMotionComponent Motion = motion;
        public readonly Dictionary<EntityUid, EntityUid> Eyes = [];
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCZGridMotionComponent, ComponentShutdown>(OnMotionShutdown);
    }

    public bool TryStart(EntityUid grid, int direction, TimeSpan duration)
    {
        if (!CanStart(grid, direction, duration, out var source, out var target))
            return false;

        var transitMap = _maps.CreateMap();
        var transitOwner = AddComp<NCZGridMotionMapComponent>(transitMap);
        transitOwner.Grid = grid;
        Dirty(transitMap, transitOwner);
        var sourceZ = Comp<NCZMapComponent>(source);
        var transitZ = EnsureComp<NCZMapComponent>(transitMap);
        transitZ.NetworkUid = sourceZ.NetworkUid;
        transitZ.Depth = sourceZ.Depth;
        transitZ.MapAbove = sourceZ.MapAbove;
        transitZ.MapBelow = sourceZ.MapBelow;
        Dirty(transitMap, transitZ);

        if (TryComp<MapLightComponent>(source, out var light))
        {
            var transitLight = EnsureComp<MapLightComponent>(transitMap);
            transitLight.AmbientLightColor = light.AmbientLightColor;
            Dirty(transitMap, transitLight);
        }
        if (TryComp<MapAtmosphereComponent>(source, out var air))
            _atmos.SetMapAtmosphere(transitMap, air.Space, air.Mixture);

        var motion = AddComp<NCZGridMotionComponent>(grid);
        motion.SourceMap = source;
        motion.TargetMap = target;
        motion.Direction = direction;
        motion.StartedAt = _timing.CurTime;
        motion.ArrivesAt = _timing.CurTime + duration;
        Dirty(grid, motion);
        var transit = new Transit(transitMap, motion);
        _transits.Add(grid, transit);

        MoveRoot(grid, transitMap);
        _connectors.MarkDirty();
        UpdateViews(transit);
        return true;
    }

    private bool CanStart(EntityUid grid, int direction, TimeSpan duration,
        out EntityUid source, out EntityUid target)
    {
        source = default;
        target = default;
        if (direction is not (1 or -1) || duration <= TimeSpan.Zero ||
            TerminatingOrDeleted(grid) || !HasComp<MapGridComponent>(grid) || HasComp<MapComponent>(grid) ||
            HasComp<NCZGridMotionComponent>(grid) || Transform(grid).MapUid is not { } map ||
            !TryComp<NCZMapComponent>(map, out var zMap) ||
            !_levels.TryMapOffset((map, zMap), direction, out var destination) ||
            !HasComp<MapComponent>(destination))
            return false;

        source = map;
        target = destination;
        return true;
    }

    public bool TryComplete(EntityUid grid)
    {
        if (!_transits.TryGetValue(grid, out var transit))
            return false;
        if (!TryComp<NCZMapComponent>(transit.Motion.TargetMap, out var target) ||
            !HasComp<MapComponent>(transit.Motion.TargetMap) || TerminatingOrDeleted(transit.Motion.TargetMap))
        {
            Cancel(grid);
            return false;
        }

        Finish(grid, transit, transit.Motion.TargetMap, target.Depth, transit.Motion.Direction);
        return true;
    }

    public void Cancel(EntityUid grid)
    {
        if (!_transits.TryGetValue(grid, out var transit))
            return;
        var destination = transit.Motion.SourceMap;
        if (!HasComp<MapComponent>(destination) || TerminatingOrDeleted(destination))
            destination = transit.Motion.TargetMap;
        var depth = TryComp<NCZMapComponent>(destination, out var zMap) ? zMap.Depth : 0;
        Finish(grid, transit, destination, depth, 0);
    }

    private void Finish(EntityUid grid, Transit transit, EntityUid destination, int depth, int offset)
    {
        // Remove ownership first, so removing the component cannot recursively cancel this trip.
        _transits.Remove(grid);
        var hasDestination = HasComp<MapComponent>(destination) && !TerminatingOrDeleted(destination);
        if (hasDestination && !TerminatingOrDeleted(grid))
        {
            MoveRoot(grid, destination);
            NotifyRiders(grid, offset, depth);
        }

        foreach (var eye in transit.Eyes.Values)
            QueueDel(eye);

        // An object that left the platform must not be deleted with its temporary map.
        _children.Clear();
        if (TryComp(transit.Map, out TransformComponent? mapTransform))
        {
            var children = mapTransform.ChildEnumerator;
            while (children.MoveNext(out var child))
                _children.Add(child);
        }
        if (hasDestination)
        {
            foreach (var child in _children)
            {
                if (!TerminatingOrDeleted(child) && !EntityManager.IsQueuedForDeletion(child))
                {
                    MoveRoot(child, destination);
                    NotifyRiders(child, offset, depth);
                }
            }
        }

        if (!TerminatingOrDeleted(grid) && transit.Motion.LifeStage < ComponentLifeStage.Stopping)
            RemComp<NCZGridMotionComponent>(grid);
        _connectors.MarkDirty();
        if (hasDestination)
            QueueDel(transit.Map);
    }

    private void MoveRoot(EntityUid uid, EntityUid map)
    {
        var position = _transform.GetWorldPosition(uid);
        var rotation = _transform.GetWorldRotation(uid);
        _transform.SetMapCoordinates(uid, new MapCoordinates(position, Comp<MapComponent>(map).MapId));
        _transform.SetWorldRotation(uid, rotation);
    }

    private void NotifyRiders(EntityUid grid, int offset, int depth)
    {
        _descendants.Clear();
        _descendants.Push(grid);
        while (_descendants.TryPop(out var uid))
        {
            if (TerminatingOrDeleted(uid))
                continue;
            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                _descendants.Push(child);
            if (!HasComp<NCZPhysicsComponent>(uid))
                continue;
            var ev = new NCZLevelMapMoveEvent(offset, depth);
            RaiseLocalEvent(uid, ref ev);
        }
    }

    private void OnMotionShutdown(Entity<NCZGridMotionComponent> ent, ref ComponentShutdown args)
    {
        Cancel(ent);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_timing.CurTime < _nextViewUpdate)
            return;
        _nextViewUpdate = _timing.CurTime + TimeSpan.FromSeconds(0.1);

        _scratch.Clear();
        _scratch.AddRange(_transits.Keys);
        foreach (var grid in _scratch)
        {
            if (!_transits.TryGetValue(grid, out var transit))
                continue;
            if (TerminatingOrDeleted(grid))
                Cancel(grid);
            else
                UpdateViews(transit);
        }
    }

    private void UpdateViews(Transit transit)
    {
        if (!TryComp<NCZMapComponent>(transit.Motion.SourceMap, out var source))
            return;

        _viewers.Clear();
        var actors = EntityQueryEnumerator<ActorComponent, TransformComponent>();
        while (actors.MoveNext(out var player, out var actor, out var transform))
        {
            if (transform.MapUid is not { } playerMap ||
                !TryComp<NCZMapComponent>(playerMap, out var playerZ) ||
                playerZ.NetworkUid != source.NetworkUid)
                continue;

            var difference = source.Depth - playerZ.Depth;
            if (difference < -NCSharedZLevelsSystem.MaxZLevelsBelowRendering - 1 || difference > 2)
                continue;

            // Riders already see the transit map; they need the real floor outside their cabin.
            // Other observers need a PVS view onto the cabin's temporary map.
            _viewers.Add(player);
            var viewMap = playerMap == transit.Map ? transit.Motion.SourceMap : transit.Map;
            if (!transit.Eyes.TryGetValue(player, out var eye) || TerminatingOrDeleted(eye) ||
                Transform(eye).MapUid != viewMap)
            {
                if (eye.IsValid())
                    QueueDel(eye);
                eye = Spawn(ViewEye, new EntityCoordinates(viewMap, _transform.GetWorldPosition(player)));
                transit.Eyes[player] = eye;
                _views.AddViewSubscriber(eye, actor.PlayerSession);
            }
            _transform.SetWorldPosition(eye, _transform.GetWorldPosition(player));
            var viewEye = EnsureComp<EyeComponent>(eye);
            var scale = TryComp<EyeComponent>(player, out var playerEye) ? playerEye.PvsScale : 1f;
            _eyes.SetPvsScale((eye, viewEye), scale);
        }

        _obsoleteObservers.Clear();
        foreach (var (player, eye) in transit.Eyes)
        {
            if (_viewers.Contains(player))
                continue;
            QueueDel(eye);
            _obsoleteObservers.Add(player);
        }
        foreach (var player in _obsoleteObservers)
            transit.Eyes.Remove(player);
    }
}
