// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Shared.Access;
using Content.Shared.Access.Components;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC;

[TestFixture]
public sealed class NCAirlockAccessPrototypeTest : GameTest
{
    private static readonly (string Token, ProtoId<AccessLevelPrototype> Access)[] Cases =
    [
        ("NCGeneral", "NCGeneral"),
        ("NCPDPatrol", "NCPD_Patrol"),
        ("NCPDArmory", "NCPD_Armory"),
        ("NCPDInvestigations", "NCPD_Investigations"),
        ("NCPDCommand", "NCPD_Command"),
        ("TraumaMedical", "Trauma_Medical"),
        ("TraumaMorgue", "Trauma_Morgue"),
        ("TraumaHangar", "Trauma_Hangar"),
        ("TraumaCommand", "Trauma_Command"),
        ("TraumaOperative", "Trauma_Operative"),
        ("MedicalGeneral", "Medical_General"),
        ("MedicalMorgue", "Medical_Morgue"),
        ("MedicalCommand", "Medical_Command"),
        ("BiotechDirector", "Biotech_Director"),
        ("BiotechHeadOfDepartment", "Biotech_HeadOfDepartment"),
        ("BiotechBotany", "Biotech_Botany"),
        ("BiotechMedTech", "Biotech_MedTech"),
        ("BiotechTech", "Biotech_Tech"),
        ("BiotechOperative", "Biotech_Operative"),
        ("BiotechStaff", "Biotech_Staff"),
        ("BiotechGeneral", "Biotech_General"),
        ("MilitechCommand", "Militech_Command"),
        ("MilitechOperative", "Militech_Operative"),
        ("MilitechArmory", "Militech_Armory"),
        ("MilitechTech", "Militech_Tech"),
        ("MilitechMedical", "Militech_Medical"),
        ("MilitechStaff", "Militech_Staff"),
        ("MilitechGeneral", "Militech_General"),
        ("ZhirafaGeneral", "Zhirafa_General"),
        ("ZhirafaCommand", "Zhirafa_Command"),
    ];

    [SidedDependency(Side.Server)] private SharedContainerSystem _containers = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void EveryNightCityAccessHasConfiguredSolidAndGlassAirlocks()
    {
        // Keep this table exhaustive so a newly added NC access cannot silently ship without mapper-ready doors.
        var declared = SProtoMan.EnumeratePrototypes<AccessLevelPrototype>()
            .Select(prototype => prototype.ID)
            .Where(IsNightCityAccess);

        Assert.That(Cases.Select(entry => entry.Access.Id), Is.EquivalentTo(declared));

        foreach (var (token, access) in Cases)
        {
            AssertDoor($"NCAirlock{token}Locked", access);
            AssertDoor($"NCAirlock{token}GlassLocked", access);
        }
    }

    private void AssertDoor(string prototype, ProtoId<AccessLevelPrototype> expected)
    {
        var door = SSpawn(prototype);
        Assert.That(_containers.TryGetContainer(door, "board", out var boardContainer), Is.True,
            $"{prototype} has no electronics container.");
        Assert.That(boardContainer.ContainedEntities, Has.Count.EqualTo(1),
            $"{prototype} must contain exactly one electronics board.");

        // Access lives on the electronics so servicing or rebuilding the door preserves its requirement.
        var board = boardContainer.ContainedEntities.Single();
        var reader = SComp<AccessReaderComponent>(board);
        Assert.That(reader.AccessLists, Has.Count.EqualTo(1), $"{prototype} must require one access group.");
        Assert.That(reader.AccessLists.Single(), Is.EquivalentTo(new[] { expected }),
            $"{prototype} is wired to the wrong access.");
    }

    private static bool IsNightCityAccess(string id)
    {
        return id == "NCGeneral" ||
               id.StartsWith("NCPD_") ||
               id.StartsWith("Trauma_") ||
               id.StartsWith("Medical_") ||
               id.StartsWith("Biotech_") ||
               id.StartsWith("Militech_") ||
               id.StartsWith("Zhirafa_");
    }
}

