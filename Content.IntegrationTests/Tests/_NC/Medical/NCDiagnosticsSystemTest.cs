// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Diagnostics.Detail;
using Content.Server._NC.Medical.Pharmacology;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Diagnostics.Detail;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Content.Shared.FixedPoint;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

[TestFixture]
[TestOf(typeof(NCDiagnosticsSystem))]
public sealed class NCDiagnosticsSystemTest : GameTest
{
    private static readonly EntProtoId HumanMob = "MobHuman";
    private static readonly ProtoId<NCTargetZonePrototype> ChestZone = "NCZoneChest";
    private static readonly ProtoId<OrganCategoryPrototype> ThoraxRegion = "Thorax";
    private static readonly ProtoId<NCWoundPrototype> BallisticWound = "NCWoundBallistic";

    private static readonly ProtoId<NCDiagnosticCapabilityPrototype>[] NetworkCapabilities =
    [
        "NCDiagnosticsSelfSensations",
        "NCDiagnosticsVisual",
        "NCDiagnosticsManual",
        "NCDiagnosticsHandheldApproximate",
        "NCDiagnosticsScannerInternal",
        "NCDiagnosticsSurgicalConsole",
    ];

    [SidedDependency(Side.Server)] private NCAnatomySystem _anatomy = default!;
    [SidedDependency(Side.Server)] private NCDiagnosticsSystem _diagnostics = default!;
    [SidedDependency(Side.Server)] private NCPharmacologySystem _pharmacology = default!;
    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void TiersAreMonotonicDeterministicBoundedAndDoNotLeakWoundIds()
    {
        var body = SSpawn(HumanMob);
        SeedDiagnosticState(body, woundCount: 30);

        var resolutionId = new NCActionResolutionId(Guid.Parse("45cf812c-fab8-4143-847b-bd6748941484"));
        var observedAt = TimeSpan.FromSeconds(2045);
        var snapshots = new List<NCMedicalSnapshot>(NetworkCapabilities.Length);
        foreach (var capabilityId in NetworkCapabilities)
        {
            Assert.That(
                _diagnostics.TryBuildSnapshot(body, capabilityId, resolutionId, observedAt, out var snapshot),
                Is.True,
                $"Capability {capabilityId} did not produce a snapshot.");
            snapshots.Add(snapshot);

            var policy = _prototypes.Index(capabilityId);
            Assert.Multiple(() =>
            {
                Assert.That(snapshot.ResolutionId, Is.EqualTo(resolutionId));
                Assert.That(snapshot.ObservedAt, Is.EqualTo(observedAt));
                Assert.That(snapshot.Regions.Count, Is.LessThanOrEqualTo(policy.MaxRegionRecords));
                Assert.That(snapshot.Wounds.Count, Is.LessThanOrEqualTo(policy.MaxWoundRecords));
                Assert.That(snapshot.Organs.Count, Is.LessThanOrEqualTo(policy.MaxOrganRecords));
                Assert.That(snapshot.Drugs.Count, Is.LessThanOrEqualTo(policy.MaxDrugRecords));
            });
        }

        var previous = NCDiagnosticDisclosure.None;
        foreach (var snapshot in snapshots)
        {
            Assert.That(snapshot.Disclosures & previous, Is.EqualTo(previous),
                $"Tier {snapshot.Tier} removed a disclosure from the previous tier.");
            previous = snapshot.Disclosures;
        }

        var self = snapshots[(int) NCDiagnosticTier.SelfSensations];
        var visual = snapshots[(int) NCDiagnosticTier.Visual];
        var manual = snapshots[(int) NCDiagnosticTier.Manual];
        var handheld = snapshots[(int) NCDiagnosticTier.HandheldApproximate];
        var scanner = snapshots[(int) NCDiagnosticTier.ScannerInternal];

        Assert.Multiple(() =>
        {
            Assert.That(self.Wounds, Is.Empty);
            Assert.That(self.Organs, Is.Empty);
            Assert.That(self.Regions.All(region => region.WoundCount == null), Is.True);

            Assert.That(visual.Wounds, Has.Count.EqualTo(8));
            Assert.That(visual.WoundsOmitted, Is.EqualTo(22));
            Assert.That(visual.Wounds.All(wound => wound.Prototype == null && wound.Stage == null && wound.Depth == null),
                Is.True);

            Assert.That(manual.Warnings, Does.Contain(NCDiagnosticWarning.FractureSuspected));
            Assert.That(manual.Regions.Any(region =>
                region.Findings.HasFlag(NCDiagnosticFindingFlags.FractureSuspected) && region.BoneState == null), Is.True);

            Assert.That(handheld.Vitals.InternalBleedMilliunits, Is.Null);
            Assert.That(handheld.Warnings, Does.Contain(NCDiagnosticWarning.SuspectedInternalBleeding));
            Assert.That(handheld.Organs, Is.Empty);
            Assert.That(handheld.Drugs, Is.Empty);
            Assert.That(handheld.Wounds.All(wound =>
                wound.InternalBleeding == NCDiagnosticBand.None && wound.Prototype == null), Is.True);

            Assert.That(scanner.Wounds, Has.Count.EqualTo(24));
            Assert.That(scanner.WoundsOmitted, Is.EqualTo(6));
            Assert.That(scanner.Vitals.InternalBleedMilliunits, Is.Not.Null);
            Assert.That(scanner.Warnings, Does.Contain(NCDiagnosticWarning.ConfirmedInternalBleeding));
            Assert.That(scanner.Warnings, Does.Contain(NCDiagnosticWarning.FractureConfirmed));
            Assert.That(scanner.Warnings, Does.Contain(NCDiagnosticWarning.OrganFailure));
            Assert.That(scanner.Regions.Any(region =>
                region.Findings.HasFlag(NCDiagnosticFindingFlags.InternalBleeding) &&
                region.Findings.HasFlag(NCDiagnosticFindingFlags.FractureConfirmed) &&
                region.Findings.HasFlag(NCDiagnosticFindingFlags.OrganFailure) &&
                region.BoneState >= NCBoneState.BrokenDisplaced), Is.True);
            Assert.That(scanner.Organs.Any(organ =>
                organ.Category.Id == "Heart" && organ.Failing && organ.Function == NCDiagnosticBand.Critical), Is.True);
            Assert.That(scanner.Drugs, Has.Count.EqualTo(1));
            Assert.That(scanner.Drugs[0].Profile.Id, Is.EqualTo("NCDrugSurgicalAnesthetic"));
            Assert.That(scanner.Warnings, Does.Contain(NCDiagnosticWarning.Anesthetized));
        });

        Assert.That(
            _diagnostics.TryBuildSnapshot(
                body,
                NetworkCapabilities[(int) NCDiagnosticTier.ScannerInternal],
                resolutionId,
                observedAt,
                out var repeated),
            Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(repeated.Vitals, Is.EqualTo(scanner.Vitals));
            Assert.That(repeated.Regions, Is.EqualTo(scanner.Regions));
            Assert.That(repeated.Wounds, Is.EqualTo(scanner.Wounds));
            Assert.That(repeated.Organs, Is.EqualTo(scanner.Organs));
            Assert.That(repeated.Drugs, Is.EqualTo(scanner.Drugs));
            Assert.That(repeated.Warnings, Is.EqualTo(scanner.Warnings));
            Assert.That(repeated.WoundsOmitted, Is.EqualTo(scanner.WoundsOmitted));
            Assert.That(repeated.OrgansOmitted, Is.EqualTo(scanner.OrgansOmitted));
        });

