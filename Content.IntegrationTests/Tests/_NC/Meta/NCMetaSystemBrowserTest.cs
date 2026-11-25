// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Server.Atmos.EntitySystems;
using Content.Server.GameTicking;
using Content.Server.Preferences.Managers;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Projects;
using Content.Server._NC.Meta.Runtime;
using Content.Shared.Atmos;
using Content.Shared._NC.Identity;
using Content.Shared._NC.Meta.UI;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._NC.Meta;

/// <summary>The desktop icon of a laptop installs the system browser, starts it and opens its window by itself.</summary>
[TestFixture]
[NonParallelizable]
public sealed class NCMetaSystemBrowserTest : GameTest
{
    // The engine warns when a replicated config message reaches the client a few ticks late, which the slow startup of the
    // city prototypes makes common. These tests exercise the server, so the client log fails them on errors only; the
    // server log stays as strict as everywhere else.
    private Robust.Shared.Log.LogLevel? _clientFailureLevel;

    [SetUp]
    public void AllowClientWarnings()
    {
        _clientFailureLevel = Pair.ClientLogHandler.FailureLevel;
        Pair.ClientLogHandler.FailureLevel = Robust.Shared.Log.LogLevel.Error;
    }

    [TearDown]
    public void RestoreClientFailureLevel() => Pair.ClientLogHandler.FailureLevel = _clientFailureLevel;

    [TearDown]
    public Task CloseWorld() => NCMetaNativeTestTasks.CloseWorld(Server);

    private async Task<(EntityUid Laptop, EntityUid User, NCMetaDeviceEntry Entry, string Owner, MapId Map)> StartBrowserAsync()
    {
        var preferences = Server.ResolveDependency<IServerPreferencesManager>();
        var persistence = Server.System<NCMetaPersistenceSystem>();
        var devices = Server.System<NCMetaDeviceRegistrySystem>();
        var workspace = Server.System<NCMetaWorkspaceSystem>();
        var runtime = Server.System<NCMetaRuntimeSystem>();
        NCCharacterId character = default;
        EntityUid laptop = default, user = default;
        MapId mapId = default;
        await Server.WaitAssertion(() =>
        {
            Assert.That(ServerSession, Is.Not.Null);
            Assert.That(preferences.TryGetSelectedNCCharacterId(ServerSession!.UserId, out character), Is.True);
            var mapEntity = Server.System<SharedMapSystem>().CreateMap(out var map);
            mapId = map;
            var air = new GasMixture(Atmospherics.CellVolume) { Temperature = Atmospherics.T20C };
            air.AdjustMoles(Gas.Oxygen, Atmospherics.OxygenMolesStandard);
            air.AdjustMoles(Gas.Nitrogen, Atmospherics.NitrogenMolesStandard);
            Server.System<AtmosphereSystem>().SetMapAtmosphere(mapEntity, false, air);
            user = SEntMan.SpawnEntity("MobHuman", new MapCoordinates(Vector2.Zero, map));
            laptop = SEntMan.SpawnEntity("NCMetaLaptop", new MapCoordinates(Vector2.Zero, map));
            Server.PlayerMan.SetAttachedEntity(ServerSession, user);
            SEntMan.EventBus.RaiseEvent(EventSource.Local, new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound));
        });
        var clock = Server.System<NCActiveWorldTimeSystem>();
        for (var step = 0; step < 180 && !clock.IsReady; step++) await Pair.RunTicksSync(1);
        NCMetaDeviceEntry entry = null!;
        for (var step = 0; step < 180; step++)
        {
            var ready = false;
            await Server.WaitPost(() => ready = devices.TryGetEntry(laptop, out entry) && entry.Ready);
            if (ready) break;
            await Pair.RunTicksSync(1);
        }
        Assert.That(entry?.Ready, Is.True);
        Content.Shared._NC.Persistence.Time.NCSessionEpoch epoch = default;
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime now = default;
        await Server.WaitAssertion(() => Assert.That(persistence.TryCaptureTime(out epoch, out now), Is.True));
        var owner = NCPartyRef.ForCharacter(character).ToString();
        var record = await NCMetaNativeTestTasks.Run(Server, () => persistence.ClaimDeviceAsync(Guid.NewGuid(), entry.Id, owner, "Laptop", epoch, now));
        Assert.That(record, Is.Not.Null);
        await Server.WaitAssertion(() => Assert.That(devices.TryApplyOwner(laptop, entry.Id, "", owner, record!.AuthorityEpoch), Is.True));
        await Server.WaitAssertion(() => Assert.That(Server.System<SharedUserInterfaceSystem>()
            .TryOpenUi(laptop, NCMetaUiKey.Workspace, user), Is.True));
        await Server.WaitRunTicks(5);

        workspace.LastNotice = null;
        var request = new NCMetaWorkspaceRequestMessage(NCMetaWorkspaceOperation.OpenSystemApplication, Guid.NewGuid(),
            source: "meta-stock-citinet-browser") { Actor = user, UiKey = NCMetaUiKey.Workspace };
        await Server.WaitPost(() => SEntMan.EventBus.RaiseLocalEvent(laptop, request));
        for (var step = 0; step < 900 && workspace.LastNotice == null; step++) await Pair.RunTicksSync(1);

