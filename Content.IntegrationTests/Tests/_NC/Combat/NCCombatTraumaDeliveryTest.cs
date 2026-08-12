// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Combat.Armor;
using Content.Server._NC.Combat.Damage;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Armor;
using Content.Shared._NC.Combat.Damage;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Inventory;
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
}
