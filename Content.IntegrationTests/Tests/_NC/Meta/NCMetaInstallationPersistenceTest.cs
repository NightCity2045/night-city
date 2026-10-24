// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
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
public sealed class NCMetaInstallationPersistenceTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = false };

    [Test]
    public async Task UnbootedUpdatesPreserveHealthyCodeAndDataAndCannotWriteThroughAReplacedGeneration()
    {
        var persistence = Server.System<NCMetaPersistenceSystem>();
        var clock = Server.System<NCActiveWorldTimeSystem>();
        await Server.WaitPost(() => Server.EntMan.EventBus.RaiseEvent(EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        for (var attempt = 0; attempt < 180 && !clock.IsReady; attempt++) await Server.WaitRunTicks(1);
        NCSessionEpoch epoch = default;
        NCActiveWorldTime time = default;
        await Server.WaitAssertion(() => Assert.That(persistence.TryCaptureTime(out epoch, out time), Is.True));
        var owner = NCPartyRef.ForCharacter(new NCCharacterId(731)).ToString();
        var host = Guid.NewGuid().ToString("D");
        var device = await AwaitPersistence(() => persistence.ClaimDeviceAsync(Guid.NewGuid(), host, owner, "Install test", epoch, time));
        Assert.That(device, Is.Not.Null);
        var project = (await AwaitPersistence(() => persistence.ReadProjectAsync(device!.ActiveProject, owner)))!;
        async Task<NCMetaRelease> Publish(int version, string package = "test.install")
        {
            var compiled = new MetaCompiler().Compile(new([new("main.meta",
                $"module test.install; import meta.system; fn main()->void{{system.log(\"{version}\");}}")],
                "test.install", package, $"1.0.{version}"));
            Assert.That(compiled.Artifact, Is.Not.Null, string.Join("\n", compiled.Diagnostics));
            var result = await AwaitPersistence(() => persistence.PublishReleaseAsync(Guid.NewGuid(), project, compiled.Artifact!,
                "integration", epoch, time, compiled.Exports));
            Assert.That(result.Success, Is.True, result.MessageKey);
            project = (await AwaitPersistence(() => persistence.ReadProjectAsync(project.Id, owner)))!;
            return project.Releases.Last();
        }
        async Task<NCMetaInstallation> Install(NCMetaRelease release, Guid request = default, Func<Task<bool>>? guard = null)
        {
            var result = await AwaitPersistence(() => persistence.InstallAsync(request == Guid.Empty ? Guid.NewGuid() : request,
                host, owner, project, release, [], false, epoch, time, 4 * 1024 * 1024, activationGuard: guard));
            Assert.That(result.Success, Is.True, result.MessageKey);
            return (await AwaitPersistence(() => persistence.ReadInstallationsAsync(host, owner))).Single(value => value.PackageIdentity == "test.install");
        }
        var first = await Publish(1);
        var initial = await Install(first);
        Assert.That((await AwaitPersistence(() => persistence.WriteTextAsync(Guid.NewGuid(), initial.StateVolumeId, owner, "value.txt", "confirmed",
            256 * 1024, epoch, time))).Success, Is.True);
        Assert.That(await AwaitPersistence(() => persistence.CompleteInstallationHealthAsync(initial, true, epoch, time)), Is.True);
        var second = await Publish(2);
        var candidate = await Install(second);
        Assert.Multiple(() =>
        {
            Assert.That(candidate.Id, Is.EqualTo(initial.Id));
            Assert.That(candidate.StateVolumeId, Is.Not.EqualTo(initial.StateVolumeId));
            Assert.That(candidate.Recovery?.Id, Is.EqualTo(first.Id));
            Assert.That(candidate.RecoveryStateVolumeId, Is.EqualTo(initial.StateVolumeId));
        });
        Assert.That(await AwaitPersistence(() => persistence.ReadTextAsync(candidate.StateVolumeId, owner, "value.txt")), Is.EqualTo("confirmed"));
        Assert.That((await AwaitPersistence(() => persistence.WriteTextAsync(Guid.NewGuid(), candidate.StateVolumeId, owner, "value.txt", "unproven",
            256 * 1024, epoch, time))).Success, Is.True);
        var third = await Publish(3);
        var thirdRequest = Guid.NewGuid();
        var unbooted = await Install(third, thirdRequest);
        Assert.That(unbooted.Recovery?.Id, Is.EqualTo(first.Id), "An unbooted candidate must not displace the proven fallback.");
        Assert.That(unbooted.RecoveryStateVolumeId, Is.EqualTo(initial.StateVolumeId));
        var stale = await AwaitPersistence(() => persistence.WriteTextAsync(Guid.NewGuid(), candidate.StateVolumeId, owner, "value.txt", "late",
            256 * 1024, epoch, time, () => Task.FromResult(true), new(host, device.AuthorityEpoch, candidate, device.AuthorityEpoch)));
        Assert.That(stale.Success, Is.False, "A native callback alone cannot authorize a replaced durable installation.");
        Assert.That(await AwaitPersistence(() => persistence.CompleteInstallationHealthAsync(unbooted, false, epoch, time)), Is.True);
        var recovered = (await AwaitPersistence(() => persistence.ReadInstallationsAsync(host, owner))).Single();
        Assert.Multiple(() =>
        {
            Assert.That(recovered.Active.Id, Is.EqualTo(first.Id));
            Assert.That(recovered.StateVolumeId, Is.EqualTo(initial.StateVolumeId));
            Assert.That(recovered.Recovery, Is.Null, "A failed image must not become the next automatic fallback.");
        });
        Assert.That(await AwaitPersistence(() => persistence.ReadTextAsync(recovered.StateVolumeId, owner, "value.txt")), Is.EqualTo("confirmed"));
        var guardCalls = 0;
        await Install(third, thirdRequest, () => { guardCalls++; return Task.FromResult(true); });
        Assert.That(guardCalls, Is.Zero, "A completed request must not stop a later healthy runtime or stage new state.");
        Assert.That((await AwaitPersistence(() => persistence.ReadInstallationsAsync(host, owner))).Single().Active.Id, Is.EqualTo(first.Id));
        var manualCandidate = await Install(second);
        Assert.That((await AwaitPersistence(() => persistence.RollbackInstallationAsync(Guid.NewGuid(), manualCandidate, epoch, time))).Success, Is.True);
        var manualRecovery = (await AwaitPersistence(() => persistence.ReadInstallationsAsync(host, owner))).Single();
        Assert.That(manualRecovery.Active.Id, Is.EqualTo(first.Id));
        Assert.That(manualRecovery.Recovery, Is.Null, "Manual rollback must not promote an unbooted image into the fallback slot.");
        var afterManual = await Install(third);
        Assert.That(afterManual.Recovery?.Id, Is.EqualTo(first.Id));
        Assert.That(afterManual.RecoveryStateVolumeId, Is.EqualTo(initial.StateVolumeId));
        Assert.That(await AwaitPersistence(() => persistence.CompleteInstallationHealthAsync(afterManual, false, epoch, time)), Is.True);
        Assert.That((await AwaitPersistence(() => persistence.ReadInstallationsAsync(host, owner))).Single().Active.Id, Is.EqualTo(first.Id));
        Assert.That(await AwaitPersistence(() => persistence.ReadTextAsync(initial.StateVolumeId, owner, "value.txt")), Is.EqualTo("confirmed"));
        var other = await Publish(4, "test.other");
        Assert.That((await AwaitPersistence(() => persistence.InstallAsync(Guid.NewGuid(), host, owner, project, other, [], false,
            epoch, time, 4 * 1024 * 1024))).Success, Is.True);
        Assert.That(await AwaitPersistence(() => persistence.ReadInstallationsAsync(host, owner)), Has.Length.EqualTo(2));
        Assert.That(await AwaitPersistence(() => persistence.ReadInstallationAsync(host, owner)), Is.Null, "Host-only selection must fail closed for multiple programs.");
        var quota = await AwaitPersistence(() => persistence.InstallAsync(Guid.NewGuid(), host, owner, project, other, [], false,
            epoch, time, storageBytes: 1));
        Assert.That(quota.Success, Is.False);
        Assert.That(await AwaitPersistence(() => persistence.ReadInstallationsAsync(host, owner)), Has.Length.EqualTo(2));
        await Server.WaitPost(() => Server.EntMan.EventBus.RaiseEvent(EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PostRound)));
        for (var attempt = 0; attempt < 180 && clock.IsReady; attempt++) await Server.WaitRunTicks(1);
        Assert.That(clock.IsReady, Is.False);
    }

    private Task<T> AwaitPersistence<T>(Func<Task<T>> start) => NCMetaNativeTestTasks.Run(Server, start);
}
