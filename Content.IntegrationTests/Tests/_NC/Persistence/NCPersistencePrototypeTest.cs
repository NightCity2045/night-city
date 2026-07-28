using Content.IntegrationTests.Fixtures;
using Content.Shared._NC.CCVar;
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
            var positions = prototypes.EnumeratePrototypes<NCPositionPrototype>().ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(progression.GetLevel(0), Is.EqualTo(1));
                Assert.That(progression.GetLevel(100), Is.EqualTo(10));
                Assert.That(progression.GetTotalSkillPoints(3), Is.EqualTo(30));
                Assert.That(positions, Is.Not.Empty);
                Assert.That(positions.Select(position => position.PositionId).Distinct().Count(),
                    Is.EqualTo(positions.Length));
                Assert.That(positions.All(position =>
                    position.BaseSalary >= 0 && position.PayIntervalSeconds >= 0), Is.True);
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
