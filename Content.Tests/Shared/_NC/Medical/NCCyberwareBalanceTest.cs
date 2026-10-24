// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Linq;
using Content.Shared._NC.Medical.Cyberware;
using NUnit.Framework;

namespace Content.Tests.Shared._NC.Medical;

/// <summary>Resource conservation and time-partition checks; these are not combat or server-load benchmarks.</summary>
[TestFixture]
public sealed class NCCyberwareBalanceTest
{
    [TestCase(1, 17)]
    [TestCase(8, 17)]
    [TestCase(32, 17)]
    [TestCase(64, 17)]
    [TestCase(1, 2045)]
    [TestCase(8, 2045)]
    [TestCase(32, 2045)]
    [TestCase(64, 2045)]
    public void SupplyLossAndRecoveryConserveBusPowerAndKeepStableAllocation(int count, int seed)
    {
        var random = new Random(seed);
        var demands = Enumerable.Range(0, count).Select(i =>
        {
            var bytes = new byte[16];
            BitConverter.GetBytes(i + 1).CopyTo(bytes, 0);
            return new NCCyberwarePowerDemand(new Guid(bytes), (NCCyberwarePowerPriority) (i % 4),
                .1f + (float) random.NextDouble() * 20f);
        }).ToArray();
        const float maximum = 40f;
        var baseline = NCCyberwarePowerMath.Allocate(demands, maximum, true, true);
        var reserve = NCCyberwarePowerMath.Allocate(demands, maximum, false, true);
        var dead = NCCyberwarePowerMath.Allocate(demands, maximum, false, false);
        var recovered = NCCyberwarePowerMath.Allocate(demands.Reverse(), maximum, true, true);
        Assert.Multiple(() =>
        {
            Assert.That(baseline.Sum(grant => grant.Watts), Is.LessThanOrEqualTo(maximum + .0001f));
            Assert.That(reserve.Sum(grant => grant.Watts), Is.LessThanOrEqualTo(maximum + .0001f));
            Assert.That(dead.All(grant => grant.Source == NCCyberwarePowerSource.None && grant.Watts == 0f), Is.True);
            Assert.That(recovered, Is.EqualTo(baseline));
            Assert.That(reserve.Where(grant => grant.Source == NCCyberwarePowerSource.Reserve)
                .All(grant => demands.Single(demand => demand.Id == grant.Id).Priority == NCCyberwarePowerPriority.LifeSupport), Is.True);
        });
        foreach (var grant in baseline)
            Assert.That(grant.Watts, Is.EqualTo(grant.Source == NCCyberwarePowerSource.None
                ? 0f : demands.Single(demand => demand.Id == grant.Id).Watts));
    }

    [TestCase(0f, 5.55f, 6f)]
    [TestCase(19.8f, 5.55f, 6f)]
    [TestCase(85f, 5.55f, 12f)]
    [TestCase(100f, -.45f, 30f)]
    [TestCase(95f, -2f, 60f)]
    [TestCase(100f, 0f, 60f)]
    public void HeatAndIrreversibleDamageDoNotDependOnCheckpointFrequency(float heat, float rate, float seconds)
    {
        var whole = NCCyberwarePowerMath.AdvanceHeat(heat, rate, seconds, 100f, 90f, 1f);
        var steppedHeat = heat;
        var steppedDamage = 0f;
        for (var i = 0; i < 100; i++)
        {
            var step = NCCyberwarePowerMath.AdvanceHeat(steppedHeat, rate, seconds / 100f, 100f, 90f, 1f);
            steppedHeat = step.Heat;
            steppedDamage += step.Damage;
        }
        Assert.Multiple(() =>
        {
            Assert.That(whole.Heat, Is.InRange(0f, 100f));
            Assert.That(whole.Damage, Is.GreaterThanOrEqualTo(0f));
            Assert.That(steppedHeat, Is.EqualTo(whole.Heat).Within(.002f));
            Assert.That(steppedDamage, Is.EqualTo(whole.Damage).Within(.002f));
        });
    }

    [TestCase(100f, 64800f)]
    [TestCase(120f, 64800f)]
    [TestCase(120f, 5f)]
    public void BioconversionCannotGainEnergyBySplittingTheSameFoodAndCapacity(float nutrition, float capacity)
    {
        const float seconds = 60f;
        var whole = NCCyberwarePowerMath.ConversionEnergy(seconds, 3f, nutrition, 100f, 120f, capacity);
        var stepped = 0f;
        var remainingFood = nutrition;
        for (var i = 0; i < 60; i++)
        {
            var energy = NCCyberwarePowerMath.ConversionEnergy(1f, 3f, remainingFood, 100f, 120f, capacity - stepped);
            stepped += energy;
            remainingFood -= energy / 120f;
        }
        Assert.That(stepped, Is.EqualTo(whole).Within(.01f));
        Assert.That(remainingFood, Is.GreaterThanOrEqualTo(100f));
    }
}
