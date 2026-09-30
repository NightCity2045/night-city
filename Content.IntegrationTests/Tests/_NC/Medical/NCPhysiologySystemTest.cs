// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Physiology;
using Content.Server._NC.Medical.Wounds;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Trauma;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared.Body;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

[TestFixture]
[TestOf(typeof(NCPhysiologySystem))]
public sealed class NCPhysiologySystemTest : GameTest
{
    private static readonly EntProtoId HumanMob = "MobHuman";

    [SidedDependency(Side.Server)] private NCPhysiologySystem _physiology = default!;
    [SidedDependency(Side.Server)] private NCPhysiologySchedulerSystem _scheduler = default!;
    [SidedDependency(Side.Server)] private NCWoundSystem _wounds = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void BloodLossClinicalDeathRemainsRevivableForConfiguredWindow()
    {
        var body = SSpawn(HumanMob);
        var circulation = SComp<NCCirculationComponent>(body);
        var physiology = SComp<NCPhysiologyComponent>(body);
        var mobState = SComp<MobStateComponent>(body);
        var profile = _physiology.GetProfile(body);

        Assert.That(_scheduler.ProcessNow(body, TimeSpan.Zero), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(physiology.Condition, Is.EqualTo(NCPhysiologicalCondition.Stable));
            Assert.That(physiology.Consciousness, Is.EqualTo(NCConsciousnessState.Alert));
            Assert.That(mobState.CurrentState, Is.EqualTo(MobState.Alive));
        });

