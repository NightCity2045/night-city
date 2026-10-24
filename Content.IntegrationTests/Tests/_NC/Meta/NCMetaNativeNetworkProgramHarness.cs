// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// The physical scene/owner builder is composed by the test fixture, not mocked here.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Meta.Language;
using Content.Meta.Verification;
using Content.Server.Database;
using Content.Server._NC.Meta.Authority;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.Meta.Authority;
using Content.Shared._NC.Meta.UI;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.UserInterface;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._NC.Meta;

internal sealed class NCMetaNativeNetworkProgramHarness(RobustIntegrationTest.ServerIntegrationInstance server)
{
    internal sealed record Installed(NCMetaInstallation Installation, VerifiedArtifact Artifact,
        NCMetaServiceAdmission Admission, NCMetaRuntimeInstance Runtime);
    internal sealed record Prepared(EntityUid Host, EntityUid Actor, string Owner, NCMetaInstallation Installation,
        VerifiedArtifact Artifact, NCMetaServiceAdmission Admission, bool RetainForInvocations);

    // Preconditions: real body has a durable native canonical owner; all endpoints Ready, claimed to that owner,
    // charged and physically routed. This helper does not issue ownership, manufacture host tokens or call adapters.
    public async Task<Installed> InstallAndStartAsync(EntityUid host, EntityUid actor, string module, string package,
        string source, IReadOnlyDictionary<string, EntityUid> targets, bool unattended = true, bool retainForInvocations = false) =>
        await StartAsync(await PrepareAsync(host, actor, module, package, source, targets, unattended, retainForInvocations));

