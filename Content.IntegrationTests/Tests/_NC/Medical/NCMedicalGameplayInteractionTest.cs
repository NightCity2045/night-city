// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Combat.Targeting;
using Content.Server._NC.Medical.Surgery;
using Content.Server._NC.Medical.Treatment;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Emergency;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared._NC.Medical.Treatment;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Content.Shared.FixedPoint;
using Content.Shared.Stacks;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

[TestFixture]
[TestOf(typeof(NCTreatmentInteractionSystem))]
[TestOf(typeof(NCSurgeryInteractionSystem))]
public sealed class NCMedicalGameplayInteractionTest : GameTest
{
    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;
    [SidedDependency(Side.Server)] private NCTargetingSelectionSystem _targeting = default!;
    [SidedDependency(Side.Server)] private NCTreatmentInteractionSystem _treatment = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void TreatmentAutoSelectionPrefersExplicitVisibleZoneAndIsStable()
    {
        var user = SSpawn("MobHuman");
        var patient = SSpawn("MobHuman");
        var armId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var legId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var wounds = SEntMan.EnsureComponent<NCWoundContainerComponent>(patient);
        wounds.Wounds.Clear();
        wounds.Wounds[armId] = NewWound(armId, "NCZoneArmLeft", "ArmLeft", NCWoundSeverity.Moderate);
        wounds.Wounds[legId] = NewWound(legId, "NCZoneLegRight", "LegRight", NCWoundSeverity.Critical);

        var bleeding = SEntMan.EnsureComponent<NCBleedingComponent>(patient);
        bleeding.Sources.Clear();
        bleeding.Sources.Add(NewSource(armId, "NCZoneArmLeft", "ArmLeft", 2f));
        bleeding.Sources.Add(NewSource(legId, "NCZoneLegRight", "LegRight", 10f));

        Assert.That(_targeting.TrySetSelection(
            user,
            new NCTargetingState(NCTargetingMode.SelectedZone, "NCZoneArmLeft"),
            out _,
            out _), Is.True);
        Assert.That(_treatment.TrySelectWound(user, patient, NCTreatmentKind.Bandage, out var selected), Is.True);
        Assert.That(selected, Is.EqualTo(armId));

        Assert.That(_targeting.TrySetSelection(
            user,
            new NCTargetingState(NCTargetingMode.Automatic, null),
            out _,
            out _), Is.True);
        Assert.That(_treatment.TrySelectWound(user, patient, NCTreatmentKind.Bandage, out selected), Is.True);
        Assert.That(selected, Is.EqualTo(legId));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void GameplayItemsExposeTimedConsumableAndPreparationContracts()
    {
        var bandageCapability = _prototypes.Index<NCTreatmentCapabilityPrototype>("NCFieldBandageCapability");
        var oxygenCapability = _prototypes.Index<NCEmergencyCareCapabilityPrototype>("NCOxygenDeliveryCapability");
        var bandage = SSpawn("NCFieldBandage");
        var oxygen = SSpawn("NCOxygenDeliveryKit");
        var cautery = SSpawn("NCCautery");
        var table = SSpawn("OperatingTable");

        Assert.Multiple(() =>
        {
            Assert.That(bandageCapability.ApplicationDuration, Is.GreaterThan(TimeSpan.Zero));
            Assert.That(bandageCapability.ResourceKind, Is.EqualTo(NCTreatmentResourceKind.Stack));
            Assert.That(SComp<StackComponent>(bandage).Count, Is.GreaterThan(0));
            Assert.That(oxygenCapability.ApplicationDuration, Is.GreaterThan(TimeSpan.Zero));
            Assert.That(SComp<NCEmergencyCareItemComponent>(oxygen).ChargesRemaining,
                Is.GreaterThanOrEqualTo(oxygenCapability.ChargeCost));
            Assert.That(SComp<NCSurgeryToolComponent>(cautery).StartingProcedures, Is.Not.Empty);
            Assert.That(SComp<NCSurgeryAuthorizationProviderComponent>(cautery).Authorizations, Is.Not.Empty);
            Assert.That(SComp<NCSurgeryFacilityComponent>(table).Capabilities, Is.Not.Empty);
            Assert.That(SComp<NCSurgeryPreparationProviderComponent>(table).Restrained, Is.True);
            Assert.That(SComp<NCSurgeryPreparationProviderComponent>(table).AirwayMonitored, Is.True);
        });
    }

    private static NCWoundRecord NewWound(
        Guid id,
        ProtoId<NCTargetZonePrototype> zone,
        ProtoId<OrganCategoryPrototype> region,
        NCWoundSeverity severity)
    {
        return new NCWoundRecord
        {
            WoundId = id,
            Prototype = "NCWoundBallistic",
            TargetZone = zone,
            RegionCategory = region,
            WoundType = NCWoundType.Ballistic,
            TissueDepth = NCTissueDepth.Vascular,
            Severity = severity,
            IntegrityDamage = FixedPoint2.New(10),
        };
    }

    private static NCBleedingSourceRecord NewSource(
        Guid woundId,
        ProtoId<NCTargetZonePrototype> zone,
        ProtoId<OrganCategoryPrototype> region,
        float rate)
    {
        return new NCBleedingSourceRecord
        {
            SourceId = woundId,
            WoundId = woundId,
            TargetZone = zone,
            RegionCategory = region,
            Channel = NCBleedingChannel.ExternalVenous,
            BaseRate = rate,
            CurrentRate = rate,
        };
    }
}