        circulation.BloodVolume = profile.NominalBloodVolume * profile.CriticalPerfusionThreshold * 0.9f;
        Assert.That(_scheduler.ProcessNow(body, TimeSpan.Zero), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(physiology.Condition, Is.EqualTo(NCPhysiologicalCondition.Critical));
            Assert.That(physiology.Consciousness, Is.GreaterThanOrEqualTo(NCConsciousnessState.Unconscious));
            Assert.That(mobState.CurrentState, Is.EqualTo(MobState.Critical));
        });

        circulation.BloodVolume = profile.NominalBloodVolume * profile.ArrestPerfusionThreshold * 0.5f;
        Assert.That(_scheduler.ProcessNow(body, TimeSpan.FromSeconds(1)), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(physiology.Condition, Is.EqualTo(NCPhysiologicalCondition.ClinicalArrest));
            Assert.That(physiology.Consciousness, Is.EqualTo(NCConsciousnessState.Comatose));
            Assert.That(physiology.Irreversible, Is.False);
            Assert.That(mobState.CurrentState, Is.EqualTo(MobState.Dead));
        });

        Assert.That(_scheduler.ProcessNow(body, profile.RevivableDeathDuration - TimeSpan.FromSeconds(2)), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(physiology.Condition, Is.EqualTo(NCPhysiologicalCondition.ClinicalArrest));
            Assert.That(physiology.Irreversible, Is.False);
            Assert.That(mobState.CurrentState, Is.EqualTo(MobState.Dead));
        });

        circulation.BloodVolume = profile.NominalBloodVolume;
        Assert.That(_scheduler.ProcessNow(body, TimeSpan.FromSeconds(1)), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(physiology.Condition, Is.Not.EqualTo(NCPhysiologicalCondition.Dead));
            Assert.That(physiology.Condition, Is.Not.EqualTo(NCPhysiologicalCondition.ClinicalArrest));
            Assert.That(physiology.TimeInClinicalDeath, Is.EqualTo(TimeSpan.Zero));
            Assert.That(physiology.Irreversible, Is.False);
            Assert.That(mobState.CurrentState, Is.EqualTo(MobState.Critical));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ClinicalDeathBecomesIrreversibleAfterConfiguredWindow()
    {
        var body = SSpawn(HumanMob);
        var circulation = SComp<NCCirculationComponent>(body);
        var physiology = SComp<NCPhysiologyComponent>(body);
        var mobState = SComp<MobStateComponent>(body);
        var profile = _physiology.GetProfile(body);

        circulation.BloodVolume = profile.NominalBloodVolume * profile.ArrestPerfusionThreshold * 0.5f;
        Assert.That(_scheduler.ProcessNow(body, profile.RevivableDeathDuration), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(physiology.Condition, Is.EqualTo(NCPhysiologicalCondition.Dead));
            Assert.That(physiology.Consciousness, Is.EqualTo(NCConsciousnessState.BrainDead));
            Assert.That(physiology.Irreversible, Is.True);
            Assert.That(mobState.CurrentState, Is.EqualTo(MobState.Dead));
        });

        circulation.BloodVolume = profile.NominalBloodVolume;
        Assert.That(_scheduler.ProcessNow(body, TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(physiology.Condition, Is.EqualTo(NCPhysiologicalCondition.Dead));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ExplicitDeathBoundaryCannotBeRevived()
    {
        var body = SSpawn(HumanMob);

        Assert.That(_physiology.ForceIrreversibleDeath(body), Is.True);
        Assert.That(_scheduler.ProcessNow(body, TimeSpan.Zero), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(SComp<NCPhysiologyComponent>(body).Irreversible, Is.True);
            Assert.That(SComp<NCPhysiologyComponent>(body).Condition, Is.EqualTo(NCPhysiologicalCondition.Dead));
            Assert.That(SComp<MobStateComponent>(body).CurrentState, Is.EqualTo(MobState.Dead));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void UpstreamMobThresholdProposalCannotOverrideAuthoritativePhysiology()
    {
        var body = SSpawn(HumanMob);
        var mobState = SComp<MobStateComponent>(body);
        var update = new UpdateMobStateEvent(body, mobState, MobState.Dead);
        SEntMan.EventBus.RaiseLocalEvent(body, ref update);

        Assert.Multiple(() =>
        {
            Assert.That(SComp<NCPhysiologyComponent>(body).Condition,
                Is.EqualTo(NCPhysiologicalCondition.Stable));
            Assert.That(update.State, Is.EqualTo(MobState.Alive));
            Assert.That(mobState.CurrentState, Is.EqualTo(MobState.Alive));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void SchedulerKeepsEarliestDeadlineAndSupportsIndexedRemoval()
    {
        var first = SSpawn(HumanMob);
        var second = SSpawn(HumanMob);
        var third = SSpawn(HumanMob);

        // Normalize any spawn-time invalidation before exercising only the scheduler's active set.
        Assert.That(_scheduler.ProcessNow(first, TimeSpan.Zero), Is.True);
        Assert.That(_scheduler.ProcessNow(second, TimeSpan.Zero), Is.True);
        Assert.That(_scheduler.ProcessNow(third, TimeSpan.Zero), Is.True);

        _scheduler.Schedule(first, TimeSpan.FromSeconds(30));
        _scheduler.Schedule(second, TimeSpan.FromSeconds(20));
        _scheduler.Schedule(third, TimeSpan.FromSeconds(10));
        _scheduler.Schedule(first, TimeSpan.FromSeconds(5));
        _scheduler.Schedule(second, TimeSpan.FromSeconds(40));

        Assert.Multiple(() =>
        {
            Assert.That(_scheduler.GetRemainingDelay(first), Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(_scheduler.GetRemainingDelay(second), Is.EqualTo(TimeSpan.FromSeconds(20)));
            Assert.That(_scheduler.GetRemainingDelay(third), Is.EqualTo(TimeSpan.FromSeconds(10)));
        });

        // Explicit evaluation removes an arbitrary heap entry without disturbing other deadlines.
        Assert.That(_scheduler.ProcessNow(second, TimeSpan.Zero), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(_scheduler.GetRemainingDelay(first), Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(_scheduler.GetRemainingDelay(second), Is.Null);
            Assert.That(_scheduler.GetRemainingDelay(third), Is.EqualTo(TimeSpan.FromSeconds(10)));
        });

        // Equal immediate deadlines must both be consumed, while the unrelated future entry remains queued.
        _scheduler.Schedule(first, TimeSpan.Zero);
        _scheduler.Schedule(second, TimeSpan.Zero);
        _scheduler.Update(0f);
        Assert.Multiple(() =>
        {
            Assert.That(_scheduler.GetRemainingDelay(first), Is.Null);
            Assert.That(_scheduler.GetRemainingDelay(second), Is.Null);
            Assert.That(_scheduler.GetRemainingDelay(third), Is.EqualTo(TimeSpan.FromSeconds(10)));
        });

        Assert.That(_scheduler.ProcessNow(third, TimeSpan.Zero), Is.True);
        Assert.That(_scheduler.GetRemainingDelay(third), Is.Null);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void FreshBleedingAfterEventDrivenIdleIsNotIntegratedRetroactively()
    {
        var body = SSpawn(HumanMob);
        var circulation = SComp<NCCirculationComponent>(body);
        var physiology = SComp<NCPhysiologyComponent>(body);

        // A healthy body leaves the scheduler and may remain idle for an arbitrary amount of world time.
        Assert.That(_scheduler.ProcessNow(body, TimeSpan.Zero), Is.True);
        Assert.That(_scheduler.GetRemainingDelay(body), Is.Null);
        var initialBlood = circulation.BloodVolume;
        circulation.LastEvaluation -= TimeSpan.FromMinutes(5);
        var staleAnchor = circulation.LastEvaluation;

        var first = _wounds.ApplyTrauma(body, CreatePistolLegImpact(body, 1));
        var rebasedAnchor = circulation.LastEvaluation;
        var second = _wounds.ApplyTrauma(body, CreatePistolLegImpact(body, 2));

        Assert.Multiple(() =>
        {
            Assert.That(first.Created, Is.True);
            Assert.That(second.Created, Is.True);
            Assert.That(rebasedAnchor, Is.GreaterThan(staleAnchor));
            Assert.That(circulation.LastEvaluation, Is.EqualTo(rebasedAnchor));
            Assert.That(circulation.BloodVolume, Is.EqualTo(initialBlood).Within(0.001f));
        });

        // Consuming the due-now boundary must integrate zero time, not the five-minute healthy interval.
        _scheduler.Update(0f);
        Assert.Multiple(() =>
        {
            Assert.That(circulation.BloodVolume, Is.EqualTo(initialBlood).Within(0.001f));
            Assert.That(physiology.Condition, Is.Not.EqualTo(NCPhysiologicalCondition.Critical));
            Assert.That(physiology.Consciousness, Is.LessThan(NCConsciousnessState.Unconscious));
        });

        // The same two critical local wounds still bleed harshly from this point forward, but the first
        // few real seconds cannot contain the preceding five minutes of healthy idle time.
        Assert.That(_scheduler.ProcessNow(body, TimeSpan.FromSeconds(5)), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(circulation.BloodVolume, Is.LessThan(initialBlood));
            Assert.That(circulation.BloodVolume, Is.GreaterThan(4000f));
            Assert.That(physiology.Consciousness, Is.LessThan(NCConsciousnessState.Unconscious));
        });
    }

    private static NCCombatTraumaRequest CreatePistolLegImpact(EntityUid body, uint impactSequence)
    {
        var damage = FixedPoint2.New(16f);
        return new NCCombatTraumaRequest(
            NCActionResolutionId.New(),
            impactSequence,
            impactSequence,
            body,
            null,
            null,
            null,
            new ProtoId<NCTargetZonePrototype>("NCZoneLegRight"),
            new ProtoId<OrganCategoryPrototype>("LegRight"),
            new ProtoId<NCTraumaProfilePrototype>("BallisticProjectile"),
            new DamageSpecifier(),
            damage,
            FixedPoint2.Zero,
            damage,
            FixedPoint2.Zero,
            Vector2.Zero,
            Vector2.UnitX,
            NCTraumaFlags.ArmorPenetrated,
            0.1f,
            TimeSpan.Zero);
    }
}
