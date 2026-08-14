// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Combat.Armor;
using Content.Server._NC.Combat.Damage;
using Content.Server._NC.Combat.Projectiles;
using Content.Server._NC.Medical.Physiology;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Armor;
using Content.Shared._NC.Combat.Damage;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Inventory;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Combat;

[TestFixture]
[TestOf(typeof(NCCombatTraumaDeliverySystem))]
public sealed class NCCombatTraumaDeliveryTest : GameTest
{
    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;
    [SidedDependency(Side.Server)] private DamageableSystem _damageable = default!;
    [SidedDependency(Side.Server)] private InventorySystem _inventory = default!;
    [SidedDependency(Side.Server)] private NCArmorResolutionSystem _armor = default!;
    [SidedDependency(Side.Server)] private NCCombatTraumaDeliverySystem _delivery = default!;
    [SidedDependency(Side.Server)] private NCProjectileVolleySystem _volleys = default!;
    [SidedDependency(Side.Server)] private NCPhysiologySchedulerSystem _physiology = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void AcceptedImpactAblatesOnceCreatesOneWoundAndLeavesLegacyHpUntouched()
    {
        var body = SSpawn("MobHuman");
        var armor = SSpawn("NCArmorTestLightArmorjack");
        Assert.That(_inventory.TryEquip(body, armor, "outerClothing"), Is.True);

        ProtoId<DamageTypePrototype> piercingId = "Piercing";
        var damage = new DamageSpecifier(_prototypes.Index(piercingId), 15);
        var impact = new NCResolvedCombatImpact(
            new NCActionResolutionId(Guid.Parse("8e47721c-8e1e-4fb4-bf3c-a450de8ac240")),
            0,
            0,
            0x5EEDUL,
            body,
            null,
            null,
            null,
            body,
            NCTargetingMode.SelectedZone,
            "NCZoneChest",
            "NCBallisticStandard",
            damage,
            Vector2.Zero,
            Vector2.UnitX,
            0f,
            TimeSpan.FromSeconds(1));

#pragma warning disable CS0618 // Compatibility HP is observed only to prove NC delivery did not mutate it.
        var hpBefore = _damageable.GetTotalDamage(body);
#pragma warning restore CS0618

        Assert.That(_delivery.TryDeliver(impact, out var first), Is.True);
        var wounds = SComp<NCWoundContainerComponent>(body);
        Assert.Multiple(() =>
        {
            Assert.That(first.Applied, Is.True);
            Assert.That(first.ActualZone.Id, Is.EqualTo("NCZoneChest"));
            Assert.That(first.LocationSource, Is.EqualTo(NCHitLocationSource.Selected));
            Assert.That(first.Armor.EffectiveSp, Is.EqualTo(FixedPoint2.New(11)));
            Assert.That(first.Armor.PostArmorDamage, Is.EqualTo(FixedPoint2.New(4)));
            Assert.That(wounds.Wounds, Has.Count.EqualTo(1));
            Assert.That(_armor.TryGetCurrentSp(armor, "NCArmorSectionLightArmorjackBody", out var currentSp), Is.True);
            Assert.That(currentSp, Is.EqualTo(FixedPoint2.New(10)));
        });

        // A duplicate action key is owned by medicine but cannot create a second wound or ablate twice.
        Assert.That(_delivery.TryDeliver(impact, out var duplicate), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(duplicate.Applied, Is.False);
            Assert.That(wounds.Wounds, Has.Count.EqualTo(1));
            Assert.That(_armor.TryGetCurrentSp(armor, "NCArmorSectionLightArmorjackBody", out var currentSp), Is.True);
            Assert.That(currentSp, Is.EqualTo(FixedPoint2.New(10)));
        });

#pragma warning disable CS0618 // Compatibility HP is observed only to prove NC delivery did not mutate it.
        Assert.That(_damageable.GetTotalDamage(body), Is.EqualTo(hpBefore));
#pragma warning restore CS0618
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void AutomaticPhysicalContactsResolveConfiguredAnatomicalFootprint()
    {
        var body = SSpawn("MobHuman");
        ProtoId<DamageTypePrototype> piercingId = "Piercing";
        var damage = new DamageSpecifier(_prototypes.Index(piercingId), 1);
        var contacts = new (Vector2 LocalHit, string ExpectedZone)[]
        {
            (new Vector2(0f, 0.35f), "NCZoneEyes"),
            (new Vector2(-0.4f, 0.1f), "NCZoneArmRight"),
            (new Vector2(0.2f, -0.45f), "NCZoneFootLeft"),
        };

        for (var i = 0; i < contacts.Length; i++)
        {
            var impact = new NCResolvedCombatImpact(
                new NCActionResolutionId(new Guid(100 + i, 0, 0, new byte[8])),
                (uint) i,
                0,
                (ulong) i + 1UL,
                body,
                null,
                null,
                null,
                null,
                NCTargetingMode.Automatic,
                null,
                "NCBallisticStandard",
                damage,
                contacts[i].LocalHit,
                Vector2.UnitY,
                0f,
                TimeSpan.FromSeconds(1))
            {
                HasPhysicalHitPosition = true,
            };

            Assert.That(_delivery.TryDeliver(impact, out var delivered), Is.True, contacts[i].ExpectedZone);
            Assert.Multiple(() =>
            {
                Assert.That(delivered.ActualZone.Id, Is.EqualTo(contacts[i].ExpectedZone));
                Assert.That(delivered.LocationSource, Is.EqualTo(NCHitLocationSource.Collision));
            });
        }
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ProjectileFateCreatesStableForeignBodyOrLinkedExitWounds()
    {
        ProtoId<DamageTypePrototype> piercingId = "Piercing";

        var embeddedBody = SSpawn("MobHuman");
        var embeddedImpact = CreateSelectedImpact(
            embeddedBody,
            Guid.Parse("cb63c957-ea59-4c84-a3e4-b9b598ca2aa3"),
            new DamageSpecifier(_prototypes.Index(piercingId), 8));
        Assert.That(_delivery.TryDeliver(embeddedImpact, out var embeddedDelivery), Is.True);
        var embeddedWounds = SComp<NCWoundContainerComponent>(embeddedBody).Wounds.Values.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(embeddedDelivery.CanOverpenetrateBody, Is.False);
            Assert.That(embeddedWounds, Has.Length.EqualTo(1));
            Assert.That(embeddedWounds[0].EmbeddedObjectId, Is.Not.Null);
            Assert.That(embeddedWounds[0].ExitWoundId, Is.Null);
        });

