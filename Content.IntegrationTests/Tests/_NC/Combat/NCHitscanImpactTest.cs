// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Combat.Hitscan;
using Content.Server._NC.Medical.Systemic;
using Content.Shared._NC.Medical.Systemic;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.FixedPoint;
using Content.Shared.Damage.Systems;
using Content.Shared.Weapons.Hitscan.Events;

namespace Content.IntegrationTests.Tests._NC.Combat;

[TestFixture]
[TestOf(typeof(NCHitscanImpactSystem))]
public sealed class NCHitscanImpactTest : GameTest
{
    [SidedDependency(Side.Server)] private DamageableSystem _damageable = default!;
    [SidedDependency(Side.Server)] private NCSystemicSystem _systemic = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void ValidatedRaycastCreatesThermalWoundWithoutLegacyHpDamage()
    {
        var body = SSpawn("MobHuman");
        var hitscan = SSpawn("RedLaser");
        var fired = new HitscanRaycastFiredEvent
        {
            Data = new HitscanRaycastFiredData
            {
                ShotDirection = Vector2.UnitX,
                HitEntity = body,
                Gun = hitscan,
                Shooter = null,
            },
        };

#pragma warning disable CS0618 // Compatibility HP is observed only to prove the adapter bypassed it.
        var hpBefore = _damageable.GetTotalDamage(body);
#pragma warning restore CS0618

        SEntMan.EventBus.RaiseLocalEvent(hitscan, ref fired);

        var wounds = SComp<NCWoundContainerComponent>(body).Wounds.Values;
        Assert.Multiple(() =>
        {
            Assert.That(wounds, Has.Count.EqualTo(1));
            Assert.That(wounds.Single().WoundType, Is.EqualTo(NCWoundType.Thermal));
#pragma warning disable CS0618 // Compatibility HP is observed only to prove the adapter bypassed it.
            Assert.That(_damageable.GetTotalDamage(body), Is.EqualTo(hpBefore));
#pragma warning restore CS0618
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void XrayRaycastPartitionsHeatFromRadiationAtTheSamePhysicalHit()
    {
        var body = SSpawn("MobHuman");
        var hitscan = SSpawn("XrayLaser");
        var fired = new HitscanRaycastFiredEvent
        {
            Data = new HitscanRaycastFiredData
            {
                ShotDirection = Vector2.UnitX,
                HitEntity = body,
                Gun = hitscan,
                Shooter = null,
            },
        };

#pragma warning disable CS0618 // Compatibility HP is observed only to prove the rich adapter never falls back.
        var hpBefore = _damageable.GetTotalDamage(body);
#pragma warning restore CS0618

        SEntMan.EventBus.RaiseLocalEvent(hitscan, ref fired);

        var wounds = SComp<NCWoundContainerComponent>(body).Wounds.Values.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(wounds, Has.Length.EqualTo(1));
            Assert.That(wounds[0].WoundType, Is.EqualTo(NCWoundType.Thermal));
            Assert.That(wounds[0].IntegrityDamage, Is.EqualTo(FixedPoint2.New(10)));
            Assert.That(_systemic.GetBurden(body, NCSystemicChannel.Radiation), Is.EqualTo(10f));
#pragma warning disable CS0618 // Compatibility HP is observed only to prove the rich adapter never falls back.
            Assert.That(_damageable.GetTotalDamage(body), Is.EqualTo(hpBefore));
#pragma warning restore CS0618
        });
    }
}