        Assert.That(typeof(NCMedicalWoundSummary).GetProperties().Any(property =>
            property.Name.Contains("WoundId", StringComparison.Ordinal) || property.PropertyType == typeof(Guid)), Is.False);
        Assert.That(typeof(NCMedicalSnapshot).GetProperties().Any(property =>
            property.Name.Contains("WoundId", StringComparison.Ordinal)), Is.False);

        Assert.That(_diagnostics.TryBuildAdminExactSnapshot(body, resolutionId, out var admin), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(admin.ResolutionId, Is.EqualTo(resolutionId));
            Assert.That(admin.Wounds, Has.Count.EqualTo(30));
            Assert.That(admin.Wounds.All(wound => wound.WoundId != Guid.Empty), Is.True);
            Assert.That(admin.Drugs, Has.Count.EqualTo(1));
            Assert.That(admin.Drugs[0].Concentration, Is.GreaterThan(0f));
        });
    }

    private void SeedDiagnosticState(EntityUid body, int woundCount)
    {
        // Use the prototype's therapeutic concentration so the diagnostic warning crosses the
        // configured anesthesia threshold instead of merely proving trace-drug disclosure.
        Assert.That(_pharmacology.ApplyDose(body, "NCDrugSurgicalAnesthetic", 5f).Accepted, Is.True);

        // Wound/bleeding storage is allocated lazily by the trauma pipeline in live play.
        var wounds = SEntMan.EnsureComponent<NCWoundContainerComponent>(body);
        wounds.Wounds.Clear();
        for (var i = 0; i < woundCount; i++)
        {
            var woundId = DeterministicGuid(i + 1);
            wounds.Wounds.Add(woundId, new NCWoundRecord
            {
                WoundId = woundId,
                Prototype = BallisticWound,
                TargetZone = ChestZone,
                RegionCategory = ThoraxRegion,
                WoundType = NCWoundType.Ballistic,
                TissueDepth = NCTissueDepth.Cavity,
                Severity = NCWoundSeverity.Severe,
                Stage = NCWoundStage.Fresh,
                ClosureState = NCWoundClosureState.Open,
                IntegrityDamage = FixedPoint2.New(8f),
                Area = 1f,
                ExternalBleedRate = 0.25f,
                InternalBleedRate = 0.75f,
                PrimaryBleedKind = NCBleedKind.Internal,
                Pain = 2f,
                Infection = i == 0 ? 0.5f : 0f,
                EmbeddedObjectId = i == 0 ? DeterministicGuid(500) : null,
                ForensicResolutionId = DeterministicGuid(1000 + i),
                CreatedAt = TimeSpan.FromTicks(i),
                ModifiedAt = TimeSpan.FromTicks(i),
            });
        }

        var bleeding = SEntMan.EnsureComponent<NCBleedingComponent>(body);
        bleeding.ActiveExternalRate = 2f;
        bleeding.ActiveInternalRate = 3f;
        bleeding.InternalBloodLoss = 500f;

        var physiology = SEntMan.EnsureComponent<NCPhysiologyComponent>(body);
        physiology.Oxygenation = 0.82f;
        physiology.TotalPain = 8f;
        physiology.PainLevel = NCPainLevel.Severe;
        physiology.Shock = 0.6f;
        physiology.ShockStage = NCShockStage.Severe;
        physiology.Condition = NCPhysiologicalCondition.Critical;

        var circulation = SEntMan.EnsureComponent<NCCirculationComponent>(body);
        circulation.BloodVolume = 3500f;
        circulation.EffectiveCirculatingVolume = 3000f;
        circulation.Perfusion = 0.6f;
        circulation.BloodPressureState = NCBloodPressureState.Hypotensive;
        circulation.HeartRhythm = NCHeartRhythm.Unstable;

        Assert.That(_anatomy.TryGetOrgan(body, "RibCage", out var ribs), Is.True);
        var bone = SComp<NCBoneComponent>(ribs);
        bone.State = NCBoneState.BrokenDisplaced;
        bone.Stability = 0.2f;

        Assert.That(_anatomy.TryGetOrgan(body, "Heart", out var heart), Is.True);
        var heartFunction = SComp<NCOrganFunctionComponent>(heart);
        heartFunction.CurrentCapacity = 0f;
        var heartIntegrity = SComp<NCOrganIntegrityComponent>(heart);
        heartIntegrity.CurrentIntegrity = 5f;
        heartIntegrity.FunctionalEfficiency = 0f;
    }

    private static Guid DeterministicGuid(int value)
    {
        return new Guid(value, 0, 0, new byte[8]);
    }
}
