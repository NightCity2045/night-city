// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Shared.Access.Components;
using Content.Shared.PDA;
using Robust.Shared.GameObjects;
using Robust.UnitTesting.Pool;

namespace Content.IntegrationTests.Tests._NC.Agent;

/// <summary>The Agent and its VIN card are named by their prototypes and localization, not by code at map init.</summary>
[TestFixture]
public sealed class NCAgentNamesTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = false };

    [Test]
    public async Task TheAgentAndItsVinCardCarryTheirNamesFromTheirPrototypes()
    {
        var map = await Pair.CreateTestMap();
        var agent = EntityUid.Invalid;
        await Server.WaitPost(() => agent = SEntMan.SpawnEntity("NCAgent", map.GridCoords));
        await Pair.RunTicksSync(2);
        await Server.WaitAssertion(() =>
        {
            Assert.That(SComp<MetaDataComponent>(agent).EntityName, Is.EqualTo("Agent"));
            Assert.That(SComp<PdaComponent>(agent).ContainedId, Is.Not.Null, "The Agent comes with its VIN card in the card slot.");
            var card = SComp<PdaComponent>(agent).ContainedId!.Value;
            Assert.That(SEntMan.HasComponent<IdCardComponent>(card), Is.True);
            Assert.That(SComp<MetaDataComponent>(card).EntityName, Is.EqualTo("VIN card"));
        });
    }
}