    public async Task<Prepared> PrepareAsync(EntityUid host, EntityUid actor, string module, string package,
        string source, IReadOnlyDictionary<string, EntityUid> targets, bool unattended = true, bool retainForInvocations = false)
    {
        var entities = server.EntMan;
        var devices = entities.System<NCMetaDeviceRegistrySystem>();
        var persistence = entities.System<NCMetaPersistenceSystem>();
        var authority = entities.System<NCMetaAuthoritySystem>();
        NCMetaDeviceEntry device = null!;
        NCSessionEpoch epoch = default;
        NCActiveWorldTime time = default;
        await server.WaitAssertion(() =>
        {
            Assert.That(devices.TryGetEntry(host, out device) && device.Ready, Is.True);
            Assert.That(devices.CanManage(host, actor), Is.True);
            Assert.That(persistence.TryCaptureTime(out epoch, out time), Is.True);
        });
        var proof = await NCMetaNativeTestTasks.Run(server, () => authority.CaptureManagementAsync(actor, device.Owner));
        Assert.That(proof, Is.Not.Null);
        NCMetaAuthorityConstraints constraints = null!;
        await server.WaitAssertion(() =>
            Assert.That(constraints = authority.CaptureWriteConstraints(proof!, targets.Values.Append(host).Distinct().ToArray())!, Is.Not.Null));
        var record = await NCMetaNativeTestTasks.Run(server, () => persistence.ReadDeviceAsync(device.Id));
        Assert.That(record, Is.Not.Null);
        var project = await NCMetaNativeTestTasks.Run(server, () => persistence.ReadProjectAsync(record!.ActiveProject, device.Owner));
        Assert.That(project, Is.Not.Null);
        // ClaimDevice creates src/main.meta; template projects may use another canonical path.
        // Require the actual sole source and preserve its identity in both storage and compilation.
        var file = project!.Files.Single(value => value.Path.EndsWith(".meta", StringComparison.Ordinal));
        var saved = await NCMetaNativeTestTasks.Run(server, () => persistence.SaveSourceAsync(Guid.NewGuid(), project.Id, file.Id, device.Owner,
            file.Revision, file.Path, source, epoch, time, constraints));
        Assert.That(saved.Success, Is.True, saved.MessageKey);
        project = (await NCMetaNativeTestTasks.Run(server, () => persistence.ReadProjectAsync(project.Id, device.Owner)))!;
        var compiled = new MetaCompiler().Compile(new([new(file.Path, source)], module, package));
        Assert.That(compiled.Success, Is.True, string.Join(";", compiled.Diagnostics));
        var verified = MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!));
        var bindings = new List<NCMetaBinding>();
        var consents = new List<NCMetaGrantConsent>();
        await server.WaitAssertion(() =>
        {
            foreach (var link in verified.GetLinks())
            {
                Assert.That(targets.TryGetValue(link.SlotName, out var endpoint), Is.True);
                Assert.That(devices.TryGetEntry(endpoint, out var target) && target.Ready && target.Owner == device.Owner, Is.True);
                var capability = verified.GetType(link.CapabilityTypeId).Name;
                var imports = Enumerable.Range(0, compiled.Module!.Imports.Length).Select(verified.GetImport)
                    .Select(import => import.Name).ToHashSet(StringComparer.Ordinal);
                var members = MetaApiCatalog.Default.Members.Where(member => member.Owner == capability && imports.Contains(member.QualifiedName)).ToArray();
                var scopes = members.Select(member => member.Scope).Distinct(StringComparer.Ordinal).ToArray();
                bindings.Add(new(link.SlotName, target!.Id, link.ContractHash, scopes));
                consents.Add(new(link.SlotName, target.Id, members.Select(member => member.QualifiedName).ToList(), scopes.ToList()));
            }
            Assert.That(entities.System<NCMetaRuntimeSystem>().CanInstall(host, actor, verified, bindings.ToArray()), Is.True,
                "The real source hardware profile and physical bindings must admit the compiled artifact before installation.");
        });
        var published = await NCMetaNativeTestTasks.Run(server, () => persistence.PublishReleaseAsync(Guid.NewGuid(), project, compiled.Artifact!,
            MetaCompiler.BuildId, epoch, time, compiled.Exports, buildDeviceId: device.Id, authority: constraints));
        Assert.That(published.Success, Is.True, published.MessageKey);
        project = (await NCMetaNativeTestTasks.Run(server, () => persistence.ReadProjectAsync(project.Id, device.Owner)))!;
        var release = project.Releases.Last();
        var installed = await NCMetaNativeTestTasks.Run(server, () => persistence.InstallAsync(Guid.NewGuid(), device.Id, device.Owner, project, release,
            bindings.ToArray(), false, epoch, time, 4 * 1024 * 1024, authority: constraints));
        Assert.That(installed.Success, Is.True, installed.MessageKey);
        var installation = (await NCMetaNativeTestTasks.Run(server, () => persistence.ReadInstallationsAsync(device.Id, device.Owner)))
            .Single(value => value.PackageIdentity == package);
        await server.WaitAssertion(() => Assert.That(entities.System<SharedUserInterfaceSystem>()
            .TryOpenUi(host, NCMetaUiKey.Workspace, actor), Is.True));
        var policy = await NCMetaNativeTestTasks.Run(server, () => persistence.ReadServiceGrantPolicyAsync(installation.Id));
        var approved = await NCMetaNativeTestTasks.Run(server, () => authority.TryIssueInstallationGrantsAsync(host, actor, host, Guid.NewGuid(), installation,
            verified, new(policy?.Revision ?? 0, true, unattended, null, consents)));
        Assert.That(approved.Success, Is.True, approved.MessageKey);
        installation = (await NCMetaNativeTestTasks.Run(server, () => persistence.ReadInstallationAsync(device.Id, device.Owner, installation.Id)))!;
        var admission = await NCMetaNativeTestTasks.Run(server, () => authority.ResolveServiceAdmissionAsync(installation, verified, unattended));
        Assert.That(admission, Is.Not.Null);
        return new(host, actor, device.Owner, installation, verified, admission!, retainForInvocations);
    }

    public async Task<Installed> StartAsync(Prepared prepared)
    {
        var runtime = server.EntMan.System<NCMetaRuntimeSystem>();
        NCMetaRuntimeInstance running = null!;
        for (var tick = 0; tick < 180; tick++)
        {
            var started = false;
            await server.WaitPost(() =>
            {
                started = runtime.TryGetInstallation(prepared.Host, prepared.Owner, prepared.Installation.Id, out running!) ||
                    runtime.TryStart(prepared.Host, prepared.Actor, prepared.Installation, prepared.Artifact,
                        prepared.RetainForInvocations, serviceAdmission: prepared.Admission) &&
                    runtime.TryGetInstallation(prepared.Host, prepared.Owner, prepared.Installation.Id, out running!);
            });
            if (started) return new(prepared.Installation, prepared.Artifact, prepared.Admission, running);
            await server.WaitRunTicks(1);
        }
        Assert.Fail("Real native admission did not start the published, installed and explicitly granted program.");
        throw new InvalidOperationException();
    }
}
