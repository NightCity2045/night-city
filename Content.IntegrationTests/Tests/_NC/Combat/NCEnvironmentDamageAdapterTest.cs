// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Combat.Environment;
using Content.Shared._NC.Combat.Damage;
using Content.Shared._NC.Combat.Environment;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Systemic;
using Content.Shared._NC.Medical.Trauma;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Combat;

[TestFixture]
[TestOf(typeof(NCEnvironmentDamageAdapterSystem))]
public sealed class NCEnvironmentDamageAdapterTest : GameTest
{
    [SidedDependency(Side.Server)] private IPrototypeManager _serverPrototypes = default!;
    [SidedDependency(Side.Server)] private DamageableSystem _serverDamageable = default!;
    [SidedDependency(Side.Server)] private NCEnvironmentDamageAdapterSystem _adapter = default!;
    [SidedDependency(Side.Server)] private Content.Server._NC.Medical.Systemic.NCSystemicSystem _systemic = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void MappedFallbackSourcesCreateExactlyOneLocalWoundAndNeverChangeLegacyHp()
    {
        var source = SSpawn(null);
        var cases = new[]
        {
            (DamageType: "Blunt", Wound: "NCWoundBlunt", Type: NCWoundType.Blunt),
            (DamageType: "Heat", Wound: "NCWoundThermal", Type: NCWoundType.Thermal),
            (DamageType: "Cold", Wound: "NCWoundCold", Type: NCWoundType.Cold),
            (DamageType: "Shock", Wound: "NCWoundElectrical", Type: NCWoundType.Electrical),
            (DamageType: "NCExplosive", Wound: "NCWoundBlast", Type: NCWoundType.Blast),
            (DamageType: "NCPressure", Wound: "NCWoundPressure", Type: NCWoundType.Pressure),
        };

        foreach (var testCase in cases)
        {
            var body = SSpawn("MobHuman");
            var damage = MakeDamage(_serverPrototypes, testCase.DamageType, 10);
            var legacyBefore = GetServerLegacyDamage(body);

            // Cancellation is the successful fallback result: medicine receives the impact while Damageable does not.
            Assert.That(_serverDamageable.TryChangeDamage(body, damage, origin: source), Is.False);

            var wounds = SComp<NCWoundContainerComponent>(body).Wounds.Values.ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(wounds, Has.Length.EqualTo(1), testCase.DamageType);
                Assert.That(wounds[0].Prototype.Id, Is.EqualTo(testCase.Wound), testCase.DamageType);
                Assert.That(wounds[0].WoundType, Is.EqualTo(testCase.Type), testCase.DamageType);
                Assert.That(wounds[0].ForensicResolutionId, Is.Not.EqualTo(Guid.Empty), testCase.DamageType);
                Assert.That(GetServerLegacyDamage(body), Is.EqualTo(legacyBefore), testCase.DamageType);
            });
        }
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void NegativePhysicalHealingIsRejectedAndCellularDamageBecomesSystemic()
    {
        var negativeTarget = SSpawn("MobHuman");
        var presetLegacyDamage = MakeDamage(_serverPrototypes, "Blunt", 5);
        _serverDamageable.SetDamage(negativeTarget, presetLegacyDamage);

        Assert.That(_serverDamageable.TryChangeDamage(
            negativeTarget,
            MakeDamage(_serverPrototypes, "Blunt", -3)), Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(GetServerLegacyDamage(negativeTarget), Is.EqualTo(FixedPoint2.New(5)));
            Assert.That(WoundCount(negativeTarget), Is.Zero);
        });

        var systemicTarget = SSpawn("MobHuman");
        Assert.That(_adapter.TryResolveDamageProfile(
            MakeDamage(_serverPrototypes, "Cellular", 7),
            out _), Is.False);
        Assert.That(_adapter.TryResolveSystemicProfile("Cellular", out var systemicProfile), Is.True);
        Assert.That(systemicProfile.Id, Is.EqualTo("NCCellularSystemicDamage"));
        Assert.That(_serverDamageable.TryChangeDamage(
            systemicTarget,
            MakeDamage(_serverPrototypes, "Cellular", 7)), Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(GetServerLegacyDamage(systemicTarget), Is.EqualTo(FixedPoint2.Zero));
            Assert.That(WoundCount(systemicTarget), Is.Zero);
            Assert.That(_systemic.GetBurden(systemicTarget, NCSystemicChannel.Cellular), Is.EqualTo(7f));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void GodmodeCancelsBeforeMedicalFallbackAndNonNcEntitiesRemainVanilla()
    {
        var protectedBody = SSpawn("MobHuman");
        SEntMan.EnsureComponent<GodmodeComponent>(protectedBody);
        Assert.That(_serverDamageable.TryChangeDamage(
            protectedBody,
            MakeDamage(_serverPrototypes, "Blunt", 8)), Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(WoundCount(protectedBody), Is.Zero);
            Assert.That(GetServerLegacyDamage(protectedBody), Is.EqualTo(FixedPoint2.Zero));
        });

        var nonNc = SSpawn("MobMonkey");
        Assert.That(SEntMan.HasComponent<NCMedicalBodyComponent>(nonNc), Is.False);
        var before = GetServerLegacyDamage(nonNc);
        Assert.That(_serverDamageable.TryChangeDamage(
            nonNc,
            MakeDamage(_serverPrototypes, "Blunt", 8),
            ignoreResistances: true), Is.True);
        Assert.That(GetServerLegacyDamage(nonNc), Is.GreaterThan(before));
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void DefaultMapIsCompleteDeterministicAndPressureHasAResolvableWound()
    {
        var defaults = _serverPrototypes.EnumeratePrototypes<NCEnvironmentDamageProfileMapPrototype>()
            .Where(candidate => candidate.DefaultForHumans)
            .ToArray();
        Assert.That(defaults, Has.Length.EqualTo(1));
        Assert.That(defaults[0].Profiles.Keys.Select(key => key.Id), Is.EquivalentTo(new[]
        {
            "Blunt",
            "Slash",
            "Piercing",
            "Heat",
            "Cold",
            "Shock",
            "Caustic",
            "NCExplosive",
            "NCPressure",
        }));
        Assert.That(defaults[0].SystemicProfiles.Keys.Select(key => key.Id), Is.EquivalentTo(new[]
        {
            "Asphyxiation",
            "Poison",
            "Radiation",
            "Cellular",
            "Shock",
        }));
        Assert.That(defaults[0].Profiles.Values.All(_serverPrototypes.HasIndex), Is.True);

        var tied = MakeDamage(_serverPrototypes, "Heat", 6);
        tied.DamageDict["Blunt"] = FixedPoint2.New(6);
        Assert.That(_adapter.TryResolveDamageProfile(tied, out var selected), Is.True);
        Assert.That(selected.Id, Is.EqualTo("NCEnvironmentBluntStandard"));

        ProtoId<NCWoundSystemProfilePrototype> humanWoundsId = "NCHumanWoundSystem";
        ProtoId<NCWoundPrototype> pressureWoundId = "NCWoundPressure";
        var humanWounds = _serverPrototypes.Index(humanWoundsId);
        Assert.Multiple(() =>
        {
            Assert.That(humanWounds.Wounds[NCTraumaMechanism.Cold].Id, Is.EqualTo("NCWoundCold"));
            Assert.That(humanWounds.Wounds[NCTraumaMechanism.Explosive].Id, Is.EqualTo("NCWoundBlast"));
            Assert.That(humanWounds.Wounds[NCTraumaMechanism.Pressure].Id, Is.EqualTo("NCWoundPressure"));
            Assert.That(_serverPrototypes.HasIndex(pressureWoundId), Is.True);
        });
    }

    private static DamageSpecifier MakeDamage(IPrototypeManager prototypes, string damageType, int amount)
    {
        return new DamageSpecifier(
            prototypes.Index<DamageTypePrototype>(damageType),
            FixedPoint2.New(amount));
    }

    private int WoundCount(EntityUid body)
    {
        return STryComp<NCWoundContainerComponent>(body, out var wounds)
            ? wounds.Wounds.Count
            : 0;
    }

    private FixedPoint2 GetServerLegacyDamage(EntityUid body)
    {
#pragma warning disable CS0618 // Compatibility HP is observed only to prove the NC boundary never mutates it.
        return _serverDamageable.GetTotalDamage(body);
#pragma warning restore CS0618
    }

}
