// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Meta.Runtime;
using Content.Shared._NC.Meta.Network;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._NC.Meta;

public sealed partial class NCMetaNativeNetworkTest
{
    private const string ReferenceProgram = """
        module native.refs; import meta.system; import meta.netrun;
        link session:netrun.NetSession="session";
        var first:option<netrun.NetNodeRef> = none;
        pub async fn scan()->void {
            match(await session.listVisibleNodes()){
                ok(nodes)=>{if(nodes.length>0){first=some(nodes[0].reference);system.log("scanned");}}
                err(error)=>{system.log(error.message);}
            }
        }
        pub fn saved()->option<netrun.NetNodeRef> {return first;}
        pub async fn probe(node:netrun.NetNodeRef)->void {
            match(await session.probe(node)){ok(_)=>{system.log("probed");}err(error)=>{system.log(error.message);}}
        }
        """;

    private async Task<MetaValue?> Invoke(Scene scene, NCMetaNativeNetworkProgramHarness.Installed program,
        string function, Func<MetaProcess, IReadOnlyList<MetaValue>>? arguments = null)
    {
        var id = Enumerable.Range(0, program.Artifact.FunctionCount)
            .Single(index => program.Artifact.GetFunction(index).Name == "native.refs." + function);
        var complete = false;
        MetaValue? result = null;
        await Until(() => program.Runtime.Process.State == MetaProcessState.Idle);
        await Server.WaitAssertion(() =>
        {
            MoveTo(scene.Actor, scene.Deck);
            Assert.That(_runtime.TryInvokeAuthorized(scene.Deck, program.Runtime.Generation, scene.Actor, id,
                arguments ?? (_ => []), (_, value) => { result = value; complete = true; },
                () => _devices.CanManage(scene.Deck, scene.Actor)), Is.True);
        });
        await Until(() => complete, maximumTicks: 900, reason: "The actual native helper invocation did not complete.");
        return result;
    }

    [Test]
    public async Task OpaqueNetReferencesCannotCrossRuntimeGenerationOrSurviveNodeAndSessionRevocation()
    {
        var scene = await CreateScene(net: true);
        try
        {
            await Connect(scene);
            await Server.WaitPost(() => MoveTo(scene.Actor, scene.Deck));
            var a = await Programs.InstallAndStartAsync(scene.Deck, scene.Actor, "native.refs", "native.refs.a", ReferenceProgram,
                new Dictionary<string, EntityUid> { ["session"] = scene.Deck }, retainForInvocations: true);
            var b = await Programs.InstallAndStartAsync(scene.Deck, scene.Actor, "native.refs", "native.refs.b", ReferenceProgram,
                new Dictionary<string, EntityUid> { ["session"] = scene.Deck }, retainForInvocations: true);
            await Invoke(scene, a, "scan");
            var saved = await Invoke(scene, a, "saved");
            MetaValue reference = default;
            ulong token = 0;
            await Server.WaitAssertion(() =>
            {
                Assert.That(saved, Is.Not.Null);
                Assert.That(a.Runtime.Process.GetVariantTag(saved!.Value), Is.EqualTo(1));
                reference = a.Runtime.Process.GetVariantPayload(saved.Value);
                Assert.That(a.Runtime.Process.TryGetOpaqueToken(reference, out token), Is.True);
            });
            // An intentionally hostile trusted-host injection rewraps an existing native token in a different VM.
            // Guest META cannot construct opaque values at all. The native service must still check its issuing generation.
            await Invoke(scene, b, "probe", process => [process.CreateOpaque(process.GetTypeId("meta.netrun.NetNodeRef"), token)]);
            await Server.WaitAssertion(() =>
            {
                var logs = _runtime.GetLogs(scene.Deck, scene.Actor, b.Installation.Id);
                Assert.That(logs, Has.None.EqualTo("probed"));
                Assert.That(logs, Is.Not.Empty);
                Assert.That(_net.TryGetState(scene.Deck, scene.Actor, out var state), Is.True);
                Assert.That(state.ActionPending, Is.False);
                Assert.That(_devices.TryInvalidate(scene.Physical.Objects["host"]), Is.True);
                Assert.That(a.Runtime.Process.TryGetOpaqueToken(reference, out _), Is.False,
                    "Disconnect revokes the issuing process's opaque root immediately.");
            });
            Assert.That((await ReadState(scene)).SessionId, Is.EqualTo(Guid.Empty));
            await Server.WaitAssertion(() =>
            {
                var id = Enumerable.Range(0, a.Artifact.FunctionCount).Single(index => a.Artifact.GetFunction(index).Name == "native.refs.probe");
                Assert.That(_runtime.TryInvokeAuthorized(scene.Deck, a.Runtime.Generation, scene.Actor, id,
                    _ => [reference], (_, _) => { }, () => _devices.CanManage(scene.Deck, scene.Actor)), Is.True);
            });
            await Until(() => a.Runtime.Process.State is MetaProcessState.Faulted or MetaProcessState.Stopped or MetaProcessState.Quarantined,
                reason: "A revoked host argument must be rejected before native execution.");
            await Server.WaitAssertion(() =>
            {
                Assert.That(_runtime.GetLogs(scene.Deck, scene.Actor, a.Installation.Id), Has.None.EqualTo("probed"),
                    "A revoked VM-local handle cannot authorize a native operation.");
                Assert.That(_net.TryGetState(scene.Deck, scene.Actor, out var state), Is.True);
                Assert.That(state.SessionId, Is.EqualTo(Guid.Empty));
                Assert.That(state.ActionPending, Is.False);
            });
        }
        finally { await Destroy(scene); }
    }
}
