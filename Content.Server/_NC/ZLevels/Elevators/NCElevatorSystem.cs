// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Server._NC.ZLevels.Core;
using Content.Shared._NC.ZLevels.Core.Components;
using Content.Shared._NC.ZLevels.Core.EntitySystems;
using Content.Shared._NC.ZLevels.Elevators;
using Content.Shared.ActionBlocker;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.ZLevels.Elevators;

/// <summary>
/// Moves one physical 3x3 grid between adjacent maps after a timed trip.
/// Panels resolve their own grid; no proximity-based controller selection or tile ID links are used.
/// </summary>
public sealed partial class NCElevatorSystem : EntitySystem
{
    private static readonly Vector2 CabinCenter = new(0.5f, 0.5f);
    private static readonly Box2 CabinBounds = new(-1f, -1f, 2f, 2f);
    private const float RailThickness = 0.08f;
    // Ignore the collision skin of walls touching the outside of the shaft.
    private const float BoundaryInset = PhysicsConstants.PolygonRadius * 3f;

    [Dependency] private NCSharedZLevelsSystem _zLevels = default!;
    [Dependency] private NCZGridMotionSystem _gridMotion = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private MetaDataSystem _metadata = default!;
    [Dependency] private ITileDefinitionManager _tiles = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IGameTiming _timing = default!;

    private readonly HashSet<EntityUid> _pendingSpawners = [];
    private readonly HashSet<EntityUid> _moving = [];
    private readonly HashSet<EntityUid> _panels = [];
    private readonly List<EntityUid> _scratch = [];
    private readonly Stack<EntityUid> _descendants = [];
    private List<Entity<MapGridComponent>> _gridScratch = [];
    private TimeSpan _nextSpawnCheck;

    public override void Initialize()
    {
        base.Initialize();
        UpdatesBefore.Add(typeof(NCZLevelsSystem));
        SubscribeLocalEvent<NCElevatorSpawnerComponent, MapInitEvent>(OnSpawnerMapInit);
        SubscribeLocalEvent<NCElevatorSpawnerComponent, ComponentShutdown>(OnSpawnerShutdown);
        SubscribeLocalEvent<NCElevatorComponent, ComponentShutdown>(OnCabinShutdown);
        SubscribeLocalEvent<NCElevatorPanelComponent, ComponentStartup>(OnPanelStartup);
        SubscribeLocalEvent<NCElevatorPanelComponent, ComponentShutdown>(OnPanelShutdown);
        SubscribeLocalEvent<NCElevatorPanelComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<NCElevatorPanelComponent, NCElevatorMoveMessage>(OnMoveRequested);
        SubscribeLocalEvent<NCElevatorCallButtonComponent, ActivateInWorldEvent>(OnCallRequested);
    }

    private void OnSpawnerMapInit(Entity<NCElevatorSpawnerComponent> ent, ref MapInitEvent args)
    {
        _pendingSpawners.Add(ent);
        _nextSpawnCheck = TimeSpan.Zero;
    }

    private void OnSpawnerShutdown(Entity<NCElevatorSpawnerComponent> ent, ref ComponentShutdown args)
    {
        _pendingSpawners.Remove(ent);
        if (ent.Comp.Cabin is not { } grid || TerminatingOrDeleted(grid))
            return;

        if (TryComp<NCElevatorComponent>(grid, out var cabin))
            StopTrip((grid, cabin));
        // The city snapshot restores ordinary roots, so detach people and cargo before retiring a derived cabin.
        var children = Transform(grid).ChildEnumerator;
        var passengers = new List<EntityUid>();
        while (children.MoveNext(out var child))
        {
            if (!HasComp<NCElevatorCabinPartComponent>(child))
                passengers.Add(child);
        }
        foreach (var passenger in passengers)
        {
            var rotation = _transform.GetWorldRotation(passenger);
            _transform.SetMapCoordinates(passenger, _transform.GetMapCoordinates(passenger));
            _transform.SetWorldRotation(passenger, rotation);
        }
        QueueDel(grid);
    }

