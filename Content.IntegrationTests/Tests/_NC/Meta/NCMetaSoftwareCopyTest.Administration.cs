// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Linq;
using System.Threading.Tasks;
using Content.Meta.Runtime;
using Content.Server._NC.Meta.Authority;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.Meta;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._NC.Meta;

public sealed partial class NCMetaSoftwareCopyTest
{
    [TestCase("Global")]
    [TestCase("ContentHash")]
    [TestCase("ContainerHash")]
    [TestCase("MalformedPolicy")]
    public async Task EmergencyPolicyStopsCurrentVmDeniesNewAdmissionAndLeavesNativeDoorsAvailable(string mode)
    {
        var scene = await CreatePublishedPhysicalImage("module test.piracy; fn main()->void{while(true){yield;}}", nativeOwner: true);
        await SeedPurchasedSeat(scene);
        var installation = await ActivatePurchasedImage(scene);
        await Server.WaitAssertion(() => Assert.That(_persistence.TryCaptureTime(out _epoch, out _time), Is.True));
        Assert.That(await _persistence.SetAutoStartAsync(Guid.NewGuid(), installation, true, _epoch, _time), Is.True);
        installation = (await _persistence.ReadInstallationsAsync(scene.Deck.Id, scene.Owner)).Single();
        var approved = await ApproveServiceAsync(scene, installation, scene.Artifact, scene.Deck.Entity, scene.Actor);
        installation = approved.Installation;
        var authorization = await _marketplace.ResolveRunAuthorizationAsync(scene.Owner, scene.Deck.Id, installation.Active);
        Assert.That(authorization, Is.Not.Null);
        var system = Server.System<NCMetaRuntimeSystem>();
        NCMetaRuntimeInstance runtime = null!;
        for (var attempt = 0; attempt < 180 && runtime == null; attempt++)
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
        var configuration = Server.ResolveDependency<IConfigurationManager>();
        var previousEnabled = configuration.GetCVar(NCMetaCVars.Enabled);
        var previousHashes = configuration.GetCVar(NCMetaCVars.RevokedArtifacts);
        try
        {
            await Server.WaitAssertion(() =>
            {
                if (mode == "Global") configuration.SetCVar(NCMetaCVars.Enabled, false);
                else if (mode == "MalformedPolicy") configuration.SetCVar(NCMetaCVars.RevokedArtifacts, "invalid-policy");
                else Assert.That(system.TrySetOperationalArtifactBlock(mode == "ContentHash" ? scene.Artifact.ContentHash : scene.Artifact.Digest, true), Is.True);
                Assert.That(runtime.Process.State, Is.EqualTo(MetaProcessState.Stopped));
                Assert.That(system.TryStartRecovered(scene.Deck.Entity, installation, scene.Artifact, false,
                    authorization!.ExpiresAtTicks, authorization.Contact, approved.Admission), Is.False);
                Assert.That(system.GetAdministrativeSnapshot().Audit.Length, Is.LessThanOrEqualTo(128));
                Assert.That(system.GetAdministrativeSnapshot().Audit.Any(value => value.Generation == runtime.Generation && value.Phase == NCMetaAuditPhase.Stopped), Is.True);
                // This is the real domain system, with no META adapter or automatic control.
                var door = SEntMan.SpawnEntity("Door", new EntityCoordinates(scene.Deck.Entity, new System.Numerics.Vector2(2, 0)));
                var nativeDoor = SComp<DoorComponent>(door);
                Assert.That(Server.System<SharedDoorSystem>().TryOpen(door, nativeDoor, quiet: true), Is.True);
            });
        }
        finally
        {
            await Server.WaitPost(() =>
            {
                configuration.SetCVar(NCMetaCVars.RevokedArtifacts, previousHashes);
                configuration.SetCVar(NCMetaCVars.Enabled, previousEnabled);
            });
        }
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public async Task NativeAdministrativeRevocationChecksFreshPrivilegeAndKeepsOriginalIssuer(int authorizedChecks)
    {
        var scene = await CreatePublishedPhysicalImage(nativeOwner: true);
        await SeedPurchasedSeat(scene);
        var installation = await ActivatePurchasedImage(scene);
        var approved = await ApproveServiceAsync(scene, installation, scene.Artifact, scene.Deck.Entity, scene.Actor);
        var previous = await _persistence.ReadServiceGrantPolicyAsync(installation.Id);
        Assert.That(previous, Is.Not.Null);
        var calls = 0;
        var authority = Server.System<NCMetaAuthoritySystem>();
        var request = Guid.NewGuid();
        var outcome = await authority.TryAdministrativeRevokeAsync(request, installation.Id, "integration-native-admin",
            "authority regression", () => ++calls <= authorizedChecks);
        var current = await _persistence.ReadServiceGrantPolicyAsync(installation.Id);
        Assert.That(current!.Issuer, Is.EqualTo(previous!.Issuer));
        Assert.That(current.Revoked, Is.EqualTo(authorizedChecks == 2));
        Assert.That(outcome.Success, Is.EqualTo(authorizedChecks == 2));
        if (authorizedChecks != 2)
        {
            Assert.That(current.Revision, Is.EqualTo(previous.Revision));
            Assert.That(await _database.GetNCPersistentCommandResultAsync(request), Is.Null);
            return;
        }
        Assert.That(await authority.ResolveServiceAdmissionAsync(approved.Installation, scene.Artifact, true), Is.Null);
        var replay = await authority.TryAdministrativeRevokeAsync(request, installation.Id, "integration-native-admin",
            "authority regression", () => true);
        Assert.That(replay, Is.EqualTo(outcome));
        var receipt = await _database.GetNCPersistentCommandResultAsync(request);
        Assert.That(receipt!.ScopeType, Is.EqualTo("meta.admin"));
        Assert.That(receipt.ScopeId, Is.EqualTo("integration-native-admin"));
    }
}
