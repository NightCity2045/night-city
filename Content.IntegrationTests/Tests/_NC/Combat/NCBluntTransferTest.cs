// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Shared._NC.Combat.Armor;
using Content.Shared._NC.Combat.Damage;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared.FixedPoint;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Combat;

[TestFixture]
[TestOf(typeof(NCBluntTransferRules))]
public sealed class NCBluntTransferTest : GameTest
{
    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void StoppedBallisticTransferUsesHighestMatchingDiscreteThreshold()
    {
        ProtoId<NCBluntTransferProfilePrototype> profileId = "NCBallisticStoppedTransfer";
        var profile = _prototypes.Index(profileId);
        var soft = ResolveStopped(rawDamage: 17, rigid: false);
        var light = ResolveStopped(rawDamage: 18, rigid: false);
        var moderate = ResolveStopped(rawDamage: 28, rigid: false);
        var heavy = ResolveStopped(rawDamage: 40, rigid: false);

        Assert.Multiple(() =>
        {
            Assert.That(NCBluntTransferRules.Resolve(profile, soft), Is.EqualTo(FixedPoint2.Zero));
            Assert.That(NCBluntTransferRules.Resolve(profile, light), Is.EqualTo(FixedPoint2.New(1)));
            Assert.That(NCBluntTransferRules.Resolve(profile, moderate), Is.EqualTo(FixedPoint2.New(3)));
            Assert.That(NCBluntTransferRules.Resolve(profile, heavy), Is.EqualTo(FixedPoint2.New(6)));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void TransferProfilesAreDeterministicAndSemanticallyValid()
    {
        foreach (var damageProfile in _prototypes.EnumeratePrototypes<NCCombatDamageProfilePrototype>())
        {
            Assert.Multiple(() =>
            {
                Assert.That(_prototypes.HasIndex(damageProfile.TraumaProfile), Is.True,
                    $"Combat profile {damageProfile.ID} references an unknown trauma profile.");
                Assert.That(damageProfile.AblationAmount, Is.GreaterThanOrEqualTo(0));
                Assert.That(damageProfile.EmbedMinimumPostArmorDamage, Is.GreaterThanOrEqualTo(0f));
                Assert.That(damageProfile.ExitMinimumPostArmorDamage, Is.GreaterThanOrEqualTo(0f));
                Assert.That(damageProfile.ExitTraumaMultiplier, Is.InRange(0f, 1f));
                if ((damageProfile.TraumaFlags & Content.Shared._NC.Medical.Trauma.NCTraumaFlags.CanEmbed) != 0 &&
                    damageProfile.EmbedMinimumPostArmorDamage > 0f)
                {
                    Assert.That(damageProfile.EmbeddedObjectEntityPrototype, Is.Not.Null,
                        $"Combat profile {damageProfile.ID} can embed without an extracted entity prototype.");
                    Assert.That(
                        damageProfile.EmbeddedObjectEntityPrototype is { } embedded &&
                        _prototypes.HasIndex<EntityPrototype>(embedded),
                        Is.True,
                        $"Combat profile {damageProfile.ID} references an unknown extracted entity prototype.");
                }
                if (damageProfile.CanOverpenetrateBody)
                {
                    Assert.That(damageProfile.ExitMinimumPostArmorDamage, Is.GreaterThan(0f),
                        $"Combat profile {damageProfile.ID} can overpenetrate without an exit threshold.");
                    Assert.That(
                        (damageProfile.TraumaFlags & Content.Shared._NC.Medical.Trauma.NCTraumaFlags.CanExit) != 0,
                        Is.True,
                        $"Combat profile {damageProfile.ID} can overpenetrate without CanExit.");
                }
                if (damageProfile.BluntTransferProfile is { } transfer)
                    Assert.That(_prototypes.HasIndex(transfer), Is.True,
                        $"Combat profile {damageProfile.ID} references an unknown blunt-transfer profile.");
            });
        }

        foreach (var profile in _prototypes.EnumeratePrototypes<NCBluntTransferProfilePrototype>())
        {
            Assert.Multiple(() =>
            {
                Assert.That(_prototypes.HasIndex(profile.TraumaProfile), Is.True,
                    $"Transfer profile {profile.ID} references an unknown trauma profile.");
                Assert.That(profile.Rules, Is.Not.Empty, $"Transfer profile {profile.ID} has no rules.");
            });

            var keys = new HashSet<(FixedPoint2 Threshold, NCBluntTransferArmorKind Kind)>();
            foreach (var rule in profile.Rules)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(rule.MinimumRawDamage, Is.GreaterThanOrEqualTo(FixedPoint2.Zero));
                    Assert.That(rule.AppliedTrauma, Is.GreaterThan(FixedPoint2.Zero));
                    Assert.That(keys.Add((rule.MinimumRawDamage, rule.ArmorKind)), Is.True,
                        $"Transfer profile {profile.ID} repeats a threshold for {rule.ArmorKind} armor.");
                });
            }

            var first = NCBluntTransferRules.Resolve(profile, ResolveStopped(100, rigid: false));
            var second = NCBluntTransferRules.Resolve(profile, ResolveStopped(100, rigid: false));
            Assert.That(second, Is.EqualTo(first));
        }
    }

    private static NCArmorResolution ResolveStopped(int rawDamage, bool rigid)
    {
        var target = EntityUid.Invalid;
        ProtoId<NCTargetZonePrototype> zone = "NCZoneChest";
        ProtoId<NCArmorSectionPrototype> section = "NCArmorSectionLightArmorjackBody";
        var request = new NCArmorResolveRequest(target, zone, NCArmorDamageClass.Ballistic, rawDamage, 1);
        var snapshot = new NCArmorSectionSnapshot(
            new NCArmorSectionHandle(target, section),
            100,
            100,
            rigid,
            false);
        return NCArmorRules.Resolve(request, new[] { snapshot });
    }
}
