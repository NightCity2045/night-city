// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Linq;
using System.Threading.Tasks;
using Content.Meta.Bytecode;
using Content.Meta.Language;
using Content.Meta.Runtime;
using Content.Meta.Verification;
using Content.Server._NC.Meta.Commerce;
using Content.Server._NC.Meta.Authority;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Projects;
using Content.Server._NC.Meta.Runtime;
using Content.Server._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Preferences;
using Content.Shared._NC.Meta.UI;
using Content.Shared._NC.Meta.Authority;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Manager;

namespace Content.IntegrationTests.Tests._NC.Meta;

public sealed partial class NCMetaSoftwareCopyTest
{
    private sealed record RecoveryScene(Scene Physical, NCMetaProject Project, NCMetaRelease Release,
        VerifiedArtifact Artifact, NCMetaInstallation Installation, bool Licensed, string Slot, long Memory);

    private async Task<RecoveryScene> CreateRecoveryScene(string mode)
    {
        var physical = await CreatePublishedPhysicalImage("module test.piracy; fn main()->void{while(true){yield;}}",
            nativeOwner: true, hotFirmware: mode == "Firmware");
        var project = physical.Project;
        var release = physical.Release;
        var artifact = physical.Artifact;
        var licensed = mode != "Private";
        if (!licensed)
        {
            var device = (await _persistence.ReadDeviceAsync(physical.Deck.Id))!;
            project = (await _persistence.ReadProjectAsync(device.ActiveProject, physical.Owner))!;
            var compiled = new MetaCompiler().Compile(new([new("main.meta",
                "module test.swap; fn main()->void{while(true){yield;}}")], "test.swap", "test.swap.private", "1.0.0"));
            Assert.That(compiled.Artifact, Is.Not.Null, string.Join("\n", compiled.Diagnostics));
            Assert.That((await _persistence.PublishReleaseAsync(Guid.NewGuid(), project, compiled.Artifact!,
                "integration", _epoch, _time, compiled.Exports)).Success, Is.True);
            project = (await _persistence.ReadProjectAsync(project.Id, physical.Owner))!;
            release = project.Releases.Single();
            artifact = MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(MetaBytecodeLoader.Load(compiled.Artifact!)));
        }
        else await SeedPurchasedSeat(physical);
        var slot = mode == "Firmware" ? "Firmware" : "User";
        var memory = 1048576 - artifact.ResidentMemoryBytes;
        NCMetaMutationOutcome installed;
        if (licensed)
        {
            var admission = await NCMetaNativeTestTasks.Run(Server, () => _marketplace.PrepareActivationAsync(physical.Owner, physical.Deck.Id, physical.Product.Id, release.Id, true));
            Assert.That(admission, Is.Not.Null);
            installed = await _persistence.InstallLicensedAsync(Guid.NewGuid(), physical.Deck.Id, physical.Owner, physical.Product.Id,
                admission!, [], _epoch, _time, 4 * 1024 * 1024, slotKind: slot, autoStart: true,
                memoryLimitBytes: slot == "Firmware" ? memory : null);
        }
        else installed = await _persistence.InstallAsync(Guid.NewGuid(), physical.Deck.Id, physical.Owner, project,
            release, [], true, _epoch, _time, 4 * 1024 * 1024);
        Assert.That(installed.Success, Is.True, installed.MessageKey);
        var previous = (await _persistence.ReadInstallationsAsync(physical.Deck.Id, physical.Owner)).Single();
        Assert.That((await _persistence.WriteTextAsync(Guid.NewGuid(), previous.StateVolumeId, physical.Owner,
            "swap.txt", "before", 256 * 1024, _epoch, _time)).Success, Is.True);
        await Server.WaitAssertion(() => Assert.That(Server.System<SharedUserInterfaceSystem>()
            .TryOpenUi(physical.Deck.Entity, NCMetaUiKey.Workspace, physical.Actor), Is.True));
        await Server.WaitRunTicks(5);
        var approved = await ApproveServiceAsync(physical, previous, artifact, physical.Deck.Entity, physical.Actor);
        return new(physical, project, release, artifact, approved.Installation, licensed, slot, memory);
    }

    private Task<NCMetaRuntimeInstance> StartRecoveryInstallation(RecoveryScene scene, NCMetaInstallation installation) =>
        NCMetaNativeTestTasks.Run(Server, () => StartRecoveryInstallationNative(scene, installation));

    private async Task<NCMetaRuntimeInstance> StartRecoveryInstallationNative(RecoveryScene scene, NCMetaInstallation installation)
    {
        var runtime = Server.System<NCMetaRuntimeSystem>();
        var authorization = await _marketplace.ResolveRunAuthorizationAsync(scene.Physical.Owner, scene.Physical.Deck.Id, installation.Active);
        Assert.That(authorization, Is.Not.Null);
        var service = await ApproveServiceNative(scene.Physical, installation, scene.Artifact, scene.Physical.Deck.Entity, scene.Physical.Actor);
        installation = service.Installation;
        NCMetaRuntimeInstance running = null!;
        for (var attempt = 0; attempt < 180; attempt++)
        {
            var started = runtime.TryGetInstallation(scene.Physical.Deck.Entity, scene.Physical.Owner, installation.Id, out running!) ||
                    runtime.TryStart(scene.Physical.Deck.Entity, scene.Physical.Actor, installation, scene.Artifact, false,
                        authorization!.ExpiresAtTicks, authorization.Contact, service.Admission) &&
                    runtime.TryGetInstallation(scene.Physical.Deck.Entity, scene.Physical.Owner, installation.Id, out running!);
            if (started) return running;
            await Task.Delay(Server.Timing.TickPeriod);
        }
        Assert.Fail("The native fixture never admitted its durable running installation.");
        return null!;
    }

    private Task<(NCMetaInstallation Installation, NCMetaServiceAdmission Admission)> ApproveServiceAsync(Scene scene, NCMetaInstallation installation,
        VerifiedArtifact artifact, EntityUid host, EntityUid actor, bool unattended = true) =>
        NCMetaNativeTestTasks.Run(Server, () => ApproveServiceNative(scene, installation, artifact, host, actor, unattended));

    private async Task<(NCMetaInstallation Installation, NCMetaServiceAdmission Admission)> ApproveServiceNative(Scene scene, NCMetaInstallation installation,
        VerifiedArtifact artifact, EntityUid host, EntityUid actor, bool unattended = true)
    {
        Assert.That(Server.System<SharedUserInterfaceSystem>()
            .TryOpenUi(host, NCMetaUiKey.Workspace, actor), Is.True);
        var authority = Server.System<NCMetaAuthoritySystem>();
        var admission = await authority.ResolveServiceAdmissionAsync(installation, artifact, unattended);
        if (admission != null) return (installation, admission);
        Assert.That(installation.Bindings, Is.Empty, "This fixture explicitly approves a zero-link policy.");
        var policy = await _persistence.ReadServiceGrantPolicyAsync(installation.Id);
        var approved = await authority.TryIssueInstallationGrantsAsync(host, actor, host,
            Guid.NewGuid(), installation, artifact, new NCMetaGrantApprovalRequest(policy?.Revision ?? 0, true, unattended, null, []));
        Assert.That(approved.Success, Is.True, approved.MessageKey);
        var current = await _persistence.ReadInstallationAsync(installation.HostDeviceId, installation.Owner, installation.Id);
        Assert.That(current?.Active.Digest, Is.EqualTo(artifact.ContentHash));
        admission = await authority.ResolveServiceAdmissionAsync(current!, artifact, unattended);
        Assert.That(admission, Is.Not.Null);
        return (current!, admission!);
    }

    private async Task CreateNativeServiceOwnerAsync(NCActorId actorId, string name)
    {
        HumanoidCharacterProfile profile = null!;
        await Server.WaitPost(() => profile = HumanoidCharacterProfile.Random());
        var payload = NCHumanoidProfilePayload.Serialize(Server.ResolveDependency<ISerializationManager>(), profile);
        var command = Guid.NewGuid();
        var created = await Server.System<NCCityActorRegistrySystem>().CreateResidentAsync(new(command, actorId, name,
            new("NCLivingCityResident"), new("NCDefaultResidentPersona"), payload, default, null, command,
            AuditSource: "integration.meta-service-owner"));
        Assert.That(created.Status, Is.EqualTo(NCCityActorMutationStatus.Applied), created.Error);
    }

    [TestCase("Private", "Conflict")]
    [TestCase("Licensed", "Conflict")]
    [TestCase("Firmware", "Conflict")]
    [TestCase("Private", "Stop")]
    [TestCase("Licensed", "Newer")]
    [TestCase("Firmware", "Stop")]
    [TestCase("Private", "Exception")]
    public async Task NativeInstallationSwapReconcilesARealFailedVolumeCasWithoutRevivingLaterPolicy(string mode, string race)
    {
        var scene = await CreateRecoveryScene(mode);
        var physical = scene.Physical;
        var old = scene.Installation;
        var runtime = Server.System<NCMetaRuntimeSystem>();
        var workspace = Server.System<NCMetaWorkspaceSystem>();
        var hardware = Server.System<NCMetaHardwareSystem>();
        var original = await StartRecoveryInstallation(scene, old);
        NCMetaFirmwareServiceStamp? service = null;
        if (scene.Slot == "Firmware") await Server.WaitAssertion(() =>
        {
            Assert.That(hardware.IsMaintenance(physical.Deck.Entity), Is.False);
            Assert.That(hardware.TryCaptureFirmwareService(physical.Deck.Entity, physical.Actor, out var stamp), Is.True);
            service = stamp;
        });
        var admission = scene.Licensed ? await NCMetaNativeTestTasks.Run(Server, () => _marketplace.PrepareActivationAsync(physical.Owner, physical.Deck.Id,
            physical.Product.Id, scene.Release.Id, true)) : null;
        Assert.That(!scene.Licensed || admission != null, Is.True);
        var licenseBefore = admission?.ActivationWrite?.ExpectedAggregateVersion;
        var oldVolumeBefore = (await _persistence.ReadSnapshotAsync("meta.volume", old.StateVolumeId.ToString("D")))!.AggregateVersion;
        NCMetaInstallation? newer = null;
        NCMetaRuntimeInstance? newerRuntime = null;
        var guardCalls = 0;
        async Task<bool> CompeteAfterNativeStop(Func<Task<bool>> nativeGuard)
        {
            if (!await nativeGuard()) return false;
            guardCalls++;
            Assert.That(original.Process.State, Is.EqualTo(MetaProcessState.Stopped));
            Assert.That(runtime.TryGetGeneration(physical.Deck.Entity, physical.Owner, original.Generation, out _), Is.False);
            // This real write commits after candidate preparation and invalidates its old-volume CAS.
            Assert.That(_persistence.TryCaptureTime(out _epoch, out _time), Is.True);
            var changed = await _persistence.WriteTextAsync(Guid.NewGuid(), old.StateVolumeId, physical.Owner,
                "swap.txt", "committed concurrently", 256 * 1024, _epoch, _time);
            Assert.That(changed.Success, Is.True, changed.MessageKey);
            if (race == "Stop")
            {
                var device = (await _persistence.ReadDeviceAsync(physical.Deck.Id))!;
                var stop = new NCMetaWorkspaceRequestMessage(NCMetaWorkspaceOperation.Stop, Guid.NewGuid(),
                    deviceId: physical.Deck.Id, installationId: old.Id, projectId: device.ActiveProject)
                    { Actor = physical.Actor, UiKey = NCMetaUiKey.Workspace };
                SEntMan.EventBus.RaiseLocalEvent(physical.Deck.Entity, stop);
                var stopped = false;
                for (var attempt = 0; attempt < 180; attempt++)
                {
                    var current = await _persistence.ReadInstallationAsync(physical.Deck.Id, physical.Owner, old.Id);
                    if (current is { AutoStart: false } && current.Revision > old.Revision) { stopped = true; break; }
                    await Task.Delay(Server.Timing.TickPeriod);
                }
                Assert.That(stopped, Is.True, "The real native Stop request must commit before the rejected swap completes.");
            }
            else if (race == "Newer")
            {
                var fresh = await _marketplace.PrepareActivationAsync(physical.Owner, physical.Deck.Id, physical.Product.Id, scene.Release.Id, true);
                Assert.That(fresh, Is.Not.Null);
                Assert.That(_persistence.TryCaptureTime(out _epoch, out _time), Is.True);
                var replacement = await _persistence.InstallLicensedAsync(Guid.NewGuid(), physical.Deck.Id, physical.Owner,
                    physical.Product.Id, fresh!, [], _epoch, _time, 4 * 1024 * 1024, autoStart: true);
                Assert.That(replacement.Success, Is.True, replacement.MessageKey);
                newer = (await _persistence.ReadInstallationsAsync(physical.Deck.Id, physical.Owner)).Single();
                newerRuntime = await StartRecoveryInstallationNative(scene, newer);
            }
            else if (race == "Exception") throw new InvalidOperationException("native commit aborted after the competing durable write");
            return true;
        }
        Task<NCMetaMutationOutcome> Persist(Func<Task<bool>> guard) => scene.Licensed
            ? _persistence.InstallLicensedAsync(Guid.NewGuid(), physical.Deck.Id, physical.Owner, physical.Product.Id,
                admission!, [], _epoch, _time, 4 * 1024 * 1024, activationGuard: () => CompeteAfterNativeStop(guard),
                slotKind: scene.Slot, autoStart: true, memoryLimitBytes: scene.Slot == "Firmware" ? scene.Memory : null)
            : _persistence.InstallAsync(Guid.NewGuid(), physical.Deck.Id, physical.Owner, scene.Project, scene.Release,
                [], true, _epoch, _time, 4 * 1024 * 1024, activationGuard: () => CompeteAfterNativeStop(guard));
        var attemptSwap = NCMetaNativeTestTasks.Run(Server, () =>
            workspace.TryCommitInstallationSwapAsync(physical.Deck.Entity, physical.Actor, physical.Deck.Entity,
                scene.Artifact, [], scene.Slot, old, Persist, firmwareStamp: service));
        if (race == "Exception")
        {
            InvalidOperationException? failure = null;
            try { await attemptSwap; }
            catch (InvalidOperationException exception) { failure = exception; }
            Assert.That(failure?.Message, Does.Contain("native commit aborted"));
        }
        else
        {
            var outcome = await attemptSwap;
            Assert.That(outcome.Success, Is.False, "The actual prepared old-volume CAS must reject the candidate.");
        }
        Assert.That(guardCalls, Is.EqualTo(1));
        var durableAfter = (await _persistence.ReadInstallationsAsync(physical.Deck.Id, physical.Owner)).Single();
        Assert.That((await _persistence.ReadSnapshotAsync("meta.volume", old.StateVolumeId.ToString("D")))!.AggregateVersion,
            Is.GreaterThan(oldVolumeBefore));
        if (race == "Newer")
        {
            Assert.That(durableAfter.StateVolumeId, Is.EqualTo(newer!.StateVolumeId));
            Assert.That(durableAfter.Revision, Is.EqualTo(newer.Revision));
            await Server.WaitAssertion(() => Assert.That(runtime.TryGetGeneration(physical.Deck.Entity, physical.Owner,
                newerRuntime!.Generation, out _), Is.True, "A late recovery must preserve the newer running generation."));
        }
        else
        {
            Assert.That(durableAfter.Active.Digest, Is.EqualTo(old.Active.Digest));
            Assert.That(durableAfter.StateVolumeId, Is.EqualTo(old.StateVolumeId));
            Assert.That(await _persistence.ReadTextAsync(old.StateVolumeId, physical.Owner, "swap.txt"), Is.EqualTo("committed concurrently"));
            if (race == "Stop") await Server.WaitAssertion(() => Assert.That(runtime.TryGetInstallation(physical.Deck.Entity,
                physical.Owner, old.Id, out _), Is.False, "Manual Stop must cancel a pending fresh boot."));
            else await Server.WaitAssertion(() =>
            {
                Assert.That(runtime.TryGetInstallation(physical.Deck.Entity, physical.Owner, old.Id, out var recovered), Is.True);
                Assert.That(recovered.Generation, Is.Not.EqualTo(original.Generation));
                Assert.That(recovered.Process, Is.Not.SameAs(original.Process));
                Assert.That(original.Process.State, Is.EqualTo(MetaProcessState.Stopped));
            });
        }
        if (licenseBefore is { } revision && race != "Newer")
            Assert.That((await _persistence.ReadSnapshotAsync("meta.software.license", admission!.LicensedRelease.Entitlement!.Id.ToString("D")))!
                .AggregateVersion, Is.EqualTo(revision), "A rejected candidate must not consume a licensed activation.");
    }
}
