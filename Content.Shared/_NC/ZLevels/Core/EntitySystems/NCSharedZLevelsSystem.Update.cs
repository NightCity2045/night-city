/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

// Stair surface tracking and transition grace adapted from KingdomDream #104,
// commit b66ee37c2466c9556e021db68534db892ba0614b, CESharedZLevelsSystem.Update.cs (MIT).

using Content.Shared._NC.ZLevels.Core.Components;
using Content.Shared.Throwing;
using Robust.Shared.Physics.Components;

namespace Content.Shared._NC.ZLevels.Core.EntitySystems;

public abstract partial class NCSharedZLevelsSystem
{
    private const float ClimbHoldTime = 0.5f;
    private const float ClimbDownGraceTime = 1f;

    public int UpdateCalls { get; private set; }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        UpdateCalls = 0;

        if (_net.IsClient && !_clientSimulation)
            return;

        // NC-Edit-Start: terminal Z-level falls are not owned by the vanilla chasm system.
        UpdateTerminalFalls();
        // NC-Edit-End

        _accumulatedTime += TimeSpan.FromSeconds(frameTime);

        var steps = 0;
        while (_accumulatedTime >= _fixedTimestep && steps < MaxStepsPerFrame)
        {
            UpdateZPhysics((float) _fixedTimestep.TotalSeconds);
            _accumulatedTime -= _fixedTimestep;

            steps++;
        }
    }

    // NC-Edit-Start: delete bottom-level fallers after their visual animation finishes.
    private void UpdateTerminalFalls()
    {
        var query = EntityQueryEnumerator<NCZLevelTerminalFallComponent>();
        while (query.MoveNext(out var uid, out var terminalFall))
        {
            if (_timing.CurTime < terminalFall.DeleteAt)
                continue;

            PredictedQueueDel(uid);
        }
    }
    // NC-Edit-End

    private void UpdateZPhysics(float frameTime)
    {
        UpdateDirtyMovement();

        for (var i = _activeBodies.Count - 1; i >= 0; i--)
        {
            var uid = _activeBodies[i];

            if (!ZPhysicsQuery.TryComp(uid, out var zPhysicsComponent) ||
                !_transformQuery.TryComp(uid, out var xform) ||
                !_physicsQuery.TryComp(uid, out var physics))
            {
                _activeBodies.RemoveAt(i);
                continue;
            }

            if (!_zMapQuery.HasComp(xform.MapUid))
            {
                _activeBodies.RemoveAt(i);
                continue;
            }

            ProcessZPhysics((uid, zPhysicsComponent, physics), frameTime);
        }
    }

    private void ProcessZPhysics(Entity<NCZPhysicsComponent, PhysicsComponent> entity, float frameTime)
    {
        UpdateCalls++;

        var zPhysicsComponent = entity.Comp1;

        var oldVelocity = zPhysicsComponent.Velocity;
        var oldHeight = zPhysicsComponent.LocalPosition;
        var oldHold = zPhysicsComponent.ClimbHoldTimer;
        var oldGrace = zPhysicsComponent.ClimbDownGraceTimer;

        // Hold bodies on the transit map until arrival, including objects that leave the platform.
        if (HasComp<NCZGridMotionMapComponent>(Transform(entity).MapUid))
        {
            SetZPosition((entity.Owner, zPhysicsComponent), float.Max(0f, zPhysicsComponent.CachedGroundHeight));
            SetZVelocity((entity.Owner, zPhysicsComponent), 0f);
            return;
        }

        // A stair on the lower map must not hold someone above the opening indefinitely.
        var dropThroughBelow = zPhysicsComponent.CachedGroundFromBelow &&
            zPhysicsComponent.CachedGroundIsSlope && zPhysicsComponent.CachedGroundHeight > 0f && zPhysicsComponent.ClimbHoldTimer <= 0f;
        var onStickySupport = zPhysicsComponent.AutoStep && zPhysicsComponent.CachedStickyGround &&
            !dropThroughBelow && zPhysicsComponent.Velocity is > -2f and <= 0f;

        if (!onStickySupport)
        {
            if (zPhysicsComponent.VelocityGravity)
                zPhysicsComponent.Velocity -= ZGravityForce * zPhysicsComponent.GravityMultiplier * frameTime;

            if (zPhysicsComponent.VelocityRaiseEvent)
            {
                var velocityEvent = new NCGetZVelocityEvent((entity, zPhysicsComponent));
                RaiseLocalEvent(entity, ref velocityEvent);
                zPhysicsComponent.Velocity += velocityEvent.VelocityDelta * frameTime;
            }
        }

        zPhysicsComponent.LocalPosition += zPhysicsComponent.Velocity * frameTime;
        var distanceToGround = zPhysicsComponent.LocalPosition - zPhysicsComponent.CachedGroundHeight;

        if (onStickySupport)
        {
            zPhysicsComponent.LocalPosition = zPhysicsComponent.CachedGroundHeight;
            zPhysicsComponent.Velocity = 0f;
            distanceToGround = 0f;
        }
        else if (zPhysicsComponent is { AutoStep: true, CachedGroundIsSlope: true } &&
            distanceToGround < 0f && !dropThroughBelow)
        {
            // Walls remain high ground, but only explicitly traversable slopes allow auto-stepping.
            zPhysicsComponent.LocalPosition = zPhysicsComponent.CachedGroundHeight;
            distanceToGround = 0f;
        }

        if (zPhysicsComponent.ClimbHoldTimer > 0f)
        {
            zPhysicsComponent.ClimbHoldTimer = MathF.Max(0f, zPhysicsComponent.ClimbHoldTimer - frameTime);
            if (zPhysicsComponent.CachedGroundHeight is > 0f and < 1f &&
                zPhysicsComponent.CachedGroundIsSlope)
            {
                zPhysicsComponent.LocalPosition = zPhysicsComponent.CachedGroundHeight;
                if (zPhysicsComponent.Velocity < 0f)
                    zPhysicsComponent.Velocity = 0f;
            }
            else if (zPhysicsComponent.CachedGroundHeight < 0f)
            {
                zPhysicsComponent.LocalPosition = MathF.Max(0f, zPhysicsComponent.LocalPosition);
                if (zPhysicsComponent.Velocity < 0f)
                    zPhysicsComponent.Velocity = 0f;
            }
            else
            {
                zPhysicsComponent.ClimbHoldTimer = 0f;
            }
            distanceToGround = zPhysicsComponent.LocalPosition - zPhysicsComponent.CachedGroundHeight;
        }

        if (zPhysicsComponent.ClimbDownGraceTimer > 0f)
            zPhysicsComponent.ClimbDownGraceTimer = MathF.Max(0f, zPhysicsComponent.ClimbDownGraceTimer - frameTime);

        if (zPhysicsComponent is { Velocity: < 0, Fallable: true } && !dropThroughBelow)
        {
            if (distanceToGround <= 0.05f)
            {
                if (float.Abs(zPhysicsComponent.Velocity) >= ImpactVelocityLimit)
                {
                    var hitEv = new NCZLevelHitEvent(-zPhysicsComponent.Velocity);
                    RaiseLocalEvent(entity, ref hitEv);

                    var land = new LandEvent(null, true);
                    RaiseLocalEvent(entity, ref land);
                }

                if (float.Abs(zPhysicsComponent.Velocity) < zPhysicsComponent.SleepThreshold)
                {
                    zPhysicsComponent.Velocity = 0;
                    zPhysicsComponent.LocalPosition = zPhysicsComponent.CachedGroundHeight;
                }
                else
                {
                    zPhysicsComponent.Velocity = -zPhysicsComponent.Velocity * zPhysicsComponent.Bounciness;
                }
            }
        }

        // NC-Edit-Start: a solid floor on this level can never be tunnelled through. A fast landing overshoots the
        // floor within one physics step; without this clamp the body would be moved to the map below (sinking under
        // the ground) or, on the lowest map, deleted by the terminal fall.
        if (zPhysicsComponent is { Fallable: true } && !dropThroughBelow &&
            zPhysicsComponent.CachedGroundHeight >= 0f &&
            zPhysicsComponent.LocalPosition < 0f)
        {
            zPhysicsComponent.LocalPosition = 0f;
            if (zPhysicsComponent.Velocity < 0f)
                zPhysicsComponent.Velocity = 0f;
        }
        // NC-Edit-End

        if (zPhysicsComponent.LocalPosition < 0 && TryMoveDownOrChasm(entity))
        {
            zPhysicsComponent.LocalPosition += 1;
            zPhysicsComponent.ClimbHoldTimer = 0f;
            zPhysicsComponent.ClimbDownGraceTimer = zPhysicsComponent.CachedGroundIsSlope ? ClimbDownGraceTime : 0f;
            if (zPhysicsComponent is { CachedStickyGround: false, Fallable: true })
            {
                var fallEv = new NCZLevelFallMapEvent();
                RaiseLocalEvent(entity, ref fallEv);
            }
        }

        if (zPhysicsComponent.LocalPosition >= 1)
        {
            if (HasTileAbove(entity) && !HasConnectedSlopeAbove(entity))
            {
                if (zPhysicsComponent.AutoStep && zPhysicsComponent.CachedGroundIsSlope &&
                    zPhysicsComponent.CachedGroundHeight >= 1f && zPhysicsComponent.Velocity <= 0f)
                {
                    // Keep a supported climber on the stair surface instead of bouncing into the ceiling.
                    zPhysicsComponent.LocalPosition = zPhysicsComponent.CachedGroundHeight;
                }
                else
                {
                    if (float.Abs(zPhysicsComponent.Velocity) >= ImpactVelocityLimit)
                    {
                        var hitEv = new NCZLevelHitEvent(zPhysicsComponent.Velocity);
                        RaiseLocalEvent(entity, ref hitEv);

                        var land = new LandEvent(null, true);
                        RaiseLocalEvent(entity, ref land);
                    }
                    zPhysicsComponent.LocalPosition = 1;
                    zPhysicsComponent.Velocity = -zPhysicsComponent.Velocity * zPhysicsComponent.Bounciness;
                }
            }
            else if ((zPhysicsComponent.Velocity > 0f || zPhysicsComponent.ClimbDownGraceTimer <= 0f) &&
                TryMoveUp(entity))
            {
                zPhysicsComponent.LocalPosition -= 1;
                if (zPhysicsComponent.AutoStep && zPhysicsComponent.CachedGroundIsSlope)
                    zPhysicsComponent.ClimbHoldTimer = ClimbHoldTime;
            }
        }

        if (float.Abs(zPhysicsComponent.Velocity) > ZVelocityLimit)
            zPhysicsComponent.Velocity = float.Sign(zPhysicsComponent.Velocity) * ZVelocityLimit;

        if (float.Abs(oldVelocity - zPhysicsComponent.Velocity) > 0.001f)
            DirtyField(entity, zPhysicsComponent, nameof(NCZPhysicsComponent.Velocity));

        if (float.Abs(oldHeight - zPhysicsComponent.LocalPosition) > 0.001f)
            DirtyField(entity, zPhysicsComponent, nameof(NCZPhysicsComponent.LocalPosition));

        if (!oldHold.Equals(zPhysicsComponent.ClimbHoldTimer))
            DirtyField(entity, zPhysicsComponent, nameof(NCZPhysicsComponent.ClimbHoldTimer));
        if (!oldGrace.Equals(zPhysicsComponent.ClimbDownGraceTimer))
            DirtyField(entity, zPhysicsComponent, nameof(NCZPhysicsComponent.ClimbDownGraceTimer));

        if (zPhysicsComponent.VelocityGravity)
        {
            var targetStatus = distanceToGround > AirborneHeightThreshold ? BodyStatus.InAir : BodyStatus.OnGround;
            if (entity.Comp2.BodyStatus != targetStatus)
            {
                _physicsSystem.SetBodyStatus(entity, entity.Comp2, targetStatus);
                var statusEv = new NCZBodyStatusChangedEvent(targetStatus);
                RaiseLocalEvent(entity, ref statusEv);
            }
        }

        SleepUpdate((entity, entity.Comp1), frameTime);
    }

    /// <summary>
    /// Whether an idle body may leave the Z-physics update list. Explicit lifecycle deactivation is separate.
    /// </summary>
    protected virtual bool CanAutomaticallySleep(EntityUid uid) => true;

    private void SleepUpdate(Entity<NCZPhysicsComponent> entity, float frameTime)
    {
        if (!CanAutomaticallySleep(entity))
        {
            entity.Comp.SleepTimer = 0f;
            return;
        }

        var distanceToGround = entity.Comp.LocalPosition - entity.Comp.CachedGroundHeight;
        var almostStopped = float.Abs(entity.Comp.Velocity) < entity.Comp.SleepThreshold && float.Abs(distanceToGround) <= 0.01f;

        if (!almostStopped)
        {
            entity.Comp.SleepTimer = 0f;
            return;
        }

        entity.Comp.SleepTimer += frameTime;
        if (entity.Comp.SleepTimer < entity.Comp.TimeToSleep)
            return;

        SleepBody((entity, entity));
    }
}

/// <summary>
/// Raised directed on an entity when its BodyStatus changes due to Z-physics height sync.
/// </summary>
[ByRefEvent]
public readonly record struct NCZBodyStatusChangedEvent(BodyStatus NewStatus);