    private void OnPanelStartup(Entity<NCElevatorPanelComponent> ent, ref ComponentStartup args)
    {
        _panels.Add(ent);
    }

    private void OnPanelShutdown(Entity<NCElevatorPanelComponent> ent, ref ComponentShutdown args)
    {
        _panels.Remove(ent);
        if (TryGetCabin(ent, out var cabin) && cabin.Comp.TargetDepth != null)
            StopTrip(cabin, "nc-elevator-trip-cancelled");
    }

    private void OnCabinShutdown(Entity<NCElevatorComponent> ent, ref ComponentShutdown args)
    {
        StopTrip(ent);
    }

    private void OnUiOpened(Entity<NCElevatorPanelComponent> ent, ref BoundUIOpenedEvent args)
    {
        UpdatePanel(ent);
    }

    private void OnMoveRequested(Entity<NCElevatorPanelComponent> ent, ref NCElevatorMoveMessage args)
    {
        TryMove((ent.Owner, ent.Comp), args.Actor, args.Offset);
    }

    private void OnCallRequested(Entity<NCElevatorCallButtonComponent> ent, ref ActivateInWorldEvent args)
    {
        if (args.Handled || !args.Complex)
            return;

        args.Handled = true;
        TryCall((ent.Owner, ent.Comp), args.User);
    }

    /// <summary>Calls the explicitly linked cabin to the button's own Z-level.</summary>
    public bool TryCall(Entity<NCElevatorCallButtonComponent?> button, EntityUid user)
    {
        if (!Resolve(button, ref button.Comp, false) || !CanUseCallButton(button, user))
            return false;

        if (!TryGetCalledCabin((button.Owner, button.Comp), out var cabin, out var depth))
        {
            _popup.PopupEntity(Loc.GetString("nc-elevator-call-unlinked"), button, user);
            return false;
        }
        if (cabin.Comp.TargetDepth != null)
        {
            _popup.PopupEntity(Loc.GetString("nc-elevator-busy"), button, user);
            return false;
        }
        if (!CanCall(cabin, depth, out var offset, out var failure))
        {
            _popup.PopupEntity(Loc.GetString(failure!), button, user);
            return false;
        }
        if (offset == 0)
        {
            _popup.PopupEntity(Loc.GetString("nc-elevator-call-here"), button, user);
            return true;
        }

        cabin.Comp.CalledDepth = depth;
        if (!TryStartTrip(cabin, offset, out failure))
        {
            StopTrip(cabin, failure);
            _popup.PopupEntity(Loc.GetString(failure!), button, user);
            return false;
        }

        _popup.PopupEntity(Loc.GetString("nc-elevator-call-accepted"), button, user);
        return true;
    }

    private bool CanUseCallButton(EntityUid button, EntityUid user)
    {
        return !TerminatingOrDeleted(button) && !TerminatingOrDeleted(user) &&
               Transform(button).Anchored &&
               _blocker.CanConsciouslyPerformAction(user) &&
               _interaction.InRangeUnobstructed(user, button);
    }

    private bool TryGetCalledCabin(Entity<NCElevatorCallButtonComponent> button,
        out Entity<NCElevatorComponent> cabin, out int depth)
    {
        cabin = default;
        depth = default;
        if (string.IsNullOrWhiteSpace(button.Comp.ElevatorId) ||
            !TryComp<NCZMapComponent>(Transform(button).MapUid, out var landing))
            return false;

        Entity<NCElevatorSpawnerComponent>? controller = null;
        var markers = EntityQueryEnumerator<NCElevatorSpawnerComponent, TransformComponent>();
        while (markers.MoveNext(out var uid, out var marker, out var transform))
        {
            if (marker.ElevatorId != button.Comp.ElevatorId || TerminatingOrDeleted(uid) ||
                EntityManager.IsQueuedForDeletion(uid) ||
                !TryComp<NCZMapComponent>(transform.MapUid, out var map) || map.NetworkUid != landing.NetworkUid)
                continue;

            // Duplicate IDs must not silently select a different cabin.
            if (controller != null)
                return false;
            controller = (uid, marker);
        }
        if (controller is not { } selected || selected.Comp.Cabin is not { } grid ||
            TerminatingOrDeleted(grid) || EntityManager.IsQueuedForDeletion(grid) ||
            !TryComp<NCElevatorComponent>(grid, out var elevator))
            return false;

        cabin = (grid, elevator);
        depth = landing.Depth;
        return true;
    }

