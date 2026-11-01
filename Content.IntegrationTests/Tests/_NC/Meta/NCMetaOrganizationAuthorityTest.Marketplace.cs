// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Meta.Bytecode;
using Content.Meta.Commerce;
using Content.Meta.Language;
using Content.Meta.Verification;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server.Preferences.Managers;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Meta.Authority;
using Content.Server._NC.Meta.Commerce;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Network;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.Identity;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Meta.UI;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.Roles;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Meta;

public sealed partial class NCMetaOrganizationAuthorityTest
{
    [TestPrototypes]
    private const string MarketplaceHardware = """
        - type: entity
          id: NCMetaIntegrationOrgMarketTerminal
          components:
          - type: NCMetaHost
            profile: NCMetaNetworkHost
          - type: NCMetaTerminal
          - type: NCMetaStorage
          - type: UserInterface
            interfaces:
              enum.NCMetaUiKey.Workspace:
                type: NCMetaWorkspaceBoundUserInterface
          - type: NCMetaPort
            networkId: CITYNET
          - type: NCMetaMessagePort
          - type: DeviceNetwork
            deviceNetId: Private
            receiveFrequency: 1945
            transmitFrequency: 1945
        - type: entity
          parent: NCMetaIntegrationOrgMarketTerminal
          id: NCMetaIntegrationOrgMarketEndpoint
          components:
          - type: NCMetaCitiNetService
        """;

#if TOOLS && NC_DIAGNOSTICS
    [TestCase(false, true, false)]
    [TestCase(true, false, false)]
    [TestCase(true, true, false)]
    [TestCase(false, true, true)]
    public async Task MarketplaceSqlRejectsCapturedOrganizationAuthorityAndPreservesHistoricalAcknowledgement(bool paid, bool restoreBeforeCommit, bool nativeCopy)
    {
        Task<T> Native<T>(Func<Task<T>> start) => NCMetaNativeTestTasks.Run(Server, start);
        var database = Server.ResolveDependency<IServerDbManager>();
        var preferences = Server.ResolveDependency<IServerPreferencesManager>();
        var authority = Server.System<NCMetaAuthoritySystem>();
        var devices = Server.System<NCMetaDeviceRegistrySystem>();
        var persistence = Server.System<NCMetaPersistenceSystem>();
        var marketplace = Server.System<NCMetaMarketplaceSystem>();
        var finance = Server.System<NCPartyFinanceAdapterSystem>();
        var ledger = Server.System<NCEconomyLedgerSystem>();
        EntityUid user = default, terminal = default, target = default, endpoint = default;
        NCCharacterId character = default;
        NCPartyRef actor = default, organization = default;
        ProtoId<JobPrototype> manager = default, employee = default;
        NCSessionEpoch epoch = default;
        NCActiveWorldTime time = default;
        var purchaseRequest = Guid.NewGuid();
        async Task Wait(Func<bool> predicate)
        {
            for (var step = 0; step < 900; step++)
            {
                var ready = false;
                await Server.WaitPost(() => ready = predicate());
                if (ready) return;
                await Server.WaitRunTicks(1);
            }
            Assert.Fail("The actual native marketplace fixture did not become ready.");
        }
        await Server.WaitAssertion(() =>
        {
            Assert.That(preferences.TryGetSelectedNCCharacterId(ServerSession!.UserId, out character), Is.True);
            var department = SProtoMan.EnumeratePrototypes<DepartmentPrototype>().OrderBy(value => value.ID, StringComparer.Ordinal)
                .First(value => value.NCPersonnelManagers.Count > 0);
            manager = department.NCPersonnelManagers.First();
            employee = SProtoMan.EnumeratePrototypes<JobPrototype>().Select(value => new ProtoId<JobPrototype>(value.ID))
                .First(value => !department.NCPersonnelManagers.Contains(value));
            organization = NCPersistentIdentity.Organization(department.ID);
            SetBreathableMap(Server.System<SharedMapSystem>().CreateMap(out var map));
            user = SEntMan.SpawnEntity("MobHuman", new MapCoordinates(Vector2.Zero, map));
            terminal = SEntMan.SpawnEntity("NCMetaIntegrationOrgMarketTerminal", new MapCoordinates(Vector2.Zero, map));
            target = SEntMan.SpawnEntity("NCMetaIntegrationOrgMarketTerminal", new MapCoordinates(Vector2.UnitY, map));
            endpoint = SEntMan.SpawnEntity("NCMetaIntegrationOrgMarketEndpoint", new MapCoordinates(Vector2.UnitX, map));
            Server.PlayerMan.SetAttachedEntity(ServerSession, user);
            Assert.That(Server.System<NCEntityPartyResolverSystem>().TryResolve(user, out actor), Is.True);
            SEntMan.EventBus.RaiseEvent(EventSource.Local, new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound));
        });
        await Wait(() => Server.System<NCActiveWorldTimeSystem>().IsReady);
        Assert.That(await Native(() => database.SetNCCharacterEmploymentAsync(ServerSession!.UserId, character.Value, manager)), Is.True);
        var directory = Server.System<NCPartyDirectorySystem>();
        if (await Native(() => directory.GetAsync(organization)) == null)
            Assert.That((await Native(() => directory.RegisterAsync(new(Guid.NewGuid(), organization, "Market authority fixture", Guid.NewGuid())))).Status,
                Is.EqualTo(NCPartyDirectoryMutationStatus.Applied));
        NCMetaDeviceEntry terminalEntry = null!, targetEntry = null!;
        await Wait(() => devices.TryGetEntry(terminal, out terminalEntry) && terminalEntry.Ready &&
            devices.TryGetEntry(target, out targetEntry) && targetEntry.Ready && devices.TryGetEntry(endpoint, out var service) && service.Ready);
        await Server.WaitAssertion(() => Assert.That(persistence.TryCaptureTime(out epoch, out time), Is.True));
        var personal = await Native(() => persistence.ClaimDeviceAsync(Guid.NewGuid(), terminalEntry.Id, actor.ToString(), "Personal terminal", epoch, time));
        var corporate = await Native(() => persistence.ClaimDeviceAsync(Guid.NewGuid(), targetEntry.Id, organization.ToString(), "Organization target", epoch, time));
        Assert.That(personal, Is.Not.Null); Assert.That(corporate, Is.Not.Null);
        await Server.WaitAssertion(() =>
        {
            Assert.That(devices.TryApplyOwner(terminal, terminalEntry.Id, "", actor.ToString(), personal!.AuthorityEpoch), Is.True);
            Assert.That(devices.TryApplyOwner(target, targetEntry.Id, "", organization.ToString(), corporate!.AuthorityEpoch), Is.True);
            var cable = SEntMan.SpawnEntity("NCMetaNetworkCable", SComp<TransformComponent>(terminal).Coordinates);
            Assert.That(Server.System<NCMetaNetworkSystem>().TryConnectCable(cable, user, terminal), Is.True);
            Assert.That(Server.System<NCMetaNetworkSystem>().TryConnectCable(cable, user, endpoint), Is.True);
            Assert.That(Server.System<SharedUserInterfaceSystem>().TryOpenUi(terminal, NCMetaUiKey.Workspace, user), Is.True);
        });
        Assert.That(await Native(() => authority.CaptureManagementAsync(user, organization.ToString())), Is.Not.Null);
        if (nativeCopy) await Server.WaitAssertion(() =>
        {
            var cable = SEntMan.SpawnEntity("NCMetaNetworkCable", SComp<TransformComponent>(terminal).Coordinates);
            Assert.That(Server.System<NCMetaNetworkSystem>().TryConnectCable(cable, user, terminal), Is.True);
            Assert.That(Server.System<NCMetaNetworkSystem>().TryConnectCable(cable, user, target), Is.True);
        });
        var compiled = new MetaCompiler().Compile(new([new("main.meta", "module org.authority; fn main()->void{}")], "org.authority", "org.authority", "1.0.0"));
        Assert.That(compiled.Artifact, Is.Not.Null, string.Join("\n", compiled.Diagnostics));
        var artifact = MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(MetaBytecodeLoader.Load(compiled.Artifact!)));
        Assert.That(await Native(() => database.StoreNCMetaBlobAsync(compiled.Artifact!)), Is.EqualTo(artifact.ContentHash),
            "Published product participants must reference actual admitted bytes under the real retention foreign key.");
        NCMetaProject? sourceProject = null;
        NCMetaRelease? sourceRelease = null;
        NCMetaInstallation? sourceInstallation = null;
        if (nativeCopy)
        {
            sourceProject = (await Native(() => persistence.ReadProjectAsync(personal!.ActiveProject, actor.ToString())))!;
            Assert.That((await Native(() => persistence.PublishReleaseAsync(Guid.NewGuid(), sourceProject, compiled.Artifact!,
                "integration", epoch, time, compiled.Exports))).Success, Is.True);
            sourceProject = (await Native(() => persistence.ReadProjectAsync(sourceProject.Id, actor.ToString())))!;
            sourceRelease = sourceProject.Releases.Single();
            Assert.That((await Native(() => persistence.InstallAsync(Guid.NewGuid(), terminalEntry.Id, actor.ToString(), sourceProject,
                sourceRelease, [], false, epoch, time, 1024 * 1024))).Success, Is.True);
            sourceInstallation = (await Native(() => persistence.ReadInstallationsAsync(terminalEntry.Id, actor.ToString()))).Single();
        }
        var release = new MetaSoftwareRelease(sourceRelease?.Id ?? Guid.NewGuid(), artifact.ContentHash, "org.authority", "1.0.0", artifact.Manifest.RuntimeAbi,
            artifact.Manifest.ApiCatalogHash, [], [], artifact.ResidentMemoryBytes, time.Ticks, "")
        { ContainerDigest = artifact.Digest, MinimumProcessMemoryBytes = artifact.MinimumProcessMemoryBytes,
            MaximumConstantStringBytes = artifact.MaximumConstantStringBytes, PackageBytes = compiled.Artifact!.Length };
        var product = new MetaSoftwareProduct(Guid.NewGuid(), actor.ToString(), sourceProject?.Id ?? Guid.NewGuid(), "Native org authority", "", 1,
            new() { Source = MetaSourcePolicy.Closed }, paid ? 100 : 0, [release], true, 1);
        NCPersistentAggregateWrite ProductWrite<T>(string type, string id, T value) => new(Guid.NewGuid(), "meta.test.product", type, id, 0, 1, JsonSerializer.Serialize(value), "{}");
        var publicationWrites = new System.Collections.Generic.List<NCPersistentAggregateWrite>
        { ProductWrite("meta.software.product", product.Id.ToString("D") + ":1", product) };
        if (nativeCopy)
        {
            publicationWrites.Add(ProductWrite("meta.software.publication", product.Id.ToString("D") + ":1:" + release.Id.ToString("D"),
                new NCMetaSoftwarePublication(product.Id, 1, sourceRelease!, 1) { RedistributionApproved = true }));
            publicationWrites.Add(ProductWrite("meta.software.publisher-image", sourceProject!.Id.ToString("D") + ":" + release.Id.ToString("D"),
                new NCMetaSoftwarePublisherImage(sourceProject.Id, release.Id, product.Id, 1, release.ArtifactDigest, 1)));
        }
        var seeded = await Native(() => database.ApplyNCPersistentBatchMutationAsync(new(Guid.NewGuid(), "meta.test.org-product", "meta.owner", actor.ToString(),
            publicationWrites.ToArray(),
            "{}", Guid.NewGuid(), null, epoch, time, [])));
        Assert.That(seeded.Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
        var credit = Guid.NewGuid();
        var credited = await Native(() => finance.AdjustWithIssuanceAsync(new(credit,
            NCPersistentIdentity.Transaction("meta", "org-market-credit", credit.ToString("D")), organization, 1000,
            NCLedgerTransactionKind.Adjustment, "Native authority test", organization, credit)));
        var creditClock = credited.Status == NCEconomyMutationStatus.Applied ? "" : await CaptureClockFailureAsync();
        Assert.That(credited.Status, Is.EqualTo(NCEconomyMutationStatus.Applied), $"{credited.Error}; {creditClock}");
        Assert.That((await Native(() => finance.EnsurePrimaryAccountAsync(actor, "native.org-market-test"))).IsResolved, Is.True);
        var payer = (await Native(() => finance.GetPrimaryAccountAsync(organization, true)))!;
        var payee = (await Native(() => finance.GetPrimaryAccountAsync(actor, true)))!;
        if (nativeCopy)
        {
            var proof = await Native(() => authority.CaptureManagementAsync(user, organization.ToString()));
            NCMetaAuthorityConstraints constraints = null!;
            await Server.WaitAssertion(() => constraints = authority.CaptureWriteConstraints(proof!, target)!);
            Assert.That(constraints, Is.Not.Null);
            var admission = new NCMetaSoftwareCopyAdmission(purchaseRequest, Guid.NewGuid(), organization.ToString(), targetEntry.Id,
                targetEntry.DeviceEpoch, targetEntry.AuthorityEpoch, terminalEntry.Id, actor.ToString(), terminalEntry.DeviceEpoch,
                terminalEntry.AuthorityEpoch, "native.org-copy", "data", 1, sourceInstallation!.Id);
            var before = await Native(() => persistence.ReadSoftwareStorageUsageAsync(targetEntry.Id, organization.ToString()));
            var witness = NCMetaPersistenceSystem.StableGuid("software-copy-evidence:" + purchaseRequest);
            var copyCallbacks = 0;
            await Native(async () =>
            {
                await marketplace.SetIsolatedBeforeCommitAsync(purchaseRequest, async () =>
                {
                    copyCallbacks++;
                    Assert.That(await database.SetNCCharacterEmploymentAsync(ServerSession!.UserId, character.Value, employee), Is.True);
                    Assert.That(await database.SetNCCharacterEmploymentAsync(ServerSession.UserId, character.Value, manager), Is.True);
                });
                return true;
            });
            try
            {
                var denied = await Native(() => marketplace.TryCopySoftwareAsync(admission, () => authority.IsCurrent(proof!) &&
                    devices.TryGetEntry(target, out var deckNow) && deckNow.Owner == organization.ToString() &&
                    deckNow.DeviceEpoch == admission.DeckEpoch && deckNow.AuthorityEpoch == admission.DeckAuthority &&
                    devices.TryGetEntry(terminal, out var sourceNow) && sourceNow.Owner == actor.ToString() &&
                    sourceNow.DeviceEpoch == admission.SourceEpoch && sourceNow.AuthorityEpoch == admission.SourceAuthority &&
                    Server.System<NCMetaNetworkSystem>().CanRoute(target, terminal), constraints));
                Assert.That(copyCallbacks, Is.EqualTo(1));
                Assert.That(denied.Success, Is.False);
                Assert.That(await Native(() => persistence.ReadSnapshotAsync("meta.software.copy-evidence", witness.ToString("D"))), Is.Null);
                Assert.That(await Native(() => persistence.ReadSnapshotAsync("meta.software.license", NCMetaPersistenceSystem.StableGuid("software-copy-license:" + purchaseRequest).ToString("D"))), Is.Null);
                Assert.That(await Native(() => persistence.ReadReservedSoftwareCopyBytesAsync(targetEntry.Id, organization.ToString())), Is.Zero);
                Assert.That((await Native(() => persistence.ReadSoftwareStorageUsageAsync(targetEntry.Id, organization.ToString())))!.Revision, Is.EqualTo(before!.Revision));
                var copyReceipt = await Native(() => database.GetNCPersistentCommandResultAsync(purchaseRequest));
                Assert.That(copyReceipt, Is.Not.Null);
                Assert.That(copyReceipt!.Status, Is.EqualTo(NCPersistentMutationStatus.DuplicateFailed));
                Assert.That(copyReceipt.CommandType, Is.EqualTo("meta.software.copy"));
                Assert.That(copyReceipt.ScopeId, Is.EqualTo(organization.ToString()));
                Assert.That((await Native(() => finance.GetPrimaryAccountAsync(organization, true)))!.Snapshot.Balance, Is.EqualTo(payer.Snapshot.Balance));
            }
            finally { await Native(async () => { await marketplace.ClearIsolatedBeforeCommitAsync(purchaseRequest); return true; }); }
            return;
        }
        var quoteRequest = Guid.NewGuid();
        var quoted = await Native(() => marketplace.TryQuoteAsync(terminal, user, quoteRequest, product.Id, 1, release.Id,
            organization.ToString(), MetaLicenseScope.Organization, 2, [targetEntry.Id]));
        var quoteFailure = quoted.Success ? null : await Native(() => database.GetNCPersistentCommandResultAsync(
            NCPersistentIdentity.Command("meta", "software-quote", actor + ":" + quoteRequest)));
        var quoteClock = quoted.Success ? "" : await CaptureClockFailureAsync();
        Assert.That(quoted.Success, Is.True, $"{quoted.MessageKey}; {quoteFailure?.Status}: {quoteFailure?.Error}; {quoteClock}");
        var quote = quoted.Quote!;
        var licenseId = NCMetaPersistenceSystem.StableGuid("software-license:" + quote.Id);
        var poolBefore = await Native(() => persistence.ReadSnapshotAsync("meta.quote-pool", "software"));
        var slotBefore = await Native(() => persistence.ReadSnapshotAsync("meta.software.quote", MetaQuotePoolPolicy.SlotKey(quote.PoolSlot!.Value)));
        var ownerBefore = await Native(() => persistence.ReadSnapshotAsync("meta.software.owner", organization.ToString()));
        var deviceBefore = await Native(() => persistence.ReadSnapshotAsync("meta.device", targetEntry.Id));
        var licensesBefore = await Native(() => persistence.ReadSnapshotAsync("meta.software.device", targetEntry.Id));
        var storageBefore = await Native(() => persistence.ReadSoftwareStorageUsageAsync(targetEntry.Id, organization.ToString()));
        var callbacks = 0;
        await Native(async () =>
        {
            await marketplace.SetIsolatedBeforeCommitAsync(purchaseRequest, async () =>
            {
                callbacks++;
                Assert.That(await database.SetNCCharacterEmploymentAsync(ServerSession!.UserId, character.Value, employee), Is.True);
                if (restoreBeforeCommit)
                    Assert.That(await database.SetNCCharacterEmploymentAsync(ServerSession.UserId, character.Value, manager), Is.True);
            });
            return true;
        });
        try
        {
            var rejected = await Native(() => marketplace.TryPurchaseAsync(terminal, user, purchaseRequest, quote.Id));
            Assert.That(callbacks, Is.EqualTo(1), "The race must occur after final native capture, immediately before actual SQL admission.");
            Assert.That(rejected.Success, Is.False, "Restoring the same job must not authorize an old proof.");
            Assert.That((await Native(() => finance.GetPrimaryAccountAsync(organization, true)))!.Snapshot.Balance, Is.EqualTo(payer.Snapshot.Balance));
            Assert.That((await Native(() => finance.GetPrimaryAccountAsync(actor, true)))!.Snapshot.Balance, Is.EqualTo(payee.Snapshot.Balance));
            Assert.That(await Native(() => persistence.ReadSnapshotAsync("meta.software.license", licenseId.ToString("D"))), Is.Null);
            Assert.That(await Native(() => persistence.ReadSnapshotAsync("meta.quote-pool", "software")), Is.EqualTo(poolBefore));
            Assert.That(await Native(() => persistence.ReadSnapshotAsync("meta.software.quote", slotBefore!.AggregateId)), Is.EqualTo(slotBefore));
            Assert.That(await Native(() => persistence.ReadSnapshotAsync("meta.software.owner", organization.ToString())), Is.EqualTo(ownerBefore));
            Assert.That(await Native(() => persistence.ReadSnapshotAsync("meta.device", targetEntry.Id)), Is.EqualTo(deviceBefore));
            Assert.That(await Native(() => persistence.ReadSnapshotAsync("meta.software.device", targetEntry.Id)), Is.EqualTo(licensesBefore));
            Assert.That((await Native(() => persistence.ReadSoftwareStorageUsageAsync(targetEntry.Id, organization.ToString())))!.Revision, Is.EqualTo(storageBefore!.Revision));
            var transaction = NCPersistentIdentity.Transaction("meta", "software-purchase", actor + ":" + purchaseRequest);
            Assert.That(await Native(() => ledger.GetTransactionAsync(transaction, true)), Is.Null);
            if (!restoreBeforeCommit)
                Assert.That(await Native(() => database.SetNCCharacterEmploymentAsync(ServerSession!.UserId, character.Value, manager)), Is.True);
            Assert.That(await Native(() => authority.CaptureManagementAsync(user, organization.ToString())), Is.Not.Null);
            var failedCommand = NCPersistentIdentity.Command("meta", "software-purchase", actor + ":" + purchaseRequest);
            var rejectedReceipt = await Native(() => database.GetNCPersistentCommandResultAsync(failedCommand));
            Assert.That(rejectedReceipt, Is.Not.Null);
            Assert.That(rejectedReceipt!.Status, Is.EqualTo(NCPersistentMutationStatus.DuplicateFailed));
            Assert.That((await Native(() => marketplace.TryPurchaseAsync(terminal, user, purchaseRequest, quote.Id))).Success, Is.False,
                "A terminal denial must remain denied when its command is replayed.");
            var acceptedRequest = Guid.NewGuid();
            var accepted = await Native(() => marketplace.TryPurchaseAsync(terminal, user, acceptedRequest, quote.Id));
            Assert.That(accepted.Success, Is.True, accepted.MessageKey);
            Assert.That(accepted.License!.Id, Is.EqualTo(licenseId));
            Assert.That((await Native(() => finance.GetPrimaryAccountAsync(organization, true)))!.Snapshot.Balance, Is.EqualTo(payer.Snapshot.Balance - quote.Price));
            var releaseRequest = Guid.NewGuid();
            Assert.That((await Native(() => marketplace.TryReleaseSeatAsync(terminal, user, releaseRequest, licenseId, targetEntry.Id))).Success, Is.True);
            var committedLicense = await Native(() => persistence.ReadSnapshotAsync("meta.software.license", licenseId.ToString("D")));
            Assert.That(await Native(() => database.SetNCCharacterEmploymentAsync(ServerSession!.UserId, character.Value, employee)), Is.True);
            var historical = await Native(() => marketplace.TryPurchaseAsync(terminal, user, acceptedRequest, quote.Id));
            var released = await Native(() => marketplace.TryReleaseSeatAsync(terminal, user, releaseRequest, licenseId, targetEntry.Id));
            Assert.Multiple(() =>
            {
                Assert.That(historical.Success, Is.True, historical.MessageKey);
                Assert.That(historical.License, Is.Null, "Historical commit status does not disclose current private organization data.");
                Assert.That(released.Success, Is.True, released.MessageKey);
                Assert.That(released.License, Is.Null);
            });
            Assert.That(await Native(() => persistence.ReadSnapshotAsync("meta.software.license", licenseId.ToString("D"))), Is.EqualTo(committedLicense));
            Assert.That((await Native(() => marketplace.TryAssignSeatAsync(terminal, user, Guid.NewGuid(), licenseId, targetEntry.Id))).Success, Is.False);
            Assert.That((await Native(() => finance.GetPrimaryAccountAsync(organization, true)))!.Snapshot.Balance, Is.EqualTo(payer.Snapshot.Balance - quote.Price));
        }
        finally
        {
            await Native(async () => { await marketplace.ClearIsolatedBeforeCommitAsync(purchaseRequest); return true; });
            await Native(() => database.SetNCCharacterEmploymentAsync(ServerSession!.UserId, character.Value, manager));
        }
    }
#endif
}
