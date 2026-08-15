// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Diagnostics;
using Content.Server._NC.Medical.Bleeding;
using Content.Server._NC.Medical.Physiology;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Diagnostics;
using Content.Shared._NC.Medical.Trauma;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared.Body;
using Content.Shared.Damage;
using Content.Shared.Decals;
using Content.Shared.FixedPoint;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

[TestFixture]
[TestOf(typeof(NCWoundContainerComponent))]
public sealed class NCWoundSystemTest : GameTest
{
    private static readonly EntProtoId HumanMob = "MobHuman";
    private static readonly ProtoId<NCWoundSystemProfilePrototype> HumanWounds = "NCHumanWoundSystem";

    [SidedDependency(Side.Server)] private NCAnatomySystem _anatomy = default!;
    [SidedDependency(Side.Server)] private NCMedicalStatusSnapshotSystem _status = default!;
    [SidedDependency(Side.Server)] private NCPhysiologySchedulerSystem _scheduler = default!;
    [SidedDependency(Side.Server)] private NCBleedingSystem _bleeding = default!;
    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void CompatibleMinorWoundsMergeAndDuplicateImpactIsIgnored()
    {
        var body = SSpawn(HumanMob);
        var first = CreateRequest(body, "NCZoneArmLeft", "ArmLeft", "SlashingImpact", 3f);
        var firstEvent = new NCCombatTraumaRequestedEvent(first);
        SEntMan.EventBus.RaiseLocalEvent(body, ref firstEvent);

        var container = SComp<NCWoundContainerComponent>(body);
        Assert.Multiple(() =>
        {
            Assert.That(firstEvent.Handled, Is.True);
            Assert.That(firstEvent.Applied, Is.True);
            Assert.That(firstEvent.Cancelled, Is.False);
            Assert.That(container.Wounds, Has.Count.EqualTo(1));
        });

        var woundId = container.Wounds.Keys.Single();
        var initialBleedingSource = SComp<NCBleedingComponent>(body).Sources.Single(source =>
            source.WoundId == woundId && source.Channel != NCBleedingChannel.Internal);
        Assert.That(initialBleedingSource.SourceId,
            Is.EqualTo(new NCActionResolutionId(woundId).DeriveGuid((uint) initialBleedingSource.Channel)));
        var second = CreateRequest(body, "NCZoneArmLeft", "ArmLeft", "SlashingImpact", 3f);
        var secondEvent = new NCCombatTraumaRequestedEvent(second);
        SEntMan.EventBus.RaiseLocalEvent(body, ref secondEvent);

        Assert.Multiple(() =>
        {
            Assert.That(container.Wounds, Has.Count.EqualTo(1));
            Assert.That(container.Wounds.Keys.Single(), Is.EqualTo(woundId));
            Assert.That(container.Wounds[woundId].IntegrityDamage.Float(), Is.EqualTo(6f).Within(0.001f));
            Assert.That(container.Wounds[woundId].Severity, Is.EqualTo(NCWoundSeverity.Moderate));
            Assert.That(SComp<NCBleedingComponent>(body).Sources.Count(source =>
                source.WoundId == woundId && source.Channel != NCBleedingChannel.Internal), Is.EqualTo(1));
            Assert.That(SComp<NCBleedingComponent>(body).Sources.Single(source =>
                source.WoundId == woundId && source.Channel != NCBleedingChannel.Internal).Channel,
                Is.EqualTo(NCBleedingChannel.ExternalVenous));
        });

        var duplicateEvent = new NCCombatTraumaRequestedEvent(second);
        SEntMan.EventBus.RaiseLocalEvent(body, ref duplicateEvent);
        Assert.Multiple(() =>
        {
            Assert.That(duplicateEvent.Handled, Is.True);
            Assert.That(duplicateEvent.Applied, Is.False);
            Assert.That(container.Wounds, Has.Count.EqualTo(1));
            Assert.That(container.Wounds[woundId].IntegrityDamage.Float(), Is.EqualTo(6f).Within(0.001f));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void InvalidActionResolutionCannotCreateNondeterministicWoundIdentity()
    {
        var body = SSpawn(HumanMob);
        var invalid = CreateRequest(
            body,
            "NCZoneArmLeft",
            "ArmLeft",
            "SlashingImpact",
            5f) with
        {
            ResolutionId = default,
        };
        var trauma = new NCCombatTraumaRequestedEvent(invalid);
        SEntMan.EventBus.RaiseLocalEvent(body, ref trauma);

        Assert.Multiple(() =>
        {
            Assert.That(trauma.Handled, Is.True);
            Assert.That(trauma.Applied, Is.False);
            Assert.That(STryComp<NCWoundContainerComponent>(body, out var wounds)
                ? wounds.Wounds.Count
                : 0, Is.Zero);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void OrganicImpactBloodPulseIsPhysicalAndExactlyOnce()
    {
        var body = SSpawn(HumanMob);
        var circulation = SComp<NCCirculationComponent>(body);
        var before = circulation.BloodVolume;
        var request = CreateRequest(body, "NCZoneLegLeft", "LegLeft", "BallisticProjectile", 10f);
        var first = new NCCombatTraumaRequestedEvent(request);
        SEntMan.EventBus.RaiseLocalEvent(body, ref first);
        var afterFirst = circulation.BloodVolume;

        var duplicate = new NCCombatTraumaRequestedEvent(request);
        SEntMan.EventBus.RaiseLocalEvent(body, ref duplicate);

        Assert.Multiple(() =>
        {
            Assert.That(first.Applied, Is.True);
            Assert.That(first.AppliedWoundType, Is.EqualTo(NCWoundType.Ballistic));
            Assert.That(first.AppliedSeverity, Is.EqualTo(NCWoundSeverity.Severe));
            Assert.That(first.AppliedExternalBleedRate, Is.GreaterThan(0f));
            Assert.That(afterFirst, Is.EqualTo(before - 15f).Within(0.01f));
            Assert.That(duplicate.Applied, Is.False);
            Assert.That(circulation.BloodVolume, Is.EqualTo(afterFirst).Within(0.01f),
                "Replaying one impact key must not eject blood twice.");
        });
    }

    [TestCase("BluntImpact")]
    [TestCase("ThermalImpact")]
    [RunOnSide(Side.Server)]
    public void ClosedOrNonBleedingTraumaDoesNotCreateImpactBlood(string traumaProfile)
    {
        var body = SSpawn(HumanMob);
        var circulation = SComp<NCCirculationComponent>(body);
        var before = circulation.BloodVolume;
        var request = CreateRequest(body, "NCZoneLegLeft", "LegLeft", traumaProfile, 10f);
        var trauma = new NCCombatTraumaRequestedEvent(request);
        SEntMan.EventBus.RaiseLocalEvent(body, ref trauma);

        Assert.Multiple(() =>
        {
            Assert.That(trauma.Applied, Is.True);
            Assert.That(circulation.BloodVolume, Is.EqualTo(before).Within(0.01f));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void HumanImpactBloodProfileIsBoundedAndComplete()
    {
        var woundSystem = _prototypes.Index<NCWoundSystemProfilePrototype>(HumanWounds);
        var profile = _prototypes.Index<NCImpactBloodProfilePrototype>(woundSystem.ImpactBloodProfile);
        var severities = Enum.GetValues<NCWoundSeverity>();

        Assert.Multiple(() =>
        {
            Assert.That(profile.BloodLossBySeverity.Keys, Is.EquivalentTo(severities));
            Assert.That(profile.DropletsBySeverity.Keys, Is.EquivalentTo(severities));
            Assert.That(profile.DecalsBySeverity.Keys, Is.EquivalentTo(severities));
            Assert.That(profile.Patterns.Keys, Is.EquivalentTo(profile.WoundTypes));
            Assert.That(profile.BloodLossBySeverity.Values, Is.All.GreaterThanOrEqualTo(0f));
            Assert.That(profile.DropletsBySeverity.Values, Is.All.InRange(0, byte.MaxValue));
            Assert.That(profile.DecalsBySeverity.Values, Is.All.GreaterThanOrEqualTo(0));
            Assert.That(profile.MaximumDecalsPerImpact, Is.InRange(0, 4));
            Assert.That(profile.MaximumNearbyDecals, Is.InRange(1, 16));
            Assert.That(profile.MaximumSurfaceStainsPerEntity, Is.InRange(1, 24));
            Assert.That(profile.MaximumActiveSurfaceEntities, Is.InRange(1, 512));
            Assert.That(profile.SurfaceStainScale, Is.InRange(0.1f, 1f));
            Assert.That(profile.MaximumClientBursts, Is.InRange(1, 128));
            Assert.That(profile.EffectDuration, Is.GreaterThan(TimeSpan.Zero));
            Assert.That(profile.SprayDistance, Is.GreaterThan(0f));
            Assert.That(profile.DropletSize, Is.GreaterThan(0f));
            Assert.That(profile.EffectColor.A, Is.GreaterThan(0f));
            Assert.That(profile.DecalColor.A, Is.GreaterThan(0f));
            Assert.That(profile.WoundTypes, Does.Contain(NCWoundType.Ballistic));
            Assert.That(profile.WoundTypes, Does.Not.Contain(NCWoundType.Blunt));
            Assert.That(profile.WoundTypes, Does.Not.Contain(NCWoundType.Thermal));
        });

        foreach (var (woundType, pattern) in profile.Patterns)
        {
            Assert.Multiple(() =>
            {
                Assert.That(pattern.SplatsBySeverity.Keys, Is.EquivalentTo(severities),
                    $"{woundType} splat counts must cover every severity.");
                Assert.That(pattern.StreaksBySeverity.Keys, Is.EquivalentTo(severities),
                    $"{woundType} streak counts must cover every severity.");
                Assert.That(pattern.SplatsBySeverity.Values, Is.All.InRange(0, byte.MaxValue));
                Assert.That(pattern.StreaksBySeverity.Values, Is.All.InRange(0, byte.MaxValue));
                Assert.That(pattern.SplatDecals, Is.Not.Empty);
                Assert.That(pattern.StreakDecals, Is.Not.Empty);
                Assert.That(pattern.SplatDecals.Select(id => id.Id), Is.Unique);
                Assert.That(pattern.StreakDecals.Select(id => id.Id), Is.Unique);
                Assert.That(pattern.ConeDegrees, Is.InRange(0f, 360f));
                Assert.That(pattern.DistanceMultiplier, Is.GreaterThan(0f));
                Assert.That(pattern.SplatRadius, Is.GreaterThanOrEqualTo(0f));
                Assert.That(pattern.SplatSize, Is.GreaterThan(0f));
                Assert.That(pattern.StreakLengthMultiplier, Is.GreaterThan(0f));
                Assert.That(pattern.StreakWidth, Is.GreaterThan(0f));
                Assert.That(pattern.PersistentStreakFraction, Is.InRange(0f, 1f));
            });

            foreach (var decal in pattern.SplatDecals.Concat(pattern.StreakDecals))
                Assert.That(_prototypes.TryIndex<DecalPrototype>(decal, out _), Is.True,
                    $"{woundType} references missing decal {decal}.");

            var first = NCImpactBloodRules.SamplePatternDirection(
                Vector2.UnitX,
                pattern.Kind,
                pattern.DirectionOffsetDegrees,
                pattern.ConeDegrees,
                42UL,
                3u);
            var repeated = NCImpactBloodRules.SamplePatternDirection(
                Vector2.UnitX,
                pattern.Kind,
                pattern.DirectionOffsetDegrees,
                pattern.ConeDegrees,
                42UL,
                3u);
            Assert.Multiple(() =>
            {
                Assert.That(repeated, Is.EqualTo(first), "Spray layout must be replay-deterministic.");
                Assert.That(first.Length(), Is.EqualTo(1f).Within(0.0001f));
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(profile.Patterns[NCWoundType.Ballistic].Kind, Is.EqualTo(NCImpactBloodPatternKind.Directed));
            Assert.That(profile.Patterns[NCWoundType.Slash].Kind, Is.EqualTo(NCImpactBloodPatternKind.Arc));
            Assert.That(profile.Patterns[NCWoundType.Blast].Kind, Is.EqualTo(NCImpactBloodPatternKind.Radial));
            Assert.That(profile.Patterns[NCWoundType.Ballistic].SplatDecals.Select(id => id.Id),
                Does.Contain("NCBloodSplat0"));
            Assert.That(profile.Patterns[NCWoundType.Ballistic].StreakDecals.Select(id => id.Id),
                Does.Contain("NCBloodStreakThin"));
            Assert.That(NCImpactBloodRules.ResolveDistanceMultiplier(
                    profile,
                    NCTraumaProjectileDisposition.Exit),
                Is.GreaterThan(NCImpactBloodRules.ResolveDistanceMultiplier(
                    profile,
                    NCTraumaProjectileDisposition.Embedded)));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void CoreTraumaMechanismsCreateDistinctAuthoritativeWounds()
    {
        var body = SSpawn(HumanMob);
        var cases = new (string Profile, NCWoundType Type)[]
        {
            ("BallisticProjectile", NCWoundType.Ballistic),
            ("SlashingImpact", NCWoundType.Slash),
            ("PiercingImpact", NCWoundType.Pierce),
            ("BluntImpact", NCWoundType.Blunt),
            ("ThermalImpact", NCWoundType.Thermal),
        };

        foreach (var (profile, _) in cases)
        {
            var request = CreateRequest(body, "NCZoneLegLeft", "LegLeft", profile, 5f);
            var trauma = new NCCombatTraumaRequestedEvent(request);
            SEntMan.EventBus.RaiseLocalEvent(body, ref trauma);
            Assert.That(trauma.Handled, Is.True);
        }

        var container = SComp<NCWoundContainerComponent>(body);
        Assert.That(container.Wounds.Values.Select(wound => wound.WoundType),
            Is.EquivalentTo(cases.Select(entry => entry.Type)));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ThoraxTraumaChangesRegionBoneAndContainedOrganFunction()
    {
        var body = SSpawn(HumanMob);
        Assert.That(_anatomy.TryGetOrgan(body, "Thorax", out var thorax), Is.True);
        Assert.That(_anatomy.TryGetOrgan(body, "RibCage", out var ribs), Is.True);
        Assert.That(_anatomy.TryGetOrgan(body, "Heart", out var heart), Is.True);
        Assert.That(_anatomy.TryGetOrgan(body, "Lungs", out var lungs), Is.True);

        var thoraxBefore = SComp<NCOrganIntegrityComponent>(thorax).CurrentIntegrity;
        var heartBefore = SComp<NCOrganFunctionComponent>(heart).CurrentCapacity;
        var lungsBefore = SComp<NCOrganFunctionComponent>(lungs).CurrentCapacity;

        var request = CreateRequest(body, "NCZoneChest", "Thorax", "BallisticProjectile", 10f);
        var trauma = new NCCombatTraumaRequestedEvent(request);
        SEntMan.EventBus.RaiseLocalEvent(body, ref trauma);

        Assert.Multiple(() =>
        {
            Assert.That(SComp<NCOrganIntegrityComponent>(thorax).CurrentIntegrity, Is.LessThan(thoraxBefore));
            Assert.That(SComp<NCBoneComponent>(ribs).State, Is.GreaterThanOrEqualTo(NCBoneState.BrokenStable));
            Assert.That(
                SComp<NCOrganFunctionComponent>(heart).CurrentCapacity < heartBefore ||
                SComp<NCOrganFunctionComponent>(lungs).CurrentCapacity < lungsBefore,
                Is.True,
                "A severe thorax impact must affect one deterministic contained vital organ.");
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void LimbTraumaChangesTendonNerveAndJointFunctionIndependently()
    {
        var body = SSpawn(HumanMob);
        Assert.That(_anatomy.TryGetOrgan(body, "ArmLeft", out var arm), Is.True);
        Assert.That(_anatomy.TryGetOrgan(body, "TendonSetLeftArm", out var tendons), Is.True);

        var tendonBefore = SComp<NCTendonComponent>(tendons).Integrity;
        var motorBefore = SComp<NCNerveComponent>(arm).MotorFunction;
        var jointBefore = SComp<NCJointComponent>(arm).Stability;
        var request = CreateRequest(body, "NCZoneArmLeft", "ArmLeft", "SlashingImpact", 17f);
        var trauma = new NCCombatTraumaRequestedEvent(request);
        SEntMan.EventBus.RaiseLocalEvent(body, ref trauma);

        Assert.Multiple(() =>
        {
            Assert.That(SComp<NCTendonComponent>(tendons).Integrity, Is.LessThan(tendonBefore));
            Assert.That(SComp<NCTendonComponent>(tendons).Ruptured, Is.True);
            Assert.That(SComp<NCNerveComponent>(arm).MotorFunction, Is.LessThan(motorBefore));
            Assert.That(SComp<NCJointComponent>(arm).Stability, Is.LessThan(jointBefore));
            Assert.That(SComp<NCJointComponent>(arm).MovementRestriction, Is.GreaterThan(0f));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void StoppedArmorTransferCreatesBluntWoundAndArterialBleedingIsDistinct()
    {
        var body = SSpawn(HumanMob);
        var transfer = CreateRequest(
            body,
            "NCZoneChest",
            "Thorax",
            "BluntImpact",
            0f,
            transferredBluntDamage: 6f);
        var transferEvent = new NCCombatTraumaRequestedEvent(transfer);
        SEntMan.EventBus.RaiseLocalEvent(body, ref transferEvent);

        var arterial = CreateRequest(body, "NCZoneArmRight", "ArmRight", "SlashingImpact", 10f);
        var arterialEvent = new NCCombatTraumaRequestedEvent(arterial);
        SEntMan.EventBus.RaiseLocalEvent(body, ref arterialEvent);

        var wounds = SComp<NCWoundContainerComponent>(body).Wounds.Values;
        var bleeding = SComp<NCBleedingComponent>(body);
        var circulation = SComp<NCCirculationComponent>(body);
        var bloodBefore = circulation.BloodVolume;
        var expectedOneSecondLoss = bleeding.ActiveExternalRate + bleeding.ActiveInternalRate;
        Assert.Multiple(() =>
        {
            Assert.That(wounds.Any(wound =>
                wound.WoundType == NCWoundType.Blunt && wound.Severity == NCWoundSeverity.Moderate), Is.True);
            Assert.That(wounds.Any(wound =>
                wound.WoundType == NCWoundType.Slash && wound.PrimaryBleedKind == NCBleedKind.Arterial), Is.True);
            Assert.That(bleeding.Sources.Any(source =>
                source.Channel == NCBleedingChannel.ExternalArterial), Is.True);
        });

        Assert.That(_bleeding.AdvanceBody(body, TimeSpan.FromSeconds(1)),
            Is.EqualTo(expectedOneSecondLoss).Within(0.001f));
        Assert.That(circulation.BloodVolume,
            Is.EqualTo(bloodBefore - expectedOneSecondLoss).Within(0.001f));

        // Processing the immediate wound invalidation must schedule the next update at the profile rate,
        // not recursively at the current frame.
        _scheduler.Update(0f);
        Assert.That(_scheduler.GetRemainingDelay(body), Is.GreaterThan(TimeSpan.Zero));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void DetailedRecordBudgetAndOwnerStatusSnapshotAreBounded()
    {
        var body = SSpawn(HumanMob);
        var profile = _prototypes.Index(HumanWounds);
        for (var i = 0; i < profile.MaxActiveWounds + 8; i++)
        {
            var request = CreateRequest(body, "NCZoneHead", "Head", "BallisticProjectile", 1f);
            var trauma = new NCCombatTraumaRequestedEvent(request);
            SEntMan.EventBus.RaiseLocalEvent(body, ref trauma);
        }

        var container = SComp<NCWoundContainerComponent>(body);
        var expectedZoneCount = _prototypes.Index(SComp<NCMedicalBodyComponent>(body).Profile).TargetZones.Count;
        var first = _status.BuildOwnerStatusSnapshot(body);
        var second = _status.BuildOwnerStatusSnapshot(body);
        Assert.Multiple(() =>
        {
            Assert.That(container.Wounds, Has.Count.EqualTo(profile.MaxActiveWounds));
            Assert.That(container.OverflowTraumaByZone["NCZoneHead"], Is.GreaterThan(0f));
            Assert.That(first, Is.EqualTo(second));
            Assert.That(first, Has.Count.EqualTo(expectedZoneCount));
            Assert.That(first.Single(zone => zone.Zone.Id == "NCZoneHead").Severity, Is.EqualTo(8));
            Assert.That(first.All(zone => zone.Severity <= 8), Is.True);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void SeparateImpactsKeepSeparateStableVisualCoordinatesAndOwnerFlags()
    {
        var body = SSpawn(HumanMob);
        var firstRequest = CreateRequest(
            body,
            "NCZoneLegRight",
            "LegRight",
            "BallisticProjectile",
            9f,
            localHitPosition: new Vector2(-0.25f, 0.25f),
            impactDirection: Vector2.UnitX);
        var secondRequest = CreateRequest(
            body,
            "NCZoneLegRight",
            "LegRight",
            "BallisticProjectile",
            9f,
            localHitPosition: new Vector2(0.25f, -0.25f),
            impactDirection: Vector2.UnitY);

        var firstEvent = new NCCombatTraumaRequestedEvent(firstRequest);
        var secondEvent = new NCCombatTraumaRequestedEvent(secondRequest);
        SEntMan.EventBus.RaiseLocalEvent(body, ref firstEvent);
        SEntMan.EventBus.RaiseLocalEvent(body, ref secondEvent);

        var wounds = SComp<NCWoundContainerComponent>(body).Wounds.Values
            .Where(wound => wound.TargetZone.Id == "NCZoneLegRight")
            .ToList();
        var visibleMarkers = SComp<NCVisibleWoundComponent>(body).Markers
            .Where(marker => marker.Zone.Id == "NCZoneLegRight")
            .ToList();
        var status = _status.BuildOwnerStatusSnapshot(body)
            .Single(zone => zone.Zone.Id == "NCZoneLegRight");
        Assert.Multiple(() =>
        {
            Assert.That(firstEvent.Applied, Is.True);
            Assert.That(secondEvent.Applied, Is.True);
            Assert.That(wounds, Has.Count.EqualTo(2));
            Assert.That(wounds.Select(wound => (wound.VisualX, wound.VisualY)), Is.Unique);
            Assert.That(wounds.Select(wound => wound.VisualDirection), Is.Unique);
            Assert.That(visibleMarkers, Has.Count.EqualTo(2));
            Assert.That(visibleMarkers.Select(marker => (marker.VisualX, marker.VisualY)), Is.Unique);
            Assert.That(visibleMarkers.All(marker => marker.Type == NCWoundType.Ballistic), Is.True);
            Assert.That(status.Flags.HasFlag(NCMedicalZoneStatusFlags.ExternalBleeding), Is.True);
            Assert.That(typeof(NCVisibleWoundMarker).GetProperties()
                .All(property => property.PropertyType != typeof(Guid)), Is.True,
                "Public world marks must not expose wound or forensic identity.");
        });

        Assert.That(_anatomy.TryGetOrgan(body, "LegRight", out var leg), Is.True);
        SComp<NCOrganIntegrityComponent>(leg).FunctionalEfficiency = 0f;
        status = _status.BuildOwnerStatusSnapshot(body)
            .Single(zone => zone.Zone.Id == "NCZoneLegRight");
        Assert.Multiple(() =>
        {
            Assert.That(status.Flags.HasFlag(NCMedicalZoneStatusFlags.FunctionLost), Is.True);
            Assert.That(status.Severity, Is.EqualTo(8));
        });
    }

    private static NCCombatTraumaRequest CreateRequest(
        EntityUid body,
        ProtoId<NCTargetZonePrototype> zone,
        ProtoId<OrganCategoryPrototype> region,
        ProtoId<NCTraumaProfilePrototype> traumaProfile,
        float postArmorDamage,
        float transferredBluntDamage = 0f,
        Vector2? localHitPosition = null,
        Vector2? impactDirection = null)
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
            FixedPoint2.New(transferredBluntDamage),
            localHitPosition ?? Vector2.Zero,
            impactDirection ?? Vector2.UnitX,
            transferredBluntDamage > 0f ? NCTraumaFlags.ArmorStopped : NCTraumaFlags.ArmorPenetrated,
            0.1f,
            TimeSpan.Zero);
    }
}
