// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Bleeding;
using Content.Server._NC.Medical.Pharmacology;
using Content.Server._NC.Medical.Physiology;
using Content.Server._NC.Medical.Systemic;
using Content.Server._NC.Medical.Treatment;
using Content.Server._NC.Medical.Wounds;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Trauma;
using Content.Shared._NC.Medical.Treatment;
using Content.Shared._NC.Medical.Systemic;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

[TestFixture]
[TestOf(typeof(NCWoundProgressionSystem))]
public sealed class NCWoundProgressionSystemTest : GameTest
{
    private static readonly EntProtoId HumanMob = "MobHuman";

    [SidedDependency(Side.Server)] private NCAnatomySystem _anatomy = default!;
    [SidedDependency(Side.Server)] private NCBleedingSystem _bleeding = default!;
    [SidedDependency(Side.Server)] private NCPharmacologySystem _pharmacology = default!;
    [SidedDependency(Side.Server)] private NCPhysiologySchedulerSystem _scheduler = default!;
    [SidedDependency(Side.Server)] private NCSystemicSystem _systemic = default!;
    [SidedDependency(Side.Server)] private NCTreatmentSystem _treatment = default!;
    [SidedDependency(Side.Server)] private NCWoundProgressionSystem _progression = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void SuturedWoundResolvesIntoPersistentCompactScar()
    {
        var body = SSpawn(HumanMob);
        var woundId = CreateWound(body, "NCZoneArmLeft", "ArmLeft", "SlashingImpact", 6f);
        var wounds = SComp<NCWoundContainerComponent>(body);
        var wound = wounds.Wounds[woundId];
        wound.ClosureState = NCWoundClosureState.Sutured;
        wound.Stage = NCWoundStage.Closed;
        Assert.That(_anatomy.TryGetOrgan(body, "ArmLeft", out var arm), Is.True);
        var integrityBeforeHealing = SComp<NCOrganIntegrityComponent>(arm).CurrentIntegrity;

        var result = _progression.AdvanceBody(
            body,
            TimeSpan.FromHours(2),
            TimeSpan.FromHours(2));

        Assert.Multiple(() =>
        {
            Assert.That(result.Accepted, Is.True);
            Assert.That(result.ScarredWounds, Is.EqualTo(1));
            Assert.That(wounds.Wounds, Does.Not.ContainKey(woundId));
            Assert.That(wounds.Scars, Has.Count.EqualTo(1));
            Assert.That(wounds.Scars[0].SourceWoundId, Is.EqualTo(woundId));
            Assert.That(wounds.Scars[0].TargetZone.Id, Is.EqualTo("NCZoneArmLeft"));
            Assert.That(SComp<NCBleedingComponent>(body).Sources.Any(source => source.WoundId == woundId),
                Is.False);
            Assert.That(SComp<NCOrganIntegrityComponent>(arm).CurrentIntegrity,
                Is.GreaterThan(integrityBeforeHealing));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void InfectionProgressionAndAntibioticResponseAreDeterministic()
    {
        var untreated = SSpawn(HumanMob);
        var treated = SSpawn(HumanMob);
        var immunosuppressed = SSpawn(HumanMob);
        var untreatedId = CreateWound(
            untreated,
            "NCZoneLegLeft",
            "LegLeft",
            "SlashingImpact",
            6f,
            contamination: 1f);
        var treatedId = CreateWound(
            treated,
            "NCZoneLegLeft",
            "LegLeft",
            "SlashingImpact",
            6f,
            contamination: 1f);
        var immunosuppressedId = CreateWound(
            immunosuppressed,
            "NCZoneLegLeft",
            "LegLeft",
            "SlashingImpact",
            6f,
            contamination: 1f);

        Assert.That(_pharmacology.ApplyDose(treated, "NCDrugBroadAntibiotic", 5f).Accepted, Is.True);
        Assert.That(_pharmacology.ApplyDose(immunosuppressed, "NCDrugImmunosuppressant", 5f).Accepted, Is.True);
        // NC: The semi-release balance reaches localized infection on a round-scale timer. Keep this
        // comparison below saturation so antibiotic and immunosuppression effects remain observable.
        var elapsed = TimeSpan.FromMinutes(5);
        var untreatedResult = _progression.AdvanceBody(untreated, elapsed, elapsed);
        var treatedResult = _progression.AdvanceBody(treated, elapsed, elapsed);
        var immunosuppressedResult = _progression.AdvanceBody(immunosuppressed, elapsed, elapsed);
        var untreatedWound = SComp<NCWoundContainerComponent>(untreated).Wounds[untreatedId];
        var treatedWound = SComp<NCWoundContainerComponent>(treated).Wounds[treatedId];
        var immunosuppressedWound = SComp<NCWoundContainerComponent>(immunosuppressed)
            .Wounds[immunosuppressedId];

        Assert.Multiple(() =>
        {
            Assert.That(untreatedResult.Accepted, Is.True);
            Assert.That(treatedResult.Accepted, Is.True);
            Assert.That(immunosuppressedResult.Accepted, Is.True);
            Assert.That(untreatedWound.InfectionStage, Is.GreaterThanOrEqualTo(NCInfectionStage.Localized));
            Assert.That(untreatedWound.Stage, Is.EqualTo(NCWoundStage.Infected));
            Assert.That(treatedWound.Infection, Is.LessThan(untreatedWound.Infection));
            Assert.That(immunosuppressedWound.Infection, Is.GreaterThan(untreatedWound.Infection));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void SystemicInfectionCreatesSepsisAndAntibioticsReduceItsBurden()
    {
        var body = SSpawn(HumanMob);
        var woundId = CreateWound(
            body,
            "NCZoneChest",
            "Thorax",
            "SlashingImpact",
            8f,
            contamination: 1f);
        var wound = SComp<NCWoundContainerComponent>(body).Wounds[woundId];
        wound.Infection = 0.9f;
        wound.InfectionStage = NCInfectionStage.Sepsis;
        wound.Stage = NCWoundStage.Infected;

        Assert.That(_progression.AdvanceBody(body, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(100)).Accepted,
            Is.True);
        var untreatedBurden = _systemic.GetBurden(body, NCSystemicChannel.Sepsis);
        Assert.That(untreatedBurden, Is.GreaterThan(0f));

        Assert.That(_pharmacology.ApplyDose(body, "NCDrugBroadAntibiotic", 5f).Accepted, Is.True);
        Assert.That(_progression.AdvanceBody(body, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(200)).Accepted,
            Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(_systemic.GetBurden(body, NCSystemicChannel.Sepsis), Is.LessThan(untreatedBurden));
            Assert.That(_systemic.GetPhysiologyEffects(body).ShockContribution, Is.GreaterThan(0f));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void PharmacologyScalesActualExternalAndInternalLossWithoutMutatingRates()
    {
        var untreated = SSpawn(HumanMob);
        var treated = SSpawn(HumanMob);
        CreateWound(untreated, "NCZoneChest", "Thorax", "PiercingImpact", 6f);
        CreateWound(treated, "NCZoneChest", "Thorax", "PiercingImpact", 6f);
        Assert.That(_pharmacology.ApplyDose(treated, "NCDrugProcoagulant", 4f).Accepted, Is.True);

        var untreatedBleeding = SComp<NCBleedingComponent>(untreated);
        var treatedBleeding = SComp<NCBleedingComponent>(treated);
        var untreatedExternalRate = untreatedBleeding.ActiveExternalRate;
        var untreatedInternalRate = untreatedBleeding.ActiveInternalRate;
        var treatedExternalRate = treatedBleeding.ActiveExternalRate;
        var treatedInternalRate = treatedBleeding.ActiveInternalRate;
        var multiplier = _pharmacology.GetModifiers(treated).BleedingMultiplier;

        var untreatedLoss = _bleeding.AdvanceBody(untreated, TimeSpan.FromSeconds(1));
        var treatedLoss = _bleeding.AdvanceBody(treated, TimeSpan.FromSeconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(multiplier, Is.LessThan(1f));
            Assert.That(treatedLoss, Is.EqualTo(untreatedLoss * multiplier).Within(0.001f));
            Assert.That(treatedBleeding.InternalBloodLoss,
                Is.EqualTo(untreatedBleeding.InternalBloodLoss * multiplier).Within(0.001f));
            Assert.That(treatedBleeding.ActiveExternalRate, Is.EqualTo(treatedExternalRate));
            Assert.That(treatedBleeding.ActiveInternalRate, Is.EqualTo(treatedInternalRate));
            Assert.That(treatedExternalRate, Is.EqualTo(untreatedExternalRate).Within(0.001f));
            Assert.That(treatedInternalRate, Is.EqualTo(untreatedInternalRate).Within(0.001f));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ScheduledDrugClearancePrecedesInfectionProgression()
    {
        var body = SSpawn(HumanMob);
        var woundId = CreateWound(
            body,
            "NCZoneArmLeft",
            "ArmLeft",
            // A fully contaminated slash crosses the localized-infection threshold in one hour once
            // the antibiotic has cleared. Thermal wounds intentionally scale incoming contamination.
            "SlashingImpact",
            6f,
            contamination: 1f);
        Assert.That(_pharmacology.ApplyDose(body, "NCDrugBroadAntibiotic", 5f).Accepted, Is.True);

        Assert.That(_scheduler.ProcessNow(body, TimeSpan.FromHours(1)), Is.True);
        var wound = SComp<NCWoundContainerComponent>(body).Wounds[woundId];

        Assert.Multiple(() =>
        {
            Assert.That(_pharmacology.GetModifiers(body).AntibioticPower, Is.Zero,
                "The dose must clear at the shared boundary before wound progression reads modifiers.");
            Assert.That(wound.InfectionStage, Is.GreaterThanOrEqualTo(NCInfectionStage.Localized),
                "A cleared antibiotic must not suppress this boundary's infection growth.");
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void SaturatedBandageStopsControllingBleedingAndContaminatesWound()
    {
        var body = SSpawn(HumanMob);
        var woundId = CreateWound(body, "NCZoneArmRight", "ArmRight", "SlashingImpact", 6f);
        Assert.That(_treatment.BandageWound(body, woundId).Succeeded, Is.True);
        var wounds = SComp<NCWoundContainerComponent>(body);
        var wound = wounds.Wounds[woundId];
        var contaminationBefore = wound.Contamination;
        var treatment = SComp<NCTreatmentStateComponent>(body).WoundTreatments[woundId];
        treatment.BandageAbsorptionRemaining = 1f;
        var source = SComp<NCBleedingComponent>(body).Sources.Single(candidate =>
            candidate.WoundId == woundId && candidate.Channel != NCBleedingChannel.Internal);
        var controlledRate = source.CurrentRate;

        var result = _progression.AdvanceBody(
            body,
            TimeSpan.FromMinutes(1),
            treatment.BandagedAt + TimeSpan.FromMinutes(1));

        Assert.Multiple(() =>
        {
            Assert.That(result.SaturatedBandages, Is.EqualTo(1));
            Assert.That(wound.TreatmentFlags.HasFlag(NCWoundTreatmentFlags.Bandaged), Is.False);
            Assert.That(SComp<NCTreatmentStateComponent>(body).WoundTreatments[woundId]
                .BandageAbsorptionRemaining, Is.Zero);
            Assert.That(wound.Contamination, Is.GreaterThan(contaminationBefore));
            Assert.That(SComp<NCBleedingComponent>(body).Sources.Single(candidate =>
                candidate.WoundId == woundId && candidate.Channel != NCBleedingChannel.Internal).CurrentRate,
                Is.GreaterThan(controlledRate));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ReimpactReopensClosureAndTourniquetCausesTimedIschemia()
    {
        var body = SSpawn(HumanMob);
        var closedId = CreateWound(body, "NCZoneArmLeft", "ArmLeft", "SlashingImpact", 6f);
        var wounds = SComp<NCWoundContainerComponent>(body);
        var closed = wounds.Wounds[closedId];
        closed.ClosureState = NCWoundClosureState.Sutured;
        closed.Stage = NCWoundStage.Closed;
        closed.HealingProgress = 0.5f;
        closed.PeakHealingProgress = 0.5f;

        CreateWound(body, "NCZoneArmLeft", "ArmLeft", "SlashingImpact", 14f);
        var reopenResult = _progression.AdvanceBody(body, TimeSpan.Zero, TimeSpan.Zero);
        Assert.Multiple(() =>
        {
            Assert.That(reopenResult.ReopenedWounds, Is.EqualTo(1));
            Assert.That(closed.ClosureState, Is.EqualTo(NCWoundClosureState.Open));
            Assert.That(closed.Stage, Is.EqualTo(NCWoundStage.Reopened));
            Assert.That(closed.HealingProgress, Is.LessThan(0.5f));
        });

        Assert.That(_treatment.SetTourniquet(body, "NCZoneArmLeft", TimeSpan.Zero).Succeeded, Is.True);
        Assert.That(_anatomy.TryGetOrgan(body, "ArmLeft", out var arm), Is.True);
        var integrityBeforeIschemia = SComp<NCOrganIntegrityComponent>(arm).CurrentIntegrity;
        var nerveBeforeIschemia = SComp<NCNerveComponent>(arm).NeuralIntegrity;

        var ischemiaResult = _progression.AdvanceBody(
            body,
            TimeSpan.FromMinutes(100),
            TimeSpan.FromMinutes(100));

        Assert.Multiple(() =>
        {
            // NC: Infection may legitimately supersede the broad Treated stage during the 100-minute
            // ischemia interval. The treatment flag is the authoritative proof that the tourniquet remains.
            Assert.That(closed.TreatmentFlags.HasFlag(NCWoundTreatmentFlags.Tourniqueted), Is.True);
            Assert.That(ischemiaResult.IschemicRegions, Is.GreaterThan(0));
            Assert.That(SComp<NCOrganIntegrityComponent>(arm).CurrentIntegrity,
                Is.LessThan(integrityBeforeIschemia));
            Assert.That(SComp<NCNerveComponent>(arm).NeuralIntegrity,
                Is.LessThan(nerveBeforeIschemia));
            Assert.That(closed.Necrosis, Is.GreaterThan(0f));
        });
    }

    private Guid CreateWound(
        EntityUid body,
        ProtoId<NCTargetZonePrototype> zone,
        ProtoId<OrganCategoryPrototype> region,
        ProtoId<NCTraumaProfilePrototype> traumaProfile,
        float postArmorDamage,
        float contamination = 0f)
    {
        var before = SEntMan.TryGetComponent<NCWoundContainerComponent>(body, out var existing)
            ? existing.Wounds.Keys.ToHashSet()
            : [];
        var request = new NCCombatTraumaRequest(
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
            contamination,
            TimeSpan.Zero);
        var trauma = new NCCombatTraumaRequestedEvent(request);
        SEntMan.EventBus.RaiseLocalEvent(body, ref trauma);
        Assert.That(trauma.Applied, Is.True);
        return SComp<NCWoundContainerComponent>(body).Wounds.Keys.Single(id => !before.Contains(id));
    }
}
