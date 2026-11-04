// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Armor;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared.FixedPoint;
using Robust.Shared.GameObjects;
using Content.Shared._NC.Medical.Surgery;
using NUnit.Framework;

namespace Content.Tests.Shared._NC.Medical;

[TestFixture]
public sealed class NCDermalArmorTest
{
    [TestCase(7f)]
    [TestCase(11f)]
    public void PenetrationsConsumeDurabilityAndRepairRestoresProtection(float nominal)
    {
        var integrity = 100f;
        for (var hits = 1; hits <= (int) nominal; hits++)
        {
            integrity = NCDermalArmorMath.Ablate(integrity, 100f, nominal, 1f);
            Assert.That(NCDermalArmorMath.Sp(nominal, integrity, 100f),
                Is.EqualTo(nominal - hits).Within(.001f));
        }
        Assert.That(NCDermalArmorMath.Sp(nominal, 100f, 100f), Is.EqualTo(nominal));
        Assert.That(NCDermalArmorMath.Sp(nominal, -10f, 100f), Is.Zero);
        Assert.That(NCDermalArmorMath.Sp(nominal, 200f, 100f), Is.EqualTo(nominal));
        Assert.That(NCDermalArmorMath.Ablate(50f, 100f, nominal, 0f), Is.EqualTo(50f));
        Assert.That(NCDermalArmorMath.Ablate(10f, 100f, nominal, 2f), Is.Zero);
    }

    [Test]
    public void WornArmorAndImplantUseMaximumAndOnlyPenetrationsAblate()
    {
        NCArmorSectionSnapshot[] layers =
        [
            new(new(new EntityUid(1), "NCSkinweaveTorsoSurface"), 7, 7, false, false),
            new(new(new EntityUid(2), "TestWornArmor"), 11, 11, true, false),
        ];
        var request = new NCArmorResolveRequest(new EntityUid(3), "NCZoneChest", NCArmorDamageClass.Ballistic, 15, 1);
        var result = NCArmorRules.Resolve(request, layers);
        Assert.That(result.EffectiveSp, Is.EqualTo(FixedPoint2.New(11)));
        Assert.That(result.PostArmorDamage, Is.EqualTo(FixedPoint2.New(4)));
        Assert.That(result.AblationApplied, Is.EqualTo(1));
        var stopped = NCArmorRules.Resolve(request with { RawDamage = 10 }, layers);
        Assert.That(stopped.Stopped, Is.True);
        Assert.That(stopped.AblationApplied, Is.Zero);
    }

    [Test]
    public void DermalInstallationCannotSkipRegionalDistributionOrFinalCheck()
    {
        NCSkeletalSurgeryStep[] plan =
        [
            new() { Stage = NCSkeletalSurgeryStage.DermalChest, Action = NCFreeformSurgeryAction.DistributeDermal },
            new() { Stage = NCSkeletalSurgeryStage.DermalAbdomen, Action = NCFreeformSurgeryAction.DistributeDermal,
                Prerequisites = NCSkeletalSurgeryStage.DermalChest },
            new() { Stage = NCSkeletalSurgeryStage.DermalGroin, Action = NCFreeformSurgeryAction.DistributeDermal,
                Prerequisites = NCSkeletalSurgeryStage.DermalAbdomen },
            new() { Stage = NCSkeletalSurgeryStage.DermalFixation, Action = NCFreeformSurgeryAction.FixDermal,
                Prerequisites = NCSkeletalSurgeryStage.DermalGroin },
            new() { Stage = NCSkeletalSurgeryStage.DermalCheck, Action = NCFreeformSurgeryAction.CheckDermal,
                Prerequisites = NCSkeletalSurgeryStage.DermalFixation },
        ];
        var all = NCSkeletalSurgeryPlan.Required(plan);
        Assert.That(NCSkeletalSurgeryPlan.ValidDefinition(plan), Is.True);
        Assert.That(NCSkeletalSurgeryPlan.Complete(plan, all), Is.True);
        Assert.That(NCSkeletalSurgeryPlan.Complete(plan, all & ~NCSkeletalSurgeryStage.DermalCheck), Is.False);
        Assert.That(NCSkeletalSurgeryPlan.Valid(plan, all & ~NCSkeletalSurgeryStage.DermalAbdomen), Is.False);
        Assert.That(NCSkeletalSurgeryPlan.Prune(plan, all & ~NCSkeletalSurgeryStage.DermalChest), Is.EqualTo(NCSkeletalSurgeryStage.None));
    }
}