    private bool CanCall(Entity<NCElevatorComponent> cabin, int depth, out int offset, out string? failure)
    {
        offset = 0;
        failure = "nc-elevator-call-no-route";
        if (!TryComp<MapGridComponent>(cabin, out var grid) ||
            Transform(cabin).MapUid is not { } map ||
            !TryComp<NCZMapComponent>(map, out var currentZ) ||
            cabin.Comp.Settings.Stops.Count > 0 && !cabin.Comp.Settings.Stops.Contains(depth))
            return false;

        offset = depth.CompareTo(currentZ.Depth);
        Entity<NCZMapComponent> current = (map, currentZ);
        while (current.Comp.Depth != depth)
        {
            if (!_zLevels.TryMapOffset((current.Owner, current.Comp), offset, out var next) ||
                next.Comp.Depth != current.Comp.Depth + offset || next.Comp.NetworkUid != currentZ.NetworkUid)
                return false;
            if (!HasClearShaft((cabin.Owner, grid), next))
            {
                failure = "nc-elevator-shaft-blocked";
                return false;
            }
            current = next;
        }
        failure = null;
        return true;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Z-network components are assigned after mapper entities receive MapInit.
        // Only pending markers are polled, at most once a second.
        if (_pendingSpawners.Count > 0 && _timing.CurTime >= _nextSpawnCheck)
        {
            _nextSpawnCheck = _timing.CurTime + TimeSpan.FromSeconds(1);
            _scratch.Clear();
            _scratch.AddRange(_pendingSpawners);
            foreach (var uid in _scratch)
            {
                if (!TryComp<NCElevatorSpawnerComponent>(uid, out var spawner) ||
                    TerminatingOrDeleted(uid))
                {
                    _pendingSpawners.Remove(uid);
                    continue;
                }

                if (Transform(uid).MapUid is not { } map || !HasComp<NCZMapComponent>(map))
                    continue;

                _pendingSpawners.Remove(uid);
                if (!TryCreateCabin((uid, spawner)))
                    Log.Error($"Unable to create elevator at {ToPrettyString(uid)}. Check its settings and prepare an empty 3x3 shaft.");
            }
        }

        // As in AV movement, change a complete transform tree outside physics callbacks.
        _scratch.Clear();
        _scratch.AddRange(_moving);
        foreach (var uid in _scratch)
        {
            if (!TryComp<NCElevatorComponent>(uid, out var elevator) || TerminatingOrDeleted(uid))
            {
                _moving.Remove(uid);
                continue;
            }

            if (_metadata.EntityPaused(uid))
                continue;
            if (elevator.Barrier is not { } barrier || TerminatingOrDeleted(barrier) || EntityManager.IsQueuedForDeletion(barrier))
                StopTrip((uid, elevator), "nc-elevator-trip-cancelled");
            else if (_timing.CurTime >= elevator.ArrivesAt)
                CompleteTrip((uid, elevator));
        }
    }