        return (laptop, user, entry, owner, mapId);
    }

    [Test]
    public async Task ASystemBrowserInstallsItselfOnALaptopAndStarts()
    {
        var (laptop, _, entry, owner, _) = await StartBrowserAsync();
        var persistence = Server.System<NCMetaPersistenceSystem>();
        var workspace = Server.System<NCMetaWorkspaceSystem>();
        var runtime = Server.System<NCMetaRuntimeSystem>();
        var installed = await NCMetaNativeTestTasks.Run(Server, () => persistence.ReadInstallationsAsync(entry.Id, owner));
        var running = false;
        await Server.WaitPost(() => running = installed.Length == 1 && runtime.TryGetInstallation(laptop, owner, installed[0].Id, out _));
        Assert.That(installed, Has.Length.EqualTo(1), "Last server notice: " + workspace.LastNotice);
        Assert.That(running, Is.True, "Last server notice: " + workspace.LastNotice);
    }

    private const string OfferedProgram = """
        module offer.demo;
        import meta.system;
        async fn main() -> void { system.log("hello"); }
        """;

    [Test]
    public async Task AProgramOfferedBySiteInstallsOnTheVisitorsLaptopOnlyAfterTheyAccept()
    {
        var (laptop, user, entry, owner, map) = await StartBrowserAsync();
        var persistence = Server.System<NCMetaPersistenceSystem>();
        var devices = Server.System<NCMetaDeviceRegistrySystem>();
        var workspace = Server.System<NCMetaWorkspaceSystem>();
        var runtime = Server.System<NCMetaRuntimeSystem>();

        // The site: another computer of the same owner with the program it will offer already installed.
        EntityUid site = default;
        await Server.WaitPost(() => site = SEntMan.SpawnEntity("NCMetaLaptop", new MapCoordinates(Vector2.UnitX, map)));
        NCMetaDeviceEntry siteEntry = null!;
        for (var step = 0; step < 180; step++)
        {
            var ready = false;
            await Server.WaitPost(() => ready = devices.TryGetEntry(site, out siteEntry) && siteEntry.Ready);
            if (ready) break;
            await Pair.RunTicksSync(1);
        }
        Assert.That(siteEntry?.Ready, Is.True);
        Content.Shared._NC.Persistence.Time.NCSessionEpoch epoch = default;
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime now = default;
        await Server.WaitAssertion(() => Assert.That(persistence.TryCaptureTime(out epoch, out now), Is.True));
        var record = await NCMetaNativeTestTasks.Run(Server, () => persistence.ClaimDeviceAsync(Guid.NewGuid(), siteEntry.Id, owner, "Site", epoch, now));
        Assert.That(record, Is.Not.Null);
        await Server.WaitAssertion(() => Assert.That(devices.TryApplyOwner(site, siteEntry.Id, "", owner, record!.AuthorityEpoch), Is.True));
        await new NCMetaNativeNetworkProgramHarness(Server).InstallAndStartAsync(site, user, "offer.demo", "offer.demo", OfferedProgram,
            new System.Collections.Generic.Dictionary<string, EntityUid>());

        var before = await NCMetaNativeTestTasks.Run(Server, () => persistence.ReadInstallationsAsync(entry.Id, owner));
        (string Id, string Name, string[] Scopes)? pending = null;
        // The browser takes the offer once its program is running, so a site that offered too early simply offers again.
        for (var attempt = 0; attempt < 20 && pending == null; attempt++)
        {
            var offered = false;
            await Server.WaitPost(() => offered = workspace.TryOfferProgram(site, entry.Id, "Free tool", "offer.demo"));
            for (var step = 0; step < 90 && pending == null; step++)
            {
                await Server.WaitPost(() => pending = workspace.GetPendingOffer(entry.Id));
                if (pending == null) await Pair.RunTicksSync(1);
            }
        }
        Assert.That(pending, Is.Not.Null, "The browser never received the offer.");
        Assert.That(pending!.Value.Name, Is.EqualTo("Free tool"));
        var afterOffer = await NCMetaNativeTestTasks.Run(Server, () => persistence.ReadInstallationsAsync(entry.Id, owner));
        Assert.That(afterOffer, Has.Length.EqualTo(before.Length), "An offer alone must install nothing.");

        string? wrong = null;
        await NCMetaNativeTestTasks.Run(Server, async () => { wrong = await workspace.AcceptOfferAsync(laptop, user, Guid.NewGuid().ToString("D")); return true; });
        Assert.That(wrong, Is.EqualTo("NotFound"));

        string? problem = "pending";
        await NCMetaNativeTestTasks.Run(Server, async () => { problem = await workspace.AcceptOfferAsync(laptop, user, pending.Value.Id); return true; });
        Assert.That(problem, Is.Null, "Last server notice: " + workspace.LastNotice);
        var installed = await NCMetaNativeTestTasks.Run(Server, () => persistence.ReadInstallationsAsync(entry.Id, owner));
        // The copy is a project of the visitor's own, so it carries a name of theirs, not the site's package name.
        var known = before.Select(value => value.Id).ToHashSet();
        var added = installed.Where(value => !known.Contains(value.Id)).ToArray();
        Assert.That(added, Has.Length.EqualTo(1), "The accepted program was not installed. Installed: " +
            string.Join(";", installed.Select(value => value.SlotKind + "/" + value.PackageIdentity)));
        var program = added[0];
        // The program only says hello and ends, so its log is what shows it really ran on the visitor's laptop.
        string[] logs = [];
        for (var step = 0; step < 600 && !logs.Contains("hello"); step++)
        {
            await Server.WaitPost(() => logs = runtime.GetLogs(laptop, user, program.Id));
            if (!logs.Contains("hello")) await Pair.RunTicksSync(1);
        }
        Assert.That(logs, Does.Contain("hello"), "The accepted program never ran. Last server notice: " + workspace.LastNotice);
        await Server.WaitPost(() => Assert.That(workspace.GetPendingOffer(entry.Id), Is.Null));
    }
}
