/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Shared.Damage.Systems;

namespace Content.Shared._NC.ZLevels.Damage.FallingDamage;

public sealed partial class NCFallingDamageSystem : EntitySystem
{
    [Dependency] private DamageableSystem _damageable = default!;
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCFallingDamageComponent, NCZFellOnMeEvent>(OnFallOnMe);
    }

    private void OnFallOnMe(Entity<NCFallingDamageComponent> ent, ref NCZFellOnMeEvent args)
    {
        _damageable.TryChangeDamage(args.Fallen, ent.Comp.Damage * args.Speed);
    }
}
