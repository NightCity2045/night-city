// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Shared.Inventory;
using Content.Shared.Roles;
using Content.Shared.Station;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Combat;

/// <summary>
/// Protects the fixed Night City department loadouts from missing essentials and accidental weapon grants.
/// </summary>
[TestFixture]
public sealed class NCDepartmentStartingGearTest : GameTest
{
    [SidedDependency(Side.Server)] private InventorySystem _inventory = default!;
    [SidedDependency(Side.Server)] private SharedStationSpawningSystem _station = default!;

    private static readonly string[] Jobs =
    [
        "NCPDChief",
        "NCPDCommander",
        "NCPDLieutenant",
        "NCPDInspector",
        "NCPDSergeant",
        "NCPDOfficer",
        "NCPDCadet",
        "TraumaTeamOperative",
        "TraumaTeamParamedic",
        "NCMedicalChief",
        "NCMedicalCoroner",
        "NCMedicalPsychologist",
        "NCMedicalDoctor",
        "NCMedicalTechnician",
        "NCMedicalIntern",
        "BiotechnicaChief",
        "BiotechnicaOperativeLead",
        "BiotechnicaOperative",
        "BiotechnicaNetrunner",
        "BiotechnicaRigger",
        "BiotechnicaMedTech",
        "BiotechnicaParamedic",
        "BiotechnicaTech",
        "BiotechnicaBotanist",
        "MilitechChief",
        "MilitechOperativeLead",
        "MilitechSecuritySpecialist",
        "MilitechOperative",
        "MilitechNetrunner",
        "MilitechRigger",
        "MilitechCombatMedic",
        "MilitechTech",
        "MilitechQuartermaster",
        "ZhirafaJanitor",
    ];

    private static readonly string[] RequiredSlots = ["jumpsuit", "back", "shoes", "id"];

    private static readonly HashSet<string> ForbiddenLoadoutEntities =
    [
        "ClothingBeltSecurityFilled",
        "ClothingBeltSecurityWebbingFilled",
        "ClothingBeltHolsterFilled",
        "Stunbaton",
    ];

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void EveryNightCityJobHasACompleteWeaponFreeStartingGear()
    {
        foreach (var jobId in Jobs)
        {
            var job = SProtoMan.Index<JobPrototype>(jobId);
            Assert.That(job.StartingGear.HasValue, Is.True, $"{jobId} has no fixed starting gear.");

            var gear = SProtoMan.Index<StartingGearPrototype>(job.StartingGear!.Value);

            foreach (var slot in RequiredSlots)
            {
                Assert.That(gear.Equipment.ContainsKey(slot), Is.True,
                    $"{jobId} is missing required equipment slot '{slot}'.");
            }

            foreach (var (slot, prototype) in gear.Equipment)
                AssertAllowedAndResolvable(jobId, $"equipment slot '{slot}'", prototype);

            foreach (var prototype in gear.Inhand)
                AssertAllowedAndResolvable(jobId, "in-hand equipment", prototype);

            foreach (var (slot, prototypes) in gear.Storage)
            {
                foreach (var prototype in prototypes)
                    AssertAllowedAndResolvable(jobId, $"storage in '{slot}'", prototype);
            }
        }
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void EveryNightCityStartingGearFitsTheHumanInventory()
    {
        foreach (var jobId in Jobs)
        {
            var job = SProtoMan.Index<JobPrototype>(jobId);
            var gear = SProtoMan.Index<StartingGearPrototype>(job.StartingGear!.Value);
            var human = SSpawn("MobHuman");

            _station.EquipStartingGear(human, gear, raiseEvent: false);

            foreach (var slot in gear.Equipment.Keys)
            {
                Assert.That(_inventory.TryGetSlotEntity(human, slot, out _), Is.True,
                    $"{jobId} could not equip its declared '{slot}' item.");
            }

            SEntMan.DeleteEntity(human);
        }
    }

    private void AssertAllowedAndResolvable(string jobId, string location, EntProtoId prototype)
    {
        Assert.Multiple(() =>
        {
            Assert.That(SProtoMan.HasIndex<EntityPrototype>(prototype), Is.True,
                $"{jobId} references missing prototype '{prototype.Id}' in {location}.");
            Assert.That(IsForbiddenWeapon(prototype.Id), Is.False,
                $"{jobId} receives forbidden weapon entity '{prototype.Id}' in {location}.");
        });
    }

    // Names cover direct weapons while the explicit set catches containers that hide weapons in their fill.
    private static bool IsForbiddenWeapon(string prototype)
    {
        return ForbiddenLoadoutEntities.Contains(prototype)
               || prototype.StartsWith("Weapon", StringComparison.Ordinal)
               || prototype.Contains("Stunbaton", StringComparison.OrdinalIgnoreCase)
               || prototype.Contains("CombatKnife", StringComparison.OrdinalIgnoreCase)
               || prototype.Contains("Machete", StringComparison.OrdinalIgnoreCase)
               || prototype.Contains("Katana", StringComparison.OrdinalIgnoreCase);
    }
}
