// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Anatomy.Detachment;
using Content.Server._NC.Medical.Function;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Function;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared.ActionBlocker;
using Content.Shared.Body;
using Content.Shared.Eye.Blinding.Components;
using Content.Shared.Eye.Blinding.Systems;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Speech;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

[TestFixture]
[TestOf(typeof(NCBodyFunctionSystem))]
public sealed class NCBodyFunctionSystemTest : GameTest
{
    private static readonly EntProtoId HumanMob = "MobHuman";
    private static readonly EntProtoId TestItem = "Crowbar";

    [SidedDependency(Side.Server)] private NCAnatomySystem _anatomy = default!;
    [SidedDependency(Side.Server)] private NCMedicalBodySystem _medicalBodies = default!;
    [SidedDependency(Side.Server)] private NCOrganDetachmentSystem _detachment = default!;
    [SidedDependency(Side.Server)] private SharedHandsSystem _hands = default!;
    [SidedDependency(Side.Server)] private ActionBlockerSystem _blocker = default!;
    [SidedDependency(Side.Server)] private MovementSpeedModifierSystem _movement = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void HealthyHumanHasFullSanitizedFunctions()
    {
        var body = SSpawn(HumanMob);
        var function = SComp<NCBodyFunctionComponent>(body);

        Assert.Multiple(() =>
        {
            Assert.That(function.Movement, Is.EqualTo(1f).Within(0.001f));
            Assert.That(function.LeftGrip, Is.EqualTo(1f).Within(0.001f));
            Assert.That(function.RightGrip, Is.EqualTo(1f).Within(0.001f));
            Assert.That(function.Vision, Is.EqualTo(1f).Within(0.001f));
            Assert.That(function.Hearing, Is.EqualTo(1f).Within(0.001f));
            Assert.That(function.Speech, Is.EqualTo(1f).Within(0.001f));
        });

        _movement.RefreshMovementSpeedModifiers(body);
        var movement = SComp<MovementSpeedModifierComponent>(body);
        var canSee = new CanSeeAttemptEvent();
        SEntMan.EventBus.RaiseLocalEvent(body, canSee);
        var canListen = new ListenAttemptEvent(body);
        SEntMan.EventBus.RaiseLocalEvent(body, canListen);
        Assert.Multiple(() =>
        {
            Assert.That(movement.WalkSpeedModifier, Is.EqualTo(1f).Within(0.001f));
            Assert.That(movement.SprintSpeedModifier, Is.EqualTo(1f).Within(0.001f));
            Assert.That(canSee.Cancelled, Is.False);
            Assert.That(canListen.Cancelled, Is.False);
            Assert.That(_blocker.CanSpeak(body), Is.True);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void RupturedArmBlocksOnlyItsActiveHandAndDoesNotSlowLocomotion()
    {
        var body = SSpawn(HumanMob);
        var held = SSpawn(TestItem);
        Assert.That(_hands.TryPickup(body, held, "left", animate: false), Is.True);
        _hands.SetActiveHand(body, "left");

        var tendonUid = GetOrgan(body, "TendonSetLeftArm");
        var tendon = SComp<NCTendonComponent>(tendonUid);
        tendon.Integrity = 0f;
        tendon.Ruptured = true;
        tendon.MovementContribution = 0f;
        tendon.GripContribution = 0f;
        MarkFunctionDirty(body);

        var function = SComp<NCBodyFunctionComponent>(body);
        var pickupCandidate = SSpawn(TestItem);
        Assert.Multiple(() =>
        {
            Assert.That(function.LeftGrip, Is.Zero.Within(0.001f));
            Assert.That(function.RightGrip, Is.EqualTo(1f).Within(0.001f));
            Assert.That(function.Movement, Is.EqualTo(1f).Within(0.001f),
                "Arm manipulation must not be folded into leg locomotion.");
            Assert.That(_blocker.CanUseHeldEntity(body, held), Is.False);
            Assert.That(_blocker.CanPickup(body, pickupCandidate), Is.False);
        });

        _hands.SetActiveHand(body, "right");
        Assert.Multiple(() =>
        {
            Assert.That(_blocker.CanPickup(body, pickupCandidate), Is.True);
            Assert.That(_hands.TryPickup(body, pickupCandidate, "right", animate: false), Is.True);
            Assert.That(_blocker.CanUseHeldEntity(body, pickupCandidate), Is.True);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void RupturedLegDisablesDistalFootContributionAndRestrictsSprint()
    {
        var body = SSpawn(HumanMob);
        var tendonUid = GetOrgan(body, "TendonSetLeftLeg");
        var tendon = SComp<NCTendonComponent>(tendonUid);
        tendon.Integrity = 0f;
        tendon.Ruptured = true;
        tendon.MovementContribution = 0f;
        MarkFunctionDirty(body);

        var function = SComp<NCBodyFunctionComponent>(body);
        var expectedMovement = 1.35f / 2.35f;
        _movement.RefreshMovementSpeedModifiers(body);
        var movement = SComp<MovementSpeedModifierComponent>(body);
        Assert.Multiple(() =>
        {
            Assert.That(function.Movement, Is.EqualTo(expectedMovement).Within(0.001f));
            Assert.That(movement.WalkSpeedModifier, Is.EqualTo(expectedMovement).Within(0.001f));
            Assert.That(movement.SprintSpeedModifier, Is.EqualTo(expectedMovement * 0.4f).Within(0.001f));
            Assert.That(function.LeftGrip, Is.EqualTo(1f).Within(0.001f));
            Assert.That(function.RightGrip, Is.EqualTo(1f).Within(0.001f));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void SensoryOrgansAndPhysiologyDriveStandardGameplayGates()
    {
        var body = SSpawn(HumanMob);
        SComp<NCOrganFunctionComponent>(GetOrgan(body, "Eyes")).CurrentCapacity = 0f;
        SComp<NCOrganFunctionComponent>(GetOrgan(body, "Ears")).CurrentCapacity = 0f;
        SComp<NCOrganFunctionComponent>(GetOrgan(body, "Tongue")).CurrentCapacity = 0f;
        MarkFunctionDirty(body);

        var function = SComp<NCBodyFunctionComponent>(body);
        var canSee = new CanSeeAttemptEvent();
        SEntMan.EventBus.RaiseLocalEvent(body, canSee);
        var canListen = new ListenAttemptEvent(body);
        SEntMan.EventBus.RaiseLocalEvent(body, canListen);
        Assert.Multiple(() =>
        {
            Assert.That(function.Vision, Is.Zero.Within(0.001f));
            Assert.That(function.Hearing, Is.Zero.Within(0.001f));
            Assert.That(function.Speech, Is.Zero.Within(0.001f));
            Assert.That(canSee.Cancelled, Is.True);
            Assert.That(canListen.Cancelled, Is.True);
            Assert.That(_blocker.CanSpeak(body), Is.False);
            Assert.That(SComp<BlindableComponent>(body).IsBlind, Is.True);
        });

        var secondBody = SSpawn(HumanMob);
        var physiology = SComp<NCPhysiologyComponent>(secondBody);
        physiology.PainLevel = NCPainLevel.Severe;
        physiology.Consciousness = NCConsciousnessState.Confused;
        var evaluated = new NCPhysiologyEvaluatedEvent();
        SEntMan.EventBus.RaiseLocalEvent(secondBody, ref evaluated);

        var impaired = SComp<NCBodyFunctionComponent>(secondBody);
        Assert.Multiple(() =>
        {
            Assert.That(impaired.Movement, Is.EqualTo(0.68f * 0.72f).Within(0.001f));
            Assert.That(impaired.LeftGrip, Is.EqualTo(0.68f * 0.72f).Within(0.001f));
            Assert.That(impaired.RightGrip, Is.EqualTo(0.68f * 0.72f).Within(0.001f));
            Assert.That(impaired.Vision, Is.EqualTo(0.78f).Within(0.001f));
            Assert.That(impaired.Hearing, Is.EqualTo(0.78f).Within(0.001f));
            Assert.That(impaired.Speech, Is.EqualTo(0.78f).Within(0.001f));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void LiveOrganContainerDetachmentAndReattachmentRebuildsGrip()
    {
        var body = SSpawn(HumanMob);
        Assert.That(SComp<NCBodyFunctionComponent>(body).LeftGrip, Is.EqualTo(1f).Within(0.001f));

        Assert.That(_detachment.TryDetachOrgan(body, "ArmLeft", out var arm), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(SComp<NCBodyFunctionComponent>(body).LeftGrip, Is.Zero.Within(0.001f));
            Assert.That(SComp<NCBodyFunctionComponent>(body).Movement, Is.EqualTo(1f).Within(0.001f));
            Assert.That(SComp<HandsComponent>(body).Hands, Does.Not.ContainKey("left"));
        });

        Assert.That(_detachment.TryAttachOrgan(body, "Torso", arm), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(SComp<NCBodyFunctionComponent>(body).LeftGrip, Is.EqualTo(1f).Within(0.001f));
            Assert.That(SComp<HandsComponent>(body).Hands, Does.ContainKey("left"));
        });
    }

    private EntityUid GetOrgan(EntityUid body, ProtoId<OrganCategoryPrototype> category)
    {
        Assert.That(_anatomy.TryGetOrgan(body, category, out var organ), Is.True);
        return organ;
    }

    private void MarkFunctionDirty(EntityUid body)
    {
        var medical = SComp<NCMedicalBodyComponent>(body);
        Assert.That(_medicalBodies.TryMarkDirty(
            (body, medical),
            NCMedicalDirtyDomains.OrganFunction), Is.True);
    }
}
