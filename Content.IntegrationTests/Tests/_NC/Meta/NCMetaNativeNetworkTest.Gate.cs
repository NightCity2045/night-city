// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Shared._NC.Meta.Devices;
using Content.Shared.UserInterface;
using Robust.Shared.GameObjects;

#nullable enable annotations

namespace Content.IntegrationTests.Tests._NC.Meta;

public sealed partial class NCMetaNativeNetworkTest
{
    private static string GateProgram(bool let) => $$$"""
        module native.gate;
        import meta.system; import meta.devices;
        link gate:devices.Gate="gate";
        async watch gate.useRequested(request){
            system.log("asked|"+request.subject.name);
            match(await gate.answer(request.requestId,{{{(let ? "true" : "false")}}})){ok(_)=>{system.log("answered");} err(error)=>{system.log("answer|"+error.message);}}
        }
        """;

    // A person who opens a device's interface is asked of its program first: with no program nothing changes, with one that
    // lets them in the interface opens once it has answered, and with one that does not it never opens.
    [TestCase("none")]
    [TestCase("allow")]
    [TestCase("deny")]
    public async Task AGatedDeviceOpensItsInterfaceOnlyWhenItsProgramAgrees(string program)
    {
        var scene = await CreateScene();
        try
        {
            var data = scene.Physical.Objects["data"];
            var ui = Server.System<ActivatableUISystem>();
            var userInterfaces = Server.System<SharedUserInterfaceSystem>();
            await Server.WaitPost(() =>
            {
                SEntMan.EnsureComponent<NCMetaGateComponent>(data);
                MoveTo(scene.NetworkActor, data);
            });
            Content.Server._NC.Meta.Runtime.NCMetaRuntimeInstance? installed = null;
            if (program != "none")
                installed = (await Programs.InstallAndStartAsync(data, scene.NetworkActor, "native.gate", "native.gate", GateProgram(program == "allow"),
                    new Dictionary<string, EntityUid> { ["gate"] = data })).Runtime;

            var opened = false;
            var key = default(Enum);
            await Server.WaitAssertion(() =>
            {
                var activatable = SComp<ActivatableUIComponent>(data);
                key = activatable.Key;
                // The scene may have left the interface open for this person; the test starts from a closed one.
                if (userInterfaces.IsUiOpen(data, key!, scene.NetworkActor)) userInterfaces.CloseUi(data, key!, scene.NetworkActor);
                // The attempt is the same one a click makes.
                opened = ui.InteractUI(scene.NetworkActor, data, activatable);
            });
            if (program == "none")
            {
                Assert.That(opened, Is.True, "A device with no listening program works as it always did.");
                await Server.WaitAssertion(() => Assert.That(userInterfaces.IsUiOpen(data, key!, scene.NetworkActor), Is.True));
                return;
            }
            Assert.That(opened, Is.False, "The attempt is held until the program answers.");
            await Until(() => Logs(scene, "data", installed!.Installation.Id).Any(line => line.StartsWith("asked|", System.StringComparison.Ordinal)),
                reason: "The program was never asked.");
            if (program == "allow")
                await Until(() => userInterfaces.IsUiOpen(data, key!, scene.NetworkActor), reason: "The program let the person in, but the interface did not open.");
            else
            {
                await Until(() => Logs(scene, "data", installed!.Installation.Id).Contains("answered"), reason: "The program never answered.");
                await Pair.RunTicksSync(10);
                await Server.WaitAssertion(() => Assert.That(userInterfaces.IsUiOpen(data, key!, scene.NetworkActor), Is.False,
                    "A program that turned the person away must keep the interface shut."));
            }
        }
        finally { await Destroy(scene); }
    }
}