    private bool TryCreateCabin(Entity<NCElevatorSpawnerComponent> marker)
    {
        if (marker.Comp.Cabin is { } existing && !TerminatingOrDeleted(existing))
            return true;
        var settings = marker.Comp.Settings;
        var markerXform = Transform(marker);
        if (markerXform.MapUid is not { } map ||
            !TryComp<NCZMapComponent>(map, out var zMap) ||
            !ValidSettings(settings))
        {
            return false;
        }

        // Mapping saves may contain both the controller and its generated grid.
        // Rebind by a durable ID instead of creating a second cabin on top of it.
        if (marker.Comp.CabinId != Guid.Empty)
        {
            var cabins = EntityQueryEnumerator<NCElevatorComponent, TransformComponent>();
            while (cabins.MoveNext(out var uid, out var savedCabin, out var savedXform))
            {
                if (savedCabin.CabinId != marker.Comp.CabinId || TerminatingOrDeleted(uid) ||
                    EntityManager.IsQueuedForDeletion(uid) ||
                    !TryComp<NCZMapComponent>(savedXform.MapUid, out var savedZ) || savedZ.NetworkUid != zMap.NetworkUid)
                {
                    continue;
                }
                if (savedCabin.Controller is { } owner && owner != marker.Owner &&
                    !TerminatingOrDeleted(owner) && !EntityManager.IsQueuedForDeletion(owner))
                {
                    return false;
                }
                savedCabin.Controller = marker;
                savedCabin.Settings = settings;
                marker.Comp.Cabin = uid;
                marker.Comp.CurrentDepth = savedZ.Depth;
                return true;
            }
        }

        var depth = marker.Comp.CurrentDepth ?? zMap.Depth;
        if (settings.Stops.Count > 0 && !settings.Stops.Contains(depth) ||
            !_zLevels.TryMapOffset((map, zMap), depth - zMap.Depth, out var startMap))
        {
            return false;
        }
        map = startMap;
        var rotation = _transform.GetWorldRotation(marker);
        var center = _transform.GetWorldPosition(marker);
        var grid = _maps.CreateGridEntity(map);
        _transform.SetWorldPositionRotation(grid, center - rotation.RotateVec(CabinCenter), rotation);
        grid.Comp.CanSplit = false;

        if (!HasClearShaft(grid, map, checkBodies: false))
        {
            QueueDel(grid);
            return false;
        }

        if (marker.Comp.CabinId == Guid.Empty)
            marker.Comp.CabinId = Guid.NewGuid();
        var elevator = AddComp<NCElevatorComponent>(grid);
        elevator.Settings = settings;
        elevator.CabinId = marker.Comp.CabinId;
        elevator.Controller = marker;
        marker.Comp.Cabin = grid;
        marker.Comp.CurrentDepth = depth;
        _metadata.SetEntityName(grid, Loc.GetString("nc-elevator-cabin-name"));
        var tile = new Tile(_tiles[settings.FloorTile.Id].TileId);
        var tiles = new List<(Vector2i, Tile)>(9);
        for (var x = -1; x <= 1; x++)
        for (var y = -1; y <= 1; y++)
            tiles.Add((new Vector2i(x, y), tile));
        _maps.SetTiles(grid, tiles);
        _physics.SetBodyType(grid, BodyType.Static);
        if (_maps.IsInitialized(map))
            EntityManager.RunMapInit(grid, MetaData(grid));
        Spawn(settings.PanelPrototype, new EntityCoordinates(grid, CabinCenter));
        return true;
    }

    private bool ValidSettings(NCElevatorSettings settings)
    {
        return settings.TravelTime > TimeSpan.Zero &&
               _prototypes.HasIndex(settings.FloorTile) &&
               _prototypes.HasIndex(settings.PanelPrototype) &&
               _prototypes.HasIndex(settings.BarrierPrototype);
    }

