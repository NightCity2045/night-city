// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
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
    [TearDown]
    public Task CloseWorld() => NCMetaNativeTestTasks.CloseWorld(Server);

    [Test]
    public async Task ASystemBrowserInstallsItselfOnALaptopAndStarts()
    {
        var preferences = Server.ResolveDependency<IServerPreferencesManager>();
        var persistence = Server.System<NCMetaPersistenceSystem>();
        var devices = Server.System<NCMetaDeviceRegistrySystem>();
        var workspace = Server.System<NCMetaWorkspaceSystem>();
        var runtime = Server.System<NCMetaRuntimeSystem>();
        NCCharacterId character = default;
        EntityUid laptop = default, user = default;
        await Server.WaitAssertion(() =>
        {
            Assert.That(ServerSession, Is.Not.Null);
            Assert.That(preferences.TryGetSelectedNCCharacterId(ServerSession!.UserId, out character), Is.True);
            var mapEntity = Server.System<SharedMapSystem>().CreateMap(out var map);
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
        for (var step = 0; step < 180 && !clock.IsReady; step++) await Server.WaitRunTicks(1);
        NCMetaDeviceEntry entry = null!;
        for (var step = 0; step < 180; step++)
        {
            var ready = false;
            await Server.WaitPost(() => ready = devices.TryGetEntry(laptop, out entry) && entry.Ready);
            if (ready) break;
            await Server.WaitRunTicks(1);
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
        for (var step = 0; step < 900 && workspace.LastNotice == null; step++) await Server.WaitRunTicks(1);

        var installed = await NCMetaNativeTestTasks.Run(Server, () => persistence.ReadInstallationsAsync(entry.Id, owner));
        var running = false;
        await Server.WaitPost(() => running = installed.Length == 1 && runtime.TryGetInstallation(laptop, owner, installed[0].Id, out _));
        Assert.That(installed, Has.Length.EqualTo(1), "Last server notice: " + workspace.LastNotice);
        Assert.That(running, Is.True, "Last server notice: " + workspace.LastNotice);
    }
}
