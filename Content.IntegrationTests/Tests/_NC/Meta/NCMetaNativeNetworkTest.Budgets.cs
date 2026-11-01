// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Numerics;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Shared._NC.Meta.Network;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._NC.Meta;

public sealed partial class NCMetaNativeNetworkTest
{
    [TestPrototypes]
    private const string SharedCooldownTool = """
        - type: ncMetaNetTool
          id: NCMetaIntegrationSharedCooldown
          rating: 2
          durationMilliseconds: 100
          cooldownMilliseconds: 5000
          cost: 1
          budget: 10
        """;

    [Test]
    public async Task SwitchingPhysicalDecksDoesNotBypassTheSameCharactersActionCooldown()
    {
        var scene = await CreateScene(net: true);
        try
        {
            EntityUid secondDeck = default;
            await Server.WaitPost(() => secondDeck = SEntMan.SpawnEntity("NCMetaCyberdeck",
                new EntityCoordinates(scene.Physical.Grid, new Vector2(0.5f, -0.5f))));
            await Claim(secondDeck, scene.Actor, scene.Owner);
            await Server.WaitAssertion(() =>
            {
                MoveTo(scene.Actor, scene.Deck);
                var cable = SEntMan.SpawnEntity("NCMetaNetworkCable", SComp<TransformComponent>(scene.Actor).Coordinates);
                Assert.That(_network.TryConnectCable(cable, scene.Actor, scene.Deck), Is.True);
                MoveTo(scene.Actor, secondDeck);
                Assert.That(_network.TryConnectCable(cable, scene.Actor, secondDeck), Is.True);
                SComp<NCMetaCyberdeckComponent>(scene.Deck).ToolProfile = "NCMetaIntegrationSharedCooldown";
                SComp<NCMetaCyberdeckComponent>(secondDeck).ToolProfile = "NCMetaIntegrationSharedCooldown";
            });
            await Connect(scene);
            await Until(() =>
            {
                MoveTo(scene.Actor, secondDeck);
                return _net.TryConnect(secondDeck, scene.Actor, scene.Physical.Objects["host"]);
            });
            var completed = await Action(scene, NCMetaNetOperation.Scan);
            Assert.That(completed.ActionPending, Is.False);
            await Server.WaitAssertion(() =>
            {
                MoveTo(scene.Actor, secondDeck);
                Assert.That(_net.TryGetState(secondDeck, scene.Actor, out var before), Is.True);
                Assert.That(before.SessionId, Is.Not.EqualTo(Guid.Empty));
                Assert.That(_net.TryExecuteAction(secondDeck, scene.Actor, NCMetaNetOperation.Scan,
                    before.SessionId, before.Revision), Is.False, "A second deck must retain the actor's existing cooldown.");
                Assert.That(_net.TryGetState(secondDeck, scene.Actor, out var after), Is.True);
                Assert.That(after.Budget, Is.EqualTo(before.Budget));
                Assert.That(after.ActionPending, Is.False);
            });
            var admitted = false;
            await Until(() =>
            {
                MoveTo(scene.Actor, secondDeck);
                return admitted = _net.TryGetState(secondDeck, scene.Actor, out var state) &&
                    _net.TryExecuteAction(secondDeck, scene.Actor, NCMetaNetOperation.Scan, state.SessionId, state.Revision);
            }, maximumTicks: 600, reason: "The real actor cooldown did not expire on active-world time.");
            Assert.That(admitted, Is.True);
        }
        finally { await Destroy(scene); }
    }
}
