// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.IntegrationTests.Fixtures;
using Content.Shared._NC.CCVar;
using Content.Shared._NC.Legal;
using Content.Shared._NC.Organizations;
using Content.Shared._NC.RED.Progression;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;
using System.Linq;

namespace Content.IntegrationTests.Tests._NC.Persistence;

[TestFixture]
public sealed class NCPersistencePrototypeTest : GameTest
{
    [Test]
    public async Task ProgressionAndOrganizationsAreDataDrivenAndValid()
    {
        var server = Pair.Server;
        await server.WaitAssertion(() =>
        {
            var prototypes = server.ResolveDependency<IPrototypeManager>();
            var progression = prototypes.Index<NCRedProgressionPrototype>("NCDefaultProgression");
            var organizations = prototypes.EnumeratePrototypes<NCOrganizationPrototype>().ToArray();
            var departments = prototypes.EnumeratePrototypes<NCDepartmentPrototype>().ToArray();
            var positions = prototypes.EnumeratePrototypes<NCPositionPrototype>().ToArray();
            var licenses = prototypes.EnumeratePrototypes<NCLicensePrototype>().ToArray();
            var documents = prototypes.EnumeratePrototypes<NCDocumentPrototype>().ToArray();
            var requiredLegacyPositions = new[]
            {
                "NCPDOfficer", "NCPDSergeant", "NCPDLieutenant", "NCPDInspector",
                "NCPDCommander", "NCPDChief", "NCPDWatchAgent", "MaxTacEraser",
                "MaxTacCommander",
                "TraumaTeamChief", "TraumaTeamCoroner", "TraumaTeamDoctor", "TraumaTeamIntern",
                "TraumaTeamOperative", "TraumaTeamParamedic", "TraumaTeamPsych", "TraumaTeamTech",
                "MilitechChief", "MilitechCombatMedic", "MilitechNetrunner", "MilitechOperative",
                "MilitechOperativeLead", "MilitechQuartermaster", "MilitechRigger",
                "MilitechSecuritySpecialist", "MilitechTech",
                "BiotechnicaChief", "BiotechnicaBotanist", "BiotechnicaMedTech",
                "BiotechnicaNetrunner", "BiotechnicaOperativeLead", "BiotechnicaOperative",
                "BiotechnicaParamedic", "BiotechnicaRigger", "BiotechnicaTech",
                "ZhirafaEngineer", "ZhirafaJanitor",
            };

            Assert.Multiple(() =>
            {
                Assert.That(progression.GetLevel(0), Is.EqualTo(1));
                Assert.That(progression.GetLevel(100), Is.EqualTo(10));
                Assert.That(progression.GetTotalSkillPoints(3), Is.EqualTo(30));
                Assert.That(positions, Is.Not.Empty);
                Assert.That(positions.Select(position => position.PositionId).Distinct().Count(),
                    Is.EqualTo(positions.Length));
                Assert.That(organizations.Select(organization => organization.OrganizationId)
                        .Distinct().Count(),
                    Is.EqualTo(organizations.Length));
                Assert.That(departments.Select(department => department.DepartmentId)
                        .Distinct().Count(),
                    Is.EqualTo(departments.Length));
                Assert.That(positions.All(position =>
                    position.BaseSalary >= 0 && position.PayIntervalSeconds >= 0), Is.True);
                Assert.That(requiredLegacyPositions.All(id =>
                    prototypes.HasIndex<NCPositionPrototype>(id)), Is.True);
                Assert.That(licenses.Select(license => license.ID), Does.Contain("NCDriverLicense"));
                Assert.That(licenses.Select(license => license.ID), Does.Contain("NCFirearmsLicense"));
                Assert.That(documents.Select(document => document.ID),
                    Does.Contain("NCCitizenIdentityDocument"));
                Assert.That(documents.Select(document => document.ID),
                    Does.Contain("NCEmploymentCertificate"));
                Assert.That(prototypes.HasIndex<EntityPrototype>("NCATM"), Is.True);
            });
        });
    }

    [Test]
    public async Task PermadeathIsDisabledByDefault()
    {
        var server = Pair.Server;
        await server.WaitAssertion(() =>
        {
            var configuration = server.ResolveDependency<IConfigurationManager>();
            Assert.That(configuration.GetCVar(NCCVars.PermadeathEnabled), Is.False);
        });
    }
}
