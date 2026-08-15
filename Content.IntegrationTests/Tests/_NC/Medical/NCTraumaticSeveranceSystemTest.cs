// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Anatomy.Detachment;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Damage;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Anatomy.Detachment;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Trauma;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

[TestFixture]
[TestOf(typeof(NCTraumaticSeveranceSystem))]
public sealed class NCTraumaticSeveranceSystemTest : GameTest
{
    private static readonly EntProtoId HumanMob = "MobHuman";
    private static readonly ProtoId<NCWoundSystemProfilePrototype> HumanWounds = "NCHumanWoundSystem";
    private static readonly ProtoId<NCCombatDamageProfilePrototype> StandardBallistics = "NCBallisticStandard";

    [SidedDependency(Side.Server)] private NCAnatomySystem _anatomy = default!;
    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void RepeatedStandardPistolHitsNeverRandomlyDetachLimb()
    {
        var body = SSpawn(HumanMob);

        for (var i = 0; i < 4; i++)
        {
            var impact = new NCCombatTraumaRequestedEvent(CreateRequest(
                body,
                "NCZoneLegLeft",
                "LegLeft",
                "BallisticProjectile",
                14f));
            SEntMan.EventBus.RaiseLocalEvent(body, ref impact);
            Assert.That(impact.Applied, Is.True);
        }

        Assert.Multiple(() =>
        {
            Assert.That(_anatomy.TryGetOrgan(body, "LegLeft", out _), Is.True);
            Assert.That(_anatomy.TryGetOrgan(body, "FootLeft", out _), Is.True);
            Assert.That(SComp<NCWoundContainerComponent>(body).Wounds.Values.Count(wound =>
                wound.TargetZone.Id == "NCZoneLegLeft" && wound.WoundType == NCWoundType.Ballistic), Is.EqualTo(4));
            Assert.That(SComp<NCWoundContainerComponent>(body).Wounds.Values.All(wound =>
                wound.Severity == NCWoundSeverity.Severe), Is.True);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ShotgunPelletsFromOneActionAggregateInsideOneLimb()
    {
        var body = SSpawn(HumanMob);
        var action = NCActionResolutionId.New();

        for (uint projectile = 0; projectile < 2; projectile++)
        {
            var impact = new NCCombatTraumaRequestedEvent(CreateRequest(
                body,
                "NCZoneLegLeft",
                "LegLeft",
                "BallisticProjectile",
                10f,
                action,
                projectile));
            SEntMan.EventBus.RaiseLocalEvent(body, ref impact);
            Assert.Multiple(() =>
            {
                Assert.That(impact.Applied, Is.True);
                Assert.That(_anatomy.TryGetOrgan(body, "LegLeft", out _), Is.True);
            });
        }

        var finishingPellet = new NCCombatTraumaRequestedEvent(CreateRequest(
            body,
            "NCZoneLegLeft",
            "LegLeft",
            "BallisticProjectile",
            10f,
            action,
            2));
        SEntMan.EventBus.RaiseLocalEvent(body, ref finishingPellet);

        Assert.Multiple(() =>
        {
            Assert.That(finishingPellet.Applied, Is.True);
            Assert.That(_anatomy.TryGetOrgan(body, "LegLeft", out _), Is.False);
            Assert.That(_anatomy.TryGetOrgan(body, "FootLeft", out _), Is.False);
            Assert.That(SComp<NCWoundContainerComponent>(body).Wounds.Values.Count(wound =>
                wound.WoundType == NCWoundType.Amputation), Is.EqualTo(1));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void PelletTraumaDoesNotAggregateAcrossRegionsAndArmorKeepsStructureSafe()
    {
        var splitBody = SSpawn(HumanMob);
        var splitAction = NCActionResolutionId.New();
        for (uint projectile = 0; projectile < 4; projectile++)
        {
            var left = projectile % 2 == 0;
            var impact = new NCCombatTraumaRequestedEvent(CreateRequest(
                splitBody,
                left ? "NCZoneLegLeft" : "NCZoneLegRight",
                left ? "LegLeft" : "LegRight",
                "BallisticProjectile",
                10f,
                splitAction,
                projectile));
            SEntMan.EventBus.RaiseLocalEvent(splitBody, ref impact);
        }

        var armoredBody = SSpawn(HumanMob);
        var armoredAction = NCActionResolutionId.New();
        for (uint projectile = 0; projectile < 6; projectile++)
        {
            var impact = new NCCombatTraumaRequestedEvent(CreateRequest(
                armoredBody,
                "NCZoneArmRight",
                "ArmRight",
                "BallisticProjectile",
                6f,
                armoredAction,
                projectile));
            SEntMan.EventBus.RaiseLocalEvent(armoredBody, ref impact);
        }

        Assert.Multiple(() =>
        {
            Assert.That(_anatomy.TryGetOrgan(splitBody, "LegLeft", out _), Is.True);
            Assert.That(_anatomy.TryGetOrgan(splitBody, "LegRight", out _), Is.True);
            Assert.That(_anatomy.TryGetOrgan(armoredBody, "ArmRight", out _), Is.True);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void RepeatedOrdinaryCutsOnlyDetachAnAlreadyMangledLimb()
    {
        var body = SSpawn(HumanMob);

        for (var cut = 0; cut < 6; cut++)
        {
            var impact = new NCCombatTraumaRequestedEvent(CreateRequest(
                body,
                "NCZoneArmLeft",
                "ArmLeft",
                "SlashingImpact",
                10f));
            SEntMan.EventBus.RaiseLocalEvent(body, ref impact);
            Assert.That(_anatomy.TryGetOrgan(body, "ArmLeft", out _), Is.True,
                $"Ordinary cut {cut + 1} detached the limb before the mangled-state gate.");
        }

        var finishingCut = new NCCombatTraumaRequestedEvent(CreateRequest(
            body,
            "NCZoneArmLeft",
            "ArmLeft",
            "SlashingImpact",
            10f));
        SEntMan.EventBus.RaiseLocalEvent(body, ref finishingCut);

        Assert.Multiple(() =>
        {
            Assert.That(finishingCut.Applied, Is.True);
            Assert.That(_anatomy.TryGetOrgan(body, "ArmLeft", out _), Is.False);
            Assert.That(_anatomy.TryGetOrgan(body, "HandLeft", out _), Is.False);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void HeavyChoppingImpactCanDetachAnUnprotectedLimb()
    {
        var body = SSpawn(HumanMob);
        var impact = new NCCombatTraumaRequestedEvent(CreateRequest(
            body,
            "NCZoneArmRight",
            "ArmRight",
            "SlashingImpact",
            20f));
        SEntMan.EventBus.RaiseLocalEvent(body, ref impact);

        Assert.Multiple(() =>
        {
            Assert.That(impact.Applied, Is.True);
            Assert.That(_anatomy.TryGetOrgan(body, "ArmRight", out _), Is.False);
            Assert.That(_anatomy.TryGetOrgan(body, "HandRight", out _), Is.False);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void CatastrophicBlastDetachesLiveSubtreeAndCreatesTreatableArterialStump()
    {
        var body = SSpawn(HumanMob);
        Assert.That(_anatomy.TryGetOrgan(body, "ArmLeft", out var arm), Is.True);

        var impact = new NCCombatTraumaRequestedEvent(CreateRequest(
            body,
            "NCZoneArmLeft",
            "ArmLeft",
            "NCExplosiveImpact",
            25f));
        SEntMan.EventBus.RaiseLocalEvent(body, ref impact);

        var wounds = SComp<NCWoundContainerComponent>(body);
        var stump = wounds.Wounds.Values.Single(wound => wound.WoundType == NCWoundType.Amputation);
        var detached = SComp<NCDetachedOrganMedicalDataComponent>(arm);
        Assert.Multiple(() =>
        {
            Assert.That(impact.Applied, Is.True);
            Assert.That(_anatomy.TryGetOrgan(body, "ArmLeft", out _), Is.False);
            Assert.That(_anatomy.TryGetOrgan(body, "HandLeft", out _), Is.False);
            Assert.That(SComp<ChildOrganComponent>(arm).Parent, Is.Null);
            Assert.That(detached.Wounds.Values.Any(wound => wound.WoundType == NCWoundType.Blast), Is.True);
            Assert.That(stump.TargetZone.Id, Is.EqualTo("NCZoneArmLeft"));
            Assert.That(stump.RegionCategory.Id, Is.EqualTo("Torso"));
            Assert.That(stump.PrimaryBleedKind, Is.EqualTo(NCBleedKind.Arterial));
            Assert.That(SComp<NCBleedingComponent>(body).Sources.Any(source =>
                source.WoundId == stump.WoundId &&
                source.Channel == NCBleedingChannel.ExternalArterial), Is.True);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void SeveranceProfileIsBoundedHumanOnlyAndHasNoHeadRule()
    {
        var woundProfile = _prototypes.Index(HumanWounds);
        var profile = _prototypes.Index(woundProfile.SeveranceProfile);
        var expectedRegions = new[]
        {
            "ArmLeft", "ArmRight", "HandLeft", "HandRight",
            "LegLeft", "LegRight", "FootLeft", "FootRight",
        };

        Assert.Multiple(() =>
        {
            Assert.That(profile.Regions.Keys.Select(key => key.Id), Is.EquivalentTo(expectedRegions));
            Assert.That(profile.Regions.Keys.All(key => key.Id is not "Head" and not "Torso"), Is.True);
            Assert.That(profile.Mechanisms.Keys,
                Is.EquivalentTo(new[] { NCWoundType.Ballistic, NCWoundType.Slash, NCWoundType.Blast, NCWoundType.Blunt }));
            Assert.That(profile.Mechanisms.Values.All(mechanism =>
                NCTraumaticSeveranceRules.IsValid(mechanism.Impact) &&
                NCTraumaticSeveranceRules.IsValid(mechanism.MechanicalImpact) &&
                (mechanism.CompoundAction == null || NCTraumaticSeveranceRules.IsValid(mechanism.CompoundAction)) &&
                (mechanism.MangledFinisher == null || NCTraumaticSeveranceRules.IsValid(mechanism.MangledFinisher)) &&
                (mechanism.MechanicalCompoundAction == null ||
                 NCTraumaticSeveranceRules.IsValid(mechanism.MechanicalCompoundAction)) &&
                (mechanism.MechanicalMangledFinisher == null ||
                 NCTraumaticSeveranceRules.IsValid(mechanism.MechanicalMangledFinisher))), Is.True);
            Assert.That(profile.Regions.Values.All(rule =>
                float.IsFinite(rule.StumpTrauma) && rule.StumpTrauma > 0f &&
                float.IsFinite(rule.MechanicalStumpTraumaMultiplier) &&
                rule.MechanicalStumpTraumaMultiplier is > 0f and <= 1f), Is.True);
        });

        var ballistic = profile.Mechanisms[NCWoundType.Ballistic];
        Assert.Multiple(() =>
        {
            Assert.That(NCTraumaticSeveranceRules.CanSever(
                ballistic,
                NCWoundSeverity.Severe,
                14f,
                14f,
                0.1f,
                NCAnatomicalMaterialKind.Organic,
                NCBoneState.Shattered,
                0f), Is.False, "A normal handgun impact must never detach a limb regardless of accumulated damage.");
            Assert.That(NCTraumaticSeveranceRules.CanSever(
                ballistic,
                NCWoundSeverity.Catastrophic,
                28f,
                28f,
                0.79f,
                NCAnatomicalMaterialKind.Organic,
                NCBoneState.Shattered,
                0.44f), Is.True);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void StandardHandgunBalanceIsSevereWithoutGuaranteedExitOrArterialBleed()
    {
        var woundProfile = _prototypes.Index(HumanWounds);
        var ballisticWound = _prototypes.Index(woundProfile.Wounds[NCTraumaMechanism.Projectile]);
        var ballisticDamage = _prototypes.Index(StandardBallistics);

        Assert.Multiple(() =>
        {
            Assert.That(woundProfile.SeverityThresholds, Is.EqualTo(new[] { 4f, 9f, 16f, 24f }));
            Assert.That(ballisticDamage.ExitMinimumPostArmorDamage, Is.GreaterThan(14f));
            Assert.That(ballisticWound.ArterialFromSeverity, Is.EqualTo(NCWoundSeverity.Critical));
            Assert.That(ballisticWound.ExternalBleedPerDamage, Is.LessThanOrEqualTo(0.35f));
            Assert.That(ballisticWound.PainPerDamage, Is.LessThanOrEqualTo(0.85f));
            Assert.That(ballisticWound.RegionIntegrityDamageMultiplier, Is.LessThanOrEqualTo(0.75f));
        });
    }

    private static NCCombatTraumaRequest CreateRequest(
        EntityUid body,
        ProtoId<NCTargetZonePrototype> zone,
        ProtoId<OrganCategoryPrototype> region,
        ProtoId<NCTraumaProfilePrototype> traumaProfile,
        float postArmorDamage,
        NCActionResolutionId? resolutionId = null,
        uint projectileSequence = 1,
        uint impactSequence = 1)
    {
        return new NCCombatTraumaRequest(
            resolutionId ?? NCActionResolutionId.New(),
            projectileSequence,
            impactSequence,
            body,
            null,
            null,
            null,
            zone,
            region,
            traumaProfile,
            new DamageSpecifier(),
            FixedPoint2.New(postArmorDamage),
            FixedPoint2.Zero,
            FixedPoint2.New(postArmorDamage),
            FixedPoint2.Zero,
            Vector2.Zero,
            Vector2.UnitX,
            NCTraumaFlags.ArmorPenetrated,
            0.1f,
            TimeSpan.Zero);
    }
}
