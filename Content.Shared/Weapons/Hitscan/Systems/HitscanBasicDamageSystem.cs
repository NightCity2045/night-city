using Content.Shared.Damage.Systems;
// NC start - Authoritative human hitscan trauma adapter.
using Content.Shared._NC.Combat.Damage;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared.Damage;
// NC end
using Content.Shared.Weapons.Hitscan.Components;
using Content.Shared.Weapons.Hitscan.Events;

namespace Content.Shared.Weapons.Hitscan.Systems;

public sealed partial class HitscanBasicDamageSystem : EntitySystem
{
    [Dependency] private DamageableSystem _damage = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<HitscanBasicDamageComponent, HitscanRaycastFiredEvent>(OnHitscanHit);
    }

    private void OnHitscanHit(Entity<HitscanBasicDamageComponent> ent, ref HitscanRaycastFiredEvent args)
    {
        if (args.Data.HitEntity == null)
            return;

        var dmg = ent.Comp.Damage * _damage.UniversalHitscanDamageModifier;

        // NC start - The raycast remains the only hit test; NC humans never receive duplicate legacy HP damage.
        DamageSpecifier damageDealt;
        if (HasComp<NCMedicalBodyComponent>(args.Data.HitEntity.Value))
        {
            var ncTrauma = new NCHitscanTraumaRequestedEvent(ent.Owner, args.Data, dmg);
            RaiseLocalEvent(args.Data.HitEntity.Value, ref ncTrauma);
            damageDealt = ncTrauma.FeedbackDamage ?? new DamageSpecifier(dmg);
        }
        else if (!_damage.TryChangeDamage(args.Data.HitEntity.Value, dmg, out damageDealt, origin: args.Data.Gun))
        {
            return;
        }
        // NC end

        var damageEvent = new HitscanDamageDealtEvent
        {
            Target = args.Data.HitEntity.Value,
            DamageDealt = damageDealt,
        };

        RaiseLocalEvent(ent, ref damageEvent);
    }
}
