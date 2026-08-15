// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Anatomy.Detachment;
using Content.Server._NC.Medical.Diagnostics.Detail;
using Content.Server._NC.Medical.Persistence;
using Content.Server._NC.Medical.Pharmacology;
using Content.Server._NC.Medical.Physiology;
using Content.Server._NC.Medical.Treatment;
using Content.Server.Database;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Emergency;
using Content.Shared._NC.Medical.Persistence;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Trauma;
using Content.Shared._NC.Medical.Treatment;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared._NC.Identity;
using Content.Shared.Body;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

[TestFixture]
[TestOf(typeof(NCMedicalPersistenceSystem))]
public sealed class NCMedicalPersistenceSystemTest : GameTest
{
    private static readonly EntProtoId HumanMob = "MobHuman";

    [SidedDependency(Side.Server)] private NCAnatomySystem _anatomy = default!;
    [SidedDependency(Side.Server)] private NCMedicalPersistenceSystem _persistence = default!;
    [SidedDependency(Side.Server)] private NCOrganDetachmentSystem _detachment = default!;
    [SidedDependency(Side.Server)] private NCPhysiologySchedulerSystem _scheduler = default!;
    [SidedDependency(Side.Server)] private NCPharmacologySystem _pharmacology = default!;
    [SidedDependency(Side.Server)] private NCTreatmentSystem _treatment = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void SemanticMedicalStateRoundTripsWithoutRuntimeActionWindows()
    {
        var source = SSpawn(HumanMob);
        Assert.That(_anatomy.TryGetOrgan(source, "ArmLeft", out var sourceArm), Is.True);
        var sourceIntegrity = SComp<NCOrganIntegrityComponent>(sourceArm);
        var sourceSite = SComp<NCSurgicalSiteComponent>(sourceArm);
        sourceSite.AccessDepth = NCSurgicalAccessDepth.SoftTissueRetracted;
        sourceSite.Contamination = 0.2f;
        sourceSite.ActiveProcedure = Guid.NewGuid();
        sourceSite.TemporaryDevices.Add("field-drain");

        var woundId = CreateWound(source, "NCZoneArmLeft", "ArmLeft", "SlashingImpact", 6f);
        var sourceWounds = SComp<NCWoundContainerComponent>(source);
        var sourceWound = sourceWounds.Wounds[woundId];
        sourceWound.Stage = NCWoundStage.Infected;
        sourceWound.Infection = 0.2f;
        sourceWound.InfectionStage = NCInfectionStage.Localized;
        sourceWound.Necrosis = 0.1f;
        sourceWound.HealingProgress = 0.25f;
        sourceWound.PeakHealingProgress = 0.4f;
        sourceWound.ReopenStress = 2f;
        sourceWound.Age = TimeSpan.FromMinutes(10);
        var embeddedObjectId = Guid.NewGuid();
        sourceWound.EmbeddedObjectId = embeddedObjectId;
        sourceWound.EmbeddedObjectEntityPrototype = "NCExtractedBullet";
        sourceWound.SourceProjectilePrototype = "BulletPistol";
        sourceWound.ForensicResolutionId = Guid.Parse("24fbb22a-1508-46e3-b86a-a6d45ca7ffbd");
        sourceWound.Surface = NCWoundSurface.Rear;
        var scarId = Guid.NewGuid();
        var scarSourceId = Guid.NewGuid();
        sourceWounds.Scars.Add(new NCScarRecord
        {
            ScarId = scarId,
            SourceWoundId = scarSourceId,
            TargetZone = "NCZoneArmLeft",
            RegionCategory = "ArmLeft",
            WoundType = NCWoundType.Slash,
            OriginalSeverity = NCWoundSeverity.Moderate,
            Area = 2f,
            Disfigurement = 0.4f,
            Contracture = 0.05f,
            Surface = NCWoundSurface.Left,
            CreatedAt = TimeSpan.Zero,
        });
        var treatmentAction = NCActionResolutionId.New();
        var treatment = _treatment.ApplyPressure(
            source,
            woundId,
            new NCTreatmentActionContext(treatmentAction, TimeSpan.FromMinutes(2)));
        Assert.That(treatment.Succeeded, Is.True);
        // Set an explicit post-trauma value so the round-trip assertion is independent of wound balance.
        sourceIntegrity.CurrentIntegrity = 61f;
        sourceIntegrity.FunctionalEfficiency = 0.61f;

        var emergency = SEntMan.EnsureComponent<NCEmergencyCareStateComponent>(source);
        emergency.Profile = "NCHumanEmergencyCare";
        emergency.CprRemaining = TimeSpan.FromSeconds(10);
        emergency.OxygenRemaining = TimeSpan.FromSeconds(20);
        emergency.VentilationRemaining = TimeSpan.FromSeconds(15);
        emergency.CrystalloidVolume = 100f;
        emergency.ProcessedActions.Add(NCActionResolutionId.New());

        Assert.That(_pharmacology.ApplyDose(source, "NCDrugBroadAntibiotic", 1f).Accepted, Is.True);

        var circulation = SComp<NCCirculationComponent>(source);
        circulation.BloodVolume *= 0.8f;
        circulation.EffectiveCirculatingVolume = circulation.BloodVolume;
        Assert.That(_scheduler.ProcessNow(source, TimeSpan.Zero), Is.True);

        // The clinical timeline belongs to the current round only. Treatments and medication above
        // populate it, but it must never enter the character persistence payload.
        var sourceHistory = SComp<NCMedicalHistoryComponent>(source);
        Assert.That(sourceHistory.Records, Is.Not.Empty);

        var first = _persistence.SerializeMedicalState(source);
        Assert.That(first, Is.Not.Null);
        var target = SSpawn(HumanMob);
        Assert.That(_persistence.RestoreMedicalState(target, first!), Is.True);
        var second = _persistence.SerializeMedicalState(target);
        Assert.That(second, Is.Not.Null);

        Assert.That(_anatomy.TryGetOrgan(target, "ArmLeft", out var targetArm), Is.True);
        var targetIntegrity = SComp<NCOrganIntegrityComponent>(targetArm);
        var targetSite = SComp<NCSurgicalSiteComponent>(targetArm);
        var targetTreatment = SComp<NCTreatmentStateComponent>(target);
        var targetEmergency = SComp<NCEmergencyCareStateComponent>(target);
        var targetHistory = SComp<NCMedicalHistoryComponent>(target);
        var secondWounds = second!.Wounds!;
        Assert.Multiple(() =>
        {
            Assert.That(second!.Version, Is.EqualTo(NCMedicalSnapshotVersions.Current));
            Assert.That(second.AnatomyProfile, Is.EqualTo(first!.AnatomyProfile));
            Assert.That(second.MissingOrganCategories, Is.Empty);
            Assert.That(second.Organs.Keys, Is.EquivalentTo(first.Organs.Keys));
            Assert.That(second.Organs["ArmLeft"].EntityPrototype,
                Is.EqualTo(first.Organs["ArmLeft"].EntityPrototype));
            Assert.That(second.Organs["ArmLeft"].StableOrganId,
                Is.EqualTo(first.Organs["ArmLeft"].StableOrganId));
            Assert.That(targetIntegrity.CurrentIntegrity, Is.EqualTo(61f));
            Assert.That(targetIntegrity.FunctionalEfficiency, Is.EqualTo(0.61f));
            Assert.That(targetSite.AccessDepth, Is.EqualTo(NCSurgicalAccessDepth.SoftTissueRetracted));
            Assert.That(targetSite.Contamination, Is.EqualTo(0.2f));
            Assert.That(targetSite.ActiveProcedure, Is.Null);
            Assert.That(targetSite.TemporaryDevices, Does.Contain("field-drain"));
            Assert.That(secondWounds.Records.Keys, Is.EquivalentTo(first.Wounds!.Records.Keys));
            Assert.That(secondWounds.Records[woundId].Infection, Is.EqualTo(0.2f));
            Assert.That(secondWounds.Records[woundId].InfectionStage, Is.EqualTo(NCInfectionStage.Localized));
            Assert.That(secondWounds.Records[woundId].Necrosis, Is.EqualTo(0.1f));
            Assert.That(secondWounds.Records[woundId].HealingProgress, Is.EqualTo(0.25f));
            Assert.That(secondWounds.Records[woundId].PeakHealingProgress, Is.EqualTo(0.4f));
            Assert.That(secondWounds.Records[woundId].ReopenStress, Is.EqualTo(2f));
            Assert.That(secondWounds.Records[woundId].Age, Is.EqualTo(TimeSpan.FromMinutes(10)));
            Assert.That(secondWounds.Records[woundId].EmbeddedObjectId, Is.EqualTo(embeddedObjectId));
            Assert.That(secondWounds.Records[woundId].EmbeddedObjectEntityPrototype?.Id,
                Is.EqualTo("NCExtractedBullet"));
            Assert.That(secondWounds.Records[woundId].SourceProjectilePrototype?.Id,
                Is.EqualTo("BulletPistol"));
            Assert.That(secondWounds.Records[woundId].Surface, Is.EqualTo(NCWoundSurface.Rear));
            Assert.That(first.Wounds.Records[woundId].ForensicResolutionId, Is.EqualTo(Guid.Empty));
            Assert.That(secondWounds.Records[woundId].ForensicResolutionId, Is.EqualTo(Guid.Empty));
            Assert.That(secondWounds.Scars, Has.Count.EqualTo(1));
            Assert.That(secondWounds.Scars[0].ScarId, Is.EqualTo(scarId));
            Assert.That(secondWounds.Scars[0].SourceWoundId, Is.EqualTo(scarSourceId));
            Assert.That(secondWounds.Scars[0].Disfigurement, Is.EqualTo(0.4f));
            Assert.That(secondWounds.Scars[0].Surface, Is.EqualTo(NCWoundSurface.Left));
            Assert.That(second.Bleeding!.Sources.Select(record => record.SourceId),
                Is.EquivalentTo(first.Bleeding!.Sources.Select(record => record.SourceId)));
            Assert.That(second.Treatment!.WoundTreatments.Keys,
                Is.EquivalentTo(first.Treatment!.WoundTreatments.Keys));
            Assert.That(targetTreatment.ProcessedActions, Is.Empty);
            Assert.That(targetTreatment.AuditHistory, Is.Empty);
            Assert.That(targetEmergency.CprRemaining, Is.EqualTo(TimeSpan.FromSeconds(10)));
            Assert.That(targetEmergency.OxygenRemaining, Is.EqualTo(TimeSpan.FromSeconds(20)));
            Assert.That(targetEmergency.VentilationRemaining, Is.EqualTo(TimeSpan.FromSeconds(15)));
            Assert.That(targetEmergency.CrystalloidVolume, Is.EqualTo(100f));
            Assert.That(targetEmergency.ProcessedActions, Is.Empty);
            Assert.That(targetEmergency.AuditHistory, Is.Empty);
            Assert.That(targetHistory.Records, Is.Empty);
            Assert.That(second.Circulation.BloodVolume, Is.EqualTo(first.Circulation.BloodVolume));
            Assert.That(second.Pharmacology, Is.Not.Null);
            Assert.That(second.Pharmacology!.ActiveDrugs, Has.Count.EqualTo(1));
            Assert.That(second.Pharmacology.ActiveDrugs[0].Profile.Id, Is.EqualTo("NCDrugBroadAntibiotic"));
            Assert.That(second.Pharmacology.ActiveDrugs[0].Concentration,
                Is.EqualTo(first.Pharmacology!.ActiveDrugs[0].Concentration));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void MissingOrganSubtreeRoundTripsBySemanticCategories()
    {
        var source = SSpawn(HumanMob);
        Assert.That(_detachment.TryDetachOrgan(source, "ArmLeft", out _), Is.True);
        var snapshot = _persistence.SerializeMedicalState(source);
        Assert.That(snapshot, Is.Not.Null);

        var expectedMissing = new[] { "ArmLeft", "ArmBoneLeft", "TendonSetLeftArm", "HandLeft" };
        Assert.That(snapshot!.MissingOrganCategories.Select(category => category.Id),
            Is.EquivalentTo(expectedMissing));

        var target = SSpawn(HumanMob);
        Assert.That(_persistence.RestoreMedicalState(target, snapshot), Is.True);
        foreach (var category in expectedMissing)
            Assert.That(_anatomy.TryGetOrgan(target, category, out _), Is.False, category);

        var restored = _persistence.SerializeMedicalState(target);
        Assert.That(restored, Is.Not.Null);
        Assert.That(restored!.MissingOrganCategories.Select(category => category.Id),
            Is.EquivalentTo(expectedMissing));
        Assert.That(restored.Organs.Keys.Select(category => category.Id),
            Does.Not.Contain("ArmLeft"));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void InvalidUnknownAndOversizedSnapshotsDoNotPartiallyMutateTarget()
    {
        var body = SSpawn(HumanMob);
        Assert.That(_anatomy.TryGetOrgan(body, "ArmRight", out var arm), Is.True);
        var integrity = SComp<NCOrganIntegrityComponent>(arm);
        integrity.CurrentIntegrity = 73f;
        var circulation = SComp<NCCirculationComponent>(body);
        var originalBlood = circulation.BloodVolume;
        var originalRevision = SComp<NCMedicalBodyComponent>(body).MedicalRevision;

        var wrongVersion = _persistence.SerializeMedicalState(body)!;
        wrongVersion.Version++;
        Assert.That(_persistence.RestoreMedicalState(body, wrongVersion), Is.False);
        AssertUnchanged();

        var unknownProfile = _persistence.SerializeMedicalState(body)!;
        unknownProfile.AnatomyProfile = "NCUnknownAnatomyProfile";
        Assert.That(_persistence.RestoreMedicalState(body, unknownProfile), Is.False);
        AssertUnchanged();

        var oversized = _persistence.SerializeMedicalState(body)!;
        oversized.MissingOrganCategories = Enumerable
            .Repeat<ProtoId<OrganCategoryPrototype>>("ArmLeft", NCMedicalPersistenceSystem.MaxMissingOrganCategories + 1)
            .ToList();
        Assert.That(_persistence.RestoreMedicalState(body, oversized), Is.False);
        AssertUnchanged();

        var corrupt = _persistence.SerializeMedicalState(body)!;
        corrupt.Circulation.BloodVolume = float.NaN;
        Assert.That(_persistence.RestoreMedicalState(body, corrupt), Is.False);
        AssertUnchanged();
        return;

        void AssertUnchanged()
        {
            Assert.Multiple(() =>
            {
                Assert.That(integrity.CurrentIntegrity, Is.EqualTo(73f));
                Assert.That(circulation.BloodVolume, Is.EqualTo(originalBlood));
                Assert.That(SComp<NCMedicalBodyComponent>(body).MedicalRevision, Is.EqualTo(originalRevision));
                Assert.That(_anatomy.TryGetOrgan(body, "ArmRight", out var currentArm), Is.True);
                Assert.That(currentArm, Is.EqualTo(arm));
            });
        }
    }

    [Test]
    public void SnapshotDtoGraphContainsNoRuntimeEntityIdentity()
    {
        AssertNoRuntimeEntityIdentity(typeof(NCMedicalStateSnapshot), []);
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ContinuousMedicalEventsCannotStarveDebouncedPersistence()
    {
        var body = SSpawn(HumanMob);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var queue = typeof(NCMedicalPersistenceSystem).GetMethod("QueueDebouncedSave", flags);
        var field = typeof(NCMedicalPersistenceSystem).GetField("_saveDue", flags);
        Assert.That(queue, Is.Not.Null);
        Assert.That(field, Is.Not.Null);
        var deadlines = (Dictionary<EntityUid, TimeSpan>) field!.GetValue(_persistence)!;

        try
        {
            queue!.Invoke(_persistence, [body, TimeSpan.FromSeconds(10)]);
            queue.Invoke(_persistence, [body, TimeSpan.FromSeconds(11)]);
            Assert.That(deadlines[body], Is.EqualTo(TimeSpan.FromSeconds(10)),
                "A later continuous event must not postpone the already queued write.");

            queue.Invoke(_persistence, [body, TimeSpan.FromSeconds(9)]);
            Assert.That(deadlines[body], Is.EqualTo(TimeSpan.FromSeconds(9)),
                "An earlier safety boundary may still shorten the write deadline.");
        }
        finally
        {
            deadlines.Remove(body);
        }
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void RestartRebaseFreezesOfflineStateAndPreservesTourniquetDeadlines()
    {
        var body = SSpawn(HumanMob);
        var woundId = CreateWound(body, "NCZoneArmLeft", "ArmLeft", "SlashingImpact", 6f);
        var snapshot = _persistence.SerializeMedicalState(body)!;
        snapshot.CapturedAt = TimeSpan.FromHours(2);
        var wound = snapshot.Wounds!.Records[woundId];
        wound.CreatedAt = TimeSpan.FromMinutes(90);
        wound.ModifiedAt = TimeSpan.FromMinutes(110);
        snapshot.Treatment ??= new NCTreatmentStateSnapshot { Profile = "NCHumanFieldTreatment" };
        snapshot.Treatment.Tourniquets["NCZoneArmLeft"] = new NCTourniquetStateSnapshot
        {
            RootZone = "NCZoneArmLeft",
            CoveredWoundIds = [woundId],
            Active = true,
            AppliedAt = TimeSpan.FromMinutes(100),
            IschemiaRiskAt = TimeSpan.FromMinutes(130),
            CriticalIschemiaAt = TimeSpan.FromMinutes(160),
        };
        snapshot.EmergencyCare = new NCEmergencyCareStateSnapshot
        {
            Profile = "NCHumanEmergencyCare",
            CprRemaining = TimeSpan.FromSeconds(20),
            OxygenRemaining = TimeSpan.FromSeconds(30),
        };

        var runtimeAnchor = TimeSpan.FromSeconds(5);
        Assert.That(_persistence.TryRebaseSnapshotForRuntime(snapshot, runtimeAnchor, out var rebased), Is.True);
        var rebasedWound = rebased.Wounds!.Records[woundId];
        var tourniquet = rebased.Treatment!.Tourniquets["NCZoneArmLeft"];
        Assert.Multiple(() =>
        {
            Assert.That(runtimeAnchor - rebasedWound.CreatedAt, Is.EqualTo(TimeSpan.FromMinutes(30)));
            Assert.That(runtimeAnchor - rebasedWound.ModifiedAt, Is.EqualTo(TimeSpan.FromMinutes(10)));
            Assert.That(runtimeAnchor - tourniquet.AppliedAt, Is.EqualTo(TimeSpan.FromMinutes(20)));
            Assert.That(tourniquet.IschemiaRiskAt - runtimeAnchor, Is.EqualTo(TimeSpan.FromMinutes(10)));
            Assert.That(tourniquet.CriticalIschemiaAt - runtimeAnchor, Is.EqualTo(TimeSpan.FromMinutes(40)));
            Assert.That(rebased.EmergencyCare!.CprRemaining, Is.EqualTo(TimeSpan.FromSeconds(20)));
            Assert.That(rebased.EmergencyCare.OxygenRemaining, Is.EqualTo(TimeSpan.FromSeconds(30)));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void CorruptStoredYamlCannotPartiallyMutateMaterializedBody()
    {
        var body = SSpawn(HumanMob);
        Assert.That(_anatomy.TryGetOrgan(body, "ArmRight", out var arm), Is.True);
        var integrity = SComp<NCOrganIntegrityComponent>(arm);
        integrity.CurrentIntegrity = 73f;
        var originalBlood = SComp<NCCirculationComponent>(body).BloodVolume;
        var stored = new NCMedicalStoredSnapshotData(
            new NCCharacterId(1),
            NCMedicalSnapshotVersions.Current,
            1,
            "version: [definitely-not-a-medical-snapshot",
            DateTime.UtcNow,
            DateTime.UtcNow);

        Assert.That(_persistence.TryRestoreStoredMedicalState(body, stored), Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(integrity.CurrentIntegrity, Is.EqualTo(73f));
            Assert.That(SComp<NCCirculationComponent>(body).BloodVolume, Is.EqualTo(originalBlood));
            Assert.That(_anatomy.TryGetOrgan(body, "ArmRight", out var currentArm), Is.True);
            Assert.That(currentArm, Is.EqualTo(arm));
        });
    }

    private static void AssertNoRuntimeEntityIdentity(Type type, HashSet<Type> visited)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        Assert.That(type, Is.Not.EqualTo(typeof(EntityUid)), $"{type} contains EntityUid.");
        Assert.That(type, Is.Not.EqualTo(typeof(NetEntity)), $"{type} contains NetEntity.");

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ProtoId<>))
            return;
        if (!visited.Add(type))
            return;

        if (type.IsArray)
        {
            AssertNoRuntimeEntityIdentity(type.GetElementType()!, visited);
            return;
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
                AssertNoRuntimeEntityIdentity(argument, visited);
        }

        if (type.Namespace != typeof(NCMedicalStateSnapshot).Namespace)
            return;

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            AssertNoRuntimeEntityIdentity(field.FieldType, visited);
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
        Assert.That(trauma.Applied, Is.True);
        return SComp<NCWoundContainerComponent>(body).Wounds.Keys.Single(id => !before.Contains(id));
    }
}