    /// <summary>Authoritative action for a player using a panel mounted in this cabin.</summary>
    public bool TryMove(Entity<NCElevatorPanelComponent?> panel, EntityUid user, int offset)
    {
        if (!Resolve(panel, ref panel.Comp, false))
            return false;

        if (!TryGetCabin(panel, out var cabin))
        {
            UpdatePanel(panel, "nc-elevator-disconnected");
            return false;
        }

        if (!CanUsePanel(panel, cabin, user))
        {
            UpdatePanel(panel, "nc-elevator-enter-cabin");
            return false;
        }

        if (!TryStartTrip(cabin, offset, out var failure))
        {
            UpdatePanel(panel, failure);
            return false;
        }
        return true;
    }

    private bool TryStartTrip(Entity<NCElevatorComponent> cabin, int offset, out string? failure)
    {
        if (!CanMove((cabin.Owner, cabin.Comp), offset, out var targetMap, out var targetDepth, out failure))
            return false;

        if (cabin.Comp.Barrier == null && !HasClearEdges(cabin))
        {
            failure = "nc-elevator-edge-blocked";
            return false;
        }

        // The same trip path carries passengers for cabin controls and landing calls.
        AttachPassengers(cabin);
        if (cabin.Comp.Barrier == null)
            cabin.Comp.Barrier = Spawn(cabin.Comp.Settings.BarrierPrototype, new EntityCoordinates(cabin, CabinCenter));
        if (cabin.Comp.Motor == null)
            cabin.Comp.Motor = _audio.PlayPvs(cabin.Comp.Settings.MotorSound, cabin.Owner,
                cabin.Comp.Settings.MotorSound.Params.WithLoop(true))?.Entity;
        if (!_gridMotion.TryStart(cabin, offset, cabin.Comp.Settings.TravelTime))
        {
            failure = "nc-elevator-trip-cancelled";
            StopTrip(cabin, failure);
            return false;
        }
        cabin.Comp.TargetMap = targetMap;
        cabin.Comp.TargetDepth = targetDepth;
        cabin.Comp.ArrivesAt = _timing.CurTime + cabin.Comp.Settings.TravelTime;
        _moving.Add(cabin);
        UpdateCabinPanels(cabin);
        failure = null;
        return true;
    }

    private bool CanUsePanel(EntityUid panel, Entity<NCElevatorComponent> cabin, EntityUid user)
    {
        return !TerminatingOrDeleted(user) &&
               Transform(panel).Anchored &&
               Transform(user).GridUid == cabin.Owner &&
               _ui.IsUiOpen(panel, NCElevatorUiKey.Key, user) &&
               _blocker.CanConsciouslyPerformAction(user) &&
               _interaction.InRangeUnobstructed(user, panel);
    }

    public bool CanMove(
        Entity<NCElevatorComponent?> cabin,
        int offset,
        out EntityUid targetMap,
        out int targetDepth,
        out string? failure)
    {
        targetMap = default;
        targetDepth = default;
        failure = "nc-elevator-invalid-setup";
        if (!Resolve(cabin, ref cabin.Comp, false) ||
            !TryComp<MapGridComponent>(cabin, out var grid) ||
            !ValidSettings(cabin.Comp.Settings))
        {
            return false;
        }

        if (cabin.Comp.TargetDepth != null)
        {
            failure = "nc-elevator-busy";
            return false;
        }

        if (offset is not (1 or -1) ||
            Transform(cabin).MapUid is not { } map ||
            !TryComp<NCZMapComponent>(map, out var zMap) ||
            !_zLevels.TryMapOffset((map, zMap), offset, out var target) ||
            target.Comp.Depth != zMap.Depth + offset)
        {
            failure = "nc-elevator-no-stop";
            return false;
        }

        if (cabin.Comp.CalledDepth == null && cabin.Comp.Settings.Stops.Count > 0 &&
            (!cabin.Comp.Settings.Stops.Contains(zMap.Depth) || !cabin.Comp.Settings.Stops.Contains(target.Comp.Depth)))
        {
            failure = "nc-elevator-no-stop";
            return false;
        }

        // An edited or damaged platform must not transport an unexpected footprint.
        if (!HasCompleteFloor((cabin, grid)))
            return false;

        if (!HasClearShaft((cabin, grid), target))
        {
            failure = "nc-elevator-shaft-blocked";
            return false;
        }

        targetMap = target;
        targetDepth = target.Comp.Depth;
        failure = null;
        return true;
    }

