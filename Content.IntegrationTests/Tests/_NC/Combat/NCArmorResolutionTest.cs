// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Combat.Armor;
using Content.Shared._NC.Combat.Armor;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared.FixedPoint;
using Content.Shared.Inventory;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Combat;

[TestFixture]
[TestOf(typeof(NCArmorResolutionSystem))]
public sealed class NCArmorResolutionTest : GameTest
{
    private static readonly EntProtoId KevlarEntity = "NCArmorTestKevlar";
    private static readonly EntProtoId ArmorjackEntity = "NCArmorTestLightArmorjack";
    private static readonly EntProtoId PlateCarrierEntity = "NCArmorTestPlateCarrier";
    private static readonly EntProtoId SubdermalEntity = "NCArmorTestSubdermal";
    private static readonly EntProtoId HumanMob = "MobHuman";

    private static readonly ProtoId<NCTargetZonePrototype> Chest = "NCZoneChest";
    private static readonly ProtoId<NCTargetZonePrototype> ArmLeft = "NCZoneArmLeft";
    private static readonly ProtoId<NCArmorSectionPrototype> KevlarSection = "NCArmorSectionKevlarBody";
    private static readonly ProtoId<NCArmorSectionPrototype> ArmorjackSection = "NCArmorSectionLightArmorjackBody";

    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;
    [SidedDependency(Side.Server)] private NCArmorCoverageSystem _coverage = default!;
    [SidedDependency(Side.Server)] private NCArmorResolutionSystem _armor = default!;
    [SidedDependency(Side.Server)] private InventorySystem _inventory = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void MultipleLayersUseMaximumAndAllAblateOnPenetration()
    {
        var target = SSpawn(null);
        var kevlar = SSpawn(KevlarEntity);
        var armorjack = SSpawn(ArmorjackEntity);
        Assert.Multiple(() =>
        {
            Assert.That(_coverage.TryBindSource(kevlar, target), Is.True);
            Assert.That(_coverage.TryBindSource(armorjack, target), Is.True);
        });

        var standard = new NCArmorResolveRequest(target, Chest, NCArmorDamageClass.Ballistic, 15, 1);
        var first = _armor.ResolveBound(standard);

        Assert.Multiple(() =>
        {
            Assert.That(first.HasArmor, Is.True);
            Assert.That(first.EffectiveSp, Is.EqualTo(FixedPoint2.New(11)));
            Assert.That(first.PostArmorDamage, Is.EqualTo(FixedPoint2.New(4)));
            Assert.That(first.Stopped, Is.False);
            Assert.That(first.Penetrated, Is.True);
            Assert.That(first.AblationApplied, Is.EqualTo(1));
            Assert.That(first.ParticipatingSections, Has.Length.EqualTo(2));
            Assert.That(first.ParticipatingSections.Count(entry => entry.ProvidedEffectiveSp), Is.EqualTo(1));
            Assert.That(_armor.ApplyAblation(first), Is.EqualTo(2));
            Assert.That(CurrentSp(kevlar, KevlarSection), Is.EqualTo(FixedPoint2.New(6)));
            Assert.That(CurrentSp(armorjack, ArmorjackSection), Is.EqualTo(FixedPoint2.New(10)));
        });

        // Armor-piercing changes only ablation. It does not add body damage or stack section SP.
        var armorPiercing = standard with { AblationAmount = 2 };
        var second = _armor.ResolveBound(armorPiercing);
        Assert.Multiple(() =>
        {
            Assert.That(second.EffectiveSp, Is.EqualTo(FixedPoint2.New(10)));
            Assert.That(second.PostArmorDamage, Is.EqualTo(FixedPoint2.New(5)));
            Assert.That(_armor.ApplyAblation(second), Is.EqualTo(2));
            Assert.That(CurrentSp(kevlar, KevlarSection), Is.EqualTo(FixedPoint2.New(4)));
            Assert.That(CurrentSp(armorjack, ArmorjackSection), Is.EqualTo(FixedPoint2.New(8)));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void EquipAndUnequipMaintainTargetBoundCache()
    {
        var human = SSpawn(HumanMob);
        var armorjack = SSpawn(ArmorjackEntity);
        var request = new NCArmorResolveRequest(human, Chest, NCArmorDamageClass.Ballistic, 15, 1);

        Assert.That(_armor.ResolveBound(request).HasArmor, Is.False);
        Assert.That(_inventory.TryEquip(human, armorjack, "outerClothing"), Is.True);
        Assert.That(_armor.ResolveBound(request).EffectiveSp, Is.EqualTo(FixedPoint2.New(11)));

        Assert.That(_inventory.TryUnequip(human, "outerClothing", true), Is.True);
        Assert.That(_armor.ResolveBound(request).HasArmor, Is.False);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void StoppedAndRubberHitsNeverAblate()
    {
        var target = SSpawn(null);
        var armorjack = SSpawn(ArmorjackEntity);
        Assert.That(_coverage.TryBindSource(armorjack, target), Is.True);

        var stoppedRequest = new NCArmorResolveRequest(target, Chest, NCArmorDamageClass.Ballistic, 11, 1);
        var stopped = _armor.ResolveAndApplyBound(stoppedRequest);
        Assert.Multiple(() =>
        {
            Assert.That(stopped.Stopped, Is.True);
            Assert.That(stopped.Penetrated, Is.False);
            Assert.That(stopped.PostArmorDamage, Is.EqualTo(FixedPoint2.Zero));
            Assert.That(stopped.AblationApplied, Is.Zero);
            Assert.That(CurrentSp(armorjack, ArmorjackSection), Is.EqualTo(FixedPoint2.New(11)));
        });

        var rubberRequest = stoppedRequest with { RawDamage = 15, AblationAmount = 0 };
        var rubber = _armor.ResolveAndApplyBound(rubberRequest);
        Assert.Multiple(() =>
        {
            Assert.That(rubber.Penetrated, Is.True);
            Assert.That(rubber.PostArmorDamage, Is.EqualTo(FixedPoint2.New(4)));
            Assert.That(rubber.AblationApplied, Is.Zero);
            Assert.That(CurrentSp(armorjack, ArmorjackSection), Is.EqualTo(FixedPoint2.New(11)));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void CoverageDamageClassAndBindingAreExplicit()
    {
        var target = SSpawn(null);
        var otherTarget = SSpawn(null);
        var plate = SSpawn(PlateCarrierEntity);

        Assert.That(_coverage.TryBindSource(plate, otherTarget), Is.True);
        var chest = new NCArmorResolveRequest(target, Chest, NCArmorDamageClass.Ballistic, 20, 1);

        // An item bound to somebody else never enters the target-side cache.
        var unprotected = _armor.ResolveBound(chest);
        Assert.Multiple(() =>
        {
            Assert.That(unprotected.HasArmor, Is.False);
            Assert.That(unprotected.EffectiveSp, Is.EqualTo(FixedPoint2.Zero));
            Assert.That(unprotected.PostArmorDamage, Is.EqualTo(FixedPoint2.New(20)));
            Assert.That(unprotected.Penetrated, Is.False);
        });

        Assert.That(_coverage.TryBindSource(plate, target), Is.True);
        var protectedChest = _armor.ResolveBound(chest);
        var uncoveredArm = _armor.ResolveBound(chest with { ActualZone = ArmLeft });
        var unsupportedThermal = _armor.ResolveBound(chest with { DamageClass = NCArmorDamageClass.Thermal });
        Assert.Multiple(() =>
        {
            Assert.That(protectedChest.EffectiveSp, Is.EqualTo(FixedPoint2.New(13)));
            Assert.That(uncoveredArm.HasArmor, Is.False);
            Assert.That(unsupportedThermal.HasArmor, Is.False);
        });

        // Explicit collection is available to an adapter that already proved ownership independently.
        Assert.That(_armor.ResolveExplicit(chest, new[] { plate }).EffectiveSp,
            Is.EqualTo(FixedPoint2.New(13)));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void DestroyedSectionsClampAndStopParticipating()
    {
        var target = SSpawn(null);
        var armorjack = SSpawn(ArmorjackEntity);
        Assert.That(_coverage.TryBindSource(armorjack, target), Is.True);
        Assert.That(_armor.TrySetCurrentSp(armorjack, ArmorjackSection, 1), Is.True);

        var request = new NCArmorResolveRequest(target, Chest, NCArmorDamageClass.Ballistic, 5, 2);
        var result = _armor.ResolveAndApplyBound(request);
        Assert.Multiple(() =>
        {
            Assert.That(result.EffectiveSp, Is.EqualTo(FixedPoint2.New(1)));
            Assert.That(CurrentSp(armorjack, ArmorjackSection), Is.EqualTo(FixedPoint2.Zero));
            Assert.That(_armor.TryGetConditionStage(armorjack, ArmorjackSection, out var stage), Is.True);
            Assert.That(stage, Is.EqualTo(NCArmorConditionStage.Destroyed));
        });

        var afterDestruction = _armor.ResolveBound(request);
        Assert.Multiple(() =>
        {
            Assert.That(afterDestruction.HasArmor, Is.False);
            Assert.That(afterDestruction.PostArmorDamage, Is.EqualTo(FixedPoint2.New(5)));
            Assert.That(_armor.ApplyAblation(afterDestruction), Is.Zero);
        });

        Assert.That(_armor.TrySetCurrentSp(armorjack, ArmorjackSection, -50), Is.True);
        Assert.That(CurrentSp(armorjack, ArmorjackSection), Is.EqualTo(FixedPoint2.Zero));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ConditionStagesAndInstalledArmorAreDataDriven()
    {
        var installedArmorTarget = SSpawn(SubdermalEntity);
        var request = new NCArmorResolveRequest(
            installedArmorTarget,
            Chest,
            NCArmorDamageClass.Ballistic,
            12,
            1);

        // Armor installed on the target participates without a self-binding component.
        Assert.That(_armor.ResolveBound(request).EffectiveSp, Is.EqualTo(FixedPoint2.New(11)));

        AssertCondition(installedArmorTarget, "NCArmorSectionSubdermal", 11, NCArmorConditionStage.Intact);
        AssertCondition(installedArmorTarget, "NCArmorSectionSubdermal", 8, NCArmorConditionStage.Worn);
        AssertCondition(installedArmorTarget, "NCArmorSectionSubdermal", 5, NCArmorConditionStage.Damaged);
        AssertCondition(installedArmorTarget, "NCArmorSectionSubdermal", 2, NCArmorConditionStage.Critical);
        AssertCondition(installedArmorTarget, "NCArmorSectionSubdermal", 0, NCArmorConditionStage.Destroyed);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ArmorPrototypeGraphIsSemanticallyValid()
    {
        foreach (var profile in _prototypes.EnumeratePrototypes<NCArmorProfilePrototype>())
        {
            Assert.That(profile.Sections, Is.Not.Empty, $"Armor profile {profile.ID} has no sections.");
            Assert.That(profile.Sections, Is.Unique, $"Armor profile {profile.ID} repeats a section.");
            foreach (var sectionId in profile.Sections)
                Assert.That(_prototypes.HasIndex(sectionId), Is.True, $"Armor profile {profile.ID} references {sectionId}.");
        }

        foreach (var section in _prototypes.EnumeratePrototypes<NCArmorSectionPrototype>())
        {
            Assert.Multiple(() =>
            {
                Assert.That(section.MaxSp, Is.GreaterThan(FixedPoint2.Zero), $"Section {section.ID} has no SP.");
                Assert.That(section.CoveredZones, Is.Not.Empty, $"Section {section.ID} has no coverage.");
                Assert.That(section.CoveredZones, Is.Unique, $"Section {section.ID} repeats coverage.");
                Assert.That(section.ApplicableDamageClasses, Is.Not.Empty,
                    $"Section {section.ID} applies to no damage class.");
                Assert.That(section.ApplicableDamageClasses, Is.Unique,
                    $"Section {section.ID} repeats a damage class.");
                Assert.That(_prototypes.HasIndex(section.ConditionProfile), Is.True,
                    $"Section {section.ID} has an invalid condition profile.");
            });

            foreach (var zone in section.CoveredZones)
                Assert.That(_prototypes.HasIndex(zone), Is.True, $"Section {section.ID} references unknown zone {zone}.");
        }

        foreach (var profile in _prototypes.EnumeratePrototypes<NCArmorConditionProfilePrototype>())
        {
            Assert.Multiple(() =>
            {
                Assert.That(profile.Stages.Select(stage => stage.Stage), Is.Unique);
                Assert.That(profile.Stages.Select(stage => stage.MinimumFraction), Is.Unique);
                Assert.That(profile.Stages.Any(stage =>
                    stage.Stage == NCArmorConditionStage.Destroyed && stage.MinimumFraction == 0f), Is.True);
            });

            foreach (var stage in profile.Stages)
            {
                Assert.That(float.IsFinite(stage.MinimumFraction), Is.True);
                Assert.That(stage.MinimumFraction, Is.InRange(0f, 1f));
            }
        }
    }

    private FixedPoint2 CurrentSp(EntityUid source, ProtoId<NCArmorSectionPrototype> section)
    {
        Assert.That(_armor.TryGetCurrentSp(source, section, out var currentSp), Is.True);
        return currentSp;
    }

    private void AssertCondition(
        EntityUid source,
        ProtoId<NCArmorSectionPrototype> section,
        FixedPoint2 currentSp,
        NCArmorConditionStage expected)
    {
        Assert.That(_armor.TrySetCurrentSp(source, section, currentSp), Is.True);
        Assert.That(_armor.TryGetConditionStage(source, section, out var actual), Is.True);
        Assert.That(actual, Is.EqualTo(expected));
    }
}

/// <summary>
/// Pure arithmetic coverage remains runnable even when an unrelated server system prevents integration setup.
/// </summary>
[TestFixture]
[TestOf(typeof(NCArmorRules))]
public sealed class NCArmorRulesTest
{
    private static readonly ProtoId<NCTargetZonePrototype> Chest = "NCZoneChest";

    [Test]
    public void EffectiveSpIsMaximumAndPenetrationAblatesEveryLayer()
    {
        var request = new NCArmorResolveRequest(
            new EntityUid(100),
            Chest,
            NCArmorDamageClass.Ballistic,
            15,
            1);
        var sections = new[]
        {
            Section(1, "NCArmorSectionKevlarBody", 7),
            Section(2, "NCArmorSectionLightArmorjackBody", 11),
        };

        var result = NCArmorRules.Resolve(request, sections);
        Assert.Multiple(() =>
        {
            Assert.That(result.EffectiveSp, Is.EqualTo(FixedPoint2.New(11)));
            Assert.That(result.PostArmorDamage, Is.EqualTo(FixedPoint2.New(4)));
            Assert.That(result.Penetrated, Is.True);
            Assert.That(result.Stopped, Is.False);
            Assert.That(result.AblationApplied, Is.EqualTo(1));
            Assert.That(result.ParticipatingSections.Select(section => section.CurrentSpAfter),
                Is.EquivalentTo(new[] { FixedPoint2.New(6), FixedPoint2.New(10) }));
        });

        var armorPiercing = NCArmorRules.Resolve(request with { AblationAmount = 2 }, sections);
        Assert.That(armorPiercing.ParticipatingSections.Select(section => section.CurrentSpAfter),
            Is.EquivalentTo(new[] { FixedPoint2.New(5), FixedPoint2.New(9) }));
    }

    [Test]
    public void StoppedAndRubberHitsApplyNoAblation()
    {
        var section = new[] { Section(1, "NCArmorSectionLightArmorjackBody", 11) };
        var request = new NCArmorResolveRequest(
            new EntityUid(100),
            Chest,
            NCArmorDamageClass.Ballistic,
            11,
            1);

        var stopped = NCArmorRules.Resolve(request, section);
        Assert.Multiple(() =>
        {
            Assert.That(stopped.Stopped, Is.True);
            Assert.That(stopped.PostArmorDamage, Is.EqualTo(FixedPoint2.Zero));
            Assert.That(stopped.AblationApplied, Is.Zero);
            Assert.That(stopped.ParticipatingSections[0].CurrentSpAfter, Is.EqualTo(FixedPoint2.New(11)));
        });

        var rubber = NCArmorRules.Resolve(request with { RawDamage = 15, AblationAmount = 0 }, section);
        Assert.Multiple(() =>
        {
            Assert.That(rubber.Penetrated, Is.True);
            Assert.That(rubber.PostArmorDamage, Is.EqualTo(FixedPoint2.New(4)));
            Assert.That(rubber.AblationApplied, Is.Zero);
            Assert.That(rubber.ParticipatingSections[0].CurrentSpAfter, Is.EqualTo(FixedPoint2.New(11)));
        });
    }

    [Test]
    public void DestroyedDuplicateAndInvalidValuesAreNormalized()
    {
        var valid = Section(1, "NCArmorSectionKevlarBody", 7);
        var destroyed = Section(2, "NCArmorSectionLightArmorjackBody", 0);
        var request = new NCArmorResolveRequest(
            new EntityUid(100),
            Chest,
            NCArmorDamageClass.Ballistic,
            5,
            2);

        var result = NCArmorRules.Resolve(request, new[] { valid, valid, destroyed });
        Assert.Multiple(() =>
        {
            Assert.That(result.ParticipatingSections, Has.Length.EqualTo(1));
            Assert.That(result.EffectiveSp, Is.EqualTo(FixedPoint2.New(7)));
            Assert.That(result.Stopped, Is.True);
        });

        var noArmor = NCArmorRules.Resolve(
            request with { RawDamage = -20 },
            new[] { destroyed });
        Assert.Multiple(() =>
        {
            Assert.That(noArmor.HasArmor, Is.False);
            Assert.That(noArmor.RawDamage, Is.EqualTo(FixedPoint2.Zero));
            Assert.That(noArmor.PostArmorDamage, Is.EqualTo(FixedPoint2.Zero));
            Assert.That(noArmor.AblationApplied, Is.Zero);
        });
    }

    private static NCArmorSectionSnapshot Section(int uid, string section, FixedPoint2 currentSp)
    {
        return new NCArmorSectionSnapshot(
            new NCArmorSectionHandle(new EntityUid(uid), section),
            currentSp,
            currentSp,
            false,
            false);
    }
}
