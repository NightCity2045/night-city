using Content.Shared._NC.ZLevels.Core.EntitySystems;
using Content.Shared.Movement.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;

namespace Content.Shared.Movement.Systems;

/// <summary>
/// Applies an occlusion shader for any relevant entities.
/// </summary>
public abstract class SharedFloorOcclusionSystem : EntitySystem
{
    [Dependency] private SharedPhysicsSystem _physics = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<FloorOccluderComponent, StartCollideEvent>(OnStartCollide);
        SubscribeLocalEvent<FloorOccluderComponent, EndCollideEvent>(OnEndCollide);
        SubscribeLocalEvent<FloorOcclusionComponent, NCZBodyStatusChangedEvent>(OnBodyStatusChanged);
    }

    private void OnStartCollide(Entity<FloorOccluderComponent> entity, ref StartCollideEvent args)
    {
        var other = args.OtherEntity;

        if (!TryComp<FloorOcclusionComponent>(other, out var occlusion) ||
            occlusion.Colliding.Contains(entity.Owner))
        {
            return;
        }

        // Airborne entities are above floor occluders and must remain visible.
        if (TryComp<PhysicsComponent>(other, out var physics) && physics.BodyStatus == BodyStatus.InAir)
            return;

        occlusion.Colliding.Add(entity.Owner);
        Dirty(other, occlusion);
        SetEnabled((other, occlusion));
    }

    private void OnBodyStatusChanged(Entity<FloorOcclusionComponent> entity, ref NCZBodyStatusChangedEvent args)
    {
        if (args.NewStatus == BodyStatus.InAir)
        {
            if (entity.Comp.Colliding.Count == 0)
                return;

            entity.Comp.Colliding.Clear();
            Dirty(entity);
            SetEnabled(entity);
            return;
        }

        // Rebuild contacts when the entity lands inside an occluder.
        var changed = false;
        foreach (var contacting in _physics.GetContactingEntities(entity))
        {
            if (!HasComp<FloorOccluderComponent>(contacting) || entity.Comp.Colliding.Contains(contacting))
                continue;

            entity.Comp.Colliding.Add(contacting);
            changed = true;
        }

        if (!changed)
            return;

        Dirty(entity);
        SetEnabled(entity);
    }

    private void OnEndCollide(Entity<FloorOccluderComponent> entity, ref EndCollideEvent args)
    {
        var other = args.OtherEntity;

        if (!TryComp<FloorOcclusionComponent>(other, out var occlusion))
            return;

        if (!occlusion.Colliding.Remove(entity.Owner))
            return;

        Dirty(other, occlusion);
        SetEnabled((other, occlusion));
    }

    protected virtual void SetEnabled(Entity<FloorOcclusionComponent> entity)
    {

    }
}
