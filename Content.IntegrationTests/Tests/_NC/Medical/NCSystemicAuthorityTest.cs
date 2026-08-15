// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Combat.Damage;
using Content.Server._NC.Medical.Anatomy.Detachment;
using Content.Server._NC.Medical.Emergency;
using Content.Server._NC.Medical.Physiology;
using Content.Server._NC.Medical.Systemic;
using Content.Server.Radiation.Systems;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Emergency;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Systemic;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Rejuvenate;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

[TestFixture]
[TestOf(typeof(NCSystemicSystem))]
public sealed class NCSystemicAuthorityTest : GameTest
{
    private static readonly EntProtoId HumanMob = "MobHuman";

    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;
    [SidedDependency(Side.Server)] private DamageableSystem _damageable = default!;
    [SidedDependency(Side.Server)] private NCSystemicSystem _systemic = default!;
    [SidedDependency(Side.Server)] private NCMedicalDamageRoutingSystem _routing = default!;
    [SidedDependency(Side.Server)] private NCPhysiologySystem _physiology = default!;
    [SidedDependency(Side.Server)] private NCPhysiologySchedulerSystem _scheduler = default!;
    [SidedDependency(Side.Server)] private NCEmergencyCareSystem _emergency = default!;
    [SidedDependency(Side.Server)] private NCOrganDetachmentSystem _detachment = default!;
    [SidedDependency(Side.Server)] private NCAnatomySystem _anatomy = default!;
    [SidedDependency(Side.Server)] private RadiationSystem _radiation = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void SystemicDamageAndRecoveryAreAuthoritativeWithoutLocalWoundsOrLegacyHp()
    {
        var cases = new[]
        {
            (Type: "Asphyxiation", Channel: NCSystemicChannel.Hypoxia),
            (Type: "Poison", Channel: NCSystemicChannel.Toxin),
            (Type: "Radiation", Channel: NCSystemicChannel.Radiation),
            (Type: "Cellular", Channel: NCSystemicChannel.Cellular),
        };

        foreach (var testCase in cases)
        {
            var body = SSpawn(HumanMob);
            var legacyBefore = LegacyDamage(body);
            Assert.That(_damageable.TryChangeDamage(
                body,
                MakeDamage(testCase.Type, 10),
                ignoreResistances: true), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(_systemic.GetBurden(body, testCase.Channel), Is.EqualTo(10f), testCase.Type);
                Assert.That(WoundCount(body), Is.Zero, testCase.Type);
                Assert.That(LegacyDamage(body), Is.EqualTo(legacyBefore), testCase.Type);
            });

            Assert.That(_damageable.TryChangeDamage(
                body,
                MakeDamage(testCase.Type, -4),
                ignoreResistances: true), Is.False);
            Assert.That(_systemic.GetBurden(body, testCase.Channel), Is.EqualTo(6f), testCase.Type);
        }
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void MixedHeatAndRadiationNeverInflatesLocalTrauma()
    {
        var body = SSpawn(HumanMob);
        var damage = MakeDamage("Heat", 8);
        damage.DamageDict["Radiation"] = FixedPoint2.New(50);

        Assert.That(_damageable.TryChangeDamage(body, damage, ignoreResistances: true), Is.False);
        var wounds = SComp<NCWoundContainerComponent>(body).Wounds.Values.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(wounds, Has.Length.EqualTo(1));
            Assert.That(wounds[0].WoundType, Is.EqualTo(NCWoundType.Thermal));
            Assert.That(wounds[0].IntegrityDamage.Float(), Is.EqualTo(8f));
            Assert.That(_systemic.GetBurden(body, NCSystemicChannel.Radiation), Is.EqualTo(50f));
            Assert.That(LegacyDamage(body), Is.EqualTo(FixedPoint2.Zero));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void MeasuredRadiationWorldEventFeedsSystemicAuthority()
    {
        var body = SSpawn(HumanMob);
        _radiation.IrradiateEntity(body, 5f, 2f);

        Assert.Multiple(() =>
        {
            Assert.That(_systemic.GetBurden(body, NCSystemicChannel.Radiation), Is.EqualTo(10f));
            Assert.That(WoundCount(body), Is.Zero);
            Assert.That(LegacyDamage(body), Is.EqualTo(FixedPoint2.Zero));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void TrustedPayloadApiAppliesSystemicSubsetExactlyOnce()
    {
        var body = SSpawn(HumanMob);
        var damage = MakeDamage("Poison", 12);
        damage.DamageDict["Heat"] = FixedPoint2.New(40);
        var action = new NCActionResolutionId(Guid.Parse("3a4ab5f7-6f2e-40a3-b567-c860f01bb65b"));

        Assert.That(_routing.TryPartitionDamage(damage, out var partition), Is.True);
        var first = _routing.ApplySystemicSubset(
            body,
            action,
            NCSystemicDamageSourceKind.Projectile,
            2,
            4,
            damage);
        var duplicate = _routing.ApplySystemicSubset(
            body,
            action,
            NCSystemicDamageSourceKind.Projectile,
            2,
            4,
            damage);
        var nextImpact = _routing.ApplySystemicSubset(
            body,
            action,
            NCSystemicDamageSourceKind.Projectile,
            2,
            5,
            damage);

        Assert.Multiple(() =>
        {
            Assert.That(partition.LocalTrauma.DamageDict.Keys.Select(type => type.Id),
                Is.EquivalentTo(new[] { "Heat" }));
            Assert.That(partition.LocalTrauma.GetTotal(), Is.EqualTo(FixedPoint2.New(40)));
            Assert.That(partition.Systemic.Select(delta => delta.DamageType.Id),
                Is.EquivalentTo(new[] { "Poison" }));
            Assert.That(first.Accepted, Is.True);
            Assert.That(first.Duplicate, Is.False);
            Assert.That(first.AppliedChannels, Is.EqualTo(1));
            Assert.That(duplicate.Duplicate, Is.True);
            Assert.That(nextImpact.Accepted, Is.True);
            Assert.That(nextImpact.Duplicate, Is.False);
            Assert.That(nextImpact.AppliedChannels, Is.EqualTo(1));
            Assert.That(_systemic.GetBurden(body, NCSystemicChannel.Toxin), Is.EqualTo(24f));
            Assert.That(_systemic.GetBurden(body, NCSystemicChannel.Radiation), Is.Zero);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ElectricalExposureCreatesProductionShockableArrestAndDefibrillationClearsLatch()
    {
        var body = SSpawn(HumanMob);
        var defibrillator = SSpawn("DefibrillatorOneHandedUnpowered");

        Assert.That(_damageable.TryChangeDamage(
            body,
            MakeDamage("Shock", 15),
            ignoreResistances: true), Is.False);
        Assert.That(_scheduler.ProcessNow(body, TimeSpan.Zero), Is.True);
        Assert.That(SComp<NCCirculationComponent>(body).HeartRhythm,
            Is.EqualTo(NCHeartRhythm.ShockableArrest));

        var result = _emergency.DefibrillateWithExternalPower(body, defibrillator, TimeSpan.Zero);
        Assert.That(_scheduler.ProcessNow(body, TimeSpan.Zero), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(NCEmergencyCareOutcome.Applied));
            // Defibrillation converts the shockable arrest; remaining measured systemic burden may
            // immediately produce an organized tachycardia instead of a perfectly normal rhythm.
            Assert.That(SComp<NCCirculationComponent>(body).HeartRhythm,
                Is.Not.AnyOf(NCHeartRhythm.ShockableArrest, NCHeartRhythm.NonShockableArrest));
            Assert.That(SComp<NCSystemicStateComponent>(body).ShockableArrestLatched, Is.False);
            Assert.That(_systemic.GetBurden(body, NCSystemicChannel.Electrical), Is.LessThanOrEqualTo(4f));
            Assert.That(LegacyDamage(body), Is.EqualTo(FixedPoint2.Zero));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void NegativePhysicalDamageCannotEraseWoundsButRejuvenateFullyRestoresBody()
    {
        var body = SSpawn(HumanMob);
        Assert.That(_damageable.TryChangeDamage(
            body,
            MakeDamage("Blunt", 12),
            ignoreResistances: true), Is.False);
        var wound = SComp<NCWoundContainerComponent>(body).Wounds.Values.Single();
        var woundDamage = wound.IntegrityDamage;

        Assert.That(_damageable.TryChangeDamage(
            body,
            MakeDamage("Blunt", -100),
            ignoreResistances: true), Is.False);
        Assert.That(SComp<NCWoundContainerComponent>(body).Wounds.Values.Single().IntegrityDamage,
            Is.EqualTo(woundDamage));

        Assert.That(_damageable.TryChangeDamage(
            body,
            MakeDamage("Poison", 50),
            ignoreResistances: true), Is.False);
        SComp<NCCirculationComponent>(body).BloodVolume = 100f;
        Assert.That(_anatomy.TryGetOrgan(body, "ArmLeft", out var originalArm), Is.True);
        Assert.That(_detachment.TryDetachOrgan(body, "ArmLeft", out var detachedArm), Is.True);
        Assert.That(detachedArm, Is.EqualTo(originalArm));

        SEntMan.EventBus.RaiseLocalEvent(body, new RejuvenateEvent());
        Assert.That(_anatomy.TryGetOrgan(body, "ArmLeft", out var restoredArm), Is.True);
        var profile = _physiology.GetProfile(body);
        Assert.Multiple(() =>
        {
            Assert.That(restoredArm, Is.Not.EqualTo(originalArm));
            Assert.That(WoundCount(body), Is.Zero);
            Assert.That(Enum.GetValues<NCSystemicChannel>()
                .All(channel => _systemic.GetBurden(body, channel) == 0f), Is.True);
            Assert.That(SComp<NCCirculationComponent>(body).BloodVolume, Is.EqualTo(profile.NominalBloodVolume));
            Assert.That(SComp<NCPhysiologyComponent>(body).Condition,
                Is.EqualTo(NCPhysiologicalCondition.Stable));
            Assert.That(SComp<NCPhysiologyComponent>(body).Irreversible, Is.False);
        });
    }

    private DamageSpecifier MakeDamage(string damageType, int amount)
    {
        return new DamageSpecifier(
            _prototypes.Index<DamageTypePrototype>(damageType),
            FixedPoint2.New(amount));
    }

    private int WoundCount(EntityUid body)
    {
        return STryComp<NCWoundContainerComponent>(body, out var wounds) ? wounds.Wounds.Count : 0;
    }

    private FixedPoint2 LegacyDamage(EntityUid body)
    {
#pragma warning disable CS0618 // Read-only assertion that compatibility HP never changes authority state.
        return _damageable.GetTotalDamage((body, SComp<DamageableComponent>(body)));
#pragma warning restore CS0618
    }
}
