// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Meta.Bytecode;
using Content.Meta.Commerce;
using Content.Meta.Language;
using Content.Meta.Runtime;
using Content.Meta.Verification;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.Meta.Commerce;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Network;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.Meta;
using Content.Shared.Atmos;
using Content.Shared._NC.Identity;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._NC.Meta;

[TestFixture]
[NonParallelizable]
[TestOf(typeof(NCMetaMarketplaceSystem))]
public sealed partial class NCMetaSoftwareCopyTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = false };
    [SidedDependency(Side.Server)] private NCMetaPersistenceSystem _persistence = default!;
    [SidedDependency(Side.Server)] private NCMetaMarketplaceSystem _marketplace = default!;
    [SidedDependency(Side.Server)] private NCMetaDeviceRegistrySystem _devices = default!;
    [SidedDependency(Side.Server)] private IServerDbManager _database = default!;
    private NCSessionEpoch _epoch;
    private NCActiveWorldTime _time;
    private readonly List<EntityUid> _nativeSoftwareBodies = [];

    public override async Task DoTeardown()
    {
        try { await base.DoTeardown(); }
        catch when (Server.UnhandledException != null || Client.UnhandledException != null)
        {
            ExceptionDispatchInfo.Capture(Server.UnhandledException ?? Client.UnhandledException!).Throw();
            throw;
        }
    }

    [TearDown]
    public async Task UnbindNativeSoftwareActors()
    {
        await NCMetaNativeTestTasks.CloseWorld(Server);
        if (_nativeSoftwareBodies.Count != 0)
        {
            await NCMetaNativeActorHarness.UnbindBeforeDelete(Server, _nativeSoftwareBodies.ToArray());
            _nativeSoftwareBodies.Clear();
        }
        await Pair.ReallyBeIdle();
    }

    [TestPrototypes]
    private const string Hardware = """
        - type: entity
          parent: PowerCellSlotMediumItem
          id: NCMetaIntegrationCopyHost
          components:
          - type: NCMetaHost
            profile: NCMetaNetworkHost
          - type: NCMetaDevice
          - type: NCMetaTerminal
          - type: UserInterface
            interfaces:
              enum.NCMetaUiKey.Workspace:
                type: NCMetaWorkspaceBoundUserInterface
          - type: NCMetaStorage
            capacityBytes: 4194304
          - type: NCMetaPort
            networkId: LOCAL
          - type: NCMetaMessagePort
          - type: DeviceNetwork
            deviceNetId: Private
            receiveFrequency: 1945
            transmitFrequency: 1945
        - type: entity
          parent: NCMetaIntegrationCopyHost
          id: NCMetaIntegrationCopyMarketplace
          components:
          - type: NCMetaPort
            networkId: CITYNET
          - type: NCMetaMessagePort
            gatewayNetworks: [LOCAL]
          - type: NCMetaCitiNetService
        - type: ncMetaHardware
          id: NCMetaIntegrationFirmwareOnly
          memoryBytes: 1048576
          storageBytes: 4194304
          maxProcesses: 1
          bootMilliseconds: 0
          allowUserPackages: false
          allowFirmwareUpdates: true
          firmwareRequiresMaintenance: true
          idleWatts: 0
          activeWatts: 0
          radioWatts: 0
        - type: entity
          parent: [NCMetaIntegrationCopyHost, PowerCellSlotMediumItem]
          id: NCMetaIntegrationFirmwareHost
          components:
          - type: NCMetaHost
            profile: NCMetaIntegrationFirmwareOnly
        # Fixture-only hot-update hardware exercises recovery after the guard stops a running VM.
        # Real required-maintenance profiles remain covered by the separate firmware service case.
        - type: ncMetaHardware
          id: NCMetaIntegrationHotFirmware
          memoryBytes: 1048576
          storageBytes: 4194304
          maxProcesses: 1
          bootMilliseconds: 0
          allowUserPackages: false
          allowFirmwareUpdates: true
          firmwareRequiresMaintenance: false
          idleWatts: 0
          activeWatts: 0
          radioWatts: 0
        - type: entity
          parent: [NCMetaIntegrationCopyHost, PowerCellSlotMediumItem]
          id: NCMetaIntegrationHotFirmwareHost
          components:
          - type: NCMetaHost
            profile: NCMetaIntegrationHotFirmware
        """;

    private sealed record Scene(string Owner, string SourceOwner, NCMetaDeviceEntry Deck,
        NCMetaDeviceEntry Source, NCMetaProject Project, NCMetaRelease Release, MetaSoftwareProduct Product,
        NCMetaInstallation Installation, int Bytes, VerifiedArtifact Artifact, EntityUid Service, EntityUid Actor,
        EntityUid? SourceActor = null);

    private async Task<Scene> CreatePublishedPhysicalImage(string sourceText = "module test.piracy; fn main()->void{}", MetaSoftwareTerms? terms = null,
        bool firmwareHost = false, bool nativeOwner = false, bool hotFirmware = false, bool nativeSourceOwner = false)
    {
        var clock = Server.System<NCActiveWorldTimeSystem>();
        await Server.WaitPost(() => SEntMan.EventBus.RaiseEvent(EventSource.Local,
            new GameRunLevelChangedEvent(GameRunLevel.PreRoundLobby, GameRunLevel.InRound)));
        for (var attempt = 0; attempt < 180 && !clock.IsReady; attempt++) await Server.WaitRunTicks(1);
        await Server.WaitAssertion(() => Assert.That(_persistence.TryCaptureTime(out _epoch, out _time), Is.True));
        EntityUid deck = default, source = default, service = default, actor = default;
        EntityUid? sourceActor = null;
        MapId map = default;
        await Server.WaitPost(() =>
        {
            var mapEntity = Server.System<SharedMapSystem>().CreateMap(out map);
            var air = new GasMixture(Atmospherics.CellVolume) { Temperature = Atmospherics.T20C };
            air.AdjustMoles(Gas.Oxygen, Atmospherics.OxygenMolesStandard);
            air.AdjustMoles(Gas.Nitrogen, Atmospherics.NitrogenMolesStandard);
            Server.System<AtmosphereSystem>().SetMapAtmosphere(mapEntity, false, air);
            deck = SEntMan.SpawnEntity(hotFirmware ? "NCMetaIntegrationHotFirmwareHost" :
                firmwareHost ? "NCMetaIntegrationFirmwareHost" : "NCMetaIntegrationCopyHost", new MapCoordinates(Vector2.Zero, map));
            source = SEntMan.SpawnEntity("NCMetaIntegrationCopyHost", new MapCoordinates(Vector2.UnitY, map));
            service = SEntMan.SpawnEntity("NCMetaIntegrationCopyMarketplace", new MapCoordinates(Vector2.UnitX, map));
            actor = SEntMan.SpawnEntity("MobHuman", new MapCoordinates(Vector2.Zero, map));
            if (nativeSourceOwner) sourceActor = SEntMan.SpawnEntity("MobHuman", new MapCoordinates(Vector2.UnitY, map));
        });
        NCMetaDeviceEntry deckEntry = null, sourceEntry = null;
        for (var attempt = 0; attempt < 180; attempt++)
        {
            var ready = false;
            await Server.WaitPost(() => ready = _devices.TryGetEntry(deck, out deckEntry) && deckEntry.Ready &&
                _devices.TryGetEntry(source, out sourceEntry) && sourceEntry.Ready && _devices.TryGetEntry(service, out var endpoint) && endpoint.Ready);
            if (ready) break;
            await Server.WaitRunTicks(1);
        }
        Assert.That(deckEntry?.Ready, Is.True);
        Assert.That(sourceEntry?.Ready, Is.True);
        var actorId = new NCActorId(Guid.NewGuid());
        if (firmwareHost || nativeOwner || hotFirmware)
        {
            await CreateNativeServiceOwnerAsync(actorId, "Fixture device owner");
            await NCMetaNativeActorHarness.RestoreBinding(Server, actorId, actor);
            _nativeSoftwareBodies.Add(actor);
        }
        var owner = firmwareHost || nativeOwner || hotFirmware ? NCPartyRef.ForActor(actorId).ToString() : NCPartyRef.ForCharacter(new NCCharacterId(744)).ToString();
        var sourceActorId = new NCActorId(Guid.NewGuid());
        if (sourceActor is { } nativeSourceActor)
        {
            await CreateNativeServiceOwnerAsync(sourceActorId, "Fixture software publisher");
            await NCMetaNativeActorHarness.RestoreBinding(Server, sourceActorId, nativeSourceActor);
            _nativeSoftwareBodies.Add(nativeSourceActor);
        }
        var sourceOwner = nativeSourceOwner ? NCPartyRef.ForActor(sourceActorId).ToString() : NCPartyRef.ForCharacter(new NCCharacterId(745)).ToString();
        var deckRecord = await NCMetaNativeTestTasks.Run(Server, () => _persistence.ClaimDeviceAsync(Guid.NewGuid(), deckEntry.Id, owner, "Copy target", _epoch, _time));
        var sourceRecord = await NCMetaNativeTestTasks.Run(Server, () => _persistence.ClaimDeviceAsync(Guid.NewGuid(), sourceEntry.Id, sourceOwner, "Copy source", _epoch, _time));
        Assert.That(deckRecord, Is.Not.Null);
        Assert.That(sourceRecord, Is.Not.Null);
        await Server.WaitAssertion(() =>
        {
            Assert.That(_devices.TryApplyOwner(deck, deckEntry.Id, "", owner, deckRecord.AuthorityEpoch), Is.True);
            Assert.That(_devices.TryApplyOwner(source, sourceEntry.Id, "", sourceOwner, sourceRecord.AuthorityEpoch), Is.True);
            var cable = SEntMan.SpawnEntity("NCMetaNetworkCable", new MapCoordinates(Vector2.Zero, map));
            var network = Server.System<NCMetaNetworkSystem>();
            Assert.That(network.TryConnectCable(cable, actor, deck), Is.True);
            Assert.That(network.TryConnectCable(cable, actor, service), Is.True);
            Assert.That(_marketplace.HasMarketplaceRoute(deck), Is.True);
        });
        var project = (await NCMetaNativeTestTasks.Run(Server, () => _persistence.ReadProjectAsync(sourceRecord.ActiveProject, sourceOwner)))!;
        var compiled = new MetaCompiler().Compile(new([new("main.meta", sourceText)],
            "test.piracy", "test.piracy", "1.0.0"));
        Assert.That(compiled.Artifact, Is.Not.Null, string.Join("\n", compiled.Diagnostics));
        var bytes = compiled.Artifact!;
        var verified = MetaVerifier.Verify(bytes, MetaApiCatalog.Default.CreateVerificationCatalog(MetaBytecodeLoader.Load(bytes)));
        Assert.That((await NCMetaNativeTestTasks.Run(Server, () => _persistence.PublishReleaseAsync(Guid.NewGuid(), project, bytes, "integration", _epoch, _time, compiled.Exports))).Success, Is.True);
        project = (await NCMetaNativeTestTasks.Run(Server, () => _persistence.ReadProjectAsync(project.Id, sourceOwner)))!;
        var release = project.Releases.Single();
        Assert.That((await NCMetaNativeTestTasks.Run(Server, () => _persistence.InstallAsync(Guid.NewGuid(), sourceEntry.Id, sourceOwner, project,
            release, [], false, _epoch, _time, 4 * 1024 * 1024))).Success, Is.True);
        var installed = (await NCMetaNativeTestTasks.Run(Server, () => _persistence.ReadInstallationsAsync(sourceEntry.Id, sourceOwner))).Single();
        var productId = Guid.NewGuid();
        var descriptor = new MetaSoftwareRelease(release.Id, release.Digest, "test.piracy", "1.0.0",
            verified.Manifest.RuntimeAbi, verified.Manifest.ApiCatalogHash, verified.Manifest.RequiredScopes, [],
            verified.ResidentMemoryBytes, _time.Ticks, "")
        {
            ContainerDigest = verified.Digest, PackageBytes = bytes.Length,
            MinimumProcessMemoryBytes = verified.MinimumProcessMemoryBytes, MaximumConstantStringBytes = verified.MaximumConstantStringBytes,
        };
        var product = new MetaSoftwareProduct(productId, sourceOwner, project.Id, "Fixture software", "", 1,
            terms ?? new() { Source = MetaSourcePolicy.Closed }, 100, [descriptor], true, 1);
        var key = productId.ToString("D") + ":1";
        // Arrange an already published immutable product through the real protected batch/journal API.
        // NET probe/session authority is covered separately; this fixture enters its trusted native copy boundary.
        var publication = await NCMetaNativeTestTasks.Run(Server, () => _database.ApplyNCPersistentBatchMutationAsync(new(Guid.NewGuid(), "meta.test.publication",
            "meta.owner", sourceOwner,
            [Write("meta.software.product", key, 0, product),
                Write("meta.software.identity", productId.ToString("D"), 0, new NCMetaSoftwareProductIdentity(productId, sourceOwner, project.Id, 1)),
                Write("meta.software.publication", key + ":" + release.Id.ToString("D"), 0, new NCMetaSoftwarePublication(productId, 1, release, 1) { RedistributionApproved = true }),
                Write("meta.software.release-index", productId.ToString("D") + ":" + release.Id.ToString("D"), 0, new NCMetaSoftwareReleaseLocator(productId, release.Id, 1, 1)),
                Write("meta.software.publisher-image", project.Id.ToString("D") + ":" + release.Id.ToString("D"), 0, new NCMetaSoftwarePublisherImage(project.Id, release.Id, productId, 1, release.Digest, 1))],
            "{}", Guid.NewGuid(), null, _epoch, _time, [])));
        Assert.That(publication.Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
        return new(owner, sourceOwner, deckEntry, sourceEntry, project, release, product, installed, bytes.Length, verified, service, actor, sourceActor);
    }

    [Test]
    public async Task LicensedFirmwareUsesItsPhysicalSlotAndExactRetryCannotReactivateAnOlderImage()
    {
        var scene = await CreatePublishedPhysicalImage(firmwareHost: true);
        var license = await SeedPurchasedSeat(scene);
        var hardware = Server.System<NCMetaHardwareSystem>();
        var runtime = Server.System<NCMetaRuntimeSystem>();
        var normal = await NCMetaNativeTestTasks.Run(Server, () => _marketplace.ReadCompatibilityAsync(scene.Owner, scene.Deck.Id, scene.Product.Id, scene.Release.Id));
        var firmware = await NCMetaNativeTestTasks.Run(Server, () => _marketplace.ReadCompatibilityAsync(scene.Owner, scene.Deck.Id, scene.Product.Id, scene.Release.Id, "Firmware"));
        Assert.That(normal, Is.Not.Null);
        Assert.That(normal!.Compatible, Is.False, "A firmware-only device must not sell an unusable User-slot admission.");
        Assert.That(firmware?.Compatible, Is.True);
        NCMetaFirmwareServiceStamp stamp = null!;
        await Server.WaitAssertion(() =>
        {
            Assert.That(runtime.CanInstall(scene.Deck.Entity, scene.Actor, scene.Artifact, []), Is.False);
            Assert.That(hardware.TryCaptureFirmwareService(scene.Deck.Entity, scene.Actor, out _), Is.False);
            Assert.That(hardware.TrySetMaintenance(scene.Deck.Entity, scene.Actor, true), Is.True);
            Assert.That(hardware.TryCaptureFirmwareService(scene.Deck.Entity, scene.Actor, out stamp), Is.True);
            Assert.That(runtime.CanInstallFirmware(scene.Deck.Entity, scene.Actor, scene.Artifact, []), Is.True);
            Assert.That(hardware.TrySetMaintenance(scene.Deck.Entity, scene.Actor, false), Is.True);
            Assert.That(hardware.CanActivateFirmware(scene.Deck.Entity, scene.Actor, stamp), Is.False);
            Assert.That(hardware.TrySetMaintenance(scene.Deck.Entity, scene.Actor, true), Is.True);
            Assert.That(hardware.TryCaptureFirmwareService(scene.Deck.Entity, scene.Actor, out stamp), Is.True);
        });
        var memory = 1048576 - scene.Artifact.ResidentMemoryBytes;
        var admission = await NCMetaNativeTestTasks.Run(Server, () => _marketplace.PrepareActivationAsync(scene.Owner, scene.Deck.Id, scene.Product.Id, scene.Release.Id, true));
        Assert.That(admission, Is.Not.Null);
        async Task<bool> NativeGuard()
        {
            var allowed = false;
            await Server.WaitPost(() => allowed = hardware.CanActivateFirmware(scene.Deck.Entity, scene.Actor, stamp) &&
                runtime.CanInstallFirmware(scene.Deck.Entity, scene.Actor, scene.Artifact, []));
            return allowed;
        }
        // Inject a real competing device CAS after native admission, before the atomic installation commit.
        var rejected = await _persistence.InstallLicensedAsync(Guid.NewGuid(), scene.Deck.Id, scene.Owner, scene.Product.Id,
            admission!, [], _epoch, _time, 4 * 1024 * 1024, activationGuard: async () =>
            {
                if (!await NativeGuard()) return false;
                var device = (await _persistence.ReadDeviceAsync(scene.Deck.Id))!;
                var conflict = await _database.ApplyNCPersistentBatchMutationAsync(new(Guid.NewGuid(), "meta.test.device-cas",
                    "meta.owner", scene.Owner, [Write("meta.device", scene.Deck.Id, device.Revision,
                        device with { Revision = device.Revision + 1 })], "{}", Guid.NewGuid(), null, _epoch, _time, []));
                Assert.That(conflict.Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
                return true;
            }, slotKind: "Firmware", autoStart: true, memoryLimitBytes: memory,
            expectedInstallationId: Guid.Empty, expectedInstallationRevision: 0);
        Assert.That(rejected.Success, Is.False);
        Assert.That(await _persistence.ReadInstallationsAsync(scene.Deck.Id, scene.Owner), Is.Empty);
        Assert.That((await _persistence.ReadSnapshotAsync("meta.software.license", license.Id.ToString("D")))!.AggregateVersion,
            Is.EqualTo(license.Revision), "The rejected slot swap must not consume its activation grant.");
        admission = await NCMetaNativeTestTasks.Run(Server, () => _marketplace.PrepareActivationAsync(scene.Owner, scene.Deck.Id, scene.Product.Id, scene.Release.Id, true));
        Assert.That(admission, Is.Not.Null);
        var requestId = Guid.NewGuid();
        var installed = await _persistence.InstallLicensedAsync(requestId, scene.Deck.Id, scene.Owner, scene.Product.Id,
            admission!, [], _epoch, _time, 4 * 1024 * 1024, activationGuard: NativeGuard,
            slotKind: "Firmware", autoStart: true, memoryLimitBytes: memory,
            expectedInstallationId: Guid.Empty, expectedInstallationRevision: 0);
        Assert.That(installed.Success, Is.True, installed.MessageKey);
        var first = (await _persistence.ReadInstallationsAsync(scene.Deck.Id, scene.Owner)).Single();
        Assert.Multiple(() =>
        {
            Assert.That(first.SlotKind, Is.EqualTo("Firmware"));
            Assert.That(first.PackageIdentity, Is.EqualTo("firmware"));
            Assert.That(first.MemoryLimitBytes, Is.EqualTo(memory));
            Assert.That(first.Active.CommercialOrigin?.ProductId, Is.EqualTo(scene.Product.Id));
            Assert.That(first.Owner, Is.EqualTo(scene.Owner));
            Assert.That(first.ProjectId, Is.EqualTo(scene.Project.Id), "Publisher provenance remains immutable.");
        });
        Assert.That((await _persistence.ReadSoftwareStorageUsageAsync(scene.Deck.Id, scene.Owner, "Firmware"))!
            .UpgradeStateBytes.ContainsKey("firmware"), Is.True);
        Assert.That((await _persistence.ReadSoftwareStorageUsageAsync(scene.Deck.Id, scene.Owner))!
            .UpgradeStateBytes.ContainsKey("firmware"), Is.False, "Slot reservations must not collide with a User package of the same name.");
        var refreshed = await NCMetaNativeTestTasks.Run(Server, () => _marketplace.PrepareActivationAsync(scene.Owner, scene.Deck.Id, scene.Product.Id, scene.Release.Id, true));
        var staleGuardCalls = 0;
        var stale = await _persistence.InstallLicensedAsync(Guid.NewGuid(), scene.Deck.Id, scene.Owner, scene.Product.Id,
            refreshed!, [], _epoch, _time, 4 * 1024 * 1024,
            activationGuard: () => { staleGuardCalls++; return Task.FromResult(true); }, slotKind: "Firmware", autoStart: true,
            memoryLimitBytes: memory, expectedInstallationId: Guid.Empty, expectedInstallationRevision: 0);
        Assert.That(stale.Success, Is.False);
        Assert.That(stale.MessageKey, Is.EqualTo("nc-meta-error-revision"));
        Assert.That(staleGuardCalls, Is.Zero, "Stale or wrong-slot confirmations must not stop a running generation.");
        var replacement = await _persistence.InstallLicensedAsync(Guid.NewGuid(), scene.Deck.Id, scene.Owner, scene.Product.Id,
            refreshed!, [], _epoch, _time, 4 * 1024 * 1024, activationGuard: NativeGuard, slotKind: "Firmware", autoStart: true,
            memoryLimitBytes: memory, expectedInstallationId: first.Id, expectedInstallationRevision: first.Revision);
        Assert.That(replacement.Success, Is.True, replacement.MessageKey);
        var current = (await _persistence.ReadInstallationsAsync(scene.Deck.Id, scene.Owner)).Single();
        Assert.That(current.StateVolumeId, Is.Not.EqualTo(first.StateVolumeId));
        await Server.WaitAssertion(() => Assert.That(hardware.TrySetMaintenance(scene.Deck.Entity, scene.Actor, false), Is.True));
        var retryCalls = 0;
        var retry = await _persistence.InstallLicensedAsync(requestId, scene.Deck.Id, scene.Owner, scene.Product.Id,
            admission!, [], _epoch, _time, 4 * 1024 * 1024,
            activationGuard: () => { retryCalls++; return Task.FromResult(false); }, slotKind: "Firmware", autoStart: true,
            memoryLimitBytes: memory, expectedInstallationId: Guid.Empty, expectedInstallationRevision: 0);
        Assert.That(retry.Success, Is.True, retry.MessageKey);
        Assert.That(retryCalls, Is.Zero);
        var changedConsent = await _persistence.InstallLicensedAsync(requestId, scene.Deck.Id, scene.Owner, scene.Product.Id,
            admission!, [], _epoch, _time, 4 * 1024 * 1024, slotKind: "Firmware", autoStart: true,
            memoryLimitBytes: memory, expectedInstallationId: Guid.Empty, expectedInstallationRevision: 0,
            consentFingerprint: "different-confirmation");
        Assert.That(changedConsent.Success, Is.False, "A request id cannot acknowledge a changed native consent payload.");
        var receipt = await _persistence.ReadFirmwareInstallReplayAsync(requestId, scene.Deck.Id, scene.Owner, scene.Project.Id,
            scene.Release, [], memory, Guid.Empty, 0, scene.Product.Id);
        Assert.That(receipt?.Success, Is.True);
        Assert.That((await _persistence.ReadInstallationsAsync(scene.Deck.Id, scene.Owner)).Single().StateVolumeId, Is.EqualTo(current.StateVolumeId));
        var serviceAdmission = await ApproveServiceAsync(scene, current, scene.Artifact, scene.Deck.Entity, scene.Actor);
        current = serviceAdmission.Installation;
        var started = false;
        for (var attempt = 0; attempt < 180 && !started; attempt++)
        {
            await Server.WaitAssertion(() =>
            {
                Assert.That(runtime.TryStartRecovered(scene.Deck.Entity, current with { SlotKind = "User" }, scene.Artifact, false,
                    serviceAdmission: serviceAdmission.Admission), Is.False);
                started = runtime.TryGetInstallation(scene.Deck.Entity, scene.Owner, current.Id, out _) ||
                    runtime.TryStartRecovered(scene.Deck.Entity, current, scene.Artifact, false, serviceAdmission: serviceAdmission.Admission);
            });
            if (!started) await Server.WaitRunTicks(1);
        }
        Assert.That(started, Is.True, "Approved firmware must boot outside maintenance even when User packages are disabled.");
    }

    private static NCMetaSoftwareCopyAdmission Admission(Scene scene) => new(Guid.NewGuid(), Guid.NewGuid(), scene.Owner,
        scene.Deck.Id, scene.Deck.DeviceEpoch, scene.Deck.AuthorityEpoch, scene.Source.Id, scene.SourceOwner,
        scene.Source.DeviceEpoch, scene.Source.AuthorityEpoch, "copy-fixture", "data", 1, scene.Installation.Id);

    private static NCPersistentAggregateWrite Write<T>(string type, string id, long revision, T value) =>
        new(Guid.NewGuid(), "meta.test.changed", type, id, revision, 1, JsonSerializer.Serialize(value), "{}");

    private async Task<MetaSoftwareLicense> SeedPurchasedSeat(Scene scene)
    {
        await Server.WaitAssertion(() => Assert.That(_persistence.TryCaptureTime(out _epoch, out _time), Is.True));
        var id = Guid.NewGuid();
        var license = new MetaSoftwareLicense(id, scene.Product.Id, 1, scene.Owner, MetaLicenseScope.Device, 1,
            [new(Guid.NewGuid(), scene.Deck.Id)], [], scene.Product.Terms, _time.Ticks, _time.Ticks, false,
            MetaLicenseProvenance.Purchase, Guid.NewGuid(), null, null, 1) { BoundDeviceId = scene.Deck.Id };
        var key = scene.Product.Id.ToString("D") + ":1";
        var ownerRow = await _persistence.ReadSnapshotAsync("meta.software.owner", scene.Owner);
        var owner = ownerRow == null ? new NCMetaSoftwareOwnerIndex(scene.Owner, [], 0) :
            JsonSerializer.Deserialize<NCMetaSoftwareOwnerIndex>(ownerRow.Payload)!;
        var buckets = owner.ProductLicenses.ToDictionary(value => value.Key, value => value.Value);
        buckets[key] = [id];
        var nextOwner = owner with { Licenses = [.. owner.Licenses, id], ProductLicenses = buckets, Revision = owner.Revision + 1 };
        var result = await _database.ApplyNCPersistentBatchMutationAsync(new(Guid.NewGuid(), "meta.test.purchased-seat",
            "meta.owner", scene.Owner, [Write("meta.software.license", id.ToString("D"), 0, license),
                Write("meta.software.owner", scene.Owner, owner.Revision, nextOwner),
                Write("meta.software.device", scene.Deck.Id, 0, new NCMetaSoftwareDeviceIndex(scene.Deck.Id, new() { [key] = id }, 1))],
            "{}", Guid.NewGuid(), null, _epoch, _time, []));
        Assert.That(result.Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
        return license;
    }

    private async Task<NCMetaInstallation> ActivatePurchasedImage(Scene scene)
    {
        var activation = await NCMetaNativeTestTasks.Run(Server, () => _marketplace.PrepareActivationAsync(scene.Owner, scene.Deck.Id, scene.Product.Id, scene.Release.Id, true));
        Assert.That(activation, Is.Not.Null);
        await Server.WaitAssertion(() => Assert.That(_persistence.TryCaptureTime(out _epoch, out _time), Is.True));
        var result = await _persistence.InstallLicensedAsync(Guid.NewGuid(), scene.Deck.Id, scene.Owner, scene.Product.Id,
            activation!, [], _epoch, _time, 4 * 1024 * 1024);
        Assert.That(result.Success, Is.True, result.MessageKey);
        return (await _persistence.ReadInstallationsAsync(scene.Deck.Id, scene.Owner)).Single();
    }

    private async Task<NCMetaRuntimeInstance> StartLicensedLoop(Scene scene, NCMetaInstallation installation)
    {
        var authorization = await NCMetaNativeTestTasks.Run(Server, () => _marketplace.ResolveRunAuthorizationAsync(scene.Owner, scene.Deck.Id, installation.Active, online: true));
        Assert.That(authorization?.Contact, Is.Not.Null);
        await Server.WaitAssertion(() => Assert.That(_persistence.TryCaptureTime(out _epoch, out _time), Is.True));
        Assert.That(await _persistence.SetAutoStartAsync(Guid.NewGuid(), installation, true, _epoch, _time), Is.True);
        installation = (await _persistence.ReadInstallationsAsync(scene.Deck.Id, scene.Owner)).Single();
        var approved = await ApproveServiceAsync(scene, installation, scene.Artifact, scene.Deck.Entity, scene.Actor);
        installation = approved.Installation;
        var system = Server.System<NCMetaRuntimeSystem>();
        NCMetaRuntimeInstance runtime = null;
        for (var attempt = 0; attempt < 180 && runtime == null; attempt++)
        {
            await Server.WaitAssertion(() =>
            {
                if (system.TryStartRecovered(scene.Deck.Entity, installation, scene.Artifact, false,
                    authorization!.ExpiresAtTicks, authorization.Contact, approved.Admission))
                    Assert.That(system.TryGetInstallation(scene.Deck.Entity, scene.Owner, installation.Id, out runtime), Is.True);
            });
            if (runtime == null) await Server.WaitRunTicks(1);
        }
        Assert.That(runtime, Is.Not.Null);
        return runtime!;
    }

    private async Task<NCMetaDeviceTransferPreparation> TransferPhysicalFixture(Scene scene, string previousOwner, string nextOwner)
    {
        var preparation = await NCMetaNativeTestTasks.Run(Server, () => _marketplace.PrepareDeviceTransferAsync(scene.Deck.Id, previousOwner, nextOwner,
            scene.Deck.AuthorityEpoch, Guid.NewGuid()));
        Assert.That(preparation, Is.Not.Null);
        var physical = await _persistence.PrepareInstallationOwnerTransferAsync(scene.Deck.Id, previousOwner, nextOwner,
            preparation!.PreserveInstallationIds);
        Assert.That(physical, Is.Not.Null);
        await Server.WaitAssertion(() =>
        {
            Assert.That(_marketplace.CanCommitDeviceTransfer(preparation), Is.True);
            Assert.That(_persistence.TryCaptureTime(out _epoch, out _time), Is.True);
        });
        var transferred = await _database.ApplyNCPersistentBatchMutationAsync(new(Guid.NewGuid(), "meta.test.device-transfer",
            "meta.owner", previousOwner, [.. preparation.Writes, .. physical!], "{}", Guid.NewGuid(), null, _epoch, _time, []));
        Assert.That(transferred.Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
        Assert.That(await NCMetaNativeTestTasks.Run(Server, () => _marketplace.TryApplyCommittedDeviceTransferAsync(preparation)), Is.True);
        return preparation;
    }

    [Test]
    public async Task NativeCopyCreatesOneRestrictedEvidenceBoundLicenseAndExactActivationCapturesItsExpiry()
    {
        var scene = await CreatePublishedPhysicalImage();
        var admission = Admission(scene);
        var copied = await NCMetaNativeTestTasks.Run(Server, () => _marketplace.TryCopySoftwareAsync(admission, () => true));
        Assert.That(copied.Success, Is.True, copied.MessageKey);
        var license = copied.License!;
        Assert.Multiple(() =>
        {
            Assert.That(license.Provenance, Is.EqualTo(MetaLicenseProvenance.NativePiracy));
            Assert.That(license.Terms.AllowLibraryBuild, Is.False);
            Assert.That(license.Terms.AllowRedistribution, Is.False);
            Assert.That(license.Terms.AllowResale, Is.False);
            Assert.That(license.Terms.Source, Is.EqualTo(MetaSourcePolicy.Closed));
            Assert.That(license.PinnedArtifactDigest, Is.EqualTo(scene.Release.Digest));
            Assert.That(license.Activations, Is.Empty);
            Assert.That(license.IllegalExpiresAtTicks - license.PurchasedAtTicks, Is.EqualTo(MetaSoftwarePolicy.MaximumPiracyDurationTicks));
        });
        Assert.That(await _persistence.ReadReservedSoftwareCopyBytesAsync(scene.Deck.Id, scene.Owner), Is.EqualTo(scene.Bytes));
        var command = await _database.GetNCPersistentCommandResultAsync(admission.ActionId);
        Assert.That(command?.CommandType, Is.EqualTo("meta.software.copy"));
        Assert.That(command?.RequestFingerprint, Has.Length.EqualTo(64));
        var retry = await NCMetaNativeTestTasks.Run(Server, () => _marketplace.TryCopySoftwareAsync(admission, () => true));
        Assert.That(retry.Success, Is.True);
        Assert.That(retry.License?.Id, Is.EqualTo(license.Id));
        Assert.That(await _persistence.ReadReservedSoftwareCopyBytesAsync(scene.Deck.Id, scene.Owner), Is.EqualTo(scene.Bytes));
        Assert.That((await NCMetaNativeTestTasks.Run(Server, () => _marketplace.TryCopySoftwareAsync(admission with { NodeGeneration = 2 }, () => true))).Success, Is.False);
        Assert.That(await NCMetaNativeTestTasks.Run(Server, () => _marketplace.ResolveRunAuthorizationAsync(scene.Owner, scene.Deck.Id,
            scene.Release with { CommercialOrigin = new(scene.Product.Id, 1, scene.Project.Id, scene.Release.Id) })), Is.Null,
            "Owning the copied bytes does not silently approve activation.");
        var activation = await NCMetaNativeTestTasks.Run(Server, () => _marketplace.PrepareActivationAsync(scene.Owner, scene.Deck.Id, scene.Product.Id, scene.Release.Id, true));
        Assert.That(activation, Is.Not.Null);
        await Server.WaitAssertion(() => Assert.That(_persistence.TryCaptureTime(out _epoch, out _time), Is.True));
        Assert.That((await _persistence.InstallLicensedAsync(Guid.NewGuid(), scene.Deck.Id, scene.Owner, scene.Product.Id,
            activation!, [], _epoch, _time, 4 * 1024 * 1024)).Success, Is.True);
        var installed = (await _persistence.ReadInstallationsAsync(scene.Deck.Id, scene.Owner)).Single();
        Assert.That(installed.Bindings, Is.Empty);
        Assert.That(installed.Owner, Is.EqualTo(scene.Owner));
        Assert.That(await _persistence.ReadProjectAsync(scene.Project.Id, scene.Owner), Is.Null);
        var authorization = await NCMetaNativeTestTasks.Run(Server, () => _marketplace.ResolveRunAuthorizationAsync(scene.Owner, scene.Deck.Id, installed.Active));
        Assert.That(authorization, Is.Not.Null);
        Assert.That(authorization!.ExpiresAtTicks, Is.EqualTo(license.IllegalExpiresAtTicks));
    }

    [Test]
    public async Task LosingTheFinalNativeGuardAndReplacingTheSourceBothLeaveNoPartialCopyReservation()
    {
        var scene = await CreatePublishedPhysicalImage();
        var admission = Admission(scene);
        var checks = 0;
        var denied = await NCMetaNativeTestTasks.Run(Server, () => _marketplace.TryCopySoftwareAsync(admission, () => ++checks == 1));
        Assert.That(checks, Is.EqualTo(2), "The native guard must be rechecked after bytecode and DB work.");
        Assert.That(denied.Success, Is.False);
        Assert.That(await _persistence.ReadReservedSoftwareCopyBytesAsync(scene.Deck.Id, scene.Owner), Is.Zero);
        Assert.That(await _database.GetNCPersistentCommandResultAsync(admission.ActionId), Is.Null);
        var evidenceId = NCMetaPersistenceSystem.StableGuid("software-copy-evidence:" + admission.ActionId);
        Assert.That(await _persistence.ReadSnapshotAsync("meta.software.copy-evidence", evidenceId.ToString("D")), Is.Null);
        var copyId = Guid.NewGuid();
        var prepared = await _persistence.PrepareSoftwareCopyAsync(scene.Deck.Id, scene.Owner, scene.Bytes,
            scene.Installation, copyId, 4 * 1024 * 1024);
        Assert.That(prepared, Is.Not.Null);
        Assert.That((await _persistence.InstallAsync(Guid.NewGuid(), scene.Source.Id, scene.SourceOwner, scene.Project,
            scene.Release, [], false, _epoch, _time, 4 * 1024 * 1024)).Success, Is.True);
        var witness = Guid.NewGuid().ToString("D");
        var conflict = await _database.ApplyNCPersistentBatchMutationAsync(new(Guid.NewGuid(), "meta.test.copy-race",
            "meta.owner", scene.Owner, [.. prepared!.Writes, Write("meta.test.copy-witness", witness, 0, new { Revision = 1 })],
            "{}", Guid.NewGuid(), null, _epoch, _time, []));
        Assert.That(conflict.Status, Is.EqualTo(NCPersistentMutationStatus.VersionConflict));
        Assert.That(await _persistence.ReadReservedSoftwareCopyBytesAsync(scene.Deck.Id, scene.Owner), Is.Zero);
        Assert.That(await _database.GetNCAggregateSnapshotAsync("meta.test.copy-witness", witness), Is.Null,
            "Evidence/license participants in the enclosing batch must roll back with a stale source image.");
    }

    [Test]
    public async Task AWellFormedIllegalLicenseWithoutTheNativeEvidenceReceiptCannotResolveTheImage()
    {
        var scene = await CreatePublishedPhysicalImage();
        var copied = await NCMetaNativeTestTasks.Run(Server, () => _marketplace.TryCopySoftwareAsync(Admission(scene), () => true));
        Assert.That(copied.Success, Is.True, copied.MessageKey);
        var forged = copied.License! with { Id = Guid.NewGuid(), EvidenceId = Guid.NewGuid(), Revision = 1 };
        Assert.That(MetaSoftwarePolicy.ValidLicense(forged), Is.True);
        var key = scene.Product.Id.ToString("D") + ":1";
        var index = new NCMetaSoftwareDeviceIndex(scene.Deck.Id, new() { [key] = forged.Id }, 2);
        await Server.WaitAssertion(() => Assert.That(_persistence.TryCaptureTime(out _epoch, out _time), Is.True));
        var changed = await _database.ApplyNCPersistentBatchMutationAsync(new(Guid.NewGuid(), "meta.test.unattested-license",
            "meta.owner", scene.Owner, [Write("meta.software.license", forged.Id.ToString("D"), 0, forged),
                Write("meta.software.device", scene.Deck.Id, 1, index)], "{}", Guid.NewGuid(), null, _epoch, _time, []));
        Assert.That(changed.Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
        Assert.That(await NCMetaNativeTestTasks.Run(Server, () => _marketplace.ResolveLicensedReleaseAsync(scene.Owner, scene.Deck.Id, scene.Product.Id, scene.Release.Id)), Is.Null);
    }

    [Test]
    public async Task AnAbsoluteNativeEntitlementDeadlineStopsPureComputationBeforeAnotherVmPhase()
    {
        var scene = await CreatePublishedPhysicalImage("module test.piracy; fn main()->void{while(true){yield;}}", nativeSourceOwner: true);
        Assert.That(await _persistence.SetAutoStartAsync(Guid.NewGuid(), scene.Installation, true, _epoch, _time), Is.True);
        var installation = (await _persistence.ReadInstallationsAsync(scene.Source.Id, scene.SourceOwner)).Single();
        var approved = await ApproveServiceAsync(scene, installation, scene.Artifact, scene.Source.Entity, scene.SourceActor!.Value);
        installation = approved.Installation;
        var system = Server.System<NCMetaRuntimeSystem>();
        NCMetaRuntimeInstance runtime = null;
        for (var attempt = 0; attempt < 180 && runtime == null; attempt++)
        {
            await Server.WaitAssertion(() =>
            {
                Assert.That(_persistence.TryCaptureTime(out _, out var now), Is.True);
                // This fixture supplies a stricter native deadline to exercise the host gate without
                // simulating thirty minutes. The prior test resolves the real evidence-bound expiry.
                if (system.TryStartRecovered(scene.Source.Entity, installation, scene.Artifact, false,
                    now.Ticks + TimeSpan.TicksPerMillisecond * 100, serviceAdmission: approved.Admission))
                    Assert.That(system.TryGetInstallation(scene.Source.Entity, scene.SourceOwner, installation.Id, out runtime), Is.True);
            });
            if (runtime == null) await Server.WaitRunTicks(1);
        }
        Assert.That(runtime, Is.Not.Null, "The physical host did not finish its native power-on sequence.");
        for (var attempt = 0; attempt < 30 && runtime.Process.State != MetaProcessState.Stopped; attempt++)
            await Server.WaitRunTicks(1);
        await Server.WaitAssertion(() =>
        {
            Assert.That(runtime.Process.State, Is.EqualTo(MetaProcessState.Stopped));
            Assert.That(runtime.Process.StopReason, Is.EqualTo(MetaStopReason.PermissionRevoked));
            Assert.That(system.TryGetInstallation(scene.Source.Entity, scene.SourceOwner, installation.Id, out _), Is.False);
        });
    }

    [TestCase(MetaOfflinePolicy.OnlineRequired)]
    [TestCase(MetaOfflinePolicy.GracePeriod)]
    public async Task LosingTheRealCitiNetRouteStopsPureComputationAccordingToTheDeclaredPolicy(MetaOfflinePolicy policy)
    {
        var terms = new MetaSoftwareTerms { Source = MetaSourcePolicy.Closed, Offline = policy,
            OfflineGraceTicks = policy == MetaOfflinePolicy.GracePeriod ? TimeSpan.TicksPerMillisecond * 300 : 0 };
        var scene = await CreatePublishedPhysicalImage("module test.piracy; fn main()->void{while(true){yield;}}", terms, nativeOwner: true);
        await SeedPurchasedSeat(scene);
        var installation = await ActivatePurchasedImage(scene);
        var runtime = await StartLicensedLoop(scene, installation);
        await Server.WaitAssertion(() =>
        {
            Assert.That(Server.System<NCMetaRuntimeSystem>().TryGetInstallation(scene.Deck.Entity, scene.Owner, installation.Id, out _), Is.True);
            SComp<NCMetaHostComponent>(scene.Service).Disabled = true;
            _devices.TryInvalidate(scene.Service);
            Assert.That(_marketplace.HasMarketplaceRoute(scene.Deck.Entity), Is.False);
            if (policy == MetaOfflinePolicy.GracePeriod)
                Assert.That(Server.System<NCMetaRuntimeSystem>().TryGetInstallation(scene.Deck.Entity, scene.Owner, installation.Id, out _), Is.True,
                    "A lost route does not erase the last successful native contact or declared grace.");
        });
        for (var attempt = 0; attempt < 90 && runtime.Process.State != MetaProcessState.Stopped; attempt++)
            await Server.WaitRunTicks(1);
        await Server.WaitAssertion(() =>
        {
            Assert.That(runtime.Process.State, Is.EqualTo(MetaProcessState.Stopped));
            Assert.That(runtime.Process.StopReason, Is.EqualTo(MetaStopReason.PermissionRevoked));
        });
    }

    [TestCase(MetaOfflinePolicy.ActivatedPerpetual)]
    [TestCase(MetaOfflinePolicy.GracePeriod)]
    public async Task WithDeviceCustodyRetainsOnlyInactiveBytesAndOfflineRebindPreservesTheOriginalContactHorizon(MetaOfflinePolicy policy)
    {
        var scene = await CreatePublishedPhysicalImage(terms: new() { Source = MetaSourcePolicy.Closed,
            Transfer = MetaLicenseTransferPolicy.WithDevice, Offline = policy,
            OfflineGraceTicks = policy == MetaOfflinePolicy.GracePeriod ? TimeSpan.TicksPerHour : 0 });
        var originalLicense = await SeedPurchasedSeat(scene);
        var previousInstallation = await ActivatePurchasedImage(scene);
        var licenseRow = await _persistence.ReadSnapshotAsync("meta.software.license", originalLicense.Id.ToString("D"));
        var activatedLicense = JsonSerializer.Deserialize<MetaSoftwareLicense>(licenseRow!.Payload)!;
        var previousReceipt = activatedLicense.Activations.Single();
        await Server.WaitAssertion(() => Assert.That(_persistence.TryCaptureTime(out _epoch, out _time), Is.True));
        Assert.That((await _persistence.WriteTextAsync(Guid.NewGuid(), previousInstallation.StateVolumeId, scene.Owner,
            "private.txt", "previous owner's private data", 262144, _epoch, _time)).Success, Is.True);
        var nextOwner = NCPartyRef.ForCharacter(new NCCharacterId(746)).ToString();
        var preparation = await NCMetaNativeTestTasks.Run(Server, () => _marketplace.PrepareDeviceTransferAsync(scene.Deck.Id, scene.Owner, nextOwner,
            scene.Deck.AuthorityEpoch, Guid.NewGuid()));
        Assert.That(preparation, Is.Not.Null);
        Assert.That(preparation!.PreserveInstallationIds, Is.EqualTo(new[] { previousInstallation.Id }));
        var images = await _persistence.PrepareInstallationOwnerTransferAsync(scene.Deck.Id, scene.Owner, nextOwner,
            preparation.PreserveInstallationIds);
        Assert.That(images, Is.Not.Null);
        await Server.WaitAssertion(() =>
        {
            Assert.That(_marketplace.CanCommitDeviceTransfer(preparation), Is.True);
            Assert.That(_persistence.TryCaptureTime(out _epoch, out _time), Is.True);
        });
        var transferred = await _database.ApplyNCPersistentBatchMutationAsync(new(Guid.NewGuid(), "meta.test.device-transfer",
            "meta.owner", scene.Owner, [.. preparation.Writes, .. images!], "{}", Guid.NewGuid(), null, _epoch, _time, []));
        Assert.That(transferred.Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
        Assert.That(await NCMetaNativeTestTasks.Run(Server, () => _marketplace.TryApplyCommittedDeviceTransferAsync(preparation)), Is.True);
        Assert.That(await _persistence.ReadInstallationsAsync(scene.Deck.Id, nextOwner), Is.Empty);
        Assert.That(await _persistence.HasInheritedImageAsync(scene.Deck.Id, nextOwner, previousInstallation.Id,
            previousInstallation.Active.Id, previousInstallation.Active.Digest), Is.True);
        Assert.That(await _persistence.ReadReservedInheritedBytesAsync(scene.Deck.Id, nextOwner), Is.EqualTo(scene.Bytes));
        var usage = await _persistence.ReadSoftwareStorageUsageAsync(scene.Deck.Id, nextOwner);
        Assert.That(usage?.Bytes, Is.EqualTo(scene.Bytes));
        var compatibility = await NCMetaNativeTestTasks.Run(Server, () => _marketplace.ReadCompatibilityAsync(nextOwner, scene.Deck.Id, scene.Product.Id, scene.Release.Id));
        Assert.That(compatibility?.Compatible, Is.True);
        Assert.That(compatibility?.AvailableStorageBytes, Is.EqualTo(4 * 1024 * 1024), "Reinstall consumes its own retained-image reservation atomically.");
        Assert.That(await NCMetaNativeTestTasks.Run(Server, () => _marketplace.ResolveInstalledReleaseAsync(nextOwner, scene.Deck.Id, scene.Product.Id, scene.Release.Id)), Is.Null);
        await Server.WaitPost(() => { SComp<NCMetaHostComponent>(scene.Service).Disabled = true; _devices.TryInvalidate(scene.Service); });
        Assert.That(await NCMetaNativeTestTasks.Run(Server, () => _marketplace.PrepareActivationAsync(nextOwner, scene.Deck.Id, scene.Product.Id, scene.Release.Id, false)), Is.Null);
        var activation = await NCMetaNativeTestTasks.Run(Server, () => _marketplace.PrepareActivationAsync(nextOwner, scene.Deck.Id, scene.Product.Id, scene.Release.Id, true));
        Assert.That(activation, Is.Not.Null);
        Assert.That(activation!.RequiredInheritedInstallationId, Is.EqualTo(previousInstallation.Id));
        var nextLicense = JsonSerializer.Deserialize<MetaSoftwareLicense>(activation.ActivationWrite!.SnapshotPayload)!;
        var receipt = nextLicense.Activations.Single();
        Assert.Multiple(() =>
        {
            Assert.That(receipt.Owner, Is.EqualTo(nextOwner));
            Assert.That(receipt.DeviceAuthorityEpoch, Is.EqualTo(preparation.NextAuthorityEpoch));
            Assert.That(receipt.AllocationId, Is.Not.EqualTo(previousReceipt.AllocationId));
            Assert.That(receipt.LastOnlineTicks, Is.EqualTo(previousReceipt.LastOnlineTicks));
            Assert.That(receipt.ActivatedAtTicks, Is.EqualTo(previousReceipt.ActivatedAtTicks));
        });
        await Server.WaitAssertion(() => Assert.That(_persistence.TryCaptureTime(out _epoch, out _time), Is.True));
        var installed = await _persistence.InstallLicensedAsync(Guid.NewGuid(), scene.Deck.Id, nextOwner, scene.Product.Id,
            activation, [], _epoch, _time, 4 * 1024 * 1024);
        Assert.That(installed.Success, Is.True, installed.MessageKey);
        var fresh = (await _persistence.ReadInstallationsAsync(scene.Deck.Id, nextOwner)).Single();
        Assert.Multiple(() =>
        {
            Assert.That(fresh.Id, Is.Not.EqualTo(previousInstallation.Id));
            Assert.That(fresh.StateVolumeId, Is.Not.EqualTo(previousInstallation.StateVolumeId));
            Assert.That(fresh.Bindings, Is.Empty);
            Assert.That(fresh.AutoStart, Is.False);
            Assert.That(fresh.Recovery, Is.Null);
        });
        Assert.That(await _persistence.ReadTextAsync(fresh.StateVolumeId, nextOwner, "private.txt"), Is.Null);
        Assert.That(await _persistence.ReadTextAsync(previousInstallation.StateVolumeId, scene.Owner, "private.txt"), Is.EqualTo("previous owner's private data"));
        Assert.That(await _persistence.ReadReservedInheritedBytesAsync(scene.Deck.Id, nextOwner), Is.Zero);
        await Server.WaitAssertion(() => Assert.That(_persistence.TryCaptureTime(out _epoch, out _time), Is.True));
        Assert.That((await _persistence.InstallLicensedAsync(Guid.NewGuid(), scene.Deck.Id, nextOwner, scene.Product.Id,
            activation, [], _epoch, _time, 4 * 1024 * 1024)).Success, Is.False,
            "A previously prepared offline admission cannot materialize a second image after its physical reservation was consumed.");
    }

    [Test]
    public async Task AReleasedWithDeviceSeatStillTransfersItsBoundEntitlementWithoutInventingAnActivation()
    {
        var scene = await CreatePublishedPhysicalImage();
        var license = await SeedPurchasedSeat(scene);
        var released = license with { Seats = [], Activations = [], Revision = 2 };
        await Server.WaitAssertion(() => Assert.That(_persistence.TryCaptureTime(out _epoch, out _time), Is.True));
        var release = await _database.ApplyNCPersistentBatchMutationAsync(new(Guid.NewGuid(), "meta.test.seat-released",
            "meta.owner", scene.Owner, [Write("meta.software.license", license.Id.ToString("D"), 1, released),
                Write("meta.software.device", scene.Deck.Id, 1, new NCMetaSoftwareDeviceIndex(scene.Deck.Id, [], 2))],
            "{}", Guid.NewGuid(), null, _epoch, _time, []));
        Assert.That(release.Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
        Assert.That(MetaSoftwarePolicy.CanTransferWithDevice(released, scene.Deck.Id), Is.True);
        var nextOwner = NCPartyRef.ForCharacter(new NCCharacterId(747)).ToString();
        var preparation = await NCMetaNativeTestTasks.Run(Server, () => _marketplace.PrepareDeviceTransferAsync(scene.Deck.Id, scene.Owner, nextOwner,
            scene.Deck.AuthorityEpoch, Guid.NewGuid()));
        Assert.That(preparation, Is.Not.Null);
        Assert.That(preparation!.TransferredLicenses, Does.Contain(license.Id));
        Assert.That(preparation.PreserveInstallationIds, Is.Empty);
        var licenseWrite = preparation.Writes.Single(value => value.AggregateType == "meta.software.license");
        var moved = JsonSerializer.Deserialize<MetaSoftwareLicense>(licenseWrite.SnapshotPayload)!;
        Assert.That(moved.Owner, Is.EqualTo(nextOwner));
        Assert.That(moved.Activations, Is.Empty);
    }

    [Test]
    public async Task ReleasingADormantSeatDoesNotDestroyItsPhysicalImageOrCreateAnotherOfflineActivation()
    {
        var scene = await CreatePublishedPhysicalImage();
        var license = await SeedPurchasedSeat(scene);
        var image = await ActivatePurchasedImage(scene);
        var middleOwner = NCPartyRef.ForCharacter(new NCCharacterId(748)).ToString();
        var first = await TransferPhysicalFixture(scene, scene.Owner, middleOwner);
        Assert.That(first.PreserveInstallationIds, Is.EqualTo(new[] { image.Id }));
        var licenseRow = await _persistence.ReadSnapshotAsync("meta.software.license", license.Id.ToString("D"));
        var current = JsonSerializer.Deserialize<MetaSoftwareLicense>(licenseRow!.Payload)!;
        var indexRow = await _persistence.ReadSnapshotAsync("meta.software.device", scene.Deck.Id);
        var index = JsonSerializer.Deserialize<NCMetaSoftwareDeviceIndex>(indexRow!.Payload)!;
        var entries = index.Licenses.ToDictionary(value => value.Key, value => value.Value);
        entries.Remove(scene.Product.Id.ToString("D") + ":1");
        await Server.WaitAssertion(() => Assert.That(_persistence.TryCaptureTime(out _epoch, out _time), Is.True));
        var released = await _database.ApplyNCPersistentBatchMutationAsync(new(Guid.NewGuid(), "meta.test.dormant-seat-released",
            "meta.owner", middleOwner,
            [Write("meta.software.license", current.Id.ToString("D"), current.Revision, current with
                { Seats = [], Activations = [], Revision = current.Revision + 1 }),
                Write("meta.software.device", scene.Deck.Id, index.Revision, index with { Licenses = entries, Revision = index.Revision + 1 })],
            "{}", Guid.NewGuid(), null, _epoch, _time, []));
        Assert.That(released.Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
        var nextOwner = NCPartyRef.ForCharacter(new NCCharacterId(749)).ToString();
        var second = await TransferPhysicalFixture(scene, middleOwner, nextOwner);
        Assert.That(second.PreserveInstallationIds, Is.EqualTo(new[] { image.Id }));
        var proofRow = await _persistence.ReadSnapshotAsync("meta.software.inheritance", scene.Deck.Id);
        var proof = JsonSerializer.Deserialize<NCMetaSoftwareInheritance>(proofRow!.Payload)!;
        Assert.That(proof.Images.Single().LastOnlineTicks, Is.Null);
        Assert.That(proof.Images.Single().ActivatedAtTicks, Is.Null);
        Assert.That(await _persistence.HasInheritedImageAsync(scene.Deck.Id, nextOwner, image.Id, image.Active.Id, image.Active.Digest), Is.True);
        await Server.WaitPost(() => { SComp<NCMetaHostComponent>(scene.Service).Disabled = true; _devices.TryInvalidate(scene.Service); });
        Assert.That(await NCMetaNativeTestTasks.Run(Server, () => _marketplace.PrepareActivationAsync(nextOwner, scene.Deck.Id, scene.Product.Id, scene.Release.Id, true)), Is.Null,
            "The bytes and bound commercial rights survive, but a released activation cannot be reconstructed offline.");
    }
}