    private bool HasCompleteFloor(Entity<MapGridComponent> cabin)
    {
        var count = 0;
        foreach (var tile in _maps.GetAllTiles(cabin, cabin.Comp))
        {
            if (tile.GridIndices.X is < -1 or > 1 || tile.GridIndices.Y is < -1 or > 1)
                return false;
            count++;
        }
        return count == 9;
    }

    private bool TryGetCabin(EntityUid panel, out Entity<NCElevatorComponent> cabin)
    {
        cabin = default;
        if (TerminatingOrDeleted(panel) ||
            Transform(panel).GridUid is not { } grid ||
            !TryComp<NCElevatorComponent>(grid, out var elevator) ||
            TerminatingOrDeleted(grid))
        {
            return false;
        }

        cabin = (grid, elevator);
        return true;
    }

    private void CompleteTrip(Entity<NCElevatorComponent> cabin)
    {
        if (Transform(cabin).MapUid is not { } map ||
            !TryComp<NCZMapComponent>(map, out var zMap) ||
            cabin.Comp.TargetDepth is not { } targetDepth ||
            cabin.Comp.TargetMap is not { } targetMap ||
            !TryComp<NCZMapComponent>(targetMap, out var targetZ) ||
            targetZ.Depth != targetDepth || targetZ.NetworkUid != zMap.NetworkUid ||
            !TryComp<MapGridComponent>(cabin, out var grid) ||
            !HasCompleteFloor((cabin, grid)) ||
            !HasClearShaft((cabin, grid), targetMap))
        {
            StopTrip(cabin, "nc-elevator-trip-cancelled");
            return;
        }

        // Include bodies that were dropped or otherwise appeared inside during the delay.
        AttachPassengers(cabin);
        var offset = targetDepth - zMap.Depth;
        if (offset is not (1 or -1) || !_gridMotion.TryComplete(cabin))
        {
            StopTrip(cabin, "nc-elevator-trip-cancelled");
            return;
        }

        SynchronizePassengerHeights(cabin, offset, targetDepth);
        // An interrupted call restores at the last served stop, never at a forbidden intermediate level.
        if (TryComp<NCElevatorSpawnerComponent>(cabin.Comp.Controller, out var controller) &&
            (cabin.Comp.Settings.Stops.Count == 0 || cabin.Comp.Settings.Stops.Contains(targetDepth)))
            controller.CurrentDepth = targetDepth;

        if (cabin.Comp.CalledDepth is { } calledDepth && calledDepth != targetDepth)
        {
            cabin.Comp.TargetMap = null;
            cabin.Comp.TargetDepth = null;
            if (TryStartTrip(cabin, calledDepth.CompareTo(targetDepth), out var failure))
                return;
            StopTrip(cabin, failure);
            return;
        }
        StopTrip(cabin);
        _audio.PlayPvs(cabin.Comp.Settings.ArrivalSound, cabin.Owner);
    }

    private void StopTrip(Entity<NCElevatorComponent> cabin, string? feedback = null)
    {
        _gridMotion.Cancel(cabin);
        _moving.Remove(cabin);
        if (cabin.Comp.Motor is { } motor)
            _audio.Stop(motor);
        if (cabin.Comp.Barrier is { } barrier && !TerminatingOrDeleted(barrier))
            QueueDel(barrier);
        cabin.Comp.Motor = null;
        cabin.Comp.Barrier = null;
        cabin.Comp.TargetMap = null;
        cabin.Comp.TargetDepth = null;
        cabin.Comp.CalledDepth = null;
        if (!TerminatingOrDeleted(cabin))
            UpdateCabinPanels(cabin, feedback);
    }

