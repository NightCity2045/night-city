// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Combat.Physical;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Physical;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Damage.Systems;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Combat;

[TestFixture]
[TestOf(typeof(NCPhysicalImpactAdapterSystem))]
public sealed class NCPhysicalImpactAdapterTest : GameTest
{
    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;
    [SidedDependency(Side.Server)] private DamageableSystem _damageable = default!;
    [SidedDependency(Side.Server)] private NCPhysicalImpactAdapterSystem _physical = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void FallAndThrownPayloadsUseDistinctProfilesAndNeverMutateHumanHp()
    {
        var fallTarget = SSpawn("MobHuman");
        var thrownTarget = SSpawn("MobHuman");
        ProtoId<DamageTypePrototype> bluntId = "Blunt";
        ProtoId<DamageTypePrototype> piercingId = "Piercing";
        var fall = new NCPhysicalImpactRequestedEvent(
            NCPhysicalImpactSourceKind.Fall,
            new DamageSpecifier(_prototypes.Index(bluntId), 8),
            null,
            fallTarget,
            Vector2.Zero,
            ResolutionId: new NCActionResolutionId(Guid.Parse("2e544243-27ab-4ad1-a413-1715325b9133")));
        var thrown = new NCPhysicalImpactRequestedEvent(
            NCPhysicalImpactSourceKind.ThrownObject,
            new DamageSpecifier(_prototypes.Index(piercingId), 8),
            fallTarget,
            fallTarget,
            Vector2.UnitX,
            ResolutionId: new NCActionResolutionId(Guid.Parse("6d76a53a-3b99-4399-b775-8feb89c2b315")));

#pragma warning disable CS0618 // Compatibility HP is observed only to prove the authoritative adapter bypasses it.
        var fallHp = _damageable.GetTotalDamage(fallTarget);
        var thrownHp = _damageable.GetTotalDamage(thrownTarget);
#pragma warning restore CS0618

        Assert.That(_physical.TryApply(fallTarget, ref fall), Is.True);
        Assert.That(_physical.TryApply(thrownTarget, ref thrown), Is.True);
        var fallWound = SComp<NCWoundContainerComponent>(fallTarget).Wounds.Values.Single();
        var thrownWound = SComp<NCWoundContainerComponent>(thrownTarget).Wounds.Values.Single();
        Assert.Multiple(() =>
        {
            Assert.That(fall.Handled, Is.True);
            Assert.That(thrown.Handled, Is.True);
            Assert.That(fallWound.WoundType, Is.EqualTo(NCWoundType.Blunt));
            Assert.That(thrownWound.WoundType, Is.EqualTo(NCWoundType.Pierce));
        });

        // Replaying the immutable throw correlation is accepted without a second wound.
        Assert.That(_physical.TryApply(thrownTarget, ref thrown), Is.True);
        Assert.That(SComp<NCWoundContainerComponent>(thrownTarget).Wounds, Has.Count.EqualTo(1));

#pragma warning disable CS0618
        Assert.That(_damageable.GetTotalDamage(fallTarget), Is.EqualTo(fallHp));
        Assert.That(_damageable.GetTotalDamage(thrownTarget), Is.EqualTo(thrownHp));
#pragma warning restore CS0618
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void PhysicalImpactProfileCoversEveryTrustedSource()
    {
        var defaults = _prototypes.EnumeratePrototypes<NCPhysicalImpactProfilePrototype>()
            .Where(profile => profile.DefaultForHumans)
            .ToArray();
        Assert.That(defaults, Has.Length.EqualTo(1));

        var profile = defaults[0];
        Assert.That(profile.Sources.Keys, Is.EquivalentTo(Enum.GetValues<NCPhysicalImpactSourceKind>()));
        foreach (var definition in profile.Sources.Values)
        {
            Assert.That(definition.Contamination, Is.InRange(0f, 1f));
            if (definition.DamageProfile is { } damageProfile)
                Assert.That(_prototypes.HasIndex(damageProfile), Is.True, damageProfile.Id);
        }
    }
}
