// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Combat.Armor;
using Content.Shared._NC.Combat.Armor;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared.Charges.Components;
using Content.Shared.Charges.Systems;
using System.Collections.Generic;
using Content.Shared.Clothing;
using Content.Shared.Clothing.Components;
using Content.Shared.Examine;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._NC.Combat;

[TestFixture]
[TestOf(typeof(NCArmorRepairSystem))]
public sealed class NCArmorGameplayRolloutTest : GameTest
{
    private static readonly ProtoId<NCTargetZonePrototype> Head = "NCZoneHead";
    private static readonly ProtoId<NCTargetZonePrototype> Chest = "NCZoneChest";
    private static readonly ProtoId<NCTargetZonePrototype> ArmLeft = "NCZoneArmLeft";
    private static readonly ProtoId<NCTargetZonePrototype> HandLeft = "NCZoneHandLeft";
    private static readonly ProtoId<NCTargetZonePrototype> FootLeft = "NCZoneFootLeft";
    private static readonly ProtoId<NCArmorSectionPrototype> ArmorjackSection =
        "NCArmorSectionLightArmorjackBody";

    private static readonly string[] DedicatedArmorSlotPrototypes =
    [
        "ClothingOuterArmorBasic",
        "ClothingOuterArmorBasicSlim",
        "ClothingOuterArmorBulletproof",
        "ClothingOuterArmorReflective",
        "ClothingOuterVestDetective",
        "ClothingOuterArmorCaptainCarapace",
        "ClothingOuterArmorCentcommCarapace",
        "ClothingOuterVestWeb",
        "ClothingOuterVestWebElite",
        "ClothingOuterVestWebMerc",
        "ClothingOuterArmorBiotechBulletproof",
        "ClothingOuterArmorMilitechBulletproof",
        "ClothingOuterArmorNWN",
    ];

    private static readonly string[] LightArmorMobilityPrototypes =
    [
        "ClothingOuterArmorBasic",
        "ClothingOuterArmorBasicSlim",
        "ClothingOuterArmorReflective",
        "ClothingOuterVestDetective",
    ];

    private static readonly string[] HeavyArmorMobilityPrototypes =
    [
        "ClothingOuterArmorBulletproof",
        "ClothingOuterArmorCaptainCarapace",
        "ClothingOuterArmorCentcommCarapace",
        "ClothingOuterVestWeb",
        "ClothingOuterVestWebElite",
        "ClothingOuterVestWebMerc",
        "ClothingOuterArmorBiotechBulletproof",
        "ClothingOuterArmorMilitechBulletproof",
        "ClothingOuterArmorNWN",
    ];

