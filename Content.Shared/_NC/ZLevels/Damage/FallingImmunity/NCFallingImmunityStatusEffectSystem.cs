/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Shared._NC.ZLevels.Damage;
using Content.Shared.StatusEffectNew;

namespace Content.Shared._NC.ZLevels.Damage.FallingImmunity;

/// <summary>
/// Applies status-effect multipliers to the shared Z-level fall calculation.
/// </summary>
public sealed class NCFallingImmunityStatusEffectSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCFallingImmunityStatusEffectComponent,
            StatusEffectRelayedEvent<NCZFallingDamageCalculateEvent>>(OnFall);
    }

    private void OnFall(
        Entity<NCFallingImmunityStatusEffectComponent> entity,
        ref StatusEffectRelayedEvent<NCZFallingDamageCalculateEvent> args)
    {
        args.Args.DamageMultiplier *= entity.Comp.DamageMultiplier;
        args.Args.StunMultiplier *= entity.Comp.StunMultiplier;
    }
}
