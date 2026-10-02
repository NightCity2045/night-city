// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Combat.Armor;
using Content.Server._NC.Combat.Explosion;
using Content.Server._NC.Medical.Systemic;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Explosion;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Systemic;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Inventory;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Combat;

[TestFixture]
[TestOf(typeof(NCExplosionMedicalAdapterSystem))]
public sealed class NCExplosionMedicalAdapterTest : GameTest
{
    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;
    [SidedDependency(Side.Server)] private SharedTransformSystem _transform = default!;
    [SidedDependency(Side.Server)] private DamageableSystem _damageable = default!;
    [SidedDependency(Side.Server)] private InventorySystem _inventory = default!;
    [SidedDependency(Side.Server)] private NCAnatomySystem _anatomy = default!;
    [SidedDependency(Side.Server)] private NCArmorResolutionSystem _armor = default!;
    [SidedDependency(Side.Server)] private NCExplosionMedicalAdapterSystem _adapter = default!;
    [SidedDependency(Side.Server)] private NCSystemicSystem _systemic = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void DistributedExplosionCreatesBoundedMechanismSpecificWoundsWithoutLegacyHp()
    {
        var body = SSpawn("MobHuman");
        var source = SSpawn(null);
        var resolutionId = new NCActionResolutionId(Guid.Parse("f67f58dc-c790-4f2e-95a4-352ee1549dad"));
        var damage = MakeExplosionDamage(20);
        damage.DamageDict["Radiation"] = FixedPoint2.New(50);

#pragma warning disable CS0618 // Compatibility HP is observed only to prove the authoritative boundary is intact.
        var legacyBefore = _damageable.GetTotalDamage(body);
#pragma warning restore CS0618

        Assert.That(_adapter.TryHandleExplosionDamage(
            body,
            _transform.GetMapCoordinates(source),
            damage,
            "Default",
            source,
            resolutionId), Is.True);

        var wounds = SComp<NCWoundContainerComponent>(body).Wounds.Values.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(wounds, Has.Length.EqualTo(6));
            Assert.That(wounds.Count(wound => wound.WoundType == NCWoundType.Blast), Is.EqualTo(3));
            Assert.That(wounds.Count(wound => wound.WoundType == NCWoundType.Ballistic), Is.EqualTo(2));
            Assert.That(wounds.Count(wound => wound.WoundType == NCWoundType.Thermal), Is.EqualTo(1));
            Assert.That(wounds.Select(wound => wound.ForensicResolutionId),
                Is.All.EqualTo(resolutionId.Value));
            Assert.That(wounds.Select(wound => wound.TargetZone).Distinct().Count(),
                Is.GreaterThanOrEqualTo(3));
            Assert.That(_systemic.GetBurden(body, NCSystemicChannel.Radiation), Is.EqualTo(50f));
#pragma warning disable CS0618 // Compatibility HP is observed only to prove the authoritative boundary is intact.
            Assert.That(_damageable.GetTotalDamage(body), Is.EqualTo(legacyBefore));
#pragma warning restore CS0618
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void OneExplosionAblatesTheSameArmorSectionAtMostOnce()
    {
        var body = SSpawn("MobHuman");
        var source = SSpawn(null);
        var armor = SSpawn("NCArmorTestLightArmorjack");
        Assert.That(_inventory.TryEquip(body, armor, "armor"), Is.True);

        var resolutionId = FindResolutionForCoveredPrimary(body);
        Assert.That(_adapter.TryHandleExplosionDamage(
            body,
            _transform.GetMapCoordinates(source),
            MakeExplosionDamage(40),
            "Default",
            source,
            resolutionId), Is.True);

        Assert.That(_armor.TryGetCurrentSp(
            armor,
            "NCArmorSectionLightArmorjackBody",
            out var currentSp), Is.True);
        Assert.That(currentSp, Is.EqualTo(FixedPoint2.New(10)));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ExplosionProfilesAreFiniteBoundedAndFullyResolvable()
    {
        var profiles = _prototypes.EnumeratePrototypes<NCExplosionMedicalProfilePrototype>().ToArray();
        Assert.That(profiles.Count(profile => profile.DefaultForHumans), Is.EqualTo(1));

        var assignedExplosions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            Assert.Multiple(() =>
            {
                Assert.That(profile.MaxAblationApplicationsPerSection, Is.GreaterThan(0), profile.ID);
                AssertImpactValid(profile.PrimaryImpact, profile.ID);
                Assert.That(profile.PrimaryImpact.Count, Is.EqualTo(1), profile.ID);
            });

            foreach (var impact in profile.SecondaryImpacts)
            {
                AssertImpactValid(impact, profile.ID);
                Assert.That(impact.Count, Is.GreaterThan(0), profile.ID);
            }

            foreach (var explosion in profile.ExplosionPrototypes)
                Assert.That(assignedExplosions.Add(explosion.Id), Is.True, explosion.Id);
        }
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void NonMedicalEntitiesRemainOwnedByVanillaExplosionDamage()
    {
        var nonMedical = SSpawn("MobMonkey");
        Assert.That(_adapter.TryHandleExplosionDamage(
            nonMedical,
            _transform.GetMapCoordinates(nonMedical),
            MakeExplosionDamage(10),
            "Default",
            null), Is.False);
    }

    private NCActionResolutionId FindResolutionForCoveredPrimary(EntityUid body)
    {
        var medical = SComp<NCMedicalBodyComponent>(body);
        var covered = new HashSet<string>(StringComparer.Ordinal)
        {
            "NCZoneChest",
            "NCZoneAbdomen",
            "NCZoneGroin",
            "NCZoneArmLeft",
            "NCZoneArmRight",
            "NCZoneLegLeft",
            "NCZoneLegRight",
        };

        for (var value = 1; value <= 128; value++)
        {
            var candidate = new NCActionResolutionId(new Guid(value, 0, 0, new byte[8]));
            if (_anatomy.TryResolveAutomaticZone(
                    (body, medical),
                    candidate.DeriveSeed(),
                    out var zone,
                    out _) &&
                covered.Contains(zone.Id))
            {
                return candidate;
            }
        }

        Assert.Fail("Could not find a deterministic covered primary zone within the bounded test search.");
        return default;
    }

    private DamageSpecifier MakeExplosionDamage(int amount)
    {
        var damage = new DamageSpecifier();
        damage.DamageDict["Blunt"] = FixedPoint2.New(amount);
        damage.DamageDict["Piercing"] = FixedPoint2.New(amount);
        damage.DamageDict["Heat"] = FixedPoint2.New(amount);
        damage.DamageDict["Structural"] = FixedPoint2.New(1000);
        return damage;
    }

    private void AssertImpactValid(NCExplosionImpactDefinition impact, string profile)
    {
        Assert.Multiple(() =>
        {
            Assert.That(_prototypes.HasIndex(impact.DamageProfile), Is.True, profile);
            Assert.That(impact.SourceDamageTypes, Is.Not.Empty, profile);
            Assert.That(impact.SourceDamageTypes, Is.Unique, profile);
            Assert.That(impact.SourceDamageTypes.All(_prototypes.HasIndex), Is.True, profile);
            Assert.That(float.IsFinite(impact.DamageMultiplier), Is.True, profile);
            Assert.That(impact.DamageMultiplier, Is.GreaterThan(0f), profile);
        });
    }
}
