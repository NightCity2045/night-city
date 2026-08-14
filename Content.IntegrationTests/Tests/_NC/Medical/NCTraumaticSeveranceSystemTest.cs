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
                0.1f,
                NCAnatomicalMaterialKind.Organic,
                NCBoneState.Shattered,
                0f), Is.False, "A normal handgun impact must never detach a limb regardless of accumulated damage.");
            Assert.That(NCTraumaticSeveranceRules.CanSever(
                ballistic,
                NCWoundSeverity.Catastrophic,
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
        float postArmorDamage)
    {
        return new NCCombatTraumaRequest(
            NCActionResolutionId.New(),
            1,
            1,
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
