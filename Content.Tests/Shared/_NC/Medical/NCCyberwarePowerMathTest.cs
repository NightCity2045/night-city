// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Linq;
using Content.Shared._NC.Medical.Cyberware;
using NUnit.Framework;

namespace Content.Tests.Shared._NC.Medical;

[TestFixture]
public sealed class NCCyberwarePowerMathTest
{
    private static readonly Guid Heart = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Optics = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Weapon = Guid.Parse("00000000-0000-0000-0000-000000000003");

    [Test]
    public void BusPowerProtectsLifeSupportBeforeOptionalLoads()
    {
        var grants = NCCyberwarePowerMath.Allocate(
        [
            new(Weapon, NCCyberwarePowerPriority.Combat, 8f),
            new(Optics, NCCyberwarePowerPriority.Control, 3f),
            new(Heart, NCCyberwarePowerPriority.LifeSupport, 4f),
        ], 10f, true, true);
        Assert.Multiple(() =>
        {
            Assert.That(grants.Single(value => value.Id == Heart).Watts, Is.EqualTo(4f));
            Assert.That(grants.Single(value => value.Id == Optics).Watts, Is.EqualTo(3f));
            Assert.That(grants.Single(value => value.Id == Weapon).Source, Is.EqualTo(NCCyberwarePowerSource.None));
            Assert.That(grants.Sum(value => value.Watts), Is.EqualTo(7f));
        });
    }

    [Test]
    public void ReserveCannotSupplyControlOrCombatEvenWhenItsCellIsFull()
    {
        var grants = NCCyberwarePowerMath.Allocate(
        [
            new(Heart, NCCyberwarePowerPriority.LifeSupport, 4f),
            new(Optics, NCCyberwarePowerPriority.Control, 3f),
            new(Weapon, NCCyberwarePowerPriority.Combat, 8f),
        ], 100f, false, true);
        Assert.That(grants.Where(value => value.Source != NCCyberwarePowerSource.None).Select(value => value.Id),
            Is.EqualTo(new[] { Heart }));
        Assert.That(grants[0].Source, Is.EqualTo(NCCyberwarePowerSource.Reserve));
    }

    [Test]
    public void EqualPriorityUsesStableItemIdentityInsteadOfEnumerationOrder()
    {
        NCCyberwarePowerDemand[] demands =
        [
            new(Weapon, NCCyberwarePowerPriority.Professional, 5f),
            new(Optics, NCCyberwarePowerPriority.Professional, 5f),
        ];
        var first = NCCyberwarePowerMath.Allocate(demands, 5f, true, false);
        var second = NCCyberwarePowerMath.Allocate(demands.Reverse(), 5f, true, false);
        Assert.That(first, Is.EqualTo(second));
        Assert.That(first.Single(value => value.Source == NCCyberwarePowerSource.Main).Id, Is.EqualTo(Optics));
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    public void MissingSupplyOrInsufficientBusPowerNeverGrantsAPartialMinimum(bool main, bool reserve)
    {
        var grants = NCCyberwarePowerMath.Allocate([new(Heart, NCCyberwarePowerPriority.LifeSupport, 4f)],
            main ? 3f : 100f, main, reserve);
        Assert.That(grants[0].Source, Is.EqualTo(NCCyberwarePowerSource.None));
    }

    [TestCase(150f, 100f, 1000f, 6f)]
    [TestCase(100f, 100f, 1000f, 0f)]
    [TestCase(150f, 100f, 2f, 2f)]
    [TestCase(100.01f, 100f, 1000f, 1.2f)]
    public void ConversionIsBoundedByTimeProtectedNutritionAndRealCapacity(
        float nutrition, float protection, float space, float expected)
    {
        Assert.That(NCCyberwarePowerMath.ConversionEnergy(2f, 3f, nutrition, protection, 120f, space),
            Is.EqualTo(expected).Within(0.002f));
    }

    [Test]
    public void HeatDamageCountsOnlyTimeActuallyAboveTheThreshold()
    {
        var one = NCCyberwarePowerMath.AdvanceHeat(0f, 10f, 10f, 100f, 90f, 10f);
        var first = NCCyberwarePowerMath.AdvanceHeat(0f, 10f, 9f, 100f, 90f, 10f);
        var second = NCCyberwarePowerMath.AdvanceHeat(first.Heat, 10f, 1f, 100f, 90f, 10f);
        Assert.Multiple(() =>
        {
            Assert.That(one.Heat, Is.EqualTo(100f));
            Assert.That(one.Damage, Is.EqualTo(5f).Within(0.0001f));
            Assert.That(first.Damage + second.Damage, Is.EqualTo(one.Damage).Within(0.0001f));
            Assert.That(NCCyberwarePowerMath.AdvanceHeat(100f, -10f, 10f, 100f, 90f, 10f).Damage,
                Is.EqualTo(5f).Within(0.0001f));
        });
    }
}
