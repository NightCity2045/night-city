// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
#if TOOLS && NC_DIAGNOSTICS
#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Client._NC.Meta.UI;
using Content.IntegrationTests.Fixtures;
using Content.Meta.Language;
using Content.Meta.Mui;
using Content.Meta.Runtime;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Meta.Applications;
using Content.Server._NC.Meta.Demo;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;
using Content.Server._NC.Meta.Services;
using Content.Server._NC.Persistence.RoundSnapshot;
using Content.Shared._NC.Meta.UI;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.Mind;
using Microsoft.EntityFrameworkCore;
using Robust.Client.UserInterface;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Content.IntegrationTests.Tests._NC.Meta;

[TestFixture, NonParallelizable]
[TestOf(typeof(NCMetaServiceOrderSystem))]
public sealed class NCMetaServiceReplayTransportTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = true };
    private Func<string>? _serverDiagnostics;

    [TestCase("create")]
    [TestCase("assign")]
    public async Task RealFrontendActionReplaysACompletedServiceCommandButRejectsADamagedSelectedParticipant(string operation)
    {
        var scenes = Server.System<NCMetaDemoSceneSystem>();
        var devices = Server.System<NCMetaDeviceRegistrySystem>();
        var runtime = Server.System<NCMetaRuntimeSystem>();
        var persistence = Server.System<NCMetaPersistenceSystem>();
        var apps = Server.System<NCMetaApplicationSystem>();
        var services = Server.System<NCMetaServiceOrderSystem>();
        var database = (ServerDbManager) Server.ResolveDependency<IServerDbManager>();
        var programs = new NCMetaNativeNetworkProgramHarness(Server);
        NCMetaDemoSceneInstance? scene = null;
        EntityUid mind = default;
        string owner = "", deskId = "", original = "", participantType = "", participantId = "";
        var changed = false;
        var active = false;
        var logLevel = services.Log.Level;
        var source = Program(operation);
        var (contract, documents) = Contract(operation);

        Task<T> Native<T>(Func<Task<T>> start) => NCMetaNativeTestTasks.Run(Server, start);
        Task Fault(Action<SqliteServerDbContext> fault) => Native(async () => { await database.ApplyMetaIsolatedSqliteFaultAsync(fault); return true; });
        async Task<string> Evidence(NCPersistentCommandResultData receipt) => await Native(async () =>
        {
            var rows = new List<object?> { await database.GetNCPersistentCommandResultAsync(receipt.CommandId) };
            foreach (var participant in receipt.Aggregates)
            {
                rows.Add(await database.GetNCAggregateSnapshotAsync(participant.AggregateType, participant.AggregateId));
                rows.Add(await database.GetNCPersistentEventPageAsync(participant.AggregateType, participant.AggregateId, null, null, 32));
            }
            return JsonSerializer.Serialize(rows);
        });
        async Task<NCMetaNativeNetworkProgramHarness.Installed> OpenApplication()
        {
            var host = scene!.Objects["host"];
            await Server.WaitPost(() => Server.System<SharedTransformSystem>().SetCoordinates(scene.Operator,
                SComp<TransformComponent>(host).Coordinates.Offset(new Vector2(0, -0.35f))));
            var backend = await programs.InstallAndStartAsync(host, scene.Operator, "native.service.replay", "native.service.replay", source,
                new Dictionary<string, EntityUid> { ["orders"] = host }, retainForInvocations: true);
            _serverDiagnostics = () => $"backend={backend.Runtime.Process.State}; fault={backend.Runtime.Process.Fault}; invocation={backend.Runtime.InvocationActor}; " +
                $"tick={runtime.LastTick}; logs={string.Join(" | ", runtime.GetLogs(host, scene.Operator, backend.Installation.Id).TakeLast(8))}";
            var project = await Native(() => persistence.ReadProjectAsync(backend.Installation.ProjectId, owner));
            Assert.That(project, Is.Not.Null);
            var sourcePath = project!.Files.Single(value => value.Path.EndsWith(".meta", StringComparison.Ordinal)).Path;
            var request = new MetaCompilationRequest([new(sourcePath, source)], "native.service.replay", "native.service.replay");
            var digest = MuiVerifiedApplication.ComputePackageDigest(backend.Artifact.Digest, contract, documents, "");
            Assert.That(MuiVerifiedApplication.TryAdmit(backend.Artifact, new MetaCompiler().VerifyExports(request, backend.Artifact),
                contract, documents, "", digest, out var app, out var error), Is.True, error);
            await Server.WaitAssertion(() =>
            {
                Assert.That(apps.TryRegister(host, scene.Operator, app!, NCMetaAppPolicy.OwnerOnly with
                    { GrantedScopes = backend.Admission.GetGrantedScopes().ToImmutableHashSet(StringComparer.Ordinal) }), Is.True);
                Assert.That(apps.TryOpen(host, scene.Operator, host, "main", NCMetaAppPresentation.Desktop, NCMetaUiKey.Application,
                    () => devices.CanManage(host, scene.Operator), out _), Is.True);
            });
            await FrontendUntil(window => window.GetDiagnosticSnapshot().Revision >= 0);
            await Client.WaitAssertion(() => Assert.That(FindWindow()!.TryStartDiagnosticCapture(), Is.True));
            return backend;
        }
        async Task<Guid> Send(string action, string json)
        {
            Guid id = default;
            await Client.WaitAssertion(() => Assert.That(FindWindow()!.TryRequestDiagnosticAction(action, json, out id), Is.True));
            Assert.That(id, Is.Not.EqualTo(Guid.Empty));
            return id;
        }

        try
        {
            await Server.WaitAssertion(() =>
            {
                Assert.That(ServerSession?.Status, Is.EqualTo(SessionStatus.InGame));
                SEntMan.EventBus.RaiseEvent(EventSource.Local,
                    new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound));
                active = true;
                Assert.That(scenes.TryCreate("NCMetaGarageScene", out scene), Is.True);
                mind = Server.System<SharedMindSystem>().CreateMind(ServerSession!.UserId);
                Server.System<SharedMindSystem>().TransferTo(mind, scene!.Operator);
                Assert.That(ServerSession.AttachedEntity, Is.EqualTo(scene.Operator));
                Assert.That(Server.System<NCEntityPartyResolverSystem>().TryResolve(scene.Operator, out var party), Is.True);
                owner = party.ToString();
            });
            await Until(() => Server.System<NCActiveWorldTimeSystem>().IsReady && devices.TryGetEntry(scene!.Objects["host"], out var entry) &&
                entry.Ready && devices.IsPowered(entry.Entity));
            await Server.WaitAssertion(() =>
            {
                var host = scene!.Objects["host"];
                Server.System<SharedTransformSystem>().SetCoordinates(scene.Operator, SComp<TransformComponent>(host).Coordinates.Offset(new Vector2(0, -0.35f)));
                Assert.That(devices.TryClaim(host, scene.Operator), Is.True);
            });
            await Until(() => devices.TryGetEntry(scene!.Objects["host"], out var entry) && entry.Owner == owner && entry.AuthorityEpoch >= 2);
            await Server.WaitPost(() => { Assert.That(devices.TryGetEntry(scene!.Objects["host"], out var entry), Is.True); deskId = entry.Id; });
            var first = await OpenApplication();
            var seedAction = await Send("seed", "{\"order\":\"\"}");
            await FrontendUntil(window => window.GetDiagnosticSnapshot().LastCompletedAction == seedAction);
            await Server.WaitAssertion(() =>
            {
                Assert.That(first.Runtime.Process.Fault, Is.Null);
                Assert.That(runtime.GetLogs(scene!.Objects["host"], scene.Operator, first.Installation.Id), Does.Contain("seeded"));
            });
            var seedId = NCMetaPersistenceSystem.StableGuid("service:" + deskId + ":" + owner + ":" + seedAction.ToString("D") + ":seed-order");
            var seedReceipt = (await Native(() => database.GetNCPersistentCommandResultAsync(seedId)))!;
            Assert.That(seedReceipt.Status, Is.EqualTo(NCPersistentMutationStatus.DuplicateCompleted));

            // Select the real order/board as the next epoch's immutable baseline. The app and VM
            // are stopped by normal closing, then published/installed/granted/opened normally again.
            NCSessionEpoch ending = default;
            await Server.WaitPost(() =>
            {
                Assert.That(persistence.TryCaptureTime(out ending, out _), Is.True);
                SEntMan.EventBus.RaiseEvent(EventSource.Local,
                    new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PreRoundLobby));
            });
            var coordinator = Server.System<NCRoundSnapshotCoordinatorSystem>();
            await Until(() => !Server.System<NCActiveWorldTimeSystem>().IsReady && coordinator.State == NCRoundSnapshotCoordinatorState.Committed);
            active = false;
            var selected = coordinator.LastSnapshotId;
            var sections = await Native(() => database.GetNCRoundSnapshotSectionsAsync(selected, "meta-aggregates", "world", "city"));
            var records = sections.SelectMany(value => JsonSerializer.Deserialize<NCAggregateSnapshotData[]>(value.Payload)!).ToArray();
            foreach (var participant in seedReceipt.Aggregates)
                Assert.That(records.Single(value => value.AggregateType == participant.AggregateType && value.AggregateId == participant.AggregateId).AggregateVersion,
                    Is.EqualTo(participant.AggregateVersion));
            await Server.WaitPost(() =>
            {
                SEntMan.EventBus.RaiseEvent(EventSource.Local,
                    new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound));
                active = true;
            });
            await Until(() => persistence.TryCaptureTime(out var epoch, out _) && epoch != ending &&
                Server.System<NCMetaSnapshotSystem>().SelectedSnapshotId == selected &&
                devices.TryGetEntry(scene!.Objects["host"], out var entry) && entry.Ready && entry.Owner == owner);
            var backend = await OpenApplication();
            var actionId = await Send("exercise", JsonSerializer.Serialize(new { order = seedId.ToString("D") }));
            await Until(() => runtime.GetLogs(scene!.Objects["host"], scene.Operator, backend.Installation.Id).Contains("positive-replay"));
            var command = NCMetaPersistenceSystem.StableGuid("service:" + deskId + ":" + owner + ":" + actionId.ToString("D") + ":" +
                (operation == "create" ? "create-order" : "assign-order"));
            var receipt = (await Native(() => database.GetNCPersistentCommandResultAsync(command)))!;
            Assert.That(receipt.Status, Is.EqualTo(NCPersistentMutationStatus.DuplicateCompleted));
            Assert.That(receipt.CommandType, Is.EqualTo(operation == "create" ? "meta.service.create" : "meta.service.change"));
            participantType = operation == "create" ? "meta.service-board" : "meta.service-order";
            var target = receipt.Aggregates.Single(value => value.AggregateType == participantType);
            participantId = target.AggregateId;
            var current = (await Native(() => database.GetNCAggregateSnapshotAsync(participantType, participantId)))!;
            Assert.That(current.AggregateVersion, Is.EqualTo(2));
            original = current.Payload;
            // Create reaches private Replay then rejects its board participant. Assign also has an
            // authenticated ReadOrder gate before Replay; either native gate must fail closed.
            await Server.WaitPost(() => services.Log.Level = LogLevel.Fatal);
            await Fault(context => Assert.That(context.NCAggregateSnapshots.Where(value => value.AggregateType == participantType && value.AggregateId == participantId)
                .ExecuteUpdate(update => update.SetProperty(value => value.Payload, original + " ")), Is.EqualTo(1)));
            changed = true;
            var damaged = await Evidence(receipt);
            await FrontendUntil(window => window.GetDiagnosticSnapshot().LastCompletedAction == actionId);
            await Server.WaitAssertion(() =>
            {
                Assert.That(backend.Runtime.Process.Fault, Is.Null);
                var logs = runtime.GetLogs(scene!.Objects["host"], scene.Operator, backend.Installation.Id);
                Assert.That(logs, Does.Contain("rejected"));
                Assert.That(logs, Does.Not.Contain("accepted-damaged"));
                Assert.That(ServerSession!.AttachedEntity, Is.EqualTo(scene.Operator));
            });
            Assert.That(await Evidence(receipt), Is.EqualTo(damaged), "A rejected retry must not append, recreate the order, notify success or replace its receipt.");
        }
        finally
        {
            try
            {
                if (changed) await Fault(context => Assert.That(context.NCAggregateSnapshots.Where(value => value.AggregateType == participantType && value.AggregateId == participantId)
                    .ExecuteUpdate(update => update.SetProperty(value => value.Payload, original)), Is.EqualTo(1)));
            }
            finally
            {
                await Server.WaitPost(() =>
                {
                    services.Log.Level = logLevel;
                    if (mind != default) Server.System<SharedMindSystem>().TransferTo(mind, null, createGhost: false);
                    if (scene != null) scenes.TryDestroy(scene.Id);
                    if (active) SEntMan.EventBus.RaiseEvent(EventSource.Local,
                        new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PreRoundLobby));
                });
                if (active)
                {
                    var coordinator = Server.System<NCRoundSnapshotCoordinatorSystem>();
                    await Until(() => !Server.System<NCActiveWorldTimeSystem>().IsReady &&
                        coordinator.State is NCRoundSnapshotCoordinatorState.Committed or NCRoundSnapshotCoordinatorState.Failed);
                    await Server.WaitAssertion(() => Assert.That(coordinator.State, Is.EqualTo(NCRoundSnapshotCoordinatorState.Committed), coordinator.LastError));
                }
                else await Pair.RunTicksSync(2);
            }
        }
    }

    private NCMetaApplicationWindow? FindWindow()
    {
        var pending = new Queue<Control>();
        pending.Enqueue(Client.ResolveDependency<IUserInterfaceManager>().RootControl);
        for (var index = 0; index < 8192 && pending.TryDequeue(out var control); index++)
        {
            if (control.Disposed) continue;
            if (control is NCMetaApplicationWindow window && window.SessionId != Guid.Empty) return window;
            foreach (var child in control.Children) if (pending.Count < 8192) pending.Enqueue(child);
        }
        return null;
    }

    private async Task FrontendUntil(Func<NCMetaApplicationWindow, bool> condition, [CallerArgumentExpression(nameof(condition))] string boundary = "")
    {
        var elapsed = Stopwatch.StartNew();
        for (var index = 0; index < 1800 && elapsed.Elapsed < TimeSpan.FromSeconds(30); index++)
        {
            var remaining = TimeSpan.FromSeconds(30) - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            var ready = false;
            await Client.WaitPost(() => ready = FindWindow() is { } window && condition(window)).WaitAsync(remaining);
            if (ready) return;
            remaining = TimeSpan.FromSeconds(30) - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await Pair.RunTicksSync(1).WaitAsync(remaining);
        }
        var frontend = "window absent";
        await Client.WaitPost(() => { if (FindWindow() is { } window) frontend = window.GetDiagnosticSnapshot().ToString(); });
        var native = "";
        if (_serverDiagnostics != null) await Server.WaitPost(() => native = _serverDiagnostics());
        Assert.Fail("The actual native MUI frontend did not reach its expected state within 30 seconds/1800 ticks: " + boundary + "\n" + frontend + "\n" + native);
    }

    private async Task Until(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        for (var index = 0; index < 1800 && elapsed.Elapsed < TimeSpan.FromSeconds(30); index++)
        {
            var remaining = TimeSpan.FromSeconds(30) - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            var ready = false;
            await Server.WaitPost(() => ready = condition()).WaitAsync(remaining);
            if (ready) return;
            remaining = TimeSpan.FromSeconds(30) - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await Pair.RunTicksSync(1).WaitAsync(remaining);
        }
        Assert.Fail("The real service operation did not reach its expected state within 30 seconds/1800 ticks.");
    }

    private static string Program(string operation)
    {
        var call = operation == "create"
            ? "orders.create(context.user,context.session,\"Native replay order\",\"create-order\")"
            : "orders.assign(context.user,context.session,input.order,\"self\",1,\"assign-order\")";
        return $$$"""
            module native.service.replay; import meta.app; import meta.services; import meta.system;
            link orders:services.ServiceOrders="orders";
            pub struct State { label:string; }
            pub struct Input { order:string; }
            fn main()->void {}
            pub fn view(context:app.AppContext)->State {return new State(label:"Native replay");}
            pub async fn seed(context:app.AppContext,input:Input)->result<void> {
                match(await orders.create(context.user,context.session,"Checkpoint order","seed-order")){
                    ok(_)=>{system.log("seeded");return ok(());}
                    err(problem)=>{return err(problem);}
                }
            }
            pub async fn exercise(context:app.AppContext,input:Input)->result<void> {
                match(await {{{call}}}){ok(_)=>{} err(problem)=>{return err(problem);}}
                match(await {{{call}}}){ok(_)=>{system.log("positive-replay");} err(problem)=>{return err(problem);}}
                var pause:int=0; while(pause<90){pause=pause+1;yield;}
                match(await {{{call}}}){
                    ok(_)=>{system.log("accepted-damaged");return ok(());}
                    err(_)=>{system.log("rejected");return ok(());}
                }
            }
            """;
    }

    private static (string, Dictionary<string, string>) Contract(string operation)
    {
        var scope = operation == "create" ? "services.create" : "services.assign";
        var json = $$$"""
            {"version":"1.0","views":{"main":{"function":"native.service.replay.view","document":"main.mui",
            "stateType":"native.service.replay.State","fields":{"label":{"type":"string","maxLength":32}},"actions":["seed","exercise"]}},
            "actions":{"seed":{"function":"native.service.replay.seed","inputType":"native.service.replay.Input","inputs":{"order":{"type":"string","maxLength":36}},"scopes":["services.create"]},
            "exercise":{"function":"native.service.replay.exercise","inputType":"native.service.replay.Input","inputs":{"order":{"type":"string","maxLength":36}},"scopes":["{{{scope}}}"]}},"resources":[]}
            """;
        return (json, new() { ["main.mui"] = """
            <Page id="main" title="{bind label}"><Column id="layout"><Button id="seed" action="seed" text="Seed"/><Button id="exercise" action="exercise" text="Exercise"/></Column></Page>
            """ });
    }
}
#endif
