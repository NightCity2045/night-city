// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Meta.Language;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.Identity;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._NC.Meta;

[TestFixture]
[NonParallelizable]
public sealed class NCMetaStateMigrationTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = false };

    [TestPrototypes]
    private const string Transformers = """
        - type: ncMetaStateTransformer
          id: NCMetaIntegrationSettingsV2
          package: test.migration
          fromVersion: 1
          toVersion: 2
          steps:
          - kind: Rename
            path: legacy.txt
            target: settings/message.txt
          - kind: SetDefault
            path: language.txt
            text: ru-RU
        """;

    [Test]
    public async Task UpgradeStagesTrustedStateAndRollbackRestoresTheMatchingSchemaWithoutDataLoss()
    {
        var persistence = Server.System<NCMetaPersistenceSystem>();
        var clock = Server.System<NCActiveWorldTimeSystem>();
        await Server.WaitPost(() => Server.EntMan.EventBus.RaiseEvent(EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        for (var attempt = 0; attempt < 180 && !clock.IsReady; attempt++) await Server.WaitRunTicks(1);
        NCSessionEpoch epoch = default;
        NCActiveWorldTime time = default;
        await Server.WaitAssertion(() => Assert.That(persistence.TryCaptureTime(out epoch, out time), Is.True));
        var owner = NCPartyRef.ForCharacter(new NCCharacterId(732)).ToString();
        var host = Guid.NewGuid().ToString("D");
        var device = await AwaitPersistence(() => persistence.ClaimDeviceAsync(Guid.NewGuid(), host, owner, "Migration test", epoch, time));
        Assert.That(device, Is.Not.Null);
        var project = (await AwaitPersistence(() => persistence.ReadProjectAsync(device!.ActiveProject, owner)))!;
        var manifestId = Guid.NewGuid();
        long manifestRevision = 0;
        async Task<NCMetaRelease> Publish(int schema)
        {
            var saved = await AwaitPersistence(() => persistence.SaveSourceAsync(Guid.NewGuid(), project.Id, manifestId, owner,
                manifestRevision, "manifest.json", $"{{\"stateSchemaVersion\":{schema}}}", epoch, time));
            Assert.That(saved.Success, Is.True, saved.MessageKey);
            manifestRevision++;
            project = (await AwaitPersistence(() => persistence.ReadProjectAsync(project.Id, owner)))!;
            var compiled = new MetaCompiler().Compile(new([new("main.meta", "module test.migration; fn main()->void{}")],
                "test.migration", "test.migration", $"1.0.{schema}"));
            Assert.That(compiled.Artifact, Is.Not.Null);
            Assert.That((await AwaitPersistence(() => persistence.PublishReleaseAsync(Guid.NewGuid(), project, compiled.Artifact!, "integration",
                epoch, time, compiled.Exports))).Success, Is.True);
            project = (await AwaitPersistence(() => persistence.ReadProjectAsync(project.Id, owner)))!;
            return project.Releases.Last();
        }
        async Task<NCMetaInstallation> Install(NCMetaRelease release)
        {
            var result = await AwaitPersistence(() => persistence.InstallAsync(Guid.NewGuid(), host, owner, project, release, [], false,
                epoch, time, 4 * 1024 * 1024));
            Assert.That(result.Success, Is.True, result.MessageKey);
            return (await AwaitPersistence(() => persistence.ReadInstallationsAsync(host, owner))).Single();
        }
        var first = await Install(await Publish(1));
        Assert.That((await AwaitPersistence(() => persistence.WriteTextAsync(Guid.NewGuid(), first.StateVolumeId, owner, "legacy.txt",
            "Привет 👩‍💻", 262144, epoch, time))).Success, Is.True);
        Assert.That(await AwaitPersistence(() => persistence.CompleteInstallationHealthAsync(first, true, epoch, time)), Is.True);
        var second = await Install(await Publish(2));
        Assert.That(second.StateSchemaVersion, Is.EqualTo(2));
        Assert.That(second.RecoveryStateSchemaVersion, Is.EqualTo(1));
        Assert.That(second.StateVolumeId, Is.Not.EqualTo(first.StateVolumeId));
        Assert.That(await AwaitPersistence(() => persistence.ReadTextAsync(second.StateVolumeId, owner, "legacy.txt")), Is.Null);
        Assert.That(await AwaitPersistence(() => persistence.ReadTextAsync(second.StateVolumeId, owner, "settings/message.txt")), Is.EqualTo("Привет 👩‍💻"));
        Assert.That(await AwaitPersistence(() => persistence.ReadTextAsync(second.StateVolumeId, owner, "language.txt")), Is.EqualTo("ru-RU"));
        Assert.That(await AwaitPersistence(() => persistence.ReadTextAsync(first.StateVolumeId, owner, "legacy.txt")), Is.EqualTo("Привет 👩‍💻"));
        Assert.That(await AwaitPersistence(() => persistence.CompleteInstallationHealthAsync(second, true, epoch, time)), Is.True);
        var unknown = await Publish(3);
        var denied = await AwaitPersistence(() => persistence.InstallAsync(Guid.NewGuid(), host, owner, project, unknown, [], false,
            epoch, time, 4 * 1024 * 1024));
        Assert.That(denied, Is.EqualTo(new NCMetaMutationOutcome(false, "nc-meta-error-state-migration")));
        var unchanged = (await AwaitPersistence(() => persistence.ReadInstallationsAsync(host, owner))).Single();
        Assert.That(unchanged.Active.Id, Is.EqualTo(second.Active.Id));
        Assert.That(unchanged.StateVolumeId, Is.EqualTo(second.StateVolumeId));
        Assert.That((await AwaitPersistence(() => persistence.RollbackInstallationAsync(Guid.NewGuid(), unchanged, epoch, time))).Success, Is.True);
        var restored = (await AwaitPersistence(() => persistence.ReadInstallationsAsync(host, owner))).Single();
        Assert.That(restored.StateSchemaVersion, Is.EqualTo(1));
        Assert.That(restored.RecoveryStateSchemaVersion, Is.EqualTo(2));
        Assert.That(restored.StateVolumeId, Is.EqualTo(first.StateVolumeId));
        Assert.That(await AwaitPersistence(() => persistence.ReadTextAsync(restored.StateVolumeId, owner, "legacy.txt")), Is.EqualTo("Привет 👩‍💻"));
        await Server.WaitPost(() => Server.EntMan.EventBus.RaiseEvent(EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
        for (var attempt = 0; attempt < 180 && clock.IsReady; attempt++) await Server.WaitRunTicks(1);
        Assert.That(clock.IsReady, Is.False);
    }

    private Task<T> AwaitPersistence<T>(Func<Task<T>> start) => NCMetaNativeTestTasks.Run(Server, start);
}
