// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Server._NC.Vehicle.AV.Components;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared._NC.Vehicle.AV.Components;
using Content.Shared._NC.ZLevels.Core.Components;
using Content.Shared._NC.ZLevels.Core.EntitySystems;
using Content.Shared.Movement.Systems;
using Content.Shared.Power.EntitySystems;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;

namespace Content.Server._NC.Vehicle.AV.Systems;

/// <summary>
/// Simulates server-authoritative vectored lift for AV grids, including planar movement and Z transitions.
/// </summary>
public sealed partial class NCAVSystem : EntitySystem
{
    private const float HeightEpsilon = 0.001f;
    private const float TileProbeHalfSize = 0.05f;

    [Dependency] private NCSharedZLevelsSystem _zLevels = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedPhysicsSystem _physics = default!;
    [Dependency] private SharedPowerReceiverSystem _power = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private ShuttleSystem _shuttles = default!;

    [Dependency] private EntityQuery<MapGridComponent> _gridQuery = default!;
    [Dependency] private EntityQuery<PhysicsComponent> _physicsQuery = default!;

    private readonly HashSet<EntityUid> _avs = [];
    private readonly HashSet<EntityUid> _lifters = [];
    private readonly HashSet<EntityUid> _pilots = [];
    private readonly Dictionary<EntityUid, float> _availableLift = [];
    private readonly Dictionary<EntityUid, int> _verticalInput = [];
    private readonly Dictionary<EntityUid, Vector2> _planarInput = [];
    private readonly Dictionary<EntityUid, float> _angularInput = [];
    private readonly Dictionary<EntityUid, int> _pendingTransitions = [];
    private readonly List<(EntityUid Uid, int Offset)> _transitionScratch = [];
    private readonly HashSet<Entity<PhysicsComponent>> _obstacleQuery = [];
    private readonly HashSet<EntityUid> _currentObstacles = [];
    private readonly HashSet<EntityUid> _candidateObstacles = [];
    private List<Entity<MapGridComponent>> _gridScratch = [];

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCAVComponent, ComponentStartup>(OnAVStartup);
        SubscribeLocalEvent<NCAVComponent, ComponentShutdown>(OnAVShutdown);
        SubscribeLocalEvent<NCAVComponent, PreventCollideEvent>(OnPreventCollide);
        SubscribeLocalEvent<NCAVLifterComponent, ComponentStartup>(OnLifterStartup);
        SubscribeLocalEvent<NCAVLifterComponent, ComponentShutdown>(OnLifterShutdown);
        SubscribeLocalEvent<NCAVPilotComponent, ComponentStartup>(OnPilotStartup);
        SubscribeLocalEvent<NCAVPilotComponent, ComponentShutdown>(OnPilotShutdown);
        SubscribeLocalEvent<PhysicsUpdateBeforeSolveEvent>(OnPhysicsStep);
    }

    private void OnAVStartup(Entity<NCAVComponent> ent, ref ComponentStartup args)
    {
        if (!_gridQuery.HasComp(ent))
            return;

        _avs.Add(ent);
        EnsureComp<PreventFTLComponent>(ent);

        // A placed AV starts landed. Shuttle movement is enabled only after lift exceeds weight.
        if (!ent.Comp.Airborne && TryComp<ShuttleComponent>(ent, out var shuttle))
        {
            shuttle.Enabled = false;
            _shuttles.Disable(ent);
        }
    }

    private void OnAVShutdown(Entity<NCAVComponent> ent, ref ComponentShutdown args)
    {
        _avs.Remove(ent);
        _availableLift.Remove(ent);
        _verticalInput.Remove(ent);
        _planarInput.Remove(ent);
        _angularInput.Remove(ent);
        _pendingTransitions.Remove(ent);
    }

    private void OnLifterStartup(Entity<NCAVLifterComponent> ent, ref ComponentStartup args) => _lifters.Add(ent);
    private void OnLifterShutdown(Entity<NCAVLifterComponent> ent, ref ComponentShutdown args) => _lifters.Remove(ent);
    private void OnPilotStartup(Entity<NCAVPilotComponent> ent, ref ComponentStartup args) => _pilots.Add(ent);
    private void OnPilotShutdown(Entity<NCAVPilotComponent> ent, ref ComponentShutdown args) => _pilots.Remove(ent);

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Moving a complete grid between maps mutates its transform tree. Do it outside the physics solve callback,
        // where map-change reactions are allowed to update child entities safely.
        _transitionScratch.Clear();
        foreach (var transition in _pendingTransitions)
            _transitionScratch.Add((transition.Key, transition.Value));
        _pendingTransitions.Clear();

        foreach (var (uid, offset) in _transitionScratch)
            ProcessLevelTransition(uid, offset);
    }

    /// <summary>Updates the vertical input for a pilot only when their console belongs to an AV grid.</summary>
    public void SetPilotInput(EntityUid pilotUid, NCAVButtons button, bool pressed)
    {
        if (!TryComp<PilotComponent>(pilotUid, out var pilot) ||
            pilot.Console is not { } console ||
            Transform(console).GridUid is not { } grid ||
            !HasComp<NCAVComponent>(grid))
        {
            RemComp<NCAVPilotComponent>(pilotUid);
            return;
        }

        var input = EnsureComp<NCAVPilotComponent>(pilotUid);
        input.AV = grid;

        if (pressed)
            input.HeldButtons |= button;
        else
            input.HeldButtons &= ~button;
    }

    private void OnPhysicsStep(ref PhysicsUpdateBeforeSolveEvent args)
    {
        if (args.Prediction || !float.IsFinite(args.DeltaTime) || args.DeltaTime <= 0f)
            return;

        AggregateLift();
        AggregatePilotInput();

        foreach (var uid in _avs)
        {
            if (IsPaused(uid) ||
                !TryComp<NCAVComponent>(uid, out var av) ||
                !_physicsQuery.TryComp(uid, out var body) ||
                !_gridQuery.TryComp(uid, out var grid))
            {
                continue;
            }

            var lift = _availableLift.GetValueOrDefault(uid);
            var input = Math.Clamp(_verticalInput.GetValueOrDefault(uid), -1, 1);
            var liftChanged = !av.AvailableLift.Equals(lift);

            if (liftChanged)
                av.AvailableLift = lift;

            if (_pendingTransitions.ContainsKey(uid))
            {
                if (liftChanged)
                    Dirty(uid, av);
                continue;
            }

            if (!av.Airborne)
            {
                if (input > 0 && lift > body.Mass * av.Gravity)
                    TakeOff((uid, av), body);
                else
                {
                    EnsureLanded((uid, av), body);
                    if (liftChanged)
                        Dirty(uid, av);
                    continue;
                }
            }

            SimulatePlanar((uid, av), (uid, grid), body, lift, args.DeltaTime);
            SimulateVertical((uid, av), (uid, grid), body, input, lift, args.DeltaTime);
        }
    }

    private void AggregateLift()
    {
        _availableLift.Clear();

        foreach (var uid in _lifters)
        {
            if (!TryComp<NCAVLifterComponent>(uid, out var lifter) ||
                lifter.Lift <= 0f ||
                !float.IsFinite(lifter.Lift))
            {
                continue;
            }

            var xform = Transform(uid);
            if (!xform.Anchored || xform.GridUid is not { } grid || !HasComp<NCAVComponent>(grid) || !_power.IsPowered(uid))
                continue;

            _availableLift[grid] = _availableLift.GetValueOrDefault(grid) + lifter.Lift;
        }
    }

    private void AggregatePilotInput()
    {
        _verticalInput.Clear();
        _planarInput.Clear();
        _angularInput.Clear();

        foreach (var uid in _pilots)
        {
            if (!TryComp<NCAVPilotComponent>(uid, out var input))
                continue;

            // PilotComponent is removed when shuttle piloting ends. Clear the cached input here instead of
            // adding a second exclusive ComponentShutdown subscription for the inherited pilot component.
            if (!TryComp<PilotComponent>(uid, out var pilot) ||
                pilot.Console is not { } console ||
                Transform(console).GridUid is not { } currentGrid ||
                input.AV != currentGrid ||
                !HasComp<NCAVComponent>(currentGrid))
            {
                input.AV = null;
                input.HeldButtons = NCAVButtons.None;
                continue;
            }

            var av = currentGrid;

            var direction = 0;
            if ((input.HeldButtons & NCAVButtons.Ascend) != 0)
                direction++;
            if ((input.HeldButtons & NCAVButtons.Descend) != 0)
                direction--;

            _verticalInput[av] = _verticalInput.GetValueOrDefault(av) + direction;

            // Shuttle input is relative to the console orientation, so rotated cockpits retain intuitive controls.
            var planar = Vector2.Zero;
            if ((pilot.HeldButtons & ShuttleButtons.StrafeLeft) != 0)
                planar.X--;
            if ((pilot.HeldButtons & ShuttleButtons.StrafeRight) != 0)
                planar.X++;
            if ((pilot.HeldButtons & ShuttleButtons.StrafeUp) != 0)
                planar.Y++;
            if ((pilot.HeldButtons & ShuttleButtons.StrafeDown) != 0)
                planar.Y--;

            _planarInput[av] = _planarInput.GetValueOrDefault(av) + Transform(console).LocalRotation.RotateVec(planar);

            var rotation = 0f;
            if ((pilot.HeldButtons & ShuttleButtons.RotateLeft) != 0)
                rotation--;
            if ((pilot.HeldButtons & ShuttleButtons.RotateRight) != 0)
                rotation++;

            _angularInput[av] = _angularInput.GetValueOrDefault(av) + rotation;
        }
    }

    private void SimulatePlanar(
        Entity<NCAVComponent> ent,
        Entity<MapGridComponent> grid,
        PhysicsComponent body,
        float availableLift,
        float deltaTime)
    {
        // Horizontal control comes from tilting the powered lift vector. Partial lift provides proportionally
        // weaker control, while a complete power loss leaves the AV ballistic until it lands.
        var hoverLift = Math.Max(body.Mass * ent.Comp.Gravity, 0.001f);
        var controlAuthority = Math.Clamp(availableLift / hoverLift, 0f, 1f);
        var worldRotation = _transform.GetWorldRotation(ent);

        var planarInput = _planarInput.GetValueOrDefault(ent);
        if (planarInput.LengthSquared() > 1f)
            planarInput = Vector2.Normalize(planarInput);

        var localVelocity = (-worldRotation).RotateVec(body.LinearVelocity);
        var targetVelocity = planarInput * Math.Max(ent.Comp.MaxPlanarSpeed, 0f);
        var acceleration = planarInput == Vector2.Zero
            ? Math.Max(ent.Comp.PlanarDeceleration, 0f)
            : Math.Max(ent.Comp.PlanarAcceleration, 0f) * controlAuthority;
        var nextLocalVelocity = MoveTowards(localVelocity, targetVelocity, acceleration * deltaTime);
        var nextWorldVelocity = worldRotation.RotateVec(nextLocalVelocity);

        var angularInput = Math.Clamp(_angularInput.GetValueOrDefault(ent), -1f, 1f);
        var targetAngularVelocity = -angularInput * Math.Max(ent.Comp.MaxAngularSpeed, 0f);
        var angularStep = Math.Max(ent.Comp.AngularAcceleration, 0f) * controlAuthority * deltaTime;
        var nextAngularVelocity = MoveTowards(body.AngularVelocity, targetAngularVelocity, angularStep);

        ResolveObstacleVelocity(ent, grid, ref nextWorldVelocity, ref nextAngularVelocity, deltaTime);
        _physics.SetLinearVelocity(ent, nextWorldVelocity, body: body);
        _physics.SetAngularVelocity(ent, nextAngularVelocity, body: body);
    }

    /// <summary>
    /// Grid fixtures only collide with other grid tiles in Robust. Sweep the occupied AV footprint against hard,
    /// anchored structures so flight ignores the ground plane while walls, windows, and doors remain solid.
    /// </summary>
    private void ResolveObstacleVelocity(
        Entity<NCAVComponent> ent,
        Entity<MapGridComponent> grid,
        ref Vector2 linearVelocity,
        ref float angularVelocity,
        float deltaTime)
    {
        var position = _transform.GetWorldPosition(ent);
        var rotation = _transform.GetWorldRotation(ent);
        var mapId = Transform(ent).MapID;

        CollectFlightObstacles(ent, grid, mapId, position, rotation, _currentObstacles);

        // Resolve rotation separately so turning into a wall does not suppress safe translation away from it.
        if (angularVelocity != 0f &&
            SweepHitsNewObstacle(ent, grid, mapId, position, rotation, Vector2.Zero, angularVelocity, deltaTime))
        {
            angularVelocity = 0f;
        }

        if (linearVelocity == Vector2.Zero ||
            !SweepHitsNewObstacle(ent, grid, mapId, position, rotation, linearVelocity, angularVelocity, deltaTime))
        {
            return;
        }

        // Test world axes independently to let the AV slide along a wall instead of stopping dead on contact.
        var resolvedVelocity = Vector2.Zero;
        var first = MathF.Abs(linearVelocity.X) >= MathF.Abs(linearVelocity.Y)
            ? new Vector2(linearVelocity.X, 0f)
            : new Vector2(0f, linearVelocity.Y);
        var second = linearVelocity - first;

        if (!SweepHitsNewObstacle(ent, grid, mapId, position, rotation, first, angularVelocity, deltaTime))
            resolvedVelocity = first;

        var intermediatePosition = position + resolvedVelocity * deltaTime;
        var intermediateRotation = rotation + angularVelocity * deltaTime;
        if (!SweepHitsNewObstacle(ent, grid, mapId, intermediatePosition, intermediateRotation, second, 0f, deltaTime))
            resolvedVelocity += second;

        linearVelocity = resolvedVelocity;
    }

    private bool SweepHitsNewObstacle(
        Entity<NCAVComponent> ent,
        Entity<MapGridComponent> grid,
        MapId mapId,
        Vector2 position,
        Angle rotation,
        Vector2 linearVelocity,
        float angularVelocity,
        float deltaTime)
    {
        var linearTravel = linearVelocity.Length() * deltaTime;
        var turnRadius = grid.Comp.LocalAABB.Size.Length() * 0.5f;
        var angularTravel = MathF.Abs(angularVelocity) * deltaTime * turnRadius;
        var sweepStep = Math.Max(ent.Comp.CollisionSweepStep, 0.05f);
        var steps = Math.Max(1, (int) MathF.Ceiling(Math.Max(linearTravel, angularTravel) / sweepStep));

        for (var i = 1; i <= steps; i++)
        {
            var fraction = i / (float) steps;
            var samplePosition = position + linearVelocity * deltaTime * fraction;
            var sampleRotation = rotation + angularVelocity * deltaTime * fraction;
            CollectFlightObstacles(ent, grid, mapId, samplePosition, sampleRotation, _candidateObstacles);

            foreach (var obstacle in _candidateObstacles)
            {
                // Existing overlap is ignored so an AV affected by the old bug can still fly back out.
                if (!_currentObstacles.Contains(obstacle))
                    return true;
            }
        }

        return false;
    }

    private void CollectFlightObstacles(
        Entity<NCAVComponent> ent,
        Entity<MapGridComponent> grid,
        MapId mapId,
        Vector2 position,
        Angle rotation,
        HashSet<EntityUid> obstacles)
    {
        obstacles.Clear();
        var inset = Math.Clamp(ent.Comp.CollisionInset, 0f, grid.Comp.TileSize * 0.49f);

        foreach (var tile in _maps.GetAllTiles(grid, grid.Comp))
        {
            var localBounds = _lookup.GetLocalBounds(tile.GridIndices, grid.Comp.TileSize).Enlarged(-inset);
            var worldCenter = position + rotation.RotateVec(localBounds.Center);
            var worldBounds = Box2.CenteredAround(worldCenter, localBounds.Size);
            var rotatedBounds = new Box2Rotated(worldBounds, rotation, worldCenter);

            _obstacleQuery.Clear();
            _lookup.GetEntitiesIntersecting(mapId, rotatedBounds, _obstacleQuery, LookupFlags.Static);

            foreach (var obstacle in _obstacleQuery)
            {
                var obstacleTransform = Transform(obstacle);
                if (obstacle.Owner == ent.Owner ||
                    obstacleTransform.GridUid == ent.Owner ||
                    !obstacleTransform.Anchored ||
                    !obstacle.Comp.CanCollide ||
                    !obstacle.Comp.Hard ||
                    (obstacle.Comp.CollisionLayer & (int) ent.Comp.ObstacleCollisionMask) == 0)
                {
                    continue;
                }

                obstacles.Add(obstacle);
            }
        }
    }

    private static Vector2 MoveTowards(Vector2 current, Vector2 target, float maxDelta)
    {
        var difference = target - current;
        if (difference.LengthSquared() <= maxDelta * maxDelta)
            return target;

        return current + Vector2.Normalize(difference) * maxDelta;
    }

    private static float MoveTowards(float current, float target, float maxDelta)
    {
        if (MathF.Abs(target - current) <= maxDelta)
            return target;

        return current + MathF.CopySign(maxDelta, target - current);
    }

    private void TakeOff(Entity<NCAVComponent> ent, PhysicsComponent body)
    {
        ent.Comp.Airborne = true;
        ent.Comp.Height = Math.Max(ent.Comp.Height, HeightEpsilon);
        ent.Comp.LandingBlocked = false;

        if (TryComp<ShuttleComponent>(ent, out var shuttle))
        {
            shuttle.Enabled = true;
            _shuttles.Enable(ent, component: body, shuttle: shuttle);
        }

        Dirty(ent, ent.Comp);
    }

    private void EnsureLanded(Entity<NCAVComponent> ent, PhysicsComponent body)
    {
        ent.Comp.LandingBlocked = false;

        if (!TryComp<ShuttleComponent>(ent, out var shuttle) ||
            (!shuttle.Enabled && body.BodyType == BodyType.Static))
        {
            return;
        }

        // Component startup order is not guaranteed when a serialized AV grid loads.
        // Reassert the landed body state if the ordinary shuttle startup enabled it later.
        shuttle.Enabled = false;
        _shuttles.Disable(ent, component: body);
    }

    private void SimulateVertical(
        Entity<NCAVComponent> ent,
        Entity<MapGridComponent> grid,
        PhysicsComponent body,
        int input,
        float availableLift,
        float deltaTime)
    {
        var mass = Math.Max(body.Mass, 0.001f);

        // A blocked-landing warning remains latched until the pilot climbs away or a later landing succeeds.
        if (input > 0)
            ent.Comp.LandingBlocked = false;

        var targetVelocity = input * Math.Max(ent.Comp.MaxVerticalSpeed, 0f);
        var desiredAcceleration = (targetVelocity - ent.Comp.VerticalVelocity) * Math.Max(ent.Comp.VerticalResponse, 0f);
        var requestedLift = Math.Max(0f, mass * (ent.Comp.Gravity + desiredAcceleration));
        var actualAcceleration = Math.Min(availableLift, requestedLift) / mass - ent.Comp.Gravity;

        ent.Comp.VerticalVelocity = Math.Clamp(
            ent.Comp.VerticalVelocity + actualAcceleration * deltaTime,
            -Math.Max(ent.Comp.MaxVerticalSpeed, 0f),
            Math.Max(ent.Comp.MaxVerticalSpeed, 0f));
        ent.Comp.Height += ent.Comp.VerticalVelocity * deltaTime;

        if (ent.Comp.Height >= 1f)
        {
            if (CanEnterAdjacentLevel(grid, 1))
            {
                ent.Comp.Height = 1f - HeightEpsilon;
                ent.Comp.VerticalVelocity = 0f;
                ent.Comp.LandingBlocked = false;
                _pendingTransitions[ent] = 1;
            }
            else
            {
                ent.Comp.Height = 1f - HeightEpsilon;
                ent.Comp.VerticalVelocity = Math.Min(0f, ent.Comp.VerticalVelocity);
            }
        }
        else if (ent.Comp.Height <= 0f)
        {
            if (CanLand(grid))
            {
                Land(ent, body);
            }
            else if (CanEnterAdjacentLevel(grid, -1))
            {
                ent.Comp.Height = HeightEpsilon;
                ent.Comp.VerticalVelocity = 0f;
                ent.Comp.LandingBlocked = false;
                _pendingTransitions[ent] = -1;
            }
            else
            {
                ent.Comp.Height = HeightEpsilon;
                ent.Comp.VerticalVelocity = Math.Max(0f, ent.Comp.VerticalVelocity);
                ent.Comp.LandingBlocked = true;
            }
        }

        Dirty(ent, ent.Comp);
    }

    private void ProcessLevelTransition(EntityUid uid, int offset)
    {
        if (!TryComp<NCAVComponent>(uid, out var av) ||
            !av.Airborne ||
            !_gridQuery.TryComp(uid, out var grid) ||
            !CanEnterAdjacentLevel((uid, grid), offset) ||
            !_zLevels.TryMove(uid, offset))
        {
            if (av != null)
            {
                av.VerticalVelocity = 0f;
                av.LandingBlocked = offset < 0;
                Dirty(uid, av);
            }

            return;
        }

        // Preserve the continuous altitude convention after crossing a discrete Z-level boundary.
        av.Height = offset > 0 ? HeightEpsilon : 1f - HeightEpsilon;
        av.LandingBlocked = false;
        Dirty(uid, av);
    }

    private void Land(Entity<NCAVComponent> ent, PhysicsComponent body)
    {
        ent.Comp.Airborne = false;
        ent.Comp.Height = 0f;
        ent.Comp.VerticalVelocity = 0f;
        ent.Comp.LandingBlocked = false;

        _physics.SetLinearVelocity(ent, Vector2.Zero, body: body);
        _physics.SetAngularVelocity(ent, 0f, body: body);

        if (TryComp<ShuttleComponent>(ent, out var shuttle))
        {
            shuttle.Enabled = false;
            _shuttles.Disable(ent, component: body);
        }
    }

    /// <summary>
    /// A landed AV needs a solid tile below every filled tile of its own grid and no hard anchored obstacle.
    /// This is the entire landing contract; no docking entity or landing-pad component is involved.
    /// </summary>
    private bool CanLand(Entity<MapGridComponent> avGrid)
    {
        var mapId = Transform(avGrid).MapID;

        foreach (var tile in _maps.GetAllTiles(avGrid, avGrid.Comp))
        {
            var world = _transform.ToMapCoordinates(_maps.GridTileToLocal(avGrid, avGrid.Comp, tile.GridIndices));
            if (world.MapId != mapId || !HasClearSupportAt(avGrid, world.Position, mapId))
                return false;
        }

        return true;
    }

    private bool HasClearSupportAt(EntityUid av, Vector2 worldPosition, MapId mapId)
    {
        _gridScratch.Clear();
        var probe = new Box2(worldPosition - new Vector2(TileProbeHalfSize), worldPosition + new Vector2(TileProbeHalfSize));
        _maps.FindGridsIntersecting(mapId, probe, ref _gridScratch, includeMap: true);

        var supported = false;
        foreach (var candidate in _gridScratch)
        {
            if (candidate.Owner == av || !_maps.TryGetTileRef(candidate, candidate.Comp, worldPosition, out var tile) || tile.Tile.IsEmpty)
                continue;

            supported = true;
            if (HasHardAnchoredObstacle(candidate, tile.GridIndices))
                return false;
        }

        return supported;
    }

    private bool CanEnterAdjacentLevel(Entity<MapGridComponent> avGrid, int offset)
    {
        var currentMap = Transform(avGrid).MapUid;
        if (currentMap is null ||
            !TryComp<NCZMapComponent>(currentMap, out var zMap) ||
            !_zLevels.TryMapOffset((currentMap.Value, zMap), offset, out var targetMap) ||
            !TryComp<MapComponent>(targetMap, out var targetMapComponent))
        {
            return false;
        }

        foreach (var tile in _maps.GetAllTiles(avGrid, avGrid.Comp))
        {
            var world = _transform.ToMapCoordinates(_maps.GridTileToLocal(avGrid, avGrid.Comp, tile.GridIndices)).Position;
            _gridScratch.Clear();
            var probe = new Box2(world - new Vector2(TileProbeHalfSize), world + new Vector2(TileProbeHalfSize));
            _maps.FindGridsIntersecting(targetMapComponent.MapId, probe, ref _gridScratch, includeMap: true);

            foreach (var candidate in _gridScratch)
            {
                if (HasComp<NCAVComponent>(candidate))
                    return false;

                if (!_maps.TryGetTileRef(candidate, candidate.Comp, world, out var targetTile) || targetTile.Tile.IsEmpty)
                    continue;

                if (HasHardAnchoredObstacle(candidate, targetTile.GridIndices))
                    return false;
            }
        }

        return true;
    }

    private bool HasHardAnchoredObstacle(Entity<MapGridComponent> grid, Vector2i tile)
    {
        var anchored = _maps.GetAnchoredEntitiesEnumerator(grid, grid.Comp, tile);
        while (anchored.MoveNext(out var uid))
        {
            if (_physicsQuery.TryComp(uid, out var physics) && physics.CanCollide && physics.Hard)
                return true;
        }

        return false;
    }

    private void OnPreventCollide(Entity<NCAVComponent> ent, ref PreventCollideEvent args)
    {
        // Tile grids represent the supporting plane in 2D. Ignore only static grid bodies while airborne;
        // anchored walls and dynamic vehicles remain ordinary colliders.
        if (ent.Comp.Airborne && _gridQuery.HasComp(args.OtherEntity) && args.OtherBody.BodyType == BodyType.Static)
            args.Cancelled = true;
    }
}
