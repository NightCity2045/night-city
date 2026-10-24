// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Content.Client._NC.Meta.Mui;
using Content.IntegrationTests.Fixtures;
using Content.Meta.Language;
using Content.Meta.Mui;
using Content.Meta.Runtime;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Meta.Applications;
using Content.Server._NC.Meta.Demo;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.Meta.Network;
using Content.Shared._NC.Meta.UI;
using Content.Shared.Mind;
using Content.Shared.UserInterface;
using Robust.Client.UserInterface;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._NC.Meta;

[TestFixture, NonParallelizable]
public sealed class NCMetaNativeAppTransportTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = true };
    private Func<string> _serverDiagnostics = null!;

    private async Task Until(Func<bool> check, int maximumTicks = 900, [CallerArgumentExpression(nameof(check))] string condition = "")
    {
        for (var tick = 0; tick < maximumTicks; tick++)
        {
            var ready = false;
            await Server.WaitPost(() => ready = check());
            if (ready) return;
            await Pair.RunTicksSync(1);
        }
        var diagnostics = "";
        if (_serverDiagnostics != null) await Server.WaitPost(() => diagnostics = _serverDiagnostics());
        Assert.Fail("The real connected native application did not reach its required boundary: " + condition + "\n" + diagnostics);
    }

    private async Task SendThroughReadyFrontend(Guid session)
    {
        for (var tick = 0; tick < 900; tick++)
        {
            var sent = false;
            await Client.WaitPost(() =>
            {
                var pending = new Queue<Control>();
                pending.Enqueue(Client.ResolveDependency<IUserInterfaceManager>().RootControl);
                for (var work = 0; work < 8192 && pending.TryDequeue(out var control); work++)
                {
                    if (control.Disposed) continue;
                    if (control is NCMetaMuiRenderer renderer && renderer.SessionId == session && renderer.Revision >= 0)
                    {
                        sent = renderer.TryRequestAction("send", "{}");
                        return;
                    }
                    foreach (var child in control.Children) if (pending.Count < 8192) pending.Enqueue(child);
                }
            });
            if (sent) return;
            await Pair.RunTicksSync(1);
        }
        Assert.Fail("The real native renderer did not receive its first snapshot and submit the declared action through BUI.");
    }

    [TestCase("CloseUi")]
    [TestCase("DetachMind")]
    public async Task AnActuallyConnectedApplicationCannotDeliverAnAlreadyAcceptedPacketAfterItsInvokingSessionCloses(string transition)
    {
        var scenes = Server.System<NCMetaDemoSceneSystem>();
        var devices = Server.System<NCMetaDeviceRegistrySystem>();
        var runtime = Server.System<NCMetaRuntimeSystem>();
        var apps = Server.System<NCMetaApplicationSystem>();
        var observer = Server.System<NCMetaNativePacketObserverSystem>();
        var programs = new NCMetaNativeNetworkProgramHarness(Server);
        NCMetaDemoSceneInstance scene = null!;
        EntityUid mind = default;
        string owner = "";
        void Move(EntityUid target) => Server.System<SharedTransformSystem>().SetCoordinates(scene.Operator,
            SComp<TransformComponent>(target).Coordinates.Offset(new Vector2(0, -0.35f)));
        try
        {
            await Server.WaitAssertion(() =>
            {
                Assert.That(ServerSession, Is.Not.Null);
                Assert.That(ServerSession!.Status, Is.EqualTo(SessionStatus.InGame));
                SEntMan.EventBus.RaiseEvent(EventSource.Local,
                    new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound));
                Assert.That(scenes.TryCreate("NCMetaNetScene", out scene), Is.True);
                foreach (var key in new[] { "deck", "door", "firewall", "blackIce" }) SEntMan.DeleteEntity(scene.Objects[key]);
                mind = Server.System<SharedMindSystem>().CreateMind(ServerSession.UserId);
                Server.System<SharedMindSystem>().TransferTo(mind, scene.Operator);
                Assert.That(ServerSession.AttachedEntity, Is.EqualTo(scene.Operator));
                Assert.That(Server.System<NCEntityPartyResolverSystem>().TryResolve(scene.Operator, out var party), Is.True);
                owner = party.ToString();
            });
            await Until(() => Server.System<NCActiveWorldTimeSystem>().IsReady);
            foreach (var key in new[] { "host", "data", "control" })
            {
                var endpoint = scene.Objects[key];
                await Until(() => devices.TryGetEntry(endpoint, out var device) && device.Ready && devices.IsPowered(endpoint));
                await Server.WaitAssertion(() => { Move(endpoint); Assert.That(devices.TryClaim(endpoint, scene.Operator), Is.True); });
                await Until(() => devices.TryGetEntry(endpoint, out var device) && device.Owner == owner && device.AuthorityEpoch >= 2);
            }
            var sourceHost = scene.Objects["host"];
            var receiverHost = scene.Objects["data"];
            await Server.WaitPost(() => Move(receiverHost));
            var receiver = await programs.InstallAndStartAsync(receiverHost, scene.Operator, "native.receiver", "native.receiver", """
                module native.receiver; import meta.net; import meta.system;
                link port:net.MessagePort="port";
                watch port.messageReceived(message){system.log(message.payload);}
                """, new Dictionary<string, EntityUid> { ["port"] = receiverHost });
            var destination = "";
            await Server.WaitPost(() =>
            {
                Move(sourceHost); destination = SComp<NCMetaMessagePortComponent>(receiverHost).Address;
                SComp<NCMetaMessagePortComponent>(sourceHost).LatencyMilliseconds = 500;
                observer.Clear(); observer.Observe(receiverHost);
            });
            var source = $$"""
                module native.application; import meta.app; import meta.net; import meta.system;
                link port:net.MessagePort="port";
                pub struct State { label:string; }
                pub struct Input {}
                pub fn view(context:app.AppContext)->State {return new State(label:"Send");}
                pub async fn send(context:app.AppContext,input:Input)->result<void> {
                    match(await port.send("{{destination}}","safe","from-real-app")){
                        ok(_)=>{system.log("queued");return ok(());}
                        err(problem)=>{return err(problem);}
                    }
                }
                """;
            var sender = await programs.InstallAndStartAsync(sourceHost, scene.Operator, "native.application", "native.application", source,
                new Dictionary<string, EntityUid> { ["port"] = sourceHost }, retainForInvocations: true);
            _serverDiagnostics = () => $"sender={sender.Runtime.Process.State}; fault={sender.Runtime.Process.Fault}; invocation={sender.Runtime.InvocationActor}; " +
                $"tick={runtime.LastTick}; logs={string.Join(" | ", runtime.GetLogs(sourceHost, scene.Operator, sender.Installation.Id).TakeLast(8))}";
            var compiler = new MetaCompiler();
            var project = await NCMetaNativeTestTasks.Run(Server, () => Server.System<NCMetaPersistenceSystem>()
                .ReadProjectAsync(sender.Installation.ProjectId, owner));
            Assert.That(project, Is.Not.Null);
            var sourcePath = project!.Files.Single(file => file.Path.EndsWith(".meta", StringComparison.Ordinal)).Path;
            var request = new MetaCompilationRequest([new(sourcePath, source)], "native.application", "native.application");
            const string contract = """
                {"version":"1.0","views":{"main":{"function":"native.application.view","document":"main.mui",
                "stateType":"native.application.State","fields":{"label":{"type":"string","maxLength":32}},"actions":["send"]}},
                "actions":{"send":{"function":"native.application.send","inputType":"native.application.Input","inputs":{},"scopes":["net.send"]}},"resources":[]}
                """;
            var documents = new Dictionary<string, string> { ["main.mui"] = """
                <Page id="main" title="{bind label}"><Column id="layout"><Button id="send" action="send" text="{bind label}"/></Column></Page>
                """ };
            var digest = MuiVerifiedApplication.ComputePackageDigest(sender.Artifact.Digest, contract, documents, "");
            Assert.That(MuiVerifiedApplication.TryAdmit(sender.Artifact, compiler.VerifyExports(request, sender.Artifact), contract,
                documents, "", digest, out var app, out var error), Is.True, error);
            Guid session = default;
            await Server.WaitAssertion(() =>
            {
                Move(sourceHost);
                Assert.That(apps.TryRegister(sourceHost, scene.Operator, app!, NCMetaAppPolicy.OwnerOnly with
                    { GrantedScopes = sender.Admission.GetGrantedScopes().ToImmutableHashSet(StringComparer.Ordinal) }), Is.True);
                Assert.That(apps.TryOpen(sourceHost, scene.Operator, sourceHost, "main", NCMetaAppPresentation.Desktop,
                    NCMetaUiKey.Application, () => devices.CanManage(sourceHost, scene.Operator), out session), Is.True);
            });
            await SendThroughReadyFrontend(session);
            await Until(() => runtime.GetLogs(sourceHost, scene.Operator, sender.Installation.Id).Contains("queued") &&
                sender.Runtime.Process.State == MetaProcessState.Idle && sender.Runtime.InvocationActor == null);
            await Server.WaitAssertion(() =>
            {
                Assert.That(observer.Packets, Is.Empty, "The native transport latency separates completed action from delivery.");
                Assert.That(ServerSession!.AttachedEntity, Is.EqualTo(scene.Operator));
                if (transition == "CloseUi") Server.System<SharedUserInterfaceSystem>().CloseUi(sourceHost, NCMetaUiKey.Application, scene.Operator);
                else Server.System<SharedMindSystem>().TransferTo(mind, null, createGhost: false);
                Assert.That(sender.Runtime.Process.State, Is.EqualTo(MetaProcessState.Idle), "A retained unattended service survives losing its UI invoker.");
            });
            await Pair.RunTicksSync(90);
            await Server.WaitAssertion(() =>
            {
                Assert.That(observer.Packets, Is.Empty, "A completed app action cannot retain authority from a closed/logged-out real session.");
                Assert.That(receiver.Runtime.Process.Fault, Is.Null);
                if (transition == "DetachMind") Server.System<SharedMindSystem>().TransferTo(mind, scene.Operator);
                Move(receiverHost);
                Assert.That(runtime.GetLogs(receiverHost, scene.Operator, receiver.Installation.Id), Is.Empty);
            });
        }
        finally
        {
            await Server.WaitPost(() =>
            {
                if (mind != default) Server.System<SharedMindSystem>().TransferTo(mind, null, createGhost: false);
                if (scene != null) scenes.TryDestroy(scene.Id);
                observer.Clear();
            });
            await Pair.RunTicksSync(2);
        }
    }
}
