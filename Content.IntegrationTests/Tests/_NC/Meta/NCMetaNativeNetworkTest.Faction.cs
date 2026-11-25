// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Shared._NC.Meta.Devices;
using Content.Shared._NC.Meta.Network;
using Content.Shared.UserInterface;
using Robust.Shared.GameObjects;

#nullable enable annotations

namespace Content.IntegrationTests.Tests._NC.Meta;

/// <summary>
/// A faction guards its base with its own network and programs, and a rival, who is another party, tries to get in. These are
/// the cross-owner checks: what a person who owns nothing of the base can and cannot do.
/// </summary>
public sealed partial class NCMetaNativeNetworkTest
{
    [Test]
    public async Task ARivalDeckEntersAFactionNetworkThroughItsPublicEntryButOwnsNothingOfIt()
    {
        var scene = await CreateScene(net: true, foreignNetwork: true);
        try
        {
            Assert.That(scene.NetworkOwner, Is.Not.EqualTo(scene.Owner), "The base and the deck must belong to two different parties.");
            var data = scene.Physical.Objects["data"];
            // The defender commits the security policy of their access point; then the attacker plugs in the deck.
            var host = scene.Physical.Objects["host"];
            await ActAsDefender(scene);
            await Server.WaitPost(() => MoveTo(scene.NetworkActor, host));
            await Server.WaitAssertion(() => Assert.That(_net.TrySynchronizeSecurityPolicy(host, scene.NetworkActor), Is.True));
            await ActAsAttacker(scene);
            await Until(() =>
            {
                MoveTo(scene.Actor, scene.Deck);
                if (_net.TryGetState(scene.Deck, scene.Actor, out var current) && current.SessionId != Guid.Empty) return true;
                return _net.TryConnect(scene.Deck, scene.Actor, host) &&
                    _net.TryGetState(scene.Deck, scene.Actor, out var state) && state.SessionId != Guid.Empty;
            }, maximumTicks: 900, reason: "The rival's deck could not enter the faction's access point.");
            var scanned = await Action(scene, NCMetaNetOperation.Scan);
            Assert.That(scanned.Nodes.Select(node => node.Id), Is.EquivalentTo(new[] { "entry", "data", "control", "firewall", "archive" }),
                "A rival's deck sees the architecture of the base it is connected to.");
            var probed = await Action(scene, NCMetaNetOperation.Probe, "data");
            Assert.That(probed.Nodes.Single(node => node.Id == "data").Accessible, Is.True, "An unprotected node of the base yields to a rival's probe.");

            await Server.WaitAssertion(() =>
            {
                MoveTo(scene.Actor, data);
                Assert.That(_devices.TryGetEntry(data, out var entry) && entry.Owner == scene.NetworkOwner, Is.True, "The base device belongs to the faction.");
                Assert.That(_devices.CanManage(data, scene.Actor), Is.False, "Entering a network does not make the rival a manager of its devices.");
                Assert.That(_devices.TryClaim(data, scene.Actor), Is.False, "A claimed device cannot be claimed again by someone else.");
            });
        }
        finally { await Destroy(scene); }
    }

    private static string FactionGateProgram(string owner) => $$$"""
        module native.faction;
        import meta.system; import meta.devices;
        link gate:devices.Gate="gate";
        async watch gate.useRequested(request){
            let ours:bool = request.subject.key == "{{{owner}}}";
            system.log("asked|" + request.subject.key);
            match(await gate.answer(request.requestId, ours)){ok(_)=>{system.log("answered");} err(error)=>{system.log("answer|" + error.message);}}
        }
        """;

    [Test]
    public async Task AFactionGateTurnsARivalAwayAndLetsItsOwnPeopleIn()
    {
        var scene = await CreateScene(net: true, foreignNetwork: true);
        try
        {
            var data = scene.Physical.Objects["data"];
            var ui = Server.System<ActivatableUISystem>();
            var userInterfaces = Server.System<SharedUserInterfaceSystem>();
            await ActAsDefender(scene);
            await Server.WaitPost(() =>
            {
                SEntMan.EnsureComponent<NCMetaGateComponent>(data);
                MoveTo(scene.NetworkActor, data);
            });
            var gate = await Programs.InstallAndStartAsync(data, scene.NetworkActor, "native.faction", "native.faction",
                FactionGateProgram(scene.NetworkOwner), new Dictionary<string, EntityUid> { ["gate"] = data });
            var key = default(System.Enum);

            // The person tries the device while their own character is the selected one, as in the game.
            async Task<bool> OpensFor(EntityUid person, System.Func<Task<string>> becomeThem)
            {
                await becomeThem();
                await Server.WaitPost(() =>
                {
                    MoveTo(person, data);
                    var activatable = SComp<ActivatableUIComponent>(data);
                    key = activatable.Key;
                    if (userInterfaces.IsUiOpen(data, key!, person)) userInterfaces.CloseUi(data, key!, person);
                    ui.InteractUI(person, data, activatable);
                });
                await Pair.RunTicksSync(60);
                var open = false;
                await Server.WaitPost(() => open = userInterfaces.IsUiOpen(data, key!, person));
                return open;
            }

            Assert.That(await OpensFor(scene.Actor, () => ActAsAttacker(scene)), Is.False, "The faction's program refuses a rival.");
            Assert.That(await OpensFor(scene.NetworkActor, () => ActAsDefender(scene)), Is.True, "The faction's own person is let in.");
            await ActAsDefender(scene);
            var asked = Logs(scene, "data", gate.Installation.Id).Where(line => line.StartsWith("asked|", System.StringComparison.Ordinal)).Distinct().ToArray();
            Assert.That(asked, Is.EquivalentTo(new[] { "asked|" + scene.NetworkOwner, "asked|" + scene.Owner }),
                "The program saw both people and told them apart by who they are.");
        }
        finally { await Destroy(scene); }
    }
}
