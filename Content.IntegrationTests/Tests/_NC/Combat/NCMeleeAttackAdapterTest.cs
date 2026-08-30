// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Combat.Melee;
using Content.Server._NC.Combat.Targeting;
using Content.Server._NC.Medical.Systemic;
using Content.Shared._NC.Combat.Damage;
using Content.Shared._NC.Combat.Melee;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Systemic;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Combat;

[TestFixture]
[TestOf(typeof(NCMeleeAttackAdapterSystem))]
public sealed class NCMeleeAttackAdapterTest : GameTest
{
    private static readonly ProtoId<DamageTypePrototype> Blunt = "Blunt";
    private static readonly ProtoId<DamageTypePrototype> Heat = "Heat";
    private static readonly ProtoId<DamageTypePrototype> Piercing = "Piercing";
    private static readonly ProtoId<DamageTypePrototype> Shock = "Shock";

    [SidedDependency(Side.Server)] private IPrototypeManager _prototypes = default!;
    [SidedDependency(Side.Server)] private DamageableSystem _damageable = default!;
    [SidedDependency(Side.Server)] private NCMeleeAttackAdapterSystem _melee = default!;
    [SidedDependency(Side.Server)] private NCSystemicSystem _systemic = default!;
    [SidedDependency(Side.Server)] private NCTargetingSelectionSystem _targeting = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void AcceptedContactUsesSelectionSnapshotOnceAndNeverChangesLegacyHp()
    {
        var attacker = SSpawn("MobHuman");
        var target = SSpawn("MobHuman");
        var weapon = SSpawn(null);
        var piercing = new DamageSpecifier(_prototypes.Index(Piercing), 8);

        Assert.That(_targeting.TrySetSelection(
            attacker,
            new NCTargetingState(NCTargetingMode.SelectedZone, "NCZoneChest"),
            out _,
            out _), Is.True);

#pragma warning disable CS0618 // Compatibility HP is observed only to prove the NC adapter never mutates it.
        var hpBefore = _damageable.GetTotalDamage(target);
#pragma warning restore CS0618

        var resolution = _melee.BeginAttack(attacker, weapon, target);
        Assert.That(resolution.IsValid, Is.True);

        // Selection changes after the accepted attack must not alter the snapshotted physical resolution.
        Assert.That(_targeting.TrySetSelection(
            attacker,
            new NCTargetingState(NCTargetingMode.SelectedZone, "NCZoneHead"),
            out _,
            out _), Is.True);

        Assert.That(_melee.TryApplyImpact(attacker, weapon, target, piercing, null, out var firstFeedback), Is.True);
        var wounds = SComp<NCWoundContainerComponent>(target);
        Assert.Multiple(() =>
        {
            Assert.That(firstFeedback.GetTotal(), Is.EqualTo(FixedPoint2.New(8)));
            Assert.That(wounds.Wounds, Has.Count.EqualTo(1));
            Assert.That(wounds.Wounds.Values.Single().TargetZone.Id, Is.EqualTo("NCZoneChest"));
        });

        // Duplicate entities in a forged wide-attack list cannot repeat one contact.
        Assert.That(_melee.TryApplyImpact(attacker, weapon, target, piercing, null, out var duplicateFeedback), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(duplicateFeedback.GetTotal(), Is.EqualTo(FixedPoint2.Zero));
            Assert.That(wounds.Wounds, Has.Count.EqualTo(1));
        });

        _melee.EndAttack(attacker, weapon);
#pragma warning disable CS0618 // Compatibility HP is observed only to prove the NC adapter never mutates it.
        Assert.That(_damageable.GetTotalDamage(target), Is.EqualTo(hpBefore));
#pragma warning restore CS0618
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void DefaultMapAndWeaponOverrideAreDataDrivenAndDeterministic()
    {
        var weapon = SSpawn(null);
        var mixed = new DamageSpecifier(_prototypes.Index(Blunt), 3);
        mixed.DamageDict["Slash"] = 7;

        Assert.That(_melee.TryResolveDamageProfile(weapon, mixed, out var dominant), Is.True);
        Assert.That(dominant.Id, Is.EqualTo("NCMeleeSlashStandard"));

        // Equal entries use a stable lexical damage-type tie break instead of runtime dictionary order.
        mixed.DamageDict["Blunt"] = 7;
        Assert.That(_melee.TryResolveDamageProfile(weapon, mixed, out var tied), Is.True);
        Assert.That(tied.Id, Is.EqualTo("NCMeleeBluntStandard"));

        var profileOverride = SEntMan.EnsureComponent<NCMeleeDamageProfileComponent>(weapon);
        profileOverride.Profile = "NCMeleePiercingStandard";
        Assert.That(_melee.TryResolveDamageProfile(weapon, mixed, out var overridden), Is.True);
        Assert.That(overridden.Id, Is.EqualTo("NCMeleePiercingStandard"));

        var defaults = _prototypes.EnumeratePrototypes<NCMeleeDamageProfileMapPrototype>()
            .Where(profile => profile.DefaultForHumans)
            .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(defaults, Has.Length.EqualTo(1));
            Assert.That(defaults[0].Profiles.Keys.Select(key => key.Id),
                Is.EquivalentTo(new[] { "Blunt", "Slash", "Piercing", "Heat", "Shock" }));
            Assert.That(defaults[0].Profiles.Values.All(_prototypes.HasIndex), Is.True);
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void MixedMeleePayloadRoutesOnlyHeatLocallyAndRadiationSystemically()
    {
        var attacker = SSpawn("MobHuman");
        var target = SSpawn("MobHuman");
        var weapon = SSpawn(null);
        var damage = new DamageSpecifier(_prototypes.Index(Heat), 8);
        damage.DamageDict["Radiation"] = FixedPoint2.New(50);

        _melee.BeginAttack(attacker, weapon, target);
        Assert.That(_melee.TryApplyImpact(attacker, weapon, target, damage, Vector2.UnitX, out var feedback), Is.True);
        _melee.EndAttack(attacker, weapon);

        var wounds = SComp<NCWoundContainerComponent>(target).Wounds.Values.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(wounds, Has.Length.EqualTo(1));
            Assert.That(wounds[0].WoundType, Is.EqualTo(NCWoundType.Thermal));
            Assert.That(wounds[0].IntegrityDamage, Is.EqualTo(FixedPoint2.New(8)));
            Assert.That(feedback.GetTotal(), Is.EqualTo(FixedPoint2.New(8)));
            Assert.That(_systemic.GetBurden(target, NCSystemicChannel.Radiation), Is.EqualTo(50f));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void ElectricalMeleeContactCreatesLocalWoundAndSystemicRhythmBurden()
    {
        var attacker = SSpawn("MobHuman");
        var target = SSpawn("MobHuman");
        var weapon = SSpawn(null);
        var damage = new DamageSpecifier(_prototypes.Index(Shock), 6);

        _melee.BeginAttack(attacker, weapon, target);
        Assert.That(_melee.TryApplyImpact(attacker, weapon, target, damage, Vector2.UnitX, out _), Is.True);
        _melee.EndAttack(attacker, weapon);

        var wounds = SComp<NCWoundContainerComponent>(target).Wounds.Values.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(wounds, Has.Length.EqualTo(1));
            Assert.That(wounds[0].WoundType, Is.EqualTo(NCWoundType.Electrical));
            Assert.That(wounds[0].IntegrityDamage, Is.EqualTo(FixedPoint2.New(6)));
            Assert.That(_systemic.GetBurden(target, NCSystemicChannel.Electrical), Is.EqualTo(6f));
        });
    }
}
