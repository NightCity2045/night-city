// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Anatomy.Detachment;
using Content.Server._NC.Medical.Treatment;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Anatomy.Detachment;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Trauma;
using Content.Shared._NC.Medical.Treatment;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mind;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

[TestFixture]
[TestOf(typeof(NCOrganDetachmentSystem))]
public sealed class NCOrganDetachmentSystemTest : GameTest
{
    private static readonly EntProtoId HumanMob = "MobHuman";

    [SidedDependency(Side.Server)] private NCAnatomySystem _anatomy = default!;
    [SidedDependency(Side.Server)] private OrganRelationSystem _relations = default!;
    [SidedDependency(Side.Server)] private NCOrganDetachmentSystem _detachment = default!;
    [SidedDependency(Side.Server)] private NCTreatmentSystem _treatment = default!;
    [SidedDependency(Side.Server)] private SharedMindSystem _minds = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void LimbRoundTripPreservesLiveSubtreeComponentsHandsAndMedicalData()
    {
        var body = SSpawn(HumanMob);
        Assert.That(_anatomy.TryGetOrgan(body, "ArmLeft", out var arm), Is.True);
        Assert.That(_anatomy.TryGetOrgan(body, "HandLeft", out var hand), Is.True);
        Assert.That(_anatomy.TryGetOrgan(body, "ArmBoneLeft", out var bone), Is.True);
        Assert.That(_anatomy.TryGetOrgan(body, "TendonSetLeftArm", out var tendon), Is.True);
        Assert.That(SComp<HandsComponent>(body).Hands, Does.ContainKey("left"));

        var integrity = SComp<NCOrganIntegrityComponent>(arm);
        integrity.CurrentIntegrity = 42f;
        var trauma = new NCCombatTraumaRequestedEvent(CreateRequest(
            body,
            "NCZoneArmLeft",
            "ArmLeft",
            "SlashingImpact",
            10f));
        SEntMan.EventBus.RaiseLocalEvent(body, ref trauma);
        Assert.That(trauma.Applied, Is.True);
        var preservedIntegrity = integrity.CurrentIntegrity;

        var woundContainer = SComp<NCWoundContainerComponent>(body);
        var woundId = woundContainer.Wounds.Keys.Single();
        Assert.That(_treatment.ApplyHemostatic(body, woundId, TimeSpan.FromSeconds(1)).Succeeded, Is.True);
        var sourceIds = SComp<NCBleedingComponent>(body).Sources
            .Where(source => source.WoundId == woundId)
            .Select(source => source.SourceId)
            .ToHashSet();
        Assert.That(sourceIds, Is.Not.Empty);
        woundContainer.OverflowTraumaByZone["NCZoneArmLeft"] = 12f;
        woundContainer.OverflowTraumaByZone["NCZoneChest"] = 4f;
        woundContainer.OverflowPain = 8f;
        var bleeding = SComp<NCBleedingComponent>(body);
        bleeding.OverflowExternalRate = 4f;
        bleeding.OverflowInternalRate = 2f;

        var revisionBefore = SComp<NCMedicalBodyComponent>(body).MedicalRevision;
        Assert.That(_detachment.TryDetachOrgan(body, "ArmLeft", out var detached), Is.True);
        Assert.That(detached, Is.EqualTo(arm));

        var detachedCategories = _relations.AllChildren(detached)
            .Select(child => SComp<OrganComponent>(child).Category!.Value.Id)
            .ToHashSet();
        var payload = SComp<NCDetachedOrganMedicalDataComponent>(detached);
        Assert.Multiple(() =>
        {
            Assert.That(_anatomy.TryGetOrgan(body, "ArmLeft", out _), Is.False);
            Assert.That(_anatomy.TryGetOrgan(body, "HandLeft", out _), Is.False);
            Assert.That(_anatomy.TryGetOrgan(body, "ArmBoneLeft", out _), Is.False);
            Assert.That(_anatomy.TryGetOrgan(body, "TendonSetLeftArm", out _), Is.False);
            Assert.That(detachedCategories,
                Is.EquivalentTo(new[] { "ArmBoneLeft", "TendonSetLeftArm", "HandLeft" }));
            Assert.That(SComp<ChildOrganComponent>(detached).Parent, Is.Null);
            Assert.That(SComp<OrganComponent>(arm).Body, Is.Null);
            Assert.That(SComp<OrganComponent>(hand).Body, Is.Null);
            Assert.That(SComp<OrganComponent>(bone).Body, Is.Null);
            Assert.That(SComp<OrganComponent>(tendon).Body, Is.Null);
            Assert.That(SComp<HandsComponent>(body).Hands, Does.Not.ContainKey("left"));
            Assert.That(payload.Wounds.Keys, Does.Contain(woundId));
            Assert.That(payload.OverflowTraumaByZone["NCZoneArmLeft"], Is.EqualTo(12f));
            Assert.That(payload.OverflowPain, Is.EqualTo(6f).Within(0.001f));
            Assert.That(payload.OverflowExternalBleedRate, Is.EqualTo(3f).Within(0.001f));
            Assert.That(payload.OverflowInternalBleedRate, Is.EqualTo(1.5f).Within(0.001f));
            Assert.That(woundContainer.OverflowTraumaByZone.Keys, Does.Not.Contain("NCZoneArmLeft"));
            Assert.That(woundContainer.OverflowTraumaByZone["NCZoneChest"], Is.EqualTo(4f));
            Assert.That(woundContainer.OverflowPain, Is.EqualTo(2f).Within(0.001f));
            Assert.That(bleeding.OverflowExternalRate, Is.EqualTo(1f).Within(0.001f));
            Assert.That(bleeding.OverflowInternalRate, Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(payload.WoundTreatments.Keys, Does.Contain(woundId));
            Assert.That(payload.BleedingSources.Select(source => source.SourceId), Is.EquivalentTo(sourceIds));
            Assert.That(woundContainer.Wounds.Keys, Does.Not.Contain(woundId));
            Assert.That(SComp<NCTreatmentStateComponent>(body).WoundTreatments.Keys,
                Does.Not.Contain(woundId));
            Assert.That(SComp<NCBleedingComponent>(body).Sources.All(source => source.WoundId != woundId),
                Is.True);
            Assert.That(SComp<NCMedicalBodyComponent>(body).MedicalRevision, Is.GreaterThan(revisionBefore));
            Assert.That(integrity.CurrentIntegrity, Is.EqualTo(preservedIntegrity));
        });

        Assert.That(_detachment.TryAttachOrgan(body, "Torso", detached), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(_anatomy.TryGetOrgan(body, "ArmLeft", out var attachedArm), Is.True);
            Assert.That(attachedArm, Is.EqualTo(arm));
            Assert.That(_anatomy.TryGetOrgan(body, "HandLeft", out var attachedHand), Is.True);
            Assert.That(attachedHand, Is.EqualTo(hand));
            Assert.That(SComp<ChildOrganComponent>(arm).Parent,
                Is.EqualTo(GetOrgan(body, "Torso")));
            Assert.That(SComp<OrganComponent>(arm).Body, Is.EqualTo(body));
            Assert.That(SComp<OrganComponent>(hand).Body, Is.EqualTo(body));
            Assert.That(SComp<HandsComponent>(body).Hands, Does.ContainKey("left"));
            Assert.That(SComp<NCOrganIntegrityComponent>(arm).CurrentIntegrity,
                Is.EqualTo(preservedIntegrity));
            Assert.That(SEntMan.HasComponent<NCDetachedOrganMedicalDataComponent>(detached), Is.False);
            Assert.That(woundContainer.Wounds.Keys, Does.Contain(woundId));
            Assert.That(woundContainer.OverflowTraumaByZone["NCZoneArmLeft"], Is.EqualTo(12f));
            Assert.That(woundContainer.OverflowTraumaByZone["NCZoneChest"], Is.EqualTo(4f));
            Assert.That(woundContainer.OverflowPain, Is.EqualTo(8f).Within(0.001f));
            Assert.That(bleeding.OverflowExternalRate, Is.EqualTo(4f).Within(0.001f));
            Assert.That(bleeding.OverflowInternalRate, Is.EqualTo(2f).Within(0.001f));
            Assert.That(SComp<NCTreatmentStateComponent>(body).WoundTreatments.Keys, Does.Contain(woundId));
            Assert.That(SComp<NCBleedingComponent>(body).Sources
                    .Where(source => source.WoundId == woundId)
                    .Select(source => source.SourceId),
                Is.EquivalentTo(sourceIds));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void MissingVitalOrganImmediatelyRecalculatesAndSameEntityCanBeReattached()
    {
        var body = SSpawn(HumanMob);
        Assert.That(_anatomy.TryGetOrgan(body, "Heart", out var heart), Is.True);
        var function = SComp<NCOrganFunctionComponent>(heart);
        function.CurrentCapacity = 0.75f;

        Assert.That(_detachment.TryDetachOrgan(body, "Heart", out var detached), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(detached, Is.EqualTo(heart));
            Assert.That(SComp<NCPhysiologyComponent>(body).Condition,
                Is.EqualTo(NCPhysiologicalCondition.ClinicalArrest));
            Assert.That(SComp<MobStateComponent>(body).CurrentState, Is.EqualTo(MobState.Critical));
        });

        Assert.That(_detachment.TryAttachOrgan(body, "Thorax", detached), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(GetOrgan(body, "Heart"), Is.EqualTo(heart));
            Assert.That(SComp<NCOrganFunctionComponent>(heart).CurrentCapacity, Is.EqualTo(0.75f));
            Assert.That(SComp<NCPhysiologyComponent>(body).Condition,
                Is.EqualTo(NCPhysiologicalCondition.Stable));
            Assert.That(SComp<MobStateComponent>(body).CurrentState, Is.EqualTo(MobState.Alive));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void DuplicateCategoryRejectsForeignAttachWithoutMutatingEitherBody()
    {
        var donor = SSpawn(HumanMob);
        var recipient = SSpawn(HumanMob);
        var recipientArm = GetOrgan(recipient, "ArmLeft");

        Assert.That(_detachment.TryDetachOrgan(donor, "Torso", out _), Is.False,
            "The graph root has no reattachable boundary edge.");
        Assert.That(_detachment.TryDetachOrgan(donor, "ArmLeft", out var detached), Is.True);
        Assert.That(_detachment.TryAttachOrgan(donor, "Head", detached), Is.False,
            "A detached subtree can only restore its severed boundary category.");
        Assert.That(_detachment.TryAttachOrgan(recipient, "Torso", detached), Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(GetOrgan(recipient, "ArmLeft"), Is.EqualTo(recipientArm));
            Assert.That(SComp<OrganComponent>(detached).Body, Is.Null);
            Assert.That(SComp<ChildOrganComponent>(detached).Parent, Is.Null);
        });

        Assert.That(_detachment.TryAttachOrgan(donor, "Torso", detached), Is.True);
        Assert.That(GetOrgan(donor, "ArmLeft"), Is.EqualTo(detached));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void HeadSubtreeUsesNormalBrainEventsToTransferMindBothWays()
    {
        var body = SSpawn(HumanMob);
        var brain = GetOrgan(body, "Brain");
        var mind = _minds.CreateMind(null, "detachment-test");
        _minds.TransferTo(mind, body, mind: mind);
        Assert.That(_minds.GetMind(body), Is.EqualTo(mind.Owner));

        Assert.That(_detachment.TryDetachOrgan(body, "Head", out var head), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(_minds.GetMind(body), Is.Null);
            Assert.That(_minds.GetMind(brain), Is.EqualTo(mind.Owner));
            Assert.That(SComp<OrganComponent>(brain).Body, Is.Null);
        });

        Assert.That(_detachment.TryAttachOrgan(body, "Torso", head), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(_minds.GetMind(brain), Is.Null);
            Assert.That(_minds.GetMind(body), Is.EqualTo(mind.Owner));
            Assert.That(SComp<OrganComponent>(brain).Body, Is.EqualTo(body));
        });
    }

    private EntityUid GetOrgan(EntityUid body, ProtoId<OrganCategoryPrototype> category)
    {
        Assert.That(_anatomy.TryGetOrgan(body, category, out var organ), Is.True);
        return organ;
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
