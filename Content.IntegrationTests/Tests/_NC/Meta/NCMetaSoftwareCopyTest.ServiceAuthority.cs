// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Linq;
using System.Threading.Tasks;
using Content.Meta.Runtime;
using Content.Server._NC.Meta.Authority;
using Content.Server._NC.Meta.Runtime;
using Content.Server._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Meta.Authority;

namespace Content.IntegrationTests.Tests._NC.Meta;

public sealed partial class NCMetaSoftwareCopyTest
{
    [TestCase("Revoke")]
    [TestCase("Expire")]
    [TestCase("Dead")]
    [TestCase("Archived")]
    public async Task AZeroLinkServiceStopsOnPolicyRevocationExpiryOrItsOwnPrincipalBecomingInactive(string invalidation)
    {
        var expire = invalidation == "Expire";
        var scene = await CreatePublishedPhysicalImage("module test.piracy; fn main()->void{while(true){yield;}}", nativeOwner: true);
        await SeedPurchasedSeat(scene);
        var installation = await ActivatePurchasedImage(scene);
        await Server.WaitAssertion(() => Assert.That(_persistence.TryCaptureTime(out _epoch, out _time), Is.True));
        Assert.That(await _persistence.SetAutoStartAsync(Guid.NewGuid(), installation, true, _epoch, _time), Is.True);
        installation = (await _persistence.ReadInstallationsAsync(scene.Deck.Id, scene.Owner)).Single();
        var approved = await ApproveServiceAsync(scene, installation, scene.Artifact, scene.Deck.Entity, scene.Actor);
        installation = approved.Installation;
        var authority = Server.System<NCMetaAuthoritySystem>();
        if (expire)
        {
            var policy = await _persistence.ReadServiceGrantPolicyAsync(installation.Id);
            await Server.WaitAssertion(() => Assert.That(_persistence.TryCaptureTime(out _epoch, out _time), Is.True));
            var result = await NCMetaNativeTestTasks.Run(Server, () => authority.TryIssueInstallationGrantsAsync(scene.Deck.Entity, scene.Actor, scene.Deck.Entity,
                Guid.NewGuid(), installation, scene.Artifact,
                new NCMetaGrantApprovalRequest(policy!.Revision, true, true, _time.Ticks + TimeSpan.TicksPerSecond * 3, [])));
            Assert.That(result.Success, Is.True, result.MessageKey);
            installation = (await _persistence.ReadInstallationsAsync(scene.Deck.Id, scene.Owner)).Single();
            var fresh = await NCMetaNativeTestTasks.Run(Server, () => authority.ResolveServiceAdmissionAsync(installation, scene.Artifact, unattended: true));
            Assert.That(fresh?.ExpiresAtTicks, Is.Not.Null, "The policy deadline exists even when it grants no device links.");
            approved = (installation, fresh!);
        }
        var authorization = await NCMetaNativeTestTasks.Run(Server, () => _marketplace.ResolveRunAuthorizationAsync(scene.Owner, scene.Deck.Id, installation.Active));
        Assert.That(authorization, Is.Not.Null);
        var system = Server.System<NCMetaRuntimeSystem>();
        NCMetaRuntimeInstance runtime = null!;
        for (var step = 0; step < 180 && runtime == null; step++)
        {
            await Server.WaitPost(() =>
            {
                if (system.TryStartRecovered(scene.Deck.Entity, installation, scene.Artifact, false,
                        authorization!.ExpiresAtTicks, authorization.Contact, approved.Admission))
                    Assert.That(system.TryGetInstallation(scene.Deck.Entity, scene.Owner, installation.Id, out runtime), Is.True);
            });
            if (runtime == null) await Server.WaitRunTicks(1);
        }
        Assert.That(runtime, Is.Not.Null);
        if (invalidation == "Revoke")
        {
            var policy = await _persistence.ReadServiceGrantPolicyAsync(installation.Id);
            Assert.That(policy!.Grants, Is.Empty);
            var revoked = await NCMetaNativeTestTasks.Run(Server, () => authority.TryRevokeGrantAsync(scene.Deck.Entity, scene.Actor, scene.Deck.Entity,
                Guid.NewGuid(), scene.Deck.Id, installation.Id, Guid.Empty, policy.Revision));
            Assert.That(revoked.Success, Is.True, revoked.MessageKey);
        }
        else if (!expire)
        {
            Assert.That(NCPartyRef.TryParse(scene.Owner, out var principal), Is.True);
            var actors = Server.System<NCCityActorRegistrySystem>();
            var actorId = new NCActorId(principal.PersistentId);
            var record = await NCMetaNativeTestTasks.Run(Server, () => actors.GetAsync(actorId));
            Assert.That(record, Is.Not.Null);
            var result = await NCMetaNativeTestTasks.Run(Server, () => actors.SetStatusAsync(new(Guid.NewGuid(), actorId, record!.AggregateVersion,
                invalidation == "Dead" ? NCCityActorStatus.Dead : NCCityActorStatus.Archived, Guid.NewGuid(),
                AuditSource: "integration.meta-service-principal")));
            Assert.That(result.Status, Is.EqualTo(NCCityActorMutationStatus.Applied), result.Error);
        }
        for (var step = 0; step < 300 && runtime.Process.State != MetaProcessState.Stopped; step++)
            await Server.WaitRunTicks(1);
        await Server.WaitAssertion(() =>
        {
            Assert.That(runtime.Process.State, Is.EqualTo(MetaProcessState.Stopped));
            Assert.That(runtime.Process.StopReason, Is.EqualTo(MetaStopReason.PermissionRevoked));
            Assert.That(system.TryGetInstallation(scene.Deck.Entity, scene.Owner, installation.Id, out _), Is.False);
        });
        Assert.That(await NCMetaNativeTestTasks.Run(Server, () => authority.ResolveServiceAdmissionAsync(installation, scene.Artifact, unattended: true)), Is.Null);
    }
}