    private void AttachPassengers(EntityUid cabin)
    {
        var xform = Transform(cabin);
        var bounds = WorldBounds(cabin, CabinBounds);
        foreach (var uid in _lookup.GetEntitiesIntersecting(xform.MapID, bounds))
        {
            if (uid == cabin || !TryComp<PhysicsComponent>(uid, out _) ||
                HasComp<MapGridComponent>(uid) || HasComp<MapComponent>(uid) ||
                TerminatingOrDeleted(uid))
            {
                continue;
            }

            var bodyXform = Transform(uid);
            if (bodyXform.Anchored || bodyXform.MapUid != xform.MapUid ||
                bodyXform.ParentUid != bodyXform.GridUid && bodyXform.ParentUid != bodyXform.MapUid)
            {
                continue;
            }

            var position = Vector2.Transform(_transform.GetWorldPosition(uid), _transform.GetInvWorldMatrix(cabin));
            if (!CabinBounds.Contains(position))
                continue;

            _transform.SetParent(uid, cabin);
            if (TryComp<NCZPhysicsComponent>(uid, out var zPhysics))
            {
                _zLevels.SetZPosition((uid, zPhysics), 0);
                _zLevels.SetZVelocity((uid, zPhysics), 0);
            }
        }
    }

    private void SynchronizePassengerHeights(EntityUid cabin, int offset, int depth)
    {
        _descendants.Clear();
        var children = Transform(cabin).ChildEnumerator;
        while (children.MoveNext(out var child))
            _descendants.Push(child);
        while (_descendants.TryPop(out var uid))
        {
            if (TerminatingOrDeleted(uid))
                continue;
            children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                _descendants.Push(child);
            if (!TryComp<NCZPhysicsComponent>(uid, out var zPhysics))
                continue;

            _zLevels.SetZPosition((uid, zPhysics), 0);
            _zLevels.SetZVelocity((uid, zPhysics), 0);
            var ev = new NCZLevelMapMoveEvent(offset, depth);
            RaiseLocalEvent(uid, ref ev);
        }
    }

    private bool HasClearShaft(Entity<MapGridComponent> cabin, EntityUid map, bool checkBodies = true)
    {
        var bounds = WorldBounds(cabin, CabinBounds);
        _gridScratch.Clear();
        _maps.FindGridsIntersecting(Transform(map).MapID, bounds, ref _gridScratch, includeMap: true);
        var matrix = _transform.GetWorldMatrix(cabin);
        foreach (var candidate in _gridScratch)
        {
            if (candidate.Owner == cabin.Owner || TerminatingOrDeleted(candidate) || EntityManager.IsQueuedForDeletion(candidate))
                continue;
            for (var x = -1; x <= 1; x++)
            for (var y = -1; y <= 1; y++)
            {
                var world = Vector2.Transform(new Vector2(x + 0.5f, y + 0.5f), matrix);
                if (_maps.TryGetTileRef(candidate, candidate.Comp, world, out var tile) && !tile.Tile.IsEmpty)
                    return false;
            }
        }

        var bodyBounds = WorldBounds(cabin, CabinBounds.Enlarged(-BoundaryInset));
        foreach (var uid in _lookup.GetEntitiesIntersecting(Transform(map).MapID, bodyBounds,
                     LookupFlags.Dynamic | LookupFlags.Static))
        {
            if (TerminatingOrDeleted(uid) || EntityManager.IsQueuedForDeletion(uid) ||
                uid == cabin.Owner || Transform(uid).GridUid == cabin.Owner ||
                HasComp<MapGridComponent>(uid) ||
                !TryComp<PhysicsComponent>(uid, out var body) || !body.CanCollide || !body.Hard)
            {
                continue;
            }

            // Existing movers may board while a marker creates its initial floor.
            if (checkBodies || Transform(uid).Anchored || body.BodyType == BodyType.Static)
                return false;
        }

        return true;
    }

