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
[TestOf(typeof(NCMetaPersistenceSystem))]
public sealed class NCMetaFirmwareStateIsolationTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = false };

    [TestPrototypes]
    private const string Transformers = """
        - type: ncMetaStateTransformer
          id: NCMetaIntegrationFirmwarePackageAdoption
          package: test.firmware.adopted
          fromPackage: test.firmware.original
          fromVersion: 1
          toVersion: 1
          steps:
          - kind: Rename
            path: controls.txt
            target: settings/controls.txt
          - kind: SetDefault
            path: adopted.txt
            text: approved
        """;

    [TestCase(false)]
    [TestCase(true)]
    public async Task DifferentFirmwarePackagesRequireAnExplicitTransitionEvenWhenBothSchemasAreOne(bool explicitTransition)
    {
        var persistence = Server.System<NCMetaPersistenceSystem>();
        var clock = Server.System<NCActiveWorldTimeSystem>();
        await Server.WaitPost(() => Server.EntMan.EventBus.RaiseEvent(EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        try
        {
            for (var attempt = 0; attempt < 180 && !clock.IsReady; attempt++) await Server.WaitRunTicks(1);
            NCSessionEpoch epoch = default;
            NCActiveWorldTime time = default;
            await Server.WaitAssertion(() => Assert.That(persistence.TryCaptureTime(out epoch, out time), Is.True));
            var owner = NCPartyRef.ForCharacter(new NCCharacterId(733)).ToString();
            var host = Guid.NewGuid().ToString("D");
            var device = await AwaitPersistence(() => persistence.ClaimDeviceAsync(Guid.NewGuid(), host, owner, "Firmware state test", epoch, time));
            Assert.That(device, Is.Not.Null);
            var project = (await AwaitPersistence(() => persistence.ReadProjectAsync(device!.ActiveProject, owner)))!;

            async Task<NCMetaRelease> Publish(string package)
            {
                var compiled = new MetaCompiler().Compile(new(
                    [new("main.meta", "module test.firmware; fn main()->void{}")],
                    "test.firmware", package, "1.0.0"));
                Assert.That(compiled.Artifact, Is.Not.Null, string.Join("\n", compiled.Diagnostics));
                Assert.That((await AwaitPersistence(() => persistence.PublishReleaseAsync(Guid.NewGuid(), project, compiled.Artifact!,
                    "integration", epoch, time, compiled.Exports))).Success, Is.True);
                project = (await AwaitPersistence(() => persistence.ReadProjectAsync(project.Id, owner)))!;
                return project.Releases.Last();
            }

            var originalRelease = await Publish("test.firmware.original");
            var originalInstall = await AwaitPersistence(() => persistence.InstallAsync(Guid.NewGuid(), host, owner, project, originalRelease,
                [], false, epoch, time, 4 * 1024 * 1024, slotKind: "Firmware"));
            Assert.That(originalInstall.Success, Is.True, originalInstall.MessageKey);
            var original = (await AwaitPersistence(() => persistence.ReadInstallationsAsync(host, owner))).Single();
            Assert.That(original.StateSchemaVersion, Is.EqualTo(1));
            Assert.That((await AwaitPersistence(() => persistence.WriteTextAsync(Guid.NewGuid(), original.StateVolumeId, owner, "controls.txt",
                "Пользовательские настройки 👩‍💻", 262144, epoch, time))).Success, Is.True);
            Assert.That(await AwaitPersistence(() => persistence.CompleteInstallationHealthAsync(original, true, epoch, time)), Is.True);

            var replacementRelease = await Publish(explicitTransition ? "test.firmware.adopted" : "test.firmware.unrelated");
            var replacement = await AwaitPersistence(() => persistence.InstallAsync(Guid.NewGuid(), host, owner, project, replacementRelease,
                [], false, epoch, time, 4 * 1024 * 1024, slotKind: "Firmware"));
            var current = (await AwaitPersistence(() => persistence.ReadInstallationsAsync(host, owner))).Single();
            Assert.That(await AwaitPersistence(() => persistence.ReadTextAsync(original.StateVolumeId, owner, "controls.txt")),
                Is.EqualTo("Пользовательские настройки 👩‍💻"), "The retained original state must remain unchanged.");

            if (!explicitTransition)
            {
                Assert.That(replacement, Is.EqualTo(new NCMetaMutationOutcome(false, "nc-meta-error-state-migration")));
                Assert.Multiple(() =>
                {
                    Assert.That(current.Active.Id, Is.EqualTo(originalRelease.Id));
                    Assert.That(current.Active.Digest, Is.EqualTo(originalRelease.Digest));
                    Assert.That(current.StateVolumeId, Is.EqualTo(original.StateVolumeId));
                    Assert.That(current.StateSchemaVersion, Is.EqualTo(1));
                    Assert.That(current.Revision, Is.EqualTo(original.Revision));
                });
                return;
            }

            Assert.That(replacement.Success, Is.True, replacement.MessageKey);
            Assert.Multiple(() =>
            {
                Assert.That(current.Id, Is.EqualTo(original.Id), "Both packages still occupy the one physical firmware slot.");
                Assert.That(current.Active.Id, Is.EqualTo(replacementRelease.Id));
                Assert.That(current.StateSchemaVersion, Is.EqualTo(1));
                Assert.That(current.StateVolumeId, Is.Not.EqualTo(original.StateVolumeId));
                Assert.That(current.Recovery?.Id, Is.EqualTo(originalRelease.Id));
                Assert.That(current.RecoveryStateVolumeId, Is.EqualTo(original.StateVolumeId));
            });
            Assert.That(await AwaitPersistence(() => persistence.ReadTextAsync(current.StateVolumeId, owner, "controls.txt")), Is.Null);
            Assert.That(await AwaitPersistence(() => persistence.ReadTextAsync(current.StateVolumeId, owner, "settings/controls.txt")),
                Is.EqualTo("Пользовательские настройки 👩‍💻"));
            Assert.That(await AwaitPersistence(() => persistence.ReadTextAsync(current.StateVolumeId, owner, "adopted.txt")), Is.EqualTo("approved"));
            Assert.That((await AwaitPersistence(() => persistence.RollbackInstallationAsync(Guid.NewGuid(), current, epoch, time))).Success, Is.True);
            var restored = (await AwaitPersistence(() => persistence.ReadInstallationsAsync(host, owner))).Single();
            Assert.That(restored.Active.Id, Is.EqualTo(originalRelease.Id));
            Assert.That(restored.StateVolumeId, Is.EqualTo(original.StateVolumeId));
        }
        finally
        {
            await Server.WaitPost(() => Server.EntMan.EventBus.RaiseEvent(EventSource.Local,
                new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
            for (var attempt = 0; attempt < 180 && clock.IsReady; attempt++) await Server.WaitRunTicks(1);
            Assert.That(clock.IsReady, Is.False);
        }
    }

    private Task<T> AwaitPersistence<T>(Func<Task<T>> start) => NCMetaNativeTestTasks.Run(Server, start);
}
