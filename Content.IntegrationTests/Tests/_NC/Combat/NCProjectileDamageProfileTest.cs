// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Combat.Projectiles;
using Content.Server._NC.Medical.Physiology;
using Content.Server._NC.Medical.Systemic;
using Content.Server.Stunnable.Components;
using Content.Shared._NC.Combat.Damage;
using Content.Shared._NC.Combat.Projectiles;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Systemic;
using Content.Shared._NC.Medical.Trauma;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Prototypes;
using Content.Shared.FixedPoint;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Projectiles;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Combat;

[TestFixture]
[TestOf(typeof(NCProjectileContextSystem))]
public sealed class NCProjectileDamageProfileTest : GameTest
{
    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;
    [SidedDependency(Side.Server)] private NCProjectileContextSystem _contexts = default!;
    [SidedDependency(Side.Server)] private NCProjectileImpactSystem _impacts = default!;
    [SidedDependency(Side.Server)] private NCPhysiologySchedulerSystem _physiology = default!;
    [SidedDependency(Side.Server)] private NCSystemicSystem _systemic = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void DefaultMapClassifiesActualProjectilePayloads()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ResolveSpawned("BulletPistol").Id, Is.EqualTo("NCBallisticStandard"));
            Assert.That(ResolveSpawned("BulletLaser").Id, Is.EqualTo("NCProjectileThermalStandard"));
            Assert.That(ResolveSpawned("BulletAcid").Id, Is.EqualTo("NCProjectileChemicalStandard"));
        });

        var syntheticShock = SSpawn(null);
        var projectile = SEntMan.EnsureComponent<ProjectileComponent>(syntheticShock);
        projectile.Damage = MakeDamage("Shock", 8);
        Assert.That(_contexts.TryResolveDamageProfile(syntheticShock, projectile.Damage, out var electrical), Is.True);
        Assert.That(electrical.Id, Is.EqualTo("NCProjectileElectricalStandard"));

        // Equal positive entries use stable lexical ordering and never depend on dictionary insertion order.
        projectile.Damage = MakeDamage("Heat", 8);
        projectile.Damage.DamageDict["Caustic"] = FixedPoint2.New(8);
        Assert.That(_contexts.TryResolveDamageProfile(syntheticShock, projectile.Damage, out var tied), Is.True);
        Assert.That(tied.Id, Is.EqualTo("NCProjectileChemicalStandard"));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ExactAmmunitionOverridesBeatDominantFallback()
    {
        var armorPiercing = SSpawn("BaseBulletAP");
        var armorPiercingDamage = SComp<ProjectileComponent>(armorPiercing).Damage;
        Assert.That(_contexts.TryResolveDamageProfile(armorPiercing, armorPiercingDamage, out var ap), Is.True);

        var incendiary = SSpawn("BaseBulletIncendiary");
        var incendiaryDamage = SComp<ProjectileComponent>(incendiary).Damage;
        Assert.That(_contexts.TryResolveDamageProfile(incendiary, incendiaryDamage, out var fire), Is.True);

        var expansive = SSpawn("NCBulletPistolExpansive");
        var expansiveDamage = SComp<ProjectileComponent>(expansive).Damage;
        Assert.That(_contexts.TryResolveDamageProfile(expansive, expansiveDamage, out var softPoint), Is.True);

        var emp = SSpawn("BulletGrenadeEMP");
        var empDamage = SComp<ProjectileComponent>(emp).Damage;
        Assert.That(_contexts.TryResolveDamageProfile(emp, empDamage, out var empUtility), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(SEntMan.HasComponent<NCProjectileDamageProfileComponent>(armorPiercing), Is.True);
            Assert.That(ap.Id, Is.EqualTo("NCBallisticArmorPiercing"));
            Assert.That(SEntMan.HasComponent<NCProjectileDamageProfileComponent>(incendiary), Is.True);
            Assert.That(fire.Id, Is.EqualTo("NCBallisticIncendiary"));
            Assert.That(softPoint.Id, Is.EqualTo("NCBallisticExpansive"));
            Assert.That(empUtility.Id, Is.EqualTo("NCProjectileEmpUtility"));
            Assert.That(_prototypes.Index(empUtility).SuppressLocalTrauma, Is.True);
            Assert.That(
                _prototypes.Index(_prototypes.Index(softPoint).TraumaProfile).WoundPrototype?.Id,
                Is.EqualTo("NCWoundBallisticExpansive"));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ZeroDamageUtilityProjectilesKeepVanillaCollisionEffectsWithoutTraumaProfile()
    {
        var taser = SSpawn("BulletTaser");
        var taserDamage = SComp<ProjectileComponent>(taser).Damage;
        var disabler = SSpawn("BulletDisabler");
        var disablerDamage = SComp<ProjectileComponent>(disabler).Damage;

        Assert.Multiple(() =>
        {
            Assert.That(_contexts.TryResolveDamageProfile(taser, taserDamage, out _), Is.False);
            Assert.That(_contexts.TryResolveDamageProfile(disabler, disablerDamage, out _), Is.False);
            Assert.That(SEntMan.HasComponent<NCProjectileDamageProfileComponent>(taser), Is.False);
            Assert.That(SEntMan.HasComponent<NCProjectileDamageProfileComponent>(disabler), Is.False);
            Assert.That(SEntMan.HasComponent<StunOnCollideComponent>(taser), Is.True);
            Assert.That(SEntMan.HasComponent<StaminaDamageOnCollideComponent>(disabler), Is.True);
        });

        AttachShotContext(taser, disabler);
        Assert.Multiple(() =>
        {
            Assert.That(SEntMan.HasComponent<NCProjectileHitContextComponent>(taser), Is.False);
            Assert.That(SEntMan.HasComponent<NCProjectileHitContextComponent>(disabler), Is.False);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void RadiationProjectileNeverFallsBackToBallistics()
    {
        var uranium = SSpawn("BaseBulletUranium");
        var projectile = SComp<ProjectileComponent>(uranium);
        var damage = projectile.Damage;

        Assert.Multiple(() =>
        {
            Assert.That(damage.DamageDict["Radiation"], Is.GreaterThan(FixedPoint2.Zero));
            Assert.That(SEntMan.HasComponent<NCProjectileDamageProfileComponent>(uranium), Is.False);
            Assert.That(_contexts.TryResolveDamageProfile(uranium, damage, out _), Is.False);
        });

        // Systemic-only ammunition still keeps its physical shot correlation and never acquires a fake ballistic profile.
        AttachShotContext(uranium);
        Assert.That(SEntMan.HasComponent<NCProjectileHitContextComponent>(uranium), Is.True);

        var body = SSpawn("MobHuman");
        Assert.That(_impacts.TryHandleImpact(
            (uranium, projectile),
            body,
            damage,
            Vector2.UnitX,
            out var feedback,
            out var spent), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(feedback.GetTotal(), Is.EqualTo(FixedPoint2.Zero));
            Assert.That(spent, Is.True);
            Assert.That(
                !SEntMan.TryGetComponent<NCWoundContainerComponent>(body, out var wounds) || wounds.Wounds.Count == 0,
                Is.True);
            Assert.That(_systemic.GetBurden(body, NCSystemicChannel.Radiation), Is.EqualTo(11f));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void RepeatedPhysicalContactsFromOneProjectileApplySystemicPayloadPerImpact()
    {
        var uranium = SSpawn("BaseBulletUranium");
        var projectile = SComp<ProjectileComponent>(uranium);
        AttachShotContext(uranium);
        var body = SSpawn("MobHuman");

        Assert.That(_impacts.TryHandleImpact(
            (uranium, projectile),
            body,
            projectile.Damage,
            Vector2.UnitX,
            out _,
            out _), Is.True);
        Assert.That(_impacts.TryHandleImpact(
            (uranium, projectile),
            body,
            projectile.Damage,
            Vector2.UnitX,
            out _,
            out _), Is.True);

        Assert.That(_systemic.GetBurden(body, NCSystemicChannel.Radiation), Is.EqualTo(22f));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void StandardPistolPhysicalImpactDoesNotCollapseHealthyHuman()
    {
        var projectileUid = SSpawn("BulletPistol");
        var projectile = SComp<ProjectileComponent>(projectileUid);
        AttachShotContext(projectileUid);
        var body = SSpawn("MobHuman");
        Assert.That(_physiology.ProcessNow(body, TimeSpan.Zero), Is.True);

        Assert.That(_impacts.TryHandleImpact(
            (projectileUid, projectile),
            body,
            projectile.Damage,
            Vector2.UnitX,
            out _,
            out _), Is.True);
        Assert.That(_physiology.ProcessNow(body, TimeSpan.Zero), Is.True);

        var physiology = SComp<NCPhysiologyComponent>(body);
        Assert.That(
            SComp<MobStateComponent>(body).CurrentState,
            Is.EqualTo(MobState.Alive),
            $".35 auto impact resolved as {physiology.Condition}/{physiology.Consciousness}, " +
            $"shock={physiology.Shock}, oxygenation={physiology.Oxygenation}");
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void MixedProjectileRoutesOnlyHeatLocallyAndRadiationSystemically()
    {
        var projectileUid = SSpawn(null);
        var projectile = SEntMan.EnsureComponent<ProjectileComponent>(projectileUid);
        projectile.Damage = MakeDamage("Heat", 8);
        projectile.Damage.DamageDict["Radiation"] = FixedPoint2.New(50);
        AttachShotContext(projectileUid);

        var body = SSpawn("MobHuman");
        Assert.That(_impacts.TryHandleImpact(
            (projectileUid, projectile),
            body,
            projectile.Damage,
            Vector2.UnitX,
            out var feedback,
            out _), Is.True);

        var wounds = SComp<NCWoundContainerComponent>(body).Wounds.Values.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(wounds, Has.Length.EqualTo(1));
            Assert.That(wounds[0].WoundType, Is.EqualTo(NCWoundType.Thermal));
            Assert.That(wounds[0].IntegrityDamage, Is.EqualTo(FixedPoint2.New(8)));
            Assert.That(feedback.GetTotal(), Is.EqualTo(FixedPoint2.New(8)));
            Assert.That(_systemic.GetBurden(body, NCSystemicChannel.Radiation), Is.EqualTo(50f));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void MapAndChemicalWoundAreCompleteData()
    {
        var defaults = _prototypes.EnumeratePrototypes<NCProjectileDamageProfileMapPrototype>()
            .Where(candidate => candidate.DefaultForHumans)
            .ToArray();
        ProtoId<NCCombatDamageProfilePrototype> chemicalProfileId = "NCProjectileChemicalStandard";
        ProtoId<NCWoundSystemProfilePrototype> woundSystemId = "NCHumanWoundSystem";

        Assert.That(defaults, Has.Length.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(defaults[0].Profiles.Keys.Select(key => key.Id), Is.EquivalentTo(new[]
            {
                "Blunt",
                "Slash",
                "Piercing",
                "Heat",
                "Cold",
                "Shock",
                "Caustic",
            }));
            Assert.That(defaults[0].Profiles.Values.All(_prototypes.HasIndex), Is.True);
            Assert.That(_prototypes.Index(chemicalProfileId).TraumaProfile.Id, Is.EqualTo("ChemicalImpact"));
            Assert.That(_prototypes.Index(woundSystemId).Wounds[NCTraumaMechanism.Chemical].Id,
                Is.EqualTo("NCWoundChemical"));
        });
    }

    private ProtoId<NCCombatDamageProfilePrototype> ResolveSpawned(string prototype)
    {
        var projectile = SSpawn(prototype);
        var damage = SComp<ProjectileComponent>(projectile).Damage;
        Assert.That(_contexts.TryResolveDamageProfile(projectile, damage, out var profile), Is.True);
        return profile;
    }

    private DamageSpecifier MakeDamage(string damageType, int amount)
    {
        return new DamageSpecifier(
            _prototypes.Index<DamageTypePrototype>(damageType),
            FixedPoint2.New(amount));
    }

    private void AttachShotContext(params EntityUid[] projectiles)
    {
        var gun = SSpawn(null);
        var gunComponent = SEntMan.EnsureComponent<GunComponent>(gun);
        _contexts.PrepareShot((gun, gunComponent), null);
        SEntMan.EventBus.RaiseLocalEvent(gun, new AmmoShotEvent
        {
            FiredProjectiles = projectiles.ToList(),
        });
    }
}