    private bool HasClearEdges(EntityUid cabin)
    {
        var mapId = Transform(cabin).MapID;
        var strips = new[]
        {
            new Box2(-1 + BoundaryInset, -1 + BoundaryInset, 2 - BoundaryInset, -1 + RailThickness),
            new Box2(-1 + BoundaryInset, 2 - RailThickness, 2 - BoundaryInset, 2 - BoundaryInset),
            new Box2(-1 + BoundaryInset, -1 + BoundaryInset, -1 + RailThickness, 2 - BoundaryInset),
            new Box2(2 - RailThickness, -1 + BoundaryInset, 2 - BoundaryInset, 2 - BoundaryInset),
        };
        foreach (var strip in strips)
        foreach (var uid in _lookup.GetEntitiesIntersecting(mapId, WorldBounds(cabin, strip),
                     LookupFlags.Dynamic | LookupFlags.Static))
        {
            if (TerminatingOrDeleted(uid) || EntityManager.IsQueuedForDeletion(uid) ||
                HasComp<MapComponent>(uid) || HasComp<NCElevatorCabinPartComponent>(uid) ||
                Transform(uid).GridUid == cabin && Transform(uid).Anchored)
                continue;

            if (uid != cabin && !HasComp<MapGridComponent>(uid) &&
                TryComp<PhysicsComponent>(uid, out var body) && body.CanCollide && body.Hard)
            {
                return false;
            }
        }

        return true;
    }

    private Box2 WorldBounds(EntityUid cabin, Box2 bounds)
    {
        var matrix = _transform.GetWorldMatrix(cabin);
        var a = Vector2.Transform(bounds.BottomLeft, matrix);
        var b = Vector2.Transform(bounds.BottomRight, matrix);
        var c = Vector2.Transform(bounds.TopLeft, matrix);
        var d = Vector2.Transform(bounds.TopRight, matrix);
        return new Box2(Vector2.Min(Vector2.Min(a, b), Vector2.Min(c, d)),
            Vector2.Max(Vector2.Max(a, b), Vector2.Max(c, d)));
    }

    private void UpdateCabinPanels(EntityUid cabin, string? feedback = null)
    {
        foreach (var panel in _panels)
        {
            if (!TerminatingOrDeleted(panel) && Transform(panel).GridUid == cabin)
                UpdatePanel(panel, feedback);
        }
    }

    private void UpdatePanel(EntityUid panel, string? feedback = null)
    {
        if (!TryGetCabin(panel, out var cabin) ||
            Transform(cabin).MapUid is not { } map ||
            !TryComp<NCZMapComponent>(map, out var zMap))
        {
            _ui.SetUiState(panel, NCElevatorUiKey.Key,
                new NCElevatorUiState(null, null, false, false, 0, "nc-elevator-disconnected"));
            return;
        }

        var up = CanMove((cabin.Owner, cabin.Comp), 1, out _, out _, out var upFailure);
        var down = CanMove((cabin.Owner, cabin.Comp), -1, out _, out _, out var downFailure);
        // Occupancy may change while this window stays open. Keep existing stops retryable;
        // TryMove always revalidates clearance before accepting a request.
        up |= upFailure == "nc-elevator-shaft-blocked";
        down |= downFailure == "nc-elevator-shaft-blocked";
        if (feedback == null && cabin.Comp.TargetDepth == null && !up && !down)
            feedback = upFailure == "nc-elevator-shaft-blocked" || downFailure == "nc-elevator-shaft-blocked"
                ? "nc-elevator-shaft-blocked"
                : upFailure;
        _ui.SetUiState(panel, NCElevatorUiKey.Key,
            new NCElevatorUiState(zMap.Depth, cabin.Comp.TargetDepth, up, down,
                cabin.Comp.Settings.TravelTime.TotalSeconds, feedback));
    }
}
