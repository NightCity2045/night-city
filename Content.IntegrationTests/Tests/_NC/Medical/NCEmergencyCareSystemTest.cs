// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Emergency;
using Content.Server._NC.Medical.Physiology;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Emergency;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Standing;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

[TestFixture]
[TestOf(typeof(NCEmergencyCareSystem))]
public sealed class NCEmergencyCareSystemTest : GameTest
{
    private static readonly EntProtoId HumanMob = "MobHuman";

    [SidedDependency(Side.Server)] private NCEmergencyCareSystem _emergency = default!;
    [SidedDependency(Side.Server)] private NCPhysiologySystem _physiology = default!;
    [SidedDependency(Side.Server)] private NCPhysiologySchedulerSystem _scheduler = default!;
    [SidedDependency(Side.Server)] private StandingStateSystem _standing = default!;
    [SidedDependency(Side.Server)] private DamageableSystem _damageable = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void CprSlowsButDoesNotStopBrainDecline()
    {
        var unsupported = SSpawn(HumanMob);
        var assisted = SSpawn(HumanMob);
        var unsupportedCirculation = SComp<NCCirculationComponent>(unsupported);
        var assistedCirculation = SComp<NCCirculationComponent>(assisted);
        var profile = _physiology.GetProfile(unsupported);
        unsupportedCirculation.BloodVolume = profile.NominalBloodVolume * 0.1f;
        assistedCirculation.BloodVolume = profile.NominalBloodVolume * 0.1f;
        _scheduler.ProcessNow(unsupported, TimeSpan.Zero);
        _scheduler.ProcessNow(assisted, TimeSpan.Zero);
        Assert.That(_standing.Down(assisted, playSound: false, dropHeldItems: false, force: true), Is.True);

        var cpr = _emergency.PerformCpr(assisted, TimeSpan.Zero);
        _scheduler.ProcessNow(unsupported, TimeSpan.FromSeconds(3));
        _scheduler.ProcessNow(assisted, TimeSpan.FromSeconds(3));

        var unsupportedBrainTime = SComp<NCPhysiologyComponent>(unsupported).TimeBelowCriticalPerfusion;
        var assistedBrainTime = SComp<NCPhysiologyComponent>(assisted).TimeBelowCriticalPerfusion;
        Assert.Multiple(() =>
        {
            Assert.That(cpr.Outcome, Is.EqualTo(NCEmergencyCareOutcome.Applied));
            Assert.That(assistedCirculation.Perfusion, Is.GreaterThan(unsupportedCirculation.Perfusion));
            Assert.That(assistedBrainTime, Is.GreaterThan(TimeSpan.Zero));
            Assert.That(assistedBrainTime, Is.LessThan(unsupportedBrainTime));
            Assert.That(SComp<NCEmergencyCareStateComponent>(assisted).CprRemaining,
                Is.EqualTo(TimeSpan.FromSeconds(3)));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ExternallyPoweredDefibrillationRejectsWrongRhythmWithoutParallelCharges()
    {
        var body = SSpawn(HumanMob);
        var defibrillator = SSpawn("DefibrillatorOneHandedUnpowered");
        var circulation = SComp<NCCirculationComponent>(body);
        var item = SComp<NCEmergencyCareItemComponent>(defibrillator);
        var bloodBefore = circulation.BloodVolume;
        circulation.HeartRhythm = NCHeartRhythm.Normal;

        var result = _emergency.DefibrillateWithExternalPower(body, defibrillator, TimeSpan.Zero);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(NCEmergencyCareOutcome.WrongRhythm));
            Assert.That(item.ChargesRemaining, Is.Zero);
            Assert.That(result.ChargesConsumed, Is.EqualTo(1));
            Assert.That(circulation.HeartRhythm, Is.EqualTo(NCHeartRhythm.Normal));
            Assert.That(circulation.BloodVolume, Is.EqualTo(bloodBefore));
            Assert.That(SEntMan.TryGetComponent<NCWoundContainerComponent>(body, out var wounds) &&
                        wounds.Wounds.Count > 0,
                Is.False);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ShockableArrestConvertsDeterministicallyAndBurnNeverTouchesLegacyHp()
    {
        var body = SSpawn(HumanMob);
        var defibrillator = SSpawn("DefibrillatorOneHandedUnpowered");
        var circulation = SComp<NCCirculationComponent>(body);
        var damageable = SComp<DamageableComponent>(body);
        var legacyBefore = _damageable.GetTotalDamage((body, damageable));
        var bloodBefore = circulation.BloodVolume;
        circulation.HeartRhythm = NCHeartRhythm.ShockableArrest;

        var result = _emergency.DefibrillateWithExternalPower(body, defibrillator, TimeSpan.Zero);
        var wounds = SComp<NCWoundContainerComponent>(body);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(NCEmergencyCareOutcome.Applied));
            Assert.That(circulation.HeartRhythm, Is.EqualTo(NCHeartRhythm.Normal));
            Assert.That(circulation.BloodVolume, Is.EqualTo(bloodBefore));
            Assert.That(SComp<NCEmergencyCareItemComponent>(defibrillator).ChargesRemaining, Is.Zero);
            Assert.That(wounds.Wounds.Values.Any(wound =>
                wound.TargetZone.Id == "NCZoneChest" && wound.WoundType == NCWoundType.Thermal), Is.True);
            Assert.That(_damageable.GetTotalDamage((body, damageable)), Is.EqualTo(legacyBefore));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void OxygenRaisesTransportOnlyWhenPerfusionExists()
    {
        var perfused = SSpawn(HumanMob);
        var arrested = SSpawn(HumanMob);
        SetVitalCapacity(perfused, NCOrganFunctionType.Respiration, 0.2f);
        SetVitalCapacity(arrested, NCOrganFunctionType.Respiration, 0.2f);
        SComp<NCCirculationComponent>(arrested).BloodVolume = 0f;
        _scheduler.ProcessNow(perfused, TimeSpan.Zero);
        _scheduler.ProcessNow(arrested, TimeSpan.Zero);
        var baseline = SComp<NCPhysiologyComponent>(perfused).Oxygenation;

        var perfusedResult = _emergency.ApplyOxygen(perfused, SSpawn("NCOxygenDeliveryKit"), TimeSpan.Zero);
        var arrestedResult = _emergency.ApplyOxygen(arrested, SSpawn("NCOxygenDeliveryKit"), TimeSpan.Zero);
        _scheduler.ProcessNow(perfused, TimeSpan.FromSeconds(1));
        _scheduler.ProcessNow(arrested, TimeSpan.FromSeconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(perfusedResult.Outcome, Is.EqualTo(NCEmergencyCareOutcome.Applied));
            Assert.That(arrestedResult.Outcome, Is.EqualTo(NCEmergencyCareOutcome.Applied));
            Assert.That(SComp<NCPhysiologyComponent>(perfused).Oxygenation, Is.GreaterThan(baseline));
            Assert.That(SComp<NCCirculationComponent>(arrested).Perfusion, Is.Zero);
            Assert.That(SComp<NCPhysiologyComponent>(arrested).Oxygenation, Is.Zero);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void CrystalloidChangesEffectiveVolumeWithinConfiguredCap()
    {
        var body = SSpawn(HumanMob);
        var circulation = SComp<NCCirculationComponent>(body);
        circulation.BloodVolume = 2500f;
        _scheduler.ProcessNow(body, TimeSpan.Zero);
        var effectiveBefore = circulation.EffectiveCirculatingVolume;

        var result = _emergency.InfuseCrystalloid(body, SSpawn("NCCrystalloidBag"), TimeSpan.Zero);
        _scheduler.ProcessNow(body, TimeSpan.Zero);
        var emergencyState = SComp<NCEmergencyCareStateComponent>(body);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(NCEmergencyCareOutcome.Applied));
            Assert.That(emergencyState.CrystalloidVolume, Is.EqualTo(500f));
            Assert.That(circulation.EffectiveCirculatingVolume, Is.EqualTo(effectiveBefore + 500f));
            Assert.That(emergencyState.CrystalloidVolume, Is.LessThanOrEqualTo(2000f));
            Assert.That(circulation.BloodVolume, Is.EqualTo(2500f));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void IncompatibleBloodIsRejectedAndCompatibleBloodRespectsCap()
    {
        var body = SSpawn(HumanMob);
        var circulation = SComp<NCCirculationComponent>(body);
        var profile = _physiology.GetProfile(body);
        circulation.BloodVolume = profile.NominalBloodVolume - 200f;
        circulation.TransfusionCompatibility = "NC-A-Positive";
        var incompatible = SSpawn("NCBloodBagBPositive");
        var compatible = SSpawn("NCBloodBagAPositive");

        var rejected = _emergency.TransfuseBlood(body, incompatible, TimeSpan.Zero);
        var bloodAfterRejected = circulation.BloodVolume;
        var accepted = _emergency.TransfuseBlood(body, compatible, TimeSpan.Zero);

        Assert.Multiple(() =>
        {
            Assert.That(rejected.Outcome, Is.EqualTo(NCEmergencyCareOutcome.IncompatibleBlood));
            Assert.That(SComp<NCEmergencyCareItemComponent>(incompatible).ChargesRemaining, Is.EqualTo(1));
            Assert.That(bloodAfterRejected, Is.EqualTo(profile.NominalBloodVolume - 200f));
            Assert.That(accepted.Outcome, Is.EqualTo(NCEmergencyCareOutcome.Applied));
            Assert.That(circulation.BloodVolume, Is.EqualTo(profile.NominalBloodVolume));
            Assert.That(SComp<NCEmergencyCareItemComponent>(compatible).ChargesRemaining, Is.Zero);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void DuplicateActionCannotMutateVolumeChargeAuditOrMedicalRevision()
    {
        var body = SSpawn(HumanMob);
        var bag = SSpawn("NCCrystalloidBag");
        var actionId = NCActionResolutionId.New();
        var context = new NCEmergencyCareActionContext(
            actionId,
            TimeSpan.FromMinutes(1),
            HumanMob,
            new EntProtoId("NCCrystalloidBag"),
            new ProtoId<NCEmergencyCareCapabilityPrototype>("NCCrystalloidInfusionCapability"));

        var first = _emergency.InfuseCrystalloid(body, bag, context);
        var state = SComp<NCEmergencyCareStateComponent>(body);
        var volumeAfterFirst = state.CrystalloidVolume;
        var chargeAfterFirst = SComp<NCEmergencyCareItemComponent>(bag).ChargesRemaining;
        var auditsAfterFirst = state.AuditHistory.Count;
        var revisionAfterFirst = SComp<NCMedicalBodyComponent>(body).MedicalRevision;
        var duplicate = _emergency.InfuseCrystalloid(body, bag, context);

        Assert.Multiple(() =>
        {
            Assert.That(first.Outcome, Is.EqualTo(NCEmergencyCareOutcome.Applied));
            Assert.That(duplicate.Outcome, Is.EqualTo(NCEmergencyCareOutcome.DuplicateAction));
            Assert.That(state.CrystalloidVolume, Is.EqualTo(volumeAfterFirst));
            Assert.That(SComp<NCEmergencyCareItemComponent>(bag).ChargesRemaining, Is.EqualTo(chargeAfterFirst));
            Assert.That(state.ProcessedActions.Count(id => id == actionId), Is.EqualTo(1));
            Assert.That(state.AuditHistory, Has.Count.EqualTo(auditsAfterFirst));
            Assert.That(SComp<NCMedicalBodyComponent>(body).MedicalRevision, Is.EqualTo(revisionAfterFirst));
        });
    }

    private void SetVitalCapacity(EntityUid body, NCOrganFunctionType type, float capacity)
    {
        var bodyComponent = SComp<BodyComponent>(body);
        Assert.That(bodyComponent.Organs, Is.Not.Null);
        var found = false;
        foreach (var organ in bodyComponent.Organs!.ContainedEntities)
        {
            if (!SEntMan.TryGetComponent<NCOrganFunctionComponent>(organ, out var function) ||
                function.FunctionType != type)
            {
                continue;
            }

            function.CurrentCapacity = capacity;
            found = true;
        }

        Assert.That(found, Is.True, $"Expected a {type} organ on MobHuman.");
    }
}
