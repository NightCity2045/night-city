/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Shared._NC.ZLevels.Core.Components;
using JetBrains.Annotations;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Events;

namespace Content.Shared._NC.ZLevels.Core.EntitySystems;

public abstract partial class NCSharedZLevelsSystem
{
    private readonly List<EntityUid> _activeBodies = new();

    public IReadOnlyList<EntityUid> ActiveBodies => _activeBodies;

    private void InitializeActivation()
    {
        SubscribeLocalEvent<NCZPhysicsComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<NCZPhysicsComponent, ComponentShutdown>(OnShutdown);

        SubscribeLocalEvent<NCZPhysicsComponent, AnchorStateChangedEvent>(OnAnchorStateChanged);
        SubscribeLocalEvent<NCZPhysicsComponent, PhysicsBodyTypeChangedEvent>(OnPhysicsBodyTypeChanged);

        SubscribeLocalEvent<NCZPhysicsComponent, EntParentChangedMessage>(OnParentChanged);
    }

    private void OnMapInit(Entity<NCZPhysicsComponent> entity, ref MapInitEvent args)
    {
        RefreshBody(entity);

        var mapUid = Transform(entity).MapUid;

        if (!_zMapQuery.TryComp(mapUid, out var zLevel))
            return;

        if (entity.Comp.CurrentZLevel == zLevel.Depth)
            return;

        entity.Comp.CurrentZLevel = zLevel.Depth;
        DirtyField(entity, entity.Comp, nameof(NCZPhysicsComponent.CurrentZLevel));
    }

    private void OnShutdown(Entity<NCZPhysicsComponent> entity, ref ComponentShutdown args)
    {
        SleepBody((entity, entity));
    }

    private void OnAnchorStateChanged(Entity<NCZPhysicsComponent> entity, ref AnchorStateChangedEvent args)
    {
        RefreshBody(entity);
    }

    private void OnPhysicsBodyTypeChanged(Entity<NCZPhysicsComponent> entity, ref PhysicsBodyTypeChangedEvent args)
    {
        RefreshBody(entity);
    }

    private void OnParentChanged(Entity<NCZPhysicsComponent> entity, ref EntParentChangedMessage args)
    {
        RefreshBody(entity);

        if (ZPhysicsQuery.TryComp(args.OldParent, out var oldParentPhysics))
        {
            SetZPosition((entity, entity), oldParentPhysics.LocalPosition);
            return;
        }

        if (ZPhysicsQuery.HasComp(Transform(entity).ParentUid))
        {
            SetZPosition((entity, entity), 0);
        }
    }

    [PublicAPI]
    public void WakeBody(Entity<NCZPhysicsComponent?> entity)
    {
        if (!Resolve(entity, ref entity.Comp, false))
            return;

        if (_activeBodies.Contains(entity))
            return;

        entity.Comp.Sleeping = false;
        entity.Comp.SleepTimer = 0f;

        _activeBodies.Add(entity);
    }

    [PublicAPI]
    public void SleepBody(Entity<NCZPhysicsComponent?> entity)
    {
        if (!Resolve(entity, ref entity.Comp, false))
            return;

        entity.Comp.Sleeping = true;
        entity.Comp.SleepTimer = 0f;

        _activeBodies.Remove(entity);
    }

    [PublicAPI]
    public void RefreshBody(Entity<NCZPhysicsComponent> entity)
    {
        if (TerminatingOrDeleted(entity))
        {
            SleepBody((entity, entity));
            return;
        }

        var transform = Transform(entity);
        var parent = transform.ParentUid;

        var onMap = parent == transform.GridUid || parent == transform.MapUid;

        if (!onMap
            || transform.Anchored
            || _physicsQuery.TryComp(entity, out var physics)
            && physics.BodyType == BodyType.Static)
        {
            SleepBody((entity, entity));
            return;
        }

        WakeBody((entity, entity));
    }
}
