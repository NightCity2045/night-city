// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.GameTicking;
using Content.Server.Preferences.Managers;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Meta.Demo;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.Devices;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

#nullable enable annotations

namespace Content.IntegrationTests.Tests._NC.Meta;

/// <summary>
/// Door access decided by META programs. The device owner is an ordinary player character: a real session attached to
/// the operator body whose selected profile supplies the persistent character identity.
/// </summary>
[TestFixture, NonParallelizable]
[TestOf(typeof(NCMetaDoorAccessSystem))]
public sealed class NCMetaDoorAccessTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = true };
    [SidedDependency(Side.Server)] private NCMetaDemoSceneSystem _scenes = default!;
    [SidedDependency(Side.Server)] private NCMetaDeviceRegistrySystem _devices = default!;
    [SidedDependency(Side.Server)] private NCMetaRuntimeSystem _runtime = default!;

    private const string ListProgram = """
        module door.access;
        import meta.system;
        import meta.doors;

        link door: Door = "main-door";

        fn isAllowed(name: string) -> bool {
            if (name == "__NAME__") { return true; }
            return false;
        }

        async watch door.accessRequested(request) {
            let allow: bool = isAllowed(request.subject.name);
            var line: string = "deny " + request.subject.name;
            if (allow) { line = "allow " + request.subject.name; }
            system.log(line);
            let answered = await door.answerAccess(request.requestId, allow);
            match (answered) {
                ok(state) => { system.log("answered"); }
                err(problem) => { system.log(problem.message); }
            }
        }
        """;

    private const string SilentProgram = """
        module door.access;
        import meta.system;
        import meta.doors;

        link door: Door = "main-door";

        watch door.accessRequested(request) {
            system.log("received " + request.subject.name);
        }
        """;

    private const string DirectProgram = """
        module door.access;
        import meta.system;
        import meta.doors;

        link door: Door = "main-door";

        async watch door.accessRequested(request) {
            let opened = await door.setOpen(true);
            match (opened) {
                ok(state) => { system.log("opened directly"); }
                err(problem) => { system.log(problem.message); }
            }
        }
        """;

    private sealed record DoorScene(NCMetaDemoSceneInstance Physical, ICommonSession Session, EntityUid? PreviousBody,
        EntityUid Actor, string Owner)
    {
        public EntityUid Host => Physical.Objects["host"];
        public EntityUid Door => Physical.Objects["door"];
    }

    private NCMetaNativeNetworkProgramHarness Programs => new(Server);

    private async Task Until(Func<bool> condition, int maximumTicks = 600, string reason = "Condition did not complete.")
    {
        var ready = false;
        for (var tick = 0; tick < maximumTicks; tick++)
        {
            await Server.WaitPost(() => ready = condition());
            if (ready) return;
            await Server.WaitRunTicks(1);
        }
        Assert.Fail(reason);
    }

    private void MoveTo(EntityUid actor, EntityUid target) => Server.System<SharedTransformSystem>().SetCoordinates(actor,
        SComp<TransformComponent>(target).Coordinates.Offset(new Vector2(0, -0.35f)));

    private async Task<DoorScene> CreateScene(bool claim)
    {
        await Server.WaitPost(() => SEntMan.EventBus.RaiseEvent(EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        await Until(() => Server.System<NCActiveWorldTimeSystem>().IsReady);
        NCMetaDemoSceneInstance physical = null!;
        await Server.WaitAssertion(() => Assert.That(_scenes.TryCreate("NCMetaDoorScene", out physical), Is.True));
        var session = Server.PlayerMan.Sessions.Single();
        var previous = session.AttachedEntity;
        var actor = physical.Operator;
        var owner = "";
        await Server.WaitAssertion(() =>
        {
            Server.PlayerMan.SetAttachedEntity(session, actor);
            Assert.That(Server.ResolveDependency<IServerPreferencesManager>()
                .TryGetSelectedNCCharacterId(session.UserId, out var character), Is.True, "The test player has no persistent character.");
            owner = NCPartyRef.ForCharacter(character).ToString();
        });
        var scene = new DoorScene(physical, session, previous, actor, owner);
        await Until(() => _devices.IsPowered(scene.Door), 600, "The scene door never became powered.");
        if (claim)
            foreach (var endpoint in new[] { scene.Host, scene.Door })
                await Claim(endpoint, actor, owner);
        return scene;
    }

    private async Task Claim(EntityUid endpoint, EntityUid actor, string owner)
    {
        await Until(() => _devices.TryGetEntry(endpoint, out var device) && device.Ready && _devices.IsPowered(endpoint),
            reason: "The endpoint did not finish persistence restore and power-up.");
        await Server.WaitAssertion(() => { MoveTo(actor, endpoint); Assert.That(_devices.TryClaim(endpoint, actor), Is.True); });
        await Until(() => _devices.TryGetEntry(endpoint, out var device) && device.Owner == owner && device.AuthorityEpoch >= 2,
            reason: "The durable device claim did not commit.");
    }

    private async Task Destroy(DoorScene scene)
    {
        await NCMetaNativeTestTasks.CloseWorld(Server);
        await Server.WaitPost(() =>
        {
            Server.PlayerMan.SetAttachedEntity(scene.Session, scene.PreviousBody);
            _scenes.TryDestroy(scene.Physical.Id);
        });
        await Server.WaitRunTicks(2);
    }

    private async Task<NCMetaNativeNetworkProgramHarness.Installed> Install(DoorScene scene, string source)
    {
        await Server.WaitPost(() => MoveTo(scene.Actor, scene.Host));
        return await Programs.InstallAndStartAsync(scene.Host, scene.Actor, "door.access", "door.access", source,
            new Dictionary<string, EntityUid> { ["main-door"] = scene.Door });
    }

    private string[] Logs(DoorScene scene, Guid installation)
    {
        MoveTo(scene.Actor, scene.Host);
        return _runtime.GetLogs(scene.Host, scene.Actor, installation);
    }

    private string NameOf(DoorScene scene) => SEntMan.GetComponent<MetaDataComponent>(scene.Actor).EntityName;

    private bool TryOpen(DoorScene scene) => Server.System<SharedDoorSystem>().TryOpen(scene.Door, user: scene.Actor, quiet: true);

    private bool IsOpening(DoorScene scene) =>
        SEntMan.GetComponent<DoorComponent>(scene.Door).State is DoorState.Opening or DoorState.Open;

    private bool Has(DoorScene scene, NCMetaDoorAccessOutcome outcome) =>
        SEntMan.GetComponent<NCMetaDoorAccessComponent>(scene.Door).Journal.Any(record => record.Outcome == outcome);


    private string Describe(DoorScene scene, Guid? installation = null)
    {
        var journal = string.Join("; ", SEntMan.GetComponent<NCMetaDoorAccessComponent>(scene.Door).Journal
            .Select(record => record.Outcome + ":" + record.Subject));
        var state = SEntMan.GetComponent<DoorComponent>(scene.Door).State;
        var logs = installation is { } id ? string.Join(" | ", Logs(scene, id)) : "";
        return $"door={state}; journal=[{journal}]; logs=[{logs}]";
    }

    /// <summary>Claiming walks the operator into the door, which opens it for everyone; start every body from a closed, clean door.</summary>
    private async Task CloseDoor(DoorScene scene)
    {
        await Server.WaitPost(() =>
        {
            MoveTo(scene.Actor, scene.Host);
            Server.System<SharedDoorSystem>().TryClose(scene.Door);
        });
        await Until(() => SEntMan.GetComponent<DoorComponent>(scene.Door).State == DoorState.Closed, 900, "The scene door never closed.");
        await Server.WaitPost(() => SEntMan.GetComponent<NCMetaDoorAccessComponent>(scene.Door).Journal.Clear());
    }

    /// <summary>Runs a body in a fresh scene; a teardown failure never hides the first failure.</summary>
    private async Task RunScene(bool claim, Func<DoorScene, Task> body)
    {
        var scene = await CreateScene(claim);
        Exception? failure = null;
        try
        {
            await CloseDoor(scene);
            await body(scene);
        }
        catch (Exception exception) { failure = exception; throw; }
        finally
        {
            try { await Destroy(scene); }
            catch when (failure != null) { }
        }
    }

    [Test]
    public async Task AHostRefusesAProgramWhoseLinkDoesNotFitItsCapacityAndTakesItOnceItFits()
    {
        await RunScene(true, async scene =>
        {
            var profile = Server.ResolveDependency<IPrototypeManager>().Index<NCMetaHardwareProfilePrototype>("NCMetaComputer");
            var original = profile.LinkCapacity;
            var runtime = Server.System<NCMetaRuntimeSystem>();
            var prepared = await Programs.PrepareAsync(scene.Host, scene.Actor, "door.access", "door.access", SilentProgram,
                new Dictionary<string, EntityUid> { ["main-door"] = scene.Door });
            try
            {
                var started = true;
                await Server.WaitPost(() =>
                {
                    profile.LinkCapacity = 0;
                    started = runtime.TryStart(scene.Host, scene.Actor, prepared.Installation, prepared.Artifact,
                        prepared.RetainForInvocations, serviceAdmission: prepared.Admission);
                });
                Assert.That(started, Is.False, "A link that does not fit the host capacity must be refused.");
                await Server.WaitAssertion(() => Assert.That(runtime.IsLinkCapacityExceeded(scene.Host, prepared.Installation), Is.True,
                    "The refusal must be reported as a link capacity problem, not as denied access."));
                await Server.WaitAssertion(() =>
                {
                    Assert.That(runtime.TryGetLinkUsage(scene.Host, out var used, out var capacity), Is.True);
                    Assert.That(used, Is.Zero);
                    Assert.That(capacity, Is.Zero);
                });

                await Server.WaitPost(() => profile.LinkCapacity = original);
                await Server.WaitAssertion(() => Assert.That(runtime.IsLinkCapacityExceeded(scene.Host, prepared.Installation), Is.False));
                await Programs.StartAsync(prepared);
                await Server.WaitAssertion(() =>
                {
                    Assert.That(runtime.TryGetLinkUsage(scene.Host, out var used, out var capacity), Is.True);
                    Assert.That(capacity, Is.EqualTo(original));
                    Assert.That(used, Is.GreaterThan(0), "The running program must reserve capacity for its link.");
                });
            }
            finally { await Server.WaitPost(() => profile.LinkCapacity = original); }
        });
    }

    [Test]
    public async Task ADoorWithoutAListeningProgramOpensForEveryone()
    {
        await RunScene(false, async scene =>
        {
            var opened = false;
            await Server.WaitPost(() => opened = TryOpen(scene));
            Assert.That(opened, Is.True);
            await Until(() => IsOpening(scene));
            await Server.WaitAssertion(() => Assert.That(Has(scene, NCMetaDoorAccessOutcome.Unavailable), Is.True));
        });
    }

    [Test]
    public async Task ProgramAnswerAllowOpensTheDoorForTheListedPerson()
    {
        await RunScene(true, async scene =>
        {
            var name = NameOf(scene);
            Assert.That(name, Does.Not.Contain("\""));
            var program = await Install(scene, ListProgram.Replace("__NAME__", name));
            var opened = true;
            await Server.WaitPost(() => opened = TryOpen(scene));
            Assert.That(opened, Is.False, "The vanilla open must be cancelled until the program answers.");
            var state = "";
            await Until(() => { state = Describe(scene, program.Installation.Id); return Has(scene, NCMetaDoorAccessOutcome.Allowed); },
                900, "The allow answer never settled the request. " + state);
            await Until(() => IsOpening(scene), 300, "The allowed answer never opened the door. " + state);
            await Server.WaitAssertion(() => Assert.That(Logs(scene, program.Installation.Id), Does.Contain("allow " + name), Describe(scene)));
        });
    }

    [Test]
    public async Task ProgramAnswerDenyKeepsTheDoorClosedForAnUnlistedPerson()
    {
        await RunScene(true, async scene =>
        {
            var name = NameOf(scene);
            var program = await Install(scene, ListProgram.Replace("__NAME__", "Somebody Else"));
            var opened = true;
            await Server.WaitPost(() => opened = TryOpen(scene));
            Assert.That(opened, Is.False);
            var state = "";
            await Until(() => { state = Describe(scene, program.Installation.Id); return Has(scene, NCMetaDoorAccessOutcome.Denied); },
                900, "The deny answer never reached the door. " + state);
            await Server.WaitAssertion(() =>
            {
                Assert.That(IsOpening(scene), Is.False, Describe(scene, program.Installation.Id));
                Assert.That(Has(scene, NCMetaDoorAccessOutcome.TimedOut), Is.False, Describe(scene, program.Installation.Id));
                Assert.That(Logs(scene, program.Installation.Id), Does.Contain("deny " + name));
            });
        });
    }

    [Test]
    public async Task ASilentProgramIsADenialAfterTheResponseDeadline()
    {
        await RunScene(true, async scene =>
        {
            var program = await Install(scene, SilentProgram);
            var opened = true;
            await Server.WaitPost(() => opened = TryOpen(scene));
            Assert.That(opened, Is.False);
            var state = "";
            await Until(() => { state = Describe(scene, program.Installation.Id); return Has(scene, NCMetaDoorAccessOutcome.TimedOut); },
                900, "Silence never resolved as a timeout. " + state);
            await Server.WaitAssertion(() => Assert.That(IsOpening(scene), Is.False, Describe(scene, program.Installation.Id)));
        });
    }

    [Test]
    public async Task AProgramThatOpensTheDoorDirectlySettlesItsOwnRequest()
    {
        await RunScene(true, async scene =>
        {
            var program = await Install(scene, DirectProgram);
            var opened = true;
            await Server.WaitPost(() => opened = TryOpen(scene));
            Assert.That(opened, Is.False);
            var state = "";
            await Until(() => { state = Describe(scene, program.Installation.Id); return IsOpening(scene); },
                900, "setOpen from the program was blocked by the door access interception. " + state);
            await Until(() => Has(scene, NCMetaDoorAccessOutcome.Allowed));
            await Server.WaitRunTicks(240);
            await Server.WaitAssertion(() => Assert.That(Has(scene, NCMetaDoorAccessOutcome.TimedOut), Is.False,
                Describe(scene, program.Installation.Id)));
        });
    }
}
