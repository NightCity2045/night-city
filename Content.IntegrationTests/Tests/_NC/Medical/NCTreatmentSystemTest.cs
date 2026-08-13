// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using System.Reflection;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Emergency;
using Content.Server._NC.Medical.Physiology;
using Content.Server._NC.Medical.Treatment;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Emergency;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Trauma;
using Content.Shared._NC.Medical.Treatment;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Content.Shared.Stacks;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._NC.Medical;

[TestFixture]
[TestOf(typeof(NCTreatmentSystem))]
public sealed class NCTreatmentSystemTest : GameTest
{
    private static readonly EntProtoId HumanMob = "MobHuman";

    [SidedDependency(Side.Server)] private NCTreatmentSystem _treatment = default!;
    [SidedDependency(Side.Server)] private NCAnatomySystem _anatomy = default!;
    [SidedDependency(Side.Server)] private NCMedicalBodySystem _medicalBodies = default!;
    [SidedDependency(Side.Server)] private NCPhysiologySchedulerSystem _scheduler = default!;
    [SidedDependency(Side.Server)] private NCEmergencyCareSystem _emergency = default!;
    [SidedDependency(Side.Server)] private IGameTiming _timing = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void VenousPressureAndBandageReduceMeasuredRateWithoutErasingTrauma()
    {
        var body = SSpawn(HumanMob);
        var woundId = CreateWound(body, "NCZoneArmLeft", "ArmLeft", "SlashingImpact", 6f);
        var container = SComp<NCWoundContainerComponent>(body);
        var wound = container.Wounds[woundId];
        var source = SComp<NCBleedingComponent>(body).Sources.Single(candidate =>
            candidate.WoundId == woundId && candidate.Channel == NCBleedingChannel.ExternalVenous);
        var originalIntegrity = wound.IntegrityDamage;
        var originalSeverity = wound.Severity;
        var originalCount = container.Wounds.Count;
        var baseRate = source.BaseRate;

        Assert.That(_scheduler.ProcessNow(body, TimeSpan.Zero), Is.True);
        _medicalBodies.TryClearDirty(body, NCMedicalDirtyDomains.All);
        var revision = SComp<NCMedicalBodyComponent>(body).MedicalRevision;

        // Integration pools retain their simulation clock between fixtures. Treatment timestamps are absolute,
        // so anchor this lifecycle to the current server time instead of assuming a fresh zero-time process.
        var treatmentStartedAt = _timing.CurTime;
        var pressure = _treatment.ApplyPressure(body, woundId, treatmentStartedAt);
        var pressureRate = source.CurrentRate;
        var pressureActiveAfterApply = source.PressureApplied &&
                                       wound.TreatmentFlags.HasFlag(NCWoundTreatmentFlags.Pressure);
        var bandage = _treatment.BandageWound(body, woundId, treatmentStartedAt + TimeSpan.FromSeconds(20));
        var bandagedRate = source.CurrentRate;
        var hemostatic = _treatment.ApplyHemostatic(body, woundId, treatmentStartedAt + TimeSpan.FromSeconds(30));
        var packedRate = source.CurrentRate;

        var state = SComp<NCTreatmentStateComponent>(body);
        var medical = SComp<NCMedicalBodyComponent>(body);
        Assert.Multiple(() =>
        {
            Assert.That(pressure.Outcome, Is.EqualTo(NCTreatmentOutcome.Applied));
            Assert.That(bandage.Outcome, Is.EqualTo(NCTreatmentOutcome.Applied));
            Assert.That(hemostatic.Outcome, Is.EqualTo(NCTreatmentOutcome.Applied));
            Assert.That(pressureRate, Is.GreaterThan(0f).And.LessThan(baseRate));
            Assert.That(bandagedRate, Is.GreaterThan(0f).And.LessThan(pressureRate));
            Assert.That(packedRate, Is.GreaterThan(0f).And.LessThan(bandagedRate));
            Assert.That(pressureActiveAfterApply, Is.True);
            Assert.That(source.PressureApplied, Is.False,
                "manual pressure must expire when it is no longer actively renewed");
            Assert.That(source.Bandaged, Is.True);
            Assert.That(wound.TreatmentFlags.HasFlag(NCWoundTreatmentFlags.Pressure), Is.False);
            Assert.That(wound.TreatmentFlags.HasFlag(NCWoundTreatmentFlags.Bandaged), Is.True);
            Assert.That(wound.TreatmentFlags.HasFlag(NCWoundTreatmentFlags.Hemostatic), Is.True);
            Assert.That(wound.IntegrityDamage, Is.EqualTo(originalIntegrity));
            Assert.That(wound.Severity, Is.EqualTo(originalSeverity));
            Assert.That(container.Wounds, Has.Count.EqualTo(originalCount));
            Assert.That(state.WoundTreatments.Keys, Does.Contain(woundId));
            Assert.That(state.WoundTreatments[woundId].WoundId, Is.EqualTo(woundId));
            Assert.That(state.WoundTreatments[woundId].BandageAbsorptionRemaining, Is.GreaterThan(0f));
            Assert.That(medical.MedicalRevision, Is.GreaterThan(revision));
            Assert.That(_scheduler.GetRemainingDelay(body), Is.Not.Null);
            Assert.That(_scheduler.GetRemainingDelay(body), Is.LessThanOrEqualTo(TimeSpan.Zero));
        });

        _scheduler.Update(0f);
        Assert.That(_scheduler.GetRemainingDelay(body), Is.GreaterThan(TimeSpan.Zero));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void TwoBallisticLegWoundsCanBeControlledAndVanillaBloodpackRestoresBlood()
    {
        var body = SSpawn(HumanMob);
        var firstWound = CreateWound(body, "NCZoneLegRight", "LegRight", "BallisticProjectile", 16f);
        var secondWound = CreateWound(body, "NCZoneLegRight", "LegRight", "BallisticProjectile", 16f);
        var wounds = SComp<NCWoundContainerComponent>(body);
        var bleeding = SComp<NCBleedingComponent>(body);
        var initialRate = bleeding.ActiveExternalRate;

        var bandage = _treatment.BandageWound(body, firstWound);
        var bandagedRate = bleeding.ActiveExternalRate;
        var hemostatic = _treatment.ApplyHemostatic(body, firstWound);
        var packedRate = bleeding.ActiveExternalRate;
        var tourniquet = _treatment.SetTourniquet(body, "NCZoneLegRight");

        var circulation = SComp<NCCirculationComponent>(body);
        circulation.BloodVolume -= 900f;
        var beforeTransfusion = circulation.BloodVolume;
        var bloodpack = SSpawn("Bloodpack");
        var stack = SComp<StackComponent>(bloodpack);
        var stackBefore = stack.Count;
        var transfusion = _emergency.TransfuseBlood(body, bloodpack);

        Assert.Multiple(() =>
        {
            Assert.That(wounds.Wounds.Keys, Is.EquivalentTo(new[] { firstWound, secondWound }));
            Assert.That(initialRate, Is.GreaterThan(0f));
            Assert.That(bandage.Outcome, Is.EqualTo(NCTreatmentOutcome.Applied));
            Assert.That(bandagedRate, Is.LessThan(initialRate).And.GreaterThan(0f));
            Assert.That(hemostatic.Outcome, Is.EqualTo(NCTreatmentOutcome.Applied));
            Assert.That(packedRate, Is.LessThan(bandagedRate).And.GreaterThan(0f));
            Assert.That(tourniquet.Outcome, Is.EqualTo(NCTreatmentOutcome.Applied));
            Assert.That(bleeding.ActiveExternalRate, Is.Zero.Within(0.001f));
            Assert.That(bleeding.Sources.Where(source => source.Channel != NCBleedingChannel.Internal),
                Has.All.Matches<NCBleedingSourceRecord>(source => source.Tourniqueted && source.CurrentRate == 0f));
            Assert.That(transfusion.Outcome, Is.EqualTo(NCEmergencyCareOutcome.Applied));
            Assert.That(circulation.BloodVolume, Is.EqualTo(beforeTransfusion + 450f).Within(0.001f));
            Assert.That(stack.Count, Is.EqualTo(stackBefore - 1));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ArterialTourniquetTracksIschemiaAndRequiresControlledRelease()
    {
        var body = SSpawn(HumanMob);
        var woundId = CreateWound(body, "NCZoneArmRight", "ArmRight", "SlashingImpact", 10f);
        var container = SComp<NCWoundContainerComponent>(body);
        var wound = container.Wounds[woundId];
        var source = SComp<NCBleedingComponent>(body).Sources.Single(candidate =>
            candidate.WoundId == woundId && candidate.Channel == NCBleedingChannel.ExternalArterial);
        var baseRate = source.BaseRate;
        var appliedAt = TimeSpan.FromHours(1);

        var applied = _treatment.SetTourniquet(body, "NCZoneArmRight", appliedAt);
        var protectedRate = source.CurrentRate;
        Assert.That(_treatment.TryGetTourniquetStatus(
            body,
            "NCZoneHandRight",
            out var status,
            appliedAt + TimeSpan.FromMinutes(50)), Is.True);

        var unsafeRelease = _treatment.RemoveTourniquet(
            body,
            "NCZoneArmRight",
            false,
            appliedAt + TimeSpan.FromMinutes(50));
        var controlledRelease = _treatment.RemoveTourniquet(
            body,
            "NCZoneHandRight",
            true,
            appliedAt + TimeSpan.FromMinutes(50));

        var tourniquet = SComp<NCTreatmentStateComponent>(body).Tourniquets["NCZoneArmRight"];
        Assert.Multiple(() =>
        {
            Assert.That(applied.Outcome, Is.EqualTo(NCTreatmentOutcome.Applied));
            Assert.That(protectedRate, Is.GreaterThanOrEqualTo(0f).And.LessThan(baseRate));
            Assert.That(status.Active, Is.True);
            Assert.That(status.Ischemia, Is.EqualTo(NCTourniquetIschemiaState.AtRisk));
            Assert.That(status.ControlledReleaseRequired, Is.True);
            Assert.That(unsafeRelease.Outcome,
                Is.EqualTo(NCTreatmentOutcome.UnsafeReleaseRequiresControlled));
            Assert.That(controlledRelease.Outcome, Is.EqualTo(NCTreatmentOutcome.Applied));
            Assert.That(source.CurrentRate, Is.GreaterThan(baseRate));
            Assert.That(source.Tourniqueted, Is.False);
            Assert.That(wound.TreatmentFlags.HasFlag(NCWoundTreatmentFlags.Tourniqueted), Is.False);
            Assert.That(tourniquet.Active, Is.False);
            Assert.That(tourniquet.ControlledRelease, Is.True);
            Assert.That(tourniquet.AppliedAt, Is.EqualTo(appliedAt));
            Assert.That(tourniquet.CoveredWoundIds, Does.Contain(woundId));
            Assert.That(container.Wounds, Has.Count.EqualTo(1));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void TorsoAndInternalBleedingCannotReceiveTourniquetOrExternalDressings()
    {
        var body = SSpawn(HumanMob);
        var torsoWound = CreateWound(body, "NCZoneChest", "Thorax", "SlashingImpact", 10f);
        // Closed limb bruising has no cavity to bleed into. Use the live thoracic cavity for a genuinely
        // internal-only blunt source, then verify that external field treatment cannot affect it.
        var internalWound = CreateWound(body, "NCZoneChest", "Thorax", "BluntImpact", 10f);
        var container = SComp<NCWoundContainerComponent>(body);
        var internalSource = SComp<NCBleedingComponent>(body).Sources.Single(candidate =>
            candidate.WoundId == internalWound && candidate.Channel == NCBleedingChannel.Internal);
        var internalRate = internalSource.CurrentRate;

        var torsoTourniquet = _treatment.SetTourniquet(body, "NCZoneChest");
        var internalPressure = _treatment.ApplyPressure(body, internalWound);
        var internalBandage = _treatment.BandageWound(body, internalWound);

        Assert.Multiple(() =>
        {
            Assert.That(torsoTourniquet.Outcome, Is.EqualTo(NCTreatmentOutcome.InvalidTourniquetZone));
            Assert.That(internalPressure.Outcome, Is.EqualTo(NCTreatmentOutcome.NoExternalBleeding));
            Assert.That(internalBandage.Outcome, Is.EqualTo(NCTreatmentOutcome.NoExternalBleeding));
            Assert.That(internalSource.CurrentRate, Is.EqualTo(internalRate));
            Assert.That(container.Wounds.Keys, Is.EquivalentTo(new[] { torsoWound, internalWound }));
            Assert.That(container.Wounds[internalWound].TreatmentFlags, Is.EqualTo(NCWoundTreatmentFlags.None));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void TreatmentStateAndItemsUsePersistentSafeCapabilities()
    {
        var body = SSpawn(HumanMob);
        var woundId = CreateWound(body, "NCZoneLegRight", "LegRight", "SlashingImpact", 6f);
        var itemIds = new EntProtoId[]
        {
            "Gauze",
            "Brutepack",
            "Tourniquet",
            "NCFieldSplint",
        };

        foreach (var itemId in itemIds)
        {
            var item = SSpawn(itemId);
            Assert.That(SComp<NCTreatmentItemComponent>(item).Capability.Id, Is.Not.Empty);
        }

        var actionId = NCActionResolutionId.New();
        var context = new NCTreatmentActionContext(
            actionId,
            TimeSpan.FromMinutes(1),
            HumanMob,
            new EntProtoId("Gauze"),
            new ProtoId<NCTreatmentCapabilityPrototype>("NCFieldBandageCapability"));
        var result = _treatment.BandageWound(body, woundId, context);
        var state = SComp<NCTreatmentStateComponent>(body);
        var record = state.WoundTreatments[woundId];
        var rateAfterFirstResolution = SComp<NCBleedingComponent>(body).Sources
            .Single(source => source.WoundId == woundId && source.Channel != NCBleedingChannel.Internal)
            .CurrentRate;
        var revisionAfterFirstResolution = SComp<NCMedicalBodyComponent>(body).MedicalRevision;
        var auditCountAfterFirstResolution = state.AuditHistory.Count;
        var duplicate = _treatment.BandageWound(body, woundId, context);
        var persistentRecordTypes = typeof(NCWoundTreatmentRecord)
            .GetFields(BindingFlags.Instance | BindingFlags.Public)
            .Select(field => field.FieldType)
            .Concat(typeof(NCTourniquetRecord)
                .GetFields(BindingFlags.Instance | BindingFlags.Public)
                .Select(field => field.FieldType))
            .Concat(typeof(NCTreatmentAuditRecord)
                .GetFields(BindingFlags.Instance | BindingFlags.Public)
                .Select(field => field.FieldType));
        var audit = state.AuditHistory.Single(entry => entry.ResolutionId == actionId);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(NCTreatmentOutcome.Applied));
            Assert.That(duplicate.Outcome, Is.EqualTo(NCTreatmentOutcome.DuplicateAction));
            Assert.That(record.WoundId, Is.EqualTo(woundId));
            Assert.That(state.ProcessedActions.Count(id => id == actionId), Is.EqualTo(1));
            Assert.That(state.AuditHistory, Has.Count.EqualTo(auditCountAfterFirstResolution));
            Assert.That(audit.ActorPrototype, Is.EqualTo((EntProtoId?) HumanMob));
            Assert.That(audit.ItemPrototype, Is.EqualTo((EntProtoId?) new EntProtoId("Gauze")));
            Assert.That(audit.Capability?.Id, Is.EqualTo("NCFieldBandageCapability"));
            Assert.That(SComp<NCBleedingComponent>(body).Sources
                .Single(source => source.WoundId == woundId && source.Channel != NCBleedingChannel.Internal)
                .CurrentRate, Is.EqualTo(rateAfterFirstResolution));
            Assert.That(SComp<NCMedicalBodyComponent>(body).MedicalRevision,
                Is.EqualTo(revisionAfterFirstResolution));
            Assert.That(persistentRecordTypes, Does.Not.Contain(typeof(EntityUid)));
            Assert.That(SComp<NCWoundContainerComponent>(body).Wounds, Has.Count.EqualTo(1));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void FieldSplintStabilizesButDoesNotRepairFracture()
    {
        var body = SSpawn(HumanMob);
        var woundId = CreateWound(body, "NCZoneLegRight", "LegRight", "BluntImpact", 10f);
        Assert.That(_anatomy.TryGetOrgan(body, "LegBoneRight", out var boneUid), Is.True);
        var bone = SComp<NCBoneComponent>(boneUid);
        var originalState = bone.State;
        var originalFixation = bone.RequiresFixation;
        var originalRisk = bone.SecondaryDamageRisk;

        var result = _treatment.SplintZone(body, "NCZoneLegRight", TimeSpan.FromSeconds(10));
        var duplicate = _treatment.SplintZone(body, "NCZoneLegRight", TimeSpan.FromSeconds(11));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(NCTreatmentOutcome.Applied));
            Assert.That(duplicate.Outcome, Is.EqualTo(NCTreatmentOutcome.AlreadyApplied));
            Assert.That(result.WoundId, Is.Null);
            Assert.That(result.Zone?.Id, Is.EqualTo("NCZoneLegRight"));
            Assert.That(bone.State, Is.EqualTo(originalState).And.GreaterThanOrEqualTo(NCBoneState.Cracked));
            Assert.That(bone.RequiresFixation, Is.EqualTo(originalFixation).And.True);
            Assert.That(bone.Splinted, Is.True);
            Assert.That(bone.SplintedAt, Is.EqualTo(TimeSpan.FromSeconds(10)));
            Assert.That(bone.Stability, Is.GreaterThanOrEqualTo(0.65f));
            Assert.That(bone.SecondaryDamageRisk, Is.LessThanOrEqualTo(originalRisk));
            Assert.That(SComp<NCWoundContainerComponent>(body).Wounds, Does.ContainKey(woundId));
        });
    }

    private Guid CreateWound(
        EntityUid body,
        ProtoId<NCTargetZonePrototype> zone,
        ProtoId<OrganCategoryPrototype> region,
        ProtoId<NCTraumaProfilePrototype> traumaProfile,
        float postArmorDamage)
    {
        var before = SEntMan.TryGetComponent<NCWoundContainerComponent>(body, out var existingWounds)
            ? existingWounds.Wounds.Keys.ToHashSet()
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
            0f,
            TimeSpan.Zero);
        var trauma = new NCCombatTraumaRequestedEvent(request);
        SEntMan.EventBus.RaiseLocalEvent(body, ref trauma);

        Assert.Multiple(() =>
        {
            Assert.That(trauma.Handled, Is.True);
            Assert.That(trauma.Applied, Is.True);
            Assert.That(trauma.Cancelled, Is.False);
        });
        return SComp<NCWoundContainerComponent>(body).Wounds.Keys.Single(id => !before.Contains(id));
    }
}
