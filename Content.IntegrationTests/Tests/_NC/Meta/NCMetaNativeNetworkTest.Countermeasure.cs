// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Meta.Runtime;
using Content.Shared._NC.Meta.Network;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Temperature.Components;
using Robust.Shared.GameObjects;

#nullable enable annotations

namespace Content.IntegrationTests.Tests._NC.Meta;

public sealed partial class NCMetaNativeNetworkTest
{
    // A guard written by a player: it counts the messages it gets and, once a sender has hammered it three times,
    // answers that sender's device with a surge. The program also tries a stranger it never heard from.
    private const string Guard = """
        module native.guard;
        import meta.system; import meta.net; import meta.security;
        link port:net.MessagePort="port";
        link guard:security.Countermeasure="guard";
        var tries:int=0;
        async watch port.messageReceived(message){
            tries=tries+1;
            if (tries>=3) {
                match(await guard.surge(message.replyTo,5)){ok(_)=>{system.log("surged");} err(error)=>{system.log(error.message);}}
                match(await guard.surge("nobody-at-all",5)){ok(_)=>{system.log("stranger-surged");} err(error)=>{system.log("stranger|"+error.message);}}
            }
        }
        """;

    [Test]
    public async Task AGuardProgramSurgesTheDeviceThatKeepsHammeringItAndNoStranger()
    {
        var scene = await CreateScene();
        try
        {
            var data = scene.Physical.Objects["data"];
            var host = scene.Physical.Objects["host"];
            await Server.WaitPost(() => MoveTo(scene.NetworkActor, data));
            var guard = await Programs.InstallAndStartAsync(data, scene.NetworkActor, "native.guard", "native.guard", Guard,
                new Dictionary<string, EntityUid> { ["port"] = data, ["guard"] = data });
            string destination = "";
            float before = 0, heatBefore = 0;
            await Server.WaitPost(() =>
            {
                destination = Address(data);
                before = (float) Server.System<DamageableSystem>().GetTotalDamage((host, SComp<DamageableComponent>(host)));
                heatBefore = SEntMan.TryGetComponent(host, out TemperatureComponent? warm) ? warm.Temperature : 0;
            });
            string one = $"match(await port.send(\"{destination}\",\"safe\",\"x\")){{ok(_)=>{{}} err(error)=>{{system.log(error.message);}}}}";
            await Program(scene, "host", Send(destination, "x", one + one));
            await Until(() => Logs(scene, "data", guard.Installation.Id) is var seen && seen.Contains("surged") && seen.Any(line => line.StartsWith("stranger", System.StringComparison.Ordinal)), maximumTicks: 900,
                reason: "The guard never reported a surge: " + string.Join(";", Logs(scene, "data", guard.Installation.Id)));
            await Server.WaitAssertion(() =>
            {
                var logs = Logs(scene, "data", guard.Installation.Id);
                var after = (float) Server.System<DamageableSystem>().GetTotalDamage((host, SComp<DamageableComponent>(host)));
                Assert.That(after, Is.GreaterThan(before), "A surge must damage the device that hammered the guard.");
                if (SEntMan.TryGetComponent(host, out TemperatureComponent? hot)) Assert.That(hot.Temperature, Is.GreaterThan(heatBefore));
                Assert.That(logs, Does.Not.Contain("stranger-surged"), "A device that never contacted the guard cannot be struck.");
                Assert.That(logs.Any(line => line.StartsWith("stranger|", System.StringComparison.Ordinal)), Is.True);
            });
            await Server.WaitPost(() => Server.System<DamageableSystem>().ClearAllDamage((host, SComp<DamageableComponent>(host))));
            await Server.WaitPost(() => { _runtime.StopHost(data); _runtime.StopHost(host); });
        }
        finally { await Destroy(scene); }
    }
}