        var exitBody = SSpawn("MobHuman");
        var exitImpact = CreateSelectedImpact(
            exitBody,
            Guid.Parse("68fc98ad-daaa-4057-a7c3-655a0dbd5900"),
            new DamageSpecifier(_prototypes.Index(piercingId), 16));
        Assert.That(_delivery.TryDeliver(exitImpact, out var exitDelivery), Is.True);
        var exitWounds = SComp<NCWoundContainerComponent>(exitBody).Wounds.Values.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(exitDelivery.CanOverpenetrateBody, Is.True);
            Assert.That(exitWounds, Has.Length.EqualTo(2));
            Assert.That(exitWounds, Has.All.Matches<NCWoundRecord>(wound => wound.EmbeddedObjectId == null));
            Assert.That(exitWounds[0].ExitWoundId, Is.EqualTo(exitWounds[1].WoundId));
            Assert.That(exitWounds[1].ExitWoundId, Is.EqualTo(exitWounds[0].WoundId));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void CompoundVolleyUsesOnePrimaryZoneAndConfiguredNeighbours()
    {
        var body = SSpawn("MobHuman");
        var action = new NCActionResolutionId(Guid.Parse("62eb0789-4a74-4125-842e-d628af8f28ff"));
        _volleys.RecordPrimary(body, action, "NCZoneChest");

        Assert.Multiple(() =>
        {
            Assert.That(_volleys.TryGetDistributedZone(body, action, 0, out var primary), Is.True);
            Assert.That(primary.Id, Is.EqualTo("NCZoneChest"));
            Assert.That(_volleys.TryGetDistributedZone(body, action, 1, out var firstNeighbor), Is.True);
            Assert.That(firstNeighbor.Id, Is.EqualTo("NCZoneAbdomen"));
            Assert.That(_volleys.TryGetDistributedZone(body, action, 3, out var secondNeighbor), Is.True);
            Assert.That(secondNeighbor.Id, Is.EqualTo("NCZoneHead"));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void OneStandardPistolHitDoesNotCollapseAnOtherwiseHealthyHuman()
    {
        string[] zones =
        {
            "NCZoneHead",
            "NCZoneEyes",
            "NCZoneMouth",
            "NCZoneChest",
            "NCZoneAbdomen",
            "NCZoneGroin",
            "NCZoneArmLeft",
            "NCZoneArmRight",
            "NCZoneHandLeft",
            "NCZoneHandRight",
            "NCZoneLegLeft",
            "NCZoneLegRight",
            "NCZoneFootLeft",
            "NCZoneFootRight",
        };
        ProtoId<DamageTypePrototype> piercingId = "Piercing";
        var damage = new DamageSpecifier(_prototypes.Index(piercingId), 16);

        for (var i = 0; i < zones.Length; i++)
        {
            var body = SSpawn("MobHuman");
            Assert.That(_physiology.ProcessNow(body, TimeSpan.Zero), Is.True, $"healthy baseline for {zones[i]}");

            var impact = new NCResolvedCombatImpact(
                new NCActionResolutionId(new Guid(i + 1, 0, 0, new byte[8])),
                0,
                0,
                (ulong) i + 1UL,
                body,
                null,
                null,
                null,
                body,
                NCTargetingMode.SelectedZone,
                zones[i],
                "NCBallisticStandard",
                damage,
                Vector2.Zero,
                Vector2.UnitX,
                0f,
                TimeSpan.FromSeconds(1));

            Assert.That(_delivery.TryDeliver(impact, out var delivered), Is.True, zones[i]);
            Assert.That(delivered.Applied, Is.True, zones[i]);
            Assert.That(_physiology.ProcessNow(body, TimeSpan.Zero), Is.True, zones[i]);

            var state = SComp<MobStateComponent>(body).CurrentState;
            var physiology = SComp<NCPhysiologyComponent>(body);
            Assert.That(
                state,
                Is.EqualTo(MobState.Alive),
                $"{zones[i]} resolved as {physiology.Condition}/{physiology.Consciousness}, " +
                $"shock={physiology.Shock}, oxygenation={physiology.Oxygenation}");
        }
    }

    private static NCResolvedCombatImpact CreateSelectedImpact(
        EntityUid body,
        Guid action,
        DamageSpecifier damage)
    {
        return new NCResolvedCombatImpact(
            new NCActionResolutionId(action),
            0,
            0,
            0xA11CEUL,
            body,
            null,
            null,
            null,
            body,
            NCTargetingMode.SelectedZone,
            "NCZoneChest",
            "NCBallisticStandard",
            damage,
            Vector2.Zero,
            Vector2.UnitX,
            0f,
            TimeSpan.FromSeconds(1));
    }
}
