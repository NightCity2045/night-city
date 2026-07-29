using Content.Shared._NC.ZLevels.Core.Components;
using Content.Shared._NC.ZLevels.Core.EntitySystems;
using Content.Shared.Throwing;

namespace Content.Shared._NC.ZLevels.Throwing;

public sealed partial class NCZLevelThrowingSystem : EntitySystem
{
    [Dependency] private NCSharedZLevelsSystem _zLevels = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCZPhysicsComponent, ThrownEvent>(OnThrown);
    }

    private void OnThrown(Entity<NCZPhysicsComponent> ent, ref ThrownEvent args)
    {
        if (!TryComp<ThrownItemComponent>(ent, out var thrown)
            || thrown.LandTime is not { } landTime
            || thrown.ThrownTime is not { } thrownTime)
            return;

        var flyTime = (float)(landTime - thrownTime).TotalSeconds;
        if (flyTime <= 0f)
            return;

        var distToGround = ent.Comp.LocalPosition - ent.Comp.CachedGroundHeight;
        var v0 = MathF.Max(0f, (0.5f * NCSharedZLevelsSystem.ZGravityForce * flyTime - distToGround / flyTime) * 2f);
        _zLevels.SetZVelocity((ent.Owner, ent.Comp), v0);
    }
}