    [SidedDependency(Side.Server)] private NCArmorResolutionSystem _armor = default!;
    [SidedDependency(Side.Server)] private NCArmorRepairSystem _repair = default!;
    [SidedDependency(Side.Server)] private InventorySystem _inventory = default!;
    [SidedDependency(Side.Server)] private SharedHandsSystem _hands = default!;
    [SidedDependency(Side.Server)] private SharedChargesSystem _charges = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void RealNightCityLoadoutArmorProtectsOnlyConfiguredZones()
    {
        var human = SSpawn("MobHuman");
        var vest = SSpawn("ClothingOuterArmorBasic");
        Assert.That(_inventory.TryEquip(human, vest, "armor"), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(Resolve(human, Chest).EffectiveSp, Is.EqualTo(FixedPoint2.New(11)));
            Assert.That(Resolve(human, ArmLeft).EffectiveSp, Is.EqualTo(FixedPoint2.New(11)));
            Assert.That(Resolve(human, Head).HasArmor, Is.False);
            Assert.That(Resolve(human, HandLeft).HasArmor, Is.False);
            Assert.That(Resolve(human, FootLeft).HasArmor, Is.False);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void RealHelmetAndVestRemainIndependentByZone()
    {
        var human = SSpawn("MobHuman");
        var vest = SSpawn("ClothingOuterArmorBasic");
        var helmet = SSpawn("ClothingHeadHelmetBasic");
        Assert.Multiple(() =>
        {
            Assert.That(_inventory.TryEquip(human, vest, "armor"), Is.True);
            Assert.That(_inventory.TryEquip(human, helmet, "head"), Is.True);
        });

        var head = Resolve(human, Head);
        var chest = Resolve(human, Chest);
        Assert.Multiple(() =>
        {
            Assert.That(head.ParticipatingSections, Has.Length.EqualTo(1));
            Assert.That(head.EffectiveSection?.Source, Is.EqualTo(helmet));
            Assert.That(chest.ParticipatingSections, Has.Length.EqualTo(1));
            Assert.That(chest.EffectiveSection?.Source, Is.EqualTo(vest));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ArmorAndOuterwearCanBeEquippedTogether()
    {
        var human = SSpawn("MobHuman");
        var vest = SSpawn("ClothingOuterArmorBasic");
        var secondVest = SSpawn("ClothingOuterArmorBasic");
        var captainCoat = SSpawn("ClothingOuterWinterCap");

        Assert.Multiple(() =>
        {
            Assert.That(_inventory.TryEquip(human, vest, "armor"), Is.True);
            Assert.That(_inventory.TryEquip(human, captainCoat, "outerClothing"), Is.True);
            Assert.That(_inventory.TryEquip(human, secondVest, "armor"), Is.False);
        });

        Assert.Multiple(() =>
        {
            Assert.That(_inventory.TryGetSlotEntity(human, "armor", out var equippedVest), Is.True);
            Assert.That(equippedVest, Is.EqualTo(vest));
            Assert.That(_inventory.TryGetSlotEntity(human, "outerClothing", out var equippedCoat), Is.True);
            Assert.That(equippedCoat, Is.EqualTo(captainCoat));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void DedicatedArmorGarmentsCannotUseTheJacketSlot()
    {
        foreach (var prototype in DedicatedArmorSlotPrototypes)
        {
            var armor = SSpawn(prototype);
            var clothing = SEntMan.GetComponent<ClothingComponent>(armor);

            Assert.Multiple(() =>
            {
                Assert.That(clothing.Slots.HasFlag(SlotFlags.ARMOR), Is.True,
                    $"{prototype} cannot use the dedicated armor slot.");
                Assert.That(clothing.Slots.HasFlag(SlotFlags.OUTERCLOTHING), Is.False,
                    $"{prototype} can still use the jacket slot.");
            });
        }
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ArmorMobilityPenaltiesMatchTheirWeightClass()
    {
        AssertMobilityProfile(LightArmorMobilityPrototypes, 0.95f, 0.90f);
        AssertMobilityProfile(HeavyArmorMobilityPrototypes, 0.90f, 0.80f);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void AblationIsVisibleThroughExactItemExamine()
    {
        var examiner = SSpawn("MobHuman");
        var vest = SSpawn("ClothingOuterArmorBasic");
        Assert.That(_armor.TrySetCurrentSp(vest, ArmorjackSection, 2), Is.True);

        var examined = new ExaminedEvent(new FormattedMessage(), vest, examiner, true, false);
        SEntMan.EventBus.RaiseLocalEvent(vest, examined);
        var markup = examined.GetTotalMessage().ToMarkup();

        Assert.Multiple(() =>
        {
            Assert.That(markup, Does.Contain("critical").IgnoreCase);
            Assert.That(markup, Does.Contain("2/11"));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void FieldRepairIsCappedAndConsumesFiniteCharges()
    {
        var user = SSpawn("MobHuman");
        var vest = SSpawn("ClothingOuterArmorBasic");
        var kit = SSpawn("NCArmorRepairKit");
        Assert.Multiple(() =>
        {
            Assert.That(_hands.TryPickupAnyHand(user, kit, animate: false), Is.True);
            Assert.That(_hands.TryPickupAnyHand(user, vest, animate: false), Is.True);
            Assert.That(_armor.TrySetCurrentSp(vest, ArmorjackSection, 1), Is.True);
        });

        var tool = new Entity<NCArmorRepairToolComponent>(
            kit,
            SEntMan.GetComponent<NCArmorRepairToolComponent>(kit));
        NCArmorRepairResult result = default;
        for (var i = 0; i < 4; i++)
        {
            result = _repair.TryRepairNow(tool, user, vest);
            Assert.That(result.Outcome, Is.EqualTo(NCArmorRepairOutcome.Applied));
        }

        var charges = SEntMan.GetComponent<LimitedChargesComponent>(kit);
        Assert.Multiple(() =>
        {
            Assert.That(result.CurrentSp, Is.EqualTo(FixedPoint2.New(8.25f)));
            Assert.That(_armor.TryGetCurrentSp(vest, ArmorjackSection, out var current), Is.True);
            Assert.That(current, Is.EqualTo(FixedPoint2.New(8.25f)));
            Assert.That(_charges.GetCurrentCharges((kit, charges, null)), Is.Zero);
            Assert.That(_repair.TryRepairNow(tool, user, vest).Outcome,
                Is.EqualTo(NCArmorRepairOutcome.Depleted));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void FieldRepairCannotReviveDestroyedArmorOrSpendAtLimit()
    {
        var user = SSpawn("MobHuman");
        var vest = SSpawn("ClothingOuterArmorBasic");
        var kit = SSpawn("NCArmorRepairKit");
        Assert.Multiple(() =>
        {
            Assert.That(_hands.TryPickupAnyHand(user, kit, animate: false), Is.True);
            Assert.That(_hands.TryPickupAnyHand(user, vest, animate: false), Is.True);
            Assert.That(_armor.TrySetCurrentSp(vest, ArmorjackSection, 0), Is.True);
        });

        var tool = new Entity<NCArmorRepairToolComponent>(
            kit,
            SEntMan.GetComponent<NCArmorRepairToolComponent>(kit));
        var charges = SEntMan.GetComponent<LimitedChargesComponent>(kit);
        var before = _charges.GetCurrentCharges((kit, charges, null));
        var result = _repair.TryRepairNow(tool, user, vest);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(NCArmorRepairOutcome.AtRepairLimit));
            Assert.That(_charges.GetCurrentCharges((kit, charges, null)), Is.EqualTo(before));
            Assert.That(_armor.TryGetCurrentSp(vest, ArmorjackSection, out var current), Is.True);
            Assert.That(current, Is.EqualTo(FixedPoint2.Zero));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void RepairProfilesAndRolloutPrototypesAreSemanticallyValid()
    {
        foreach (var profile in SProtoMan.EnumeratePrototypes<NCArmorRepairProfilePrototype>())
        {
            Assert.Multiple(() =>
            {
                Assert.That(profile.AcceptedCapabilities, Is.Not.Empty, $"{profile.ID} has no repair capability.");
                Assert.That(profile.AcceptedCapabilities, Is.Unique, $"{profile.ID} repeats a repair capability.");
                Assert.That(profile.RepairPerUse, Is.GreaterThan(FixedPoint2.Zero));
                Assert.That(float.IsFinite(profile.MaximumRepairFraction), Is.True);
                Assert.That(profile.MaximumRepairFraction, Is.GreaterThan(0f).And.LessThanOrEqualTo(1f));
            });
        }

        foreach (var section in SProtoMan.EnumeratePrototypes<NCArmorSectionPrototype>())
        {
            if (section.RepairProfile is { } repairProfile)
                Assert.That(SProtoMan.HasIndex(repairProfile), Is.True, $"{section.ID} has an invalid repair profile.");
        }

        Assert.Multiple(() =>
        {
            Assert.That(SEntMan.HasComponent<NCArmorComponent>(SSpawn("ClothingOuterArmorBasic")), Is.True);
            Assert.That(SEntMan.HasComponent<NCArmorComponent>(SSpawn("ClothingOuterArmorBulletproof")), Is.True);
            Assert.That(SEntMan.HasComponent<NCArmorComponent>(SSpawn("ClothingHeadHelmetBasic")), Is.True);
            Assert.That(SEntMan.HasComponent<NCArmorComponent>(SSpawn("ClothingHeadHelmetRiot")), Is.True);
        });
    }

    private NCArmorResolution Resolve(EntityUid target, ProtoId<NCTargetZonePrototype> zone)
    {
        return _armor.ResolveBound(new NCArmorResolveRequest(
            target,
            zone,
            NCArmorDamageClass.Ballistic,
            20,
            1));
    }

    private void AssertMobilityProfile(IEnumerable<string> prototypes, float walkModifier, float sprintModifier)
    {
        foreach (var prototype in prototypes)
        {
            var armor = SSpawn(prototype);
            var speed = SEntMan.GetComponent<ClothingSpeedModifierComponent>(armor);

            Assert.Multiple(() =>
            {
                Assert.That(speed.WalkModifier, Is.EqualTo(walkModifier).Within(0.001f),
                    $"{prototype} has the wrong walking modifier.");
                Assert.That(speed.SprintModifier, Is.EqualTo(sprintModifier).Within(0.001f),
                    $"{prototype} has the wrong sprint modifier.");
            });
        }
    }
}
