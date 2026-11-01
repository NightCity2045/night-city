// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
#if TOOLS && NC_DIAGNOSTICS
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.Meta.Commerce;
using Content.Meta.Hosting;
using Content.Meta.Language;
using Content.Meta.Verification;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Meta.Commerce;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Persistence.RoundSnapshot;
using Content.Shared._NC.Identity;
using Content.Shared._NC.Meta.UI;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.GameObjects;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Content.IntegrationTests.Tests._NC.Meta;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(NCMetaPersistenceSystem))]
public sealed class NCMetaReplayIntegrityTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = false };
    private NCMetaPersistenceSystem Persistence => Server.System<NCMetaPersistenceSystem>();
    private ServerDbManager Database => (ServerDbManager) Server.ResolveDependency<IServerDbManager>();
    private NCSessionEpoch _epoch;
    private NCActiveWorldTime _time;
    private bool _roundActive;

    private sealed record Scene(NCMetaDeviceRecord Device, NCMetaProject Project, NCMetaRelease Release,
        NCMetaInstallation Installation, Guid InstallRequest, NCMetaMutationOutcome InstallOutcome,
        MetaSoftwareRelease Descriptor);

    [Test]
    public async Task SeparatelyDeserializedImmutableReleaseIsAcceptedButChangedMetadataIsRejectedBeforeActivation()
    {
        try
        {
            var scene = await CreateScene();
            var project = scene.Project;
            const string main = "module test.identity; pub fn echo(value:int)->int{return value;} fn main()->void{}";
            const string helper = "module test.identity.helper; fn local()->void{}";
            var primary = project.Files.Single();
            foreach (var (id, path, text, revision) in new[]
            {
                (primary.Id, primary.Path, main, primary.Revision),
                (Guid.NewGuid(), "src/helper.meta", helper, 0L),
                (Guid.NewGuid(), "data/one.txt", "one", 0L),
                (Guid.NewGuid(), "data/two.txt", "two", 0L),
            })
            {
                var saved = await Native(() => Persistence.SaveSourceAsync(Guid.NewGuid(), project.Id, id, project.Owner,
                    revision, path, text, _epoch, _time));
                Assert.That(saved.Success, Is.True, saved.MessageKey);
                project = (await Native(() => Persistence.ReadProjectAsync(project.Id, project.Owner)))!;
            }
            var compiled = new MetaCompiler().Compile(new([new(primary.Path, main), new("src/helper.meta", helper)],
                "test.identity", "test.replay", "1.0.9"));
            Assert.That(compiled.Success, Is.True, string.Join("\n", compiled.Diagnostics));
            Assert.That((await Native(() => Persistence.PublishReleaseAsync(Guid.NewGuid(), project, compiled.Artifact!, MetaCompiler.BuildId,
                _epoch, _time, compiled.Exports))).Success, Is.True);
            project = (await Native(() => Persistence.ReadProjectAsync(project.Id, project.Owner)))!;
            var canonical = project.Releases.Last();
            var decoded = JsonSerializer.Deserialize<NCMetaRelease>(JsonSerializer.Serialize(canonical))!;
            var exports = decoded.Exports!;
            var sources = decoded.Sources!;
            var resources = decoded.Resources!;
            Assert.That(decoded, Is.Not.SameAs(canonical));
            Assert.That(decoded.Sources, Has.Length.EqualTo(2));
            Assert.That(decoded.Resources, Has.Length.EqualTo(2));
            Assert.That(exports.Functions, Has.Length.EqualTo(1));
            var guards = 0;
            var accepted = await Native(() => Persistence.InstallAsync(Guid.NewGuid(), scene.Device.DeviceId, project.Owner,
                project, decoded, [], false, _epoch, _time, 4 * 1024 * 1024,
                activationGuard: () => { guards++; return Task.FromResult(true); }));
            Assert.That(accepted.Success, Is.True, accepted.MessageKey);
            Assert.That(guards, Is.EqualTo(1));
            var before = JsonSerializer.Serialize(await Native(() => Persistence.ReadInstallationsAsync(scene.Device.DeviceId, project.Owner)));
            var function = exports.Functions.Single();
            var mutations = new (string Name, NCMetaRelease Release)[]
            {
                ("artifact", decoded with { Digest = new string('f', 64) }),
                ("source revision", decoded with { SourceRevision = decoded.SourceRevision + 1 }),
                ("compiler", decoded with { CompilerBuild = decoded.CompilerBuild + ".changed" }),
                ("export source", decoded with { Exports = exports with { SourceTreeHash = new string('f', 64) } }),
                ("export artifact", decoded with { Exports = exports with { ArtifactDigest = new string('f', 64) } }),
                ("export parameter", decoded with { Exports = exports with { Functions = [function with { ParameterTypes = ["float"] }] } }),
                ("export type", decoded with { Exports = exports with { Types = [new(0, "changed")] } }),
                ("export constant", decoded with { Exports = exports with { Constants = [new(0, "changed")] } }),
                ("resource order", decoded with { Resources = resources.Reverse().ToArray() }),
                ("source order", decoded with { Sources = sources.Reverse().ToArray() }),
                ("source digest", decoded with { Sources = [sources[0] with { Digest = new string('f', 64) }, sources[1]] }),
                ("dependency", decoded with { Dependencies = [new(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), new string('b', 64), "other.library", "1.0.0")] }),
                ("commercial origin", decoded with { CommercialOrigin = new(Guid.NewGuid(), 1, project.Id, canonical.Id) }),
                ("build authorization", decoded with { BuildAuthorization = new(project.Owner, scene.Device.DeviceId, canonical.Digest, new string('a', 64)) }),
            };
            foreach (var (name, changed) in mutations)
            {
                var request = Guid.NewGuid();
                var rejected = await Native(() => Persistence.InstallAsync(request, scene.Device.DeviceId, project.Owner,
                    project, changed, [], false, _epoch, _time, 4 * 1024 * 1024,
                    activationGuard: () => { guards++; return Task.FromResult(true); }));
                Assert.That(rejected, Is.EqualTo(new NCMetaMutationOutcome(false, "nc-meta-error-access")), name);
                Assert.That(await Native(() => Database.GetNCPersistentCommandResultAsync(request)), Is.Null, name);
            }
            Assert.That(guards, Is.EqualTo(1), "Changed metadata must not enter the native activation guard.");
            Assert.That(JsonSerializer.Serialize(await Native(() => Persistence.ReadInstallationsAsync(scene.Device.DeviceId, project.Owner))), Is.EqualTo(before));
        }
        finally { await EndRound(); }
    }

    [Test]
    public async Task ImmutableOrdinaryAndLicensedAcknowledgementsDoNotReactivateAnOlderImage()
    {
        try
        {
            var scene = await CreateScene();
            await SelectCheckpoint(scene);
            var updatedProject = await Publish(scene.Project, 2);
            var second = updatedProject.Releases.Last();
            Assert.That((await Native(() => Persistence.InstallAsync(Guid.NewGuid(), scene.Device.DeviceId, scene.Device.Owner,
                updatedProject, second, [], false, _epoch, _time, 4 * 1024 * 1024))).Success, Is.True);
            var receipt = await Receipt(scene.InstallRequest);
            var evidence = await Evidence(receipt);
            var guards = 0;
            var replay = await Native(() => Persistence.InstallAsync(scene.InstallRequest, scene.Device.DeviceId, scene.Device.Owner,
                scene.Project, scene.Release, [], false, _epoch, _time, 4 * 1024 * 1024,
                activationGuard: () => { guards++; return Task.FromResult(false); }));
            Assert.That(replay, Is.EqualTo(scene.InstallOutcome));
            Assert.That(guards, Is.Zero, "A historical ACK must not stop/stage the current runtime again.");
            Assert.That(await Evidence(receipt), Is.EqualTo(evidence));
            Assert.That((await Native(() => Persistence.ReadInstallationsAsync(scene.Device.DeviceId, scene.Device.Owner))).Single().Active.Id,
                Is.EqualTo(second.Id));

            // Publisher-owned admission has no consumer seat. The descriptor is compiled/verified above;
            // the real installer still authors the receipt, journal attestations, state and index atomically.
            var product = Guid.NewGuid();
            var admission = new NCMetaSoftwareActivationAdmission(new(scene.Project.Id, scene.Release, scene.Descriptor, null),
                null, scene.Device.AuthorityEpoch);
            var request = Guid.NewGuid();
            var licensed = await Native(() => Persistence.InstallLicensedAsync(request, scene.Device.DeviceId, scene.Device.Owner,
                product, admission, [], _epoch, _time, 4 * 1024 * 1024));
            Assert.That(licensed.Success, Is.True, licensed.MessageKey);
            Assert.That((await Native(() => Persistence.InstallAsync(Guid.NewGuid(), scene.Device.DeviceId, scene.Device.Owner,
                updatedProject, second, [], false, _epoch, _time, 4 * 1024 * 1024))).Success, Is.True);
            var licensedReceipt = await Receipt(request);
            evidence = await Evidence(licensedReceipt);
            replay = await Native(() => Persistence.InstallLicensedAsync(request, scene.Device.DeviceId, scene.Device.Owner,
                product, admission, [], _epoch, _time, 4 * 1024 * 1024,
                activationGuard: () => { guards++; return Task.FromResult(false); }));
            Assert.That(replay, Is.EqualTo(licensed));
            Assert.That(guards, Is.Zero);
            Assert.That(await Evidence(licensedReceipt), Is.EqualTo(evidence));
            Assert.That((await Native(() => Persistence.ReadInstallationsAsync(scene.Device.DeviceId, scene.Device.Owner))).Single().Active.Id,
                Is.EqualTo(second.Id));
        }
        finally { await EndRound(); }
    }

    [Test]
    public async Task CompletedReceiptRequiresTheExactCommandScopePrincipalFingerprintAndStatus()
    {
        try
        {
            var scene = await CreateScene();
            var receipt = await Receipt(scene.InstallRequest);
            var fingerprint = receipt.RequestFingerprint!;
            var owner = scene.Device.Owner;
            var evidence = await Evidence(receipt);
            Assert.That(await Native(() => Persistence.CanReplayCommandAsync(receipt, scene.InstallRequest,
                receipt.CommandType, owner, fingerprint)), Is.True);
            var mismatches = new (string Name, NCPersistentCommandResultData Receipt)[]
            {
                ("command", receipt with { CommandId = Guid.NewGuid() }),
                ("type", receipt with { CommandType = "meta.project.write" }),
                ("scope type", receipt with { ScopeType = "meta.transaction" }),
                ("scope principal", receipt with { ScopeId = NCPartyRef.ForCharacter(new NCCharacterId(736)).ToString() }),
                ("fingerprint", receipt with { RequestFingerprint = new string('f', 64) }),
                ("no participants", receipt with { Aggregates = [] }),
                ("duplicate participant", receipt with { Aggregates = [receipt.Aggregates[0], receipt.Aggregates[0]] }),
                ("future participant", receipt with { Aggregates = [receipt.Aggregates[0] with { AggregateVersion = long.MaxValue }] }),
                ("null payload", receipt with { ResultPayload = null }),
            };
            foreach (var (name, candidate) in mismatches)
                Assert.That(await Native(() => Persistence.CanReplayCommandAsync(candidate, scene.InstallRequest,
                    receipt.CommandType, owner, fingerprint)), Is.False, name);
            foreach (var status in Enum.GetValues<NCPersistentMutationStatus>().Where(value => value != NCPersistentMutationStatus.DuplicateCompleted))
                Assert.That(await Native(() => Persistence.CanReplayCommandAsync(receipt with { Status = status }, scene.InstallRequest,
                    receipt.CommandType, owner, fingerprint)), Is.False, status.ToString());
            Assert.That(await Native(() => Persistence.CanReplayCommandAsync(receipt, Guid.NewGuid(), receipt.CommandType, owner, fingerprint)), Is.False);
            Assert.That(await Native(() => Persistence.CanReplayCommandAsync(receipt, scene.InstallRequest, receipt.CommandType, owner + "x", fingerprint)), Is.False);
            // A paid scope must come from the native transaction calculation, never the stored scope id.
            Assert.That(await Native(() => Persistence.CanReplayCommandAsync(receipt, scene.InstallRequest, receipt.CommandType,
                owner, fingerprint, "meta.transaction", Guid.NewGuid().ToString("D"))), Is.False);
            Assert.That(await Native(() => Persistence.CanReplayCommandAsync(receipt, scene.InstallRequest, receipt.CommandType,
                owner, fingerprint, "meta.transaction")), Is.False);
            Assert.That(await Evidence(receipt), Is.EqualTo(evidence), "Read-only replay authentication may not author a new outcome.");
        }
        finally { await EndRound(); }
    }

    [TestCase("ParticipantPayload")]
    [TestCase("MiddleEvent")]
    [TestCase("MissingAttestation")]
    public async Task ReplayRejectsActualProviderDamageBeyondTheSelectedCheckpointWithoutExecutingAgain(string fault)
    {
        Func<Task>? repair = null;
        try
        {
            var scene = await CreateScene();
            await SelectCheckpoint(scene);
            // SetAutoStart does not load the installation body. All three real post-checkpoint
            // journal versions must be authenticated by the first replay read, not a warmed cache.
            var current = scene.Installation;
            for (var index = 0; index < 3; index++)
            {
                var enabled = !current.AutoStart;
                Assert.That(await Native(() => Persistence.SetAutoStartAsync(Guid.NewGuid(), current, enabled, _epoch, _time)), Is.True);
                current = current with { AutoStart = enabled, Revision = current.Revision + 1 };
            }
            var receipt = await Receipt(scene.InstallRequest);
            var participant = receipt.Aggregates.Single(value => value.AggregateType == NCMetaPersistenceSystem.InstallationType);
            var original = (await Native(() => Database.GetNCAggregateSnapshotAsync(participant.AggregateType, participant.AggregateId)))!;
            Assert.That(original.AggregateVersion, Is.EqualTo(participant.AggregateVersion + 3));
            if (fault == "ParticipantPayload")
                repair = await ChangeSnapshotPayload(participant.AggregateType, participant.AggregateId, original.Payload + " ");
            else
            {
                var version = fault == "MiddleEvent" ? original.AggregateVersion - 1 : original.AggregateVersion;
                var events = await Native(() => Database.GetNCPersistentEventPageAsync(participant.AggregateType, participant.AggregateId, null, null, 16));
                var entry = events.Single(value => value.AggregateVersion == version);
                var payload = JsonNode.Parse(entry.Payload)!.AsObject();
                Assert.That(payload.ContainsKey(NCMetaJournalIntegrity.Field), Is.True, "The atomic writer must have authored a real attestation.");
                if (fault == "MiddleEvent") payload[NCMetaJournalIntegrity.Field]!["PreviousHash"] = new string('0', 64);
                else payload.Remove(NCMetaJournalIntegrity.Field);
                repair = await ChangeEventPayload(entry.EventId, entry.Payload, payload.ToJsonString());
            }
            var damagedEvidence = await Evidence(receipt);
            Assert.That(await Native(() => Persistence.CanReplayCommandAsync(receipt, scene.InstallRequest, receipt.CommandType,
                scene.Device.Owner, receipt.RequestFingerprint!)), Is.False);
            var guards = 0;
            var replay = await Native(() => Persistence.InstallAsync(scene.InstallRequest, scene.Device.DeviceId, scene.Device.Owner,
                scene.Project, scene.Release, [], false, _epoch, _time, 4 * 1024 * 1024,
                activationGuard: () => { guards++; return Task.FromResult(true); }));
            Assert.That(replay.Success, Is.False);
            Assert.That(guards, Is.Zero, "Unauthenticated ACKs must not fall through into physical stop/activation work.");
            Assert.That(await Evidence(receipt), Is.EqualTo(damagedEvidence), "Rejected replay must not repair or append over the damaged participant.");
            await repair();
            repair = null;
            Assert.That(await Native(() => Persistence.CanReplayCommandAsync(receipt, scene.InstallRequest, receipt.CommandType,
                scene.Device.Owner, receipt.RequestFingerprint!)), Is.True, "Restoring the exact fixture rows must restore authentication.");
            Assert.That((await Native(() => Persistence.ReadInstallationsAsync(scene.Device.DeviceId, scene.Device.Owner))).Single().AutoStart,
                Is.EqualTo(current.AutoStart));
        }
        finally
        {
            try { if (repair != null) await repair(); }
            finally { await EndRound(); }
        }
    }

    [Test]
    public async Task JsonNullCompletedResultNeverFallsThroughToASecondActivation()
    {
        Func<Task>? repair = null;
        try
        {
            var scene = await CreateScene();
            var receipt = await Receipt(scene.InstallRequest);
            repair = await ChangeReceiptDomainResult(scene.InstallRequest, "null");
            var nullReceipt = await Receipt(scene.InstallRequest);
            Assert.That(nullReceipt.ResultPayload, Is.EqualTo("null"));
            var evidence = await Evidence(nullReceipt);
            var guards = 0;
            var result = await Native(() => Persistence.InstallAsync(scene.InstallRequest, scene.Device.DeviceId, scene.Device.Owner,
                scene.Project, scene.Release, [], false, _epoch, _time, 4 * 1024 * 1024,
                activationGuard: () => { guards++; return Task.FromResult(true); }));
            Assert.That(result, Is.EqualTo(new NCMetaMutationOutcome(false, "nc-meta-error-storage")));
            Assert.That(guards, Is.Zero);
            Assert.That(await Evidence(nullReceipt), Is.EqualTo(evidence));
            await repair();
            repair = null;
            Assert.That(await Native(() => Persistence.InstallAsync(scene.InstallRequest, scene.Device.DeviceId, scene.Device.Owner,
                scene.Project, scene.Release, [], false, _epoch, _time, 4 * 1024 * 1024)), Is.EqualTo(scene.InstallOutcome));
            Assert.That((await Receipt(scene.InstallRequest)).ResultPayload, Is.EqualTo(receipt.ResultPayload));
        }
        finally
        {
            try { if (repair != null) await repair(); }
            finally { await EndRound(); }
        }
    }

    [Test]
    public async Task ProjectAndDevelopmentImportEarlyAcknowledgementsAuthenticateTheirRealParticipants()
    {
        try
        {
            var scene = await CreateScene();
            var request = Guid.NewGuid();
            Task<NCMetaMutationOutcome> Rename() => Persistence.ManageProjectAsync(NCMetaWorkspaceOperation.RenameProject, request,
                scene.Device, scene.Project, Guid.Empty, scene.Project.Revision, "Renamed once", _epoch, _time);
            var renamed = await Native(Rename);
            Assert.That(renamed.Success, Is.True, renamed.MessageKey);
            var receipt = await Receipt(request);
            var evidence = await Evidence(receipt);
            Assert.That(await Native(Rename), Is.EqualTo(renamed));
            Assert.That(await Evidence(receipt), Is.EqualTo(evidence));
            await WithSnapshotFault(NCMetaPersistenceSystem.ProjectType, scene.Project.Id.ToString("D"), async () =>
            {
                var damaged = await Evidence(receipt);
                Assert.That((await Native(Rename)).Success, Is.False);
                Assert.That(await Evidence(receipt), Is.EqualTo(damaged));
            });

            // Enter the already-settled source-transfer boundary with actual protected claim/inbox rows.
            // Financial settlement itself is exercised by the separate native development-contract cases.
            var claim = new NCMetaDevelopmentClaim(Guid.NewGuid(), Guid.NewGuid(), scene.Device.Owner, "Delivered source",
                scene.Release.Digest, scene.Descriptor.ContainerDigest, new string('a', 64), true, scene.Release,
                [Guid.NewGuid()], Guid.NewGuid(), 1);
            var seed = await Native(() => Database.ApplyNCPersistentBatchMutationAsync(new(Guid.NewGuid(), "test.replay.delivery",
                "meta.owner", scene.Device.Owner,
                [new(Guid.NewGuid(), "test.delivery.claim", "meta.development.delivery-claim", claim.Id.ToString("D"), 0, 1,
                    JsonSerializer.Serialize(claim), "{}"),
                 new(Guid.NewGuid(), "test.delivery.inbox", "meta.development.inbox", scene.Device.Owner, 0, 1,
                    JsonSerializer.Serialize(new NCMetaDevelopmentInbox(scene.Device.Owner, [claim.Id], 1)), "{}")],
                "{}", Guid.NewGuid(), null, _epoch, _time, [])));
            Assert.That(seed.Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
            var readClaim = await Native(() => Persistence.ReadDevelopmentClaimAsync(claim.Id, scene.Device.Owner));
            Assert.That(readClaim, Is.Not.Null);
            var authority = new NCMetaAuthorityConstraints(null, [new(scene.Device.DeviceId, scene.Device.Owner, scene.Device.AuthorityEpoch)]);
            var importRequest = Guid.NewGuid();
            Task<NCMetaMutationOutcome> Import() => Persistence.ImportDevelopmentSourceAsync(importRequest, scene.Device, readClaim!, _epoch, _time, authority);
            var imported = await Native(Import);
            Assert.That(imported.Success, Is.True, imported.MessageKey);
            var importReceipt = await Receipt(importRequest);
            evidence = await Evidence(importReceipt);
            Assert.That((await Native(Import)).Success, Is.True);
            Assert.That(await Evidence(importReceipt), Is.EqualTo(evidence));
            var importedProject = importReceipt.Aggregates.Single(value => value.AggregateType == NCMetaPersistenceSystem.ProjectType);
            await WithSnapshotFault(importedProject.AggregateType, importedProject.AggregateId, async () =>
            {
                var damaged = await Evidence(importReceipt);
                Assert.That((await Native(Import)).Success, Is.False);
                Assert.That(await Evidence(importReceipt), Is.EqualTo(damaged), "Import retry must not create another workspace.");
            });
            Assert.That((await Native(() => Persistence.ReadDeviceAsync(scene.Device.DeviceId)))!.Projects.Length,
                Is.EqualTo(scene.Device.Projects.Length + 1));
        }
        finally { await EndRound(); }
    }

    [Test]
    public async Task GrantAcknowledgementSurvivesRevocationWithoutRestoringTheGrantAndRejectsDamagedPolicy()
    {
        try
        {
            var scene = await CreateScene();
            var policy = new MetaServiceGrantPolicy(scene.Installation.Id, scene.Device.DeviceId, scene.Device.Owner,
                scene.Descriptor.ContainerDigest, scene.Device.AuthorityEpoch, true, [], scene.Device.Owner, _time.Ticks, 1);
            var fingerprint = NCMetaPersistenceSystem.Fingerprint("replay-fixture-issue", policy.InstallationId.ToString("D"));
            var issuedId = Guid.NewGuid();
            async Task Commit(MetaServiceGrantPolicy value, long expected, Guid request, string type, string hash)
            {
                var write = await Native(() => Persistence.PrepareServiceGrantPolicyWriteAsync(value, expected));
                Assert.That(write, Is.Not.Null);
                var current = await Native(() => Persistence.ReadInstallationAsync(scene.Device.DeviceId, scene.Device.Owner, scene.Installation.Id));
                Assert.That(current, Is.Not.Null);
                var attestation = await Native(() => Persistence.PrepareServiceGrantInstallationAttestationAsync(current!));
                Assert.That(attestation, Is.Not.Null);
                var result = await Native(() => Database.ApplyNCPersistentBatchMutationAsync(new(request, type, "meta.owner", scene.Device.Owner,
                    [write!, attestation!], JsonSerializer.Serialize(new NCMetaMutationOutcome(true, "nc-meta-saved", value.Revision)),
                    request, null, _epoch, _time, [], RequestFingerprint: hash)));
                Assert.That(result.Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
            }
            await Commit(policy, 0, issuedId, "meta.grants.issue", fingerprint);
            Assert.That((await Native(() => Persistence.ReadServiceGrantReplayAsync(issuedId, scene.Device.Owner, fingerprint, false)))?.Success, Is.True);
            await Commit(policy with { Revision = 2, Revoked = true }, 1, Guid.NewGuid(), "meta.grants.revoke",
                NCMetaPersistenceSystem.Fingerprint("revoke-fixture", policy.InstallationId.ToString("D")));
            var receipt = await Receipt(issuedId);
            var evidence = await Evidence(receipt);
            Assert.That((await Native(() => Persistence.ReadServiceGrantReplayAsync(issuedId, scene.Device.Owner, fingerprint, false)))?.Success, Is.True);
            Assert.That((await Native(() => Persistence.ReadServiceGrantReplayAsync(issuedId, scene.Device.Owner, fingerprint, true)))?.Success, Is.False);
            Assert.That((await Native(() => Persistence.ReadServiceGrantReplayAsync(issuedId, scene.Device.Owner, "wrong", false)))?.Success, Is.False);
            Assert.That(await Evidence(receipt), Is.EqualTo(evidence));
            // Even a previously authenticated revision must reject a changed provider payload.
            await WithSnapshotFault(NCMetaPersistenceSystem.ServiceGrantType, policy.InstallationId.ToString("D"), async () =>
            {
                var damaged = await Evidence(receipt);
                Assert.That((await Native(() => Persistence.ReadServiceGrantReplayAsync(issuedId, scene.Device.Owner, fingerprint, false)))?.Success, Is.False);
                Assert.That(await Evidence(receipt), Is.EqualTo(damaged));
            });
            Assert.That((await Native(() => Persistence.ReadServiceGrantPolicyAsync(policy.InstallationId)))!.Revoked, Is.True);
        }
        finally { await EndRound(); }
    }

    private Task<T> Native<T>(Func<Task<T>> start) => NCMetaNativeTestTasks.Run(Server, start);

    private async Task<Scene> CreateScene()
    {
        await StartRound();
        var owner = NCPartyRef.ForCharacter(new NCCharacterId(735)).ToString();
        var device = await Native(() => Persistence.ClaimDeviceAsync(Guid.NewGuid(), Guid.NewGuid().ToString("D"), owner,
            "Replay integrity", _epoch, _time));
        Assert.That(device, Is.Not.Null);
        var project = (await Native(() => Persistence.ReadProjectAsync(device!.ActiveProject, owner)))!;
        project = await Publish(project, 1);
        var release = project.Releases.Single();
        var bytes = (await Native(() => Persistence.ReadArtifactAsync(release.Digest)))!;
        var verified = MetaVerifier.Verify(bytes, MetaApiCatalog.Default.CreateVerificationCatalog(Content.Meta.Bytecode.MetaBytecodeLoader.Load(bytes)));
        var descriptor = new MetaSoftwareRelease(release.Id, release.Digest, "test.replay", "1.0.1", verified.Manifest.RuntimeAbi,
            verified.Manifest.ApiCatalogHash, verified.Manifest.RequiredScopes, [], verified.ResidentMemoryBytes, _time.Ticks, "")
            { ContainerDigest = verified.Digest, PackageBytes = bytes.Length };
        var request = Guid.NewGuid();
        var installed = await Native(() => Persistence.InstallAsync(request, device!.DeviceId, owner, project, release, [], false,
            _epoch, _time, 4 * 1024 * 1024));
        Assert.That(installed.Success, Is.True, installed.MessageKey);
        var installation = (await Native(() => Persistence.ReadInstallationsAsync(device!.DeviceId, owner))).Single();
        return new(device!, project, release, installation, request, installed, descriptor);
    }

    private async Task<NCMetaProject> Publish(NCMetaProject project, int revision)
    {
        var compiled = new MetaCompiler().Compile(new([new("main.meta", $"module test.replay; fn main()->void{{let value:int={revision};}}")],
            "test.replay", "test.replay", $"1.0.{revision}"));
        Assert.That(compiled.Success, Is.True, string.Join("\n", compiled.Diagnostics));
        var published = await Native(() => Persistence.PublishReleaseAsync(Guid.NewGuid(), project, compiled.Artifact!, MetaCompiler.BuildId,
            _epoch, _time, compiled.Exports));
        Assert.That(published.Success, Is.True, published.MessageKey);
        return (await Native(() => Persistence.ReadProjectAsync(project.Id, project.Owner)))!;
    }

    private async Task StartRound()
    {
        var clock = Server.System<NCActiveWorldTimeSystem>();
        await Server.WaitPost(() => SEntMan.EventBus.RaiseEvent(EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        _roundActive = true;
        await Until(() => clock.IsReady);
        await Server.WaitAssertion(() => Assert.That(Persistence.TryCaptureTime(out _epoch, out _time), Is.True));
    }

    private async Task SelectCheckpoint(Scene scene)
    {
        var ending = _epoch;
        await EndRound();
        var coordinator = Server.System<NCRoundSnapshotCoordinatorSystem>();
        Guid selected = default;
        await Server.WaitAssertion(() =>
        {
            Assert.That(coordinator.State, Is.EqualTo(NCRoundSnapshotCoordinatorState.Committed), coordinator.LastError);
            selected = coordinator.LastSnapshotId;
            Assert.That(selected, Is.Not.EqualTo(Guid.Empty));
        });
        var sections = await Native(() => Database.GetNCRoundSnapshotSectionsAsync(selected, "meta-aggregates", "world", "city"));
        Assert.That(sections.Count, Is.GreaterThan(0));
        var checkpointRecords = sections.SelectMany(section => JsonSerializer.Deserialize<NCAggregateSnapshotData[]>(section.Payload)!).ToArray();
        var receipt = await Receipt(scene.InstallRequest);
        foreach (var participant in receipt.Aggregates.Where(value => value.AggregateType.StartsWith("meta.", StringComparison.Ordinal)))
        {
            var anchored = checkpointRecords.Single(record => record.AggregateType == participant.AggregateType && record.AggregateId == participant.AggregateId);
            var actual = await Native(() => Database.GetNCAggregateSnapshotAsync(participant.AggregateType, participant.AggregateId));
            Assert.That(anchored.AggregateVersion, Is.GreaterThanOrEqualTo(participant.AggregateVersion));
            Assert.That(anchored, Is.EqualTo(actual), "The selected checkpoint must contain this exact receipt participant, not merely unrelated META rows.");
        }
        await StartRound();
        await Server.WaitAssertion(() =>
        {
            Assert.That(_epoch, Is.Not.EqualTo(ending));
            Assert.That(Server.System<NCMetaSnapshotSystem>().SelectedSnapshotId, Is.EqualTo(selected));
        });
    }

    private async Task EndRound()
    {
        if (!_roundActive) return;
        await Server.WaitPost(() => SEntMan.EventBus.RaiseEvent(EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.InRound, GameRunLevel.PreRoundLobby)));
        await Until(() => !Server.System<NCActiveWorldTimeSystem>().IsReady);
        _roundActive = false;
        await Until(() => Server.System<NCRoundSnapshotCoordinatorSystem>().State is NCRoundSnapshotCoordinatorState.Committed or NCRoundSnapshotCoordinatorState.Failed);
        await Server.WaitAssertion(() => Assert.That(Server.System<NCRoundSnapshotCoordinatorSystem>().State,
            Is.EqualTo(NCRoundSnapshotCoordinatorState.Committed), Server.System<NCRoundSnapshotCoordinatorSystem>().LastError));
    }

    private async Task Until(Func<bool> predicate)
    {
        var elapsed = Stopwatch.StartNew();
        for (var tick = 0; tick < 1800 && elapsed.Elapsed < TimeSpan.FromSeconds(30); tick++)
        {
            var remaining = TimeSpan.FromSeconds(30) - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            var ready = false;
            await Server.WaitPost(() => ready = predicate()).WaitAsync(remaining);
            if (ready) return;
            remaining = TimeSpan.FromSeconds(30) - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            await Server.WaitRunTicks(1).WaitAsync(remaining);
        }
        Assert.Fail("Native lifecycle did not reach the expected state within 30 seconds/1800 ticks.");
    }

    private async Task<NCPersistentCommandResultData> Receipt(Guid request)
    {
        var receipt = await Native(() => Database.GetNCPersistentCommandResultAsync(request));
        Assert.That(receipt?.Status, Is.EqualTo(NCPersistentMutationStatus.DuplicateCompleted));
        Assert.That(receipt!.Aggregates, Is.Not.Empty);
        return receipt;
    }

    private Task<string> Evidence(NCPersistentCommandResultData receipt) => Native(async () =>
    {
        var values = new List<object?> { await Database.GetNCPersistentCommandResultAsync(receipt.CommandId) };
        foreach (var participant in receipt.Aggregates)
        {
            values.Add(await Database.GetNCAggregateSnapshotAsync(participant.AggregateType, participant.AggregateId));
            values.Add(await Database.GetNCPersistentEventPageAsync(participant.AggregateType, participant.AggregateId, null, null, 32));
        }
        return JsonSerializer.Serialize(values);
    });

    private Task Fault(Action<SqliteServerDbContext> change) => Native(async () =>
    {
        // Actual provider semaphore + connection. No native API awaits or callbacks inside the EF lease.
        await Database.ApplyMetaIsolatedSqliteFaultAsync(change);
        return true;
    });

    private async Task<Func<Task>> ChangeSnapshotPayload(string type, string id, string changed)
    {
        var original = (await Native(() => Database.GetNCAggregateSnapshotAsync(type, id)))!.Payload;
        await Fault(context => Assert.That(context.NCAggregateSnapshots.Where(value => value.AggregateType == type && value.AggregateId == id)
            .ExecuteUpdate(update => update.SetProperty(value => value.Payload, changed)), Is.EqualTo(1)));
        return () => Fault(context => Assert.That(context.NCAggregateSnapshots.Where(value => value.AggregateType == type && value.AggregateId == id)
            .ExecuteUpdate(update => update.SetProperty(value => value.Payload, original)), Is.EqualTo(1)));
    }

    private async Task<Func<Task>> ChangeEventPayload(Guid eventId, string original, string changed)
    {
        await Fault(context => Assert.That(context.NCEventJournal.Where(value => value.EventId == eventId)
            .ExecuteUpdate(update => update.SetProperty(value => value.Payload, changed)), Is.EqualTo(1)));
        return () => Fault(context => Assert.That(context.NCEventJournal.Where(value => value.EventId == eventId)
            .ExecuteUpdate(update => update.SetProperty(value => value.Payload, original)), Is.EqualTo(1)));
    }

    private async Task<Func<Task>> ChangeReceiptDomainResult(Guid request, string result)
    {
        var original = "";
        await Fault(context =>
        {
            original = context.NCCommandReceipts.AsNoTracking().Single(value => value.CommandId == request).ResultPayload!;
            var envelope = JsonNode.Parse(original)!.AsObject();
            envelope["DomainResultPayload"] = result;
            var changed = envelope.ToJsonString();
            Assert.That(context.NCCommandReceipts.Where(value => value.CommandId == request)
                .ExecuteUpdate(update => update.SetProperty(value => value.ResultPayload, changed)), Is.EqualTo(1));
        });
        return () => Fault(context => Assert.That(context.NCCommandReceipts.Where(value => value.CommandId == request)
            .ExecuteUpdate(update => update.SetProperty(value => value.ResultPayload, original)), Is.EqualTo(1)));
    }

    private async Task WithSnapshotFault(string type, string id, Func<Task> assertion)
    {
        var current = (await Native(() => Database.GetNCAggregateSnapshotAsync(type, id)))!;
        var repair = await ChangeSnapshotPayload(type, id, current.Payload + " ");
        try { await assertion(); }
        finally { await repair(); }
    }
}
#endif
