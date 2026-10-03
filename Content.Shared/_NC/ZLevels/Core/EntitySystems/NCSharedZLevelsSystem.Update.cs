/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Shared._NC.ZLevels.Core.Components;
using Content.Shared.Throwing;
using Robust.Shared.Physics.Components;

namespace Content.Shared._NC.ZLevels.Core.EntitySystems;

public abstract partial class NCSharedZLevelsSystem
{
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

        // NC-Edit-Start: crossing the uphill tile boundary completes the staircase transition.
        // Reset vertical state on the destination floor so the paired down staircase cannot immediately bounce the body back.
        if (zPhysicsComponent.PendingSlopeTransitionUp)
        {
            zPhysicsComponent.PendingSlopeTransitionUp = false;
            if (TryMoveUp(entity))
            {
                zPhysicsComponent.LocalPosition = 0f;
                zPhysicsComponent.Velocity = 0f;
                DirtyField(entity, zPhysicsComponent, nameof(NCZPhysicsComponent.LocalPosition));
                DirtyField(entity, zPhysicsComponent, nameof(NCZPhysicsComponent.Velocity));
                return;
            }
        }
        // NC-Edit-End

        if (zPhysicsComponent.VelocityGravity)
            zPhysicsComponent.Velocity -= ZGravityForce * zPhysicsComponent.GravityMultiplier * frameTime;

        if (zPhysicsComponent.VelocityRaiseEvent)
        {
            var velocityEvent = new NCGetZVelocityEvent((entity, zPhysicsComponent));
            RaiseLocalEvent(entity, ref velocityEvent);

            zPhysicsComponent.Velocity += velocityEvent.VelocityDelta * frameTime;
        }

        zPhysicsComponent.LocalPosition += zPhysicsComponent.Velocity * frameTime;
        var distanceToGround = zPhysicsComponent.LocalPosition - zPhysicsComponent.CachedGroundHeight;

        // NC-Edit-Start: walls are high ground too, but only slope prototypes are valid automatic steps.
        if (zPhysicsComponent is { AutoStep: true, CachedGroundIsSlope: true } && distanceToGround < 0)
            zPhysicsComponent.LocalPosition -= distanceToGround;
        // NC-Edit-End

        if (zPhysicsComponent.CachedStickyGround)
            zPhysicsComponent.LocalPosition -= distanceToGround;

        if (zPhysicsComponent is { Velocity: < 0, Fallable: true })
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

        if (zPhysicsComponent.LocalPosition < 0)
        {
            if (TryMoveDownOrChasm(entity))
            {
                zPhysicsComponent.LocalPosition += 1;
                if (zPhysicsComponent is { CachedStickyGround: false, Fallable: true })
                {
                    var fallEv = new NCZLevelFallMapEvent();
                    RaiseLocalEvent(entity, ref fallEv);
                }
            }
        }

        if (zPhysicsComponent.LocalPosition >= 1)
        {
            // NC-Edit-Start: a floor above is the destination at the top of a staircase, not a ceiling.
            if (zPhysicsComponent.CachedGroundIsSlope && zPhysicsComponent.CachedGroundHeight >= 1f)
            {
                if (TryMoveUp(entity))
                    zPhysicsComponent.LocalPosition -= 1;
                else
                    zPhysicsComponent.LocalPosition = 1;
            }
            else if (HasTileAbove(entity))
            // NC-Edit-End
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
            else
            {
                if (TryMoveUp(entity))
                    zPhysicsComponent.LocalPosition -= 1;
            }
        }

        if (float.Abs(zPhysicsComponent.Velocity) > ZVelocityLimit)
            zPhysicsComponent.Velocity = float.Sign(zPhysicsComponent.Velocity) * ZVelocityLimit;

        if (float.Abs(oldVelocity - zPhysicsComponent.Velocity) > 0.001f)
            DirtyField(entity, zPhysicsComponent, nameof(NCZPhysicsComponent.Velocity));

        if (float.Abs(oldHeight - zPhysicsComponent.LocalPosition) > 0.001f)
            DirtyField(entity, zPhysicsComponent, nameof(NCZPhysicsComponent.LocalPosition));

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

    private void SleepUpdate(Entity<NCZPhysicsComponent> entity, float frameTime)
    {
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
