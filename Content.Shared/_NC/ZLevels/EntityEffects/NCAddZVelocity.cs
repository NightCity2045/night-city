/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Shared._NC.ZLevels.Core.Components;
using Content.Shared._NC.ZLevels.Core.EntitySystems;
using Content.Shared.EntityEffects;

namespace Content.Shared._NC.ZLevels.EntityEffects;

/// <summary>
/// Data-driven entity effect that changes vertical velocity.
/// </summary>
public sealed partial class NCAddZVelocity : EntityEffectBase<NCAddZVelocity>
{
    [DataField(required: true)]
    public float Speed;

    [DataField]
    public bool RequiresGround;
}

/// <summary>
/// Applies <see cref="NCAddZVelocity"/> through the shared predicted Z-level system.
/// </summary>
public sealed partial class NCAddZVelocitySystem : EntityEffectSystem<NCZPhysicsComponent, NCAddZVelocity>
{
    [Dependency] private NCSharedZLevelsSystem _zLevels = default!;

    protected override void Effect(Entity<NCZPhysicsComponent> entity, ref EntityEffectEvent<NCAddZVelocity> args)
    {
        var zPhysics = (entity.Owner, (NCZPhysicsComponent?) entity.Comp);
        if (args.Effect.RequiresGround && _zLevels.DistanceToGround(zPhysics) > 0.1f)
            return;

        _zLevels.AddZVelocity(zPhysics, args.Effect.Speed * args.Scale);
    }
}
