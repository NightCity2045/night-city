// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Surgery;
using NUnit.Framework;
using Robust.Shared.IoC;
using Robust.Shared.Serialization.Manager;

namespace Content.Tests.Shared._NC.Medical;

[TestFixture]
public sealed class NCSkeletalSurgeryTest : ContentUnitTest
{
    [OneTimeSetUp]
    public void InitializeSerialization() => IoCManager.Resolve<ISerializationManager>().Initialize();

    private static NCSkeletalSurgeryStep[] Plan() =>
    [
        new() { Stage = NCSkeletalSurgeryStage.Torso, Action = NCFreeformSurgeryAction.AnchorSkeletal },
        new() { Stage = NCSkeletalSurgeryStage.Pelvis, Action = NCFreeformSurgeryAction.AnchorSkeletal,
            Prerequisites = NCSkeletalSurgeryStage.Torso },
        new() { Stage = NCSkeletalSurgeryStage.ArmLeft, Action = NCFreeformSurgeryAction.ConnectSkeletal,
            Prerequisites = NCSkeletalSurgeryStage.Torso | NCSkeletalSurgeryStage.Pelvis },
        new() { Stage = NCSkeletalSurgeryStage.ArmRight, Action = NCFreeformSurgeryAction.ConnectSkeletal,
            Prerequisites = NCSkeletalSurgeryStage.Torso | NCSkeletalSurgeryStage.Pelvis },
    ];

    [Test]
    public void IncompleteAndForgedProgressCannotPass()
    {
        var plan = Plan();
        Assert.Multiple(() =>
        {
            Assert.That(NCSkeletalSurgeryPlan.ValidDefinition(plan), Is.True);
            Assert.That(NCSkeletalSurgeryPlan.Complete(plan, NCSkeletalSurgeryStage.Torso), Is.False);
            Assert.That(NCSkeletalSurgeryPlan.Valid(plan, NCSkeletalSurgeryStage.ArmLeft), Is.False);
            Assert.That(NCSkeletalSurgeryPlan.Valid(plan, (NCSkeletalSurgeryStage) 4096), Is.False);
            Assert.That(NCSkeletalSurgeryPlan.Complete(plan, NCSkeletalSurgeryPlan.Required(plan)), Is.True);
        });
    }

    [Test]
    public void LostAnchorInvalidatesDependentLinksButLostLimbPreservesOtherLinks()
    {
        var plan = Plan();
        var all = NCSkeletalSurgeryPlan.Required(plan);
        Assert.Multiple(() =>
        {
            Assert.That(NCSkeletalSurgeryPlan.Prune(plan, all & ~NCSkeletalSurgeryStage.Torso), Is.EqualTo(NCSkeletalSurgeryStage.None));
            Assert.That(NCSkeletalSurgeryPlan.Prune(plan, all & ~NCSkeletalSurgeryStage.Pelvis), Is.EqualTo(NCSkeletalSurgeryStage.Torso));
            Assert.That(NCSkeletalSurgeryPlan.Prune(plan, all & ~NCSkeletalSurgeryStage.ArmLeft), Is.EqualTo(all & ~NCSkeletalSurgeryStage.ArmLeft));
        });
    }

    [Test]
    public void DuplicateCyclicAndCombinedStagesAreRejected()
    {
        var plan = Plan();
        plan[1].Stage = NCSkeletalSurgeryStage.Torso;
        Assert.That(NCSkeletalSurgeryPlan.ValidDefinition(plan), Is.False);
        plan = Plan();
        plan[0].Prerequisites = NCSkeletalSurgeryStage.ArmRight;
        Assert.That(NCSkeletalSurgeryPlan.ValidDefinition(plan), Is.False);
        plan = Plan();
        plan[0].Stage |= NCSkeletalSurgeryStage.Pelvis;
        Assert.That(NCSkeletalSurgeryPlan.ValidDefinition(plan), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PartialProgressAndLoadTestSurviveMedicalSerialization(bool loadTest)
    {
        var serialization = IoCManager.Resolve<ISerializationManager>();
        var source = new NCCyberwareStateSnapshot
        {
            Profile = "NCImplantedLinearFrameBeta",
            SkeletalSurgeryCompleted = NCSkeletalSurgeryStage.Torso | NCSkeletalSurgeryStage.Pelvis,
            SkeletalLoadTestPassed = loadTest,
        };
        var node = serialization.WriteValue(source, alwaysWrite: true, notNullableOverride: true);
        var restored = serialization.Read<NCCyberwareStateSnapshot>(node, notNullableOverride: true);
        Assert.That(restored.SkeletalSurgeryCompleted, Is.EqualTo(source.SkeletalSurgeryCompleted));
        Assert.That(restored.SkeletalLoadTestPassed, Is.EqualTo(loadTest));
    }
}
